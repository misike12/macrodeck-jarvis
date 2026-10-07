using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Jarvis.Plugin.Audio;

/// <summary>
/// What a capture device actually agreed to deliver.
/// <para>
/// Every consumer has to be told this rather than assuming the format that was requested. Treating a 96 kHz
/// stream as 48 kHz halves its pitch, which still reads as speech on a level meter and scores the wake word
/// at nothing; treating two channels as one plays back at double speed, and a 24-bit stream read as floats
/// is noise.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public readonly record struct CaptureFormat(int SampleRate, int Channels, int BitsPerSample)
{
	public const int RequiredChannels = 1;

	public bool IsMono => Channels == RequiredChannels;

	public bool IsFloat32 => BitsPerSample == 32;

	/// <summary>Bytes one sample of one channel occupies, which is what the driver's buffer length is in.</summary>
	public int BytesPerSampleFrame => Channels * (BitsPerSample / 8);

	/// <summary>
	/// What the plugin asks for when a device will take it. A preference, never a requirement: the negotiation
	/// falls back to the endpoint's own format, and then to other rates, so no hardware is excluded for it.
	/// </summary>
	public static CaptureFormat Preferred { get; } = new(MicrophoneMonitor.SampleRate, RequiredChannels, 32);

	public static CaptureFormat From(WaveFormatShape shape) => new(shape.SampleRate, shape.Channels, shape.BitsPerSample);

	/// <summary>Reads the three numbers this type cares about out of any NAudio wave format.</summary>
	public static CaptureFormat From(WaveFormat format) => new(format.SampleRate, format.Channels, format.BitsPerSample);

	/// <summary>
	/// The NAudio format to ask a device for. Always 32-bit float, because that is the only shape the audio
	/// engine converts to without losing precision and the only one the meters and the models can read without
	/// a second conversion of our own.
	/// </summary>
	public WaveFormat ToWaveFormat() => WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

	public override string ToString() =>
		$"{SampleRate} Hz, {Channels} channel{(Channels == 1 ? string.Empty : "s")}, {BitsPerSample}-bit";
}

/// <summary>
/// The parts of a NAudio <c>WaveFormat</c> this plugin needs, read without depending on its concrete types.
/// </summary>
public readonly record struct WaveFormatShape(int SampleRate, int Channels, int BitsPerSample);

/// <summary>
/// The sample-rate ladder used when opening a device.
/// <para>
/// Ordered by how well each rate suits the models downstream, not by how common it is. 48 kHz is what the
/// orb's meter and its ring buffer are sized for and what most drivers will convert to; 16 kHz is what
/// whisper and the keyword models want; 44.1 kHz is the CD rate and is what anything with an A/D converter
/// bolted to a computer tends to offer. Anything else is taken from the endpoint itself rather than guessed
/// at, so an unusual rate is used as itself instead of being forced onto this ladder.
/// </para>
/// </summary>
public static class CaptureRates
{
	/// <summary>Tried in order when a device will convert.</summary>
	public static IReadOnlyList<int> Preferred { get; } = [48_000, 16_000, 44_100];

	/// <summary>
	/// Whether a rate is one capture can be opened at. Bounded at the top because an endpoint reporting a
	/// nonsense rate is a driver fault, and asking for it would allocate a buffer per sample period.
	/// </summary>
	public static bool IsUsable(int sampleRate) => sampleRate > 0 && sampleRate <= 384_000;
}

/// <summary>
/// Turns a capture device's packets into the single channel of 32-bit floats everything downstream expects.
/// <para>
/// WASAPI delivers interleaved samples at the device's own rate and channel count, and a driver that will
/// not convert hands over whatever it has: two channels from a stereo interface, four from a surround input,
/// 96 kHz from anything with a converter worth the name. Reading a stereo stream as mono does not merely
/// lose the right-hand channel, it plays the audio back at double speed, so the word arrives at the models
/// at half its length and half its pitch.
/// </para>
/// </summary>
public static class CaptureInterleave
{
	/// <summary>
	/// Averages the channels of one packet down to mono, in place-free fashion so the driver buffer is never
	/// retained. A single-channel packet is returned as it is, because averaging one value into itself would
	/// only cost an allocation on the commonest path there is.
	/// </summary>
	public static float[] ToMono(ReadOnlySpan<float> interleaved, int channels)
	{
		if (channels <= 1)
		{
			return interleaved.ToArray();
		}

		var frames = interleaved.Length / channels;
		var mono = new float[frames];

		for (var frame = 0; frame < frames; frame++)
		{
			var total = 0f;
			var offset = frame * channels;

			for (var channel = 0; channel < channels; channel++)
			{
				total += interleaved[offset + channel];
			}

			// The mean rather than the sum: a stereo pair that is loud in both channels is not twice as loud,
			// and a summed pair would pin the level meter on any stereo input.
			mono[frame] = total / channels;
		}

		return mono;
	}

	/// <summary>
	/// How many float samples a buffer of this many bytes holds, which is what decides whether a partial
	/// frame at the end is discarded or would shift every sample after it by one position.
	/// </summary>
	public static int WholeFrames(int byteLength, int channels)
	{
		var perFrame = channels * sizeof(float);
		return perFrame <= 0 ? 0 : byteLength / perFrame;
	}
}