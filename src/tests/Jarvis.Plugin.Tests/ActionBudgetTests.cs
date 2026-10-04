using Jarvis.Plugin.Actions;
using Jarvis.Plugin.Core;
using MacroDeck.Plugin.Protocol.Limits;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The relationship between a turn and the host's capability ceiling.
/// <para>
/// The host's <c>ProtocolTimeouts.CapabilityInvoke</c> is thirty seconds and it is a protocol constant, not
/// a setting, so an invocation that runs past it is cancelled and its result discarded. A turn used to be
/// awaited by the button that started it, capped just inside that ceiling, which meant a long spoken answer
/// was cut off mid sentence. The cap could not simply be raised, because the next wall is the host's and it
/// is not ours to move.
/// </para>
/// <para>
/// So a turn now leaves the press instead of being held by it. These tests exist to keep it that way, and to
/// keep the honest consequence: the button reports <c>Accepted</c>, not <c>Succeeded</c>, because at the
/// moment of the press the answer does not exist yet.
/// </para>
/// </summary>
[TestFixture]
public class ActionBudgetTests
{
	private static readonly string[] OnlyDetached = ["StartTurn"];

	/// <summary>The host's own bound, read from the protocol rather than restated.</summary>
	private static TimeSpan ProtocolCeiling => ProtocolTimeouts.CapabilityInvoke;

	/// <summary>
	/// The ceiling the tests assume, so that a change to the protocol is visible here rather than as a
	/// silently different relationship.
	/// </summary>
	[Test]
	public void The_host_ceiling_is_thirty_seconds()
	{
		Assert.That(ProtocolCeiling, Is.EqualTo(TimeSpan.FromSeconds(30)));
	}

	/// <summary>
	/// The turn must not be awaited by the press. This is the property that lets an answer be longer than
	/// the host's ceiling, and it is a source-level assertion because the guarantee is about which method the
	/// executor calls.
	/// </summary>
	[Test]
	public void A_press_does_not_await_the_turn()
	{
		var source = File.ReadAllText(Path.Combine(PluginDirectory(), "Actions", "SessionActions.cs"));

		Assert.Multiple(() =>
		{
			Assert.That(
				source,
				Does.Not.Contain("CancelAfter"),
				"the press still bounds the turn with its own timer, so a long answer is cut off again");

			Assert.That(
				source,
				Does.Contain("ActionBudget.StartTurn"),
				"the press no longer starts the turn through the detached path");
		});
	}

	/// <summary>
	/// Every action that starts a turn goes through the detached path, so none of them can reintroduce a wait.
	/// </summary>
	[Test]
	public void No_action_awaits_a_turn()
	{
		var source = File.ReadAllText(Path.Combine(PluginDirectory(), "Actions", "SessionActions.cs"));
		var callers = System.Text.RegularExpressions.Regex.Matches(source, @"ActionBudget\.(\w+)\(")
			.Select(match => match.Groups[1].Value)
			.Distinct()
			.ToArray();

		Assert.That(
			callers,
			Is.EqualTo(OnlyDetached),
			"an action still reaches the turn through a path that waits for it");
	}

	/// <summary>
	/// The budget on a detached turn is the user's conversation timeout, and it has to be allowed to be
	/// longer than the host ceiling, because the whole point is that the turn outlives the press.
	/// </summary>
	[Test]
	public void The_conversation_timeout_is_the_turns_own_bound()
	{
		var maximum = new JarvisSettings { ConversationTimeoutSeconds = 600 }.ConversationTimeoutSeconds;

		Assert.That(
			TimeSpan.FromSeconds(maximum),
			Is.GreaterThan(ProtocolCeiling),
			"the conversation cannot outlast the host's ceiling, so detaching the turn bought nothing");
	}

	/// <summary>
	/// The default has to be usable for a spoken answer. Twenty seconds is shorter than many replies, and it
	/// was the default while the setting existed beside it showing something else.
	/// </summary>
	[Test]
	public void The_default_conversation_is_long_enough_to_talk_in()
	{
		var seconds = new JarvisSettings().ConversationTimeoutSeconds;

		Assert.That(
			seconds,
			Is.GreaterThanOrEqualTo(60),
			$"a {seconds} second default cuts off answers a person has not finished hearing");
	}

	/// <summary>
	/// The press must not claim success for an answer that does not exist yet.
	/// </summary>
	[Test]
	public async Task A_press_that_starts_a_turn_reports_accepted_rather_than_succeeded()
	{
		var holder = new AssistantStateHolder();
		var store = new JarvisSettingsStore(
			RuntimeTestLog.Logger,
			LocalSettingsFile.Load(Path.Combine(Path.GetTempPath(), "jarvis-budget-nonexistent")));

		await using var session = new AssistantSession(
			holder,
			store,
			() => throw new InvalidOperationException("no model in this test"),
			new Jarvis.Plugin.Speech.VoiceService(
				new Jarvis.Plugin.Speech.WindowsSynthesizer(RuntimeTestLog.Logger),
				new Jarvis.Plugin.Speech.PiperSynthesizer(
					new Jarvis.Plugin.Runtime.RuntimeManager(
						new HttpClient(), RuntimeTestLog.Logger,
						new Jarvis.Plugin.Runtime.RuntimePaths(Path.Combine(Path.GetTempPath(), "jarvis-budget-nonexistent"))),
					RuntimeTestLog.Logger),
				new Jarvis.Plugin.Speech.SpeechPlayer(RuntimeTestLog.Logger),
				holder,
				store,
				RuntimeTestLog.Logger),
			new Jarvis.Plugin.Memory.MemoryStore(store, RuntimeTestLog.Logger),
			RuntimeTestLog.Logger);

		var started = new TaskCompletionSource();

		var result = ActionBudget.StartTurn(
			session,
			session.Logger,
			async token =>
			{
				await started.Task.WaitAsync(token);
				return ActionResult.Success();
			});

		Assert.Multiple(() =>
		{
			Assert.That(
				result.Status,
				Is.EqualTo(ActionResultStatus.Accepted),
				"the press claimed the answer was delivered, which it cannot know");
			Assert.That(started.Task.IsCompleted, Is.False, "the press waited for the turn after all");
		});

		started.SetResult();
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