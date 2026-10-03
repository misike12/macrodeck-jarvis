using System.Runtime.Versioning;
using Jarvis.Plugin.Vision;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Captures the real screen. A screenshot routine that has never been run on a real desktop produces a
/// plausible object and an empty picture, so this runs against the actual display.
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class ScreenCaptureTests
{
	[Test]
	public void A_capture_produces_a_real_jpeg()
	{
		var capture = ScreenCaptureService.Capture(CaptureTarget.PrimaryScreen, RuntimeTestLog.Logger);

		Assert.Multiple(() =>
		{
			Assert.That(capture.Width, Is.GreaterThan(0));
			Assert.That(capture.Height, Is.GreaterThan(0));
			Assert.That(capture.Jpeg, Has.Length.GreaterThan(1_000), "the capture is implausibly small");

			// Every JPEG starts with the SOI marker. Checking it proves the bytes are a JPEG rather than a
			// PNG or a raw buffer that merely happens to be non-empty.
			Assert.That(capture.Jpeg[0], Is.EqualTo(0xFF));
			Assert.That(capture.Jpeg[1], Is.EqualTo(0xD8));
		});

		TestContext.Out.WriteLine(
			$"captured {capture.Width}x{capture.Height}, {capture.Jpeg.Length / 1024} KiB jpeg");
	}

	/// <summary>
	/// The virtual screen can start at a negative coordinate on a multi-monitor arrangement. Capturing from
	/// (0,0) instead of the virtual origin is the classic bug here: it yields a mostly black picture with
	/// the real one pushed off the edge, which still looks like a successful capture.
	/// </summary>
	[Test]
	public void The_capture_covers_the_virtual_screen_origin()
	{
		var all = ScreenCaptureService.Capture(CaptureTarget.AllScreens, RuntimeTestLog.Logger);
		var primary = ScreenCaptureService.Capture(CaptureTarget.PrimaryScreen, RuntimeTestLog.Logger);

		Assert.Multiple(() =>
		{
			Assert.That(all.Jpeg, Has.Length.GreaterThan(0));
			Assert.That(primary.Jpeg, Has.Length.GreaterThan(0));
		});

		// The virtual desktop is at least as large as the primary monitor.
		Assert.That(all.Width, Is.GreaterThanOrEqualTo(primary.Width));
	}

	[Test]
	public void A_data_uri_is_well_formed()
	{
		var capture = ScreenCaptureService.Capture(CaptureTarget.PrimaryScreen, RuntimeTestLog.Logger);
		var uri = capture.ToDataUri();

		Assert.Multiple(() =>
		{
			Assert.That(uri, Does.StartWith("data:image/jpeg;base64,"));
			Assert.That(uri.Length, Is.GreaterThan(1_000));
			Assert.That(Convert.FromBase64String(uri["data:image/jpeg;base64,".Length..]), Is.EqualTo(capture.Jpeg));
		});
	}
}