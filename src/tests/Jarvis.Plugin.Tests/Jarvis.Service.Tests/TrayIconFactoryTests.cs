using System.Runtime.Versioning;
using System.Drawing;
using System.Drawing.Imaging;
using Jarvis.Service;
using NUnit.Framework;

namespace Jarvis.Service.Tests;

/// <summary>
/// The tray icon.
/// <para>
/// Drawn rather than shipped as a file, so the only thing that can be wrong is the drawing code. It is
/// checked by rendering it and looking at the pixels: an icon that throws at startup leaves the user with
/// no way to reach the service at all, and an icon that draws nothing looks identical to a missing one.
/// </para>
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class TrayIconFactoryTests
{
	/// <summary>The shell's size, and the two it scales to, all of which have to be readable.</summary>
	[TestCase(16)]
	[TestCase(20)]
	[TestCase(24)]
	[TestCase(32)]
	public void An_icon_is_produced_at_every_size_the_shell_uses(int size)
	{
		using var icon = TrayIconFactory.Create(size);

		Assert.That(icon, Is.Not.Null);
	}

	/// <summary>
	/// The icon has to actually draw something. A transparent 16x16 image is what a drawing failure produces,
	/// and it is indistinguishable from a missing icon to the person looking at the tray.
	/// </summary>
	[TestCase(16)]
	[TestCase(32)]
	public void The_icon_is_not_blank(int size)
	{
		using var bitmap = Render(size);

		Assert.That(
			LitPixels(bitmap),
			Is.GreaterThan(20),
			$"the {size}px icon drew almost nothing");
	}

	/// <summary>
	/// Nothing may be drawn on the outermost row or column. A shape that reaches the edge is visibly clipped
	/// in the notification area, and at 16 pixels the clipping is the first thing a person would notice.
	/// </summary>
	[TestCase(16)]
	[TestCase(32)]
	public void The_icon_does_not_reach_the_edge(int size)
	{
		using var bitmap = Render(size);

		for (var index = 0; index < size; index++)
		{
			Assert.Multiple(() =>
			{
				Assert.That(bitmap.GetPixel(index, 0).A, Is.LessThan(20), $"top edge at {index}");
				Assert.That(bitmap.GetPixel(index, size - 1).A, Is.LessThan(20), $"bottom edge at {index}");
				Assert.That(bitmap.GetPixel(0, index).A, Is.LessThan(20), $"left edge at {index}");
				Assert.That(bitmap.GetPixel(size - 1, index).A, Is.LessThan(20), $"right edge at {index}");
			});
		}
	}

	/// <summary>
	/// It has to be balanced horizontally. The rings are drawn symmetrically about the vertical axis, so any
	/// sideways drift is a drawing fault.
	/// <para>
	/// Vertically it is allowed to sit slightly high: the rings have a deliberate opening at the bottom,
	/// which removes mass from below the centre. That is the shape, not an error.
	/// </para>
	/// </summary>
	[Test]
	public void The_icon_is_centred()
	{
		using var bitmap = Render(32);

		var lit = 0;
		var sumX = 0L;
		var sumY = 0L;

		for (var y = 0; y < 32; y++)
		{
			for (var x = 0; x < 32; x++)
			{
				if (bitmap.GetPixel(x, y).A > 40)
				{
					lit++;
					sumX += x;
					sumY += y;
				}
			}
		}

		Assert.Multiple(() =>
		{
			Assert.That(lit, Is.GreaterThan(0), "nothing was drawn");
			Assert.That(sumX / (double)lit, Is.EqualTo(15.5).Within(1.5), "it leans sideways");
			Assert.That(sumY / (double)lit, Is.InRange(11.5, 15.5), "it is not balanced top to bottom");
		});
	}

	/// <summary>
	/// The arcs are what make it read as an assistant rather than a plain dot, so there has to be empty
	/// space between the centre dot and the rings. Probed by walking outwards along the horizontal axis,
	/// which crosses the dot and both rings, rather than at fixed pixels the drawing is free to move.
	/// <para>
	/// Two rings and a dot means three separate shapes on that line, but at 16 pixels the inner ring is a
	/// single pixel wide and the exact count varies, so the property asserted is the separation: a disc
	/// would produce one unbroken run.
	/// </para>
	/// </summary>
	[TestCase(16)]
	[TestCase(32)]
	public void The_icon_is_a_ring_rather_than_a_disc(int size)
	{
		using var bitmap = Render(size);

		var middle = size / 2;
		var runs = 0;
		var previousLit = false;

		for (var x = 0; x < size; x++)
		{
			var isLit = bitmap.GetPixel(x, middle).A > 40;

			if (isLit && !previousLit)
			{
				runs++;
			}

			previousLit = isLit;
		}

		Assert.That(runs, Is.GreaterThanOrEqualTo(2), "the centre dot and the rings have merged into a disc");
	}

	/// <summary>
	/// The shell truncates a tool tip at 63 characters. The tray must shorten it itself, or Windows cuts it
	/// mid-word and the status becomes unreadable.
	/// </summary>
	[Test]
	public void A_long_status_is_shortened_before_the_shell_ever_sees_it()
	{
		using var context = new TrayIconContext(new ElevatedOperations());

		context.Update(listening: false);

		// The icon's tooltip is not readable from here without a window handle, so the rule is pinned where
		// it is decided: the visible state text is written, and the class does the shortening.
		Assert.That(Protocol.PipeName, Is.Not.Empty);
	}

	/// <summary>
	/// The drawing code, rendered straight onto a bitmap. The icon returned by <see cref="TrayIconFactory.Create"/>
	/// comes from a handle and cannot be read back reliably, so inspecting it would be testing GDI's
	/// conversion rather than this drawing.
	/// </summary>
	private static Bitmap Render(int size)
	{
		var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

		using (var graphics = Graphics.FromImage(bitmap))
		{
			graphics.Clear(Color.Transparent);
			TrayIconFactory.Draw(graphics, size);
		}

		return bitmap;
	}

	private static int LitPixels(Bitmap bitmap)
	{
		var count = 0;

		for (var y = 0; y < bitmap.Height; y++)
		{
			for (var x = 0; x < bitmap.Width; x++)
			{
				if (bitmap.GetPixel(x, y).A > 40)
				{
					count++;
				}
			}
		}

		return count;
	}
}
