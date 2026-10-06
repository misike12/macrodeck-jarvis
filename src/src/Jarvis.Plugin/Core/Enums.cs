namespace Jarvis.Plugin.Core;

public enum LlmProvider
{
	NvidiaNim,
	SelfHostedNim,
	LocalLlamaCpp,
}

public enum SpeechToTextProvider
{
	WhisperCppLocal,
	NvidiaNim,
	WindowsSapi,
}

public enum TextToSpeechProvider
{
	PiperLocal,
	NvidiaNim,
	WindowsSapi,
}

/// <summary>
/// How the wake word is spotted. The keyword spotter runs entirely in-process on ONNX Runtime and needs
/// no account; the transcript engine is the original level-then-recognise path, kept selectable because
/// it is the one that can match any word the settings name, not just the keywords a model exists for.
/// </summary>
public enum WakeWordEngine
{
	/// <summary>openWakeWord's keyword models, run in-process. The default: fastest and fully offline.</summary>
	OpenWakeWord,

	/// <summary>
	/// The transcript engine: wait for the level to say someone spoke, transcribe the utterance, match
	/// the word in the text. Slower per detection, but works with any configured word.
	/// </summary>
	Transcript,
}

public enum LifetimeTier
{
	PluginOnly,
	BackgroundProcess,
	TrayCompanion,
}

public enum VisionProvider
{
	NvidiaNim,
	LocalLlamaCpp,
}

/// <summary>
/// What a tool does, which is what a standing permission is granted against. Coarse on purpose.
/// </summary>
public enum ToolClass
{
	/// <summary>Only looks at things.</summary>
	Read,

	/// <summary>Runs something on this machine.</summary>
	Execute,

	/// <summary>Changes something: a file, the clipboard, an outbound request.</summary>
	Write,

	/// <summary>Anything a permission has not been thought about, which asks.</summary>
	Other,
}

public enum SafetyMode
{
	ConfirmAll,
	Allowlist,
	ToolPermissions,
	Autonomous,
}

public enum VoiceConfirmation
{
	YesNo,
	SpokenChallenge,
	Hybrid,
}

public enum SessionScope
{
	Global,
	Local,
}

public enum OrbPreset
{
	ArcReactor,
	Halo,
	Pulse,
	Custom,
}

public enum TextDisplayMode
{
	FullTranscript,
	LastReply,
	InterimPlusReply,
}

public enum MemoryMode
{
	None,
	Session,
	Persistent,
	PersistentNotes,
}

public enum CancelDepth
{
	SpeechAndStream,
	StopRunningCommand,
}

public enum PersonaPreset
{
	ClassicJarvis,
	Terse,
	Sarcastic,
	Formal,
	Custom,
}