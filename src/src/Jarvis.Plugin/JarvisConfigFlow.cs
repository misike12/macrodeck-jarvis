using Jarvis.Plugin.Core;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;

namespace Jarvis.Plugin;

/// <summary>
/// The setup flow. Keys are written as <see cref="ConfigFlowValue.Secret"/> so the host stores them in
/// its encrypted secret store rather than beside an ordinary setting.
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
			BehaviourStepId => Complete(input),
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

	private static ConfigFlowResult Complete(IReadOnlyDictionary<string, object?> input)
	{
		var values = new Dictionary<string, ConfigFlowValue>
		{
			[JarvisSettingsStoreFields.NvidiaKeyEntryField] = ConfigFlowValue.Secret(Read(input, JarvisSettingsStoreFields.NvidiaKeyEntryField)),
			[JarvisSettingsStoreFields.PicovoiceKeyField] = ConfigFlowValue.Secret(Read(input, JarvisSettingsStoreFields.PicovoiceKeyField)),
			[JarvisSettingsStoreFields.SelfHostedTokenField] = ConfigFlowValue.Secret(Read(input, JarvisSettingsStoreFields.SelfHostedTokenField)),
			[JarvisSettingsStoreFields.LlmProviderField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.LlmProviderField)),
			[JarvisSettingsStoreFields.SelfHostedUrlField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.SelfHostedUrlField)),
			[JarvisSettingsStoreFields.NvidiaBaseUrlField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.NvidiaBaseUrlField)),
			[JarvisSettingsStoreFields.LlmModelField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.LlmModelField)),
			[JarvisSettingsStoreFields.VisionProviderField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.VisionProviderField)),
			[JarvisSettingsStoreFields.VisionModelField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.VisionModelField)),
			[JarvisSettingsStoreFields.SttProviderField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.SttProviderField)),
			[JarvisSettingsStoreFields.SttModelField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.SttModelField)),
			[JarvisSettingsStoreFields.TtsProviderField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.TtsProviderField)),
			[JarvisSettingsStoreFields.PiperVoiceField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.PiperVoiceField)),
			[JarvisSettingsStoreFields.LanguageField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.LanguageField)),
			[JarvisSettingsStoreFields.WakeEngineField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.WakeEngineField)),
			[JarvisSettingsStoreFields.WakeWordField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.WakeWordField)),
			[JarvisSettingsStoreFields.HotkeyField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.HotkeyField)),
			[JarvisSettingsStoreFields.SafetyField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.SafetyField)),
			[JarvisSettingsStoreFields.ConfirmationField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.ConfirmationField)),
			[JarvisSettingsStoreFields.CancelDepthField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.CancelDepthField)),
			[JarvisSettingsStoreFields.MemoryField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.MemoryField)),
			[JarvisSettingsStoreFields.PersonaField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.PersonaField)),
			[JarvisSettingsStoreFields.PromptField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.PromptField)),
			[JarvisSettingsStoreFields.NotesField] = ConfigFlowValue.Plain(Read(input, JarvisSettingsStoreFields.NotesField)),
		};

		return ConfigFlowResult.Complete("JARVIS", values);
	}

	private static ConfigFlowStep ProviderStep() => new()
	{
		StepId = ProviderStepId,
		Title = Strings.ConfigFlow.Provider.Title(),
		Description = Strings.ConfigFlow.Provider.Description(),
		Fields =
		[
			Choice(JarvisSettingsStoreFields.LlmProviderField, ProviderOptions(), Strings.ConfigFlow.Provider.Llm.Label(), defaultValue: "nvidia-nim"),
			Text(JarvisSettingsStoreFields.SelfHostedUrlField, Strings.ConfigFlow.Provider.SelfHostedUrl.Label(), placeholder: "http://localhost:8000/v1")
				.OnlyWhen(JarvisSettingsStoreFields.LlmProviderField, "self-hosted-nim"),
			Text(JarvisSettingsStoreFields.NvidiaBaseUrlField, Strings.ConfigFlow.Provider.NvidiaBaseUrl.Label(), defaultValue: JarvisSettings.DefaultNvidiaBaseUrl),
		],
	};

	private static ConfigFlowStep KeysStep() => new()
	{
		StepId = KeysStepId,
		Title = Strings.ConfigFlow.Keys.Title(),
		Description = Strings.ConfigFlow.Keys.Description(),
		Fields =
		[
			ActionParameter.Secret(
				JarvisSettingsStoreFields.NvidiaKeyEntryField,
				Strings.ConfigFlow.Keys.Nvidia.Label(),
				Strings.ConfigFlow.Keys.Nvidia.Description()),
			ActionParameter.Secret(
				JarvisSettingsStoreFields.SelfHostedTokenField,
				Strings.ConfigFlow.Keys.SelfHostedToken.Label(),
				Strings.ConfigFlow.Keys.SelfHostedToken.Description())
				.OnlyWhen(JarvisSettingsStoreFields.LlmProviderField, "self-hosted-nim"),
			ActionParameter.Secret(
				JarvisSettingsStoreFields.PicovoiceKeyField,
				Strings.ConfigFlow.Keys.Picovoice.Label(),
				Strings.ConfigFlow.Keys.Picovoice.Description()),
		],
		AdvancedFields =
		[
			Text(JarvisSettingsStoreFields.NvidiaBaseUrlField, Strings.ConfigFlow.Provider.NvidiaBaseUrl.Label()),
		],
	};

	private static ConfigFlowStep ModelsStep() => new()
	{
		StepId = ModelsStepId,
		Title = Strings.ConfigFlow.Models.Title(),
		Description = Strings.ConfigFlow.Models.Description(),
		Fields =
		[
			Text(JarvisSettingsStoreFields.LlmModelField, Strings.ConfigFlow.Models.Llm.Label(), defaultValue: JarvisSettings.DefaultLlmModel, required: true),
			Choice(JarvisSettingsStoreFields.VisionProviderField, VisionOptions(), Strings.ConfigFlow.Models.VisionProvider.Label(), defaultValue: "nvidia-nim"),
			Text(JarvisSettingsStoreFields.VisionModelField, Strings.ConfigFlow.Models.VisionModel.Label(), defaultValue: JarvisSettings.DefaultVisionModel),
			Choice(JarvisSettingsStoreFields.SttProviderField, SttOptions(), Strings.ConfigFlow.Models.Stt.Label(), defaultValue: "whisper-cpp"),
			Text(JarvisSettingsStoreFields.SttModelField, Strings.ConfigFlow.Models.SttModel.Label(), defaultValue: JarvisSettings.DefaultNimSpeechToTextModel)
				.OnlyWhen(JarvisSettingsStoreFields.SttProviderField, "nvidia-nim"),
		],
		AdvancedFields =
		[
			Text(JarvisSettingsStoreFields.VisionModelField, Strings.ConfigFlow.Models.VisionModel.Label()),
			Text(JarvisSettingsStoreFields.SttModelField, Strings.ConfigFlow.Models.SttModel.Label()),
		],
	};

	private static ConfigFlowStep VoiceStep() => new()
	{
		StepId = VoiceStepId,
		Title = Strings.ConfigFlow.Voice.Title(),
		Description = Strings.ConfigFlow.Voice.Description(),
		Fields =
		[
			Choice(JarvisSettingsStoreFields.TtsProviderField, TtsOptions(), Strings.ConfigFlow.Voice.Tts.Label(), defaultValue: "piper"),
			Text(JarvisSettingsStoreFields.PiperVoiceField, Strings.ConfigFlow.Voice.PiperVoice.Label(), defaultValue: "en_GB-alan-medium")
				.OnlyWhen(JarvisSettingsStoreFields.TtsProviderField, "piper"),
			Text(JarvisSettingsStoreFields.TtsModelField, Strings.ConfigFlow.Voice.TtsModel.Label(), defaultValue: JarvisSettings.DefaultNimTextToSpeechModel)
				.OnlyWhen(JarvisSettingsStoreFields.TtsProviderField, "nvidia-nim"),
			Text(JarvisSettingsStoreFields.LanguageField, Strings.ConfigFlow.Voice.Language.Label(), defaultValue: "en", required: true),
			Choice(JarvisSettingsStoreFields.WakeEngineField, WakeOptions(), Strings.ConfigFlow.Voice.WakeEngine.Label(), defaultValue: "porcupine"),
			Text(JarvisSettingsStoreFields.WakeWordField, Strings.ConfigFlow.Voice.WakeWord.Label(), defaultValue: "jarvis"),
			Text(JarvisSettingsStoreFields.HotkeyField, Strings.ConfigFlow.Voice.Hotkey.Label(), defaultValue: "Ctrl+Alt+J"),
		],
	};

	private static ConfigFlowStep BehaviourStep() => new()
	{
		StepId = BehaviourStepId,
		Title = Strings.ConfigFlow.Behaviour.Title(),
		Description = Strings.ConfigFlow.Behaviour.Description(),
		Fields =
		[
			Choice(JarvisSettingsStoreFields.SafetyField, SafetyOptions(), Strings.ConfigFlow.Behaviour.Safety.Label(), defaultValue: "confirm-all"),
			Choice(JarvisSettingsStoreFields.ConfirmationField, ConfirmationOptions(), Strings.ConfigFlow.Behaviour.Confirmation.Label(), defaultValue: "hybrid"),
			Choice(JarvisSettingsStoreFields.CancelDepthField, CancelOptions(), Strings.ConfigFlow.Behaviour.CancelDepth.Label(), defaultValue: "speech-and-stream"),
			Choice(JarvisSettingsStoreFields.MemoryField, MemoryOptions(), Strings.ConfigFlow.Behaviour.Memory.Label(), defaultValue: "persistent-notes"),
			Choice(JarvisSettingsStoreFields.PersonaField, PersonaOptions(), Strings.ConfigFlow.Behaviour.Persona.Label(), defaultValue: "classic-jarvis"),
		],
		AdvancedFields =
		[
			Multiline(JarvisSettingsStoreFields.PromptField, Strings.ConfigFlow.Behaviour.Prompt.Label()),
			Multiline(JarvisSettingsStoreFields.NotesField, Strings.ConfigFlow.Behaviour.Notes.Label()),
		],
	};

	private static IReadOnlyList<ActionParameterOption> ProviderOptions() =>
	[
		Option("nvidia-nim", Strings.ConfigFlow.Option.NvidiaNim()),
		Option("self-hosted-nim", Strings.ConfigFlow.Option.SelfHosted()),
		Option("llama-cpp", Strings.ConfigFlow.Option.LlamaCpp()),
	];

	private static IReadOnlyList<ActionParameterOption> VisionOptions() =>
	[
		Option("nvidia-nim", Strings.ConfigFlow.Option.NvidiaNim()),
		Option("llama-cpp", Strings.ConfigFlow.Option.LlamaCpp()),
	];

	private static IReadOnlyList<ActionParameterOption> SttOptions() =>
	[
		Option("whisper-cpp", Strings.ConfigFlow.Models.Option.WhisperCpp()),
		Option("nvidia-nim", Strings.ConfigFlow.Option.NvidiaNim()),
		Option("sapi", Strings.ConfigFlow.Option.Sapi()),
	];

	private static IReadOnlyList<ActionParameterOption> TtsOptions() =>
	[
		Option("piper", Strings.ConfigFlow.Voice.Option.Piper()),
		Option("nvidia-nim", Strings.ConfigFlow.Option.NvidiaNim()),
		Option("sapi", Strings.ConfigFlow.Option.Sapi()),
	];

	private static IReadOnlyList<ActionParameterOption> WakeOptions() =>
	[
		Option("porcupine", Strings.ConfigFlow.Voice.Option.Porcupine()),
		Option("nanowakeword", Strings.ConfigFlow.Voice.Option.NanoWakeWord()),
		Option("vosk", Strings.ConfigFlow.Voice.Option.Vosk()),
	];

	private static IReadOnlyList<ActionParameterOption> SafetyOptions() =>
	[
		Option("confirm-all", Strings.ConfigFlow.Behaviour.Option.SafetyConfirmAll()),
		Option("allowlist", Strings.ConfigFlow.Behaviour.Option.SafetyAllowlist()),
		Option("tool-permissions", Strings.ConfigFlow.Behaviour.Option.SafetyToolPermissions()),
		Option("autonomous", Strings.ConfigFlow.Behaviour.Option.SafetyAutonomous()),
	];

	private static IReadOnlyList<ActionParameterOption> ConfirmationOptions() =>
	[
		Option("yes-no", Strings.ConfigFlow.Behaviour.Option.ConfirmYesNo()),
		Option("challenge", Strings.ConfigFlow.Behaviour.Option.ConfirmChallenge()),
		Option("hybrid", Strings.ConfigFlow.Behaviour.Option.ConfirmHybrid()),
	];

	private static IReadOnlyList<ActionParameterOption> CancelOptions() =>
	[
		Option("speech-and-stream", Strings.ConfigFlow.Behaviour.Option.CancelSpeech()),
		Option("stop-running-command", Strings.ConfigFlow.Behaviour.Option.CancelCommand()),
	];

	private static IReadOnlyList<ActionParameterOption> MemoryOptions() =>
	[
		Option("none", Strings.ConfigFlow.Behaviour.Option.MemoryNone()),
		Option("session", Strings.ConfigFlow.Behaviour.Option.MemorySession()),
		Option("persistent", Strings.ConfigFlow.Behaviour.Option.MemoryPersistent()),
		Option("persistent-notes", Strings.ConfigFlow.Behaviour.Option.MemoryNotes()),
	];

	private static IReadOnlyList<ActionParameterOption> PersonaOptions() =>
	[
		Option("classic-jarvis", Strings.ConfigFlow.Behaviour.Option.PersonaClassic()),
		Option("terse", Strings.ConfigFlow.Behaviour.Option.PersonaTerse()),
		Option("sarcastic", Strings.ConfigFlow.Behaviour.Option.PersonaSarcastic()),
		Option("formal", Strings.ConfigFlow.Behaviour.Option.PersonaFormal()),
		Option("custom", Strings.ConfigFlow.Behaviour.Option.PersonaCustom()),
	];

	private static ActionParameterOption Option(string value, LocalizedText label) =>
		new() { Value = value, Label = label };

	private static ActionParameter Choice(string name, IReadOnlyList<ActionParameterOption> options, LocalizedText label, string defaultValue) =>
		ActionParameter.Choice(name, options, label, defaultValue: defaultValue, required: true);

	private static ActionParameter Text(string name, LocalizedText label, string? placeholder = null, string? defaultValue = null, bool required = false) =>
		ActionParameter.Text(name, label, placeholder: placeholder, defaultValue: defaultValue, required: required);

	private static ActionParameter Multiline(string name, LocalizedText label) =>
		ActionParameter.MultilineText(name, label);

	private static string Read(IReadOnlyDictionary<string, object?> input, string name) =>
		input.GetValueOrDefault(name)?.ToString() ?? string.Empty;
}