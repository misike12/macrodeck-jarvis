using System.Text.Json;
using Jarvis.Plugin.Core;
using MacroDeck.Localization;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Config.Options;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;

namespace Jarvis.Plugin.Orb;

/// <summary>
/// The orb's configuration surface. Every key <see cref="OrbWidgetData"/> owns is an input here, so the
/// widget editor is the only place the orb's look is decided rather than a schema the user edits by hand.
/// <para>
/// Each field is a <see cref="UiState{T}"/> bound with <see cref="Bind.To"/>. Nothing here persists
/// anything: the host accumulates the edits into its own draft and writes them through the ordinary widget
/// save path, which is what keeps schema validation, JSON mode and the unsaved-changes prompt working. A
/// key this tree never mentions survives a save untouched, so the orb's own appearance fields can be
/// offered alongside the host's without either side clobbering the other.
/// </para>
/// </summary>
public static class OrbConfigView
{
	/// <summary>Keys this surface owns. Appearance keys belong to the host's own section.</summary>
	private static UiOption Option(string value, LocalizedText label) => new() { Value = value, Label = label };

	public static UiElement Build(JsonElement data)
	{
		var current = OrbWidgetData.Parse(data);

		return new UiWidgetConfiguration
		{
			Key = "root",
			Properties = new UiWidgetProperties
			{
				Key = "properties",
				Children =
				[
					new UiChoiceInput
					{
						Key = OrbWidgetData.PresetKey,
						Label = Strings.Orb.Config.Preset(),
						Description = UiText.Of(Strings.Orb.Config.PresetHint()),
						Binding = Bind.To(new UiState<string>(Kebab(current.Preset))),
						Options = UiValue.Of<IReadOnlyList<UiOption>>(
						[
							Option(Kebab(OrbPreset.ArcReactor), Strings.Orb.Config.PresetOptions.ArcReactor()),
							Option(Kebab(OrbPreset.Halo), Strings.Orb.Config.PresetOptions.Halo()),
							Option(Kebab(OrbPreset.Pulse), Strings.Orb.Config.PresetOptions.Pulse()),
							Option(Kebab(OrbPreset.Custom), Strings.Orb.Config.PresetOptions.Custom()),
						]),
					},

					new UiNumberInput
					{
						Key = OrbWidgetData.RingCountKey,
						Label = Strings.Orb.Config.RingCount(),
						Description = UiText.Of(Strings.Orb.Config.RingCountHint()),
						Min = 0,
						Max = 6,
						Step = 1,
						ShowSlider = true,
						Binding = Bind.To(new UiState<double>(current.RingCount)),
					},

					new UiNumberInput
					{
						Key = OrbWidgetData.RingSpeedKey,
						Label = Strings.Orb.Config.RingSpeed(),
						Description = UiText.Of(Strings.Orb.Config.RingSpeedHint()),
						Min = 0,
						Max = 4,
						Step = 0.1,
						ShowSlider = true,
						Binding = Bind.To(new UiState<double>(current.RingSpeed)),
					},

					new UiBooleanInput
					{
						Key = OrbWidgetData.RingRotationKey,
						Label = Strings.Orb.Config.RingRotation(),
						Binding = Bind.To(new UiState<bool>(current.RingRotation)),
					},

					new UiStringInput
					{
						Key = OrbWidgetData.BorderStyleKey,
						Label = Strings.Orb.Config.BorderStyle(),
						Description = UiText.Of(Strings.Orb.Config.BorderStyleHint()),
						Binding = Bind.To(new UiState<string>(current.BorderStyle)),
					},

					new UiColorInput
					{
						Key = OrbWidgetData.AccentKey,
						Label = Strings.Orb.Config.AccentColor(),
						Binding = Bind.To(new UiState<string>(current.AccentColor)),
					},

					new UiColorInput
					{
						Key = OrbWidgetData.CoreColorKey,
						Label = Strings.Orb.Config.CoreColor(),
						Binding = Bind.To(new UiState<string>(current.CoreColor)),
					},

					new UiColorInput
					{
						Key = OrbWidgetData.TextColorKey,
						Label = Strings.Orb.Config.TextColor(),
						Binding = Bind.To(new UiState<string>(current.TextColor)),
					},

					new UiBooleanInput
					{
						Key = OrbWidgetData.GlowKey,
						Label = Strings.Orb.Config.Glow(),
						Description = UiText.Of(Strings.Orb.Config.GlowHint()),
						Binding = Bind.To(new UiState<bool>(current.Glow)),
					},

					new UiBooleanInput
					{
						Key = OrbWidgetData.ShowTextKey,
						Label = Strings.Orb.Config.ShowText(),
						Binding = Bind.To(new UiState<bool>(current.ShowText)),
					},

					new UiChoiceInput
					{
						Key = OrbWidgetData.TextModeKey,
						Label = Strings.Orb.Config.TextMode(),
						Binding = Bind.To(new UiState<string>(Kebab(current.TextMode))),
						Options = UiValue.Of<IReadOnlyList<UiOption>>(
						[
							Option(Kebab(TextDisplayMode.FullTranscript), Strings.Orb.Config.TextModeOptions.FullTranscript()),
							Option(Kebab(TextDisplayMode.LastReply), Strings.Orb.Config.TextModeOptions.LastReply()),
							Option(Kebab(TextDisplayMode.InterimPlusReply), Strings.Orb.Config.TextModeOptions.InterimPlusReply()),
						]),
					},

					new UiNumberInput
					{
						Key = OrbWidgetData.TextSizeKey,
						Label = Strings.Orb.Config.TextSize(),
						Description = UiText.Of(Strings.Orb.Config.TextSizeHint()),
						Min = 0.02,
						Max = 0.2,
						Step = 0.005,
						ShowSlider = true,
						Binding = Bind.To(new UiState<double>(current.TextSize)),
					},

					new UiBooleanInput
					{
						Key = OrbWidgetData.AudioReactiveKey,
						Label = Strings.Orb.Config.AudioReactive(),
						Description = UiText.Of(Strings.Orb.Config.AudioReactiveHint()),
						Binding = Bind.To(new UiState<bool>(current.AudioReactive)),
					},

					new UiNumberInput
					{
						Key = OrbWidgetData.SensitivityKey,
						Label = Strings.Orb.Config.Sensitivity(),
						Description = UiText.Of(Strings.Orb.Config.SensitivityHint()),
						Min = 0.1,
						Max = 3,
						Step = 0.1,
						ShowSlider = true,
						Binding = Bind.To(new UiState<double>(current.Sensitivity)),
					},

					new UiChoiceInput
					{
						Key = OrbWidgetData.ScopeKey,
						Label = Strings.Orb.Config.Scope(),
						Description = UiText.Of(Strings.Orb.Config.ScopeHint()),
						Binding = Bind.To(new UiState<string>(Kebab(current.Scope))),
						Options = UiValue.Of<IReadOnlyList<UiOption>>(
						[
							Option(Kebab(SessionScope.Global), Strings.Orb.Config.ScopeOptions.Global()),
							Option(Kebab(SessionScope.Local), Strings.Orb.Config.ScopeOptions.Local()),
						]),
					},

					// The widget type declares exactly these groups in AppearanceProperties, so the host's
					// appearance actions reach the fields this builds.
					UiWidgetAppearance.Section(
						data,
						UiWidgetAppearanceFields.BackgroundColor
							| UiWidgetAppearanceFields.Label
							| UiWidgetAppearanceFields.LabelColor
							| UiWidgetAppearanceFields.Font
							| UiWidgetAppearanceFields.AccentColor),
				],
			},

			// The editor region is what a field list cannot hold. The host draws the widget preview itself
			// from the draft it accumulates, so this carries the action flows and nothing else. Without an
			// editor at all the panel is a single pane rather than a split.
			Editor = new UiWidgetEditor
			{
				Key = "editor",
				Children =
				[
					// The host runs a widget's event flows only from the top-level `flows` key, so the
					// actions editor is bound there rather than inside a nested object.
					new UiActionsListEditor
					{
						Key = OrbWidgetData.FlowsKey,
						Binding = Bind.To(new UiState<JsonElement>(StoredFlows(data))),
						CanRun = true,
					},
				],
			},
		};
	}

	/// <summary>
	/// A <see cref="UiState{T}"/> must hold a defined value: a default <see cref="JsonElement"/> has no JSON
	/// form and building the view throws naming the node and property.
	/// </summary>
	private static JsonElement StoredFlows(JsonElement data)
	{
		if (data.ValueKind == JsonValueKind.Object
			&& data.TryGetProperty(OrbWidgetData.FlowsKey, out var flows)
			&& flows.ValueKind == JsonValueKind.Array)
		{
			return flows;
		}

		return JsonSerializer.SerializeToElement(Array.Empty<object>());
	}

	/// <summary>
	/// The stored spelling is kebab-case, which is what a widget written by an older release holds, so a
	/// choice binds the string rather than the enum.
	/// </summary>
	private static string Kebab<TEnum>(TEnum value) where TEnum : struct, Enum
	{
		var name = value.ToString();
		var builder = new System.Text.StringBuilder(name.Length + 4);

		for (var index = 0; index < name.Length; index++)
		{
			if (char.IsUpper(name[index]))
			{
				if (index > 0)
				{
					builder.Append('-');
				}

				builder.Append(char.ToLowerInvariant(name[index]));
			}
			else
			{
				builder.Append(name[index]);
			}
		}

		return builder.ToString();
	}
}
