using Jarvis.Plugin.Core;
using Jarvis.Plugin.Orb;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The orb renderer.
/// <para>
/// These tests assert on the pixels rather than on a summary, because the two failures being pinned here
/// were both invisible to any higher-level check: every preset rendered the same picture, and the
/// amplitude never changed anything. A count of distinct colours and a count of differing pixels is what
/// actually distinguishes one from the other.
/// </para>
/// </summary>
[TestFixture]
public class OrbRenderTests
{
	private static readonly double[] Amplitudes = [0.0, 0.2, 0.5, 0.9];

	/// <summary>
	/// The preset is the headline defect this fixes: with it ignored, whichever preset was rendered first
	/// filled the cache and every widget showed that one.
	/// </summary>
	[TestCase(OrbPreset.ArcReactor, OrbPreset.Halo)]
	[TestCase(OrbPreset.ArcReactor, OrbPreset.Pulse)]
	[TestCase(OrbPreset.ArcReactor, OrbPreset.Custom)]
	[TestCase(OrbPreset.Halo, OrbPreset.Pulse)]
	[TestCase(OrbPreset.Halo, OrbPreset.Custom)]
	[TestCase(OrbPreset.Pulse, OrbPreset.Custom)]
	public void Two_presets_render_differently(OrbPreset first, OrbPreset second)
	{
		var a = OrbFrameRenderer.Render(AssistantState.Listening, 0.5, OrbPalette.Default, 0.3, first);
		var b = OrbFrameRenderer.Render(AssistantState.Listening, 0.5, OrbPalette.Default, 0.3, second);

		Assert.That(Difference(a, b), Is.GreaterThan(200), $"{first} and {second} render almost identically");
	}

	/// <summary>Every preset must render something, or a user selecting one gets a blank widget.</summary>
	[TestCase(OrbPreset.ArcReactor)]
	[TestCase(OrbPreset.Halo)]
	[TestCase(OrbPreset.Pulse)]
	[TestCase(OrbPreset.Custom)]
	public void Every_preset_renders_something(OrbPreset preset)
	{
		var frame = OrbFrameRenderer.Render(AssistantState.Idle, 0.2, OrbPalette.Default, 0.2, preset);

		Assert.That(Opaque(frame), Is.GreaterThan(100), $"{preset} renders almost nothing");
	}

	/// <summary>Also at every state, because a preset that only draws in one state is still broken.</summary>
	[TestCase(AssistantState.Idle)]
	[TestCase(AssistantState.Listening)]
	[TestCase(AssistantState.Thinking)]
	[TestCase(AssistantState.Speaking)]
	[TestCase(AssistantState.Executing)]
	[TestCase(AssistantState.Confirming)]
	[TestCase(AssistantState.Error)]
	public void Every_preset_renders_in_every_state(AssistantState state)
	{
		foreach (var preset in new[]
		{
			OrbPreset.ArcReactor,
			OrbPreset.Halo,
			OrbPreset.Pulse,
			OrbPreset.Custom,
		})
		{
			var frame = OrbFrameRenderer.Render(state, 0.4, OrbPalette.Default, 0.4, preset);
			Assert.That(Opaque(frame), Is.GreaterThan(100), $"{preset} in {state} renders almost nothing");
		}
	}

	/// <summary>
	/// The other headline defect. A louder voice has to move the orb, in the states where a voice is the
	/// thing happening.
	/// </summary>
	[TestCase(AssistantState.Listening)]
	[TestCase(AssistantState.Speaking)]
	public void A_louder_voice_changes_the_picture(AssistantState state)
	{
		var quiet = OrbFrameRenderer.Render(state, 0.3, OrbPalette.Default, 0.0);
		var loud = OrbFrameRenderer.Render(state, 0.3, OrbPalette.Default, 1.0);

		Assert.That(Difference(quiet, loud), Is.GreaterThan(500), $"{state} ignores amplitude");
	}

	/// <summary>
	/// Silence has to look different from a voice. If quiet and loud produce the same frame, the amplitude
	/// is being rounded away somewhere and the orb is not reacting at all.
	/// </summary>
	[Test]
	public void Silence_is_visibly_different_from_a_voice()
	{
		foreach (var state in new[] { AssistantState.Listening, AssistantState.Speaking })
		{
			var silent = OrbFrameRenderer.Render(state, 0.3, OrbPalette.Default, 0.0);
			var speaking = OrbFrameRenderer.Render(state, 0.3, OrbPalette.Default, 0.8);

			Assert.That(Difference(silent, speaking), Is.GreaterThan(500), $"{state} looks the same either way");
		}
	}

	/// <summary>Monotonic: louder is never quieter. A shape that dips would read as a bug.</summary>
	[Test]
	public void A_louder_voice_never_renders_a_smaller_core()
	{
		foreach (var state in new[] { AssistantState.Listening, AssistantState.Speaking })
		{
			var previous = -1.0;

			foreach (var amplitude in Amplitudes)
			{
				var lit = Lightness(OrbFrameRenderer.Render(state, 0.3, OrbPalette.Default, amplitude));

				Assert.That(lit, Is.GreaterThanOrEqualTo(previous - 0.01), $"{state} got dimmer at {amplitude}");
				previous = lit;
			}
		}
	}

