using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Plugin.Core;

namespace Jarvis.Plugin.Orb;

/// <summary>
/// The orb widget's stored configuration. A plugin cannot read the host's widget store, so this arrives
/// on the surface; every read is therefore defensive, because a widget placed before a release that
/// added a key must still draw.
/// </summary>
public sealed record OrbWidgetData
{
	public const string PresetKey = "preset";
	public const string RingCountKey = "ringCount";
	public const string RingSpeedKey = "ringSpeed";
	public const string RingRotationKey = "ringRotation";
	public const string ShowTextKey = "showText";
	public const string TextModeKey = "textMode";
	public const string TextSizeKey = "textSize";
	public const string TextColorKey = "textColor";
	public const string AccentKey = "accentColor";
	public const string CoreColorKey = "coreColor";
	public const string GlowKey = "glow";
	public const string BorderStyleKey = "borderStyle";
	public const string AudioReactiveKey = "audioReactive";
	public const string SensitivityKey = "sensitivity";
	public const string ScopeKey = "scope";
	public const string FlowsKey = "flows";

	public const string DefaultJson = """
		{
		  "preset": "arc-reactor",
		  "ringCount": 3,
		  "ringSpeed": 1.0,
		  "ringRotation": true,
		  "showText": true,
		  "textMode": "interim-plus-reply",
		  "textSize": 0.055,
		  "textColor": "#dff6ff",
		  "accentColor": "#4fd2ff",
		  "coreColor": "#0b3d63",
		  "glow": true,
		  "borderStyle": "comet",
		  "audioReactive": true,
		  "sensitivity": 1.0,
		  "scope": "global"
		}
		""";

	public const string Schema = """
		{
		  "type": "object",
		  "properties": {
		    "preset":         { "type": "string", "enum": ["arc-reactor", "halo", "pulse", "custom"] },
		    "ringCount":      { "type": "integer", "minimum": 0, "maximum": 6 },
		    "ringSpeed":      { "type": "number",  "minimum": 0, "maximum": 4 },
		    "ringRotation":   { "type": "boolean" },
		    "showText":       { "type": "boolean" },
		    "textMode":       { "type": "string", "enum": ["full-transcript", "last-reply", "interim-plus-reply"] },
		    "textSize":       { "type": "number",  "minimum": 0.02, "maximum": 0.2 },
		    "textColor":      { "type": "string" },
		    "accentColor":    { "type": "string" },
		    "coreColor":      { "type": "string" },
		    "glow":           { "type": "boolean" },
		    "borderStyle":    { "type": "string" },
		    "audioReactive":  { "type": "boolean" },
		    "sensitivity":    { "type": "number",  "minimum": 0.1, "maximum": 3 },
		    "scope":          { "type": "string", "enum": ["global", "local"] },
		    "flows":          { "type": "array" },
		    "backgroundColor":{ "type": "string" },
		    "label":          { "type": "string" },
		    "labelColor":     { "type": "string" },
		    "fontFaceId":     { "type": "string" },
		    "fontSize":       { "type": "string" },
		    "textAlign":      { "type": "string" },
		    "labelPosition":  { "type": "string" },
		    "accentColor2":   { "type": "string" },
		    "border":         { "type": "object" }
		  }
		}
		""";

	public OrbPreset Preset { get; init; } = OrbPreset.ArcReactor;

	public int RingCount { get; init; } = 3;

	public double RingSpeed { get; init; } = 1.0;

	public bool RingRotation { get; init; } = true;

	public bool ShowText { get; init; } = true;

	public TextDisplayMode TextMode { get; init; } = TextDisplayMode.InterimPlusReply;

	public double TextSize { get; init; } = 0.055;

	public string TextColor { get; init; } = "#dff6ff";

	public string AccentColor { get; init; } = "#4fd2ff";

	public string CoreColor { get; init; } = "#0b3d63";

	public bool Glow { get; init; } = true;

	public string BorderStyle { get; init; } = "comet";

	public bool AudioReactive { get; init; } = true;

	public double Sensitivity { get; init; } = 1.0;

	public SessionScope Scope { get; init; } = SessionScope.Global;

	public static OrbWidgetData Parse(JsonElement element)
	{
		var fallback = new OrbWidgetData();

		if (element.ValueKind != JsonValueKind.Object)
		{
			return fallback;
		}

		return new OrbWidgetData
		{
			Preset = ReadEnum(element, PresetKey, fallback.Preset),
			RingCount = Math.Clamp(ReadInt(element, RingCountKey, fallback.RingCount), 0, 6),
			RingSpeed = Math.Clamp(ReadDouble(element, RingSpeedKey, fallback.RingSpeed), 0, 4),
			RingRotation = ReadBool(element, RingRotationKey, fallback.RingRotation),
			ShowText = ReadBool(element, ShowTextKey, fallback.ShowText),
			TextMode = ReadEnum(element, TextModeKey, fallback.TextMode),
			TextSize = Math.Clamp(ReadDouble(element, TextSizeKey, fallback.TextSize), 0.02, 0.2),
			TextColor = ReadString(element, TextColorKey, fallback.TextColor),
			AccentColor = ReadString(element, AccentKey, fallback.AccentColor),
			CoreColor = ReadString(element, CoreColorKey, fallback.CoreColor),
			Glow = ReadBool(element, GlowKey, fallback.Glow),
			BorderStyle = ReadString(element, BorderStyleKey, fallback.BorderStyle),
			AudioReactive = ReadBool(element, AudioReactiveKey, fallback.AudioReactive),
			Sensitivity = Math.Clamp(ReadDouble(element, SensitivityKey, fallback.Sensitivity), 0.1, 3),
			Scope = ReadEnum(element, ScopeKey, fallback.Scope),
		};
	}

	private static string ReadString(JsonElement element, string name, string fallback) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? fallback
			: fallback;

	private static bool ReadBool(JsonElement element, string name, bool fallback) =>
		element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
			? value.GetBoolean()
			: fallback;

	private static int ReadInt(JsonElement element, string name, int fallback) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
			? value.GetInt32()
			: fallback;

	private static double ReadDouble(JsonElement element, string name, double fallback) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
			? value.GetDouble()
			: fallback;

	private static T ReadEnum<T>(JsonElement element, string name, T fallback) where T : struct, Enum =>
		element.TryGetProperty(name, out var value)
			&& value.ValueKind == JsonValueKind.String
			&& Enum.TryParse<T>(value.GetString(), true, out var parsed)
				? parsed
				: fallback;

	/// <summary>
	/// Written by the configuration tree, so it validates the shape before the widget can store an
	/// unreadable payload.
	/// </summary>
	public static JsonNode? Describe(JsonNode? node)
	{
		return node is JsonObject obj
			? obj.DeepClone().AsObject()
			: JsonNode.Parse(DefaultJson);
	}
}