using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Jarvis.Plugin.Core;

namespace Jarvis.Plugin.Actions;

/// <summary>
/// The one place the button states are defined. Every action that shows state reads it from here so
/// all three buttons agree on colour and label, and <c>unavailable</c> is always present because a
/// host that has no configuration yet still has to render something.
/// </summary>
public static class AssistantStateReader
{
	private const string UnavailableId = "unavailable";
	private const string IdleId = "idle";
	private const string ListeningId = "listening";
	private const string ThinkingId = "thinking";
	private const string SpeakingId = "speaking";
	private const string ExecutingId = "executing";
	private const string ConfirmingId = "confirming";
	private const string ErrorId = "error";

	private static readonly ActionStateDefinition[] States =
	[
		new(UnavailableId, Strings.States.Unavailable())
			{ DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#4a5568", LabelColor = "#cbd5e0" } },
		new(IdleId, Strings.States.Idle())
			{ DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#1a202c", LabelColor = "#a0aec0" } },
		new(ListeningId, Strings.States.Listening())
			{ DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#2b6cb0", LabelColor = "#ffffff" } },
		new(ThinkingId, Strings.States.Thinking())
			{ DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#6b46c1", LabelColor = "#ffffff" } },
		new(SpeakingId, Strings.States.Speaking())
			{ DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#2f855a", LabelColor = "#ffffff" } },
		new(ExecutingId, Strings.States.Executing())
			{ DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#b7791f", LabelColor = "#ffffff" } },
		new(ConfirmingId, Strings.States.Confirming())
			{ DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#c05621", LabelColor = "#ffffff" } },
		new(ErrorId, Strings.States.Error())
			{ DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#c53030", LabelColor = "#ffffff" } },
	];

	public static ActionStateSnapshot Read(AssistantSession session)
	{
		var snapshot = session.StateSnapshot;

		var active = snapshot.State switch
		{
			AssistantState.Listening => ListeningId,
			AssistantState.Thinking => ThinkingId,
			AssistantState.Speaking => SpeakingId,
			AssistantState.Executing => ExecutingId,
			AssistantState.Confirming => ConfirmingId,
			AssistantState.Error => ErrorId,
			AssistantState.Unavailable => UnavailableId,
			_ => IdleId,
		};

		return new ActionStateSnapshot(States, active);
	}
}