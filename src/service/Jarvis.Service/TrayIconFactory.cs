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

		var centre = size / 2.0;
		var unit = size / 16.0;

		// Outer ring, a gap, then an inner ring: the gaps are what make it read as rings rather than as a
		// filled circle at small sizes.
		DrawRing(graphics, centre, centre, 7.0 * unit, 1.15 * unit);
		DrawRing(graphics, centre, centre, 4.6 * unit, 1.0 * unit);

		// The centre dot is the part that survives being scaled down to 16 from 32.
		using var dot = new SolidBrush(Centre);
		graphics.FillEllipse(dot, (float)(centre - (1.5 * unit)), (float)(centre - (1.5 * unit)), (float)(3.0 * unit), (float)(3.0 * unit));

		return Icon.FromHandle(bitmap.GetHicon());
	}

	private static void DrawRing(Graphics graphics, double centreX, double centreY, double radius, double width)
	{
		// Drawn as an arc rather than a full circle so the icon has a direction to it, which is what stops
		// it reading as a plain dot at notification-area sizes.
		using var pen = new Pen(Accent, (float)width)
		{
			StartCap = System.Drawing.Drawing2D.LineCap.Round,
			EndCap = System.Drawing.Drawing2D.LineCap.Round,
		};

		graphics.DrawArc(pen, (float)(centreX - radius), (float)(centreY - radius), (float)(radius * 2), (float)(radius * 2), 205, 230);
	}
}
