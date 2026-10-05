using System.Runtime.InteropServices;
using Jarvis.Plugin.Orb;
using NUnit.Framework;
using SkiaSharp;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The orb's encoder, checked against a decoder that was not written here.
/// <para>
/// The orb is a hand-written GIF89a because a plugin that ships one animated image does not justify a
/// dependency, and <c>image/gif</c> is one of the four media types a plugin may register. The cost of writing
/// an encoder is that it has to be right, and the thing that is easy to get wrong is LZW: the decoder grows
/// its own dictionary as it reads, so an encoder that emits bare literals and never grows desynchronises
/// from it within the first few hundred codes and the image decodes as noise, or not at all.
/// </para>
/// <para>
/// Nothing else could have caught that. The encoder's own tests compare its output with its own output.
/// These decode the bytes with SkiaSharp, which is what the browser's decoder has in common with them.
/// </para>
/// </summary>
[TestFixture]
public class AnimatedGifTests
{
	private const int Size = 96;

	private static byte[] Encoded(int frames)
	{
		var gif = AnimatedGif.Create(Size, Size);
		var frame = new byte[Size * Size * 4];

		for (var index = 0; index < frames; index++)
		{
			// A pattern that shifts per frame, so a decoder that loses sync part way through produces later
			// frames that are visibly wrong rather than merely blank.
			for (var pixel = 0; pixel < Size * Size; pixel++)
			{
				var offset = pixel * 4;
				var lit = ((pixel + (index * 37)) % 23) < 12;

				frame[offset] = lit ? (byte)(40 + (index * 3)) : (byte)4;
				frame[offset + 1] = lit ? (byte)200 : (byte)6;
				frame[offset + 2] = lit ? (byte)230 : (byte)10;
				frame[offset + 3] = lit ? (byte)255 : (byte)0;
			}

			gif.AddFrame(frame);
		}

		return gif.Encode();
	}

	private static SKCodec Open(int frames) =>
		SKCodec.Create(SKData.CreateCopy(Encoded(frames)))
			?? throw new InvalidOperationException("SkiaSharp refused the bytes outright.");

private static SKColor[] DecodeFrameManaged(SKCodec codec, int frame)
	{
		var info = new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
		var stride = info.BytesPerPixel * Size;
		var raw = new byte[stride * Size];

		if (frame == 0)
		{
			// The simplest call the decoder offers, so a failure here is the file rather than the options.
			var result = codec.GetPixels(info, raw);
			Assert.That(result, Is.EqualTo(SKCodecResult.Success), $"frame 0 did not decode: {result}");
		}
		else
		{
			var buffer = Marshal.AllocHGlobal(raw.Length);

			try
			{
				var result = codec.GetPixels(
					info,
					buffer,
					stride,
					new SKCodecOptions { FrameIndex = frame, PriorFrame = frame - 1 });

				Assert.That(result, Is.EqualTo(SKCodecResult.Success), $"frame {frame} did not decode: {result}");

				Marshal.Copy(buffer, raw, 0, raw.Length);
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
			}
		}

		var pixels = new SKColor[Size * Size];

		for (var index = 0; index < pixels.Length; index++)
		{
			var offset = index * 4;
			pixels[index] = new SKColor(raw[offset], raw[offset + 1], raw[offset + 2], raw[offset + 3]);
		}

		return pixels;
	}

	/// <summary>
	/// The file has to carry the looping extension with a count of zero.
	/// <para>
	/// Eighteen frames at eight hundredths of a second is a loop of about a second and a half, and the orb
	/// went still after exactly that, every time. The extension was simply never written, and a file without it
	/// is specified as playing once.
	/// </para>
	/// <para>
	/// Asserted on the bytes rather than on what a decoder reports for them. SkiaSharp answers -1 for a count
	/// of zero, which is its own encoding of "forever" and is not a value this file can be checked against
	/// without depending on that choice. The bytes are the contract.
	/// </para>
	/// </summary>
	[Test]
	public void The_file_says_it_loops_forever()
	{
		var bytes = Encoded(3);

		// 0x21 0xFF 0x0b "NETSCAPE2.0" 0x03 0x01 <count:2> 0x00, sitting between the palette and the first
		// frame, which is where a reader looks for it. The signature is three bytes and the name eleven, so
		// the sub-block size lands at at+14.
		var signature = new byte[] { 0x21, 0xFF, 0x0B };
		var at = IndexOf(bytes, signature);

		Assert.That(at, Is.GreaterThanOrEqualTo(0), "the looping extension is absent, so the file plays once");

		Assert.That(
			System.Text.Encoding.ASCII.GetString(bytes, at + 3, 11),
			Is.EqualTo("NETSCAPE2.0"),
			"the extension is present but is not the looping one");

		Assert.That(bytes[at + 14], Is.EqualTo(0x03), "the sub-block is the wrong size");
		Assert.That(bytes[at + 15], Is.EqualTo(0x01), "the sub-block is not the loop count");
		Assert.That(bytes[at + 16], Is.EqualTo(0), "the loop count is not zero, so the animation stops");
		Assert.That(bytes[at + 17], Is.EqualTo(0), "the loop count is not zero, so the animation stops");
	}

