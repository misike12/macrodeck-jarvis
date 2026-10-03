using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Speech;
using Serilog;

namespace Jarvis.Plugin.Speech;

/// <summary>
/// Interrupts a spoken reply when the user starts talking over it.
/// <para>
/// The decision needs three things to go right, and each of them has a way of going wrong that looks like
/// working:
/// </para>
/// <list type="bullet">
/// <item>The level has to come from the microphone, not from the render loopback. Judging on the loopback
/// means JARVIS interrupts itself every time it speaks.</item>
/// <item>The threshold has to be exceeded for a moment, not once. A single packet above the line is a
/// click, a door or a cough, and interrupting on one of those is worse than not interrupting at all.</item>
/// <item>It has to stop listening for a moment afterwards. Without that, the tail of the reply that was
/// already in the speaker buffer comes back through the microphone and starts a second interruption.</item>
/// </list>
/// </summary>
public sealed class BargeInDetector : IDisposable
{
	/// <summary>
	/// Fast enough that a natural interruption is caught at the start of a word rather than after it,
	/// which is the difference between a barge-in feeling responsive and feeling broken.
	/// </summary>
	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

	/// <summary>
	/// How long the level must stay over the threshold. Long enough to exclude a click, short enough that
	/// a deliberate interruption still feels immediate.
	/// </summary>
	private static readonly TimeSpan RequiredDuration = TimeSpan.FromMilliseconds(320);

	/// <summary>
	/// After interrupting, the detector stays quiet for this long. It has to be longer than the output
	/// latency, because the audio already queued in the speaker comes back through the microphone.
	/// </summary>
	private static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(900);

	private readonly MicrophoneMonitor _microphone;
	private readonly VoiceService _voice;
	private readonly ILogger _logger;
	private readonly Lock _gate = new();

	private Timer? _timer;
	private DateTime? _overSince;
	private DateTime _cooldownUntil = DateTime.MinValue;
	private bool _disposed;

	/// <summary>
	/// How many times this detector has interrupted a reply. Read by the tests and by the state reader, so
	/// barge-in can be shown to have happened rather than merely enabled.
	/// </summary>
	private int _interruptions;

	public BargeInDetector(MicrophoneMonitor microphone, VoiceService voice, ILogger logger)
	{
		_microphone = microphone;
		_voice = voice;
		_logger = logger.ForContext<BargeInDetector>();
	}

	public int Interruptions
	{
		get
		{
			lock (_gate)
			{
				return _interruptions;
			}
		}
	}

	/// <summary>Starts watching. Does nothing until a reply is actually playing.</summary>
	public void Start()
	{
		lock (_gate)
		{
			if (_disposed || _timer is not null)
			{
				return;
			}

			_timer = new Timer(OnPoll, null, PollInterval, PollInterval);
		}
	}

	public void Stop()
	{
		Timer? timer;

		lock (_gate)
		{
			timer = _timer;
			_timer = null;
			_overSince = null;
		}

		timer?.Dispose();
	}

	private void OnPoll(object? state)
	{
		try
		{
			if (!_voice.IsSpeaking)
			{
				ResetRun();

				lock (_gate)
				{
					_cooldownUntil = DateTime.MinValue;
				}

				return;
			}

			var now = DateTime.UtcNow;
			DateTime cooldownUntil;

			lock (_gate)
			{
				cooldownUntil = _cooldownUntil;
			}

			if (now < cooldownUntil)
			{
				return;
			}

			// Judged on the microphone. The render loopback would say JARVIS is speaking, which is true, and
			// would make it interrupt itself.
			var level = _microphone.Level;
			var threshold = _threshold;

			if (!_microphone.IsListening || level < threshold)
			{
				ResetRun();
				return;
			}

			DateTime? overSince;

			lock (_gate)
			{
				_overSince ??= now;
				overSince = _overSince;
			}

			if (now - overSince < RequiredDuration)
			{
				return;
			}

			ResetRun();

			lock (_gate)
			{
				_cooldownUntil = now.Add(Cooldown);
				_interruptions++;
			}

			_logger.Information("Interrupted the reply: speech held over {Threshold} for {Duration}ms.", threshold, RequiredDuration.TotalMilliseconds);
			_voice.Stop();
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			// A failed poll must not kill the timer, and must certainly not end the reply.
			_logger.Debug(exception, "The barge-in check did not complete.");
			ResetRun();
		}
	}

	private void ResetRun()
	{
		lock (_gate)
		{
			_overSince = null;
		}
	}

	private double _threshold = 0.12;

	/// <summary>
	/// The level that counts as speech. Set from the configured value; the default matches the documented
	/// setting so a detector built without configuration still behaves sensibly.
	/// </summary>
	public double Threshold
	{
		get
		{
			lock (_gate)
			{
				return _threshold;
			}
		}
		set
		{
			lock (_gate)
			{
				_threshold = Math.Clamp(value, 0.001, 1);
			}
		}
	}

	/// <summary>Exposed so a test can assert the timing rules without waiting in real time.</summary>
	internal static TimeSpan HoldRequired => RequiredDuration;

	/// <summary>Exposed for the same reason.</summary>
	internal static TimeSpan CooldownLength => Cooldown;

	public void Dispose()
	{
		Stop();

		lock (_gate)
		{
			_disposed = true;
		}
	}
}
