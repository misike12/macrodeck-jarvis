using System.Text.Json;
using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Orb;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The orb's view tree, built the way the host builds it.
/// <para>
/// The host materializes a widget's tree when the session opens, not when the plugin starts, so a tree that
/// is invalid at construction looked fine to every other check in this repository. The orb shipped a
/// modifier whose child set <c>Fill</c>, which a wrapper's child may not do: the widget opened empty and the
/// host logged a <c>session.open</c> failure once per attempt, forever.
/// </para>
/// <para>
/// So this constructs the real <see cref="UiView"/> over a spread of configurations. That is the only place
/// the host's materializer rules are actually exercised.
/// </para>
/// </summary>
[TestFixture]
public class OrbViewTests
{
	private static readonly string[] BareOrb = ["ui.stack", "ui.image"];

	private static readonly string[] OrbWithText = ["ui.stack", "ui.image", "ui.text"];

	private static UiView BuildFor(OrbWidgetData data) =>
		new(
			new UiSurface { Kind = UiSurfaceKinds.Widget, SessionMode = UiSessionModes.Shared },
			OrbView.Build(
				data,
				new UiState<AssistantState>(AssistantState.Idle),
				new UiState<MacroDeck.Ui.Model.Resources.UiResource?>(null),
				new UiState<string>(string.Empty),
				new UiState<string>(string.Empty)));

	/// <summary>The default configuration, which is what a freshly added widget uses.</summary>
	[Test]
	public void The_orb_view_materializes()
	{
		Assert.That(BuildFor(new OrbWidgetData()), Is.Not.Null);
	}

	/// <summary>
	/// Every toggle the widget's editor offers, because each one adds or removes a branch of the tree and a
	/// branch that is only wrong in one configuration is still wrong.
	/// </summary>
	[TestCase(true, true, 1)]
	[TestCase(true, true, 4)]
	[TestCase(true, false, 3)]
	[TestCase(false, true, 3)]
	[TestCase(false, false, 4)]
	[TestCase(true, true, 0)]
	public void The_orb_view_materializes_in_every_configuration(bool glow, bool showText, int rings)
	{
		var data = new OrbWidgetData { Glow = glow, ShowText = showText, RingCount = rings };

		Assert.That(BuildFor(data), Is.Not.Null);
	}

	/// <summary>
	/// Data parsed from a stored widget, which is the path a real widget takes rather than the defaults.
	/// </summary>
	[Test]
	public void The_orb_view_materializes_from_stored_widget_data()
	{
		using var document = JsonDocument.Parse(
			"""
			{
				"preset": "custom",
				"rings": 3,
				"ringSpeed": 1.2,
				"ringRotation": true,
				"glow": true,
				"accentColor": "#4aa3ff",
				"coreColor": "#0b1a33",
				"textColor": "#ffffff",
				"borderStyle": "soft",
				"showText": true,
				"textMode": "last-reply",
				"textSize": 0.08,
				"audioReactive": true,
				"sensitivity": 1.4,
				"scope": "global"
			}
			""");

		Assert.That(BuildFor(OrbWidgetData.Parse(document.RootElement)), Is.Not.Null);
	}

	/// <summary>
	/// An unrecognised preset still has to produce a view. A stored value from a newer release, or a typo,
	/// must not stop the widget from opening.
	/// </summary>
	[Test]
	public void An_unrecognised_preset_still_produces_a_view()
	{
		using var document = JsonDocument.Parse("""{ "preset": "from-the-future", "rings": 2 }""");

		Assert.That(BuildFor(OrbWidgetData.Parse(document.RootElement)), Is.Not.Null);
	}

	/// <summary>
	/// The orb is one image, because the asset already contains the core, the glow and the rings.
	/// <para>
	/// It used to carry a glow disc and a ring per setting as separate layers above that image, which drew
	/// everything twice at two sizes and clipped the rings against the widget edge. It also rewrote a rotation
	/// twenty-five times a second to animate a layer whose motion was already baked into the GIF. Counting the
	/// nodes is what makes both visible: a duplicate layer cannot be added without the count moving.
	/// </para>
	/// </summary>
	[Test]
	public void The_orb_is_one_image_and_nothing_else()
	{
		using var view = BuildFor(new OrbWidgetData { ShowText = false });

		var root = view.Tree.Root;
		var types = new List<string>();
		Collect(root, types);

		Assert.That(
			types,
			Is.EqualTo(BareOrb),
			"the orb drew something besides its image, so part of it is drawn twice");
	}

	[Test]
	public void Text_adds_exactly_one_layer_when_it_is_asked_for()
	{
		using var view = BuildFor(new OrbWidgetData { ShowText = true });

		var types = new List<string>();
		Collect(view.Tree.Root, types);

		Assert.That(types, Is.EqualTo(OrbWithText));
	}

	private static void Collect(MacroDeck.Ui.Model.Nodes.UiNode node, List<string> types)
	{
		types.Add(node.Type);

		foreach (var child in node.Children ?? [])
		{
			Collect(child, types);
		}
	}
}