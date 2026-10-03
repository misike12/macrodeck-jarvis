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

public enum WakeWordEngine
{
	Porcupine,
	NanoWakeWord,
	Vosk,
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