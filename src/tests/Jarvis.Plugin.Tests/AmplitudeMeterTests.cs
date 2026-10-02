using Jarvis.Plugin.Audio;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

[TestFixture]
public class AmplitudeMeterTests
{
	[Test]
	public void Silence_reads_as_zero()
	{
		var meter = new AmplitudeMeter();
		meter.Accumulate(new float[480]);

		Assert.That(meter.Level, Is.LessThan(0.001));
	}

	[Test]
	public void A_full_scale_tone_reaches_the_top()
	{
		var meter = new AmplitudeMeter();
		var samples = new float[4096];
		Array.Fill(samples, 1f);

		for (var pass = 0; pass < 40; pass++)
		{
			meter.Accumulate(samples);
		}

		Assert.That(meter.Level, Is.GreaterThan(0.9));
	}

	[Test]
	public void A_quiet_voice_is_expanded_rather_than_lost()
	{
		// A speaking voice sits far below full scale. Without the expansion curve a normal voice would
		// map to a fifth of the orb's radius and the reaction would be invisible.
		var meter = new AmplitudeMeter();
		var samples = new float[4096];
		Array.Fill(samples, 0.05f);

		for (var pass = 0; pass < 40; pass++)
		{
			meter.Accumulate(samples);
		}

		Assert.That(meter.Level, Is.GreaterThan(0.2), "A quiet voice must still move the orb.");
		Assert.That(meter.Level, Is.LessThan(1.0));
	}

	[Test]
	public void The_level_is_always_within_range()
	{
		var meter = new AmplitudeMeter();
		var random = new Random(1234);

		for (var pass = 0; pass < 200; pass++)
		{
			var samples = new float[512];

			for (var index = 0; index < samples.Length; index++)
			{
				samples[index] = (float)((random.NextDouble() * 2.0) - 1.0) * (float)random.NextDouble();
			}

			var level = meter.Accumulate(samples);

			Assert.That(level, Is.InRange(0.0, 1.0));
		}
	}

	[Test]
	public void The_attack_is_faster_than_the_release()
	{
		var meter = new AmplitudeMeter();
		var loud = new float[1024];
		var quiet = new float[1024];
		Array.Fill(loud, 1f);

		meter.Accumulate(loud);
		var afterAttack = meter.Level;

		for (var pass = 0; pass < 60; pass++)
		{
			meter.Accumulate(loud);
		}

		var loudLevel = meter.Level;

		for (var pass = 0; pass < 5; pass++)
		{
			meter.Accumulate(quiet);
		}

		var earlyRelease = loudLevel - meter.Level;

		for (var pass = 0; pass < 60; pass++)
		{
			meter.Accumulate(quiet);
		}

		var totalRelease = loudLevel - meter.Level;

		Assert.That(afterAttack, Is.GreaterThan(0.1), "Speech onset should be visible at once.");
		Assert.That(earlyRelease, Is.GreaterThan(0.0));
		Assert.That(totalRelease, Is.GreaterThan(earlyRelease), "Release should decay over several blocks, not one.");
	}

	[Test]
	public void Peak_tracks_the_largest_sample_in_the_block()
	{
		var meter = new AmplitudeMeter();
		var samples = new float[] { 0.1f, -0.8f, 0.2f, 0.05f };

		meter.Accumulate(samples);

		Assert.That(meter.Peak, Is.EqualTo(0.8f).Within(0.0001));

		meter.Reset();

		Assert.That(meter.Peak, Is.Zero);
		Assert.That(meter.Level, Is.Zero);
	}

	[Test]
	public void The_accumulator_is_cleared_between_blocks()
	{
		// A running average over the whole session would never settle, so the figure has to be per block.
		var meter = new AmplitudeMeter();
		var loud = new float[512];
		var quiet = new float[512];
		Array.Fill(loud, 1f);

		meter.Accumulate(loud);
		meter.Accumulate(loud);

		for (var pass = 0; pass < 80; pass++)
		{
			meter.Accumulate(quiet);
		}

		Assert.That(meter.Level, Is.LessThan(0.05), "The level must decay once the signal stops.");
	}

	[Test]
	public void An_empty_block_leaves_the_level_alone()
	{
		var meter = new AmplitudeMeter();
		var samples = new float[256];
		Array.Fill(samples, 0.5f);
		meter.Accumulate(samples);

		var before = meter.Level;
		var after = meter.Accumulate(ReadOnlySpan<float>.Empty);

		Assert.That(after, Is.EqualTo(before));
	}
}