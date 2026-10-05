using Jarvis.Plugin.Core;
using NUnit.Framework;
using Serilog;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The setup flow has to hand back everything it collected, or nothing is stored.
/// <para>
/// The completion used to return no values on the theory that the host persists each step as the flow
/// runs, which holds for single-step flows only. A multi-step completion with no values persisted
/// nothing: every plain setting reverted to its default on every setup while the secrets survived through
/// the secret store, which is exactly the shape of "nothing saves" that took a day to chase down.
/// </para>
/// </summary>
[TestFixture]
public class ConfigFlowPersistenceTests
{
	[Test]
	public async Task Completing_the_flow_returns_every_step_values()
	{
		var flow = NewFlow();

		await flow.SubmitAsync(JarvisFields.ProviderStep, StepInput(
			(JarvisSettingsStoreFields.LlmProviderField, "nvidia-nim")), Context(), CancellationToken.None);
		await flow.SubmitAsync(JarvisFields.KeysStep, StepInput(
			(JarvisSettingsStoreFields.NvidiaKeyEntryField, "sk-test")), Context(), CancellationToken.None);
		await flow.SubmitAsync(JarvisFields.ModelsStep, StepInput(
			(JarvisSettingsStoreFields.SttProviderField, "whisper-cpp")), Context(), CancellationToken.None);
		await flow.SubmitAsync(JarvisFields.VoiceStep, StepInput(
			(JarvisSettingsStoreFields.MicrophoneIdField, "{0.0.1.00000000}.{wo-mic}"),
			(JarvisSettingsStoreFields.WakeSensitivityField, 0.06)), Context(), CancellationToken.None);
		var result = await flow.SubmitAsync(JarvisFields.BehaviourStep, StepInput(
			(JarvisSettingsStoreFields.SafetyField, "confirm-all")), Context(), CancellationToken.None);

		Assert.That(result.EntryTitle, Is.EqualTo("JARVIS"));
		Assert.Multiple(() =>
		{
			// One key per step: only the last step's would survive if the completion forgot the rest.
			Assert.That(Value(result, JarvisSettingsStoreFields.LlmProviderField), Is.EqualTo("nvidia-nim"));
			Assert.That(Value(result, JarvisSettingsStoreFields.SttProviderField), Is.EqualTo("whisper-cpp"));
			Assert.That(Value(result, JarvisSettingsStoreFields.MicrophoneIdField), Is.EqualTo("{0.0.1.00000000}.{wo-mic}"));
			Assert.That(Value(result, JarvisSettingsStoreFields.SafetyField), Is.EqualTo("confirm-all"));

			// A number must not be formatted in the machine's culture.
			Assert.That(Value(result, JarvisSettingsStoreFields.WakeSensitivityField), Is.EqualTo("0.06"));
		});
	}

	[Test]
	public async Task Secrets_are_flagged_and_an_untouched_one_is_left_alone()
	{
		var flow = NewFlow();

		await flow.SubmitAsync(JarvisFields.KeysStep, StepInput(
			(JarvisSettingsStoreFields.NvidiaKeyEntryField, "sk-test"),
			(JarvisSettingsStoreFields.PicovoiceKeyField, string.Empty)), Context(), CancellationToken.None);
		var result = await flow.SubmitAsync(JarvisFields.BehaviourStep, StepInput(), Context(), CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(Secret(result, JarvisSettingsStoreFields.NvidiaKeyEntryField), Is.True);

			// The host does not echo a stored secret back into the form. Persisting the empty string
			// would wipe the key on every setup that did not retype it.
			Assert.That(
				result.Values?.ContainsKey(JarvisSettingsStoreFields.PicovoiceKeyField) ?? true,
				Is.False);
		});
	}

	[Test]
	public async Task An_empty_plain_value_is_kept_honestly()
	{
		var flow = NewFlow();

		await flow.SubmitAsync(JarvisFields.VoiceStep, StepInput(
			(JarvisSettingsStoreFields.MicrophoneIdField, string.Empty)), Context(), CancellationToken.None);
		var result = await flow.SubmitAsync(JarvisFields.BehaviourStep, StepInput(), Context(), CancellationToken.None);

		// Choosing the system default arrives as an empty string, and dropping it would make the choice
		// impossible to unpick.
		Assert.That(Value(result, JarvisSettingsStoreFields.MicrophoneIdField), Is.EqualTo(string.Empty));
	}

	private static string? Value(
		MacroDeck.Sdk.ConfigFlow.ConfigFlowResult result,
		string key) => result.Values?.GetValueOrDefault(key)?.Value;

	private static bool? Secret(
		MacroDeck.Sdk.ConfigFlow.ConfigFlowResult result,
		string key) => result.Values?.GetValueOrDefault(key)?.IsSecret;

	private static JarvisConfigFlow NewFlow() =>
		new(new LoggerConfiguration().CreateLogger());

	private static MacroDeck.Sdk.ConfigFlow.IConfigFlowContext Context() => null!;

	private static Dictionary<string, object?> StepInput(params (string Key, object? Value)[] entries) =>
		entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
}
