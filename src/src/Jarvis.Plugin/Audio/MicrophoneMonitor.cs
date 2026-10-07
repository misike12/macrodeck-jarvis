using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Jarvis.Plugin.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace Jarvis.Plugin.Audio;

/// <summary>
/// Captures the microphone and publishes a smoothed level to the shared state holder.
/// <para>
/// The publish rate is the whole design constraint. The host's UI patch bucket is thirty a second and
/// terminates a session that overshoots, and the state holder already refuses to notify below a deadband,
/// so audio arrives as fast as the device produces it and leaves here.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MicrophoneMonitor : IDisposable
{
	private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(50);

	/// <summary>
	/// The rate the device is opened at, matching <see cref="AudioDeviceCatalog.TryOpenCapture"/>. Named
	/// here so a consumer can size a buffer against it without asking the device what it negotiated.
	/// </summary>
	public const int SampleRate = 48_000;

	private readonly AssistantStateHolder _state;
	private readonly ILogger _logger;

	private readonly Lock _gate = new();
	private WasapiRecorder? _capture;
	private Timer? _timer;
	private AmplitudeMeter _meter = new();
	private Input.SampleRingBuffer? _tap;

	/// <summary>
	/// An optional rolling copy of the captured samples. The monitor itself keeps no audio and writes
	/// nothing: this exists so a wake word can recover the word that has just been said, which a level
	/// meter alone can never provide.
	/// </summary>
	public Input.SampleRingBuffer? Tap => _tap;

	/// <summary>Points the monitor at a rolling buffer. Existing capture keeps running.</summary>
	public void AttachTap(Input.SampleRingBuffer buffer)
	{
		lock (_gate)
		{
			_tap = buffer;
		}
	}

	/// <summary>
	/// Stops copying samples anywhere.
	/// <para>
	/// Needed because the monitor outlives any one configuration. Without it, switching the wake word off
	/// left the tap attached, so the microphone kept filling a ring buffer nothing was reading: the user
	/// turned a feature off and the recording did not stop.
	/// </para>
	/// </summary>
	public void DetachTap()
	{
		lock (_gate)
		{
			_tap = null;
		}
	}

	public MicrophoneMonitor(AssistantStateHolder state, ILogger logger)
	{
		_state = state;
		_logger = logger.ForContext<MicrophoneMonitor>();
	}

	public string? ActiveDeviceName { get; private set; }

	/// <summary>
	/// The format the open device is actually delivering, which is not the one that was asked for.
	/// <para>
	/// Shared mode converts for most devices, and a driver that will not converts nothing: several 96 kHz and
	/// 192 kHz interfaces hand over their own rate, a Bluetooth headset its own, and an aggregate endpoint
	/// whatever the driver underneath it settled on. Every consumer resamples from this rather than from
	/// <see cref="SampleRate"/>, because treating a 96 kHz stream as 48 kHz halves its pitch: the audio still
	/// looks like speech on a level meter and the wake word scores nothing at all.
	/// </para>
	/// </summary>
	public CaptureFormat ActiveFormat { get; private set; } = CaptureFormat.Preferred;

	/// <summary>The rate half of <see cref="ActiveFormat"/>, which is what the resamplers need.</summary>
	public int ActiveSampleRate => ActiveFormat.SampleRate;

	public string? LastError { get; private set; }

	/// <summary>
	/// The measured microphone level, separate from whatever the orb is showing.
	/// <para>
	/// The orb shows the render loopback while a reply is playing, which is the right thing to look at but
	/// the wrong thing to decide on: a barge-in has to be judged on the microphone, or JARVIS would interrupt
	/// itself every time it spoke.
	/// </para>
	/// </summary>
	public double Level
	{
		get
		{
			lock (_gate)
			{
				return _capture is null ? 0 : _meter.Level;
			}
		}
	}

	/// <summary>Whether capture is running, which a barge-in detector needs before it can mean anything.</summary>
	public bool IsListening
	{
		get
		{
			lock (_gate)
			{
				return _capture is not null;
			}
		}
	}

	/// <summary>
	/// Opens the configured device. Returns false rather than throwing: a missing microphone degrades the
	/// orb to a static one and must not stop the plugin from starting.
	/// </summary>
	public bool Start(string? preferredId, string? preferredName)
	{
		Stop();

		var device = AudioDeviceCatalog.ResolveCapture(preferredId, preferredName);

		if (device is null)
		{
			LastError = "No capture device is available.";
			_logger.Warning("{Error}", LastError);
			return false;
		}

		if (!AudioDeviceCatalog.TryOpenCapture(device, out var capture, out var error, out var format) || capture is null)
		{
			LastError = error ?? "The capture device could not be opened.";
			_logger.Warning("{Error} on {Device}.", LastError, device.Name);
			return false;
		}

		capture.DataAvailable += OnDataAvailable;

		try
		{
			capture.StartRecording();
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			LastError = exception.Message;
			capture.DataAvailable -= OnDataAvailable;
			capture.Dispose();
			_logger.Warning(exception, "Recording could not be started on {Device}.", device.Name);
			return false;
		}

		lock (_gate)
		{
			_capture = capture;
			_meter = new AmplitudeMeter();
			LastError = null;
			_timer = new Timer(_ => Publish(), null, PublishInterval, PublishInterval);
		}

		ActiveDeviceName = device.Name;
		ActiveFormat = format;

		// The format is logged whenever it is not the one asked for, because a device that opened its own way
		// is the kind of thing that looks like a wake word bug until it is written down.
		if (format == CaptureFormat.Preferred)
		{
			_logger.Information("Microphone capture started on {Device} at {Format}.", device.Name, format);
		}
		else
		{
			_logger.Information(
				"Microphone capture started on {Device} at {Format}, where the plugin asked for {Requested}.",
				device.Name,
				format,
				CaptureFormat.Preferred);
		}

		return true;
	}

	public void Stop()
	{
		Timer? timer;
		WasapiRecorder? capture;

		lock (_gate)
		{
			timer = _timer;
			capture = _capture;
			_timer = null;
			_capture = null;
		}

		timer?.Dispose();

		if (capture is not null)
		{
			capture.DataAvailable -= OnDataAvailable;

			try
			{
				capture.StopRecording();
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				_logger.Debug(exception, "Recording did not stop cleanly.");
			}

			capture.Dispose();
		}

		_state.UpdateAmplitude(0);
	}

	/// <summary>
	/// WASAPI delivers roughly 10 ms packets and the buffer is a span over the driver's own memory, so
	/// it is cast and copied before this returns; nothing here retains it.
	/// </summary>
	private void OnDataAvailable(
		ReadOnlySpan<byte> buffer,
		AudioClientBufferFlags flags,
		long devicePosition,
		long qpcPosition)
	{
		if (flags.HasFlag(AudioClientBufferFlags.Silent))
		{
			return;
		}

		// The channel count comes from the format that was actually negotiated rather than assumed, because a
		// stereo interface that will not convert hands over two channels and reading them as mono plays the
		// audio back at double speed.
		var channels = Math.Max(1, ActiveFormat.Channels);
		var frames = CaptureInterleave.WholeFrames(buffer.Length, channels);

		// A packet that is not a whole number of frames is dropped rather than read: the leftover would shift
		// every sample after it by one position, which the models read as a click at the start of the word.
		if (frames <= 0)
		{
			return;
		}

		var samples = CaptureInterleave.ToMono(MemoryMarshal.Cast<byte, float>(buffer[..(frames * channels * sizeof(float))]), channels);

		lock (_gate)
		{
			_meter.Accumulate(samples);
			_tap?.Append(samples);
		}

		// Raised outside the lock with a defensive copy. OnDataAvailable is the only place raw packets
		// exist - the publish tick carries levels, not audio - and a keyword-spotting engine fed from
		// anything coarser than this would be deciding on a summary of the word rather than the word.
		SamplesCaptured?.Invoke(samples);
	}

	private void Publish()
	{
		double level;

		lock (_gate)
		{
			if (_capture is null)
			{
				return;
			}

			level = _meter.Level;
		}

		_state.UpdateAmplitude(level);

		// The wake word reads the level from here. Its offer method has always been documented as being
		// called from this tick, and it was not: nothing ever raised it, so the detector received no audio at
		// all and the wake word could not fire however it was configured.
		//
		// Handlers are invoked outside the lock and their exceptions swallowed. A subscriber is somebody
		// else's recogniser, and this runs on a timer that also owns the amplitude the orb is showing.
		foreach (var handler in LevelPublished?.GetInvocationList() ?? [])
		{
			try
			{
				((Action<double>)handler)(level);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				_logger.Debug(exception, "A microphone level subscriber failed.");
			}
		}
	}

	/// <summary>
	/// Raised on the publish tick with the smoothed microphone level, for anything that needs to make a
	/// decision from loudness. Not the raw samples: a subscriber that wants audio takes the tap.
	/// </summary>
	public event Action<double>? LevelPublished;

	/// <summary>
	/// Raised for every raw packet the device delivers, as device-rate floats. For the keyword engine,
	/// which needs the audio itself: a ring buffer read a tick later misses the 30 ms between packets, and
	/// the wake word is exactly a couple of hundred milliseconds of audio.
	/// </summary>
	public event Action<float[]>? SamplesCaptured;

	public void Dispose()
	{
		Stop();
		GC.SuppressFinalize(this);
	}
}