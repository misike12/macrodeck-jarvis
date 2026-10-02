namespace Jarvis.Plugin.Core;

/// <summary>The states JARVIS moves through. Persisted nowhere; only the current one is observable.</summary>
public enum AssistantState
{
	Idle,
	Listening,
	Thinking,
	Speaking,
	Executing,
	Confirming,
	Error,
	Unavailable,
}