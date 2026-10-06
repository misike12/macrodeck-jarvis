using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Input;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Tests for the openWakeWord front-end that do not need ONNX Runtime: the conversion that turns
/// microphone floats into the int16-scaled 16 kHz form the models expect.
/// <para>
/// The conversion is the one piece of the engine where a silent drift changes every score the model
/// produces, which is why it is pinned by itself rather than only through end-to-end scores.
/// </para>
/// </summary>
[TestFixture]
public class DownsampleTests
{
	[Test]
	public void A_48k_signal_is_resampled_to_16k()
	{
		// One second: 48000 samples in, 16000 out.
		var samples = new float[48_000];

		var result = Downsample.ToInt16Scale(samples, 48_000);

		Assert.That(result.Length, Is.EqualTo(16_000));
	}

	[Test]
	public void The_output_is_at_int16_scale_not_normalised()
	{
		// A full-scale sine must reach values near 32767 after conversion. A normalised float pipeline
		// (the probe's original mistake) scores a real phrase at 0.006, which is why this is pinned.
		var samples = new float[480];

		for (var index = 0; index < samples.Length; index++)
		{
			samples[index] = (float)Math.Sin(2 * Math.PI * 220 * index / 48_000.0);
		}

		var result = Downsample.ToInt16Scale(samples, 48_000);

		Assert.That(result.Select(value => Math.Abs(value)).Max(), Is.GreaterThan(30_000));
	}

	/// <summary>
	/// The scale multiplies by 32767 on both signs rather than keeping int16's asymmetric -32768: one
	/// least-significant bit is three thousandths of a percent of the range and means nothing to a model,
	/// while a special case for the negative extreme would be one more line to get wrong.
	/// </summary>
	[Test]
	public void Samples_beyond_full_scale_are_clamped()
	{
		var result = Downsample.ToInt16Scale([5f, -5f], 16_000);

		Assert.Multiple(() =>
		{
			Assert.That(result[0], Is.EqualTo(short.MaxValue));
			Assert.That(result[1], Is.EqualTo(-short.MaxValue));
		});
	}

	[Test]
	public void A_16k_signal_passes_through_unchanged()
	{
		var samples = new float[] { 1.5f, -0.5f, 0.25f };

		var result = Downsample.ToInt16Scale(samples, 16_000);

		Assert.That(
			result,
			Is.EqualTo(new float[] { short.MaxValue, -0.5f * short.MaxValue, 0.25f * short.MaxValue }));
	}

	[Test]
	public void Empty_input_produces_empty_output()
	{
		Assert.That(Downsample.ToInt16Scale([], 48_000), Is.Empty);
	}
}

/// <summary>
/// The keyword engine against the real pinned models, when they are installed. This is the check that
/// the port scores the phrase high and everything else low; without the models installed the fixture
/// reports inconclusive, because asserting the maths against nothing would be theatre.
/// </summary>
[TestFixture]
public class OpenWakeWordEngineLiveTests
{
	/// <summary>
	/// The temp-path runtime root the plugin uses wherever the host has not supplied a data directory.
	/// The tests do not download: they verify what a real install produces, and the download itself is
	/// covered by the live download fixture.
	/// </summary>
	private static string? ModelDirectory()
	{
		var candidate = Path.Combine(Path.GetTempPath(), "jarvis-runtime", "wakeword", "preprocessors");

		return Directory.Exists(candidate) ? candidate : null;
	}

	private static string? ClassifierPath()
	{
		var candidate = Path.Combine(
			Path.GetTempPath(), "jarvis-runtime", "wakeword", "hey-jarvis", "hey_jarvis_v0.1.onnx");

		return File.Exists(candidate) ? candidate : null;
	}

	private static OpenWakeWordEngine? NewEngine(out string reason)
	{
		reason = string.Empty;
		var directory = ModelDirectory();
		var classifier = ClassifierPath();

		if (directory is null || classifier is null)
		{
			reason = "the openWakeWord models are not installed";
			return null;
		}

		return new OpenWakeWordEngine(directory, classifier, RuntimeTestLog.Logger);
	}

