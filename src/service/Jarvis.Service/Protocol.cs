using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace Jarvis.Service;

/// <summary>
/// The requests the plugin can make, and the answers it gets back.
/// <para>
/// A request and its reply share one line-delimited JSON object each. That is chosen over a binary framing
/// because the conversation is a handful of small messages between two processes on one machine, and a
/// framing that has to be parsed correctly is a framing that can be got wrong.
/// </para>
/// <para>
/// The protocol is versioned because the service is installed separately from the plugin and can outlive
/// several upgrades of it. A plugin that meets an older service is told so rather than being allowed to
/// send something it will not understand.
/// </para>
/// </summary>
public static class Protocol
{
	/// <summary>Bumped only when the shape of a message changes incompatibly.</summary>
	public const int Version = 1;

	/// <summary>
	/// The pipe name. Includes the user, because the pipe namespace is machine-wide and two users on one
	/// machine must not be able to talk to each other's service.
	/// </summary>
	/// <summary>
/// The pipe name, derived from the signed-in user's security identifier.
/// <para>
/// Not from <see cref="Environment.UserName"/>: the service runs as LocalSystem, so that would name the pipe
/// "jarvis-service-SYSTEM" and the plugin, running as the person at the keyboard, would never reach it. The
/// identifier is stable across renames and unique per user, which is what a shared machine needs.
/// </para>
/// </summary>
public static string PipeName => PipeNameForSid(InteractiveUserSid());

/// <summary>
/// The pipe name for a specific user. A suffix exists so a test can stand up a second listener on its own
/// name: the service allows unlimited instances on one name, so a listener left behind by an earlier test
/// would silently accept the next test's connection.
/// </summary>
public static string PipeNameForSid(string? sid, string? suffix = null)
{
	var who = string.IsNullOrWhiteSpace(sid) ? "unknown" : sid;

	return suffix is null ? $"jarvis-service-{who}" : $"jarvis-service-{who}-{suffix}";
}

/// <summary>Only for tests, which need a listener on a name of its own.</summary>
public static string PipeNameForTest(string suffix) => PipeNameForSid(suffix, suffix);

/// <summary>The signed-in user's identifier, or null when there is no interactive session.</summary>
private static string? InteractiveUserSid() => PipeIdentity.InteractiveUserSid();

	public static readonly string[] Operations =
	[
		"ping",
		"status",
		"registry_get",
		"registry_set",
		"registry_delete",
		"scheduled_task_create",
		"scheduled_task_list",
		"scheduled_task_delete",
		"scheduled_task_run",
		"settings_get",
		"settings_set",
		"shutdown",
	];

	/// <summary>
	/// Builds a request. The version is stamped here so no caller can forget it, which is the failure that
	/// would produce a plugin talking to a service it does not understand.
	/// </summary>
	public static JsonObject Request(string operation, JsonObject? arguments = null) => new()
	{
		["v"] = Version,
		["op"] = operation,
		["args"] = arguments ?? new JsonObject(),
	};

	public static JsonObject Reply(bool ok, string content, JsonObject? data = null) => new()
	{
		["v"] = Version,
		["ok"] = ok,
		["content"] = content,
		["data"] = data,
	};

	/// <summary>
	/// Reads one line as a request, or returns null when it is not one this protocol understands.
	/// <para>
	/// Refusing an unknown shape is deliberate. A service that guessed at what it was being asked would be
	/// the most privileged thing on the machine being talked to by whatever found its pipe.
	/// </para>
	/// </summary>
	public static JsonObject? ParseRequest(string line)
	{
		if (!TryParse(line, out var message))
		{
			return null;
		}

		if (message["v"]?.GetValue<int>() != Version)
		{
			return null;
		}

		var operation = message["op"]?.GetValue<string>();

		if (operation is null || !Operations.Contains(operation, StringComparer.Ordinal))
		{
			return null;
		}

		return message;
	}

	public static bool TryParse(string line, out JsonObject message)
	{
		message = null!;

		try
		{
			// A line longer than this is not one of ours, and parsing it would be a way to make the service
			// allocate whatever the sender asked it to.
			if (line.Length is 0 or > 64 * 1024)
			{
				return false;
			}

			if (JsonNode.Parse(line) is not JsonObject parsed)
			{
				return false;
			}

			message = parsed;
			return true;
		}
		catch (System.Text.Json.JsonException)
		{
			return false;
		}
	}

	/// <summary>
	/// Reads one line as a reply. A reply is less strictly checked than a request because it comes from a
	/// process this one started.
	/// </summary>
	public static JsonObject? ParseReply(string line) =>
		TryParse(line, out var message) ? message : null;
}
