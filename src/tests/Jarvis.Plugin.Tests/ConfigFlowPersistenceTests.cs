using Jarvis.Plugin.Core;
using NUnit.Framework;
using Serilog;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// What the setup flow hands back at completion, and how it classifies the values.
/// <para>
/// The host persists every submitted form field itself, so this dictionary is not what makes the plain
/// settings stick. It is covered because it is the only channel for a value the plugin computes rather
/// than collects, and because the secret classification has to be stated explicitly: an empty secret
/// returned for an untouched field would wipe the stored key, since the host only retains a secret the
/// form did not send.
/// </para>
/// </summary>
[TestFixture]
public class ConfigFlowPersistenceTests
{
	[Test]
	public void Every_submitted_field_becomes_a_value()
	{
		var values = JarvisConfigFlow.ValuesFor(StepInput(
			(JarvisSettingsStoreFields.LlmProviderField, "nvidia-nim"),
			(JarvisSettingsStoreFields.MicrophoneIdField, "{0.0.1.00000000}.{wo-mic}"),
			(JarvisSettingsStoreFields.SafetyField, "confirm-all")));

		Assert.Multiple(() =>
		{
			Assert.That(Value(values, JarvisSettingsStoreFields.LlmProviderField), Is.EqualTo("nvidia-nim"));
			Assert.That(
				Value(values, JarvisSettingsStoreFields.MicrophoneIdField),
				Is.EqualTo("{0.0.1.00000000}.{wo-mic}"));
			Assert.That(Value(values, JarvisSettingsStoreFields.SafetyField), Is.EqualTo("confirm-all"));
		});
	}

	[Test]
	public void A_number_is_formatted_without_the_machines_culture()
	{
		var values = JarvisConfigFlow.ValuesFor(
			StepInput((JarvisSettingsStoreFields.WakeSensitivityField, 0.06d)));

		// A de-DE machine would otherwise store "0,06", which no reader parses back.
		Assert.That(
			Value(values, JarvisSettingsStoreFields.WakeSensitivityField),
			Is.EqualTo("0.06"));
	}

	[Test]
	public void A_credential_is_flagged_as_a_secret()
	{
		var values = JarvisConfigFlow.ValuesFor(
			StepInput((JarvisSettingsStoreFields.NvidiaKeyEntryField, "sk-test")));

		Assert.That(
			Secret(values, JarvisSettingsStoreFields.NvidiaKeyEntryField),
			Is.True,
			"a credential returned as Plain would be written to the entry in cleartext");
	}

	/// <summary>
	/// The host does not echo a stored secret back into the form, so an untouched credential arrives empty.
	/// Returning that empty value would replace the stored one with nothing.
	/// </summary>
	[Test]
	public void An_untouched_credential_is_left_out_entirely()
	{
		var values = JarvisConfigFlow.ValuesFor(
			StepInput((JarvisSettingsStoreFields.PicovoiceKeyField, string.Empty)));

		Assert.That(
			values.ContainsKey(JarvisSettingsStoreFields.PicovoiceKeyField),
			Is.False,
			"an empty credential was handed back, which would wipe the key the user already stored");
	}

	/// <summary>
	/// The microphone dropdown offers the system default as an empty value, so an empty plain field is a real
	/// answer rather than an absent one, and dropping it would make that choice impossible to unpick.
	/// </summary>
	[Test]
	public void An_empty_plain_value_is_kept_honestly()
	{
		var values = JarvisConfigFlow.ValuesFor(
			StepInput((JarvisSettingsStoreFields.MicrophoneIdField, string.Empty)));

		Assert.That(
			Value(values, JarvisSettingsStoreFields.MicrophoneIdField),
			Is.EqualTo(string.Empty));
	}

	[Test]
	public void An_absent_field_stays_absent()
	{
		var values = JarvisConfigFlow.ValuesFor(StepInput((JarvisSettingsStoreFields.LlmModelField, null)));

		Assert.That(
			values.ContainsKey(JarvisSettingsStoreFields.LlmModelField),
			Is.False,
			"a field the user never reached was invented as an empty value");
	}

	private static string? Value(
		IReadOnlyDictionary<string, MacroDeck.Sdk.ConfigFlow.ConfigFlowValue> values,
		string key) => values.GetValueOrDefault(key)?.Value;

	private static bool? Secret(
		IReadOnlyDictionary<string, MacroDeck.Sdk.ConfigFlow.ConfigFlowValue> values,
		string key) => values.GetValueOrDefault(key)?.IsSecret;

	private static Dictionary<string, object?> StepInput(params (string Key, object? Value)[] entries) =>
		entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
}