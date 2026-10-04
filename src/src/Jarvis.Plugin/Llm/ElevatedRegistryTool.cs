using System.Text.Json.Nodes;
using Jarvis.Plugin.Core;
using Serilog;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// Asks the elevated service to do a registry operation, when there is one.
/// <para>
/// This is a fallback rather than a replacement. A registry change under HKCU is something the plugin can
/// do itself and does, because a request that has to cross a pipe and wait for another process is a worse
/// way to do something the plugin is already allowed to do. The service is consulted only for the cases the
/// plugin genuinely cannot reach, which is HKLM.
/// </para>
/// <para>
/// Every failure here is reported as the reason the operation did not happen, never as a silent fall back
/// to the unelevated path: an operation that quietly did less than was asked for is worse than one that
/// said it could not be done.
/// </para>
/// </summary>
public sealed class ElevatedRegistryTool : ITool, IConditionalTool
{
	private readonly ElevatedServiceClient _client;
	private readonly ServiceAvailability _availability;
	private readonly JarvisSettingsStore _settings;
	private readonly ILogger _logger;

	public ElevatedRegistryTool(
		ElevatedServiceClient client,
		ServiceAvailability availability,
		JarvisSettingsStore settings,
		ILogger logger)
	{
		_client = client;
		_availability = availability;
		_settings = settings;
		_logger = logger.ForContext<ElevatedRegistryTool>();
	}

	/// <summary>
	/// Three separate things have to be true before this tool is worth offering, and they are reported
	/// separately because the fix for each is different.
	/// <para>
	/// Offering it while any of them is false produced a tool the model called confidently and that silently
	/// did nothing, which is the failure mode this replaces.
	/// </para>
	/// </summary>
	public (bool Available, string UnavailableReason) DescribeAvailability()
	{
		var settings = _settings.Current;

		if (!settings.ElevatedServiceEnabled)
		{
			return (false, "The elevated service is switched off in JARVIS's settings, so machine-wide "
				+ "registry changes are not available. Ask the user to turn it on in the configuration.");
		}

		if (!settings.ElevatedServiceAdminOperations)
		{
			return (false, "Machine-wide registry changes are switched off in JARVIS's settings.");
		}

		if (!_availability.IsAvailable)
		{
			return (false, "The JARVIS elevated service is not answering, so machine-wide registry changes "
				+ "are not available. Ask the user to install and start it.");
		}

		return (true, string.Empty);
	}

	public string Name => "registry_elevated";

	public bool RequiresConfirmation => true;

	public ToolDefinition Definition => new()
	{
		Name = Name,
		Description = "Reads or changes a registry value under HKLM, which needs administrator rights and is "
			+ "done by the JARVIS service. Only HKLM\\SOFTWARE\\Jarvis and keys below it can be reached; any "
			+ "other key is refused by the service. Use registry_get and registry_set for your own settings "
			+ "under HKCU.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["operation"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "get, set or delete.",
					["enum"] = new JsonArray("get", "set", "delete"),
				},
				["path"] = new JsonObject { ["type"] = "string", ["description"] = "Key path below HKLM." },
				["name"] = new JsonObject { ["type"] = "string", ["description"] = "Value name." },
				["kind"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "string, expandstring, multistring, binary, dword or qword, for a set.",
					["enum"] = new JsonArray("string", "expandstring", "multistring", "binary", "dword", "qword"),
				},
				["value"] = new JsonObject { ["description"] = "The value to write, for a set." },
				["deleteKey"] = new JsonObject
				{
					["type"] = "boolean",
					["description"] = "Remove the key rather than one value, for a delete.",
				},
			},
			["required"] = new JsonArray("operation", "path"),
		},
	};

	public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
	{
		var operation = arguments["operation"]?.GetValue<string>()?.Trim().ToLowerInvariant();
		var path = arguments["path"]?.GetValue<string>();

		if (string.IsNullOrWhiteSpace(operation) || string.IsNullOrWhiteSpace(path))
		{
			return ToolOutcome.Failure("An operation and a key path are both needed.");
		}

		if (operation is not ("get" or "set" or "delete"))
		{
			return ToolOutcome.Failure("The operation must be get, set or delete.");
		}

		var serviceOperation = operation switch
		{
			"get" => "registry_get",
			"set" => "registry_set",
			_ => "registry_delete",
		};

		var forwarded = new JsonObject
		{
			["hive"] = "HKLM",
			["path"] = path,
		};

		foreach (var key in new[] { "name", "kind", "value", "deleteKey" })
		{
			if (arguments[key] is { } node)
			{
				// Deep-copied, because the same node cannot be attached to a second parent.
				forwarded[key] = node.DeepClone();
			}
		}

		var reply = await _client.SendAsync(
			ServiceProtocol.Request(serviceOperation, forwarded),
			cancellationToken).ConfigureAwait(false);

		var (ok, content) = ServiceProtocol.Read(reply);

		if (!ok)
		{
			_logger.Debug("The elevated registry {Operation} was refused: {Content}", serviceOperation, content);
			return ToolOutcome.Failure(content);
		}

		return ToolOutcome.Success(content);
	}
}
