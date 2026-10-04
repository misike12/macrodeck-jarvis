using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;
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

	/// <summary>
	/// Reads a flag, or refuses it.
	/// <para>
	/// A parameter declared as a boolean arrives as whatever the host has stored, and the host does not
	/// coerce the wire type. The old reader mapped "not a bool, not a parseable bool" to false, so a string
	/// <c>"yes"</c> or a number silently became the opposite of what the caller sent and the action reported
	/// that it had done that.
	/// </para>
	/// </summary>
	public static bool TryReadFlag(
		IReadOnlyDictionary<string, object> parameters,
		string name,
		out bool value,
		out string? rejected)
	{
		value = false;
		rejected = null;

		switch (parameters.GetValueOrDefault(name))
		{
			case null:
				return true;

			case bool flag:
				value = flag;
				return true;

			case string text when bool.TryParse(text, out var parsed):
				value = parsed;
				return true;

			default:
				rejected = parameters.GetValueOrDefault(name)?.ToString() ?? string.Empty;
				return false;
		}
	}

	/// <summary>
	/// Reads a text parameter.
	/// <para>
	/// Only a string. The old version called <c>ToString()</c> on whatever arrived, so a numeric parameter
	/// came back as its digits and the model was told its own number was text.
	/// </para>
	/// </summary>
	public static string? ReadText(IReadOnlyDictionary<string, object> parameters, string name) =>
		parameters.GetValueOrDefault(name) is string text && !string.IsNullOrWhiteSpace(text) ? text : null;
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
						? Strings.Actions.Activate.Mode.Required()
						: Strings.Actions.Activate.Mode.Unknown(rejected)));
			}

			var prompt = ActionParameters.ReadText(context.Parameters, ActionParameters.Prompt);

			// Refused here rather than inside the turn. A missing model is known before a turn starts, and a
			// press that has already returned cannot report it afterwards: the tile would claim the turn began
			// and the reason would arrive later as speech nobody asked for.
			if (!session.HasLlmCredentials)
			{
				return Task.FromResult(ActionResult.Failed(
					ActionErrorCodes.NotConfigured, Strings.Errors.NoModelConfigured()));
			}

			// Every press goes through the voice loop. It used to be split: a wait-for-wake-word press took a
			// path that opened no microphone, started no turn and reported success anyway.
			return Task.FromResult(ActionBudget.StartTurn(
				session,
				session.Logger,
				token => listening.ListenAndAnswerAsync(prompt, token)));
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
			if (!ActionParameters.TryReadFlag(
				context.Parameters, ActionParameters.KillRunningCommand, out var kill, out var rejected))
			{
				return Task.FromResult(ActionResult.Failed(
					ActionErrorCodes.InvalidParameter,
					Strings.Errors.UnknownFlag(ActionParameters.KillRunningCommand, rejected)));
			}

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
				if (!session.HasLlmCredentials)
				{
					return Task.FromResult(ActionResult.Failed(
						ActionErrorCodes.NotConfigured, Strings.Errors.NoModelConfigured()));
				}

				return Task.FromResult(ActionBudget.StartTurn(
					session,
					session.Logger,
					token => listening.ListenAndAnswerAsync(null, token)));
			}

			return Task.FromResult(session.Cancel(false));
		}
	}
}

/// <summary>
/// Applies the host's thirty second capability ceiling to work started from a deck button.
/// <para>
/// A turn can legitimately take minutes: a model call, a wait for a person to confirm, a slow tool. None of
/// that is a problem for the hotkey or the wake word, which are not capability invocations. It is a problem
/// for a button press, because the host cancels the invocation at thirty seconds and reclaims the slot while
/// the turn keeps running inside it. So the ceiling is applied here, where the press is known to be a press.
/// </para>
/// <para>
/// The returned source is disposed by the caller's using, which also cancels anything still running.
/// </para>
/// </summary>
/// <summary>
/// Starts a turn and lets the press go.
/// <para>
/// A turn cannot be awaited by the button that starts it. The host's <c>CapabilityInvoke</c> ceiling is thirty
/// seconds and it is a protocol constant, not a setting: past it the host cancels the invocation and throws
/// the result away. An answer that takes longer than that was never going to fit, so the press used to be
/// capped just inside the ceiling, which meant a spoken answer was cut off at the point where the model was
/// still talking. Raising the cap does not help, because the next wall is the host's and it is not ours to
/// move.
/// </para>
/// <para>
/// So the turn runs on its own token, bounded by the conversation timeout, and the press returns
/// <see cref="ActionResult.Accepted"/> immediately. The answer arrives as speech and in the orb, which is
/// where an assistant's answer belongs anyway. The session already owns a turn that outlives an invocation
/// and a cancel button that stops it, so nothing new is left running unowned.
/// </para>
/// <para>
/// The result is <c>Accepted</c> rather than <c>Succeeded</c> on purpose: the work was taken and will be
/// heard, but at the moment of the press nobody can confirm it finished, and reporting success for an answer
/// that has not been produced yet would be a lie the user can see through.
/// </para>
/// </summary>
public static class ActionBudget
{
	public static ActionResult StartTurn(
		AssistantSession session,
		ILogger logger,
		Func<CancellationToken, Task<ActionResult>> work)
	{
		var budget = new CancellationTokenSource(session.ConversationBudget);

		_ = Task.Run(
			async () =>
			{
				try
				{
					await work(budget.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					// The turn was cancelled, or ran out of its conversation timeout. Either way the session
					// has already put its own state back, and the user has heard whatever was said.
				}
				catch (Exception exception) when (exception is not OutOfMemoryException)
				{
					// Nothing is left to return this to, so it would otherwise vanish without a trace.
					logger.Warning(exception, "A turn that had already left its button failed.");
				}
				finally
				{
					budget.Dispose();
				}
			},
			CancellationToken.None);

		return ActionResult.Accepted(Strings.Errors.TurnStarted());
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

	public IActionExecutor CreateExecutor() => new Executor(session);

	private sealed class Executor(AssistantSession session) : IActionExecutor
	{
		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var text = ActionParameters.ReadText(context.Parameters, ActionParameters.Text);

			// The prompt is validated here rather than inside the turn, because the turn has already left by
			// the time it would report a problem and the press would show as started either way.
			if (text is null)
			{
				return Task.FromResult(ActionResult.Failed(
					ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Actions.Say.Prompt.Label())));
			}

			if (!session.HasLlmCredentials)
			{
				return Task.FromResult(ActionResult.Failed(
					ActionErrorCodes.NotConfigured, Strings.Errors.NoModelConfigured()));
			}

			return Task.FromResult(ActionBudget.StartTurn(
				session,
				session.Logger,
				token => session.SayAsync(text, token)));
		}
	}
}
