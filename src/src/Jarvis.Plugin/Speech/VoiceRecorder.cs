using System.Runtime.Versioning;
using Jarvis.Plugin.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace Jarvis.Plugin.Speech;

/// <summary>
/// Records one utterance from the microphone and writes it as a WAV for the transcriber.
/// <para>
/// Separate from <see cref="MicrophoneMonitor"/> because that one is a permanent level meter for the orb
/// and must never write anything to disk, while this one opens the device only while someone is speaking
/// and deletes its recording as soon as it has been transcribed. Audio is never retained: a microphone
/// that is always on produces a recording of every room it sits in.
/// </para>
/// <para>
/// Endpoints are found by energy rather than by a timer, because a fixed duration truncates half the
/// sentences and pads the other half. Speaking starts above a threshold and ends after a stretch of
/// quiet, which is roughly how a person decides they have finished.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VoiceRecorder : IDisposable
{
	/// <summary>
	/// Whisper's native sample rate. The device usually runs at 48 kHz, and resampling down costs a fraction
	/// of a second and removes the need for whisper to do it.
	/// </summary>
	private const int TargetSampleRate = 16_000;

	private readonly ILogger _logger;
	private readonly Lock _gate = new();

	private WasapiRecorder? _capture;
	private List<float>? _samples;
	private bool _speaking;
	private int _quietRuns;

	/// <param name="speechThreshold">RMS above which audio counts as speech.</param>
	/// <param name="silenceMilliseconds">Quiet that ends an utterance.</param>
	/// <param name="maximumMilliseconds">Hard cap, so a room tone that never falls quiet cannot record forever.</param>
	public VoiceRecorder(
		ILogger logger,
		double speechThreshold = 0.02,
		int silenceMilliseconds = 900,
		int maximumMilliseconds = 20_000)
	{
		_logger = logger.ForContext<VoiceRecorder>();
		SpeechThreshold = speechThreshold;
		SilenceMilliseconds = silenceMilliseconds;
		MaximumMilliseconds = maximumMilliseconds;
	}

	public double SpeechThreshold { get; }

	public int SilenceMilliseconds { get; }

	public int MaximumMilliseconds { get; }

	public bool IsRecording
	{
		get
		{
			lock (_gate)
			{
				return _capture is not null;
			}
		}
	}

	/// <summary>What went wrong if a recording could not be started.</summary>
	public string? LastError { get; private set; }

	/// <summary>
	/// Begins recording on the chosen device. Returns false rather than throwing, because "no microphone"
	/// is a normal state the caller reports to a user rather than a fault.
	/// </summary>
	public bool Start(string? preferredId, string? preferredName)
	{
		Stop();

		try
		{
			var device = AudioDeviceCatalog.ResolveCapture(preferredId, preferredName)
				?? throw new InvalidOperationException("No capture device was found.");

			var recorder = new WasapiRecorderBuilder()
				.WithDevice(new MMDeviceEnumerator().GetDevice(device.Id))
				.WithSharedMode()
				.WithPollingSync()
				.WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(TargetSampleRate, 1))
				.Build();

			recorder.DataAvailable += OnData;

			lock (_gate)
			{
				_samples = [];
				_capture = recorder;
				_speaking = false;
				_quietRuns = 0;
			}

			recorder.StartRecording();
			LastError = null;

			return true;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			LastError = exception.Message;
			_logger.Warning(exception, "Recording could not be started.");
			Stop();
			return false;
		}
	}

	/// <summary>
	/// Stops and writes the utterance to a temporary WAV, or returns null when nothing was said. The
	/// caller owns the file and must delete it once the transcript exists.
	/// </summary>
	public string? Finish()
	{
		WasapiRecorder? capture;
		List<float>? samples;
		double seconds;

		lock (_gate)
		{
			capture = _capture;
			samples = _samples;
			_capture = null;
			_samples = null;

			if (capture is null)
			{
				return null;
			}
		}

		try
		{
			capture.StopRecording();
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException)
		{
			// The samples are already in hand, so a recorder that will not stop cleanly is not fatal.
		}

		capture.DataAvailable -= OnData;
		capture.Dispose();

		if (samples is not { Count: > 0 })
		{
			return null;
		}

		seconds = samples.Count / (double)TargetSampleRate;

		// Under a fifth of a second is a click or a breath, not a sentence. Feeding it to a speech model
		// wastes a model load and invites a hallucinated transcript.
		if (seconds < 0.2)
		{
			return null;
		}

		var path = Path.Combine(
			Path.GetTempPath(),
			$"jarvis-utt-{Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant()}.wav");

		// Whisper reads 16-bit PCM. The capture is already IEEE float at the target rate, so the samples are
		// narrowed and clipped here rather than resampled a second time downstream.
		var format = new WaveFormat(TargetSampleRate, 16, 1);
		using var pcm = new RawSourceWaveStream(new MemoryStream(ToPcm16(samples)), format);
		WaveFileWriter.CreateWaveFile(path, pcm);

		return path;
	}

	public void Stop()
	{
		WasapiRecorder? capture;

		lock (_gate)
		{
			capture = _capture;
			_capture = null;
			_samples = null;
			_speaking = false;
		}

		if (capture is null)
		{
			return;
		}

		try
		{
			capture.StopRecording();
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException)
		{
			// Nothing left to do about it.
		}

		capture.DataAvailable -= OnData;
		capture.Dispose();
	}

	/// <summary>Whether the utterance has gone quiet long enough to be finished.</summary>
	public bool HasReachedEndpoint
	{
		get
		{
			lock (_gate)
			{
				return _speaking && _quietRuns >= SilenceMilliseconds;
			}
		}
	}

	private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long _, long __)
	{
		if (flags.HasFlag(AudioClientBufferFlags.Silent) || buffer.Length < sizeof(float))
		{
			return;
		}

		// WASAPI hands over a span over the driver's own memory, so the samples are copied out before this
		// returns. Nothing here retains the span.
		var block = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer).ToArray();
		var meter = new AmplitudeMeter();
		var level = meter.Accumulate(block);

		lock (_gate)
		{
			if (_capture is null || _samples is not { } samples)
			{
				return;
			}

			var blockMilliseconds = (int)(block.Length * 1000.0 / TargetSampleRate);

			if (level >= SpeechThreshold)
			{
				_speaking = true;
				_quietRuns = 0;
				samples.AddRange(block);
			}
			else if (_speaking)
			{
				_quietRuns += blockMilliseconds;
				samples.AddRange(block);

				// The trailing quiet is kept so the tail of the last word is not clipped off.
				if (_quietRuns >= SilenceMilliseconds
					|| samples.Count >= TargetSampleRate * MaximumMilliseconds / 1000)
				{
					_speaking = false;
				}
			}
		}
	}

	/// <summary>
	/// Whisper reads 16-bit PCM. The capture is already IEEE float at the target rate, so this narrows it
	/// and clips rather than resampling a second time.
	/// </summary>
	private static byte[] ToPcm16(List<float> samples)
	{
		var pcm = new byte[samples.Count * 2];

		for (var index = 0; index < samples.Count; index++)
		{
			var clamped = Math.Clamp(samples[index], -1f, 1f);
			BitConverter.TryWriteBytes(pcm.AsSpan(index * 2), (short)(clamped * short.MaxValue));
		}

		return pcm;
	}

	public void Dispose()
	{
		Stop();
		GC.SuppressFinalize(this);
	}
}