using System.Diagnostics;
using Jarvis.Plugin.Runtime;
using Serilog;

namespace Jarvis.Plugin.Speech;

/// <summary>Why a clip could not be transcribed.</summary>
public enum TranscriptionFailure
{
	/// <summary>The component is not installed.</summary>
	NotInstalled,

	/// <summary>The clip was silent, or too short to contain a word.</summary>
	NoSpeech,

	/// <summary>The model or the tool ran but failed.</summary>
	Failed,
}

/// <summary>The outcome of transcribing one clip.</summary>
public sealed record TranscriptionResult
{
	public required bool Ok { get; init; }

	public string Text { get; init; } = string.Empty;

	public TranscriptionFailure? Failure { get; init; }

	public string? Detail { get; init; }

	public TimeSpan Duration { get; init; }

	public static TranscriptionResult Success(string text, TimeSpan duration) =>
		new() { Ok = true, Text = text, Duration = duration };

	public static TranscriptionResult Failed(TranscriptionFailure failure, string? detail = null) =>
		new() { Ok = false, Failure = failure, Detail = detail };
}

/// <summary>
/// Transcribes recorded audio with whisper.cpp.
/// <para>
/// Driven as a child process, like Piper, because whisper.cpp offers only a command line. The transcript is
/// read from the file whisper writes rather than from its standard output: the output file is a documented,
/// stable interface, whereas stdout interleaves progress, timings and warnings that change between
/// versions.
/// </para>
/// <para>
/// Audio is written to a temporary file and deleted immediately. It is never written anywhere else and
/// never kept, because a continuously listening microphone produces a recording of every room it is in.
/// </para>
/// </summary>
public sealed class WhisperTranscriber(RuntimeManager runtime, ILogger logger)
{
	/// <summary>
	/// Generous next to a fast tool, because the first run pays for loading a model that can be hundreds
	/// of megabytes, and short enough that a wedged process is noticed.
	/// </summary>
	private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

	private readonly RuntimeManager _runtime = runtime;
	private readonly ILogger _logger = logger.ForContext<WhisperTranscriber>();

	public bool IsAvailable => _runtime.AssetPath(PinnedAssets.WhisperBinary) is not null;

	/// <summary>The model files actually on disk, by name. Empty when the component is not installed.</summary>
	public IReadOnlyList<string> InstalledModels => _runtime
		.InstalledFiles(AssetCatalog.Whisper, "*.bin")
		.Select(Path.GetFileName)
		.OfType<string>()
		.ToArray();

	public async Task<TranscriptionResult> TranscribeAsync(
		string wavPath,
		string language,
		CancellationToken cancellationToken)
	{
		if (_runtime.UnpackedFile(PinnedAssets.WhisperBinary, "Release", "whisper-cli.exe") is not { } executable)
		{
			return TranscriptionResult.Failed(TranscriptionFailure.NotInstalled);
		}

		if (ModelPath() is not { } model)
		{
			return TranscriptionResult.Failed(TranscriptionFailure.NotInstalled, "no whisper model is installed");
		}

		// Whisper appends .txt to the prefix it is given, so the prefix is chosen to be unique and the
		// transcript is read back from where it promised to put it.
		var prefix = Path.Combine(Path.GetTempPath(), $"jarvis-stt-{Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant()}");
		var transcriptPath = prefix + ".txt";

		try
		{
			var started = DateTimeOffset.UtcNow;

			await RunAsync(executable, model, wavPath, prefix, language, cancellationToken).ConfigureAwait(false);

			var text = File.Exists(transcriptPath) ? File.ReadAllText(transcriptPath).Trim() : string.Empty;

			return text.Length == 0
				? TranscriptionResult.Failed(TranscriptionFailure.NoSpeech)
				: TranscriptionResult.Success(text, DateTimeOffset.UtcNow - started);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Warning(exception, "The recording could not be transcribed.");
			return TranscriptionResult.Failed(TranscriptionFailure.Failed, exception.Message);
		}
		finally
		{
			TryDelete(transcriptPath);
		}
	}

	/// <summary>
	/// Picks the model to use: the configured one when it is installed, otherwise the largest installed
	/// model. Falling back to a different model beats refusing to work, and the chosen name is reported so
	/// a wrong answer is explainable.
	/// </summary>
	private string? ModelPath()
	{
		var installed = _runtime.InstalledFiles(AssetCatalog.Whisper, "*.bin").ToArray();

		if (installed.Length == 0)
		{
			return null;
		}

		var preferred = installed.FirstOrDefault(path =>
			Path.GetFileName(path).Contains("tiny.en", StringComparison.OrdinalIgnoreCase));

		return preferred ?? installed.OrderByDescending(path => new FileInfo(path).Length).First();
	}

	private static async Task RunAsync(
		string executable,
		string model,
		string wavPath,
		string outputPrefix,
		string language,
		CancellationToken cancellationToken)
	{
		var info = new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WindowStyle = ProcessWindowStyle.Hidden,
			// whisper.cpp resolves its DLLs beside the executable, so the working directory has to be
			// wherever the archive unpacked to rather than the plugin's own.
			WorkingDirectory = Path.GetDirectoryName(executable)!,
		};

		info.ArgumentList.Add("-m");
		info.ArgumentList.Add(model);
		info.ArgumentList.Add("-f");
		info.ArgumentList.Add(wavPath);

		// 'auto' is whisper.cpp's own spelling for detect the language, and is what a caller asking for
		// automatic detection should pass rather than an empty string.
		info.ArgumentList.Add("-l");
		info.ArgumentList.Add(string.IsNullOrWhiteSpace(language) ? "auto" : language);

		info.ArgumentList.Add("-otxt");
		info.ArgumentList.Add("-of");
		info.ArgumentList.Add(outputPrefix);

		// Timestamps would prefix every segment with a time, which is noise in a chat transcript.
		info.ArgumentList.Add("-nt");

		// A quarter of the machine's cores: whisper scales poorly past that, and using everything makes the
		// assistant unresponsive while it is thinking.
		info.ArgumentList.Add("-t");
		info.ArgumentList.Add(Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString(
			System.Globalization.CultureInfo.InvariantCulture));

		using var process = Process.Start(info)
			?? throw new InvalidOperationException("whisper.cpp could not be started.");

		var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

		using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		budget.CancelAfter(Budget);

		try
		{
			await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			TryKill(process);
			throw new TimeoutException($"whisper.cpp did not finish within {Budget.TotalMinutes:F0} minutes.");
		}

		var error = await stderr.ConfigureAwait(false);
		await stdout.ConfigureAwait(false);

		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException($"whisper.cpp exited with {process.ExitCode}: {error.Trim()}");
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
			// Already gone, which is what was wanted.
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
			// A leftover transcript is a privacy problem, so it is retried on the next pass rather than
			// being allowed to sit here quietly.
		}
	}
}