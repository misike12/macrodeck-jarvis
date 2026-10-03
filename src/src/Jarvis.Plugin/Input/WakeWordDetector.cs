using System.Runtime.Versioning;
using Jarvis.Plugin.Audio;
using Serilog;

namespace Jarvis.Plugin.Input;

/// <summary>
/// A bounded rolling buffer of microphone samples, so a keyphrase that has just been spoken can still be
/// recovered after the fact.
/// <para>
/// The monitor already receives every sample and throws them away. Copying them into a ring buffer costs
/// almost nothing and is what makes an offline wake word possible without opening a second capture device:
/// by the time the detector decides somebody has spoken, the audio of the word itself is already gone from
/// a pull model and only a push buffer still has it.
/// </para>
/// <para>
/// The buffer is fixed size and overwrites oldest-first, so a long session cannot make it grow.
/// </para>
/// </summary>
public sealed class SampleRingBuffer
{
	private readonly float[] _buffer;
	private readonly Lock _gate = new();
	private int _start;
	private int _count;

	public SampleRingBuffer(TimeSpan capacity, int sampleRate)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, TimeSpan.Zero);

		SampleRate = sampleRate;
		Capacity = Math.Max(1, (int)(capacity.TotalSeconds * sampleRate));
		_buffer = new float[Capacity];
	}

	public int SampleRate { get; }

	public int Capacity { get; }

	/// <summary>How many samples are held, never more than the capacity.</summary>
	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _count;
			}
		}
	}

	public void Append(ReadOnlySpan<float> samples)
	{
		lock (_gate)
		{
			foreach (var sample in samples)
			{
				_buffer[_start] = sample;
				_start = (_start + 1) % Capacity;

				if (_count < Capacity)
				{
					_count++;
				}
			}
		}
	}

	/// <summary>
	/// The most recent samples, oldest first. A ring buffer has to be unwound before it can be read
	/// linearly, which is why this is a method rather than a span.
	/// </summary>
	public float[] TakeLast(int count)
	{
		lock (_gate)
		{
			var taken = Math.Min(count, _count);

			if (taken == 0)
			{
				return [];
			}

			var result = new float[taken];
			var oldest = (_start - taken + Capacity) % Capacity;

			for (var index = 0; index < taken; index++)
			{
				result[index] = _buffer[(oldest + index) % Capacity];
			}

			return result;
		}
	}

	public void Clear()
	{
		lock (_gate)
		{
			_start = 0;
			_count = 0;
		}
	}
}

/// <summary>
/// An offline wake word, with no model file and no account.
/// <para>
/// Rather than shipping a keyword-spotting model, this reuses the speech recognition already installed:
/// the microphone's own level decides that somebody has started speaking, and the audio that triggered it
/// is transcribed and checked for the word. The wake word is therefore exactly as good as the recogniser
/// and exactly as offline, at the cost of a recognition pass per utterance, which is why the trigger is
/// deliberately conservative.
/// </para>
/// <para>
/// The trade is worth recording. A dedicated model would be faster and would not need a recogniser at all,
/// but Porcupine needs an account and NanoWakeWord needs an ONNX runtime dependency. Neither was worth it
/// for a feature that already works offline with what is installed.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WakeWordDetector : IDisposable
{
	/// <summary>
	/// How long after a detection the word is ignored. Without this the word re-detects itself, because the
	/// tail of "jarvis" is often still in the buffer when the next pass runs.
	/// </summary>
	private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

	private readonly ILogger _logger;
	private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
	private int _busy;

	public WakeWordDetector(ILogger logger, TimeSpan? window = null)
	{
		_logger = logger.ForContext<WakeWordDetector>();
		Window = window ?? TimeSpan.FromSeconds(3);
		Buffer = new SampleRingBuffer(Window, MicrophoneMonitor.SampleRate);
	}

	/// <summary>How much audio is retained for inspection: long enough for the word, short enough not to be a recording.</summary>
	public TimeSpan Window { get; }

	/// <summary>The retained samples, oldest first.</summary>
	public SampleRingBuffer Buffer { get; }

	public string Word { get; set; } = "jarvis";

	public double Sensitivity { get; set; } = 0.06;

	public bool Enabled { get; set; }

	/// <summary>Raised when the wake word is recognised.</summary>
	public event Action? Detected;

	/// <summary>
	/// Runs the actual check. Supplied by the owner rather than constructed here, so this class has no
	/// opinion about which recogniser is configured and can be exercised without one.
	/// </summary>
	public Func<float[], CancellationToken, Task<string?>>? Recognizer { get; set; }

	/// <summary>
	/// Offers the current level to the detector. Called from the monitor's own publish tick, so nothing
	/// extra is scheduled and a silent microphone costs nothing.
	/// </summary>
	public async Task OfferAsync(double level, CancellationToken cancellationToken)
	{
		if (!Enabled || Recognizer is null || level < Sensitivity || DateTimeOffset.UtcNow < _nextAllowed)
		{
			return;
		}

		// One check at a time. A level that stays above the threshold across several ticks must not start
		// several recognitions of the same utterance.
		if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
		{
			return;
		}

		try
		{
			var samples = Buffer.TakeLast((int)(Window.TotalSeconds * Buffer.SampleRate));

			if (samples.Length == 0)
			{
				return;
			}

			if (await Recognizer(samples, cancellationToken).ConfigureAwait(false) is { } text
				&& Mentions(text, Word))
			{
				_logger.Information("Wake word '{Word}' heard in: {Text}", Word, text);
				_nextAllowed = DateTimeOffset.UtcNow + Cooldown;
				Detected?.Invoke();
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			// A failed check is not worth interrupting anything for; the wake word is a convenience.
			_logger.Debug(exception, "A wake word check failed.");
		}
		finally
		{
			Interlocked.Exchange(ref _busy, 0);
		}
	}

	/// <summary>
	/// Matches the word loosely, because a recogniser reliably drops or adds the odd letter. Comparing a
	/// sliding window with an edit distance tolerates "jarvisz" and "jarves" without matching an unrelated
	/// phrase that happens to contain the letters.
	/// </summary>
	public static bool Mentions(string heard, string word)
	{
		var needle = Clean(word);

		if (needle.Length == 0)
		{
			return false;
		}

		var haystack = Clean(heard);

		if (haystack.Length == 0)
		{
			return false;
		}

		var window = Math.Min(haystack.Length, needle.Length + 2);
		var tolerance = Math.Max(1, needle.Length / 5);

		for (var start = 0; start + window <= haystack.Length; start++)
		{
			if (Distance(haystack.AsSpan(start, window), needle) <= tolerance)
			{
				return true;
			}
		}

		return false;
	}

	private static string Clean(string text) =>
		new(text.ToLowerInvariant().Where(char.IsLetter).ToArray());

	/// <summary>Levenshtein distance over the window, which is short enough that the cost does not matter.</summary>
	private static int Distance(ReadOnlySpan<char> left, string right)
	{
		var previous = new int[right.Length + 1];
		var current = new int[right.Length + 1];

		for (var index = 0; index <= right.Length; index++)
		{
			previous[index] = index;
		}

		for (var row = 1; row <= left.Length; row++)
		{
			current[0] = row;

			for (var column = 1; column <= right.Length; column++)
			{
				var cost = left[row - 1] == right[column - 1] ? 0 : 1;
				current[column] = Math.Min(
					Math.Min(current[column - 1] + 1, previous[column] + 1),
					previous[column - 1] + cost);
			}

			(previous, current) = (current, previous);
		}

		return previous[right.Length];
	}

	public void Dispose()
	{
		Enabled = false;
		GC.SuppressFinalize(this);
	}
}