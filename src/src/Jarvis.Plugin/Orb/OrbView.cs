using Jarvis.Plugin.Core;
using MacroDeck.Localization;
using MacroDeck.Ui.Model.Resources;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;

namespace Jarvis.Plugin.Orb;

/// <summary>
/// What a press on the orb's own button means. Supplied by the session, so the view has no opinion about how
/// a turn is started or stopped and can be built and asserted without one.
/// </summary>
public sealed record OrbButtonActions(Action StartListening, Action Stop);

/// <summary>
/// Composes the orb, which is one image.
/// <para>
/// The orb is drawn analytically by <see cref="OrbFrameRenderer"/> into an animated GIF: the core, the glow
/// and the rings are all inside the asset, and the browser decodes it at its own frame rate. So the tree has
/// nothing to animate. A state change swaps one <c>source</c> on one node and costs a single
/// <c>set-properties</c> patch.
/// </para>
/// <para>
/// This used to carry a glow disc and a set of rotating rings as separate layers above the image. All three
/// drew the same thing, so the orb appeared twice at two sizes, and the rings were clipped by the widget
/// edge. They were also not free: a timer rewrote a rotation twenty-five times a second to animate a layer
/// that was already baked into the asset, which is a patch every forty milliseconds forever, for an
/// animation that plays itself.
/// </para>
/// </summary>
internal static class OrbView
{
	public static UiElement Build(
		OrbWidgetData data,
		UiState<AssistantState> orbState,
		UiState<UiResource?> core,
		UiState<string> reply,
		UiState<string> transcript,
		OrbButtonActions? buttons = null)
	{
		var layers = new List<UiElement> { CoreLayer(core) };

		if (data.ShowText)
		{
			layers.Add(TextLayer(data, orbState, reply, transcript));
		}

		// Buttons need something to press. A widget with no handler would render two controls that do
		// nothing at all, which is worse than not offering them.
		if (data.ShowButtons && buttons is not null)
		{
			layers.Add(ButtonBar(data, orbState, buttons));
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
	/// The animated core. All the smooth motion lives inside the asset, so the browser decodes it at its
	/// own frame rate and the whole orb costs exactly one patch when the state changes.
	/// <para>
	/// The state is read through <c>Value</c>, never <c>Peek</c>. A computed property records a dependency on
	/// each state it reads while the tree is being built, and only a recorded dependency is re-evaluated when
	/// that state is written. <c>Peek</c> reads without subscribing, so binding the image through it produced a
	/// tree that was correct once and then never again: the asset arrived, the state was written, and nothing
	/// was listening, so the widget stayed empty with no error anywhere.
	/// </para>
	/// </summary>
	private static UiImage CoreLayer(UiState<UiResource?> core) => new()
	{
		Key = "core-art",
		Source = UiValue.From(() => core.Value!),
		Size = UiSize.FromBasis(0.86),
	};

	private static UiTextRun TextLayer(
		OrbWidgetData data,
		UiState<AssistantState> orbState,
		UiState<string> reply,
		UiState<string> transcript)
	{
		return new UiTextRun
		{
			Key = "say",
			Text = UiText.From(() => Compose(data, orbState.Value, reply.Value, transcript.Value)),
			Size = UiSize.FromBasis(data.TextSize),
			Color = UiValue.Of(data.TextColor),
			Align = UiComponentAlignments.Center,
			MaxLines = MaxLinesFor(data.TextMode),
			Wrap = true,
			Role = UiComponentTextRoles.Primary,
		};
	}

	/// <summary>
	/// The orb's own controls. Two buttons, because two are what a turn needs and a third would only
	/// duplicate a deck button.
	/// <para>
	/// The stop button is hidden while nothing is running rather than greyed: a disabled control on a
	/// surface this small reads as broken rather than unavailable, and the state is already shown by the orb.
	/// </para>
	/// </summary>
	private static UiStack ButtonBar(
		OrbWidgetData data,
		UiState<AssistantState> orbState,
		OrbButtonActions buttons)
	{
		return new UiStack
		{
			Key = "buttons",
			Direction = UiComponentDirections.Horizontal,
			Justify = UiComponentJustify.Center,
			Align = UiComponentAlignments.Center,
			Gap = UiSize.FromBasis(0.03),
			Children =
			[
				OrbButton(
					"listen",
					Strings.Orb.Button.Listen(),
					data.AccentColor,
					() => buttons.StartListening()),

				// Present but inert rather than absent, so the bar does not change width as a turn starts
				// and stops. A control that is visibly there and does nothing when pressed is exactly what
				// Disabled is for.
				new UiModifier
				{
					Key = "stop-guard",
					Child = OrbButton(
						"stop",
						Strings.Orb.Button.Stop(),
						data.TextColor,
						() => buttons.Stop()),
					Disabled = UiValue.From(() => !IsBusy(orbState.Value)),
					Opacity = UiValue.From(() => IsBusy(orbState.Value) ? 1d : UiComponentModifiers.DimOpacity),
				},
			],
		};
	}

	/// <summary>
	/// Whether a turn is under way. Stop is offered for the states a cancel press would do something to, and
	/// hidden for the rest.
	/// </summary>
	internal static bool IsBusy(AssistantState state) => state is AssistantState.Listening
		or AssistantState.Thinking
		or AssistantState.Speaking
		or AssistantState.Executing
		or AssistantState.Confirming;

	private static UiButton OrbButton(string key, LocalizedString label, string colour, Action onPress) => new()
	{
		Key = key,
		Background = UiValue.Of(colour),
		Corner = UiValue.Of("round"),
		Padding = UiSize.FromBasis(0.02),
		Justify = UiComponentJustify.Center,
		Align = UiComponentAlignments.Center,
		Children =
		[
			new UiTextRun
			{
				Key = $"{key}-label",
				Text = UiText.Of(label),
				Size = UiSize.FromBasis(0.06),
				Color = UiValue.Of("#04121c"),
				Align = UiComponentAlignments.Center,
			},
		],
		Events = [UiEventHandler.On(UiComponentEvents.Press, onPress)],
	};

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
}