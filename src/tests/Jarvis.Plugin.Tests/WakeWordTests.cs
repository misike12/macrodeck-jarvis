using Jarvis.Plugin.Input;


using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

// Literal sample sequences are fixed data, so they are hoisted rather than allocated on every call.
internal static class Samples
{
	internal static readonly float[] Six = [1, 2, 3, 4, 5, 6];
	internal static readonly float[] Four = [1, 2, 3, 4];
	internal static readonly float[] FiveMore = [5, 6, 7, 8, 9];
	internal static readonly float[] Two = [1, 2];
	internal static readonly float[] LastThreeOfSix = [4, 5, 6];
	internal static readonly float[] LastFourOfNine = [6, 7, 8, 9];
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

		Assert.That(buffer.TakeLast(3), Is.EqualTo(Samples.LastThreeOfSix));
		Assert.That(buffer.TakeLast(6), Is.EqualTo(Samples.Six));
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

		detector.Buffer.Append(new float[48_000]);
		var checks = 0;
		detector.Recognizer = (_, _) =>
		{
			checks++;
			return Task.FromResult<string?>("yes jarvis here");
		};

		var fired = 0;
		detector.Detected += () => fired++;

		await detector.OfferAsync(0.9, TestContext.CurrentContext.CancellationToken);

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

		detector.Buffer.Append(new float[48_000]);

		var concurrent = 0;
		var peak = 0;
		var gate = new TaskCompletionSource();

		detector.Recognizer = async (_, _) =>
		{
			var now = Interlocked.Increment(ref concurrent);
			InterlockedMax(ref peak, now);

			await gate.Task;
			Interlocked.Decrement(ref concurrent);

			return null;
		};

		for (var tick = 0; tick < 10; tick++)
		{
			await detector.OfferAsync(0.9, TestContext.CurrentContext.CancellationToken);
		}

		gate.SetResult();
		Assert.That(peak, Is.EqualTo(1), "more than one recognition ran at once");
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