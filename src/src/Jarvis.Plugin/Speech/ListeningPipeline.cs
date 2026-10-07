using System.Runtime.Versioning;
using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Speech;
using MacroDeck.Sdk.Actions;
using NAudio.Wave;
using Serilog;

namespace Jarvis.Plugin.Speech;

/// <summary>
	/// One utterance, encoded as the WAV the transcriber reads. Kept here because both callers that need a
	/// temporary WAV - the listening pipeline and the wake word - must agree on the format, and agreeing on
	/// it in one place is cheaper than agreeing on it twice.
/// </summary>
internal static class UtteranceAudio
{
	private const int TargetSampleRate = AudioResampler.SpeechRate;

	/// <summary>
	/// Resamples and writes a 16 kHz mono 16-bit WAV, returning its path. The device runs at 48 kHz, and
	/// whisper's native rate is 16 kHz, so the conversion happens once here rather than in each caller.
	/// </summary>
	public static string WriteWav(float[] samples, int sourceSampleRate)
	{
		var resampled = Resample(samples, sourceSampleRate, TargetSampleRate);
		var pcm = new byte[resampled.Length * 2];

		for (var index = 0; index < resampled.Length; index++)
		{
			BitConverter.TryWriteBytes(
				pcm.AsSpan(index * 2), (short)(Math.Clamp(resampled[index], -1f, 1f) * short.MaxValue));
		}

		var path = Path.Combine(
			Path.GetTempPath(),
			$"jarvis-utt-{Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant()}.wav");

		using var stream = new MemoryStream(pcm);
		using var pcmStream = new RawSourceWaveStream(stream, new WaveFormat(TargetSampleRate, 16, 1));
		WaveFileWriter.CreateWaveFile(path, pcmStream);

		return path;
	}

/// <summary>
	/// Delegates to the shared resampler rather than implementing its own.
	/// <para>
	/// The wake word's front end and this recorder have to convert the same device audio to the same rate,
	/// and two implementations would score the same word differently with nothing to say which was wrong.
	/// </para>
	/// </summary>
	internal static float[] Resample(float[] samples, int from, int to) => AudioResampler.To(samples, from, to);

	public static void Delete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Nothing more can be done, and failing a turn over a leftover file would be worse.
		}
	}
}

