using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Jarvis.Plugin.Core;

namespace Jarvis.Plugin.Actions;

public static class ActionParameters
{
	public const string Mode = "mode";
	public const string Prompt = "prompt";
	public const string WaitForWakeWord = "wait-for-wake-word";
	public const string TimeoutSeconds = "timeout-seconds";
	public const string KillRunningCommand = "kill-running-command";
	public const string Text = "text";

	public static IReadOnlyList<ActionParameterOption> ModeOptions =>
	[
		new() { Value = "one-shot", Label = Strings.Parameter.Mode.OneShot.Label() },
		new() { Value = "conversation", Label = Strings.Parameter.Mode.Conversation.Label() },
		new() { Value = "wait-for-wake-word", Label = Strings.Parameter.Mode.WaitForWakeWord.Label() },
	];

	public static ActivateMode ReadMode(IReadOnlyDictionary<string, object> parameters)
	{
		if (parameters.GetValueOrDefault(Mode) is not string text)
		{
			return ActivateMode.OneShot;
		}

		return text switch
		{
			"conversation" => ActivateMode.Conversation,
			"wait-for-wake-word" => ActivateMode.WaitForWakeWord,
			_ => ActivateMode.OneShot,
		};
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

	public static int ReadSeconds(IReadOnlyDictionary<string, object> parameters, string name, int fallback)
	{
		var raw = parameters.GetValueOrDefault(name);
		return raw switch
		{
			double number => (int)Math.Clamp(number, 1, 600),
			int integer => Math.Clamp(integer, 1, 600),
			string text when int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
				=> Math.Clamp(parsed, 1, 600),
			_ => fallback,
		};
	}

	public static string? ReadText(IReadOnlyDictionary<string, object> parameters, string name)
	{
		var raw = parameters.GetValueOrDefault(name);
		var text = raw?.ToString();
		return string.IsNullOrWhiteSpace(text) ? null : text;
	}
}

public sealed class ActivateAction(AssistantSession session) : IActionDefinition, IStateProviderActionDefinition
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
		ActionParameter.Toggle(
			ActionParameters.WaitForWakeWord,
			label: Strings.Actions.Activate.WaitForWakeWord.Label(),
			description: Strings.Actions.Activate.WaitForWakeWord.Description()),
		ActionParameter.Number(
			ActionParameters.TimeoutSeconds,
			label: Strings.Actions.Activate.TimeoutSeconds.Label(),
			description: Strings.Actions.Activate.TimeoutSeconds.Description(),
			min: 1,
			max: 600,
			defaultValue: 20),
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
			var mode = ActionParameters.ReadMode(context.Parameters);
			var prompt = ActionParameters.ReadText(context.Parameters, ActionParameters.Prompt);
			var waitForWakeWord = ActionParameters.ReadFlag(context.Parameters, ActionParameters.WaitForWakeWord);
			var timeout = ActionParameters.ReadSeconds(context.Parameters, ActionParameters.TimeoutSeconds, 20);

			return Task.FromResult(session.Activate(mode, prompt, waitForWakeWord, timeout));
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

public sealed class ToggleAction(AssistantSession session) : IActionDefinition, IStateProviderActionDefinition
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
			var mode = ActionParameters.ReadMode(context.Parameters);
			return Task.FromResult(session.Toggle(mode));
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