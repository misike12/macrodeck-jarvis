namespace Jarvis.Plugin.Orb;

/// <summary>
/// A minimal GIF89a encoder, written here rather than taken from a package: a plugin that ships one
/// animated orb does not justify a dependency, and <c>image/gif</c> is one of the four media types
/// <c>UiResourceRules</c> admits from a plugin.
/// </summary>
public sealed class AnimatedGif
{
	private const int MinimumCodeSize = 8;
	private const int ClearCode = 1 << MinimumCodeSize;
	private const int EndCode = ClearCode + 1;
	private const int TransparentIndex = 1;
	private const int FirstColour = 2;
	private const int PaletteSize = 256;

	/// <summary>Hundredths of a second between frames; the format cannot express anything finer.</summary>
	public const int DelayHundredths = 8;

	private static readonly byte[] Palette = BuildPalette();
	private static readonly Dictionary<int, byte> QuantisationCache = [];

	private readonly int _width;
	private readonly int _height;
	private readonly List<byte[]> _frames = [];
	private readonly List<int> _delays = [];

	private AnimatedGif(int width, int height)
	{
		_width = width;
		_height = height;
	}

	public int FrameCount => _frames.Count;

	public static AnimatedGif Create(int width, int height)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
		ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

		return new AnimatedGif(width, height);
	}

	/// <summary>
	/// Appends a frame from straight RGBA. A pixel below half opacity becomes the transparent index, so
	/// the orb composites over whatever the deck draws behind it rather than over a black box.
	/// </summary>
	public void AddFrame(ReadOnlySpan<byte> rgba, int delayHundredths = DelayHundredths)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(delayHundredths, 0);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(delayHundredths, 255);

		if (rgba.Length != _width * _height * 4)
		{
			throw new ArgumentException("The frame buffer does not match the declared size.", nameof(rgba));
		}

		var indexed = new byte[_width * _height];

		for (var pixel = 0; pixel < indexed.Length; pixel++)
		{
			var offset = pixel * 4;

			indexed[pixel] = rgba[offset + 3] < 128
				? (byte)TransparentIndex
				: Nearest(rgba[offset], rgba[offset + 1], rgba[offset + 2]);
		}

		_frames.Add(indexed);
		_delays.Add(delayHundredths);
	}

	public byte[] Encode()
	{
		using var output = new MemoryStream();

		WriteAscii(output, "GIF89a");
		WriteShort(output, _width);
		WriteShort(output, _height);
		output.WriteByte(0xF7);
		output.WriteByte(0);
		output.WriteByte(0);

		for (var index = 0; index < PaletteSize; index++)
		{
			output.WriteByte(Palette[index * 3]);
			output.WriteByte((Palette[(index * 3) + 1]));
			output.WriteByte(Palette[(index * 3) + 2]);
		}

		for (var frame = 0; frame < _frames.Count; frame++)
		{
			WriteGraphicControlExtension(output, _delays[frame]);
			WriteImageDescriptor(output);
			output.WriteByte(MinimumCodeSize);
			WriteLzw(output, _frames[frame]);
		}

		output.WriteByte(0x3B);
		return output.ToArray();
	}

	private static void WriteGraphicControlExtension(Stream stream, int delay)
	{
		stream.WriteByte(0x21);
		stream.WriteByte(0xF9);
		stream.WriteByte(0x04);
		stream.WriteByte(0x09);
		WriteShort(stream, delay);
		stream.WriteByte(TransparentIndex);
		stream.WriteByte(0);
	}

	private void WriteImageDescriptor(Stream stream)
	{
		stream.WriteByte(0x2C);
		WriteShort(stream, 0);
		WriteShort(stream, 0);
		WriteShort(stream, _width);
		WriteShort(stream, _height);
		stream.WriteByte(0);
	}

	/// <summary>
	/// Literal-mode LZW: every pixel is emitted as its own root code and the dictionary is never grown.
	/// The compressed form of a run-encoding encoder and this differ by the table-growth bookkeeping,
	/// which is the classic place a hand-written GIF goes subtly wrong and renders as noise in a browser.
	/// The cost is size, not correctness: about 1.125 times the raw frame, which for a 96 pixel orb at
	/// twenty-four frames is roughly a quarter of a megabyte against a two megabyte resource limit.
	/// </summary>
	private static void WriteLzw(Stream stream, byte[] indexed)
	{
		const int codeSize = MinimumCodeSize + 1;

		var writer = new BitWriter(stream);
		writer.Write(ClearCode, codeSize);

		foreach (var pixel in indexed)
		{
			writer.Write(pixel, codeSize);
		}

		writer.Write(EndCode, codeSize);
		writer.Flush();
	}

	private sealed class BitWriter(Stream stream)
	{
		private readonly List<byte> _block = [];
		private int _accumulator;
		private int _bits;

		public void Write(int code, int bitCount)
		{
			_accumulator |= code << _bits;
			_bits += bitCount;

			while (_bits >= 8)
			{
				Emit((byte)(_accumulator & 0xFF));
				_accumulator >>= 8;
				_bits -= 8;
			}
		}

		public void Flush()
		{
			if (_bits > 0)
			{
				Emit((byte)(_accumulator & 0xFF));
				_accumulator = 0;
				_bits = 0;
			}

			CloseBlock();
			stream.WriteByte(0);
		}

		/// <summary>
		/// GIF carries compressed data in sub-blocks of at most 255 bytes, each preceded by its length.
		/// Writing one long run produces a file that only a decoder lenient about sub-blocks will read.
		/// </summary>
		private void Emit(byte value)
		{
			_block.Add(value);

			if (_block.Count == 255)
			{
				CloseBlock();
			}
		}

		private void CloseBlock()
		{
			if (_block.Count == 0)
			{
				return;
			}

			stream.WriteByte((byte)_block.Count);

			foreach (var value in _block)
			{
				stream.WriteByte(value);
			}

			_block.Clear();
		}
	}

	/// <summary>
	/// The palette is built for this orb rather than being generic: a neutral ramp plus a cyan-to-violet
	/// hue sweep, because every colour the renderer can emit lives in that band. A generic 256-entry
	/// cube would spend most of its entries on hues the orb never draws.
	/// </summary>
	private static byte[] BuildPalette()
	{
		var palette = new byte[PaletteSize * 3];
		var greys = 12;

		for (var index = 0; index < greys; index++)
		{
			var level = (byte)Math.Round(index / (greys - 1.0) * 255.0);
			var offset = (FirstColour + index) * 3;
			palette[offset] = level;
			palette[(offset + 1)] = level;
			palette[(offset + 2)] = level;
		}

		var hueStart = FirstColour + greys;
		var hueCount = PaletteSize - hueStart;

		for (var index = 0; index < hueCount; index++)
		{
			var t = index / (hueCount - 1.0);
			var hue = 175.0 + (t * 95.0);
			var saturation = 0.25 + (t * 0.7);
			var value = 0.45 + ((index % 8) / 7.0 * 0.55);

			HsvToRgb(hue, saturation, value, out var r, out var g, out var b);

			var offset = (hueStart + index) * 3;
			palette[offset] = r;
			palette[(offset + 1)] = g;
			palette[(offset + 2)] = b;
		}

		return palette;
	}

	private static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
	{
		var c = v * s;
		var x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1));
		var m = v - c;

		double pr, pg, pb;

		switch (h)
		{
			case < 60: (pr, pg, pb) = (c, x, 0d); break;
			case < 120: (pr, pg, pb) = (x, c, 0d); break;
			case < 180: (pr, pg, pb) = (0d, c, x); break;
			case < 240: (pr, pg, pb) = (0d, x, c); break;
			case < 300: (pr, pg, pb) = (x, 0d, c); break;
			default: (pr, pg, pb) = (c, 0d, x); break;
		}

		r = (byte)Math.Clamp((pr + m) * 255, 0, 255);
		g = (byte)Math.Clamp((pg + m) * 255, 0, 255);
		b = (byte)Math.Clamp((pb + m) * 255, 0, 255);
	}

	/// <summary>
	/// Nearest palette entry by squared RGB distance. A radial gradient quantises to a few dozen
	/// distinct colours, so memoising on the packed RGB triple turns the nearest-colour search from
	/// per-pixel into per-distinct-colour.
	/// </summary>
	private static byte Nearest(byte r, byte g, byte b)
	{
		var key = (r << 16) | (g << 8) | b;

		if (QuantisationCache.TryGetValue(key, out var cached))
		{
			return cached;
		}

		var best = FirstColour;
		var bestDistance = int.MaxValue;

		for (var index = FirstColour; index < PaletteSize; index++)
		{
			var offset = index * 3;
			var dr = Palette[offset] - r;
			var dg = Palette[(offset + 1)] - g;
			var db = Palette[(offset + 2)] - b;
			var distance = (dr * dr) + (dg * dg) + (db * db);

			if (distance == 0)
			{
				QuantisationCache[key] = (byte)index;
				return (byte)index;
			}

			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = index;
			}
		}

		QuantisationCache[key] = (byte)best;
		return (byte)best;
	}

	private static void WriteAscii(Stream stream, string value)
	{
		foreach (var character in value)
		{
			stream.WriteByte((byte)character);
		}
	}

	private static void WriteShort(Stream stream, int value)
	{
		stream.WriteByte((byte)(value & 0xFF));
		stream.WriteByte((byte)((value >> 8) & 0xFF));
	}
}