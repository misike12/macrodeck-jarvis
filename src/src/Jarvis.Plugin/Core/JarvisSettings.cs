namespace Jarvis.Plugin.Core;

/// <summary>
/// Everything a user can configure. The integration config flow writes the secret-bearing half;
/// this object is the merged result the rest of the plugin reads.
/// </summary>
public sealed record JarvisSettings
{
	public const string DefaultNvidiaBaseUrl = "https://integrate.api.nvidia.com/v1";

	public string NvidiaApiKey { get; init; } = string.Empty;

	public string NvidiaBaseUrl { get; init; } = DefaultNvidiaBaseUrl;

	public string SelfHostedBaseUrl { get; init; } = string.Empty;

	public string SelfHostedToken { get; init; } = string.Empty;

	public string PicovoiceAccessKey { get; init; } = string.Empty;

	public const string DefaultLlmModel = "nvidia/nemotron-3-super-120b-a12b";

	public const string DefaultFastLlmModel = "nvidia/nemotron-3.5-lightning-30b-a3b";

	public const string DefaultVisionModel = "meta/llama-3.2-90b-vision-instruct";

	public const string DefaultNimSpeechToTextModel = "nvidia/parakeet-tdt-0.6b-v2";

	public const string DefaultNimTextToSpeechModel = "nvidia/magpie-tts-flow";

	public LlmProvider Llm { get; init; } = LlmProvider.NvidiaNim;

	public string LlmModel { get; init; } = DefaultLlmModel;

	public VisionProvider Vision { get; init; } = VisionProvider.NvidiaNim;

	public string VisionModel { get; init; } = DefaultVisionModel;

	public SpeechToTextProvider SpeechToText { get; init; } = SpeechToTextProvider.WhisperCppLocal;

	public string NimSpeechToTextModel { get; init; } = DefaultNimSpeechToTextModel;

	public string WhisperModel { get; init; } = "base-q5_1";

	public string Language { get; init; } = "en";

	public TextToSpeechProvider TextToSpeech { get; init; } = TextToSpeechProvider.PiperLocal;

	public string NimTextToSpeechModel { get; init; } = DefaultNimTextToSpeechModel;

	/// <summary>
	/// Language passed to the speech recogniser. Empty or "auto" means detect it, which is the default:
	/// a user who speaks more than one language should not have to say which before every turn.
	/// </summary>
	public string SttLanguage { get; init; } = string.Empty;

	public string PiperVoice { get; init; } = "en_GB-alan-medium";

	/// <summary>Voice name as Windows reports it, used when the built-in synthesizer is selected.</summary>
	public string WindowsVoice { get; init; } = string.Empty;

	public int VolumePercent { get; init; } = 100;

	/// <summary>Whether replies are spoken. When false the orb still reacts, nothing is audible.</summary>
	public bool SpeakReplies { get; init; } = true;

	public WakeWordEngine WakeWordEngine { get; init; } = WakeWordEngine.Porcupine;

	public string WakeWord { get; init; } = "jarvis";

	public double WakeWordSensitivity { get; init; } = 0.6;

	public string PushToTalkHotkey { get; init; } = "Ctrl+Alt+J";

	/// <summary>Endpoint id of the microphone to prefer. Empty means the system default.</summary>
	public string MicrophoneId { get; init; } = string.Empty;

	/// <summary>
	/// Substring of the microphone's friendly name. Used when the stored id is gone, which is the common
	/// case: a USB endpoint's id changes when it is re-enumerated, the name does not.
	/// </summary>
	public string MicrophoneName { get; init; } = "WO Mic";

	/// <summary>Whether the microphone stays open so the orb and the wake word can both react.</summary>
	public bool MicrophoneAlwaysOn { get; init; } = true;

	public LifetimeTier Lifetime { get; init; } = LifetimeTier.PluginOnly;

	public bool BargeInEnabled { get; init; } = true;

	public double BargeInThreshold { get; init; } = 0.25;

	public SafetyMode Safety { get; init; } = SafetyMode.ConfirmAll;

	public VoiceConfirmation Confirmation { get; init; } = VoiceConfirmation.Hybrid;

	public CancelDepth CancelDepth { get; init; } = CancelDepth.SpeechAndStream;

	public int MaxIterations { get; init; } = 4;

	public int ConversationTimeoutSeconds { get; init; } = 20;

	public MemoryMode Memory { get; init; } = MemoryMode.PersistentNotes;

	public PersonaPreset Persona { get; init; } = PersonaPreset.ClassicJarvis;

	public string CustomSystemPrompt { get; init; } = string.Empty;

	public string Notes { get; init; } = string.Empty;

	public bool ElevatedServiceEnabled { get; init; }

	public bool ElevatedServiceScheduling { get; init; }

	public bool ElevatedServiceAdminOperations { get; init; }

	public IReadOnlyList<string> CommandAllowlist { get; init; } = ["dir", "echo", "git", "dotnet", "code", "type", "where", "tasklist"];

	public bool HasLlmCredentials => Llm switch
	{
		LlmProvider.NvidiaNim => !string.IsNullOrWhiteSpace(NvidiaApiKey),
		LlmProvider.SelfHostedNim => !string.IsNullOrWhiteSpace(SelfHostedBaseUrl),
		LlmProvider.LocalLlamaCpp => true,
		_ => false,
	};

	public bool HasWakeWordCredentials => WakeWordEngine switch
	{
		WakeWordEngine.Porcupine => !string.IsNullOrWhiteSpace(PicovoiceAccessKey),
		WakeWordEngine.NanoWakeWord => true,
		WakeWordEngine.Vosk => true,
		_ => false,
	};
}