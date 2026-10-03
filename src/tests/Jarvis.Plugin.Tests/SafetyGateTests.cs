using System.Text.Json.Nodes;
using Jarvis.Plugin.Actions;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Llm;
using NUnit.Framework;
using MacroDeck.Sdk.Actions;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The confirmation gate.
/// <para>
/// This is the most important thing in the plugin and it was silently broken: under the default safety mode
/// every tool that acts asked for confirmation, nothing ever answered, and so run_shell, write_file,
/// kill_process, screenshot and set_persona could never run at all. The assistant could read things and
/// nothing else, while appearing to work. These tests exist so that cannot come back unnoticed.
/// </para>
/// </summary>
[TestFixture]
public class SafetyGateTests
{
	private static JarvisSettingsStore StoreWith(JarvisSettings settings)
	{
		var store = new JarvisSettingsStore(RuntimeTestLog.Logger, LocalSettingsFile.Load(
			Path.Combine(Path.GetTempPath(), "jarvis-safety-nonexistent")));

		store.Apply(settings);
		return store;
	}

	private sealed class RecordingTool(string name, bool confirms) : ITool
	{
		public int Invocations { get; private set; }

		public string Name => name;

		public bool RequiresConfirmation => confirms;

		public ToolDefinition Definition => new() { Name = name, Description = name, Parameters = new JsonObject() };

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			Invocations++;
			return Task.FromResult(ToolOutcome.Success($"{name} ran"));
		}
	}

	/// <summary>Builds a registry over a real session, so the gate is exercised through the real code path.</summary>
	private static (AssistantSession Session, ToolRegistry Registry) Build(
		JarvisSettings settings,
		out RecordingTool tool)
	{
		var store = StoreWith(settings);
		var holder = new AssistantStateHolder();
		var session = new AssistantSession(
			holder,
			store,
			() => throw new InvalidOperationException("no model in this test"),
			new Jarvis.Plugin.Speech.VoiceService(
				new Jarvis.Plugin.Speech.WindowsSynthesizer(RuntimeTestLog.Logger),
				new Jarvis.Plugin.Speech.PiperSynthesizer(
					new Jarvis.Plugin.Runtime.RuntimeManager(
						new HttpClient(), RuntimeTestLog.Logger,
						new Jarvis.Plugin.Runtime.RuntimePaths(Path.Combine(Path.GetTempPath(), "jarvis-safety-nonexistent"))),
					RuntimeTestLog.Logger),
				new Jarvis.Plugin.Speech.SpeechPlayer(RuntimeTestLog.Logger),
				holder,
				store,
				RuntimeTestLog.Logger),
			new Jarvis.Plugin.Memory.MemoryStore(store, RuntimeTestLog.Logger),
			RuntimeTestLog.Logger);

		tool = new RecordingTool("test_tool", confirms: true);

		var registry = new ToolRegistry(store, session, RuntimeTestLog.Logger);
		registry.Register(tool);

		return (session, registry);
	}

	private static ToolCall Call(string name, string arguments = "{}") => new()
	{
		Id = "call_1",
		Name = name,
		Arguments = arguments,
	};

	/// <summary>
	/// The regression. Confirm-all must run the tool once the user says yes, and refuse it once they say no.
	/// </summary>
	[Test]
	public async Task Confirm_all_runs_the_tool_once_the_user_approves()
	{
		var (session, registry) = Build(new JarvisSettings(), out var tool);

		var pending = registry.InvokeAsync(Call(tool.Name), CancellationToken.None);

		// The turn is now waiting. Answer it the way the button does.
		await WaitForConfirmationAsync(session);
		session.ResolveConfirmation(approved: true);

		var outcome = await pending;

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(tool.Invocations, Is.EqualTo(1));
		});
	}

	[Test]
	public async Task Confirm_all_refuses_the_tool_when_the_user_declines()
	{
		var (session, registry) = Build(new JarvisSettings(), out var tool);

		var pending = registry.InvokeAsync(Call(tool.Name), CancellationToken.None);

		await WaitForConfirmationAsync(session);
		session.ResolveConfirmation(approved: false);

		var outcome = await pending;

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(tool.Invocations, Is.Zero, "a refused tool still ran");
			Assert.That(outcome.Content, Does.Contain("not approved").IgnoreCase);
		});
	}

	/// <summary>A read must never block on a human, or the assistant stops being useful.</summary>
	[Test]
	public async Task A_read_needs_no_confirmation_at_all()
	{
		var (session, registry) = Build(new JarvisSettings(), out _);
		var reader = new RecordingTool("read_thing", confirms: false);
		registry.Register(reader);

		var outcome = await registry.InvokeAsync(Call("read_thing"), CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True);
			Assert.That(session.PendingConfirmation, Is.Null);
			Assert.That(reader.Invocations, Is.EqualTo(1));
		});
	}

	[Test]
	public async Task Autonomous_runs_without_asking()
	{
		var (_, registry) = Build(new JarvisSettings { Safety = SafetyMode.Autonomous }, out var tool);

		var outcome = await registry.InvokeAsync(Call(tool.Name), CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True);
			Assert.That(tool.Invocations, Is.EqualTo(1));
		});
	}

	/// <summary>
	/// Tool-permissions must be a real mode. It used to fall through to the same branch as confirm-all,
	/// which made the two indistinguishable and the whole setting decorative.
	/// </summary>
	[Test]
	public async Task Tool_permissions_runs_a_permitted_class_without_asking()
	{
		var settings = new JarvisSettings
		{
			Safety = SafetyMode.ToolPermissions,
			PermitRead = true,
			PermitExecute = true,
		};

		var (_, registry) = Build(settings, out _);
		var reader = new RecordingTool("read_thing", confirms: false);
		var executor = new RecordingTool("run_thing", confirms: true);
		registry.Register(reader);
		registry.Register(executor);

		await registry.InvokeAsync(Call("read_thing"), CancellationToken.None);
		var outcome = await registry.InvokeAsync(Call("run_thing"), CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, "a permitted execute tool still asked");
			Assert.That(executor.Invocations, Is.EqualTo(1));
		});
	}

	[Test]
	public async Task Tool_permissions_asks_for_a_class_that_was_not_granted()
	{
		var settings = new JarvisSettings { Safety = SafetyMode.ToolPermissions, PermitRead = true };

		var (session, registry) = Build(settings, out var tool);

		var pending = registry.InvokeAsync(Call(tool.Name), CancellationToken.None);
		await WaitForConfirmationAsync(session);
		session.ResolveConfirmation(approved: false);

		var outcome = await pending;

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(tool.Invocations, Is.Zero);
		});
	}

	/// <summary>
	/// Ending the turn must release whoever is waiting, or a cancel leaves a tool call wedged forever.
	/// </summary>
	[Test]
	public async Task Ending_a_turn_releases_a_waiting_confirmation()
	{
		var (session, registry) = Build(new JarvisSettings(), out var tool);

		session.BeginTurn();
		var pending = registry.InvokeAsync(Call(tool.Name), CancellationToken.None);

		await WaitForConfirmationAsync(session);
		session.EndTurn();

		var outcome = await pending;

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(tool.Invocations, Is.Zero);
		});
	}

	/// <summary>
	/// The button is the only way a user answers, so it is tested as the way a user answers: approving a
	/// pending request runs the tool.
	/// </summary>
	[Test]
	public async Task The_confirm_action_is_what_actually_answers()
	{
		var (session, registry) = Build(new JarvisSettings(), out var tool);

		var pending = registry.InvokeAsync(Call(tool.Name), CancellationToken.None);
		await WaitForConfirmationAsync(session);

		var action = new ConfirmAction(session).CreateExecutor();
		var result = await action.ExecuteAsync(new ActionExecutionContext
		{
			Parameters = new Dictionary<string, object> { ["approve"] = true },
			CancellationToken = CancellationToken.None,
		});

		var outcome = await pending;

		Assert.Multiple(() =>
		{
			Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Succeeded));
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(tool.Invocations, Is.EqualTo(1));
		});
	}

	/// <summary>Pressing confirm with nothing waiting is a no-op, not an error.</summary>
	[Test]
	public async Task Confirming_with_nothing_pending_is_a_successful_no_op()
	{
		var (session, _) = Build(new JarvisSettings(), out _);

		var action = new ConfirmAction(session).CreateExecutor();
		var result = await action.ExecuteAsync(new ActionExecutionContext
		{
			Parameters = new Dictionary<string, object> { ["approve"] = true },
			CancellationToken = CancellationToken.None,
		});

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Succeeded));
	}

	/// <summary>An unknown tool is a refusal, not an exception the model has to interpret as a crash.</summary>
	[Test]
	public async Task An_unknown_tool_is_refused_rather_than_throwing()
	{
		var (_, registry) = Build(new JarvisSettings(), out _);

		var outcome = await registry.InvokeAsync(Call("no_such_tool"), CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("no tool called"));
		});
	}

	/// <summary>Malformed arguments must be caught before a tool sees them.</summary>
	[Test]
	public async Task Malformed_arguments_are_refused_rather_than_thrown()
	{
		var (_, registry) = Build(new JarvisSettings(), out var tool);

		var outcome = await registry.InvokeAsync(Call(tool.Name, "not json at all"), CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(tool.Invocations, Is.Zero);
		});
	}

	private static async Task WaitForConfirmationAsync(AssistantSession session)
	{
		for (var attempt = 0; attempt < 100 && session.PendingConfirmation is null; attempt++)
		{
			await Task.Delay(20);
		}

		Assert.That(session.PendingConfirmation, Is.Not.Null, "no confirmation was ever requested");
	}
}