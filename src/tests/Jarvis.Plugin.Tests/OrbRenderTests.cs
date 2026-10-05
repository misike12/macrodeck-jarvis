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

	/// <summary>Monotonic: a louder voice never shrinks the core.</summary>
	[Test]
	public void A_louder_voice_never_renders_a_smaller_core()
	{
		// Measured on the radius rather than on the mean lightness of the frame. The core is dark and the halo
		// is bright, so a core that grows over a bright halo lowers the mean lightness of the picture while the
		// orb is plainly reacting: that statistic says the opposite of what is on screen. It only ever agreed
		// with the radius while the halo was invisible, because then nothing bright was being covered.
		foreach (var state in new[] { AssistantState.Listening, AssistantState.Speaking })
		{
			var previous = -1.0;

			foreach (var amplitude in Amplitudes)
			{
				var radius = OrbFrameRenderer.CoreRadiusFor(state, amplitude);

				Assert.That(
					radius,
					Is.GreaterThanOrEqualTo(previous),
					$"{state} shrank its core at amplitude {amplitude}, from {previous:0.0} to {radius:0.0}");

previous = radius;
			}
		}
	}

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

	/// <summary>
	/// The frames of one animation have to differ from each other.
	/// <para>
	/// The rings are the only thing in the frame that depends on the phase, so when they were being discarded
	/// the orb was a still image: eighteen identical frames, a GIF that plays itself into a static ball, and
	/// nothing in the suite noticed because every other test rendered a single frame. This is the one property
	/// that cannot be checked on one frame.
	/// </para>
	/// </summary>
	[Test]
	public void Consecutive_frames_of_one_animation_differ()
	{
		foreach (var state in new[] { AssistantState.Idle, AssistantState.Speaking })
		{
			var frames = new[]
			{
				OrbFrameRenderer.Render(state, 0.0, OrbPalette.Default, 0.2),
				OrbFrameRenderer.Render(state, Math.Tau / 18, OrbPalette.Default, 0.2),
			};

			Assert.That(
				Difference(frames[0], frames[1]),
				Is.GreaterThan(500),
				$"{state} produced the same frame twice, so its animation is a still image");
		}
	}

	/// <summary>
	/// The rings have to be in the picture at all, not merely computed and then dropped.
	/// <para>
	/// A ring's peak coverage is its alpha, and the frame builder maps anything under half opacity to the
	/// transparent index. At the idle weight of 0.5 a ring came out at 127 of 255 and was thrown away, so
	/// the orb had no rings in any state and the setting that counts them did nothing.
	/// </para>
	/// </summary>
	[Test]
	public void The_rings_are_actually_drawn()
	{
		var frame = OrbFrameRenderer.Render(AssistantState.Idle, 0.0, OrbPalette.Default, 0.2);

		// Counted rather than asserted on a colour, because the rings are drawn in the palette's ring colour
		// which is near white and the core is dark, so a count of bright pixels outside the core is the thing
		// that says the rings are there.
		var centre = OrbFrameRenderer.Size / 2.0;
		var ringPixels = 0;

		for (var y = 0; y < OrbFrameRenderer.Size; y++)
		{
			for (var x = 0; x < OrbFrameRenderer.Size; x++)
			{
				var offset = ((y * OrbFrameRenderer.Size) + x) * 4;
				var alpha = frame[offset + 3];

				if (alpha < 128)
				{
					continue;
				}

				var distance = Math.Sqrt(((x - centre) * (x - centre)) + ((y - centre) * (y - centre)));

				// Outside the core and inside the outermost ring, which is where a ring has to be to be a ring.
				if (distance > OrbFrameRenderer.Size * 0.20 && distance < OrbFrameRenderer.Size * 0.46)
				{
					ringPixels++;
				}
			}
		}

		Assert.That(
			ringPixels,
			Is.GreaterThan(OrbFrameRenderer.Size * 4),
			"no pixels were drawn in the band where the rings live, so the rings are still being discarded");
	}

	/// <summary>
	/// Every ring the user asked for has to be inside the square.
	/// <para>
	/// The spacing used to come straight from the preset, so asking for more rings than the preset defines put
	/// the outer ones past the half width and they were drawn off the edge: a count of six on an arc reactor
	/// gave four rings and two that were not there at all.
	/// </para>
	/// </summary>
	[TestCase(OrbPreset.ArcReactor, 6)]
	[TestCase(OrbPreset.ArcReactor, 8)]
	[TestCase(OrbPreset.Halo, 6)]
	[TestCase(OrbPreset.Pulse, 4)]
	public void Every_ring_the_user_asked_for_is_inside_the_square(OrbPreset preset, int ringCount)
	{
		var frame = OrbFrameRenderer.Render(
			AssistantState.Idle, 0.0, OrbPalette.Default, 0.2, preset, ringCount);

		var centre = OrbFrameRenderer.Size / 2.0;
		var half = OrbFrameRenderer.Size / 2.0;
		// Checked per ring at the radius that ring is meant to sit on, rather than by counting lit pixels in a
		// band. A band count cannot tell a ring that is missing from one that is merely dim, and it passed
		// while two of the six rings were being drawn off the edge of the image.
		for (var ring = 0; ring < ringCount; ring++)
		{
			var spacing = 0.065 * 3 / Math.Max(ringCount, 3);
			var expected = (OrbFrameRenderer.Size * 0.24) + (ring * OrbFrameRenderer.Size * spacing);

			Assert.That(
				expected,
				Is.LessThan(half * 0.98),
				$"ring {ring} of {ringCount} on {preset} sits at {expected:0} pixels against a half width of {half:0}, so it is off the edge");

			var lit = 0;

			for (var y = 0; y < OrbFrameRenderer.Size; y++)
			{
				for (var x = 0; x < OrbFrameRenderer.Size; x++)
				{
					var offset = ((y * OrbFrameRenderer.Size) + x) * 4;

					if (frame[offset + 3] < 128)
					{
						continue;
					}

					var distance = Math.Sqrt(((x - centre) * (x - centre)) + ((y - centre) * (y - centre)));

					if (Math.Abs(distance - expected) < OrbFrameRenderer.Size * 0.02)
					{
						lit++;
					}
				}
			}

			Assert.That(
				lit,
				Is.GreaterThan(0),
				$"ring {ring} of {ringCount} on {preset} has nothing drawn on it, so it is not in the picture");
		}
	}

	/// <summary>
	/// The animation loops: the frame at a full turn is the frame at zero.
	/// <para>
	/// Each ring's pattern has to repeat a whole number of times over the loop, or the last frame sits
	/// somewhere other than the first and the animation visibly restarts from the wrong position. This
	/// compares rendered bytes rather than geometric reasoning, because the reasoning was done once before
	/// and was wrong in a way that only showed on screen.
	/// </para>
	/// </summary>
	[TestCase(AssistantState.Idle, OrbPreset.ArcReactor)]
	[TestCase(AssistantState.Speaking, OrbPreset.ArcReactor)]
	[TestCase(AssistantState.Idle, OrbPreset.Halo)]
	[TestCase(AssistantState.Idle, OrbPreset.Pulse)]
	public void The_animation_returns_to_its_first_frame(AssistantState state, OrbPreset preset)
	{
		var first = OrbFrameRenderer.Render(state, 0.0, OrbPalette.Default, 0.2, preset, 6);
		var last = OrbFrameRenderer.Render(state, Math.Tau, OrbPalette.Default, 0.2, preset, 6);

		Assert.That(
			last,
			Is.EqualTo(first),
			"the frame after one full turn is not the first frame, so the loop jumps every time it restarts");
	}

	/// <summary>
	/// The core breathes with the animation, or the orb is a still ball with spinning rings.
	/// <para>
	/// The core radius used to come from state and amplitude alone, which meant that at rest nothing in it
	/// moved: voice made it answer and states made it change, but between the two it sat frozen while the
	/// rings spun around it. Pulse is described as a heartbeat rather than a machine, and a heartbeat that
	/// does not beat is a misnomer on every preset.
	/// </para>
	/// </summary>
	[Test]
	public void The_core_breathes_with_the_animation()
	{
		var still = OrbFrameRenderer.CoreRadiusFor(AssistantState.Idle, 0.2, OrbPreset.ArcReactor, 0.0);
		var breathed = OrbFrameRenderer.CoreRadiusFor(AssistantState.Idle, 0.2, OrbPreset.ArcReactor, Math.PI / 2);

		Assert.That(
			Math.Abs(breathed - still),
			Is.GreaterThan(0),
			"the core radius ignores the phase, so the core never moves with time");
	}

	/// <summary>Pulse throbs; the others barely stir.</summary>
	[Test]
	public void Pulse_breathes_deeper_than_the_machine_presets()
	{
		var pulse = Math.Abs(
			OrbFrameRenderer.CoreRadiusFor(AssistantState.Idle, 0.2, OrbPreset.Pulse, Math.PI / 2)
				- OrbFrameRenderer.CoreRadiusFor(AssistantState.Idle, 0.2, OrbPreset.Pulse, 0.0));

		var reactor = Math.Abs(
			OrbFrameRenderer.CoreRadiusFor(AssistantState.Idle, 0.2, OrbPreset.ArcReactor, Math.PI / 2)
				- OrbFrameRenderer.CoreRadiusFor(AssistantState.Idle, 0.2, OrbPreset.ArcReactor, 0.0));

		Assert.That(pulse, Is.GreaterThan(reactor * 2), "Pulse breathes no deeper than the machines");
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
