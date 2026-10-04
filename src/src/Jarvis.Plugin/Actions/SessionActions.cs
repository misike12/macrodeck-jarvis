using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Speech;

namespace Jarvis.Plugin.Actions;

public static class ActionParameters
{
	public const string Mode = "mode";
	public const string Prompt = "prompt";
	public const string KillRunningCommand = "kill-running-command";
	public const string Text = "text";

	public static IReadOnlyList<ActionParameterOption> ModeOptions =>
	[
		new() { Value = "one-shot", Label = Strings.Parameter.Mode.OneShot.Label() },
		new() { Value = "conversation", Label = Strings.Parameter.Mode.Conversation.Label() },
	];

	/// <summary>
	/// Reads the mode, or refuses it.
	/// <para>
	/// The parameter is declared as a choice, but the host sends whatever is stored and nothing coerces
	/// the wire type, so an unrecognised value has to be a failure rather than a silent default. Defaulting
	/// an unknown mode to one-shot would run the wrong kind of turn while reporting that it was asked for.
	/// </para>
	/// </summary>
	public static bool TryReadMode(
		IReadOnlyDictionary<string, object> parameters,
		out ActivateMode mode,
		out string? rejected)
	{
		mode = ActivateMode.OneShot;
		rejected = null;

		if (parameters.GetValueOrDefault(Mode) is not string text)
		{
			return false;
		}

		switch (text)
		{
			case "one-shot":
				mode = ActivateMode.OneShot;
				return true;

			case "conversation":
				mode = ActivateMode.Conversation;
				return true;

			default:
				rejected = text;
				return false;
		}
	}

	public static bool ReadFlag(IReadOnlyDictionary<string, object> parameters, string name)
	{
		return parameters.GetValueOrDefault(name) switch
		{
			bool flag => flag,
			string text => bool.TryParse(text, out var parsed) && parsed,
			_ => false,
		};
	}

	public static string? ReadText(IReadOnlyDictionary<string, object> parameters, string name)
	{
		var raw = parameters.GetValueOrDefault(name);
		var text = raw?.ToString();
		return string.IsNullOrWhiteSpace(text) ? null : text;
	}
}

public sealed class ActivateAction(AssistantSession session, ListeningPipeline listening) : IActionDefinition, IStateProviderActionDefinition
{
	public string Id => "jarvis-activate";

	public LocalizedText Name => Strings.Actions.Activate.Name();

	public LocalizedText Description => Strings.Actions.Activate.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Choice(
			ActionParameters.Mode,
			ActionParameters.ModeOptions,
			label: Strings.Actions.Activate.Mode.Label(),
			description: Strings.Actions.Activate.Mode.Description(),
			defaultValue: "one-shot",
			required: true),
		ActionParameter.Text(
			ActionParameters.Prompt,
			label: Strings.Actions.Activate.Prompt.Label(),
			description: Strings.Actions.Activate.Prompt.Description()),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows;

	public IActionExecutor CreateExecutor() => new Executor(listening);

	public Task<ActionStateSnapshot?> GetActionStateAsync(
		IReadOnlyDictionary<string, object?> parameters,
		CancellationToken cancellationToken)
	{
		return Task.FromResult<ActionStateSnapshot?>(AssistantStateReader.Read(session));
	}

	private sealed class Executor(ListeningPipeline listening) : IActionExecutor
	{
		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (!ActionParameters.TryReadMode(context.Parameters, out _, out var rejected))
			{
				return Task.FromResult(ActionResult.Failed(
					ActionErrorCodes.InvalidParameter,
					rejected is null
						? Strings.Actions.Activate.Mode.Required()
						: Strings.Actions.Activate.Mode.Unknown(rejected)));
			}

			var prompt = ActionParameters.ReadText(context.Parameters, ActionParameters.Prompt);

			// Every press goes through the voice loop. It used to be split: a wait-for-wake-word press took a
			// path that opened no microphone, started no turn and reported success anyway.
			return listening.ListenAndAnswerAsync(prompt, context.CancellationToken);
		}
	}
}

public sealed class CancelAction(AssistantSession session) : IActionDefinition, IStateProviderActionDefinition
{
	public string Id => "jarvis-cancel";

	public LocalizedText Name => Strings.Actions.Cancel.Name();

	public LocalizedText Description => Strings.Actions.Cancel.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Toggle(
			ActionParameters.KillRunningCommand,
			label: Strings.Actions.Cancel.KillRunningCommand.Label(),
			description: Strings.Actions.Cancel.KillRunningCommand.Description()),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows;

	public IActionExecutor CreateExecutor() => new Executor(session);

	public Task<ActionStateSnapshot?> GetActionStateAsync(
		IReadOnlyDictionary<string, object?> parameters,
		CancellationToken cancellationToken)
	{
		return Task.FromResult<ActionStateSnapshot?>(AssistantStateReader.Read(session));
	}

	private sealed class Executor(AssistantSession session) : IActionExecutor
	{
		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var kill = ActionParameters.ReadFlag(context.Parameters, ActionParameters.KillRunningCommand);
			return Task.FromResult(session.Cancel(kill));
		}
	}
}

public sealed class ToggleAction(AssistantSession session, ListeningPipeline listening)
	: IActionDefinition, IStateProviderActionDefinition
{
	public string Id => "jarvis-toggle";

	public LocalizedText Name => Strings.Actions.Toggle.Name();

	public LocalizedText Description => Strings.Actions.Toggle.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Choice(
			ActionParameters.Mode,
			ActionParameters.ModeOptions,
			label: Strings.Actions.Toggle.Mode.Label(),
			description: Strings.Actions.Toggle.Mode.Description(),
			defaultValue: "one-shot",
			required: true),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows;

	public IActionExecutor CreateExecutor() => new Executor(session, listening);

	public Task<ActionStateSnapshot?> GetActionStateAsync(
		IReadOnlyDictionary<string, object?> parameters,
		CancellationToken cancellationToken)
	{
		return Task.FromResult<ActionStateSnapshot?>(AssistantStateReader.Read(session));
	}

	private sealed class Executor(AssistantSession session, ListeningPipeline listening) : IActionExecutor
	{
		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (!ActionParameters.TryReadMode(context.Parameters, out _, out var rejected))
			{
				return Task.FromResult(ActionResult.Failed(
					ActionErrorCodes.InvalidParameter,
					rejected is null
						? Strings.Actions.Toggle.Mode.Required()
						: Strings.Actions.Toggle.Mode.Unknown(rejected)));
			}

			// A toggle that was not running has to start a real turn, so it goes through the same voice loop
			// as the activate button rather than a path that only marked the session as running.
			if (!session.IsRunning)
			{
				return listening.ListenAndAnswerAsync(null, context.CancellationToken);
			}

			return Task.FromResult(session.Cancel(false));
		}
	}
}

public sealed class SayAction(AssistantSession session) : IActionDefinition
{
	public string Id => "jarvis-say";

	public LocalizedText Name => Strings.Actions.Say.Name();

	public LocalizedText Description => Strings.Actions.Say.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Text(
			ActionParameters.Text,
			label: Strings.Actions.Say.Prompt.Label(),
			description: Strings.Actions.Say.Prompt.Description(),
			required: true),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows;

	public IActionExecutor CreateExecutor() => new Executor(session);

	private sealed class Executor(AssistantSession session) : IActionExecutor
	{
public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var text = ActionParameters.ReadText(context.Parameters, ActionParameters.Text);
			return session.SayAsync(text ?? string.Empty, context.CancellationToken);
		}
	}
}