	/// <summary>A 440 Hz tone at conversational loudness. A keyword model that scores this even 0.3 would interrupt music all day.</summary>
	private static float[] Tone(int hz, double seconds, float amplitude)
	{
		var samples = new float[(int)(48_000 * seconds)];

		for (var index = 0; index < samples.Length; index++)
		{
			samples[index] = amplitude * (float)Math.Sin(2 * Math.PI * hz * index / 48_000.0);
		}

		return samples;
	}

	[Test]
	public void The_engine_primes_after_enough_chunks_and_not_before()
	{
		if (NewEngine(out var reason) is not { } engine)
		{
			Assert.Ignore($"skipped: {reason}");
			return;
		}

		using (engine)
		{
			Assert.That(engine.IsPrimed, Is.False, "freshly built, the engine is still priming");

			var silence = new float[OpenWakeWordEngine.ChunkSamples * 48_000 / 16_000];

			var primed = false;

			for (var chunk = 0; chunk < 40; chunk++)
			{
				engine.Process(silence, 48_000);
				primed |= engine.IsPrimed;
			}

			Assert.That(primed, Is.True, "forty chunks of audio never primed the engine");
		}
	}

	[Test]
	public void Silence_scores_below_any_usable_threshold()
	{
		if (NewEngine(out var reason) is not { } engine)
		{
			Assert.Ignore($"skipped: {reason}");
			return;
		}

		using (engine)
		{
			var silence = new float[48_000 * 6];
			var highest = 0f;

			for (var offset = 0; offset + 4800 <= silence.Length; offset += 4800)
			{
				var score = engine.Process(silence[offset..(offset + 4800)], 48_000);

				if (score is { } value)
				{
					highest = Math.Max(highest, value);
				}
			}

			Assert.That(highest, Is.LessThan(0.3f), $"silence scored {highest:0.000}");
		}
	}

	[Test]
	public void Tones_and_noise_score_below_any_usable_threshold()
	{
		if (NewEngine(out var reason) is not { } engine)
		{
			Assert.Ignore($"skipped: {reason}");
			return;
		}

		using (engine)
		{
			foreach (var signal in new[] { Tone(440, 6, 0.4f), Tone(1000, 6, 0.4f), Noise(6, 0.3f) })
			{
				var highest = 0f;

				for (var offset = 0; offset + 4800 <= signal.Length; offset += 4800)
				{
					var score = engine.Process(signal[offset..(offset + 4800)], 48_000);

					if (score is { } value)
					{
						highest = Math.Max(highest, value);
					}
				}

				Assert.That(highest, Is.LessThan(0.3f), $"a non-speech signal scored {highest:0.000}");
			}
		}
	}

	[Test]
	public void A_reset_engine_behaves_like_a_fresh_one()
	{
		if (NewEngine(out var reason) is not { } engine)
		{
			Assert.Ignore($"skipped: {reason}");
			return;
		}

		using (engine)
		{
			var silence = new float[48_000];

			for (var packet = 0; packet < 100; packet++)
			{
				engine.Process(silence, 48_000);
			}

			engine.Reset();

			Assert.That(engine.IsPrimed, Is.False, "reset left the engine believing it had history");
		}
	}

	private static float[] Noise(double seconds, float amplitude)
	{
		var random = new Random(9);
		var samples = new float[(int)(48_000 * seconds)];

		for (var index = 0; index < samples.Length; index++)
		{
			samples[index] = amplitude * (random.NextSingle() * 2f - 1f);
		}

		return samples;
	}
}

/// <summary>
/// The detector's score path: fire after a run of confident chunks, not on one, and not while cooling
/// down. The feed is a stub, because what is under test here is the counting and the cooldown, not the
/// model - the model's own scores are asserted in the fixture above.
/// </summary>
[TestFixture]
public class WakeWordScorePathTests
{
	private static WakeWordDetector NewDetector() =>
		new(RuntimeTestLog.Logger, TimeSpan.FromSeconds(2)) { Enabled = true };

