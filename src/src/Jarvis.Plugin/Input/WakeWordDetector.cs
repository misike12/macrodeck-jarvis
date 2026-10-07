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
}	/// <summary>
	/// The wake word trigger, with two selectable paths behind one event.
	/// <para>
	/// The score path runs the openWakeWord keyword model in-process: the engine's pump hands over one
	/// score per 80 ms chunk, and a run of confident chunks fires. The transcript path keeps the original
	/// design - the microphone's level decides that somebody has spoken, the audio that triggered it is
	/// transcribed, and the transcript is matched for the word. The engine setting picks the path; the
	/// cooldown and the <see cref="Detected"/> event are shared, so nothing downstream knows or cares
	/// which one ran.
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

	/// <summary>
	/// The level has to fall to this fraction of the sensitivity before an utterance counts as finished.
	/// <para>
	/// A separate, lower figure rather than the sensitivity itself, because the smoothed level sags between
	/// syllables and a pause inside one word would otherwise cut the utterance in half.
	/// </para>
	/// </summary>
	private const double ReleaseRatio = 0.6;

	/// <summary>
	/// Samples quieter than this are room tone for the purpose of finding where an utterance starts and
	/// ends. Well under speech, and only ever used to trim, never to decide whether anybody spoke.
	/// </summary>
	private const float SpeechFloor = 0.01f;

	/// <summary>An utterance shorter than this is a noise spike rather than a spoken word.</summary>
	private static readonly TimeSpan MinimumUtterance = TimeSpan.FromMilliseconds(120);

	/// <summary>
	/// A keyword score has to exceed this for a run of consecutive chunks before the wake word fires.
	/// <para>
	/// The model's score for the real phrase measured 0.999, and everything that was not the phrase -
	/// silence, tones, noise, speech-like babble - scored below 0.01, so 0.5 sits in the gap. The count is
	/// the openWakeWord patience behaviour: a single chunk over the line is a click or a syllable of an
	/// unrelated word, and requiring three keeps that from starting a turn.
	/// </para>
	/// </summary>
	public const double ScoreThreshold = 0.5;

	/// <summary>The number of consecutive over-threshold chunks a score run requires.</summary>
	public const int ScorePatience = 3;

	/// <summary>How long after a score fire the score path goes quiet. Mirrors the transcript cooldown.</summary>
	private static readonly TimeSpan ScoreCooldown = TimeSpan.FromSeconds(5);

	private readonly ILogger _logger;
	private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
	private int _busy;

	/// <summary>Consecutive chunks the score path has seen above the threshold, reset on any miss.</summary>
	private int _consecutiveScores;

	/// <summary>Whether an utterance is in progress, so its end is what triggers a recognition.</summary>
	private bool _speaking;

	/// <summary>When the current utterance began, used to ignore a blip too short to be a word.</summary>
	private DateTimeOffset _speechStartedAt;

	public WakeWordDetector(ILogger logger, TimeSpan? window = null)
	{
		_logger = logger.ForContext<WakeWordDetector>();
		Window = window ?? TimeSpan.FromSeconds(3);
		Buffer = new SampleRingBuffer(Window, MicrophoneMonitor.SampleRate);
	}

	/// <summary>How much audio is retained for inspection: long enough for the word, short enough not to be a recording.</summary>
	public TimeSpan Window { get; }

	/// <summary>
	/// The retained samples, oldest first.
	/// <para>
	/// Replaced rather than resized when the open device turns out to deliver a different rate than the one
	/// the buffer was sized for. A buffer holding 96 kHz audio while claiming 48 kHz would hand the recogniser
	/// half as much time as it thinks it has, which trims the front of the word off.
	/// </para>
	/// </summary>
	public SampleRingBuffer Buffer { get; private set; }

	/// <summary>
	/// Points the detector at a capture rate, rebuilding the buffer when it differs from the one in hand.
	/// Safe to call on every configuration: a device that stayed at the same rate keeps its audio.
	/// </summary>
	public void UseSampleRate(int sampleRate)
	{
		if (sampleRate <= 0 || sampleRate == Buffer.SampleRate)
		{
			return;
		}

		var existing = Buffer.TakeLast(Buffer.Count);
		Buffer = new SampleRingBuffer(Window, sampleRate);

		// Only meaningful going upwards; resampling retained audio is the recogniser's job, not this one's.
		if (existing.Length > 0)
		{
			Buffer.Append(existing);
		}
	}

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
	/// The transcript path's entry: the level decides that someone has spoken, the recogniser decides
	/// whether the word was in it. Called from the monitor's own publish tick, so nothing extra is
	/// scheduled and a silent microphone costs nothing.
	/// </summary>
	public async Task OfferAsync(double level, CancellationToken cancellationToken)
	{
		if (!Enabled || Recognizer is null || DateTimeOffset.UtcNow < _nextAllowed)
		{
			return;
		}

		await OfferTranscriptAsync(level, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// One keyword score for one 80 ms chunk, delivered by the engine's pump. Synchronous and cheap: the
	/// pump has already paid for the inference, so what is left is only the counting.
	/// <para>
	/// The rules are the openWakeWord patience behaviour with a cooldown bolted on. A single chunk over
	/// the threshold is a click or a syllable of an unrelated word, so a run of three fires; any chunk
	/// under it restarts the run; and after a fire the word goes quiet for a while, because the tail of
	/// the phrase is still in the model's context window and would otherwise re-detect itself.
	/// </para>
	/// </summary>
	public void OfferScore(float score)
	{
		if (!Enabled || DateTimeOffset.UtcNow < _nextAllowed)
		{
			return;
		}

		NoteLoudest(score);

		if (score >= ScoreThreshold)
		{
			_consecutiveScores++;

			if (_consecutiveScores >= ScorePatience)
			{
				_logger.Information(
					"Wake word scored {Score} over {Patience} chunks; firing.",
					score,
					_consecutiveScores);

				_consecutiveScores = 0;
				_nextAllowed = DateTimeOffset.UtcNow + ScoreCooldown;
				Detected?.Invoke();
			}
		}
		else
		{
			_consecutiveScores = 0;
		}
	}

	/// <summary>How often the detector says what it has been hearing.</summary>
	private static TimeSpan ReportInterval => TimeSpan.FromSeconds(30);

	/// <summary>What it has heard, and whether anything was heard at all.</summary>
	private float _loudest;

	private int _chunksHeard;

	private DateTimeOffset _nextReport = DateTimeOffset.MinValue;

	/// <summary>
	/// Reports what the model has actually been scoring, so silence is distinguishable from a dead pump.
	/// <para>
	/// Every other signal here is negative by construction: the wake word not firing looks identical whether
	/// the models are missing, the pump died, the microphone is muted, or the open device is not the one being
	/// spoken into. A run of scores near zero, and a stream of chunks, separates "listening and hearing
	/// nothing" from "not listening at all", which is the difference between a settings mistake and a bug.
	/// </para>
	/// </summary>
	private void NoteLoudest(float score)
	{
		_chunksHeard++;
		_loudest = Math.Max(_loudest, score);

		if (DateTimeOffset.UtcNow < _nextReport)
		{
			return;
		}

		_nextReport = DateTimeOffset.UtcNow + ReportInterval;

		if (_chunksHeard == 0)
		{
			_logger.Warning(
				"The wake word is enabled but no audio has reached it for {Seconds}s. "
				+ "Check that the configured microphone is the one being spoken into.",
				(int)ReportInterval.TotalSeconds);
		}
		else
		{
			_logger.Debug(
				"The wake word has scored {Chunks} chunks in {Seconds}s, loudest {Loudest:0.000}.",
				_chunksHeard,
				(int)ReportInterval.TotalSeconds,
				_loudest);
		}

		_chunksHeard = 0;
		_loudest = 0;
	}

		/// <summary>
	/// The transcript path, unchanged: the level decides that someone has spoken, the recogniser decides
	/// whether the word was in it. Selected by the engine setting being anything but the keyword spotter.
	/// </summary>
	private async Task OfferTranscriptAsync(double level, CancellationToken cancellationToken)
	{
		// The entry already gated on the recogniser being present, but a local copy is what proves it to
		// the compiler: the null-state does not follow a call across methods.
		var recogniser = Recognizer;

		if (recogniser is null)
		{
			return;
		}

		// Triggered on the end of speech, not the start of it.
		//
		// Recognising the moment the level crosses the threshold hands the recogniser an utterance that has
		// barely begun, surrounded by seconds of room tone, and a small model answers that with silence: the
		// wake word never fired once because it kept transcribing "[BLANK_AUDIO]" while the user spoke. The
		// word is in the buffer by the time the voice stops, so the end is both the only moment the whole
		// word exists and the moment the buffer is worth reading.
		if (level >= Sensitivity)
		{
			if (!_speaking)
			{
				_speaking = true;
				_speechStartedAt = DateTimeOffset.UtcNow;
			}

			return;
		}

		// Not yet quiet enough to count as finished, so still inside the same utterance.
		if (_speaking && level >= Sensitivity * ReleaseRatio)
		{
			return;
		}

		if (!_speaking)
		{
			return;
		}

		_speaking = false;

		// A tap or a door is not a word. Without this a single spike spends a recognition on nothing.
		if (DateTimeOffset.UtcNow - _speechStartedAt < MinimumUtterance)
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
			var samples = Trim(
				Buffer.TakeLast((int)(Window.TotalSeconds * Buffer.SampleRate)), Buffer.SampleRate);

			if (samples.Length == 0)
			{
				return;
			}

var heard = await recogniser(samples, cancellationToken).ConfigureAwait(false);

		if (heard is { } text && Mentions(text, Word))
		{
			_logger.Information("Wake word '{Word}' heard in: {Text}", Word, text);
			_nextAllowed = DateTimeOffset.UtcNow + Cooldown;
			Detected?.Invoke();
		}
		else
		{
			// Reported because the alternative is a wake word that silently never fires: a threshold crossed
			// and a recognition ran, so neither the trigger nor the recogniser is at fault, and without this
			// line there is nothing at all in the log to tell a missed word from a broken detector.
			_logger.Information(
				"Wake word check ran but did not match. Heard {Text} against {Word}.",
				heard ?? "(nothing)",
				Word);
		}
	}
	catch (OperationCanceledException)
	{
		throw;
	}
	catch (Exception exception) when (exception is not OutOfMemoryException)
	{
		// A failed check is not worth interrupting anything for; the wake word is a convenience. Reported at
		// information level for the same reason as the miss above: it is the only sign the check ever ran.
		_logger.Information(exception, "A wake word check failed.");
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
public static bool Mentions(string? heard, string? word)
  	{
  		// A recogniser that failed returns null, and "no transcript" must read as "not said" rather than
  		// throwing from a wake word check that is only a convenience.
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

		var window = needle.Length;

		// A third of the word, not a fifth. At a fifth, a six-letter word allowed one edit and rejected the
		// mishearings actually observed: "jarvez", "jarivs", "jardomies". The recogniser is a small model
		// transcribing one word out of a few seconds of audio, so it lands near the word rather than on it.
		// Still proportional, so a longer configured word is not held to a stricter absolute bar.
		var tolerance = Math.Max(1, needle.Length / 3);

		// Every position is tried, with a window exactly the length of the word. An earlier version used a
		// window two characters longer, which meant a window could only ever overlap the word by a couple of
		// letters, so "hey jarvis" and "jarvis, what time is it" both scored worse than the tolerance and the
		// wake word never fired for the phrasing people actually use.
		for (var start = 0; start + window <= haystack.Length; start++)
		{
			if (Distance(haystack.AsSpan(start, window), needle) <= tolerance)
			{
				return true;
			}
		}

		return MatchesLongerRun(haystack, needle, tolerance);
	}

	/// <summary>
	/// Catches the recogniser padding the word with extra sounds, which no fixed-length window can match.
	/// <para>
	/// "jardomies" is nine letters for a six-letter word, so every six-letter window of it overlaps the real
	/// word by only a few letters and scores worse than any tolerance that would still reject an unrelated
	/// phrase. Requiring the opening of the word to be recognisable, and then allowing the rest to run longer
	/// than expected, accepts that without opening the matcher up: "Paris" shares no opening with the word
	/// and so stays rejected.
	/// </para>
	/// </summary>
	private static bool MatchesLongerRun(string haystack, string needle, int tolerance)
	{
		var opening = Math.Min(4, needle.Length);
		var openingTolerance = 1;

		for (var start = 0; start + opening <= haystack.Length; start++)
		{
			if (Distance(haystack.AsSpan(start, opening), needle[..opening]) > openingTolerance)
			{
				continue;
			}

			// The opening is right, so this is the word with sounds bolted on. What is left of the transcript
			// from here is allowed to be as long as the word plus the padding a mishearing adds.
			var rest = haystack[start..];
			var longest = Math.Min(rest.Length, needle.Length + 3);

			if (Distance(rest.AsSpan(0, longest), needle) <= tolerance + opening)
			{
				return true;
			}
		}

		return false;
	}

/// <summary>
	/// Cuts the silence off both ends of a captured utterance, keeping a margin at each end.
	/// <para>
	/// The ring buffer holds the last few seconds so the word cannot be missed, which means it is mostly room
	/// tone. Handed that, a small recogniser pads its output with silence markers and invents words, so the
	/// audio is reduced to the part that is actually speech before it is recognised. The margin keeps the
	/// attack of the first phoneme, which is where a consonant lives and which trimming would otherwise eat.
	/// </para>
	/// </summary>
	internal static float[] Trim(float[] samples, int sampleRate)
	{
		var first = -1;
		var last = -1;

		for (var index = 0; index < samples.Length; index++)
		{
			if (Math.Abs(samples[index]) > SpeechFloor)
			{
				first = index;
				last = index;
			}
		}

		// Nothing above the floor: silence all the way through, so there is nothing to recognise.
		if (first < 0)
		{
			return [];
		}

		var margin = (int)(sampleRate * 0.15);
		var start = Math.Max(0, first - margin);

		return samples[start..Math.Min(samples.Length, last + margin)];
	}

	private static string Clean(string? text) =>
  		text is null
  			? string.Empty
  			: new string(text.ToLowerInvariant().Where(char.IsLetter).ToArray());

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