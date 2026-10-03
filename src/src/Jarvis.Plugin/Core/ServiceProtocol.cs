using System.Text.Json.Nodes;

namespace Jarvis.Plugin.Core;

/// <summary>
/// The wire format the plugin and the service share.
/// <para>
/// Written out in both projects rather than shared through a referenced assembly, because the service must
/// be installable on its own with no dependency on the plugin being present. The two copies are pinned to
/// the same version number, and a version mismatch is refused rather than guessed at.
/// </para>
/// </summary>
public static class ServiceProtocol
{
	/// <summary>Bumped only when a message shape changes incompatibly.</summary>
	public const int Version = 1;

	public static readonly string[] Operations =
	[
		"ping",
		"status",
		"registry_get",
		"registry_set",
		"registry_delete",
		"shutdown",
	];

	/// <summary>
	/// The pipe name, fixed.
	/// <para>
	/// It was once built from this process's security identifier, which the service cannot reproduce: it runs
	/// as LocalSystem and would have to resolve the signed-in user's identifier for itself. The two sides would
	/// then disagree on the name whenever that resolution failed, and the plugin could not reach the service.
	/// A pipe name is world-enumerable anyway, so access is enforced by the descriptor on the pipe rather than
	/// by what it is called. This must match <c>Protocol.PipeName</c> in the service.
	/// </para>
	/// </summary>
	public const string PipeName = "jarvis-service";

	/// <summary>A pipe name carrying a caller-chosen suffix, used by tests that need a listener of their own.</summary>
	public static string PipeNameForSuffix(string suffix) => $"{PipeName}-{suffix}";

public static JsonObject Request(string operation, JsonObject? arguments = null) => new()
	{
		["v"] = Version,
		["op"] = operation,
		["args"] = arguments ?? new JsonObject(),
	};

	public static bool TryParse(string line, out JsonObject message)
	{
		message = null!;

		try
		{
			// Bounded, because the reply is read from a pipe any process running as the user can write to.
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
	/// The message text from a reply, or a reason the reply could not be used. A malformed reply is treated
	/// as a failure rather than as an empty answer, so a caller cannot mistake it for success.
	/// </summary>
	public static (bool Ok, string Content) Read(JsonObject? reply)
	{
		if (reply is null)
		{
			return (false, "The elevated service did not answer.");
		}

		if (reply["v"]?.GetValue<int>() != Version)
		{
			return (false, "The elevated service is a different version than this plugin.");
		}

		return (
			reply["ok"]?.GetValue<bool>() == true,
			reply["content"]?.GetValue<string>() ?? string.Empty);
	}
}
