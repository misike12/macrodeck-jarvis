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