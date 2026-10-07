using Jarvis.Plugin.Audio;
using NAudio.Wave;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Opening a capture device without assuming anything about it.
/// <para>
/// The plugin used to ask for 48 kHz mono float and treat anything else as a failure, so a 96 kHz studio
/// interface, a 16 kHz Bluetooth headset and a stereo line-in all presented as "JARVIS cannot hear
/// anything" on hardware that works in every other application. The negotiation is now a ladder, and what
/// it produces has to be right for every rate and channel count a device can hand over.
/// </para>
/// </summary>
[TestFixture]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class CaptureFormatTests
{
	/// <summary>A cheap USB headset, which is the commonest thing in the world.</summary>
	private static readonly CaptureFormat UsbHeadset = new(48_000, 1, 32);

	/// <summary>A studio interface that will only do 192 kHz.</summary>
	private static readonly CaptureFormat StudioInterface = new(192_000, 2, 32);

	/// <summary>A Bluetooth headset, which negotiates its own narrow rate.</summary>
	private static readonly CaptureFormat Bluetooth = new(16_000, 1, 32);

	/// <summary>An aggregate endpoint wrapping something the driver reports as 24-bit.</summary>
	private static readonly CaptureFormat Aggregate = new(44_100, 2, 24);

	/// <summary>A device that will say nothing at all, which happens on an aggregate endpoint.</summary>
	private static CaptureFormat Silent => default;

	/// <summary>
	/// A device that only does 192 kHz must be opened at 192 kHz, not refused. The whole point of the ladder
	/// is that no rate is excluded.
	/// </summary>
	[Test]
	public void A_device_that_only_offers_its_own_rate_is_opened_at_it()
	{
		var candidates = AudioDeviceCatalog.CandidatesFor(StudioInterface);

		Assert.That(
			candidates[0],
			Is.EqualTo(new CaptureFormat(192_000, 1, 32)),
			"the device's own rate was not tried first");
	}

	[Test]
	public void The_preferred_rate_is_still_asked_for_first_when_the_device_offers_it()
	{
		Assert.That(
			AudioDeviceCatalog.CandidatesFor(UsbHeadset)[0],
			Is.EqualTo(CaptureFormat.Preferred));
	}

	/// <summary>Every rate Windows is seen to negotiate has to produce a ladder that starts somewhere.</summary>
	[TestCase(8_000)]
	[TestCase(11_025)]
	[TestCase(16_000)]
	[TestCase(22_050)]
	[TestCase(24_000)]
	[TestCase(32_000)]
	[TestCase(44_100)]
	[TestCase(48_000)]
	[TestCase(88_200)]
	[TestCase(96_000)]
	[TestCase(192_000)]
	public void Every_rate_a_device_can_negotiate_is_usable(int sampleRate)
	{
		var device = new CaptureFormat(sampleRate, 1, 32);

		Assert.Multiple(() =>
		{
			Assert.That(CaptureRates.IsUsable(sampleRate), Is.True, $"{sampleRate} Hz was rejected");
			Assert.That(AudioDeviceCatalog.CandidatesFor(device), Is.Not.Empty, $"{sampleRate} Hz had nowhere to go");
		});
	}

	/// <summary>
	/// A device that reports a rate the ladder does not know is used as itself, and its own rate appears
	/// exactly once. Asking twice costs a failed open for no reason.
	/// </summary>
	[Test]
	public void The_devices_own_rate_is_not_asked_for_twice()
	{
		var candidates = AudioDeviceCatalog.CandidatesFor(new CaptureFormat(96_000, 2, 32));

		Assert.That(candidates.Count, Is.EqualTo(candidates.Distinct().Count()), "the ladder repeats itself");
	}

	/// <summary>
	/// A device that will say nothing still has to be openable, because the preferred rates cover what most
	/// drivers will convert to.
	/// </summary>
	[Test]
	public void A_device_that_reports_nothing_still_gets_the_preferred_rates()
	{
		Assert.That(AudioDeviceCatalog.CandidatesFor(Silent), Is.Not.Empty);
	}

	/// <summary>A driver reporting a nonsense rate is bounded rather than believed.</summary>
	[TestCase(0)]
	[TestCase(-1)]
	[TestCase(1_000_000)]
	public void An_unusable_rate_is_refused_rather_than_believed(int sampleRate)
	{
		Assert.That(CaptureRates.IsUsable(sampleRate), Is.False, $"{sampleRate} Hz was accepted");
	}

	/// <summary>The requested format is always floats, which is the only shape the meters can read.</summary>
	[TestCase(8_000, 1)]
	[TestCase(48_000, 1)]
	[TestCase(96_000, 2)]
	public void What_is_asked_for_is_always_float32(int sampleRate, int channels)
	{
		var requested = new CaptureFormat(sampleRate, channels, 32).ToWaveFormat();

		Assert.Multiple(() =>
		{
			Assert.That(requested.SampleRate, Is.EqualTo(sampleRate));
			Assert.That(requested.Channels, Is.EqualTo(channels));
			Assert.That(requested.BitsPerSample, Is.EqualTo(32));
			Assert.That(requested.Encoding, Is.EqualTo(NAudio.Wave.WaveFormatEncoding.IeeeFloat));
		});
	}

	/// <summary>The format that came back off the recorder is what everything downstream is told.</summary>
	[Test]
	public void The_format_is_read_out_of_the_wave_format_rather_than_assumed()
	{
		var actual = WaveFormat.CreateIeeeFloatWaveFormat(96_000, 2);

		var read = CaptureFormat.From(actual);

		Assert.Multiple(() =>
		{
			Assert.That(read.SampleRate, Is.EqualTo(96_000));
			Assert.That(read.Channels, Is.EqualTo(2));
			Assert.That(read.BitsPerSample, Is.EqualTo(32));
			Assert.That(read.IsFloat32, Is.True);
			Assert.That(read.IsMono, Is.False);
		});
	}

	/// <summary>A 24-bit endpoint is not something the meters can read, so it is reported as not float.</summary>
	[Test]
	public void A_device_that_did_not_convert_is_reported_rather_than_read_as_noise()
	{
		var read = CaptureFormat.From(new WaveFormat(44_100, 16, 2));

		Assert.That(read.IsFloat32, Is.False, "16-bit PCM was taken for floats");
	}
}

