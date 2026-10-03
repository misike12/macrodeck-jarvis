using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Speech;
using NUnit.Framework;
using Serilog;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Barge-in.
/// <para>
/// The timing rules are the whole of this feature and they are asserted as properties rather than by
/// waiting: a test that slept for the real durations would be slow and would still pass on a machine slow
/// enough to hide a regression. The detector is stopped rather than left polling, so nothing here needs an
/// audio device.
/// </para>
/// </summary>
[TestFixture]
public class BargeInTests
{
	/// <summary>
	/// A single loud packet must not interrupt. This is the failure that makes barge-in unusable: a click,
	/// a door, or the click of the user's own keyboard ends the reply mid-sentence.
	/// </summary>
	[Test]
	public void The_level_must_be_held_not_merely_touched()
	{
		Assert.That(
			BargeInDetector.HoldRequired,
			Is.GreaterThan(TimeSpan.FromMilliseconds(150)),
			"a shorter hold than this interrupts on any transient noise");
	}

	/// <summary>
	/// After interrupting, the detector has to go quiet for longer than the speaker's own latency, or the
	/// tail of the reply comes back through the microphone and starts a second interruption.
	/// </summary>
	[Test]
	public void The_cooldown_outlasts_the_required_hold()
	{
		Assert.That(BargeInDetector.CooldownLength, Is.GreaterThan(BargeInDetector.HoldRequired));
	}

	[Test]
	public void The_cooldown_is_long_enough_to_outlast_audio_already_queued()
	{
		// The player is configured with 150 ms of latency, and a clip can be further along than that by the
		// time the stop takes effect. Anything under a second re-triggers.
		Assert.That(BargeInDetector.CooldownLength, Is.GreaterThan(TimeSpan.FromMilliseconds(600)));
	}

	/// <summary>The threshold is clamped, because an unusable value silently disables the feature.</summary>
	[TestCase(0.0, 0.001)]
	[TestCase(-3.0, 0.001)]
	[TestCase(0.0001, 0.001)]
	[TestCase(0.5, 0.5)]
	[TestCase(1.0, 1.0)]
	[TestCase(99.0, 1.0)]
	public void The_threshold_is_kept_in_a_usable_range(double set, double expected)
	{
		using var detector = new BargeInDetector(null!, null!, Log.Logger);

		detector.Threshold = set;

		Assert.That(detector.Threshold, Is.EqualTo(expected).Within(0.0001));
	}

	/// <summary>Nothing is interrupted before a reply starts, so an idle plugin is never silenced.</summary>
	[Test]
	public void Nothing_is_interrupted_before_the_detector_is_started()
	{
		using var detector = new BargeInDetector(null!, null!, Log.Logger);

		detector.Threshold = 0.12;

		Assert.That(detector.Interruptions, Is.EqualTo(0));
	}

	/// <summary>Starting twice must not leave two pollers running against each other.</summary>
	[Test]
	public void Starting_twice_is_harmless()
	{
		using var detector = new BargeInDetector(null!, null!, Log.Logger);

		Assert.DoesNotThrow(() =>
		{
			detector.Start();
			detector.Start();
			detector.Stop();
			detector.Stop();
		});
	}

	/// <summary>A microphone that is not running reports zero, so no barge-in is possible without one.</summary>
	[Test]
	public void A_microphone_that_is_not_capturing_reports_nothing()
	{
		using var monitor = new MicrophoneMonitor(
			new Jarvis.Plugin.Core.AssistantStateHolder(),
			Log.Logger);

		Assert.Multiple(() =>
		{
			Assert.That(monitor.IsListening, Is.False);
			Assert.That(monitor.Level, Is.EqualTo(0));
		});
	}

	/// <summary>
	/// The threshold the settings default to. Pinned because it was previously 0.25, which sat above a
	/// normal speaking voice measured through this same meter, so barge-in could never have fired.
	/// </summary>
	[Test]
	public void The_default_threshold_is_below_a_measured_voice()
	{
		var meter = new Jarvis.Plugin.Audio.AmplitudeMeter();

		// A block shaped like sustained speech at a normal level, as the capture would deliver it.
		var samples = new float[4800];

		for (var index = 0; index < samples.Length; index++)
		{
			samples[index] = (float)(0.04 * Math.Sin(index * 0.08));
		}

		var level = 0.0;

		for (var block = 0; block < 10; block++)
		{
			level = meter.Accumulate(samples.AsSpan(block * 480, 480));
		}

		Assert.That(level, Is.GreaterThan(new Jarvis.Plugin.Core.JarvisSettings().BargeInThreshold));
	}
}
