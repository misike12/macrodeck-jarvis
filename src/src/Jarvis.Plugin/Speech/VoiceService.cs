using System.Globalization;
using System.Security.Cryptography;
using Jarvis.Plugin.Core;
using Serilog;

namespace Jarvis.Plugin.Speech;

/// <summary>
/// Speaks a reply. One place owns synthesis, playback and the temporary file, so cancellation can stop
/// all three: a clip still rendering is not yet audible, and a clip still playing must not outlive the
/// turn that produced it.
/// </summary>
public sealed class VoiceService(
	WindowsSynthesizer synthesizer,
	PiperSynthesizer piper,
	SpeechPlayer player,
	AssistantStateHolder state,
	JarvisSettingsStore settings,
	ILogger logger) : IDisposable
{
	private readonly WindowsSynthesizer _synthesizer = synthesizer;
	private readonly PiperSynthesizer _piper = piper;
	private readonly SpeechPlayer _player = player;
	private readonly AssistantStateHolder _state = state;
	private readonly JarvisSettingsStore _settings = settings;
	private readonly ILogger _logger = logger.ForContext<VoiceService>();
	private readonly Lock _gate = new();

	private string? _workingDirectory;
	private CancellationTokenSource? _speakCts;

	public bool IsSpeaking
	{
		get
		{
			lock (_gate)
			{
				return _speakCts is not null;
			}
		}
	}

	public Task<string[]> AvailableVoicesAsync(CancellationToken cancellationToken = default) =>
		_synthesizer.InstalledVoicesAsync(cancellationToken);

	/// <summary>
	/// Renders and plays, then returns when the clip has finished. Cancellation stops mid-word because
	/// both the renderer and the player observe the same token.
	/// </summary>
	public async Task<bool> SpeakAsync(string text, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var current = _settings.Current;

		if (current.TextToSpeech is not (TextToSpeechProvider.PiperLocal or TextToSpeechProvider.WindowsSapi))
		{
			return false;
		}

		CancellationTokenSource cts;

		lock (_gate)
		{
			_speakCts?.Dispose();
			cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			_speakCts = cts;
			_workingDirectory ??= CreateWorkingDirectory();
		}

		var token = cts.Token;
		var wavPath = Path.Combine(
			_workingDirectory!,
			$"reply-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.wav");

		try
		{
			_state.Transition(AssistantState.Speaking);

			if (!await RenderAsync(text, wavPath, current, token).ConfigureAwait(false))
			{
				return false;
			}

			token.ThrowIfCancellationRequested();

			if (!_player.Play(wavPath, level => _state.UpdateAmplitude(level), current.VolumePercent))
			{
				return false;
			}

			await WaitForPlaybackAsync(token).ConfigureAwait(false);
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Warning(exception, "The reply could not be spoken.");
			return false;
		}
		finally
		{
			_player.Stop();
			_state.UpdateAmplitude(0);

			lock (_gate)
			{
				if (ReferenceEquals(_speakCts, cts))
				{
					_speakCts = null;
				}
			}

			cts.Dispose();
			TryDelete(wavPath);
		}
	}

	private async Task WaitForPlaybackAsync(CancellationToken cancellationToken)
	{
		await _player.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The fallback chain. Piper is preferred because it is the voice JARVIS is meant to have, but it is
	/// an optional download, so a machine that has not installed it gets SAPI rather than silence. The
	/// fallback is silent to the user on purpose: they asked for a spoken reply, not for an explanation of
	/// which engine produced it, and a missing component is already reported as an issue.
	/// </summary>
	private async Task<bool> RenderAsync(
		string text,
		string wavPath,
		JarvisSettings current,
		CancellationToken cancellationToken)
	{
		if (current.TextToSpeech == TextToSpeechProvider.PiperLocal)
		{
			if (await _piper
				.TrySynthesizeAsync(text, wavPath, current.PiperVoice, cancellationToken)
				.ConfigureAwait(false))
			{
				return true;
			}

			_logger.Information(
				"Piper is unavailable, falling back to the Windows voice. {Reason}",
				_piper.IsAvailable ? "the configured voice is not installed" : "the component is not installed");
		}

		await _synthesizer
			.SynthesizeAsync(text, wavPath, current.WindowsVoice, 0, 100, cancellationToken)
			.ConfigureAwait(false);

		return File.Exists(wavPath);
	}

	/// <summary>Stops mid-word. Used by the cancel action, the wake word and barge-in alike.</summary>
	public void Stop()
	{
		CancellationTokenSource? cts;

		lock (_gate)
		{
			cts = _speakCts;
			_speakCts = null;
		}

		try
		{
			cts?.Cancel();
		}
		catch (ObjectDisposedException)
		{
			return;
		}
		finally
		{
			_player.Stop();
		}
	}

	public void Dispose()
	{
		Stop();
		_player.Dispose();
		GC.SuppressFinalize(this);
	}

	/// <summary>
	/// Clips live in the plugin data directory so they survive an update and never sit next to the
	/// executable, which is immutable.
	/// </summary>
	private static string CreateWorkingDirectory()
	{
		var root = Environment.GetEnvironmentVariable("MACRO_DECK_PLUGIN_DATA_DIRECTORY");

		if (string.IsNullOrWhiteSpace(root))
		{
			root = Path.Combine(Path.GetTempPath(), "jarvis-speech");
		}
		else
		{
			root = Path.Combine(root, "speech");
		}

		Directory.CreateDirectory(root);
		return root;
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