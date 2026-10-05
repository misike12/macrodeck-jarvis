using System.Globalization;
using Jarvis.Plugin.Core;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using Serilog;

namespace Jarvis.Plugin;

/// <summary>
/// The setup flow.
/// <para>
/// Credentials are declared as secret fields, so the form layer encrypts them and the host stores them in
/// its encrypted secret store rather than beside an ordinary setting.
/// </para>
/// </summary>
internal sealed class JarvisConfigFlow(ILogger logger, JarvisSettingsStore settings) : IConfigFlow
{
	private const string ProviderStepId = "provider";
	private const string KeysStepId = "keys";
	private const string ModelsStepId = "models";
	private const string VoiceStepId = "voice";
	private const string BehaviourStepId = "behaviour";

	private static readonly HashSet<string> SecretFields =
		JarvisFields.All
			.Where(field => field.Kind is JarvisFieldKind.Secret)
			.Select(field => field.Name)
			.ToHashSet(StringComparer.Ordinal);

	public Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken ct)
	{
		return Task.FromResult(ConfigFlowResult.Step(ProviderStep()));
	}

	public Task<ConfigFlowResult> SubmitAsync(
		string stepId,
		IReadOnlyDictionary<string, object?> input,
		IConfigFlowContext context,
		CancellationToken ct)
	{
		var result = stepId switch
		{
			ProviderStepId => ProviderSubmitted(input),
			KeysStepId => KeysSubmitted(input),
			ModelsStepId => ConfigFlowResult.Step(VoiceStep()),
			VoiceStepId => VoiceSubmitted(input),
			BehaviourStepId => Complete(input),
			_ => ConfigFlowResult.Error(ProviderStep(), Strings.ConfigFlow.UnknownStep()),
		};

		return Task.FromResult(result);
	}

	/// <summary>
	/// Turns the final step's input into the values handed back at completion.
	/// <para>
	/// The host already persists every form field a step submits, so this is not what makes the settings
	/// stick; it exists so a value this plugin computes rather than collects has a way to reach the entry,
	/// and so the secret classification is stated explicitly instead of inferred from a declared field type.
	/// Deriving it from <c>input</c> rather than accumulating across steps is deliberate: input is
	/// cumulative and the host rolls it back when the user goes Back, so a value the user retracted by
	/// stepping back cannot be resurrected here.
	/// </para>
	/// <para>
	/// An empty secret means "unchanged": the host does not echo a stored secret back into the form, so
	/// persisting an empty one would wipe the key. Anything else is kept honestly, including an empty
	/// string, which is how a choice for the system default arrives.
	/// </para>
	/// </summary>
	internal static IReadOnlyDictionary<string, ConfigFlowValue> ValuesFor(
		IReadOnlyDictionary<string, object?> input)
	{
		var values = new Dictionary<string, ConfigFlowValue>(StringComparer.Ordinal);

		foreach (var (key, value) in input)
		{
			if (value is null)
			{
				continue;
			}

			var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

			if (text.Length == 0 && SecretFields.Contains(key))
			{
				continue;
			}

			values[key] = SecretFields.Contains(key)
				? ConfigFlowValue.Secret(text)
				: ConfigFlowValue.Plain(text);
		}

		return values;
	}

	private ConfigFlowResult ProviderSubmitted(IReadOnlyDictionary<string, object?> input)
	{
		var provider = Read(input, JarvisSettingsStoreFields.LlmProviderField);

		if (string.Equals(provider, "self-hosted-nim", StringComparison.Ordinal)
			&& string.IsNullOrWhiteSpace(Read(input, JarvisSettingsStoreFields.SelfHostedUrlField)))
		{
			return ConfigFlowResult.Error(
				ProviderStep(),
				Strings.ConfigFlow.SelfHostedUrlRequired(),
				new Dictionary<string, LocalizedText>
				{
					[JarvisSettingsStoreFields.SelfHostedUrlField] = Strings.ConfigFlow.SelfHostedUrlRequired(),
				});
		}

		return ConfigFlowResult.Step(KeysStep());
	}

	private ConfigFlowResult KeysSubmitted(IReadOnlyDictionary<string, object?> input)
	{
		return ConfigFlowResult.Step(ModelsStep());
	}

	private ConfigFlowResult VoiceSubmitted(IReadOnlyDictionary<string, object?> input)
	{
		// Reported rather than validated: the host owns persistence, so this is the only place that can
		// show what the host actually sent for a choice. An endpoint id that arrives mangled or empty
		// here explains a dropdown that never sticks.
		logger.Information(
			"Voice step submitted with microphone id {Microphone}.",
			Read(input, JarvisSettingsStoreFields.MicrophoneIdField));

		return ConfigFlowResult.Step(BehaviourStep());
	}

	/// <summary>
	/// Ends the flow, handing back the final step's input.
	/// <para>
	/// The values are not what makes the settings stick, and this comment previously said the opposite: the
	/// host accumulates and persists every form field itself (ConfigFlowManager merges each submit into the
	/// entry's value map), so a multi-step completion with no dictionary persisted everything all the same.
	/// Reading the host's own database is what settled it: a completed setup had written every field,
	/// including the ones a dictionary would have added. What genuinely did not survive a re-opened form
	/// was nothing at all, because the host's setup UI drops its stored prefill after the first step.
	/// </para>
	/// </summary>
	private ConfigFlowResult Complete(IReadOnlyDictionary<string, object?> input)
	{
		var values = ValuesFor(input);

		logger.Information("Flow completed, handing back {Count} values.", values.Count);

		return ConfigFlowResult.Complete("JARVIS", values);
	}

	private ConfigFlowStep ProviderStep() => Step(
		JarvisFields.ProviderStep,
		Strings.ConfigFlow.Provider.Title(),
		Strings.ConfigFlow.Provider.Description());

	private ConfigFlowStep KeysStep() => Step(
		JarvisFields.KeysStep,
		Strings.ConfigFlow.Keys.Title(),
		Strings.ConfigFlow.Keys.Description());

	private ConfigFlowStep ModelsStep() => Step(
		JarvisFields.ModelsStep,
		Strings.ConfigFlow.Models.Title(),
		Strings.ConfigFlow.Models.Description());

	private ConfigFlowStep VoiceStep() => Step(
		JarvisFields.VoiceStep,
		Strings.ConfigFlow.Voice.Title(),
		Strings.ConfigFlow.Voice.Description());

	private ConfigFlowStep BehaviourStep() => Step(
		JarvisFields.BehaviourStep,
		Strings.ConfigFlow.Behaviour.Title(),
		Strings.ConfigFlow.Behaviour.Description());

	/// <summary>
	/// Builds a step from the declared table rather than from a hand-written list of fields.
	/// <para>
	/// This is the whole of the fix. The steps used to be written out field by field, the store kept its own
	/// list to read back, and a test asserted nothing connected them, so thirteen settings ended up readable
	/// but invisible: the code that consumed them was reading a default, permanently, and nothing said so.
	/// </para>
	/// </summary>
	private ConfigFlowStep Step(string stepId, LocalizedText title, LocalizedText description)
	{
		var (fields, advanced) = JarvisFields.ForStep(stepId);

		return new ConfigFlowStep
		{
			StepId = stepId,
			Title = title,
			Description = description,
			Fields = [.. fields.Select(field => ToParameter(field, DefaultFor(field)))],
			AdvancedFields = [.. advanced.Select(field => ToParameter(field, DefaultFor(field)))],
		};
	}

