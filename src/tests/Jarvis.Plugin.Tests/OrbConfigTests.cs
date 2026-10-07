using System.Text.Json;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Orb;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Model.Versioning;
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
			Assert.That(parsed.ShowButtons, Is.True);
			Assert.That(parsed.ButtonBarSize, Is.EqualTo(0.16));
		});
	}

	/// <summary>
	/// Every key the widget owns has to be reachable from the editor, and every key the editor offers has to
	/// be one the reader reads. A button setting that is stored but never offered cannot be turned off, and
	/// an offered one the schema does not describe would be rejected on save.
	/// </summary>
	[Test]
	public void The_button_settings_are_editable_and_declared()
	{
		var declared = ReadSchemaProperties();
		var offered = InputKeys(OrbConfigView.Build(OrbWidgetData.DefaultElement));

		Assert.Multiple(() =>
		{
			Assert.That(declared, Contains.Item(OrbWidgetData.ShowButtonsKey));
			Assert.That(declared, Contains.Item(OrbWidgetData.ButtonBarSizeKey));
			Assert.That(offered, Contains.Item(OrbWidgetData.ShowButtonsKey));
			Assert.That(offered, Contains.Item(OrbWidgetData.ButtonBarSizeKey));
		});
	}

	/// <summary>
	/// A widget placed before the buttons existed carries neither key, and has to keep working: both fall
	/// back rather than the widget opening empty.
	/// </summary>
	[Test]
	public void A_widget_stored_before_the_buttons_existed_still_draws_them_by_default()
	{
		var parsed = OrbWidgetData.Parse(JsonSerializer.Deserialize<JsonElement>("""{ "preset": "halo" }"""));

		Assert.Multiple(() =>
		{
			Assert.That(parsed.ShowButtons, Is.True);
			Assert.That(parsed.ButtonBarSize, Is.EqualTo(0.16));
		});
	}

	/// <summary>A stored value outside the offered range is clamped rather than carried into the tree.</summary>
	[TestCase(0.0, 0.08)]
	[TestCase(0.08, 0.08)]
	[TestCase(0.2, 0.2)]
	[TestCase(0.4, 0.4)]
	[TestCase(9.0, 0.4)]
	public void The_button_size_is_clamped_to_what_the_editor_offers(double stored, double expected)
	{
		// Written with an invariant format, because a comma decimal separator produces a payload the reader
		// cannot parse and the failure would be about the test's locale rather than about the clamp.
		var json = $$"""{ "buttonBarSize": {{stored.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }""";

		var parsed = OrbWidgetData.Parse(JsonSerializer.Deserialize<JsonElement>(json));

		Assert.That(parsed.ButtonBarSize, Is.EqualTo(expected).Within(0.0001), $"{stored}");
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

	/// <summary>
	/// A configuration surface has to actually be served.
	/// <para>
	/// Every other test here builds <see cref="OrbConfigView"/> directly, which is the tree and not the
	/// routing. The surface was compared against the widget type's <em>local</em> id while the host sends the
	/// qualified <c>integrationId::localId</c> form, so it never matched, no session was created, and the host
	/// showed "this widget's configuration is temporarily unavailable" with nothing in the log. The tree was
	/// fine the entire time, which is exactly why building it was not the thing to check.
	/// </para>
/// </summary>
[Test]
	public async Task The_visual_configuration_surface_is_served()
	{
		var surfaces = new List<UiSurface>();

		foreach (var qualified in new[]
		{
			"com.misike12.jarvis::jarvis-orb",
			OrbWidgetTypeProvider.OrbTypeId,
		})
		{
			surfaces.Clear();

			var session = await Provider().CreateSessionAsync(
				new UiSessionRequest
				{
					UiModelVersion = UiModelVersions.Current,
					Surface = new UiSurface
					{
Kind = UiSurfaceKinds.Config,
					SessionMode = UiSessionModes.Exclusive,
					Attributes = Attributes(qualified),
				},
				},
				CancellationToken.None);

			Assert.That(
				session,
				Is.Not.Null,
				$"no configuration session for widget type {qualified}, so the visual editor has nothing to show");

			await session!.DisposeAsync();
		}
	}

	/// <summary>Another integration's widget configuration must not be served by this one.</summary>
	[Test]
	public async Task Another_integrations_widget_configuration_is_not_served()
	{
		var session = await Provider().CreateSessionAsync(
			new UiSessionRequest
			{
				UiModelVersion = UiModelVersions.Current,
				Surface = new UiSurface
				{
					Kind = UiSurfaceKinds.Config,
				SessionMode = UiSessionModes.Exclusive,
				Attributes = Attributes("com.example.other::something-else"),
			},
			},
			CancellationToken.None);

		Assert.That(session, Is.Null, "this integration claimed a widget type that is not its own");
	}

	private static Dictionary<string, System.Text.Json.JsonElement> Attributes(string widgetType)
	{
		using var document = JsonDocument.Parse($$"""
			{
				"{{UiConfigSurfaceAttributes.EntryPoint}}": "{{UiConfigEntryPoints.WidgetConfig}}",
				"{{UiConfigSurfaceAttributes.WidgetType}}": "{{widgetType}}",
				"{{UiConfigSurfaceAttributes.WidgetData}}": {{OrbWidgetData.DefaultJson}}
			}
			""");

return document.RootElement
			.EnumerateObject()
			// Cloned, because a JsonElement read from a disposed document throws and this dictionary outlives
			// the parse.
			.ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
	}

	/// <summary>
	/// A widget's payload reaches the reader as raw host data, and being a JSON number is not the same as
	/// being an integer. The reader used <c>GetInt32</c> and <c>GetDouble</c>, which throw rather than fall
	/// back, so a widget saved from JSON mode with <c>"ringCount": 2.5</c> took the exception out of the
	/// session and the orb failed to open at all.
	/// </summary>
	[TestCase(@"{""ringCount"": 2.5}")]
	[TestCase(@"{""ringCount"": ""3""}")]
	[TestCase(@"{""ringCount"": 99999999999}")]
	[TestCase(@"{""ringSpeed"": ""fast""}")]
	[TestCase(@"{""textSize"": null}")]
	public void An_unreadable_number_falls_back_rather_than_throwing(string json)
	{
		var parsed = OrbWidgetData.Parse(JsonSerializer.Deserialize<JsonElement>(json));
		var defaults = new OrbWidgetData();

		Assert.Multiple(() =>
		{
			Assert.That(parsed.RingCount, Is.EqualTo(defaults.RingCount), json);
			Assert.That(parsed.RingSpeed, Is.EqualTo(defaults.RingSpeed), json);
			Assert.That(parsed.TextSize, Is.EqualTo(defaults.TextSize), json);
		});
	}

	/// <summary>A numeric preset that names no member is refused, as every other stored enum now is.</summary>
	[TestCase(@"{""preset"": 9}")]
	[TestCase(@"{""textMode"": 99}")]
	[TestCase(@"{""scope"": 7}")]
	public void A_number_standing_in_for_an_enum_falls_back(string json)
	{
		var parsed = OrbWidgetData.Parse(JsonSerializer.Deserialize<JsonElement>(json));

		Assert.Multiple(() =>
		{
			Assert.That(parsed.Preset, Is.EqualTo(OrbPreset.ArcReactor), json);
			Assert.That(parsed.TextMode, Is.EqualTo(TextDisplayMode.InterimPlusReply), json);
			Assert.That(parsed.Scope, Is.EqualTo(SessionScope.Global), json);
		});
	}

	private static OrbUiProvider Provider(Action? start = null, Action? stop = null) => new(
		new AssistantStateHolder(),
		new ThrowingRegistry(),
		RuntimeTestLog.Logger,
		new OrbWidgetTypeProvider(RuntimeTestLog.Logger),
		new OrbButtonActions(start ?? (() => { }), stop ?? (() => { })));

	private sealed class ThrowingRegistry : IUiResourceRegistry
	{
		public Task<MacroDeck.Ui.Model.Resources.UiResource> RegisterAsync(
			string name,
			ReadOnlyMemory<byte> content,
			string mediaType,
			CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("A configuration surface must not need to register an asset.");

		public Task RemoveAsync(string name, CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("A configuration surface must not remove an asset.");
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
				if (configuration.Properties is { } properties)
				{
					Collect(properties, keys);
				}

				if (configuration.Editor is { } editor)
				{
					Collect(editor, keys);
				}

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
