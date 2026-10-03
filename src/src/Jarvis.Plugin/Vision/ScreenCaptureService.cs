using System.Drawing;
using System.Drawing.Imaging;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Serilog;

namespace Jarvis.Plugin.Vision;

/// <summary>Which screen to photograph.</summary>
public enum CaptureTarget
{
	/// <summary>The whole virtual desktop, all monitors laid out as Windows sees them.</summary>
	AllScreens,

	/// <summary>Just the primary monitor.</summary>
	PrimaryScreen,
}

/// <summary>
/// A screenshot, held as JPEG.
/// <para>
/// JPEG rather than PNG because this is about to be sent to a vision model over a network: a screenshot is
/// mostly flat colour and compresses several times smaller with almost no visible loss, and the payload
/// goes out as base64 in a request body. The bytes never touch disk.
/// </para>
/// </summary>
public sealed record ScreenCapture(byte[] Jpeg, int Width, int Height)
{
	private const string JpegMediaType = "image/jpeg";

	public static string MediaType => JpegMediaType;

	/// <summary>
	/// A data URI, which is the form the OpenAI-compatible vision endpoint expects. Base64 is wasteful by
	/// a third, but it is what the API takes, and this is the only place that has to know that.
	/// </summary>
	public string ToDataUri() => $"data:{JpegMediaType};base64,{Convert.ToBase64String(Jpeg)}";
}

/// <summary>
/// Photographs the screen with GDI.
/// <para>
/// Hand-rolled rather than taken from a screenshot library because the whole of it is a few dozen lines of
/// well-documented Win32 and a library would be a dependency to do exactly this. The awkward part is the
/// virtual screen origin: on a multi-monitor arrangement the desktop's top-left is not (0,0) and is often
/// negative, and capturing from (0,0) yields a black rectangle with the real image pushed off the edge.
/// </para>
/// <para>
/// Nothing is written to disk. A screenshot is a picture of everything the user can see, including whatever
/// is in another window, so it exists only in memory and only when something asked for it.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScreenCaptureService
{
	/// <summary>
	/// Above the VLM's practical input size but below the point where text on screen becomes unreadable.
	/// Capturing at native resolution on a 4K display produces a payload most endpoints will refuse.
	/// </summary>
	private const int MaxEdge = 1920;

	/// <summary>Eighty-five. Enough to read ordinary UI text, small enough to keep the request quick.</summary>
	private const int JpegQuality = 85;

	public static ScreenCapture Capture(CaptureTarget target, ILogger logger)
	{
		var bounds = VirtualScreenBounds();

		if (target == CaptureTarget.PrimaryScreen)
		{
			var primary = NativeMethods.GetPrimaryMonitorBounds();
			bounds = primary;
		}

		var width = bounds.Width;
		var height = bounds.Height;

		if (width <= 0 || height <= 0)
		{
			throw new InvalidOperationException("The screen reported no usable area to capture.");
		}

		// Scale down by the larger ratio, so the aspect ratio is preserved and text stays legible.
		var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(width, height));
		var targetWidth = Math.Max(1, (int)(width * scale));
		var targetHeight = Math.Max(1, (int)(height * scale));

		using var bitmap = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
		using var graphics = Graphics.FromImage(bitmap);

		graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
		graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

		// The source rectangle is in physical desktop coordinates, which is exactly what CopyFromScreen
		// expects once it is offset by the virtual origin.
		graphics.CopyFromScreen(
			bounds.X,
			bounds.Y,
			0,
			0,
			new Size(targetWidth, targetHeight),
			CopyPixelOperation.SourceCopy);

		using var jpeg = new MemoryStream();

		// Quality has to be set through encoder parameters; the overload that takes a bare quality number
		// only exists for a handful of formats and not for JPEG.
		var quality = new EncoderParameters(1);

		quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)JpegQuality);

		bitmap.Save(jpeg, FindJpegCodec() ?? throw new InvalidOperationException("No JPEG encoder is available."), quality);

		logger.Information("Captured {Width}x{Height} from ({X},{Y}).", targetWidth, targetHeight, bounds.X, bounds.Y);

		return new ScreenCapture(jpeg.ToArray(), targetWidth, targetHeight);
	}

	private static Rectangle VirtualScreenBounds()
	{
// The virtual screen is the union of all monitors and can start at a negative coordinate.
		var left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
		var top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
		var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
		var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

		return new Rectangle(left, top, width, height);
	}

	private static ImageCodecInfo? FindJpegCodec()
	{
		foreach (var codec in ImageCodecInfo.GetImageEncoders())
		{
			if (string.Equals(codec.FormatDescription, "jpeg", StringComparison.OrdinalIgnoreCase))
			{
				return codec;
			}
		}

		return null;
	}

	private static class NativeMethods
	{
		internal const int SM_XVIRTUALSCREEN = 76;
		internal const int SM_YVIRTUALSCREEN = 77;
		internal const int SM_CXVIRTUALSCREEN = 78;
		internal const int SM_CYVIRTUALSCREEN = 79;

		[DllImport("user32.dll")]
		internal static extern int GetSystemMetrics(int index);

		[DllImport("user32.dll")]
		internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

/// <summary>
	/// The primary monitor's bounds. The primary handle is a documented constant, so this avoids enumerating
	/// every monitor just to find the one the desktop starts from. The virtual screen is the fallback, which
	/// is right on a single-monitor machine and merely generous on a multi-monitor one.
	/// </summary>
	internal static Rectangle GetPrimaryMonitorBounds()
	{
		var info = new NativeMethods.MonitorInfo
		{
			Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>(),
		};

		return NativeMethods.GetMonitorInfo(new nint(1), ref info)
			? Rectangle.FromLTRB(info.Left, info.Top, info.Right, info.Bottom)
			: VirtualScreenBounds();
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct MonitorInfo
	{
		public uint Size;

		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}
}
}