using System.Runtime.Versioning;

namespace Jarvis.Plugin.Audio;

/// <summary>
/// Converts captured audio to the rate a speech model expects.
/// <para>
/// Both callers need this - the wake word wants 16 kHz from whatever the device runs at, and so does whisper -
/// and they must agree on it, because two implementations of the same conversion would score the same word
/// differently and there would be no way to tell which was wrong.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AudioResampler
{
	/// <summary>The rate every speech model in this plugin is trained at.</summary>
	public const int SpeechRate = 16_000;

	/// <summary>
	/// Taps per side of the low-pass filter, scaled by how much is being thrown away.
	/// <para>
	/// Sixteen is enough to place the transition usefully, and the cap below is what stops a 192 kHz device
	/// from asking for a thousand taps per output sample. The cost matters because this runs on every packet
	/// the microphone delivers, not once per utterance: a filter that took a tenth of a second per packet
	/// would fall behind the capture thread and drop the audio the wake word needs.
	/// </para>
	/// </summary>
	private const int FilterHalfLengthPerRatio = 16;

	/// <summary>Ceiling on the taps per side, whatever the ratio asks for.</summary>
	private const int MaximumHalfLength = 256;

	/// <summary>
	/// The cutoff as a fraction of the output rate.
	/// <para>
	/// Below Nyquist, and comfortably so. Speech models care about everything up to about 7.5 kHz and nothing
	/// above it, so a cutoff at 80 percent of a 16 kHz output still passes the whole of speech while leaving
	/// the transition narrow enough to place the stopband edge above 14 kHz, where a 20 kHz tone on a studio
	/// interface has to already be gone.
	/// </para>
	/// </summary>
	private const double CutoffFraction = 0.8;

	/// <summary>Ceiling on how much filter the largest supported ratio asks for.</summary>
	private const int MaximumRatio = 32;

	/// <summary>
	/// Resamples device-rate audio to <paramref name="targetRate"/>.
	/// <para>
	/// The distinction that matters is between a ratio of one, where the samples are copied because inventing
	/// between them can only lose something, and a ratio of several, where the audio has to be filtered before
	/// it is thinned. Linear interpolation on its own decimates by aliasing: everything above the new halfway
	/// frequency folds back down and arrives as noise in the middle of the speech band, which a small
	/// recogniser answers with a confident and wrong transcript. Going from a 192 kHz interface to 16 kHz
	/// throws away twelve samples in every twelve, so this is the ordinary case on studio hardware rather than
	/// an edge one.
	/// </para>
	/// </summary>
	public static float[] To(float[] samples, int fromRate, int targetRate)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fromRate, 0);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetRate, 0);

		if (samples.Length == 0)
		{
			return [];
		}

		if (fromRate == targetRate)
		{
			return samples;
		}

		var length = (int)Math.Round(samples.Length * (double)targetRate / fromRate, MidpointRounding.AwayFromZero);

		if (length <= 0)
		{
			return [];
		}

		// Upsampling invents samples between the ones it has and cannot alias, because there is no frequency
		// content above the original Nyquist to fold down. Interpolation is the right answer for this direction
		// and an expensive filter would buy nothing.
		return targetRate > fromRate
			? Interpolate(samples, length)
			: FilterAndThin(samples, length, fromRate, targetRate);
	}

	private static float[] Interpolate(float[] samples, int length)
	{
		var step = samples.Length / (double)length;
		var result = new float[length];

		for (var index = 0; index < length; index++)
		{
			var position = index * step;
			var left = (int)position;

			if (left >= samples.Length - 1)
			{
				result[index] = samples[^1];
				continue;
			}

			var fraction = (float)(position - left);
			result[index] = (samples[left] * (1 - fraction)) + (samples[left + 1] * fraction);
		}

		return result;
	}

	/// <summary>
	/// A windowed-sinc low pass, applied before each output sample is read from the input.
	/// <para>
	/// The window is what makes it a filter rather than an ideal brick wall: a sinc that ran to its ends
	/// would ring on every transient, and the speech models treat that ringing as consonants.
	/// </para>
	/// </summary>
	private static float[] FilterAndThin(float[] samples, int length, int fromRate, int targetRate)
	{
		var ratio = fromRate / (double)targetRate;
		var halfLength = (int)Math.Min(
			FilterHalfLengthPerRatio * Math.Clamp(ratio, 1, MaximumRatio),
			Math.Min(MaximumHalfLength, samples.Length / 2));

		if (halfLength < 1)
		{
			return Interpolate(samples, length);
		}

		var kernel = SincKernel(halfLength, fromRate, targetRate);
		var step = samples.Length / (double)length;
		var result = new float[length];

		for (var index = 0; index < length; index++)
		{
			var centre = index * step;
			var leftEdge = (int)Math.Floor(centre) - halfLength;
			var total = 0f;
			var applied = 0f;

			for (var offset = -halfLength; offset <= halfLength; offset++)
			{
				var source = leftEdge + offset;

				if (source < 0 || source >= samples.Length)
				{
					continue;
				}

				var tap = kernel[offset + halfLength];

				total += samples[source] * tap;
				applied += tap;
			}

			// Renormalised by the kernel's own sum, which is one, and not by the taps that happened to land
			// inside the buffer. At the very edges of a clip only half the kernel is available, and dividing
			// by that half doubles the level of whatever is there: an out-of-band tone comes out of the filter
			// louder than it went in, which is worse than no filter at all and exactly what the stopband is
			// supposed to prevent. The edges lose a little level instead, which is inaudible on a clip that
			// already begins and ends in silence.
			result[index] = applied < 0.1f
				? samples[Math.Clamp((int)Math.Round(centre), 0, samples.Length - 1)]
				: total;
		}

		return result;
	}

	/// <summary>
	/// A windowed sinc for one conversion, normalised so its taps sum to one.
	/// <para>
	/// The cutoff is taken from the output rate and not from whichever of the two is lower. The job is to
	/// remove everything the output cannot represent, so a cutoff above the output's Nyquist removes nothing
	/// that matters and leaves the content that folds down in the passband where a recogniser reads it as
	/// speech.
	/// </para>
	/// <para>
	/// The sinc's argument is the offset scaled by the cutoff over the <em>source</em> rate, because an offset
	/// is counted in source samples. Scaling it any other way makes the filter narrower or wider than the
	/// cutoff says, which at a ratio of twelve looks almost right and leaves the alias an order of magnitude
	/// above where it needs to be.
	/// </para>
	/// </summary>
	private static float[] SincKernel(int halfLength, int fromRate, int outputRate)
	{
		var cutoff = outputRate * CutoffFraction;
		var kernel = new float[(halfLength * 2) + 1];
		var total = 0f;

		for (var offset = -halfLength; offset <= halfLength; offset++)
		{
			var normalized = offset * cutoff / fromRate;
			var sinc = Math.Abs(normalized) < 1e-9 ? 1 : Math.Sin(Math.PI * normalized) / (Math.PI * normalized);
			var window = Blackman(offset, halfLength);

			kernel[offset + halfLength] = (float)(sinc * window);
			total += kernel[offset + halfLength];
		}

		if (Math.Abs(total) < 1e-6f)
		{
			kernel[halfLength] = 1f;
			return kernel;
		}

		for (var index = 0; index < kernel.Length; index++)
		{
			kernel[index] /= total;
		}

		return kernel;
	}

	/// <summary>
	/// Blackman rather than a rectangular window. Its first side lobe is far lower than a boxcar's, which is
	/// what decides whether the stopband is quiet enough for the speech band to survive.
	/// </summary>
	private static double Blackman(int offset, int halfLength)
	{
		if (halfLength <= 0)
		{
			return 1;
		}

		var position = offset / (double)halfLength;

		return 0.42
			+ (0.5 * Math.Cos(Math.PI * position))
			- (0.08 * Math.Cos(2 * Math.PI * position));
	}
}