using Jarvis.Plugin.Core;
using MacroDeck.Sdk;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Decks;
using MacroDeck.Sdk.Events;
using MacroDeck.Sdk.Messaging;
using MacroDeck.Sdk.Notifications;
using MacroDeck.Sdk.Scripts;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Variables;
using MacroDeck.Sdk.Widgets;
using NUnit.Framework;
using Serilog;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// A re-opened setup form has to show what is actually configured.
/// <para>
/// The host prefills the first step of a re-opened flow from the stored entry and then discards that
/// prefill, rebuilding every later step from the declared defaults. A plugin therefore cannot rely on the
/// host to show the user their own settings, and a form that contradicts the running configuration reads
/// as "nothing saved" even when every value is stored and in use. Seeding each field's declared default
/// from the store is the only side of this a plugin controls.
/// </para>
/// </summary>
[TestFixture]
[FastHostReads]
public class ConfigFlowPrefillTests
{
	[Test]
	public async Task A_re_opened_form_shows_the_stored_microphone()
	{
		var store = await ConfiguredStoreAsync("{0.0.1.00000000}.{wo-mic}");

		var voice = await VoiceStepAsync(store);

		var microphone = voice.Fields.Concat(voice.AdvancedFields)
			.Single(field => field.Name == JarvisSettingsStoreFields.MicrophoneIdField);

		Assert.That(
			microphone.DefaultValue,
			Is.EqualTo("{0.0.1.00000000}.{wo-mic}"),
			"the dropdown would offer the system default for a microphone that is configured and in use");
	}

	[Test]
	public async Task A_first_run_form_still_offers_the_declared_default()
	{
		var voice = await VoiceStepAsync(NewStore());

		var microphone = voice.Fields.Concat(voice.AdvancedFields)
			.Single(field => field.Name == JarvisSettingsStoreFields.MicrophoneIdField);

		Assert.That(
			microphone.DefaultValue,
			Is.Not.Null,
			"a field with no stored value and no declared default offers nothing to select");
	}

	[Test]
	public async Task Every_stored_setting_is_shown_rather_than_its_declared_default()
	{
		var store = await ConfiguredStoreAsync(
			"{0.0.1.00000000}.{wo-mic}",
			(JarvisSettingsStoreFields.WakeWordField, "computer"));

		var voice = await VoiceStepAsync(store);

		var wakeWord = voice.Fields
			.Single(field => field.Name == JarvisSettingsStoreFields.WakeWordField);

		Assert.That(
			wakeWord.DefaultValue,
			Is.EqualTo("computer"),
			"the wake word field fell back to the built-in default instead of the configured one");
	}

	/// <summary>A store that has read a configuration carrying the given values.</summary>
	private static async Task<JarvisSettingsStore> ConfiguredStoreAsync(
		string microphoneId,
		params (string Field, string Value)[] alsoStored)
	{
		var store = NewStore();
		var entry = new ConfigEntrySnapshot(Guid.NewGuid(), "JARVIS");
		var values = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[JarvisSettingsStoreFields.MicrophoneIdField] = microphoneId,
		};

		foreach (var (field, value) in alsoStored)
		{
			values[field] = value;
		}

		// Reload against a host that answers every field with an empty string except the microphone, so
		// the store's read dictionary holds exactly the one value under test.
		await store.ReloadAsync(new StubContext(new StubConfig(entry, values)), CancellationToken.None);

		return store;
	}

	private sealed class StubConfig(ConfigEntrySnapshot entry, Dictionary<string, string> values) : IIntegrationConfig
	{
		public Task<IReadOnlyList<ConfigEntrySnapshot>> GetEntriesAsync(CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<ConfigEntrySnapshot>>([entry]);

		public Task<string?> GetStringAsync(Guid entryId, string key, CancellationToken cancellationToken) =>
			Task.FromResult(values.GetValueOrDefault(key));

		public Task<string?> GetSecretAsync(Guid entryId, string key, CancellationToken cancellationToken) =>
			Task.FromResult<string?>(null);

		public Task SetSecretAsync(Guid entryId, string key, string? value, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task SetStringAsync(Guid entryId, string key, string? value, CancellationToken cancellationToken) =>
			throw new NotSupportedException();
	}

	private sealed class StubContext(IIntegrationConfig config) : IIntegrationContext
	{
		public IIntegrationConfig Config => config;

		public IVariableApi Variables => throw new NotSupportedException();

		public IUserVariableApi UserVariables => throw new NotSupportedException();

		public IDeckNavigator Deck => throw new NotSupportedException();

		public IScriptApi Scripts => throw new NotSupportedException();

		public IWidgetApi Widgets => throw new NotSupportedException();

		public IEventPublisher Events => throw new NotSupportedException();

		public IUserNotifier Notifications => throw new NotSupportedException();

		public IMessageChannel Messages => throw new NotSupportedException();

		public IUiResourceRegistry UiResources => throw new NotSupportedException();
	}

	private static JarvisSettingsStore NewStore() =>
		new(new LoggerConfiguration().CreateLogger(), LocalSettingsFile.Empty);

	private static async Task<ConfigFlowStep> VoiceStepAsync(JarvisSettingsStore store) =>
		await StepAsync(store, JarvisFields.VoiceStep);

	private static async Task<ConfigFlowStep> StepAsync(JarvisSettingsStore store, string stepId)
	{
		var flow = new JarvisConfigFlow(new LoggerConfiguration().CreateLogger(), store);

		// Walk the flow to the step under test, the way the host does.
		var result = await flow.StartAsync(null!, CancellationToken.None);

		for (var guard = 0; guard < 8 && result.Kind != ConfigFlowResultKind.Complete; guard++)
		{
			var stepIdNow = result.NextStep?.StepId ?? string.Empty;

			if (stepIdNow == stepId)
			{
				return result.NextStep!;
			}

			result = await flow.SubmitAsync(stepIdNow, EmptyInput, null!, CancellationToken.None);
		}

		throw new InvalidOperationException($"The flow never offered the {stepId} step.");
	}

	private static Dictionary<string, object?> EmptyInput { get; } = new(StringComparer.Ordinal);
}