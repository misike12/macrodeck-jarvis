using Jarvis.Plugin.Core;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Model.Resources;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;

namespace Jarvis.Plugin.Orb;

/// <summary>
/// Composes the orb. Every animated quantity is a <see cref="UiState{T}"/> the tree reads, so a change
/// emits one <c>set-properties</c> patch on one node rather than rebuilding anything.
/// </summary>
internal static class OrbView
{
	public static UiElement Build(
		OrbWidgetData data,
		UiState<AssistantState> orbState,
		UiState<UiResource?> core,
		UiState<double> sweep,
		UiState<string> reply,
		UiState<string> transcript)
	{
		var layers = new List<UiElement>
		{
			GlowLayer(data),
			CoreLayer(core),
		};

		layers.AddRange(RingLayers(data, sweep));

		if (data.ShowText)
		{
			layers.Add(TextLayer(data, orbState, reply, transcript));
		}

		return new UiStack
		{
			Key = "orb",
			Direction = UiComponentDirections.Vertical,
			Justify = UiComponentJustify.Center,
			Align = UiComponentAlignments.Center,
			Background = "transparent",
			Fill = true,
			Children = layers,
		};
	}

/// <summary>
	/// A radial glow behind the core. Static geometry on purpose: the smoothness comes from the animated
	/// core asset, so nothing here costs a patch.
	/// <para>
	/// <c>Fill</c> is on the modifier, not on its child. A modifier is a wrapper: the host lays its child
	/// out inside it, and a child of a wrapper may not set <c>MainSize</c>, <c>Fill</c>, <c>ColumnSpan</c>
	/// or <c>RowSpan</c>. Setting it on the child threw <c>UiViewException</c> while the widget session was
	/// opening, which surfaced as an empty widget and a repeated <c>session.open</c> failure.
	/// </para>
	/// </summary>
	private static UiModifier GlowLayer(OrbWidgetData data) => new()
	{
		Key = "glow",
		RequiredComponentVersion = 2,
		Background = data.Glow ? Gradient(data) : UiBackground.Solid(data.CoreColor),
		Clip = UiComponentClips.Circle,
		Fill = true,
		Child = new UiStack
		{
			Key = "glow-fill",
			Children = [],
		},
	};

	private static UiGradient Gradient(OrbWidgetData data) => UiGradient.Radial(
		0.5,
		0.5,
		[
			new UiGradientStop { Offset = 0, Color = data.AccentColor },
			new UiGradientStop { Offset = 0.55, Color = Mix(data.CoreColor, data.AccentColor, 0.25) },
			new UiGradientStop { Offset = 1, Color = data.CoreColor },
		]);

	/// <summary>
	/// The animated core. All the smooth motion lives inside the asset, so the browser decodes it at its
	/// own frame rate and the whole orb costs exactly one patch when the state changes.
	/// </summary>
	private static UiImage CoreLayer(UiState<UiResource?> core) => new()
	{
		Key = "core-art",
		Source = UiValue.From(() => core.Peek()!),
		Size = UiSize.FromBasis(0.86),
	};

	/// <summary>
	/// Each ring is a <c>ui.transform</c> whose only animated property is <c>rotation</c>, which is the
	/// framework's own documented idiom for a sweeping element. Rings counter-rotate so the
	/// composition never looks like one rigid disc.
	/// </summary>
	private static IEnumerable<UiElement> RingLayers(OrbWidgetData data, UiState<double> sweep)
	{
		for (var index = 0; index < data.RingCount; index++)
		{
			var direction = index % 2 == 0 ? 1 : -1;
			var offset = index * 120;

			yield return new UiTransform
			{
				Key = $"ring{index}",
				Rotation = UiValue.From(() => ((sweep.Peek() * direction) + offset) % 360.0),
				OriginX = 0.5,
				OriginY = 0.5,
				Children = [],
			};
		}
	}

	private static UiTextRun TextLayer(
		OrbWidgetData data,
		UiState<AssistantState> orbState,
		UiState<string> reply,
		UiState<string> transcript)
	{
		return new UiTextRun
		{
			Key = "say",
			Text = UiText.From(() => Compose(data, orbState.Peek(), reply.Peek(), transcript.Peek())),
			Size = UiSize.FromBasis(data.TextSize),
			Color = UiValue.Of(data.TextColor),
			Align = UiComponentAlignments.Center,
			MaxLines = MaxLinesFor(data.TextMode),
			Wrap = true,
			Role = UiComponentTextRoles.Primary,
		};
	}

	private static int MaxLinesFor(TextDisplayMode mode) => mode switch
	{
		TextDisplayMode.LastReply => 4,
		TextDisplayMode.InterimPlusReply => 3,
		_ => 6,
	};

	/// <summary>
	/// Interim speech recognition text while listening, then the reply. Both come from state the session
	/// already holds, so an interim update costs one property patch.
	/// </summary>
	private static string Compose(OrbWidgetData data, AssistantState orbState, string reply, string transcript)
	{
		if (orbState == AssistantState.Listening && transcript.Length > 0)
		{
			return data.TextMode == TextDisplayMode.LastReply ? string.Empty : transcript;
		}

		return orbState switch
		{
			AssistantState.Thinking => reply,
			AssistantState.Unavailable => string.Empty,
			_ => reply,
		};
	}

	private static string Mix(string from, string to, double amount)
	{
		if (!TryParse(from, out var r1, out var g1, out var b1) || !TryParse(to, out var r2, out var g2, out var b2))
		{
			return from;
		}

		var r = (int)Math.Round(r1 + ((r2 - r1) * amount));
		var g = (int)Math.Round(g1 + ((g2 - g1) * amount));
		var b = (int)Math.Round(b1 + ((b2 - b1) * amount));

		return $"#{r:x2}{g:x2}{b:x2}";
	}

	private static bool TryParse(string hex, out int r, out int g, out int b)
	{
		r = g = b = 0;

		if (hex.Length != 7 || hex[0] != '#')
		{
			return false;
		}

		return int.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out r)
			&& int.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
			&& int.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
	}
}