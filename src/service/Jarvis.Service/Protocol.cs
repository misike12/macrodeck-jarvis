using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
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
	/// The pipe name, fixed.
	/// <para>
	/// It was once derived from the signed-in user's security identifier, which reads well but cannot work: the
	/// service runs as LocalSystem and has to resolve that identifier itself, through session APIs that fail in
	/// ways that leave the pipe named for nobody, which the plugin can then never reach. A pipe name is
	/// world-enumerable anyway, so it was never what provided security. Access is enforced by the descriptor on
	/// the pipe, which grants the interactive group, LocalSystem and administrators.
	/// </para>
	/// </summary>
	public const string PipeName = "jarvis-service";

	/// <summary>
	/// A pipe name carrying a caller-chosen suffix.
	/// <para>
	/// Used only by tests, which need a listener on a name of its own: the service allows unlimited instances on
	/// one name, so a listener left behind by an earlier test would silently accept the next test's connection.
	/// </para>
	/// </summary>
	public static string PipeNameForSuffix(string suffix) => $"{PipeName}-{suffix}";

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

	/// <summary>
	/// The largest message either side will send or accept, in bytes of UTF-8.
	/// <para>
	/// Checked against the length prefix before the body is read. A limit applied after the message has been
	/// read is not a limit: the reader has already allocated whatever the sender declared, which in a
	/// LocalSystem process is an allocation an unprivileged peer chose.
	/// </para>
	/// </summary>
	public const int MaximumMessageBytes = 64 * 1024;

	private const int PrefixBytes = 4;

	private static readonly Encoding Wire = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	/// <summary>
	/// Writes one length-prefixed message and flushes it.
	/// </summary>
	public static async Task WriteFrameAsync(Stream stream, string payload, CancellationToken cancellationToken)
	{
		var body = Wire.GetBytes(payload);

		if (body.Length > MaximumMessageBytes)
		{
			throw new InvalidDataException(
				$"A reply of {body.Length} bytes is over the {MaximumMessageBytes} byte limit.");
		}

		var prefix = new byte[PrefixBytes];

		BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)body.Length);

		await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
		await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Reads one length-prefixed message, or null when the peer disconnected cleanly.
	/// </summary>
	/// <exception cref="InvalidDataException">
	/// The declared length is zero or over the limit. Nothing is read in that case, so the caller is left at a
	/// known position and must close rather than try to resynchronise: the body the peer is still sending has
	/// no framing to resynchronise against.
	/// </exception>
	public static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
	{
		var prefix = new byte[PrefixBytes];
		var declared = await ReadAtLeastAsync(stream, prefix, PrefixBytes, cancellationToken).ConfigureAwait(false);

		if (declared < PrefixBytes)
		{
			return null;
		}

		var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);

		if (length is 0 or > MaximumMessageBytes)
		{
			throw new InvalidDataException(
				$"A message declared {length} bytes, which is outside 1 to {MaximumMessageBytes}.");
		}

		var body = new byte[length];
		var read = await ReadAtLeastAsync(stream, body, body.Length, cancellationToken).ConfigureAwait(false);

		return read < length ? null : Wire.GetString(body);
	}

	/// <summary>
	/// Reads exactly <paramref name="count"/> bytes unless the peer stops first, in which case it reports how
	/// many arrived so a clean close at a frame boundary can be told from a truncated message.
	/// </summary>
	private static async Task<int> ReadAtLeastAsync(
		Stream stream,
		byte[] buffer,
		int count,
		CancellationToken cancellationToken)
	{
		var total = 0;

		while (total < count)
		{
			var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken)
				.ConfigureAwait(false);

			if (read == 0)
			{
				return total;
			}

			total += read;
		}

		return total;
	}

	public static bool TryParse(string line, out JsonObject message)
	{
		message = null!;

		try
		{
			// A secondary assertion, not the limit: the length prefix is what actually bounds the allocation.
			if (line.Length == 0 || line.Length > MaximumMessageBytes)
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