	[Test]
	public async Task A_run_of_confident_scores_fires_once_and_then_cools_down()
	{
		using var detector = NewDetector();
		detector.ScoreFeed = (_, _, _) => Task.FromResult<float?>(0.9f);

		var fired = 0;
		detector.Detected += () => fired++;

		for (var tick = 0; tick < 10; tick++)
		{
			detector.Buffer.Append(new float[4800]);
			await detector.OfferAsync(0.5, TestContext.CurrentContext.CancellationToken);
		}

		Assert.Multiple(() =>
		{
			Assert.That(fired, Is.EqualTo(1), "the run fired more than once");
		});
	}

	[Test]
	public async Task One_confident_chunk_does_not_fire()
	{
		using var detector = NewDetector();

		var scores = new Queue<float?>([0.9f, 0f, 0f, 0f, 0f, 0f]);
		detector.ScoreFeed = (_, _, _) => Task.FromResult(scores.Dequeue());

		var fired = 0;
		detector.Detected += () => fired++;

		for (var tick = 0; tick < 6; tick++)
		{
			detector.Buffer.Append(new float[4800]);
			await detector.OfferAsync(0.5, TestContext.CurrentContext.CancellationToken);
		}

		Assert.That(fired, Is.Zero, "a single confident chunk started a turn");
	}

	[Test]
	public async Task A_null_score_interrupts_a_run()
	{
		using var detector = NewDetector();

		// Two confident chunks, then a null (still priming), then two more. The run must restart at the
		// null: a gap means the chunks were not consecutive audio, which is exactly what patience is for,
		// so two confident chunks after the gap stay under the run of three.
		var scores = new Queue<float?>([0.9f, 0.9f, null, 0.9f, 0.9f, 0f]);
		detector.ScoreFeed = (_, _, _) => Task.FromResult(scores.Dequeue());

		var fired = 0;
		detector.Detected += () => fired++;

		for (var tick = 0; tick < 6; tick++)
		{
			detector.Buffer.Append(new float[4800]);
			await detector.OfferAsync(0.5, TestContext.CurrentContext.CancellationToken);
		}

		Assert.That(fired, Is.Zero, "a run interrupted by priming still fired");
	}

	[Test]
	public async Task A_disabled_detector_never_feeds()
	{
		using var detector = NewDetector();
		detector.Enabled = false;

		var feeds = 0;
		detector.ScoreFeed = (_, _, _) =>
		{
			feeds++;
			return Task.FromResult<float?>(0.9f);
		};

		for (var tick = 0; tick < 5; tick++)
		{
			detector.Buffer.Append(new float[4800]);
			await detector.OfferAsync(0.5, TestContext.CurrentContext.CancellationToken);
		}

		Assert.That(feeds, Is.Zero);
	}

	[Test]
	public async Task Without_a_score_feed_the_transcript_path_still_works()
	{
		// The transcript path is the fallback engine and remains selectable, so the detector must still
		// run it when no score feed is attached.
		using var detector = new WakeWordDetector(RuntimeTestLog.Logger, TimeSpan.FromSeconds(1))
		{
			Enabled = true,
			Sensitivity = 0.2,
		};

		detector.Buffer.Append(Samples.Speech());
		detector.Recognizer = (_, _) => Task.FromResult<string?>("hey jarvis");

		var fired = 0;
		detector.Detected += () => fired++;

		await detector.OfferAsync(0.9, TestContext.CurrentContext.CancellationToken);
		await Task.Delay(200, TestContext.CurrentContext.CancellationToken);
		await detector.OfferAsync(0.0, TestContext.CurrentContext.CancellationToken);

		Assert.That(fired, Is.EqualTo(1), "the transcript engine stopped working when the score path was added");
	}
}
