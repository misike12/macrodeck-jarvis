using Jarvis.Plugin.Input;


using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

// Literal sample sequences are fixed data, so they are hoisted rather than allocated on every call.
internal static class Samples
{
	internal static readonly float[] Six = [1, 2, 3, 4, 5, 6];
	internal static readonly float[] LastFourOfSix = [3, 4, 5, 6];
	internal static readonly float[] Four = [1, 2, 3, 4];
	internal static readonly float[] FiveMore = [5, 6, 7, 8, 9];
	internal static readonly float[] Two = [1, 2];
	internal static readonly float[] LastThreeOfSix = [4, 5, 6];
internal static readonly float[] LastFourOfNine = [6, 7, 8, 9];

	/// <summary>
	/// One second of a voice-like signal, loud enough to be recognised.
	/// <para>
	/// Not silence: an utterance is trimmed to the part above the speech floor before it is recognised, so a
	/// buffer of zeros is correctly rejected as nothing to hear and a test using one would wait forever for a
	/// recognition that can never happen.
	/// </para>
	/// </summary>
	internal static float[] Speech(int sampleRate = 48_000)
	{
		var samples = new float[sampleRate];

		for (var index = 0; index < samples.Length; index++)
		{
			samples[index] = (float)(0.4 * Math.Sin(2 * Math.PI * 220 * index / sampleRate));
		}

		return samples;
	}
}

[TestFixture]
public class WakeWordTests
{
	/// <summary>
	/// A ring buffer that silently loses or reorders samples would make the wake word fire on the wrong
	/// audio, which is worse than not firing at all. Both properties are checked against a known sequence.
	/// </summary>
	[Test]
	public void A_ring_buffer_returns_the_most_recent_samples_in_order()
	{
		var buffer = new SampleRingBuffer(TimeSpan.FromMilliseconds(4), 1000);

buffer.Append(Samples.Six);

		// Four milliseconds at 1000 Hz is a capacity of four samples, so six appended leaves four. Asking
		// for six back would be asking the buffer to have kept more than it was built to hold.
		Assert.That(buffer.Capacity, Is.EqualTo(4));
		Assert.That(buffer.TakeLast(3), Is.EqualTo(Samples.LastThreeOfSix));
		Assert.That(buffer.TakeLast(6), Is.EqualTo(Samples.LastFourOfSix));
	}

	/// <summary>The buffer must overwrite oldest-first and never grow, or a long session leaks.</summary>
	[Test]
	public void A_ring_buffer_overwrites_the_oldest_samples_and_stays_bounded()
	{
		var buffer = new SampleRingBuffer(TimeSpan.FromMilliseconds(4), 1000);

		buffer.Append(Samples.Four);
		buffer.Append(Samples.FiveMore);

		Assert.Multiple(() =>
		{
			Assert.That(buffer.Count, Is.EqualTo(4));
			Assert.That(buffer.TakeLast(4), Is.EqualTo(Samples.LastFourOfNine));
		});
	}

	[Test]
	public void Asking_for_more_than_is_held_returns_what_exists()
	{
		var buffer = new SampleRingBuffer(TimeSpan.FromSeconds(1), 1000);
		buffer.Append(Samples.Two);

		Assert.That(buffer.TakeLast(500), Is.EqualTo(Samples.Two));
	}

	/// <summary>
	/// The recogniser reliably drops and adds the odd letter, so an exact comparison would make the wake
	/// word useless. These are the misspellings actually observed in practice.
	/// </summary>
	[TestCase("jarvis", true)]
	[TestCase("Jarvis!", true)]
	[TestCase("hey jarvis", true)]
	[TestCase("jarvis, what time is it", true)]
[TestCase("jarvisz", true)]
	[TestCase("jarves", true)]
	[TestCase("", false)]
	[TestCase(null, false)]
	[TestCase("what is the weather", false)]
	[TestCase("good evening to you all", false)]
	public void The_word_is_matched_loosely_but_not_vaguely(string? heard, bool expected)
	{
		Assert.That(WakeWordDetector.Mentions(heard!, "jarvis"), Is.EqualTo(expected), $"'{heard}'");
	}

