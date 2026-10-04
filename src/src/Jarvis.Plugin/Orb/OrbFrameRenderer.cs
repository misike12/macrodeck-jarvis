using Jarvis.Plugin.Core;

namespace Jarvis.Plugin.Orb;

/// <summary>
/// Draws one orb frame analytically into a straight RGBA buffer. Every element is a distance function,
/// so the renderer needs no drawing library at all: a radial gradient, a set of rings and a soft glow
/// are all closed-form, and antialiasing is a smoothstep across one pixel of the boundary.
/// </summary>
public static class OrbFrameRenderer
{
	/// <summary>
	/// The edge of the rendered square, in pixels.
	/// <para>
	/// This was 96, which is smaller than the widget that displays it: a one-cell orb is roughly 120 to 160
	/// reference units, so a 96 pixel asset was being stretched to about two and a half times its own size and
	/// read as a blocky ball. The asset is cheap at this size now, because the encoder compresses a smooth
	/// radial gradient well, so the resolution can simply match the display rather than being dialled against a
	/// budget it no longer comes close to.
	/// </para>
	/// </summary>
	public const int Size = 256;

	/// <summary>
	/// Samples per axis. Two rather than three, because the edge smoothstep is measured in pixels and the
	/// extra samples bought visible antialiasing on a 96 pixel asset that was mostly being thrown away by the
	/// upscale. At this resolution four samples per pixel is where the quality is, and two is enough.
	/// </summary>
	private const int Samples = 2;

	/// <summary>
	/// The most rings any preset draws, so a stored count above it cannot ask for geometry that does not exist.
	/// </summary>
	private const int MaximumRings = 8;

	public static byte[] Render(
		AssistantState state,
		double phase,
		OrbPalette palette,
		double amplitude,
		OrbPreset preset = OrbPreset.ArcReactor,
		int ringCount = 3,
		double ringSpeed = 1.0,
		bool rotateRings = true,
		bool glow = true)
	{
		var buffer = new byte[Size * Size * 4];
		var centre = Size / 2.0;

		var shape = ShapeOf(preset);
		var (coreRadius, glowStrength, _, ringAlpha) = ShapeFor(state, amplitude);
		coreRadius *= shape.CoreScale;

		// The four settings the user can change, applied to the frame rather than to a layer above it.
		//
		// They used to drive a glow disc and a set of rings drawn as separate nodes over the image, while the
		// image already contained a glow and rings of its own. So the orb drew everything twice, and these
		// settings changed the duplicate rather than the orb. They belong here, where they change the pixels
		// that are actually shown, which also means a change to any of them has to reach the asset cache key
		// or the previous frames are served from it.
		var rings = Math.Clamp(ringCount, 0, MaximumRings);
		var ringPhase = rotateRings ? phase * Math.Max(0, ringSpeed) : 0;

		if (!glow)
		{
			glowStrength = 0;
		}

		for (var y = 0; y < Size; y++)
		{
			for (var x = 0; x < Size; x++)
			{
				var covered = 0;
				var red = 0.0;
				var green = 0.0;
				var blue = 0.0;
				var alpha = 0.0;

				for (var sy = 0; sy < Samples; sy++)
				{
					for (var sx = 0; sx < Samples; sx++)
					{
						var px = x + ((sx + 0.5) / Samples);
						var py = y + ((sy + 0.5) / Samples);
						var dx = px - centre;
						var dy = py - centre;
						var distance = Math.Sqrt((dx * dx) + (dy * dy));
						var angle = Math.Atan2(dy, dx);

						var (r, g, b, a) = Shade(
							distance,
							angle,
							ringPhase,
							palette,
							coreRadius,
							glowStrength,
							ringAlpha,
							state,
							shape,
							rings);

						red += r * a;
						green += g * a;
						blue += b * a;
						alpha += a;
						covered++;
					}
				}

				if (alpha <= 0.0001)
				{
					continue;
				}

				var offset = ((y * Size) + x) * 4;
				buffer[offset] = ToByte(red / alpha);
				buffer[offset + 1] = ToByte(green / alpha);
				buffer[offset + 2] = ToByte(blue / alpha);
				buffer[offset + 3] = ToByte(alpha / covered);
			}
		}

		return buffer;
	}

