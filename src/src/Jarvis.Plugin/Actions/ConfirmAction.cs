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

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows;

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

			var approve = ActionParameters.ReadFlag(context.Parameters, ApproveParameter);
			session.ResolveConfirmation(approve);

			// Success, with no argument. The string overload is not a message parameter: it is the state id
			// the result expects to be in next, so passing the sentence told the host to expect a state
			// called "Approved run_shell.", which does not exist. The outcome is already visible through the
			// button state, so the sentence is logged rather than forced into a slot meant for an id.
			_logger.Information(
				"{Decision} {Tool}.",
				approve ? "Approved" : "Refused",
				pending.ToolName);

			return Task.FromResult(ActionResult.Success());
		}
	}
}