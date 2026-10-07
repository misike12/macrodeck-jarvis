using System.Globalization;
using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Input;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;

namespace Jarvis.Plugin;

/// <summary>How a setting is presented, which is also how it is read back.</summary>
internal enum JarvisFieldKind
{
	Text,
	Multiline,
	Choice,
	Flag,
	Number,
	Secret,
}

/// <summary>
/// One declared setting.
/// <para>
/// Everything that knows a setting exists derives from this list: the steps the setup flow shows, and the
/// fields the store reads back. There used to be three hand-maintained copies of that list, and they had
/// already drifted: thirteen settings were read back but appeared in no step, so a user could not set them
/// at all and the code that consumed them was reading a default forever.
/// </para>
/// </summary>
internal sealed record JarvisField(
	string Name,
	JarvisFieldKind Kind,
	string Step,
	Func<LocalizedText> Label,
	string? Default = null,
	bool Required = false,
	bool Advanced = false,
	IReadOnlyList<ActionParameterOption>? Options = null,
	string? OnlyWhenField = null,
	string? OnlyWhenValue = null,
	Func<LocalizedText>? Description = null,
	double? Minimum = null,
	double? Maximum = null,
	Func<IReadOnlyList<ActionParameterOption>>? OptionsSource = null);

/// <summary>
/// Every setting JARVIS has, declared once.
/// <para>
/// A field that is not here cannot be configured and is not read. That is the point: adding a setting means
/// adding one row, and a test asserts that every field the store reads is reachable from a step and that
/// every field a step shows is read.
/// </para>
/// </summary>
internal static class JarvisFields
{
	public const string ProviderStep = "provider";

	public const string KeysStep = "keys";

	public const string ModelsStep = "models";

	public const string VoiceStep = "voice";

	public const string BehaviourStep = "behaviour";

	private static ActionParameterOption Option(string value, LocalizedText label) =>
		new() { Value = value, Label = label };

	/// <summary>
	/// The stored spelling of an enum member: kebab-case, which is what a form shows and what the store reads
	/// back.
	/// <para>
	/// The option values used to be written out by hand as "llama-cpp", "whisper-cpp", "sapi", "piper",
	/// "magpie", "challenge" and none of those is a member of the enum it stood for. The store parsed the
	/// stored value with <c>Enum.TryParse</c>, so every one of them reverted to the default on the next
	/// reload: picking a local model silently ran the NVIDIA one, and picking the Windows voice still tried
	/// to download Piper. Deriving the value from the member means the two cannot disagree.
	/// </para>
	/// </summary>
	internal static string ValueOf<TEnum>(TEnum value) where TEnum : struct, Enum
	{
		var name = value.ToString();
		var builder = new System.Text.StringBuilder(name.Length + 4);

		for (var index = 0; index < name.Length; index++)
		{
			if (char.IsUpper(name[index]))
			{
				if (index > 0)
				{
					builder.Append('-');
				}

				builder.Append(char.ToLowerInvariant(name[index]));
			}
			else
			{
				builder.Append(name[index]);
			}
		}

		return builder.ToString();
	}

	/// <summary>
	/// The bounds every numeric setting is declared and read against.
	/// <para>
	/// Public because they are not only this file's business: the store reads a stored number against the
	/// bound its own field declares, and a test asserts the turn honours the iteration bound. One set of
	/// numbers, rather than one per place that needs them.
	/// </para>
	/// </summary>
	public const int MinIterations = 1;

	/// <summary>Model round trips one turn may take. Above this the host's own invocation ceiling arrives first.</summary>
	public const int MaxIterations = 16;

	public const int MinTimeoutSeconds = 10;

	public const int MaxTimeoutSeconds = 600;

	public const int MinVolumePercent = 0;

	public const int MaxVolumePercent = 100;

	/// <summary>A threshold is a fraction, so anything at or below zero can never be crossed.</summary>
	public const double MinThreshold = 0.001;

	public const double MaxThreshold = 1;

