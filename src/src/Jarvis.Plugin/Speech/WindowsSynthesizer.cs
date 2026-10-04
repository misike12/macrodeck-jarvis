using System.Globalization;
using System.Text;
using System.Text.Json;
using Serilog;

namespace Jarvis.Plugin.Speech;

/// <summary>
/// Renders text to a WAV file using the voices already installed on the machine.
/// <para>
/// Windows ships a speech synthesizer, so this path needs no download and works on a fresh install.
/// Synthesis itself is delegated to Windows PowerShell, where <c>System.Speech</c> lives; the text is
/// handed over in a file rather than on the command line, because a sentence full of quotes, backticks
/// and dollar signs is otherwise a quoting bug waiting to happen.
/// </para>
/// </summary>
public sealed class WindowsSynthesizer(ILogger logger)
{
	private readonly ILogger _logger = logger.ForContext<WindowsSynthesizer>();

	/// <summary>
	/// The voices Windows can speak with.
	/// <para>
	/// Async because it shells out to PowerShell. This used to block on that call with
	/// <c>GetAwaiter().GetResult()</c>, which was safe only because nothing called it. Anything that reaches
	/// it from an executor would hold a concurrency slot for the length of a process launch.
	/// </para>
	/// </summary>
	public async Task<string[]> InstalledVoicesAsync(CancellationToken cancellationToken)
	{
		try
		{
			var json = await RunProbeAsync(
				"Add-Type -AssemblyName System.Speech; " +
				"$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
				"$s.GetInstalledVoices() | ForEach-Object { $_.VoiceInfo.Name };" +
				"$s.Dispose()",
				cancellationToken).ConfigureAwait(false);

			return json.Where(line => line.Length > 0).ToArray();
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Debug(exception, "Installed voices could not be listed.");
			return [];
		}
	}

	/// <summary>
	/// Writes <paramref name="text"/> to <paramref name="wavPath"/> and returns its duration. Throws on
	/// failure rather than returning false, because a caller that silently gets no audio would look like
	/// a working assistant that happens to be mute.
	/// </summary>
	public async Task<TimeSpan> SynthesizeAsync(
		string text,
		string wavPath,
		string? voice,
		int rate,
		int volume,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return TimeSpan.Zero;
		}

		var textPath = Path.ChangeExtension(wavPath, ".txt");
		await File.WriteAllTextAsync(textPath, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

		var script = new StringBuilder()
			.AppendLine("Add-Type -AssemblyName System.Speech")
			.AppendLine("$ErrorActionPreference = 'Stop'")
			.AppendLine("$s = New-Object System.Speech.Synthesis.SpeechSynthesizer")
			.Append("$rate = ").Append(rate.ToString(CultureInfo.InvariantCulture)).AppendLine()
			.Append("$volume = ").Append(volume.ToString(CultureInfo.InvariantCulture)).AppendLine()
			.AppendLine("$s.Rate = $rate")
			.AppendLine("$s.Volume = $volume");

		if (!string.IsNullOrWhiteSpace(voice))
		{
			script.Append("$s.SelectVoice('").Append(voice.Replace("'", "''")).AppendLine("')");
		}

		script.AppendLine("$s.SetOutputToWaveFile($args[1])")
			.AppendLine("$s.Speak([System.IO.File]::ReadAllText($args[0]))")
			.AppendLine("$s.Dispose()");

		var scriptPath = Path.ChangeExtension(wavPath, ".ps1");
		await File.WriteAllTextAsync(scriptPath, script.ToString(), new UTF8Encoding(false), cancellationToken)
			.ConfigureAwait(false);

		try
		{
			await RunAsync(scriptPath, [textPath, wavPath], cancellationToken).ConfigureAwait(false);

			if (!File.Exists(wavPath))
			{
				throw new InvalidOperationException("The synthesizer produced no audio.");
			}

			return DurationOf(wavPath);
		}
		finally
		{
			TryDelete(textPath);
			TryDelete(scriptPath);
		}
	}

	private static TimeSpan DurationOf(string wavPath)
	{
		try
		{
			using var reader = new WaveFileReaderHeader(wavPath);
			return reader.Duration;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			return TimeSpan.Zero;
		}
	}

	private async Task<string[]> RunProbeAsync(string script, CancellationToken cancellationToken)
	{
		var scriptPath = Path.Combine(Path.GetTempPath(), $"jarvis-voices-{Guid.CreateVersion7():N}.ps1");

		try
		{
			await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false), cancellationToken)
				.ConfigureAwait(false);

			var output = await RunAsync(scriptPath, [], cancellationToken).ConfigureAwait(false);

			return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		}
		finally
		{
			TryDelete(scriptPath);
		}
	}

	private async Task<string> RunAsync(
		string scriptPath,
		string[] arguments,
		CancellationToken cancellationToken)
	{
		var info = new System.Diagnostics.ProcessStartInfo
		{
			FileName = "powershell.exe",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
		};

		info.ArgumentList.Add("-NoProfile");
		info.ArgumentList.Add("-NonInteractive");
		info.ArgumentList.Add("-ExecutionPolicy");
		info.ArgumentList.Add("Bypass");
		info.ArgumentList.Add("-File");
		info.ArgumentList.Add(scriptPath);

		foreach (var argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}

		using var process = System.Diagnostics.Process.Start(info)
			?? throw new InvalidOperationException("PowerShell could not be started.");

		var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

		await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

		var output = await stdout.ConfigureAwait(false);
		var error = await stderr.ConfigureAwait(false);

		if (process.ExitCode != 0)
		{
			_logger.Warning("Speech synthesis failed ({Code}): {Error}", process.ExitCode, error.Trim());
			throw new InvalidOperationException($"Speech synthesis failed: {error.Trim()}");
		}

		return output;
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return;
		}
	}
}

/// <summary>Reads just enough of a RIFF header to know how long the clip runs.</summary>
public sealed class WaveFileReaderHeader : IDisposable
{
	public WaveFileReaderHeader(string path)
	{
		using var stream = File.OpenRead(path);
		using var reader = new BinaryReader(stream);

		if (ReadFourCC(reader) != "RIFF")
		{
			throw new InvalidDataException("Not a RIFF file.");
		}

		reader.ReadUInt32();

		if (ReadFourCC(reader) != "WAVE")
		{
			throw new InvalidDataException("Not a RIFF WAVE file.");
		}

		var byteRate = 0;

		while (stream.Position + 8 <= stream.Length)
		{
			var chunkStart = stream.Position;
			var chunkId = ReadFourCC(reader);
			var chunkSize = reader.ReadInt32();

			if (chunkId == "fmt " && chunkSize >= 16)
			{
				reader.ReadInt16();
				reader.ReadInt16();
				reader.ReadInt32();
				byteRate = reader.ReadInt32();
			}
			else if (chunkId == "data")
			{
				Duration = byteRate > 0
					? TimeSpan.FromSeconds(chunkSize / (double)byteRate)
					: TimeSpan.Zero;

				return;
			}

			stream.Position = chunkStart + 8 + chunkSize + (chunkSize & 1);
		}
	}

	private static string ReadFourCC(BinaryReader reader)
	{
		return Encoding.ASCII.GetString(reader.ReadBytes(4));
	}

	public TimeSpan Duration { get; }

	public void Dispose()
	{
	}
}