/// <summary>
/// Turns "someone pressed the button" into a spoken answer: record one utterance, transcribe it, run the
/// turn, speak the reply.
/// <para>
/// This is the only place that knows the whole voice loop, so the microphone, the transcriber and the
/// speaker cannot drift apart. Two ordering decisions matter: the recording is deleted the moment its
/// transcript exists, before the model is even called, and a failure anywhere returns a truthful action
/// result rather than leaving the assistant stuck listening.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ListeningPipeline : IDisposable
{
	private readonly VoiceRecorder _recorder;
	private readonly WhisperTranscriber _transcriber;
	private readonly AssistantSession _session;
	private readonly JarvisSettingsStore _settings;
	private readonly AssistantStateHolder _state;
	private readonly ILogger _logger;
	private readonly Lock _gate = new();

	private CancellationTokenSource? _listenCts;

	public ListeningPipeline(
		VoiceRecorder recorder,
		WhisperTranscriber transcriber,
		AssistantSession session,
		AssistantStateHolder state,
		JarvisSettingsStore settings,
		ILogger logger)
	{
		_recorder = recorder;
		_transcriber = transcriber;
		_session = session;
		_state = state;
		_settings = settings;
		_logger = logger.ForContext<ListeningPipeline>();
	}

	/// <summary>
	/// How long one turn may last. A watch timer rather than an endless wait, because a threshold that is
	/// never crossed or never falls quiet would otherwise leave the microphone open indefinitely.
	/// </summary>
	private static TimeSpan MaxUtterance => TimeSpan.FromSeconds(20);

	/// <summary>How often the recorder is asked whether the speaker has finished.</summary>
	private static TimeSpan PollInterval => TimeSpan.FromMilliseconds(100);

	/// <summary>True while the microphone is open for a turn.</summary>
	public bool IsListening
	{
		get
		{
			lock (_gate)
			{
				return _listenCts is not null;
			}
		}
	}

	public string? LastError { get; private set; }

	/// <summary>
	/// Records until the speaker stops, transcribes, and answers. A typed prompt short-circuits the
	/// microphone entirely, which is what keeps the say action and the hotkey useful on a machine with no
	/// microphone at all.
	/// </summary>
	public async Task<ActionResult> ListenAndAnswerAsync(string? prompt, CancellationToken cancellationToken)
	{
		LastError = null;

if (!_transcriber.IsAvailable)
		{
			// Names the component rather than saying "speech recognition" in the abstract. A user who has not
			// downloaded the transcriber needs to know which of the two components to fetch, and the
			// component is what the manage-components action takes.
			return ActionResult.Failed(
				ActionErrorCodes.NotConfigured,
				Strings.Errors.ComponentMissing(Runtime.AssetCatalog.Whisper));
		}

		if (!string.IsNullOrWhiteSpace(prompt))
		{
			return await _session.SayAsync(prompt, cancellationToken).ConfigureAwait(false);
		}

		CancellationTokenSource cts;

		lock (_gate)
		{
if (_listenCts is not null)
			{
				// A refusal, not an accepted request: nothing was taken by anything, and the caller can tell
				// the difference between "queued behind your other turn" and "already speaking".
				return ActionResult.Failed(ActionErrorCodes.Unavailable, Strings.Errors.AlreadyRunning());
			}

// No budget of its own. The host's thirty second capability bound applies to a deck button press, not
			// to the hotkey or the wake word, which have no such limit and legitimately run for as long as a
			// person takes to speak. The budget is applied where that distinction is known: in the action
			// executors, which wrap the host's token.
			cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			_listenCts = cts;
		}

		try
		{
			var settings = _settings.Current;

			if (!_recorder.Start(settings.MicrophoneId, settings.MicrophoneName))
			{
				LastError = _recorder.LastError;
				return ActionResult.Failed(ActionErrorCodes.NotConnected, Strings.Errors.MicrophoneUnavailable());
			}

			_state.Transition(AssistantState.Listening, statusLine: "voice");

			using var budget = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
			budget.CancelAfter(MaxUtterance);

			await WaitForSpeechEndAsync(budget.Token).ConfigureAwait(false);

			var wav = _recorder.Finish();
			cts.Token.ThrowIfCancellationRequested();

			if (wav is null)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter, Strings.Errors.NothingHeard());
			}

			TranscriptionResult transcription;

			try
			{
				transcription = await _transcriber
					.TranscribeAsync(wav, settings.SttLanguage, cts.Token)
					.ConfigureAwait(false);
			}
			finally
			{
				// The audio is deleted before the model is called. A transcript is text; the recording is the
				// private part, and nothing downstream needs it.
				TryDelete(wav);
			}

			if (!transcription.Ok)
			{
				return transcription.Failure == TranscriptionFailure.NoSpeech
					? ActionResult.Failed(ActionErrorCodes.InvalidParameter, Strings.Errors.NothingHeard())
					: ActionResult.Failed(ActionErrorCodes.ProviderError, Strings.Errors.TranscriptionFailed());
			}

			_logger.Information("Heard: {Text}", transcription.Text);

			return await _session.SayAsync(transcription.Text, cts.Token).ConfigureAwait(false);
		}
catch (OperationCanceledException)
		{
			// Not a success. Either the caller pressed cancel, the host gave up on the invocation, or the
			// utterance budget ran out, and in all three nothing was said. Reporting success would tell the
			// host a press completed when the user heard silence.
			return ActionResult.Failed(ActionErrorCodes.Timeout, Strings.Errors.TurnCancelled());
		}
		finally
		{
			_recorder.Stop();

			lock (_gate)
			{
				if (ReferenceEquals(_listenCts, cts))
				{
					_listenCts = null;
				}
			}

			cts.Dispose();
		}
	}

	/// <summary>
	/// Polls the recorder for its endpoint rather than waiting on an event it cannot raise: WASAPI delivers
	/// audio on its own thread and the decision of when a person has finished talking belongs here.
	/// </summary>
	private async Task WaitForSpeechEndAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			if (!_recorder.IsRecording)
			{
				// Cancelled from elsewhere, or the device dropped. Either way there is nothing more to hear.
				return;
			}

			if (_recorder.HasReachedEndpoint)
			{
				_logger.Debug("The speaker stopped; ending the utterance.");
				return;
			}

			try
			{
				await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	public void Dispose()
	{
		_recorder.Dispose();
		GC.SuppressFinalize(this);
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Nothing more can be done here, and failing the turn over a leftover file would be worse.
		}
	}
}