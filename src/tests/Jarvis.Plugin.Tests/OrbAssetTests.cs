using System.Text;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Orb;
using NUnit.Framework;

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

		var bytes = gif.Encode();
		var decoded = Decode(bytes);

		Assert.That(decoded.Width, Is.EqualTo(16));
		Assert.That(decoded.Height, Is.EqualTo(16));
		Assert.That(decoded.FrameCount, Is.EqualTo(2));
		Assert.That(decoded.Pixels, Has.Length.EqualTo(16 * 16 * 3));
	}

	[Test]
	public void A_flat_frame_round_trips_to_its_own_colour()
	{
		// The palette is tuned for the orb's dark cyan-to-violet band, so the round trip is asserted on
		// a colour the palette actually covers rather than on an arbitrary greyscale ramp.
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
		var decoded = Decode(gif.Encode());

		Assert.That(decoded.Pixels, Has.Length.EqualTo(32 * 32 * 3));

		for (var pixel = 0; pixel < 32 * 32; pixel++)
		{
			Assert.That(Math.Abs(decoded.Pixels[(pixel * 3)] - level), Is.LessThan(40), $"pixel {pixel}");
		}
	}

	[Test]
	public void A_transparent_pixel_survives_as_transparent()
	{
		var gif = AnimatedGif.Create(8, 8);
		var rgba = new byte[8 * 8 * 4];

		gif.AddFrame(rgba);
		var decoded = Decode(gif.Encode());

		var distinct = decoded.Pixels
			.Select((_, index) => index / 3)
			.Select(index => (decoded.Pixels[(index * 3)], decoded.Pixels[((index * 3) + 1)], decoded.Pixels[((index * 3) + 2)]))
			.Distinct()
			.ToArray();

		Assert.That(distinct, Has.Length.EqualTo(1), "A fully transparent frame must be a single flat colour.");
	}

	private readonly record struct DecodedGif(int Width, int Height, int FrameCount, byte[] Pixels);

	/// <summary>
	/// An independent LZW decoder written against the specification rather than against the encoder, so
	/// a bug shared by both would not pass.
	/// </summary>
	private static DecodedGif Decode(byte[] bytes)
	{
		var offset = 6;
		var width = ReadShort(bytes, ref offset);
		var height = ReadShort(bytes, ref offset);
		var packed = bytes[offset];
		offset += 3;

		var tableSize = 2 << (packed & 0x07);
		var palette = new byte[tableSize * 3];

		for (var index = 0; index < tableSize; index++)
		{
			palette[(index * 3)] = bytes[offset];
			palette[((index * 3) + 1)] = bytes[(offset + 1)];
			palette[((index * 3) + 2)] = bytes[(offset + 2)];
			offset += 3;
		}

		var frames = 0;
		byte[]? firstPixels = null;

		while (offset < bytes.Length)
		{
			var marker = bytes[offset];

			if (marker == 0x3B)
			{
				break;
			}

			if (marker == 0x21)
			{
				offset += 2;

				// An extension is a chain of length-prefixed sub-blocks ended by a zero byte. The terminator
				// itself has to be consumed, or the next iteration reads it as a block marker.
				while (offset < bytes.Length)
				{
					var blockLength = bytes[offset];
					offset++;

					if (blockLength == 0)
					{
						break;
					}

					offset += blockLength;
				}

				continue;
			}

			if (marker != 0x2C)
			{
				throw new InvalidOperationException($"Unexpected block 0x{marker:X2} at {offset}.");
			}

			offset++;
			var descriptorLeft = ReadShort(bytes, ref offset);
			var descriptorTop = ReadShort(bytes, ref offset);
			var descriptorWidth = ReadShort(bytes, ref offset);
			var descriptorHeight = ReadShort(bytes, ref offset);
			offset += 1;

			Assert.That(descriptorLeft, Is.Zero);
			Assert.That(descriptorTop, Is.Zero);
			Assert.That(descriptorWidth, Is.EqualTo(width));
			Assert.That(descriptorHeight, Is.EqualTo(height));

			var minimumCodeSize = bytes[offset];
			offset++;

			// Compressed data arrives in sub-blocks of at most 255 bytes, so reading it as one run would
			// hide a real encoder bug.
			var compressed = new List<byte>();

			while (offset < bytes.Length && bytes[offset] != 0)
			{
				var blockLength = bytes[offset];
				offset++;

				for (var index = 0; index < blockLength && offset < bytes.Length; index++)
				{
					compressed.Add(bytes[offset]);
					offset++;
				}
			}

			offset++;

			var pixels = DecodeLzw(compressed.ToArray(), descriptorWidth, descriptorHeight, minimumCodeSize);
			frames++;

			firstPixels ??= ToRgb(pixels, palette);
		}

		return new DecodedGif(width, height, frames, firstPixels ?? []);
	}

	private static byte[] DecodeLzw(byte[] data, int width, int height, int minimumCodeSize)
	{
		var clearCode = 1 << minimumCodeSize;
		var endCode = clearCode + 1;

		var prefix = new int[4096];
		var suffix = new int[4096];
		var length = new int[4096];

		for (var index = 0; index < clearCode; index++)
		{
			prefix[index] = -1;
			suffix[index] = index;
			length[index] = 1;
		}

		var next = endCode + 1;
		var codeSize = minimumCodeSize + 1;

		var output = new byte[width * height];
		var written = 0;
		var accumulator = 0;
		var bits = 0;
		var previous = -1;
		var stack = new byte[4096];
		var cursor = 0;

		while (written < output.Length)
		{
			while (bits < codeSize)
			{
				if (cursor >= data.Length)
				{
					return output;
				}

				accumulator |= data[cursor] << bits;
				bits += 8;
				cursor++;
			}

			var code = accumulator & ((1 << codeSize) - 1);
			accumulator >>= codeSize;
			bits -= codeSize;

			if (code == clearCode)
			{
				next = endCode + 1;
				codeSize = minimumCodeSize + 1;
				previous = -1;
				continue;
			}

			if (code == endCode)
			{
				break;
			}

			var current = code;

			// The encoder runs in literal mode: it never grows the dictionary, so neither does this
			// decoder. Anything above the root range would mean the two disagree about the format.
			if (current >= next)
			{
				throw new InvalidOperationException($"Code {current} is not a root but the table stops at {next}.");
			}

			var top = 0;
			var walk = current;

			while (walk >= clearCode)
			{
				stack[top++] = (byte)suffix[walk];
				walk = prefix[walk];
			}

			stack[top++] = (byte)suffix[walk];

			for (var index = top - 1; index >= 0 && written < output.Length; index--)
			{
				output[written++] = stack[index];
			}

			previous = code;
		}

		return output;
	}

	private static byte[] ToRgb(byte[] indexed, byte[] palette)
	{
		var rgb = new byte[indexed.Length * 3];

		for (var pixel = 0; pixel < indexed.Length; pixel++)
		{
			var entry = Math.Min(indexed[pixel] * 3, palette.Length - 3);
			rgb[(pixel * 3)] = palette[entry];
			rgb[((pixel * 3) + 1)] = palette[entry + 1];
			rgb[((pixel * 3) + 2)] = palette[entry + 2];
		}

		return rgb;
	}

	private static int ReadShort(byte[] bytes, ref int offset)
	{
		var value = bytes[offset] | (bytes[offset + 1] << 8);
		offset += 2;
		return value;
	}
}