/// <summary>
	/// What the field should show before the user touches it: the value the host has stored, and the
	/// declared default only when it has none.
	/// <para>
	/// The host's setup UI prefills a re-opened form from the stored entry, then throws that prefill away on
	/// every step after the first and rebuilds the form from the declared defaults. So the declared default
	/// is the only thing standing between the user and a form that claims their microphone, model and
	/// persona were never chosen, when all of them are stored and in use. Seeding it from the store makes
	/// the form tell the truth either way.
	/// </para>
	/// </summary>
	private string DefaultFor(JarvisField field)
	{
		var stored = settings.StoredValue(field.Name);

		if (!string.IsNullOrEmpty(stored))
		{
			return stored;
		}

		return field.Kind is JarvisFieldKind.Flag ? field.Default ?? "true" : field.Default ?? string.Empty;
	}

	private static ActionParameter ToParameter(JarvisField field, string defaultValue)
	{
		var parameter = field.Kind switch
		{
			JarvisFieldKind.Secret => ActionParameter.Secret(
				field.Name,
				field.Label(),
				field.Description?.Invoke() ?? field.Label()),

			JarvisFieldKind.Multiline => ActionParameter.MultilineText(field.Name, field.Label()),

			JarvisFieldKind.Choice => ActionParameter.Choice(
				field.Name,
				field.OptionsSource?.Invoke() ?? field.Options ?? [],
				field.Label(),
				defaultValue: defaultValue,
				required: field.Required),

			// No bounds here: the parameter type carries no minimum or maximum, and the store already
			// clamps a number into range when it reads it back. Declaring a bound that nothing enforced
			// would be the same kind of decoration the rest of this table is replacing.
			JarvisFieldKind.Number => ActionParameter.Number(
				field.Name,
				field.Label(),
				field.Description?.Invoke() ?? field.Label(),
				required: field.Required),

			// A flag is stored as "true" or "false" and read back through the same parser as every other
			// string field, so it is declared as a choice rather than inventing a second representation.
			JarvisFieldKind.Flag => ActionParameter.Choice(
				field.Name,
				YesNo(),
				field.Label(),
				defaultValue: defaultValue,
				required: field.Required),

			_ => ActionParameter.Text(
				field.Name,
				field.Label(),
				placeholder: field.Default,
				defaultValue: defaultValue,
				required: field.Required),
		};

		if (field.OnlyWhenField is { } condition && field.OnlyWhenValue is { } value)
		{
			parameter = parameter.OnlyWhen(condition, value);
		}

		return parameter;
	}

	private static IReadOnlyList<ActionParameterOption> YesNo() =>
	[
		Option("true", Strings.ConfigFlow.Option.Yes()),
		Option("false", Strings.ConfigFlow.Option.No()),
	];

	private static ActionParameterOption Option(string value, LocalizedText label) =>
		new() { Value = value, Label = label };

	private static string Read(IReadOnlyDictionary<string, object?> input, string name) =>
		input.GetValueOrDefault(name)?.ToString() ?? string.Empty;
}
