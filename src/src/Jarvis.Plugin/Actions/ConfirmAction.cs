using Jarvis.Plugin.Core;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;

namespace Jarvis.Plugin.Actions;

/// <summary>
/// Answers a confirmation JARVIS is waiting for.
/// <para>
/// Without this the default safety mode is not a policy but a denial: every tool that acts would ask and
/// nothing could ever answer, so the assistant could read the screen and nothing else. The orb shows the
/// waiting state, and this button is how the user says yes or no.
/// </para>
/// </summary>
public sealed class ConfirmAction(AssistantSession session) : IActionDefinition, IStateProviderActionDefinition
{
	private const string ApproveParameter = "approve";

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

	public IActionExecutor CreateExecutor() => new Executor(session);

	public TimeSpan StatePollInterval => TimeSpan.FromSeconds(1);

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
			// Nothing waiting is a legitimate no-op: the button can sit on a deck and be pressed at any time.
			if (session.PendingConfirmation is not { } pending)
			{
				return Task.FromResult(ActionResult.Success());
			}

			var approve = ActionParameters.ReadFlag(context.Parameters, ApproveParameter);
			session.ResolveConfirmation(approve);

			// ActionResult.Success takes a plain string, so the localized sentence is rendered through the
			// reader's language here rather than passed as a reference.
			var message = approve
				? Strings.Actions.Confirm.Approved(pending.ToolName).ToString()
				: Strings.Actions.Confirm.Refused(pending.ToolName).ToString();

			return Task.FromResult(ActionResult.Success(message));
		}
	}
}