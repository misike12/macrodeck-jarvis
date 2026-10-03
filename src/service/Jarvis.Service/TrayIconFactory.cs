using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace Jarvis.Service;

/// <summary>
/// Draws the tray icon rather than shipping a file for it.
/// <para>
/// A 16x16 icon has to be readable at that size, which rules out anything with fine detail. Three
/// concentric arcs of decreasing weight read as a voice assistant at 16 pixels and survive being scaled
/// by the shell to 20 or 24.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayIconFactory
{
	private static readonly Color Accent = Color.FromArgb(0xFF, 0x4F, 0xD1, 0xFF);

	private static readonly Color Centre = Color.FromArgb(0xFF, 0xE8, 0xFA, 0xFF);

	/// <summary>Draws the icon at the shell's size, which is what the notification area expects.</summary>
	public static Icon Create(int size = 16)
	{
		using var bitmap = new Bitmap(size, size);
		using var graphics = Graphics.FromImage(bitmap);

		graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
		graphics.Clear(Color.Transparent);

		Draw(graphics, size);

		return Icon.FromHandle(bitmap.GetHicon());
	}

	/// <summary>
	/// The drawing itself, separated from the icon handle so it can be rendered onto a bitmap and inspected.
	/// <para>
	/// Public only so the tests can reach it. An icon built from a handle cannot be read back reliably, so
	/// testing through <see cref="Create"/> would be testing GDI's handle conversion rather than this
	/// drawing code.
	/// </para>
	/// </summary>
	public static void Draw(Graphics graphics, int size)
	{
		graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

		var centre = size / 2.0;
		var unit = size / 16.0;

		// Outer ring, a gap, then an inner ring: the gaps are what make it read as rings rather than as a
		// filled circle at small sizes. Both radii leave a pixel of margin, because a shape that reaches the
		// edge of the bitmap is visibly clipped in the notification area.
		DrawRing(graphics, centre, centre, 5.8 * unit, 1.15 * unit);
		DrawRing(graphics, centre, centre, 3.8 * unit, 1.0 * unit);

		// The centre dot is the part that survives being scaled down to 16 from 32.
		using var dot = new SolidBrush(Centre);
		graphics.FillEllipse(
			dot,
			(float)(centre - (1.4 * unit)),
			(float)(centre - (1.4 * unit)),
			(float)(2.8 * unit),
			(float)(2.8 * unit));
	}

	/// <summary>
	/// How much of each ring's circumference the gap takes, in degrees.
	/// <para>
	/// Small on purpose. A wide gap stops the ring reading as a ring at 16 pixels, and it shifts the shape's
	/// middle of mass upwards far enough that the icon looks like it sits high in the tray.
	/// </para>
	/// </summary>
	private const double GapDegrees = 70;

	private static void DrawRing(Graphics graphics, double centreX, double centreY, double radius, double width)
	{
		// GDI measures clockwise from east with y pointing down, so 90 degrees is the bottom. Starting a
		// third of the gap past that puts the gap squarely at the bottom, which is what keeps the whole
		// shape symmetric about its centre. An off-centre gap shifts the shape's middle of mass and the
		// icon reads as leaning in the tray.
		var start = 90 + (GapDegrees / 2);

		using var pen = new Pen(Accent, (float)width)
		{
			StartCap = System.Drawing.Drawing2D.LineCap.Round,
			EndCap = System.Drawing.Drawing2D.LineCap.Round,
		};

		graphics.DrawArc(
			pen,
			(float)(centreX - radius),
			(float)(centreY - radius),
			(float)(radius * 2),
			(float)(radius * 2),
			(float)start,
			(float)(360 - GapDegrees));
	}
}