/// <summary>
/// Turning a device's packets into one channel of floats.
/// <para>
/// A stereo or surround input that will not convert is delivered interleaved. Reading it as mono does not
/// merely lose a channel, it interleaves the two streams and plays the result at double speed, so the word
/// arrives at the models at half its length and half its pitch - which scores as silence rather than as a
/// mistake, and is invisible on a level meter.
/// </para>
/// </summary>
[TestFixture]
public class CaptureInterleaveTests
{
	/// <summary>
	/// A mono packet is the commonest path there is, so it is passed through rather than copied through an
	/// averaging loop that would produce the same numbers for less.
	/// </summary>
	[Test]
	public void A_mono_packet_is_passed_through_unchanged()
	{
		float[] packet = [0.1f, -0.2f, 0.3f];

		Assert.That(CaptureInterleave.ToMono(packet, 1), Is.EqualTo(packet));
	}

	/// <summary>A stereo pair becomes one sample per frame, not one per sample.</summary>
	[Test]
	public void A_stereo_packet_halves_to_one_sample_per_frame()
	{
		float[] packet = [1f, -1f, 0.5f, -0.5f, 0.25f, -0.25f];

		var mono = CaptureInterleave.ToMono(packet, 2);

		Assert.Multiple(() =>
		{
			Assert.That(mono, Has.Length.EqualTo(3));
			Assert.That(mono[0], Is.EqualTo(0f).Within(1e-6));
			Assert.That(mono[1], Is.EqualTo(0f).Within(1e-6));
			Assert.That(mono[2], Is.EqualTo(0f).Within(1e-6));
		});
	}

	private static readonly float[] LeftLoudBoth = [1f, 1f, 0.5f, 0.5f];

	/// <summary>
	/// The mean, not the sum. A pair that is loud in both channels is not twice as loud, and summing it pins
	/// the level meter on any stereo input, which is what a sensitivity threshold is then measured against.
	/// </summary>
	[Test]
	public void A_stereo_pair_is_averaged_rather_than_summed()
	{
		var mono = CaptureInterleave.ToMono(LeftLoudBoth, 2);

		Assert.Multiple(() =>
		{
			Assert.That(mono, Has.Length.EqualTo(2));
			Assert.That(mono[0], Is.EqualTo(1f).Within(1e-6), "a loud pair was doubled instead of averaged");
			Assert.That(mono[1], Is.EqualTo(0.5f).Within(1e-6));
		});
	}

	[Test]
	public void A_surround_packet_becomes_one_sample_per_frame()
	{
		var packet = new float[] { 1f, 0f, 0f, 0f, 0.5f, 0.5f, 0.5f, 0.5f };

		Assert.Multiple(() =>
		{
			Assert.That(CaptureInterleave.ToMono(packet, 4), Has.Length.EqualTo(2));
			Assert.That(CaptureInterleave.ToMono(packet, 4)[1], Is.EqualTo(0.5f).Within(1e-6));
		});
	}

	/// <summary>A trailing partial frame is dropped rather than read, because it shifts everything after it.</summary>
	[Test]
	public void A_trailing_partial_frame_is_dropped()
	{
		// Three samples is one whole frame of stereo plus a half.
		var packet = new float[] { 1f, 1f, 0.5f };

		Assert.That(CaptureInterleave.ToMono(packet, 2), Has.Length.EqualTo(1));
	}

	/// <summary>A stereo frame is eight bytes, so the count is the buffer divided by that.</summary>
	[TestCase(1, 16, 4)]
	[TestCase(1, 4, 1)]
	[TestCase(2, 16, 2)]
	[TestCase(2, 4, 0)]
	[TestCase(4, 32, 2)]
	[TestCase(4, 16, 1)]
	[TestCase(2, 17, 2)]
	[TestCase(2, 7, 0)]
	[TestCase(2, 15, 1)]
	public void Whole_frames_are_what_a_buffer_of_that_length_can_hold(int channels, int bytes, int expected)
	{
		Assert.That(CaptureInterleave.WholeFrames(bytes, channels), Is.EqualTo(expected));
	}

	/// <summary>A channel count of zero would divide by nothing, which is the one thing that must not happen.</summary>
	[TestCase(0)]
	[TestCase(-1)]
	public void No_channels_means_no_frames(int channels)
	{
		Assert.That(CaptureInterleave.WholeFrames(64, channels), Is.Zero);
	}

	/// <summary>Anything that survives a channel count has to survive at every count a device can report.</summary>
	[TestCase(1)]
	[TestCase(2)]
	[TestCase(4)]
	[TestCase(6)]
	[TestCase(8)]
	public void Every_channel_count_a_device_reports_produces_usable_mono(int channels)
	{
		var frames = 480;
		var packet = new float[frames * channels];

		for (var index = 0; index < packet.Length; index++)
		{
			packet[index] = (index % channels) + 1f;
		}

		var mono = CaptureInterleave.ToMono(packet, channels);

		Assert.Multiple(() =>
		{
			Assert.That(mono, Has.Length.EqualTo(frames));
			Assert.That(mono.All(value => value > 0f), Is.True, $"{channels} channels produced silence");
		});
	}
}