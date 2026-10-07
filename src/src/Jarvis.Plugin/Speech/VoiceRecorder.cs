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
	private CaptureFormat _format = CaptureFormat.Preferred;
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

			// Whisper wants 16 kHz, but it is asked for rather than required: a device that will not convert
			// is opened at its own rate and resampled here, which is slower and always works, while insisting
			// on the rate means no microphone at all on hardware that works everywhere else.
			if (!AudioDeviceCatalog.TryOpenCapture(device, out var recorder, out var error, out var format)
				|| recorder is null)
			{
				LastError = error ?? "The capture device could not be opened.";
				_logger.Warning("{Error} on {Device}.", LastError, device.Name);
				return false;
			}

			recorder.DataAvailable += OnData;

			lock (_gate)
			{
				_samples = [];
				_format = format;
				_capture = recorder;
				_speaking = false;
				_quietRuns = 0;
			}

			recorder.StartRecording();
			LastError = null;

			if (format != CaptureFormat.Preferred)
			{
				_logger.Information(
					"Recording from {Device} at {Format}, where the plugin asked for {Requested}.",
					device.Name,
					format,
					CaptureFormat.Preferred);
			}

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
		CaptureFormat format;

		lock (_gate)
		{
			capture = _capture;
			samples = _samples;
			format = _format;
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

		// Whisper reads 16-bit PCM at 16 kHz. A device that delivered its own rate is resampled here, once,
		// rather than handed to the transcriber as a clip that plays back at the wrong speed.
		var atTargetRate = format.SampleRate == TargetSampleRate
			? [.. samples]
			: UtteranceAudio.Resample([.. samples], format.SampleRate, TargetSampleRate);

		var pcm16 = ToPcm16(atTargetRate);
		var seconds = atTargetRate.Length / (double)TargetSampleRate;

		// Under a fifth of a second is a click or a breath, not a sentence. Feeding it to a speech model
		// wastes a model load and invites a hallucinated transcript.
		if (seconds < 0.2)
		{
			return null;
		}

		var path = Path.Combine(
			Path.GetTempPath(),
			$"jarvis-utt-{Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant()}.wav");

		// Already at the target rate by this point, so the samples are narrowed and clipped rather than
		// resampled a second time downstream.
		using var pcm = new RawSourceWaveStream(
			new MemoryStream(pcm16),
			new WaveFormat(TargetSampleRate, 16, 1));
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
		if (flags.HasFlag(AudioClientBufferFlags.Silent))
		{
			return;
		}

		CaptureFormat format;

		lock (_gate)
		{
			if (_capture is null)
			{
				return;
			}

			format = _format;
		}

		// The device's own channel count and rate, both taken from the format that was negotiated rather than
		// assumed: a stereo or 96 kHz interface that will not convert hands over exactly what it has, and
		// reading either of those as 16 kHz mono is noise.
		var channels = Math.Max(1, format.Channels);
		var frames = CaptureInterleave.WholeFrames(buffer.Length, channels);

		if (frames <= 0)
		{
			return;
		}

		// WASAPI hands over a span over the driver's own memory, so the samples are copied out before this
		// returns. Nothing here retains the span.
		var block = CaptureInterleave.ToMono(
			System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer[..(frames * channels * sizeof(float))]),
			channels);

		var meter = new AmplitudeMeter();
		var level = meter.Accumulate(block);

		lock (_gate)
		{
			if (_capture is null || _samples is not { } samples)
			{
				return;
			}

			var blockMilliseconds = (int)(block.Length * 1000.0 / format.SampleRate);

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

				// The trailing quiet is kept so the tail of the last word is not clipped off. Both bounds are
				// counted at the device's own rate, so a 96 kHz interface is not cut off after a tenth of the
				// time it should have recorded.
				if (_quietRuns >= SilenceMilliseconds
					|| samples.Count >= format.SampleRate * MaximumMilliseconds / 1000)
				{
					_speaking = false;
				}
			}
		}
	}

	/// <summary>
	/// Whisper reads 16-bit PCM, so the float capture is narrowed and clipped here. Called only once the
	/// samples are at the target rate.
	/// </summary>
	private static byte[] ToPcm16(float[] samples)
	{
		var pcm = new byte[samples.Length * 2];

		for (var index = 0; index < samples.Length; index++)
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