	/// <summary>
	/// What the recogniser actually returned when the user said the word, taken from the host log.
	/// <para>
	/// Every one of these is the word heard correctly and mangled by a small model transcribing a single
	/// word out of seconds of room tone. A fifth-of-the-word tolerance allowed one edit and rejected all of
	/// them, which is why the wake word was reported as doing nothing while the log filled with the word
	/// itself, misspelled. They are the cases that have to work.
	/// </para>
	/// </summary>
	[TestCase("And jardomies.")]
	[TestCase("jardomies")]
	[TestCase("jarvez")]
	[TestCase("jarivs")]
	[TestCase("Jarvez.")]
	public void A_word_the_recogniser_mangled_is_still_the_word(string heard)
	{
		Assert.That(WakeWordDetector.Mentions(heard, "jarvis"), Is.True, $"'{heard}'");
	}

	/// <summary>
	/// The transcripts the log showed that are not a mangled spelling of the word.
	/// <para>
	/// These are what the recogniser returned when the microphone had nothing usable in it: the room was
	/// silent, or a keyboard was being hit. "[BLANK_AUDIO]" and "(keyboard clicking)" are the recogniser
	/// describing its own input, so they must not match, and neither must an unrelated word.
	/// </para>
	/// </summary>
	[TestCase("[BLANK_AUDIO]")]
	[TestCase("(keyboard clicking)")]
	[TestCase("what is the weather")]
	[TestCase("good evening to you all")]
	public void Something_that_is_not_the_word_is_still_refused(string heard)
	{
		Assert.That(WakeWordDetector.Mentions(heard, "jarvis"), Is.False, $"'{heard}'");
	}

	/// <summary>
	/// A word the recogniser returned instead of the one spoken, which is not a near-miss and must not match.
	/// <para>
	/// The user said "jarvis" and the log recorded "Paris". Loosening the matcher until that matched would
	/// mean accepting any short phrase, which fires the wake word at ordinary speech. This is a failure of
	/// what the recogniser heard, not of how the two are compared, so it is pinned here to stop the
	/// tolerance being widened to accommodate it: the fix belongs in the audio handed to the recogniser.
	/// </para>
	/// </summary>
	[Test]
	public void A_different_word_the_recogniser_invented_is_not_the_wake_word()
	{
		Assert.That(
			WakeWordDetector.Mentions("Paris", "jarvis"),
			Is.False,
			"the recogniser heard a different word, and matching it would fire on unrelated speech");
	}

	[Test]
	public void An_empty_word_never_matches()
	{
		Assert.That(WakeWordDetector.Mentions("anything at all", "   "), Is.False);
	}

	/// <summary>
	/// A level below the threshold must never reach the recogniser. Without this the recogniser runs
	/// constantly on room tone, which is the difference between a wake word and a battery drain.
	/// </summary>
	[Test]
	public async Task A_quiet_microphone_never_triggers_a_check()
	{
		using var detector = new WakeWordDetector(RuntimeTestLog.Logger) { Enabled = true, Sensitivity = 0.5 };
		var checks = 0;
		detector.Recognizer = (_, _) =>
		{
			checks++;
			return Task.FromResult<string?>("jarvis");
		};

		for (var tick = 0; tick < 20; tick++)
		{
			await detector.OfferAsync(0.01, TestContext.CurrentContext.CancellationToken);
		}

		Assert.That(checks, Is.Zero, "a silent microphone still ran the recogniser");
	}

	[Test]
	public async Task A_loud_microphone_runs_one_check_and_fires_when_the_word_is_there()
	{
		using var detector = new WakeWordDetector(RuntimeTestLog.Logger, TimeSpan.FromSeconds(1))
		{
			Enabled = true,
			Sensitivity = 0.2,
			Word = "jarvis",
		};

		detector.Buffer.Append(Samples.Speech());
		var checks = 0;
		detector.Recognizer = (_, _) =>
		{
			checks++;
			return Task.FromResult<string?>("yes jarvis here");
		};

		var fired = 0;
		detector.Detected += _ => fired++;

// An utterance is speech followed by a pause, not a single loud sample: the check runs when the voice
		// stops, because that is the first moment the whole word is in the buffer to be recognised. The pause
		// has to outlast a real utterance, since anything shorter is treated as a noise spike.
		await detector.OfferAsync(0.9, TestContext.CurrentContext.CancellationToken);
		await Task.Delay(200, TestContext.CurrentContext.CancellationToken);
		await detector.OfferAsync(0.0, TestContext.CurrentContext.CancellationToken);

		Assert.Multiple(() =>
		{
			Assert.That(checks, Is.EqualTo(1));
			Assert.That(fired, Is.EqualTo(1));
		});
	}