	public static readonly IReadOnlyList<JarvisField> All =
	[
		// Provider.
		new(JarvisSettingsStoreFields.LlmProviderField, JarvisFieldKind.Choice, ProviderStep,
			() => Strings.ConfigFlow.Provider.Llm.Label(), Default: ValueOf(LlmProvider.NvidiaNim), Required: true,
			Options:
			[
				Option(ValueOf(LlmProvider.NvidiaNim), Strings.ConfigFlow.Option.NvidiaNim()),
				Option(ValueOf(LlmProvider.SelfHostedNim), Strings.ConfigFlow.Option.SelfHosted()),
				Option(ValueOf(LlmProvider.LocalLlamaCpp), Strings.ConfigFlow.Option.LlamaCpp()),
			]),
		new(JarvisSettingsStoreFields.SelfHostedUrlField, JarvisFieldKind.Text, ProviderStep,
			() => Strings.ConfigFlow.Provider.SelfHostedUrl.Label(), Default: "http://localhost:8000/v1",
			OnlyWhenField: JarvisSettingsStoreFields.LlmProviderField, OnlyWhenValue: ValueOf(LlmProvider.SelfHostedNim)),
		new(JarvisSettingsStoreFields.NvidiaBaseUrlField, JarvisFieldKind.Text, ProviderStep,
			() => Strings.ConfigFlow.Provider.NvidiaBaseUrl.Label(), Default: JarvisSettings.DefaultNvidiaBaseUrl,
			Advanced: true),

		// Keys.
		new(JarvisSettingsStoreFields.NvidiaKeyEntryField, JarvisFieldKind.Secret, KeysStep,
			() => Strings.ConfigFlow.Keys.Nvidia.Label(),
			Description: () => Strings.ConfigFlow.Keys.Nvidia.Description()),
		new(JarvisSettingsStoreFields.SelfHostedTokenField, JarvisFieldKind.Secret, KeysStep,
			() => Strings.ConfigFlow.Keys.SelfHostedToken.Label(),
			Description: () => Strings.ConfigFlow.Keys.SelfHostedToken.Description(),
			OnlyWhenField: JarvisSettingsStoreFields.LlmProviderField, OnlyWhenValue: ValueOf(LlmProvider.SelfHostedNim)),
		new(JarvisSettingsStoreFields.PicovoiceKeyField, JarvisFieldKind.Secret, KeysStep,
			() => Strings.ConfigFlow.Keys.Picovoice.Label(),
			Description: () => Strings.ConfigFlow.Keys.Picovoice.Description()),

		// Models.
		new(JarvisSettingsStoreFields.LlmModelField, JarvisFieldKind.Text, ModelsStep,
			() => Strings.ConfigFlow.Models.Llm.Label(), Default: JarvisSettings.DefaultLlmModel, Required: true),
		new(JarvisSettingsStoreFields.VisionProviderField, JarvisFieldKind.Choice, ModelsStep,
			() => Strings.ConfigFlow.Models.VisionProvider.Label(), Default: ValueOf(VisionProvider.NvidiaNim), Required: true,
			Options:
			[
Option(ValueOf(VisionProvider.NvidiaNim), Strings.ConfigFlow.Option.NvidiaNim()),
							Option(ValueOf(VisionProvider.LocalLlamaCpp), Strings.ConfigFlow.Option.LlamaCpp()),
			]),
		new(JarvisSettingsStoreFields.VisionModelField, JarvisFieldKind.Text, ModelsStep,
			() => Strings.ConfigFlow.Models.VisionModel.Label(), Default: JarvisSettings.DefaultVisionModel),
		new(JarvisSettingsStoreFields.SttProviderField, JarvisFieldKind.Choice, ModelsStep,
			() => Strings.ConfigFlow.Models.Stt.Label(), Default: ValueOf(SpeechToTextProvider.WhisperCppLocal), Required: true,
			Options:
			[
Option(ValueOf(SpeechToTextProvider.WhisperCppLocal), Strings.ConfigFlow.Models.Option.WhisperCpp()),
							Option(ValueOf(SpeechToTextProvider.NvidiaNim), Strings.ConfigFlow.Option.NvidiaNim()),
							Option(ValueOf(SpeechToTextProvider.WindowsSapi), Strings.ConfigFlow.Option.Sapi()),
			]),
		new(JarvisSettingsStoreFields.SttModelField, JarvisFieldKind.Text, ModelsStep,
			() => Strings.ConfigFlow.Models.SttModel.Label(), Default: JarvisSettings.DefaultNimSpeechToTextModel,
			OnlyWhenField: JarvisSettingsStoreFields.SttProviderField, OnlyWhenValue: ValueOf(SpeechToTextProvider.NvidiaNim)),
		new(JarvisSettingsStoreFields.WhisperModelField, JarvisFieldKind.Text, ModelsStep,
			() => Strings.ConfigFlow.Models.WhisperModel.Label(), Default: JarvisSettings.DefaultWhisperModel,
			OnlyWhenField: JarvisSettingsStoreFields.SttProviderField, OnlyWhenValue: ValueOf(SpeechToTextProvider.WhisperCppLocal)),

		// Voice and input.
		new(JarvisSettingsStoreFields.TtsProviderField, JarvisFieldKind.Choice, VoiceStep,
			() => Strings.ConfigFlow.Voice.Tts.Label(), Default: ValueOf(TextToSpeechProvider.PiperLocal), Required: true,
			Options:
			[
Option(ValueOf(TextToSpeechProvider.PiperLocal), Strings.ConfigFlow.Voice.Option.Piper()),
							Option(ValueOf(TextToSpeechProvider.NvidiaNim), Strings.ConfigFlow.Option.NvidiaNim()),
							Option(ValueOf(TextToSpeechProvider.WindowsSapi), Strings.ConfigFlow.Option.Sapi()),
			]),
		new(JarvisSettingsStoreFields.PiperVoiceField, JarvisFieldKind.Text, VoiceStep,
			() => Strings.ConfigFlow.Voice.PiperVoice.Label(), Default: "en_GB-alan-medium",
			OnlyWhenField: JarvisSettingsStoreFields.TtsProviderField, OnlyWhenValue: ValueOf(TextToSpeechProvider.PiperLocal)),
		new(JarvisSettingsStoreFields.WindowsVoiceField, JarvisFieldKind.Text, VoiceStep,
			() => Strings.ConfigFlow.Voice.WindowsVoice.Label(),
			OnlyWhenField: JarvisSettingsStoreFields.TtsProviderField, OnlyWhenValue: ValueOf(TextToSpeechProvider.WindowsSapi)),
		new(JarvisSettingsStoreFields.VolumePercentField, JarvisFieldKind.Number, VoiceStep,
			() => Strings.ConfigFlow.Voice.VolumePercent.Label(), Default: "100",
			Minimum: MinVolumePercent, Maximum: MaxVolumePercent),
		new(JarvisSettingsStoreFields.SpeakRepliesField, JarvisFieldKind.Flag, VoiceStep,
			() => Strings.ConfigFlow.Voice.SpeakReplies.Label(), Default: "true"),
		new(JarvisSettingsStoreFields.TtsModelField, JarvisFieldKind.Text, VoiceStep,
			() => Strings.ConfigFlow.Voice.TtsModel.Label(), Default: JarvisSettings.DefaultNimTextToSpeechModel,
			OnlyWhenField: JarvisSettingsStoreFields.TtsProviderField, OnlyWhenValue: ValueOf(TextToSpeechProvider.NvidiaNim)),
		new(JarvisSettingsStoreFields.LanguageField, JarvisFieldKind.Text, VoiceStep,
			() => Strings.ConfigFlow.Voice.Language.Label(), Default: "en", Required: true),
		new(JarvisSettingsStoreFields.WakeEngineField, JarvisFieldKind.Choice, VoiceStep,
			() => Strings.ConfigFlow.Voice.WakeEngine.Label(), Default: ValueOf(WakeWordEngine.OpenWakeWord), Required: true,
			Options:
			[
				Option(ValueOf(WakeWordEngine.OpenWakeWord), Strings.ConfigFlow.Voice.Option.OpenWakeWord()),
				Option(ValueOf(WakeWordEngine.Transcript), Strings.ConfigFlow.Voice.Option.Transcript()),
			]),

		// The wake word had no way to be switched off. It was read back from a field no step declared, so
		// the detector's enabled flag was whatever the default was and the user had no control over it.
		new(JarvisSettingsStoreFields.WakeWordEnabledField, JarvisFieldKind.Flag, VoiceStep,
			() => Strings.ConfigFlow.Voice.WakeWordEnabled.Label(), Default: "true"),
		new(JarvisSettingsStoreFields.WakeWordField, JarvisFieldKind.Text, VoiceStep,
			() => Strings.ConfigFlow.Voice.WakeWord.Label(), Default: "jarvis",
			Description: () => Strings.ConfigFlow.Voice.WakeWord.Description()),
		new(JarvisSettingsStoreFields.WakeSensitivityField, JarvisFieldKind.Number, VoiceStep,
			() => Strings.ConfigFlow.Voice.WakeSensitivity.Label(), Default: WakeWordDetector.DefaultSensitivity.ToString(CultureInfo.InvariantCulture),
			Minimum: MinThreshold, Maximum: MaxThreshold, Advanced: true,
			Description: () => Strings.ConfigFlow.Voice.WakeSensitivity.Description()),
		new(JarvisSettingsStoreFields.MicrophoneAlwaysOnField, JarvisFieldKind.Flag, VoiceStep,
			() => Strings.ConfigFlow.Voice.MicrophoneAlwaysOn.Label(), Default: "true"),
		// Chosen from a list, not typed: endpoint ids look like "{0.0.1.00000000}.{…}" and nobody should
		// have to paste one. The options are enumerated when the step is built, so a microphone plugged
		// in after the flow was opened shows up as soon as the step renders again. An empty value means
		// the system default, which is also what an untouched field reads back as.
		new(JarvisSettingsStoreFields.MicrophoneIdField, JarvisFieldKind.Choice, VoiceStep,
			() => Strings.ConfigFlow.Voice.MicrophoneId.Label(),
			OptionsSource: LiveMicrophoneOptions),
		// Stays as the escape hatch: ids can change when a USB device moves ports, and a name substring
		// still matches then. The store tries the chosen id first and this second.
		new(JarvisSettingsStoreFields.MicrophoneNameField, JarvisFieldKind.Text, VoiceStep,
			() => Strings.ConfigFlow.Voice.MicrophoneName.Label(), Advanced: true),
		new(JarvisSettingsStoreFields.HotkeyField, JarvisFieldKind.Text, VoiceStep,
			() => Strings.ConfigFlow.Voice.Hotkey.Label(), Default: "Ctrl+Alt+J"),

		// Behaviour.
		new(JarvisSettingsStoreFields.SafetyField, JarvisFieldKind.Choice, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.Safety.Label(), Default: ValueOf(SafetyMode.ConfirmAll), Required: true,
			Options:
			[
				Option(ValueOf(SafetyMode.ConfirmAll), Strings.ConfigFlow.Behaviour.Option.SafetyConfirmAll()),
				Option(ValueOf(SafetyMode.Allowlist), Strings.ConfigFlow.Behaviour.Option.SafetyAllowlist()),
				Option(ValueOf(SafetyMode.ToolPermissions), Strings.ConfigFlow.Behaviour.Option.SafetyToolPermissions()),
				Option(ValueOf(SafetyMode.Autonomous), Strings.ConfigFlow.Behaviour.Option.SafetyAutonomous()),
			]),
		new(JarvisSettingsStoreFields.ConfirmationField, JarvisFieldKind.Choice, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.Confirmation.Label(), Default: ValueOf(VoiceConfirmation.Hybrid), Required: true,
			Options:
			[
				Option(ValueOf(VoiceConfirmation.YesNo), Strings.ConfigFlow.Behaviour.Option.ConfirmYesNo()),
				Option(ValueOf(VoiceConfirmation.SpokenChallenge), Strings.ConfigFlow.Behaviour.Option.ConfirmChallenge()),
				Option(ValueOf(VoiceConfirmation.Hybrid), Strings.ConfigFlow.Behaviour.Option.ConfirmHybrid()),
			]),
		new(JarvisSettingsStoreFields.CancelDepthField, JarvisFieldKind.Choice, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.CancelDepth.Label(), Default: ValueOf(CancelDepth.SpeechAndStream), Required: true,
			Options:
			[
				Option(ValueOf(CancelDepth.SpeechAndStream), Strings.ConfigFlow.Behaviour.Option.CancelSpeech()),
				Option(ValueOf(CancelDepth.StopRunningCommand), Strings.ConfigFlow.Behaviour.Option.CancelCommand()),
			]),
		new(JarvisSettingsStoreFields.BargeInField, JarvisFieldKind.Flag, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.BargeIn.Label(), Default: "true"),
		new(JarvisSettingsStoreFields.BargeInThresholdField, JarvisFieldKind.Number, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.BargeInThreshold.Label(), Default: "0.12",
			Minimum: MinThreshold, Maximum: MaxThreshold, Advanced: true),
		new(JarvisSettingsStoreFields.MaxIterationsField, JarvisFieldKind.Number, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.MaxIterations.Label(), Default: "4",
			Minimum: MinIterations, Maximum: MaxIterations,
			Advanced: true),
		new(JarvisSettingsStoreFields.TimeoutField, JarvisFieldKind.Number, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.Timeout.Label(), Default: "120",
			Minimum: MinTimeoutSeconds, Maximum: MaxTimeoutSeconds,
			Advanced: true),

		// The three standing permissions and the allowlist. All four were read by the safety gate and set by
		// no step, so the tool-permission mode asked for every tool forever and the allowlist mode permitted
		// the same fixed commands whatever the user wanted.
		new(JarvisSettingsStoreFields.PermitReadField, JarvisFieldKind.Flag, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.PermitRead.Label(), Default: "false",
			OnlyWhenField: JarvisSettingsStoreFields.SafetyField, OnlyWhenValue: ValueOf(SafetyMode.ToolPermissions)),
		new(JarvisSettingsStoreFields.PermitWriteField, JarvisFieldKind.Flag, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.PermitWrite.Label(), Default: "false",
			OnlyWhenField: JarvisSettingsStoreFields.SafetyField, OnlyWhenValue: ValueOf(SafetyMode.ToolPermissions)),
		new(JarvisSettingsStoreFields.PermitExecuteField, JarvisFieldKind.Flag, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.PermitExecute.Label(), Default: "false",
			OnlyWhenField: JarvisSettingsStoreFields.SafetyField, OnlyWhenValue: ValueOf(SafetyMode.ToolPermissions)),
		new(JarvisSettingsStoreFields.CommandAllowlistField, JarvisFieldKind.Text, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.CommandAllowlist.Label(),
			Default: string.Join(',', JarvisSettings.DefaultCommandAllowlist),
			OnlyWhenField: JarvisSettingsStoreFields.SafetyField, OnlyWhenValue: ValueOf(SafetyMode.Allowlist)),
		new(JarvisSettingsStoreFields.MemoryField, JarvisFieldKind.Choice, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.Memory.Label(), Default: ValueOf(MemoryMode.PersistentNotes), Required: true,
			Options:
			[
				Option(ValueOf(MemoryMode.None), Strings.ConfigFlow.Behaviour.Option.MemoryNone()),
				Option(ValueOf(MemoryMode.Session), Strings.ConfigFlow.Behaviour.Option.MemorySession()),
				Option(ValueOf(MemoryMode.Persistent), Strings.ConfigFlow.Behaviour.Option.MemoryPersistent()),
				Option(ValueOf(MemoryMode.PersistentNotes), Strings.ConfigFlow.Behaviour.Option.MemoryNotes()),
			]),
		new(JarvisSettingsStoreFields.PersonaField, JarvisFieldKind.Choice, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.Persona.Label(), Default: ValueOf(PersonaPreset.ClassicJarvis), Required: true,
			Options:
			[
				Option(ValueOf(PersonaPreset.ClassicJarvis), Strings.ConfigFlow.Behaviour.Option.PersonaClassic()),
				Option(ValueOf(PersonaPreset.Terse), Strings.ConfigFlow.Behaviour.Option.PersonaTerse()),
				Option(ValueOf(PersonaPreset.Sarcastic), Strings.ConfigFlow.Behaviour.Option.PersonaSarcastic()),
				Option(ValueOf(PersonaPreset.Formal), Strings.ConfigFlow.Behaviour.Option.PersonaFormal()),
				Option(ValueOf(PersonaPreset.Custom), Strings.ConfigFlow.Behaviour.Option.PersonaCustom()),
			]),
		new(JarvisSettingsStoreFields.PromptField, JarvisFieldKind.Multiline, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.Prompt.Label(), Advanced: true),
		new(JarvisSettingsStoreFields.NotesField, JarvisFieldKind.Multiline, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.Notes.Label(), Advanced: true),

		// The three elevated-service switches. All three were declared, read back and shown nowhere, so a
		// user who turned the service off still had a model calling it.
		new(JarvisSettingsStoreFields.ServiceEnabledField, JarvisFieldKind.Flag, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.ServiceEnabled.Label(), Default: "false"),
		new(JarvisSettingsStoreFields.ServiceSchedulingField, JarvisFieldKind.Flag, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.ServiceScheduling.Label(), Default: "false",
			OnlyWhenField: JarvisSettingsStoreFields.ServiceEnabledField, OnlyWhenValue: "true"),
		new(JarvisSettingsStoreFields.ServiceAdminField, JarvisFieldKind.Flag, BehaviourStep,
			() => Strings.ConfigFlow.Behaviour.ServiceAdmin.Label(), Default: "true",
			OnlyWhenField: JarvisSettingsStoreFields.ServiceEnabledField, OnlyWhenValue: "true"),
	];

