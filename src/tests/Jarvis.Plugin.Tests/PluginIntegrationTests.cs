using Jarvis.Plugin;
using Jarvis.Plugin.Actions;
using Jarvis.Plugin.Core;
using MacroDeck.Plugin.Testing;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
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