	/// <summary>
	/// A level that stays high across several ticks is one utterance, not several. Without this the word
	/// would be checked repeatedly against the same audio.
	/// </summary>
	[Test]
	public async Task A_level_staying_high_does_not_start_overlapping_checks()
	{
		using var detector = new WakeWordDetector(RuntimeTestLog.Logger, TimeSpan.FromSeconds(1))
		{
			Enabled = true,
			Sensitivity = 0.2,
		};

		detector.Buffer.Append(Samples.Speech());

var concurrent = 0;
		var peak = 0;
		var gate = new TaskCompletionSource();
		var started = new TaskCompletionSource();

		detector.Recognizer = async (_, _) =>
		{
			var now = Interlocked.Increment(ref concurrent);
			InterlockedMax(ref peak, now);

			// Signalled before awaiting the gate, so the loop below can tell "a check is running" apart
			// from "a check has not started yet".
			started.TrySetResult();

			await gate.Task;
			Interlocked.Decrement(ref concurrent);

			return null;
		};

		// Speech, then the pause that ends it: the second call is the one that starts the recognition. It is
		// started and not awaited, because OfferAsync awaits the recogniser, the recogniser is waiting for
		// the gate, and the gate is opened only after the loop, so awaiting it here would deadlock.
		await detector.OfferAsync(0.9, TestContext.CurrentContext.CancellationToken);
		await Task.Delay(200, TestContext.CurrentContext.CancellationToken);
		var first = detector.OfferAsync(0.0, TestContext.CurrentContext.CancellationToken);
		await started.Task;

		// More speech while that check is still running, with no pause to end it: one continuous utterance
		// must not start a second recognition.
		for (var tick = 1; tick < 10; tick++)
		{
			await detector.OfferAsync(0.9, TestContext.CurrentContext.CancellationToken);
		}

		gate.SetResult();
		await first;

		Assert.That(peak, Is.EqualTo(1), "more than one recognition ran at once");
	}

	/// <summary>
	/// The words this build pins a keyword model for, in the shapes a person types them.
	/// <para>
	/// A miss here is not a cosmetic problem: the integration reads this to decide whether the score path can
	/// hear the configured word at all, and hearing "hey jarvis" back after typing "jarvis" would be a wake
	/// word that responds to a phrase nobody said.
	/// </para>
	/// </summary>
	[TestCase("jarvis", true)]
	[TestCase("Jarvis", true)]
	[TestCase("hey jarvis", true)]
	[TestCase("Hey, Jarvis!", true)]
	[TestCase("  JARVIS  ", true)]
	[TestCase("alexa", true)]
	[TestCase("mycroft", true)]
	[TestCase("hey mycroft", true)]
	[TestCase("computer", false)]

	// Digits and punctuation carry nothing here, so "jarvis2" is "jarvis". A model that could hear the
	// digit would need a training sample of it, and none of the three pinned models has one.
	[TestCase("jarvis2", true)]
	[TestCase("", false)]
	public void A_pinned_model_is_found_by_the_word_as_it_is_typed(string word, bool expected)
	{
		Assert.That(OpenWakeWordEngineHost.HasModelFor(word), Is.EqualTo(expected), $"'{word}'");
	}

	/// <summary>
	/// A word with no model must name the default model and say so, rather than pretending. The caller uses
	/// the flag to fall back to the transcript engine, which is what makes a custom wake word work.
	/// </summary>
	[Test]
	public void A_word_with_no_model_is_reported_rather_than_silently_replaced()
	{
		var (asset, exact) = OpenWakeWordEngineHost.ModelAssetFor("computer");

		Assert.Multiple(() =>
		{
			Assert.That(exact, Is.False, "a custom word was reported as having a model");
			Assert.That(asset, Is.Not.Empty, "the fallback left no model to load");
		});
	}

	/// <summary>
	/// A custom wake word has to be matchable by the loose matcher, because a custom word is by definition
	/// served by the transcript engine. The observed mishearings for "jarvis" have to hold for any word.
	/// </summary>
	[TestCase("computer, what time is it", true)]
	[TestCase("computor", true)]
	[TestCase("what is the weather", false)]
	[TestCase("Paris", false)]
	public void A_custom_wake_word_is_matched_as_loosely_as_a_pinned_one(string heard, bool expected)
	{
		Assert.That(WakeWordDetector.Mentions(heard, "computer"), Is.EqualTo(expected), $"'{heard}'");
	}

	private static void InterlockedMax(ref int target, int value)
	{
		int current;

		while (value > (current = Volatile.Read(ref target)))
		{
			if (Interlocked.CompareExchange(ref target, value, current) == current)
			{
				return;
			}
		}
	}
}