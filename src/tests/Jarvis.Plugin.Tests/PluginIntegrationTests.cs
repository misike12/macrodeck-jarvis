using System.Reflection;
using Jarvis.Plugin;
using Jarvis.Plugin.Actions;
using Jarvis.Plugin.Core;
using MacroDeck.Plugin.Testing;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

[TestFixture]
public class PluginIntegrationTests
{
private static PluginTestHarness CreateHarness() =>
		PluginTestHarness.Create(builder => builder
			.RegisterIntegration<PluginIntegration>()
			.AddJarvis());

	[Test]
	public async Task The_plugin_builds_and_initializes()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		Assert.That(harness.Declared, Is.Not.Empty);
	}

[Test]
	public async Task The_action_strings_come_from_the_catalog()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var keys = CatalogKeys();

		foreach (var group in new[] { "Activate", "Cancel", "Toggle", "Say", "CheckModels" })
		{
			Assert.That(keys, Does.Contain($"Actions.{group}.Name"), $"{group} has no localized name.");
			Assert.That(keys, Does.Contain($"Actions.{group}.Description"), $"{group} has no localized description.");
		}
	}

	[Test]
	public async Task Activate_without_a_model_is_refused_rather_than_pretending()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync("jarvis-activate", new Dictionary<string, object?>
		{
			["mode"] = "one-shot",
		});

		Assert.That(outcome.Succeeded, Is.False, "An unconfigured plugin must not report success.");
	}

	[Test]
	public async Task Say_with_blank_text_fails_on_the_required_parameter()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync("jarvis-say", new Dictionary<string, object?>
		{
			["text"] = "   ",
		});

		Assert.That(outcome.Succeeded, Is.False);
	}

	[Test]
	public async Task The_button_states_are_shared_and_always_include_unavailable()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

var outcome = await harness.Actions.GetActionStateAsync(
			"jarvis-toggle",
			new Dictionary<string, object?>());

		Assert.That(outcome.Succeeded, Is.True);

		var snapshot = outcome.DataAs<ActionStateSnapshot>();
		Assert.That(snapshot, Is.Not.Null);
		Assert.That(snapshot!.ActiveStateId, Is.Not.Null);
		Assert.That(snapshot.States.Select(state => state.Id), Does.Contain("unavailable"));
	}

[Test]
	public async Task Toggle_on_an_unconfigured_plugin_refuses_rather_than_pretending()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync("jarvis-toggle", new Dictionary<string, object?>
		{
			["mode"] = "one-shot",
		});

		Assert.That(outcome.Succeeded, Is.False, "Toggling into a session that cannot start must not report success.");
	}

	[Test]
	public async Task Cancelling_nothing_is_a_successful_no_op()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync("jarvis-cancel", new Dictionary<string, object?>
		{
			["kill-running-command"] = false,
		});

		Assert.That(outcome.Succeeded, Is.True, "A legitimate no-op is success.");
	}

[Test]
	public async Task The_variables_reflect_the_state()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		foreach (var localId in new[] { "state", "transcript", "reply", "amplitude" })
		{
			var reading = await harness.Variables.GetAsync(localId);
			Assert.That(reading, Is.Not.Null, $"{localId} should be readable.");
		}
	}

	/// <summary>
	/// A turn nobody is waiting on used to fail in complete silence.
	/// <para>
	/// The hotkey and the wake word both discarded the returned result, so a turn that failed produced no log
	/// line, no orb state and no speech: a user said the wake word, the detector logged that it fired, and
	/// then nothing happened at all. With an empty log that looks exactly like the plugin not running.
	/// </para>
	/// <para>
	/// Asserting on the resolved words also proves the shared graph registers localization: an unresolved key
	/// would read <c>plugin:com.misike12.jarvis:Errors.ComponentMissing</c>, which is what the orb would then
	/// have shown the user.
	/// </para>
	/// </summary>
	[Test]
	public async Task An_unattended_turn_that_fails_writes_the_reason_down()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		StartUnattendedTurn(harness);

		await harness.Logs.WaitForAsync(entry =>
			entry.Level == "warning" && entry.Message.Contains("did not complete"));
	}

	/// <summary>
	/// The reason has to name the component, not just say something went wrong: the usual cause is a model
	/// that was never downloaded, and a user who knows which one can fix it.
	/// </summary>
	[Test]
	public async Task The_reason_names_the_component_rather_than_the_resource_key()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		StartUnattendedTurn(harness);

		await harness.Logs.WaitForAsync(entry =>
			entry.Level == "warning" && entry.Message.Contains("whisper", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// The orb is the only surface guaranteed to be on screen, since it needs neither speech recognition nor
	/// speech synthesis. A failure that is only in the log still looks like nothing happened to whoever said
	/// the wake word and was not looking at the log viewer.
	/// </summary>
	[Test]
	public async Task An_unattended_turn_that_fails_reaches_the_orb()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		StartUnattendedTurn(harness);

		await WaitForAsync(() => OrbStateIs(harness, "error"));
	}

	/// <summary>
	/// Called by the wake word, the hotkey and the orb's own button. Private because none of those callers
	/// waits for the turn, but reachable here to prove that one of them does report.
	/// </summary>
	private static void StartUnattendedTurn(PluginTestHarness harness)
	{
		var integration = harness.Services.GetRequiredService<PluginIntegration>();
		var start = typeof(PluginIntegration).GetMethod(
			"StartListeningTurn",
			BindingFlags.Instance | BindingFlags.NonPublic);

		Assert.That(start, Is.Not.Null, "PluginIntegration no longer has the unattended turn entry point.");

		start.Invoke(integration, null);
	}

	/// <summary>The text a text variable currently holds, read past the value envelope.</summary>
	private static async Task<bool> OrbStateIs(PluginTestHarness harness, string expected)
	{
		var outcome = await harness.Variables.GetAsync("state");

		if (outcome?.Data is not { } data)
		{
			return false;
		}

		return data.TryGetProperty("value", out var value)
			&& value.TryGetProperty("text", out var text)
			&& text.GetString() == expected;
	}

	private static async Task WaitForAsync(Func<Task<bool>> condition, int attempts = 100)
	{
		for (var attempt = 0; attempt < attempts; attempt++)
		{
			if (await condition())
			{
				return;
			}

			await Task.Delay(50);
		}

		Assert.Fail("The expected state was never reached.");
	}

/// <summary>
	/// No action declares a platform.
	/// <para>
	/// All seven used to set <c>MacroDeckPlatform.Windows</c>, which the analyzer flagged as inert: gating is
	/// the manifest's <c>entrypoints</c> job, and a per-action platform narrowed nothing the host was not
	/// already refusing to load. The property is left at its default so there is one place that decides which
	/// platforms this plugin runs on.
	/// </para>
	/// </summary>
	[Test]
	public void No_action_declares_a_platform_and_leaves_gating_to_the_manifest()
	{
		foreach (var action in Actions())
		{
			Assert.That(action.Platforms, Is.EqualTo(MacroDeckPlatform.All), action.Id);
		}
	}

	[Test]
	public void Action_ids_are_unique_across_the_plugin()
	{
		var ids = Actions().Select(action => action.Id).ToArray();
		Assert.That(ids, Is.Unique);
	}

private static IEnumerable<IActionDefinition> Actions() =>
	[
		new ActivateAction(null!, null!),
		new CancelAction(null!),
		new ToggleAction(null!, null!),
	];

	private static string[] CatalogKeys() =>
		Strings.LocalizationCatalog.KeysOf(Strings.LocalizationCatalog.DefaultCulture).ToArray();
}