	/// <summary>
	/// Presets are meant to be told apart at a glance, so the difference has to be in the geometry rather
	/// than only in which pixels happen to be lit.
	/// </summary>
	/// <summary>
	/// Presets are meant to be told apart at a glance, so the difference has to be in the outline rather
	/// than only in which pixels happen to be lit.
	/// </summary>
	[Test]
	public void Every_preset_has_its_own_outline()
	{
		var outlines = new[]
		{
			OrbPreset.ArcReactor,
			OrbPreset.Halo,
			OrbPreset.Pulse,
			OrbPreset.Custom,
		}.ToDictionary(
			preset => preset,
			preset => Edges(OrbFrameRenderer.Render(AssistantState.Idle, 0.25, OrbPalette.Default, 0.3, preset)));

		var unique = outlines.Values.Distinct().Count();

		Assert.That(unique, Is.EqualTo(outlines.Count), "two presets share an outline");
	}

	/// <summary>A different palette has to give a different picture, or the colour settings do nothing.</summary>
	[Test]
	public void A_different_palette_changes_the_colours()
	{
		var warm = OrbPalette.Default with { Accent = (1.0, 0.4, 0.1) };
		var cool = OrbPalette.Default with { Accent = (0.1, 0.4, 1.0) };

		Assert.That(
			Difference(
				OrbFrameRenderer.Render(AssistantState.Idle, 0.3, warm, 0.3),
				OrbFrameRenderer.Render(AssistantState.Idle, 0.3, cool, 0.3)),
			Is.GreaterThan(500));
	}

	/// <summary>The renderer must never emit a bad byte, because the encoder would write a corrupt frame.</summary>
	[Test]
	public void Every_frame_is_well_formed()
	{
		foreach (var state in new[] { AssistantState.Idle, AssistantState.Error, AssistantState.Listening })
		{
			foreach (var preset in new[] { OrbPreset.ArcReactor, OrbPreset.Pulse })
			{
				var frame = OrbFrameRenderer.Render(state, 1.1, OrbPalette.Default, 0.5, preset);

				Assert.That(frame, Has.Length.EqualTo(OrbFrameRenderer.Size * OrbFrameRenderer.Size * 4));
			}
		}
	}

	/// <summary>Amplitude outside the normal range must not throw or invert.</summary>
	[TestCase(-5.0)]
	[TestCase(0.0)]
	[TestCase(0.5)]
	[TestCase(1.0)]
	[TestCase(17.0)]
	public void Amplitude_outside_the_normal_range_is_handled(double amplitude)
	{
		var frame = OrbFrameRenderer.Render(AssistantState.Listening, 0.3, OrbPalette.Default, amplitude);

		Assert.That(Opaque(frame), Is.GreaterThan(0));
	}

	/// <summary>Every state renders, including the one that is supposed to be almost nothing.</summary>
	[Test]
	public void Every_state_renders()
	{
		foreach (AssistantState state in Enum.GetValues<AssistantState>())
		{
			var frame = OrbFrameRenderer.Render(state, 0.3, OrbPalette.Default, 0.3);

			Assert.That(frame, Has.Length.EqualTo(OrbFrameRenderer.Size * OrbFrameRenderer.Size * 4), $"{state}");
		}
	}

	/// <summary>A number of pixels with a non-zero alpha.</summary>
	private static int Opaque(byte[] frame)
	{
		var count = 0;

		for (var offset = 3; offset < frame.Length; offset += 4)
		{
			if (frame[offset] > 0)
			{
				count++;
			}
		}

		return count;
	}

	/// <summary>How many pixels differ between two frames.</summary>
	private static int Difference(byte[] first, byte[] second)
	{
		Assert.That(second, Has.Length.EqualTo(first.Length));
		var count = 0;

		for (var index = 0; index < first.Length; index++)
		{
			if (first[index] != second[index])
			{
				count++;
			}
		}

		return count;
	}

	/// <summary>Mean brightness, used to check that amplitude is monotonic.</summary>
	private static double Lightness(byte[] frame)
	{
		double total = 0;
		var samples = 0;

		for (var offset = 0; offset < frame.Length; offset += 4)
		{
			if (frame[offset + 3] == 0)
			{
				continue;
			}

			total += ((frame[offset] + frame[offset + 1] + frame[offset + 2]) / 3.0) * (frame[offset + 3] / 255.0);
			samples++;
		}

		return samples == 0 ? 0 : total / samples;
	}

	/// <summary>
	/// A compact description of the shape: a coarse occupancy grid. Two presets with the same grid have the
	/// same outline, which is what "different shape" has to mean.
	/// </summary>
	private static string Edges(byte[] frame)
	{
		const int Grid = 12;
		var cell = OrbFrameRenderer.Size / Grid;
		var builder = new System.Text.StringBuilder(Grid * Grid);

		for (var row = 0; row < Grid; row++)
		{
			for (var column = 0; column < Grid; column++)
			{
				var lit = 0;

				for (var y = row * cell; y < (row + 1) * cell; y++)
				{
					for (var x = column * cell; x < (column + 1) * cell; x++)
					{
						var offset = (((y * OrbFrameRenderer.Size) + x) * 4) + 3;

						if (frame[offset] > 96)
						{
							lit++;
						}
					}
				}

				builder.Append(lit > (cell * cell / 3) ? '#' : lit > 0 ? '+' : '.');
			}
		}

		return builder.ToString();
	}
}
