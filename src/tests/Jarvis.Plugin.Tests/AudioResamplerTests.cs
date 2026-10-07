using System.Runtime.Versioning;
using Jarvis.Plugin.Audio;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Resampling capture audio to the rate the speech models want.
/// <para>
/// This is the step that decides whether a high-rate microphone works at all. Linear interpolation alone
/// decimates by aliasing: everything above the new halfway frequency folds back down and lands inside the
/// speech band, where a small recogniser reads it as words that were never said. Going from a 192 kHz
/// interface to 16 kHz throws away eleven samples in every twelve, so this is the ordinary case on studio
/// hardware rather than an edge one, and it is silent: the audio still looks like speech on a meter.
/// </para>
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class AudioResamplerTests
{
	/// <summary>
	/// How far the out-of-band content has to sit below what went in. Twenty-eight decibels is a thirty-fold
	/// reduction, which is what a speech model treats as noise rather than as a word, and it is deliberately
	/// not set to the exact figure the filter happens to reach: a bar pinned to the current number fails when
	/// the filter is improved and passes when it is broken.
	/// </summary>
	private const double StopbandMarginDb = -28;

	private static float[] Tone(int hz, int rate, double seconds, float amplitude = 0.5f)
	{
		var samples = new float[(int)(rate * seconds)];

		for (var index = 0; index < samples.Length; index++)
		{
			samples[index] = amplitude * (float)Math.Sin(2 * Math.PI * hz * index / rate);
		}

		return samples;
	}

	private static float[] White(int rate)
	{
		var noise = new float[rate];
		var random = new Random(31);

		for (var index = 0; index < noise.Length; index++)
		{
			noise[index] = 0.4f * (random.NextSingle() * 2f - 1f);
		}

		return noise;
	}

	/// <summary>Root mean square level, which is what a level meter shows and what the models are sensitive to.</summary>
	private static double Amplitude(float[] samples)
	{
		if (samples.Length == 0)
		{
			return 0;
		}

		var total = 0.0;

		foreach (var sample in samples)
		{
			total += sample * sample;
		}

		return Math.Sqrt(total / samples.Length);
	}

	/// <summary>Energy in one frequency bin, read with a Goertzel filter rather than a whole transform.</summary>
	private static double EnergyAt(float[] samples, int rate, int hz)
	{
		var k = 2 * Math.Cos(2 * Math.PI * hz / rate);
		var previous = 0.0;
		var previousTwo = 0.0;

		foreach (var sample in samples)
		{
			var current = sample + (k * previous) - previousTwo;
			previousTwo = previous;
			previous = current;
		}

		return Math.Sqrt(Math.Max(0, (previousTwo * previousTwo) + (previous * previous) - (k * previous * previousTwo))) / samples.Length;
	}

	/// <summary>Relative level in decibels, with a floor so near-silence does not read as an infinite margin.</summary>
	private static double Decibels(double measured, double reference) =>
		20 * Math.Log10(Math.Max(measured, 1e-12) / Math.Max(reference, 1e-12));

	/// <summary>
	/// The whole point. A tone above the target's Nyquist has to be removed rather than folded down into the
	/// speech band. The measure is the level of the result against the level of what went in: a correctly
	/// filtered 20 kHz tone comes out close to silence, and one that folded down comes out at the same level
	/// as a spoken word, which a small recogniser then transcribes with complete confidence.
	/// </summary>
	[TestCase(44_100)]
	[TestCase(48_000)]
	[TestCase(96_000)]
	[TestCase(192_000)]
	public void A_tone_above_the_target_rate_is_removed_rather_than_folded_down(int sourceRate)
	{
		// 20 kHz is four kilohertz plus exactly one output period, so it lands on 4 kHz if it folds at all,
		// and it clears every one of these rates' own Nyquist limits.
		var samples = Tone(20_000, sourceRate, 1.0);

		var resampled = AudioResampler.To(samples, sourceRate, AudioResampler.SpeechRate);

		Assert.That(
			Decibels(Amplitude(resampled), Amplitude(samples)),
			Is.LessThan(StopbandMarginDb),
			$"a 20 kHz tone from {sourceRate} Hz arrived at {Decibels(Amplitude(resampled), Amplitude(samples)):F1} dB");
	}

	/// <summary>
	/// The comparison that says the filter is doing something. The same signal through plain linear
	/// interpolation arrives at full level, so this is a test against the behaviour that was replaced rather
	/// than against a number chosen to pass.
	/// </summary>
	[Test]
	public void Linear_interpolation_would_have_left_the_alias_in()
	{
		const int SourceRate = 192_000;
		const int TargetRate = 16_000;

		var samples = Tone(20_000, SourceRate, 1.0);

		var naive = LinearInterpolation(samples, SourceRate, TargetRate);
		var filtered = AudioResampler.To(samples, SourceRate, TargetRate);

		Assert.Multiple(() =>
		{
			Assert.That(
				Decibels(Amplitude(naive), Amplitude(samples)),
				Is.GreaterThan(-3),
				"the old path did not alias, so this proves nothing");

			Assert.That(
				Decibels(Amplitude(filtered), Amplitude(samples)),
				Is.LessThan(StopbandMarginDb),
				"the filter left the alias at full level");
		});
	}

	/// <summary>
	/// A tone inside the speech band has to come out at the level it went in at, at every rate. This is the
	/// half that catches a filter which removes everything: a quiet answer is scored as silence just as
	/// confidently as a loud wrong one.
	/// </summary>
	[TestCase(8_000)]
	[TestCase(16_000)]
	[TestCase(22_050)]
	[TestCase(24_000)]
	[TestCase(32_000)]
	[TestCase(44_100)]
	[TestCase(48_000)]
	[TestCase(88_200)]
	[TestCase(96_000)]
	[TestCase(192_000)]
	public void A_tone_in_the_speech_band_keeps_its_level_at_every_rate(int sourceRate)
	{
		var samples = Tone(1_000, sourceRate, 1.0);

		var resampled = AudioResampler.To(samples, sourceRate, AudioResampler.SpeechRate);

		Assert.Multiple(() =>
		{
			Assert.That(
				resampled.Length,
				Is.EqualTo(AudioResampler.SpeechRate).Within(4),
				$"{sourceRate} Hz produced {resampled.Length} samples for one second");

			Assert.That(
				Decibels(Amplitude(resampled), Amplitude(samples)),
				Is.InRange(-6, 1),
				$"a 1 kHz tone from {sourceRate} Hz came out at {Decibels(Amplitude(resampled), Amplitude(samples)):F1} dB");
		});
	}

	/// <summary>
	/// The top of the band has to be quieter than the middle of it, which is what white noise makes visible.
	/// Without a filter a device's noise floor arrives as hiss spread across the whole speech band, which is
	/// what a low quality microphone sounds like to a model however good the microphone is.
	/// </summary>
	[TestCase(48_000)]
	[TestCase(96_000)]
	[TestCase(192_000)]
	public void The_top_of_the_band_is_quiet_on_noise(int sourceRate)
	{
		var resampled = AudioResampler.To(White(sourceRate), sourceRate, AudioResampler.SpeechRate);

		var middle = EnergyAt(resampled, AudioResampler.SpeechRate, 1_000);
		var top = EnergyAt(resampled, AudioResampler.SpeechRate, 7_500);

		Assert.That(
			top,
			Is.LessThan(middle * 0.5),
			$"noise from {sourceRate} Hz came out at {top:0.00000} at the top of the band against {middle:0.00000} in the middle");
	}

	/// <summary>The passband has to cover the frequencies a voice actually occupies.</summary>
	[TestCase(200)]
	[TestCase(500)]
	[TestCase(1_000)]
	[TestCase(2_000)]
	[TestCase(3_000)]
	[TestCase(4_000)]
	public void Speech_frequencies_pass_with_their_level_intact(int hz)
	{
		var samples = Tone(hz, 48_000, 1.0);

		var resampled = AudioResampler.To(samples, 48_000, AudioResampler.SpeechRate);

		Assert.That(
			Decibels(Amplitude(resampled), Amplitude(samples)),
			Is.InRange(-3, 1),
			$"{hz} Hz came out at {Decibels(Amplitude(resampled), Amplitude(samples)):F1} dB");
	}

	/// <summary>An unchanged rate hands the samples straight back, because inventing between them can only lose something.</summary>
	[Test]
	public void An_unchanged_rate_is_a_pass_through()
	{
		float[] samples = [0.1f, -0.2f, 0.3f];

		Assert.That(AudioResampler.To(samples, 48_000, 48_000), Is.SameAs(samples));
	}

	/// <summary>An empty buffer stays empty rather than throwing from inside the filter.</summary>
	[Test]
	public void Nothing_in_produces_nothing_out()
	{
		Assert.That(AudioResampler.To([], 192_000, 16_000), Is.Empty);
	}

	/// <summary>
	/// A rate of zero would divide by nothing. Only the device supplies a rate, so this cannot happen in
	/// practice, and the failure would otherwise be a NaN length deep inside the filter.
	/// </summary>
	[TestCase(0, 16_000)]
	[TestCase(48_000, 0)]
	[TestCase(-1, 16_000)]
	public void An_impossible_rate_is_refused(int from, int to)
	{
		Assert.That(
			() => AudioResampler.To([0.1f, 0.2f], from, to),
			Throws.TypeOf<ArgumentOutOfRangeException>());
	}

	/// <summary>
	/// A packet shorter than the filter still has to produce a finite answer. It runs on every packet the
	/// microphone delivers, so a throw here would take the wake word down rather than one packet.
	/// </summary>
	[TestCase(13)]
	[TestCase(64)]
	[TestCase(997)]
	[TestCase(4_800)]
	public void A_packet_shorter_than_the_filter_still_resamples(int length)
	{
		var samples = new float[length];

		for (var index = 0; index < length; index++)
		{
			samples[index] = index / (float)length;
		}

		var resampled = AudioResampler.To(samples, 192_000, 16_000);

		Assert.Multiple(() =>
		{
			Assert.That(resampled, Is.Not.Empty, $"{length} samples produced nothing at all");
			Assert.That(resampled.All(float.IsFinite), Is.True, $"{length} samples produced a NaN");
		});
	}

	/// <summary>
	/// A packet shorter than one output period has nothing to produce a sample from. Throwing or inventing one
	/// would both be worse than the empty answer, which the engine treats as no audio in this packet.
	/// </summary>
	[TestCase(1)]
	[TestCase(2)]
	[TestCase(3)]
	public void A_packet_shorter_than_one_output_period_produces_nothing(int length)
	{
		Assert.That(AudioResampler.To(new float[length], 192_000, 16_000), Is.Empty);
	}

	/// <summary>Upsampling has no alias to create, so it must not lose level either.</summary>
	[TestCase(8_000)]
	[TestCase(11_025)]
	public void Upsampling_keeps_the_level(int sourceRate)
	{
		var samples = Tone(440, sourceRate, 1.0);

		var resampled = AudioResampler.To(samples, sourceRate, AudioResampler.SpeechRate);

		Assert.That(
			Decibels(Amplitude(resampled), Amplitude(samples)),
			Is.InRange(-3, 1),
			$"upsampling from {sourceRate} Hz changed the level");
	}

	/// <summary>
	/// Resampling runs on the capture thread for every packet, so a filter that costs real time drops audio.
	/// This is a bound rather than a benchmark, generous enough not to fail on a slow machine and tight
	/// enough to catch a filter that grew by orders of magnitude.
	/// </summary>
	[Test]
	public void Resampling_a_packet_costs_far_less_than_the_packet_is_long()
	{
		var samples = White(96_000);

		var started = System.Diagnostics.Stopwatch.StartNew();
		AudioResampler.To(samples[..4_800], 96_000, 16_000);
		started.Stop();

		Assert.That(
			started.ElapsedMilliseconds,
			Is.LessThan(50),
			$"resampling 50 ms of 96 kHz audio took {started.ElapsedMilliseconds} ms");
	}

	private static float[] LinearInterpolation(float[] samples, int from, int to)
	{
		var length = (int)Math.Round(samples.Length * (double)to / from, MidpointRounding.AwayFromZero);
		var result = new float[length];
		var step = (double)from / to;

		for (var index = 0; index < length; index++)
		{
			var position = index * step;
			var left = (int)position;

			result[index] = left >= samples.Length - 1
				? samples[^1]
				: samples[left] + ((samples[left + 1] - samples[left]) * (float)(position - left));
		}

		return result;
	}
}