using Jarvis.Plugin.Core;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace Jarvis.Plugin.Actions;

/// <summary>
/// Answers a confirmation JARVIS is waiting for.
/// <para>
/// Without this the default safety mode is not a policy but a denial: every tool that acts would ask and
/// nothing could ever answer, so the assistant could read the screen and nothing else. The orb shows the
/// waiting state, and this button is how the user says yes or no.
/// </para>
/// </summary>
public sealed class ConfirmAction(AssistantSession session, ILogger logger)
	: IActionDefinition, IStateProviderActionDefinition
{
	private const string ApproveParameter = "approve";

	private readonly ILogger _logger = logger.ForContext<ConfirmAction>();

	public string Id => "jarvis-confirm";

	public LocalizedText Name => Strings.Actions.Confirm.Name();

	public LocalizedText Description => Strings.Actions.Confirm.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Toggle(
			ApproveParameter,
			label: Strings.Actions.Confirm.Approve.Label(),
			description: Strings.Actions.Confirm.Approve.Description()),
	];

	public IActionExecutor CreateExecutor() => new Executor(session, _logger);

	public TimeSpan StatePollInterval => TimeSpan.FromSeconds(1);

	public Task<ActionStateSnapshot?> GetActionStateAsync(
		IReadOnlyDictionary<string, object?> parameters,
		CancellationToken cancellationToken)
	{
		return Task.FromResult<ActionStateSnapshot?>(AssistantStateReader.Read(session));
	}

private sealed class Executor(AssistantSession session, ILogger logger) : IActionExecutor
	{
		private readonly ILogger _logger = logger.ForContext<ConfirmAction.Executor>();

		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			// Nothing waiting is a legitimate no-op: the button can sit on a deck and be pressed at any time.
			if (session.PendingConfirmation is not { } pending)
			{
				return Task.FromResult(ActionResult.Success());
			}

if (!ActionParameters.TryReadFlag(context.Parameters, ApproveParameter, out var approve, out var rejected))
			{
				return Task.FromResult(ActionResult.Failed(
					ActionErrorCodes.InvalidParameter,
					Strings.Errors.UnknownFlag(ApproveParameter, rejected)));
			}

session.ResolveConfirmation(approve);

			// The sentence belongs in the message the user sees, not in the state-id slot. The outcome is
			// already observable through the button state, so the result stays a bare Success and the
			// localized sentence carries the tool name.
			var sentence = approve
				? Strings.Actions.Confirm.Approved(pending.ToolName)
				: Strings.Actions.Confirm.Refused(pending.ToolName);

			_logger.Information("{Decision}", sentence);

			return Task.FromResult(ActionResult.Success());
		}
	}
}