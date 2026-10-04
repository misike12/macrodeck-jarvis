using Jarvis.Plugin.Core;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;

namespace Jarvis.Plugin;

/// <summary>
/// The setup flow.
/// <para>
/// Credentials are declared as secret fields, so the form layer encrypts them and the host stores them in
/// its encrypted secret store rather than beside an ordinary setting.
/// </para>
/// </summary>
internal sealed class JarvisConfigFlow : IConfigFlow
{
	private const string ProviderStepId = "provider";
	private const string KeysStepId = "keys";
	private const string ModelsStepId = "models";
	private const string VoiceStepId = "voice";
	private const string BehaviourStepId = "behaviour";

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
			VoiceStepId => ConfigFlowResult.Step(BehaviourStep()),
			BehaviourStepId => Complete(),
			_ => ConfigFlowResult.Error(ProviderStep(), Strings.ConfigFlow.UnknownStep()),
		};

		return Task.FromResult(result);
	}

	private static ConfigFlowResult ProviderSubmitted(IReadOnlyDictionary<string, object?> input)
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

	private static ConfigFlowResult KeysSubmitted(IReadOnlyDictionary<string, object?> input)
	{
		return ConfigFlowResult.Step(ModelsStep());
	}

	/// <summary>
	/// Ends the flow.
	/// <para>
	/// No values dictionary is returned, and that is deliberate. The host persists every form field the
	/// user filled in as the flow runs, across all steps, so handing back a dictionary built from the last
	/// step's <c>input</c> would write that step's fields over everything the earlier steps collected.
	/// The credentials are the sharp edge: they are collected two steps earlier, so a dictionary here wrote
	/// an empty secret over a stored key every time setup was completed.
	/// </para>
	/// </summary>
	private static ConfigFlowResult Complete()
	{
		return ConfigFlowResult.Complete("JARVIS");
	}

	private static ConfigFlowStep ProviderStep() => Step(
		JarvisFields.ProviderStep,
		Strings.ConfigFlow.Provider.Title(),
		Strings.ConfigFlow.Provider.Description());

	private static ConfigFlowStep KeysStep() => Step(
		JarvisFields.KeysStep,
		Strings.ConfigFlow.Keys.Title(),
		Strings.ConfigFlow.Keys.Description());

	private static ConfigFlowStep ModelsStep() => Step(
		JarvisFields.ModelsStep,
		Strings.ConfigFlow.Models.Title(),
		Strings.ConfigFlow.Models.Description());

	private static ConfigFlowStep VoiceStep() => Step(
		JarvisFields.VoiceStep,
		Strings.ConfigFlow.Voice.Title(),
		Strings.ConfigFlow.Voice.Description());

	private static ConfigFlowStep BehaviourStep() => Step(
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
	private static ConfigFlowStep Step(string stepId, LocalizedText title, LocalizedText description)
	{
		var (fields, advanced) = JarvisFields.ForStep(stepId);

		return new ConfigFlowStep
		{
			StepId = stepId,
			Title = title,
			Description = description,
			Fields = [.. fields.Select(ToParameter)],
			AdvancedFields = [.. advanced.Select(ToParameter)],
		};
	}

	private static ActionParameter ToParameter(JarvisField field)
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
				field.Options ?? [],
				field.Label(),
				defaultValue: field.Default,
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
				defaultValue: field.Default ?? "true",
				required: field.Required),

			_ => ActionParameter.Text(
				field.Name,
				field.Label(),
				placeholder: field.Default,
				defaultValue: field.Default,
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