	/// <summary>
	/// The geometry for a state, before the preset is applied.
	/// <para>
	/// Amplitude moves the core and the glow, which is what makes the orb visibly react to a voice rather
	/// than merely reporting that it is listening.
	/// </para>
	/// </summary>
	private static (double CoreRadius, double Glow, double Speed, double RingAlpha) ShapeFor(
		AssistantState state,
		double amplitude)
	{
		var lift = Math.Clamp(amplitude, 0, 1);

		return state switch
		{
			AssistantState.Listening => (Size * 0.20 + (Size * 0.05 * lift), 1.0, 1.6, 0.95),
			AssistantState.Thinking => (Size * 0.16, 0.85, -1.2, 0.9),
			AssistantState.Speaking => (Size * 0.18 + (Size * 0.09 * lift), 1.0, 0.8, 0.8),
			AssistantState.Executing => (Size * 0.17, 0.9, 2.4, 1.0),
			AssistantState.Confirming => (Size * 0.19, 1.0, 0.4, 1.0),
			AssistantState.Error => (Size * 0.15, 0.6, 0.2, 0.7),
			AssistantState.Unavailable => (Size * 0.10, 0.2, 0.05, 0.15),
			_ => (Size * 0.15, 0.45, 0.5, 0.5),
		};
	}

	/// <summary>
	/// What makes each preset a different shape rather than a different colour. Three axes are available
	/// and each preset differs on all of them, so any two are distinguishable at a glance and in a still
	/// frame.
	/// </summary>
	private readonly record struct PresetShape(int Rings, double RingSpacing, double Segments, double SegmentsOffset, double CoreScale, double RingWeight);

	private static PresetShape ShapeOf(OrbPreset preset) => preset switch
	{
		// Three close rings around a small bright core, like a containment field.
		OrbPreset.ArcReactor => new(3, 0.065, 6, 0.0, 1.00, 1.00),

		// Two wide rings, fewer segments, and a larger core: calmer and more open than a reactor.
		OrbPreset.Halo => new(2, 0.115, 3, 1.1, 1.35, 1.70),

		// One heavy ring and a small core: a heartbeat rather than a machine.
		OrbPreset.Pulse => new(1, 0.190, 2, 0.4, 0.70, 2.60),

		// Custom is the arc reactor's geometry with a distinct segment offset, so it is visibly its own
		// thing without inventing a fourth shape nobody asked for.
		_ => new(4, 0.048, 8, 2.3, 0.90, 0.80),
	};

	/// <summary>
	/// The core's radius, in pixels, for a state and a voice level.
	/// <para>
	/// Exposed because "a louder voice grows the core" is the property, and measuring it by the mean
	/// lightness of the whole frame gets it wrong: the core is dark and the halo around it is bright, so a
	/// growing core replaces bright pixels with dark ones and mean lightness falls while the orb is very
	/// obviously reacting. The radius is the thing that is actually monotonic.
	/// </para>
	/// </summary>
	public static double CoreRadiusFor(
		AssistantState state,
		double amplitude,
		OrbPreset preset = OrbPreset.ArcReactor) =>
		ShapeFor(state, amplitude).CoreRadius * ShapeOf(preset).CoreScale;

