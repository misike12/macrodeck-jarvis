namespace Jarvis.Plugin.Audio;

/// <summary>
/// Turns a stream of samples into a single smoothed level. Smoothing is asymmetric on purpose: a fast
/// attack so speech onset is visible immediately, and a slower release so the orb does not flicker
/// between syllables.
/// </summary>
public sealed class AmplitudeMeter
{
	private const double AttackCoefficient = 0.6;
	private const double ReleaseCoefficient = 0.12;

	private double _level;
	private long _frames;
	private double _sumOfSquares;
	private double _peak;

	public double Level => _level;

	/// <summary>The largest sample seen since the last read, which is what a level meter wants to show.</summary>
	public double Peak => _peak;

	public void Reset()
	{
		_level = 0;
		_frames = 0;
		_sumOfSquares = 0;
		_peak = 0;
	}

	/// <summary>
	/// Accumulates a block of 32-bit float samples and returns the smoothed level. The accumulator is
	/// cleared every block: leaving it to grow would make the figure a running average over the whole
	/// session, which both lags badly and never settles.
	/// </summary>
	public double Accumulate(ReadOnlySpan<float> samples)
	{
		foreach (var sample in samples)
		{
			_sumOfSquares += sample * sample;
			_frames++;

			var magnitude = Math.Abs(sample);

			if (magnitude > _peak)
			{
				_peak = magnitude;
			}
		}

		if (_frames == 0)
		{
			return _level;
		}

		var rms = Math.Sqrt(_sumOfSquares / _frames);

		_sumOfSquares = 0;
		_frames = 0;

		var target = Normalise(rms);
		var coefficient = target > _level ? AttackCoefficient : ReleaseCoefficient;

		_level += ((target - _level) * coefficient);
		return _level;
	}

	/// <summary>
	/// Speech RMS lives well below full scale, so the curve is expanded rather than linear. Without
	/// this a normal speaking voice maps to a fifth of the orb's radius and the reaction is invisible.
	/// </summary>
	private static double Normalise(double rms) => Math.Clamp(Math.Pow(Math.Clamp(rms * 3.2, 0, 1), 0.62), 0, 1);
}