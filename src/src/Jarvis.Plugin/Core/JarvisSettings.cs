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

	public bool WakeWordEngineEnabled { get; init; }

	public WakeWordEngine WakeWordEngine { get; init; } = WakeWordEngine.OpenWakeWord;

	public string WakeWord { get; init; } = "jarvis";

	/// <summary>
	/// RMS the microphone must exceed before a wake word can fire. Deliberately well under 0.1: measured
	/// room tone on this machine is 0.036 and a speaking voice peaks far below 0.6, so the earlier default
	/// of 0.6 sat above anything the microphone would ever see and the wake word could never fire at all.
	/// </summary>
	public double WakeWordSensitivity { get; init; } = 0.06;

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

	/// <summary>
	/// RMS above which the user is considered to be speaking over JARVIS. Lower than the wake word's
	/// threshold would be wrong in the other direction, so it sits between room tone and speech.
	/// </summary>
	public double BargeInThreshold { get; init; } = 0.12;

	public SafetyMode Safety { get; init; } = SafetyMode.ConfirmAll;

	/// <summary>
	/// Standing permissions, used only by <see cref="SafetyMode.ToolPermissions"/>. All false by default, so
	/// switching to that mode without granting anything asks for everything rather than allowing anything.
	/// </summary>
	public bool PermitRead { get; init; }

	public bool PermitWrite { get; init; }

	public bool PermitExecute { get; init; }

	public VoiceConfirmation Confirmation { get; init; } = VoiceConfirmation.Hybrid;

	public CancelDepth CancelDepth { get; init; } = CancelDepth.SpeechAndStream;

	public int MaxIterations { get; init; } = 4;

	public int ConversationTimeoutSeconds { get; init; } = 120;

	public MemoryMode Memory { get; init; } = MemoryMode.PersistentNotes;

	public PersonaPreset Persona { get; init; } = PersonaPreset.ClassicJarvis;

	public string CustomSystemPrompt { get; init; } = string.Empty;

	public string Notes { get; init; } = string.Empty;

	/// <summary>
	/// The notes file's current contents, carried on the settings so the prompt can read it without
	/// depending on <c>MemoryStore</c>. Set by the integration on each turn. Not persisted and not a config
	/// field: the file is the thing that is authoritative, and a config copy of it would be a second
	/// source that could disagree.
	/// </summary>
	public string NotesFileText { get; init; } = string.Empty;

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
		// Every engine this build ships runs without an account. The switch stays because the question
		// "can this engine work at all here" is still asked, and the answer for the keyword spotter is
		// its models being downloadable rather than a key being present.
		WakeWordEngine.OpenWakeWord => true,
		WakeWordEngine.Transcript => true,
		_ => false,
	};
}