	/// <summary>
	/// Each frame has to be restored to the background before the next one is drawn.
	/// <para>
	/// With "do not dispose" a frame is composited on top of the previous one, and because these frames are
	/// transparent outside the orb, anything a ring had covered and no longer did was left showing: the rings
	/// left trails and the loop did not return to its own first frame.
	/// </para>
	/// </summary>
	[Test]
	public void Each_frame_is_restored_before_the_next()
	{
		var bytes = Encoded(3);

		// The graphic control extension is nine bytes and ends on the image descriptor, so the pair is the anchor.
		// Searching for either byte alone finds both inside compressed pixel data, and the four bytes between
		// the packed field and the terminator are the delay and the transparent index, which are not fixed.
		var at = IndexOf(
			bytes,
			new int?[] { 0x21, 0xF9, 0x04, null, null, null, null, 0x00, 0x2C });

		Assert.That(
			at,
			Is.GreaterThanOrEqualTo(0),
			$"no graphic control extension found; first bytes were {BitConverter.ToString(bytes, 0, 16)}");

		var disposal = (bytes[at + 3] >> 2) & 0x07;

		Assert.That(
			disposal,
			Is.EqualTo(2),
			$"the disposal method is {disposal} at offset {at}, so the previous frame is left in place and the rings trail across the loop");
	}

	/// <summary>
	/// Finds a byte pattern where null entries are wildcards.
	/// </summary>
	private static int IndexOf(byte[] haystack, int?[] needle)
	{
		for (var index = 0; index <= haystack.Length - needle.Length; index++)
		{
			var match = true;

			for (var offset = 0; offset < needle.Length; offset++)
			{
				if (needle[offset] is int expected && haystack[index + offset] != expected)
				{
					match = false;
					break;
				}
			}

			if (match)
			{
				return index;
			}
		}

		return -1;
	}

	private static int IndexOf(byte[] haystack, byte[] needle)
	{
		var wildcard = new int?[needle.Length];

		for (var index = 0; index < needle.Length; index++)
		{
			wildcard[index] = needle[index];
		}

		return IndexOf(haystack, wildcard);
	}

	/// <summary>The bytes have to be a GIF a real decoder accepts, carrying every frame that was added.</summary>
	[Test]
	public void A_real_decoder_reads_back_every_frame()
	{
		using var codec = Open(frames: 18);

		Assert.That(codec.EncodedFormat, Is.EqualTo(SKEncodedImageFormat.Gif));
		Assert.That(
			codec.FrameCount,
			Is.EqualTo(18),
			$"the file carries {codec.FrameCount} frames, so the animation is truncated");
	}

	/// <summary>
	/// The declared size is what the host lays the image out by, so a wrong one is a wrong widget.
	/// </summary>
	[Test]
	public void The_decoded_image_is_the_size_that_was_asked_for()
	{
		using var codec = Open(frames: 3);

		Assert.Multiple(() =>
		{
			Assert.That(codec.Info.Width, Is.EqualTo(Size));
			Assert.That(codec.Info.Height, Is.EqualTo(Size));
		});
	}

	/// <summary>
	/// One frame of a 96 pixel square is 9216 codes, far past the 511 that fit in the initial nine bits. This
	/// is the case a literal-only encoder fails, and it fails silently: the file is still a structurally
	/// valid GIF, so nothing complains and the picture is simply not there.
	/// </summary>
	[Test]
	public void A_frame_longer_than_the_first_code_width_still_decodes_to_pixels()
	{
		using var codec = Open(frames: 1);

		var pixels = DecodeFrameManaged(codec, 0);
		var lit = pixels.Count(colour => colour.Alpha > 128);

		Assert.That(
			lit,
			Is.GreaterThan((Size * Size) / 4),
			$"only {lit} of {Size * Size} pixels came back lit, so the image data did not survive decoding");
	}

	/// <summary>
/// A frame short enough that the code width never has to widen: 64 codes against a first width of 511.
/// <para>
/// This is the bisect that separates the two possible faults. If this decodes and the 96 pixel frame does
/// not, the framing is sound and the fault is in when the code width grows. If this also fails, the fault is
/// in the file structure and the width schedule was never the problem.
/// </para>
/// </summary>
	[Test]
	public void A_frame_that_never_widens_decodes()
	{
		const int small = 8;

		var gif = AnimatedGif.Create(small, small);
		var frame = new byte[small * small * 4];

		for (var pixel = 0; pixel < small * small; pixel++)
		{
			var offset = pixel * 4;
			frame[offset] = 200;
			frame[offset + 1] = 40;
			frame[offset + 2] = 90;
			frame[offset + 3] = 255;
		}

		gif.AddFrame(frame);

		using var codec = SKCodec.Create(SKData.CreateCopy(gif.Encode()));

		Assert.That(codec, Is.Not.Null, "even a tiny GIF was rejected outright");

		var info = new SKImageInfo(small, small, SKColorType.Rgba8888, SKAlphaType.Unpremul);
		var result = codec.GetPixels(info, new byte[info.BytesPerPixel * small * small]);

		Assert.That(result, Is.EqualTo(SKCodecResult.Success), $"a frame that never widens failed to decode: {result}");
	}

	/// <summary>Frames must differ from each other, or the animation is one picture repeated.</summary>
	[Test]
	public void Frames_decode_to_different_pictures()
	{
		using var codec = Open(frames: 6);

		var first = DecodeFrameManaged(codec, 0);
		var last = DecodeFrameManaged(codec, 5);

		Assert.That(
			first.Where((colour, index) => colour != last[index]).Any(),
			Is.True,
			"the frames decoded identically, so the animation carries no movement");
	}
}