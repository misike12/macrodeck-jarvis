using Jarvis.Plugin.Core;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// A deck button press has to finish inside the host's capability ceiling.
/// <para>
/// The host cancels an invocation after thirty seconds and reclaims the concurrency slot. Nothing in the
/// plugin enforced that: the confirmation gate alone could hold a slot for ten minutes, so a press the host
/// had already abandoned went on running inside it, still writing history and still speaking.
/// </para>
/// <para>
/// This test is about the relationship between two numbers, so it is worth stating rather than only
/// exercising: if the budget is ever raised to or past the ceiling, the whole mechanism stops working and
/// the failure is invisible until a user presses a button.
/// </para>
/// </summary>
[TestFixture]
public class ActionBudgetTests
{
	/// <summary>The host's own bound, from ProtocolTimeouts.CapabilityInvoke.</summary>
	private static readonly TimeSpan ProtocolCeiling = TimeSpan.FromSeconds(30);

	[Test]
	public void The_action_budget_leaves_room_inside_the_host_ceiling()
	{
		Assert.That(
			AssistantSession.ActionBudget,
			Is.LessThan(ProtocolCeiling),
			"a turn that runs to the ceiling is already cancelled by the host when it tries to answer, so the "
				+ "result is thrown away and the slot was taken back regardless");
	}

	/// <summary>
	/// Enough margin to unwind. The budget firing at the ceiling would race the host's own cancellation, and
	/// which of the two won would decide whether the user saw a failure or silence.
	/// </summary>
	[Test]
	public void The_action_budget_leaves_room_to_unwind()
	{
		var margin = ProtocolCeiling - AssistantSession.ActionBudget;

		Assert.That(
			margin,
			Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(5)),
			"too little margin between the budget and the ceiling to log and return before the host gives up");
	}

	/// <summary>
	/// The budget belongs to a press, not to a turn. The hotkey and wake word have no ceiling, so the budget
	/// must not be applied inside the session or the pipeline, only where a press is known to be a press.
	/// </summary>
	[Test]
	public void The_budget_is_not_applied_inside_the_session_or_the_pipeline()
	{
		var plugin = PluginDirectory();

		Assert.Multiple(() =>
		{
			Assert.That(
				File.ReadAllText(Path.Combine(plugin, "Core", "AssistantSession.cs")),
				Does.Not.Contain("CancelAfter(ActionBudget)"),
				"the session applies the action budget to every turn, including the hotkey and the wake word");

			Assert.That(
				File.ReadAllText(Path.Combine(plugin, "Speech", "ListeningPipeline.cs")),
				Does.Not.Contain("ActionBudget"),
				"the voice pipeline applies the action budget, which caps the hotkey and wake word too");
		});
	}

	private static string PluginDirectory()
	{
		var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, "src", "Jarvis.Plugin");

			if (Directory.Exists(candidate))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("Could not locate the plugin directory above the test output.");
	}
}