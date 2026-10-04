using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Jarvis.Plugin.Core;

/// <summary>
/// The plugin's side of the elevated service.
/// <para>
/// The plugin runs as the user, which is the whole reason the service exists: an unelevated process cannot
/// write under HKLM or register a scheduled task without a prompt per operation. This type sends a request
/// over the pipe and waits for the answer.
/// </para>
/// <para>
/// Every call is given a deadline. A pipe to a service that is not running, or one that has hung, would
/// otherwise leave a tool call waiting forever, and a model waiting on a tool call waits on the turn.
/// </para>
/// </summary>
public sealed class ElevatedServiceClient(TimeSpan timeout, string? pipeName = null)
{
	/// <summary>
	/// Generous enough for a registry write that touches a slow hive, short enough that a hung service is
	/// reported rather than waited out.
	/// </summary>
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

	private readonly TimeSpan _timeout = timeout;

	private readonly string _pipeName = pipeName ?? ServicePipeName;

/// <summary>
	/// UTF-8 with no byte order mark, matching the service.
	/// <para>
	/// <see cref="System.Text.Encoding.UTF8"/> carries a preamble, and that preamble is written before the
	/// first frame. On a duplex pipe it deadlocks against the other end's own first write, and where it does
	/// not it arrives as three bytes in front of the length prefix, which the service refuses as unreadable.
	/// Every request would fail, and the only symptom would be a service that seemed not to be there.
	/// </para>
	/// </summary>
	private static readonly Encoding Wire = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	private const int MaximumMessageBytes = 64 * 1024;

	/// <summary>
	/// Sends a request and returns the reply, or null when the service could not be reached or did not
	/// answer in time. Null rather than an exception, because "the service is not available" is an ordinary
	/// state the caller has to fall back from, not a fault.
	/// </summary>
	public async Task<JsonObject?> SendAsync(JsonObject request, CancellationToken cancellationToken)
	{
		using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(_timeout);

		try
		{
			await using var pipe = new NamedPipeClientStream(
				".",
				_pipeName,
				PipeDirection.InOut,
				PipeOptions.Asynchronous);

			await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);

			await WriteFrameAsync(pipe, request.ToJsonString(), timeoutSource.Token).ConfigureAwait(false);

			var payload = await ReadFrameAsync(pipe, timeoutSource.Token).ConfigureAwait(false);

			if (payload is null)
			{
				return null;
			}

			return ServiceProtocol.TryParse(payload, out var reply) ? reply : null;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// The deadline, rather than the caller's own cancellation.
			return null;
		}
		catch (InvalidDataException)
		{
			// A reply the service should never have sent. Treated as no answer rather than a fault, so a
			// protocol disagreement reads as "service unavailable" instead of breaking the caller's turn.
			return null;
		}
	}

	/// <summary>
	/// Writes a length-prefixed frame. Mirrors the service's reader exactly; the two cannot share code
	/// because the plugin does not reference the service assembly.
	/// </summary>
	private static async Task WriteFrameAsync(Stream stream, string payload, CancellationToken cancellationToken)
	{
		var body = Wire.GetBytes(payload);

		if (body.Length > MaximumMessageBytes)
		{
			throw new InvalidDataException($"A request of {body.Length} bytes is over the limit.");
		}

		var prefix = new byte[4];

		BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)body.Length);

		await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
		await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
	{
		var prefix = new byte[4];

		if (await ReadAtLeastAsync(stream, prefix, prefix.Length, cancellationToken).ConfigureAwait(false)
			< prefix.Length)
		{
			return null;
		}

		var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);

		if (length is 0 or > MaximumMessageBytes)
		{
			throw new InvalidDataException($"A reply declared {length} bytes, which is outside the limit.");
		}

		var body = new byte[length];

		if (await ReadAtLeastAsync(stream, body, body.Length, cancellationToken).ConfigureAwait(false) < length)
		{
			return null;
		}

		return Wire.GetString(body);
	}

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

	/// <summary>
	/// Whether the service is reachable, used to decide whether to offer the elevated tools at all rather
	/// than letting a user press a button that cannot work.
	/// </summary>
	public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
	{
		var reply = await SendAsync(ServiceProtocol.Request("ping"), cancellationToken).ConfigureAwait(false);

		return reply?["ok"]?.GetValue<bool>() == true;
	}

	/// <summary>
	/// The pipe name, which is fixed on both sides.
	/// <para>
	/// It was once derived from this user's security identifier so the plugin and the service would each
	/// arrive at the same name. The service cannot do that: it runs as LocalSystem and has no idea who is
	/// signed in, so the two disagreed whenever that lookup failed and the plugin silently had no service to
	/// talk to. The descriptor on the pipe is what restricts access.
	/// </para>
	/// </summary>
	public static string ServicePipeName => ServiceProtocol.PipeName;
}