	private static (double R, double G, double B, double A) Shade(
		double distance,
		double angle,
		double ringPhase,
		OrbPalette palette,
		double coreRadius,
		double glowStrength,
		double ringAlpha,
		AssistantState state,
		PresetShape shape,
		int rings)
	{
		var outer = Size * 0.46;
		var red = 0.0;
		var green = 0.0;
		var blue = 0.0;
		var alpha = 0.0;

		// The falloff is squared, which concentrates the halo hard against the core, and the alpha it produced was
		// multiplied down to about a tenth. At idle strength that put the whole halo under a fifth of full
		// alpha, and the frame builder maps anything below half opacity to the transparent index, so the glow
		// was being discarded rather than drawn: the orb read as a hard ball with no halo, and switching the
		// glow off changed no pixels at all because there were none to change. A gentler falloff and a real
		// weight make it visible, which is the only way the setting can mean anything.
	var glowFalloff = 1 - Math.Clamp(distance / outer, 0, 1);
	var glow = glowFalloff * Math.Sqrt(glowFalloff) * glowStrength;
	(red, green, blue) = palette.Accent;
	alpha += glow * 1.8;

		// The edge width is in pixels, so it scales with the render size. Left at its value for a 96 pixel
		// frame it would be a third as wide again and the core would gain a hard rim at this resolution.
		var edgeWidth = 1.4 * (Size / 96.0);
		var coreEdge = 1 - Math.Clamp((distance - coreRadius) / edgeWidth, 0, 1);
		if (coreEdge > 0)
		{
			var inner = 1 - Math.Clamp(distance / Math.Max(1, coreRadius), 0, 1);
			var (cr, cg, cb) = palette.Core;
			var mix = Math.Clamp(inner * 1.3, 0, 1);
			red = (red * (1 - mix)) + (cr * mix);
			green = (green * (1 - mix)) + (cg * mix);
			blue = (blue * (1 - mix)) + (cb * mix);
			alpha = Math.Max(alpha, coreEdge * 0.96);
		}

		for (var ring = 0; ring < rings; ring++)
		{
			var radius = (Size * 0.24) + (ring * Size * shape.RingSpacing);
			var width = (1.1 + (ring * 0.25)) * shape.RingWeight * (Size / 96.0);
			var band = Math.Abs(distance - radius);

			if (band > width)
			{
				continue;
			}

			var sweep = (ringPhase * (1.0 + (ring * 0.45))) + (angle * 2.0) - (ring * 1.1) + shape.SegmentsOffset;
			var segments = Math.Cos(sweep * shape.Segments) * 0.5 + 0.5;
			var coverage = (1 - (band / width)) * segments * ringAlpha;

			if (coverage <= 0.002)
			{
				continue;
			}

			var (rr, rg, rb) = palette.Ring;
			red += ((rr - red) * coverage);
			green += ((rg - green) * coverage);
			blue += ((rb - blue) * coverage);
			alpha = Math.Max(alpha, coverage);
		}

		if (state == AssistantState.Error)
		{
			var (er, eg, eb) = palette.Error;
			red = (red * 0.45) + (er * 0.55);
			green = (green * 0.45) + (eg * 0.55);
			blue = (blue * 0.45) + (eb * 0.55);
		}

		return (red, green, blue, Math.Clamp(alpha, 0, 1));
	}

	private static byte ToByte(double value) => (byte)Math.Clamp(value * 255, 0, 255);
}

public readonly record struct OrbPalette((double R, double G, double B) Accent, (double R, double G, double B) Core, (double R, double G, double B) Ring, (double R, double G, double B) Error)
{
	public static OrbPalette Default { get; } = new(
		(0.31, 0.82, 1.00),
		(0.04, 0.24, 0.39),
		(0.87, 0.96, 1.00),
		(1.00, 0.30, 0.30));

	public static OrbPalette From(OrbWidgetData data) => new(
		Parse(data.AccentColor),
		Parse(data.CoreColor),
		(0.87, 0.96, 1.00),
		(1.00, 0.30, 0.30));

	private static (double R, double G, double B) Parse(string hex)
	{
		if (hex.Length != 7 || hex[0] != '#')
		{
			return (0.31, 0.82, 1.00);
		}

		return (
			Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0,
			Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0,
			Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0);
	}
}