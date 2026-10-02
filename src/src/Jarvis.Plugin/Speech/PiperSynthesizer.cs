using System.Diagnostics;
using System.Globalization;
using System.Text;
using Jarvis.Plugin.Runtime;
using Serilog;

namespace Jarvis.Plugin.Speech;

/// <summary>
/// Renders speech with Piper, the local ONNX voice. Chosen over a hosted endpoint because it needs no
/// network and no key, and over SAPI because it is the voice JARVIS is supposed to have.
/// <para>
/// Piper is driven as a child process reading text on stdin and writing a WAV, because that is the only
/// interface it offers and reimplementing its ONNX pipeline in-process would add a native dependency for
/// no gain. Nothing is bundled: the binary, the voice and the phonemiser data are installed on demand by
/// the runtime manager, and this class reports "not installed" rather than throwing when they are absent.
/// </para>
/// </summary>
public sealed class PiperSynthesizer(RuntimeManager runtime, ILogger logger)
{
	/// <summary>Well beyond real time, so a timeout here means the process is wedged rather than slow.</summary>
	private static readonly TimeSpan RenderBudget = TimeSpan.FromMinutes(3);

	private readonly RuntimeManager _runtime = runtime;
	private readonly ILogger _logger = logger.ForContext<PiperSynthesizer>();

	public bool IsAvailable => _runtime.AssetPath(PinnedAssets.PiperBinary) is not null;

	/// <summary>The voices on disk. Empty when the component is not installed.</summary>
	public IReadOnlyList<InstalledVoice> Voices => _runtime.InstalledVoices(AssetCatalog.Piper);

	/// <summary>
	/// Renders text to a WAV. Returns false rather than throwing when Piper is not installed or produced
	/// nothing, because the caller has a working fallback and a missing optional component is not an error.
	/// </summary>
	public async Task<bool> TrySynthesizeAsync(
		string text,
		string wavPath,
		string voiceName,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		if (_runtime.UnpackedFile(PinnedAssets.PiperBinary, "piper", "piper.exe") is not { } executable)
		{
			_logger.Debug("Piper is not installed; the caller should fall back.");
			return false;
		}

		var voice = Voices.FirstOrDefault(candidate =>
			string.Equals(candidate.Name, voiceName, StringComparison.OrdinalIgnoreCase));

		if (voice is null)
		{
			_logger.Warning("Voice {Voice} is not installed; the caller should fall back.", voiceName);
			return false;
		}

		// Piper writes to a fixed output path given on the command line, so the render happens beside the
		// final file and is moved into place only once it exists.
		var staging = wavPath + ".rendering";

		try
		{
			await RenderAsync(executable, voice, text, staging, cancellationToken).ConfigureAwait(false);

			if (!File.Exists(staging) || new FileInfo(staging).Length == 0)
			{
				_logger.Warning("Piper produced no audio.");
				return false;
			}

			File.Move(staging, wavPath, overwrite: true);
			return true;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Warning(exception, "Piper could not render the reply.");
			return false;
		}
		finally
		{
			TryDelete(staging);
		}
	}

	private async Task RenderAsync(
		string executable,
		InstalledVoice voice,
		string text,
		string staging,
		CancellationToken cancellationToken)
	{
		var info = new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WindowStyle = ProcessWindowStyle.Hidden,
			WorkingDirectory = Path.GetDirectoryName(executable)!,
		};

		info.ArgumentList.Add("--model");
		info.ArgumentList.Add(voice.ModelPath);
		info.ArgumentList.Add("--config");
		info.ArgumentList.Add(voice.ConfigPath);
		info.ArgumentList.Add("--output_file");
		info.ArgumentList.Add(staging);
		info.ArgumentList.Add("--quiet");

		// Piper resolves its phonemiser from a path relative to the executable by default. Passing it
		// explicitly keeps it working from the unpacked directory rather than from whatever the plugin's
		// working directory happens to be.
		if (_runtime.UnpackedFile(PinnedAssets.PiperBinary, "piper", "espeak-ng-data") is { } espeak)
		{
			info.ArgumentList.Add("--espeak_data");
			info.ArgumentList.Add(espeak);
		}

		if (_runtime.UnpackedFile(PinnedAssets.PiperBinary, "piper", "libtashkeel_model.ort") is { } tashkeel)
		{
			info.ArgumentList.Add("--tashkeel_model");
			info.ArgumentList.Add(tashkeel);
		}

		using var process = Process.Start(info)
			?? throw new InvalidOperationException("Piper could not be started.");

		// Text arrives on stdin and is closed, which is Piper's cue to render and exit. Writing it before
		// awaiting the exit avoids the deadlock of a full pipe.
		await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
		await process.StandardInput.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
		process.StandardInput.Close();

		// Both pipes are drained concurrently with the wait. Reading one to completion first would deadlock
		// as soon as Piper's logging filled the other buffer.
		var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

		using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		budget.CancelAfter(RenderBudget);

		try
		{
			await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			TryKill(process);
			throw new TimeoutException($"Piper did not finish within {RenderBudget.TotalSeconds:F0} seconds.");
		}

		var error = await stderr.ConfigureAwait(false);
		await stdout.ConfigureAwait(false);

		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException($"Piper exited with {process.ExitCode}: {error.Trim()}");
		}
	}

	private static void TryKill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
			// The process has already gone, which is the outcome that was wanted.
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A leftover render is harmless: it is written under a name nothing plays.
		}
	}
}

/// <summary>The pinned asset ids the speech code needs, named so a string is never spelled twice.</summary>
public static class PinnedAssets
{
	public const string PiperBinary = "piper-windows-amd64";
	public const string WhisperBinary = "whisper-bin-x64";
}
