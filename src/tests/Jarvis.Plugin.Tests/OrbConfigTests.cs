using System.Text.Json;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Orb;
using MacroDeck.Ui.Dsl;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

[TestFixture]
public class OrbConfigTests
{
	/// <summary>
	/// The orb's own default payload is what a freshly placed widget is stored with, so parsing it has to
	/// round-trip. It failed: the payload spells the enums kebab-case and the reader asked
	/// <c>Enum.TryParse</c> for exactly that, which cannot match a hyphen.
	/// </summary>
	[Test]
	public void The_default_payload_parses_back_to_the_defaults()
	{
		var parsed = OrbWidgetData.Parse(OrbWidgetData.DefaultElement);

		Assert.Multiple(() =>
		{
			Assert.That(parsed.Preset, Is.EqualTo(OrbPreset.ArcReactor));
			Assert.That(parsed.TextMode, Is.EqualTo(TextDisplayMode.InterimPlusReply));
			Assert.That(parsed.Scope, Is.EqualTo(SessionScope.Global));
			Assert.That(parsed.RingCount, Is.EqualTo(3));
			Assert.That(parsed.RingSpeed, Is.EqualTo(1.0));
			Assert.That(parsed.TextSize, Is.EqualTo(0.055));
			Assert.That(parsed.AccentColor, Is.EqualTo("#4fd2ff"));
			Assert.That(parsed.ShowText, Is.True);
			Assert.That(parsed.AudioReactive, Is.True);
		});
	}

	/// <summary>
	/// Both spellings have to be readable, because the schema and the configuration surface write the
	/// kebab form while a hand-edited payload or an older release may hold the CLR name.
	/// </summary>
	[TestCase(@"{""preset"":""halo"",""textMode"":""last-reply"",""scope"":""local""}")]
	[TestCase(@"{""preset"":""Halo"",""textMode"":""LastReply"",""scope"":""Local""}")]
	[TestCase(@"{""preset"":""HALO"",""textMode"":""LASTREPLY"",""scope"":""LOCAL""}")]
	public void Enum_keys_accept_the_spellings_the_plugin_can_produce(string json)
	{
		var parsed = OrbWidgetData.Parse(JsonSerializer.Deserialize<JsonElement>(json));

		Assert.Multiple(() =>
		{
			Assert.That(parsed.Preset, Is.EqualTo(OrbPreset.Halo));
			Assert.That(parsed.TextMode, Is.EqualTo(TextDisplayMode.LastReply));
			Assert.That(parsed.Scope, Is.EqualTo(SessionScope.Local));
		});
	}

	/// <summary>
	/// An unreadable value falls back rather than throwing: a widget placed before a release that added an
	/// enum member must still draw.
	/// </summary>
	[TestCase(@"{""preset"":""not-a-preset""}")]
	[TestCase(@"{""preset"":42}")]
	[TestCase(@"{""preset"":null}")]
	[TestCase(@"{}")]
	public void An_unreadable_enum_falls_back_instead_of_throwing(string json)
	{
		var parsed = OrbWidgetData.Parse(JsonSerializer.Deserialize<JsonElement>(json));

		Assert.That(parsed.Preset, Is.EqualTo(OrbPreset.ArcReactor));
	}

	/// <summary>
	/// Every input the configuration tree emits has to name a key the stored payload declares. An input
	/// whose key the schema does not describe would write a value the schema then rejects on save.
	/// </summary>
	[Test]
	public void Every_configured_key_is_declared_by_the_schema()
	{
		var declared = ReadSchemaProperties();

		foreach (var key in InputKeys(OrbConfigView.Build(OrbWidgetData.DefaultElement)))
		{
			Assert.That(declared, Contains.Item(key), $"'{key}' is configured but not in the schema.");
		}
	}

	/// <summary>
	/// Building the tree is the part that throws on a duplicate id, a too-deep tree or a state holding no
	/// value, so it is worth doing in a test rather than only when a user opens the editor.
	/// </summary>
	[Test]
	public void The_config_tree_builds_from_a_stored_payload()
	{
		var stored = JsonSerializer.Deserialize<JsonElement>(
			"""
			{ "preset": "pulse", "ringCount": 5, "accentColor": "#ff0000", "flows": [ { "name": "onPress" } ] }
			""");

		var root = OrbConfigView.Build(stored);

		Assert.Multiple(() =>
		{
			Assert.That(root, Is.InstanceOf<MacroDeck.Ui.Config.UiWidgetConfiguration>());
			Assert.That(root.Key, Is.EqualTo("root"));
		});
	}

	private static HashSet<string> ReadSchemaProperties()
	{
		var schema = JsonSerializer.Deserialize<JsonElement>(OrbWidgetData.Schema);

		return schema.GetProperty("properties")
			.EnumerateObject()
			.Select(property => property.Name)
			.ToHashSet(StringComparer.Ordinal);
	}

	private static List<string> InputKeys(UiElement root)
	{
		var keys = new List<string>();
		Collect(root, keys);

		return keys;
	}

	private static void Collect(UiElement element, List<string> keys)
	{
		// Only inputs name a data key. Containers name themselves for id derivation, which has nothing to do
		// with the stored payload, and the appearance fragment owns the host's ids rather than the orb's.
		if (!IsInput(element) && !string.IsNullOrEmpty(element.Key) && element.Key.StartsWith("appearance", StringComparison.Ordinal))
		{
			return;
		}

		if (IsInput(element) && !string.IsNullOrEmpty(element.Key))
		{
			keys.Add(element.Key);
		}

		switch (element)
		{
			case MacroDeck.Ui.Config.UiWidgetConfiguration configuration:
				Collect(configuration.Properties, keys);
				Collect(configuration.Editor, keys);
				break;

			case MacroDeck.Ui.Dsl.UiContainer container:
				foreach (var child in container.Children)
				{
					Collect(child, keys);
				}

				break;

			default:
				break;
		}
	}

	/// <summary>
	/// The input interfaces are internal, so an input is recognised by its base type rather than with a
	/// pattern match on a type a plugin cannot name.
	/// </summary>
	private static bool IsInput(UiElement element)
	{
		for (var type = element.GetType(); type is not null; type = type.BaseType)
		{
			if (type.Name.StartsWith("UiInput", StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}
}