	/// <summary>
	/// The fields the store reads back as plain strings. Secrets are excluded because they are read through
	/// the host's secret store instead, and a secret read as a string would land in a log.
	/// </summary>
	public static readonly string[] ReadBackAsText =
		[.. All.Where(field => field.Kind is not JarvisFieldKind.Secret).Select(field => field.Name)];

	/// <summary>The fields for one step, split into the ones shown and the ones behind Advanced.</summary>
	public static (IReadOnlyList<JarvisField> Fields, IReadOnlyList<JarvisField> Advanced) ForStep(string step)
	{
		var inStep = All.Where(field => field.Step == step).ToArray();

		return (inStep.Where(field => !field.Advanced).ToArray(), inStep.Where(field => field.Advanced).ToArray());
	}

	/// <summary>
	/// The microphone dropdown options for the devices on this machine right now.
	/// <para>
	/// Takes the devices instead of enumerating them so tests can pass fakes: the enumeration itself is
	/// NAudio against real hardware and has nothing worth asserting.
	/// </para>
	/// </summary>
	internal static IReadOnlyList<ActionParameterOption> MicrophoneOptions(IReadOnlyList<AudioDevice> devices)
	{
		var options = new List<ActionParameterOption>(devices.Count + 1)
		{
			new() { Value = string.Empty, Label = Strings.ConfigFlow.Voice.MicrophoneDefault() },
		};

		foreach (var device in devices)
		{
			options.Add(new()
			{
				Value = device.Id,
				Label = device.IsDefault ? Strings.ConfigFlow.Voice.MicrophoneIsDefault(device.Name) : device.Name,
			});
		}

		return options;
	}

	private static IReadOnlyList<ActionParameterOption> LiveMicrophoneOptions() =>
		MicrophoneOptions(AudioDeviceCatalog.CaptureDevices());
}
