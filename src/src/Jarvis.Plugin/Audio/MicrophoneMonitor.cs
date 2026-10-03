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

	public MicrophoneMonitor(AssistantStateHolder state, ILogger logger)
	{
		_state = state;
		_logger = logger.ForContext<MicrophoneMonitor>();
	}

	public string? ActiveDeviceName { get; private set; }

	public string? LastError { get; private set; }

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

		if (!AudioDeviceCatalog.TryOpenCapture(device, out var capture, out var error) || capture is null)
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
		_logger.Information("Microphone capture started on {Device}.", device.Name);
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
		if (flags.HasFlag(AudioClientBufferFlags.Silent) || buffer.Length < sizeof(float))
		{
			return;
		}

		var samples = MemoryMarshal.Cast<byte, float>(buffer).ToArray();

		lock (_gate)
		{
			_meter.Accumulate(samples);
			_tap?.Append(samples);
		}
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
	}

	public void Dispose()
	{
		Stop();
		GC.SuppressFinalize(this);
	}
}