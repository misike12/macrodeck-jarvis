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
	/// first line. On a duplex pipe it deadlocks against the other end's own first write, and where it does
	/// not it arrives as three bytes in front of the JSON, which the service refuses as unreadable. Every
	/// request would fail, and the only symptom would be a service that seemed not to be there.
	/// </para>
	/// </summary>
	private static readonly Encoding Wire = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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

			using var writer = new StreamWriter(pipe, Wire, 1024, leaveOpen: true)
			{
				AutoFlush = true,
			};

			using var reader = new StreamReader(pipe, Wire, detectEncodingFromByteOrderMarks: false, 1024, leaveOpen: true);

			await writer.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);

			var line = await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);

			if (line is null)
			{
				return null;
			}

			return ServiceProtocol.TryParse(line, out var reply) ? reply : null;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// The deadline, rather than the caller's own cancellation.
			return null;
		}
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
	/// The pipe name, derived from this user's security identifier.
	/// <para>
	/// It has to match the service's, and the service runs as LocalSystem, so it cannot name the pipe after
	/// its own user. The identifier is the one thing both processes can agree on: the plugin's own, and the
	/// service's idea of who is signed in at the console.
	/// </para>
	/// </summary>
	public static string ServicePipeName =>
		ServiceProtocol.PipeNameForSid(
			System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value);
}
