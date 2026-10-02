using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Jarvis.Plugin.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace Jarvis.Plugin.Speech;

/// <summary>
/// Plays a rendered clip and reports how loud it currently is.
/// <para>
/// The level comes from a loopback capture of the default render device rather than from the samples
/// being played, so the orb reacts to what the room actually hears and a clip routed to a different
/// device still moves it.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SpeechPlayer : IDisposable
{
	private readonly ILogger _logger;
	private readonly Lock _gate = new();

	private TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private WasapiPlayer? _output;
	private WaveFileReader? _reader;
	private WasapiRecorder? _loopback;

	public SpeechPlayer(ILogger logger)
	{
		_logger = logger.ForContext<SpeechPlayer>();
	}

	public bool IsPlaying
	{
		get
		{
			lock (_gate)
			{
				return _output is not null;
			}
		}
	}

	public TimeSpan Duration { get; private set; }

	/// <summary>Starts playback. Any clip already playing is stopped first.</summary>
	public bool Play(string wavPath, Action<double> onLevel, int volumePercent)
	{
		Stop();

		try
		{
			var reader = new WaveFileReader(wavPath);
			using var enumerator = new MMDeviceEnumerator();
			var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);

			var output = new WasapiPlayerBuilder()
				.WithDevice(endpoint)
				.WithSharedMode()
				.WithPollingSync()
				.WithLatency(150)
				.Build();

			output.Volume = Math.Clamp(volumePercent / 100f, 0f, 1f);

			output.PlaybackStopped += (_, _) => OnPlaybackStopped(output);

			output.Init(reader);

			Duration = reader.TotalTime;

			lock (_gate)
			{
				_reader = reader;
				_output = output;
				_finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			}

			StartLoopback(onLevel);
			output.Play();
			return true;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Warning(exception, "The clip could not be played.");
			Stop();
			return false;
		}
	}

	/// <summary>Completes when the clip finishes or is stopped.</summary>
	public Task WaitAsync(CancellationToken cancellationToken) => _finished.Task.WaitAsync(cancellationToken);

	/// <summary>
	/// A clip ends on its own rather than through <see cref="Stop"/>, so the fields have to be released
	/// here or <see cref="IsPlaying"/> would stay true forever. Disposing the player from inside its own
	/// event handler risks a deadlock on the polling thread, so the work is handed to the pool and
	/// completion is only signalled once the file handle is closed, which lets the caller delete the clip.
	/// </summary>
	private void OnPlaybackStopped(WasapiPlayer output)
	{
		WaveFileReader? reader;
		WasapiRecorder? loopback;

		lock (_gate)
		{
			if (!ReferenceEquals(_output, output))
			{
				return;
			}

			reader = _reader;
			loopback = _loopback;
			_output = null;
			_reader = null;
			_loopback = null;
		}

		_ = Task.Run(() =>
		{
			DisposeLoopback(loopback);
			reader?.Dispose();
			DisposePlayer(output);
			_finished.TrySetResult();
		});
	}

	/// <summary>
	/// Loopback can fail on endpoints that do not support it, most often a device with no render stream
	/// active. Failing to capture a level is no reason to refuse to speak, so this degrades silently.
	/// </summary>
	private void StartLoopback(Action<double> onLevel)
	{
		try
		{
			using var enumerator = new MMDeviceEnumerator();
			var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
			var meter = new AmplitudeMeter();

			var recorder = new WasapiRecorderBuilder()
				.WithDevice(endpoint)
				.WithLoopbackCapture()
				.WithPollingSync()
				.Build();

			recorder.DataAvailable += (ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long _, long __) =>
			{
				if (flags.HasFlag(AudioClientBufferFlags.Silent) || buffer.Length < sizeof(float))
				{
					return;
				}

				onLevel(meter.Accumulate(MemoryMarshal.Cast<byte, float>(buffer)));
			};

			recorder.StartRecording();

			lock (_gate)
			{
				_loopback = recorder;
			}
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Debug(exception, "Loopback capture is unavailable; the orb will not react to speech.");
		}
	}

	public void Stop()
	{
		WasapiPlayer? output;
		WaveFileReader? reader;
		WasapiRecorder? loopback;

		lock (_gate)
		{
			output = _output;
			reader = _reader;
			loopback = _loopback;
			_output = null;
			_reader = null;
			_loopback = null;
			_finished.TrySetResult();
		}

		if (output is not null)
		{
			try
			{
				output.Stop();
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				_logger.Debug(exception, "Playback did not stop cleanly.");
			}
		}

		DisposeLoopback(loopback);
		reader?.Dispose();
		DisposePlayer(output);
	}

	private void DisposeLoopback(WasapiRecorder? recorder)
	{
		if (recorder is null)
		{
			return;
		}

		try
		{
			recorder.StopRecording();
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Debug(exception, "Loopback did not stop cleanly.");
		}

		try
		{
			recorder.Dispose();
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Debug(exception, "Loopback did not dispose cleanly.");
		}
	}

	private void DisposePlayer(WasapiPlayer? player)
	{
		if (player is null)
		{
			return;
		}

		try
		{
			player.Dispose();
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Debug(exception, "The player did not dispose cleanly.");
		}
	}

	public void Dispose()
	{
		Stop();
		GC.SuppressFinalize(this);
	}
}