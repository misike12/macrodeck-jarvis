using System.Text;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Orb;
using NUnit.Framework;
using SkiaSharp;

namespace Jarvis.Plugin.Tests;

[TestFixture]
public class OrbAssetTests
{
	[TestCase(AssistantState.Idle)]
	[TestCase(AssistantState.Listening)]
	[TestCase(AssistantState.Thinking)]
	[TestCase(AssistantState.Speaking)]
	[TestCase(AssistantState.Executing)]
	[TestCase(AssistantState.Confirming)]
	[TestCase(AssistantState.Error)]
	public void Every_state_produces_a_gif_under_the_resource_limit(AssistantState state)
	{
		var gif = AnimatedGif.Create(OrbFrameRenderer.Size, OrbFrameRenderer.Size);
		var palette = OrbPalette.Default;

		for (var frame = 0; frame < 8; frame++)
		{
			gif.AddFrame(OrbFrameRenderer.Render(state, frame / 8.0 * Math.Tau, palette, 0.2));
		}

		var bytes = gif.Encode();

		Assert.That(bytes, Has.Length.GreaterThan(64));
		Assert.That(bytes.Length, Is.LessThan(2 * 1024 * 1024), "A UiResource is capped at 2 MiB.");
		Assert.That(Encoding.ASCII.GetString(bytes, 0, 6), Is.EqualTo("GIF89a"));
		Assert.That(bytes[^1], Is.EqualTo(0x3B), "A GIF ends with the trailer byte.");
	}

	[Test]
	public void The_stream_is_decoded_by_an_independent_decoder()
	{
		var gif = AnimatedGif.Create(16, 16);
		var frame = new byte[16 * 16 * 4];

		for (var pixel = 0; pixel < 16 * 16; pixel++)
		{
			frame[(pixel * 4)] = (byte)(pixel * 7);
			frame[((pixel * 4) + 1)] = 64;
			frame[((pixel * 4) + 2)] = (byte)(255 - pixel);
			frame[((pixel * 4) + 3)] = 255;
		}

		gif.AddFrame(frame);
		gif.AddFrame(frame);

		using var codec = Open(gif.Encode());

		Assert.Multiple(() =>
		{
			Assert.That(codec.Info.Width, Is.EqualTo(16));
			Assert.That(codec.Info.Height, Is.EqualTo(16));
			Assert.That(codec.FrameCount, Is.EqualTo(2));
		});
	}

	[Test]
	public void A_flat_frame_round_trips_to_its_own_colour()
	{
		// The palette is tuned for the orb's dark cyan-to-violet band, so the round trip is asserted on a
		// colour the palette actually covers rather than on an arbitrary greyscale ramp.
		const byte level = 140;

		var gif = AnimatedGif.Create(32, 32);
		var rgba = new byte[32 * 32 * 4];

		for (var pixel = 0; pixel < 32 * 32; pixel++)
		{
			rgba[(pixel * 4)] = level;
			rgba[((pixel * 4) + 1)] = level;
			rgba[((pixel * 4) + 2)] = level;
			rgba[((pixel * 4) + 3)] = 255;
		}

		gif.AddFrame(rgba);

		var pixels = DecodeFirstFrame(gif.Encode());

		Assert.That(pixels, Has.Length.EqualTo(32 * 32));

		for (var pixel = 0; pixel < 32 * 32; pixel++)
		{
			Assert.That(Math.Abs(pixels[pixel].Red - level), Is.LessThan(60), $"pixel {pixel}");
		}
	}

	[Test]
	public void A_transparent_pixel_survives_as_transparent()
	{
		var gif = AnimatedGif.Create(8, 8);
		var rgba = new byte[8 * 8 * 4];

		gif.AddFrame(rgba);

		var pixels = DecodeFirstFrame(gif.Encode());
		var lit = pixels.Count(colour => colour.Alpha > 128);

		Assert.That(lit, Is.Zero, "a fully transparent frame must not decode to anything opaque");
	}

	/// <summary>
	/// Decoded with SkiaSharp rather than with an LZW decoder written alongside the encoder.
	/// <para>
	/// There was one, and its comment claimed that being written against the specification rather than against
	/// the encoder meant a bug shared by both could not pass. It shared the encoder's own misunderstanding of
	/// when the code width grows, so the two agreed with each other and disagreed with every real decoder,
	/// which is how a corrupt orb survived a test suite that appeared to cover exactly this. A second
	/// implementation by the same author is not an independent check of anything.
	/// </para>
	/// </summary>
	private static SKCodec Open(byte[] gif) =>
		SKCodec.Create(SKData.CreateCopy(gif))
			?? throw new InvalidOperationException("SkiaSharp rejected the bytes outright.");

	private static SKColor[] DecodeFirstFrame(byte[] gif)
	{
		using var codec = Open(gif);

		var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
		var raw = new byte[info.BytesPerPixel * info.Width * info.Height];
		var result = codec.GetPixels(info, raw);

		Assert.That(result, Is.EqualTo(SKCodecResult.Success), $"the frame did not decode: {result}");

		var pixels = new SKColor[info.Width * info.Height];

		for (var index = 0; index < pixels.Length; index++)
		{
			var offset = index * 4;
			pixels[index] = new SKColor(raw[offset], raw[offset + 1], raw[offset + 2], raw[offset + 3]);
		}

		return pixels;
	}
}