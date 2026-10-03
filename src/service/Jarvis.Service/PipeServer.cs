using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace Jarvis.Service;

/// <summary>
/// Listens on a named pipe for the plugin and answers it.
/// <para>
/// One connection at a time, because there is only ever one plugin. Serving them in parallel would mean two
/// requests racing to write the same registry key, and a pipe with a message limit is the natural place to
/// put that backpressure.
/// </para>
/// <para>
/// The pipe carries an explicit security descriptor. It cannot use
/// <see cref="PipeOptions.CurrentUserOnly"/>, because in the elevated service this process is LocalSystem
/// and that flag would restrict the pipe to LocalSystem, locking the plugin out by construction.
/// </para>
/// </summary>
public sealed class PipeServer : IDisposable
{
	/// <summary>
	/// UTF-8 with no byte order mark.
	/// <para>
	/// This is not a stylistic choice. <see cref="Encoding.UTF8"/> carries a preamble, and a preamble is
	/// written before the first message on a duplex pipe, where it deadlocks against whatever the other end
	/// is doing at that moment. Even where it did not deadlock, it would prefix the first request with three
	/// bytes that are not JSON, and the service would refuse every one of them as unreadable.
	/// </para>
	/// </summary>
	private static readonly Encoding Wire = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	private readonly ILogger _logger;
	private readonly CancellationTokenSource _stopping = new();
	private readonly string _pipeName;
	private readonly PipeSecurity? _security;

	private Task? _listener;
	private NamedPipeServerStream? _current;
	private bool _disposed;

	/// <summary>
	/// The rules are the same in both modes now: the pipe name no longer names a user, so there is nothing for
	/// the elevated and unelevated cases to decide differently.
	/// </summary>
	public PipeServer(ILogger logger, string? pipeName = null)
	{
		_logger = logger;
		_pipeName = pipeName ?? Protocol.PipeName;
		_security = BuildSecurity();
	}

	/// <summary>Whether the service is accepting connections right now.</summary>
	public bool IsListening => !_disposed && _listener is { IsCompleted: false };

	public void Start()
	{
		if (_disposed)
		{
			return;
		}

		_listener ??= Task.Run(() => ListenAsync(_stopping.Token));
	}

	/// <summary>
	/// Builds the pipe's security.
	/// <para>
	/// Rules are added one at a time rather than parsed from a descriptor string, because the set of
	/// processes that can open this pipe is the trust boundary to a service running as LocalSystem. Written
	/// out, the boundary is visible: LocalSystem and administrators have full control, the interactive group
	/// and the creating user have read and write, and nothing else.
	/// </para>
	/// <para>
	/// Returns null when it cannot be built, which leaves the pipe on the operating system's default
	/// rather than leaving the service with no pipe at all.
	/// </para>
	/// </summary>
	private static PipeSecurity? BuildSecurity()
	{
		try
		{
			var security = new PipeSecurity();

			// Protection is set and inheritance dropped, so the rules below are the whole list. Without this
			// the machine default would be merged in and could be wider than intended.
			security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

			security.AddAccessRule(new PipeAccessRule(
				new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
				PipeAccessRights.FullControl,
				AccessControlType.Allow));

			security.AddAccessRule(new PipeAccessRule(
				new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
				PipeAccessRights.FullControl,
				AccessControlType.Allow));

			// The interactive group, so the person at the keyboard can reach the service without the service having
			// to work out who they are. This is what replaced a rule naming one user.
			security.AddAccessRule(new PipeAccessRule(
				new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
				PipeAccessRights.ReadWrite,
				AccessControlType.Allow));

			using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();

			if (identity.User is { } user && !identity.IsSystem)
			{
				security.AddAccessRule(new PipeAccessRule(
					user,
					PipeAccessRights.ReadWrite,
					AccessControlType.Allow));
			}

			return security;
		}
		catch (ArgumentException exception)
		{
			ServiceLog.Error("The pipe security could not be built: " + exception.Message);
			return null;
		}
	}

	/// <summary>
	/// Stops accepting and closes the current connection. The listener task is not awaited: a client that
	/// has gone away mid-request must not be able to hold up a shutdown.
	/// <para>
	/// Safe to call more than once. A cleanup path and an explicit teardown both reaching this is normal, and
	/// a second call throwing on a disposed cancellation source would turn a successful run into a failure at
	/// the end of it.
	/// </para>
	/// </summary>
	public void Stop()
	{
		if (_disposed)
		{
			return;
		}

		try
		{
			_stopping.Cancel();
		}
		catch (ObjectDisposedException)
		{
			return;
		}

		try
		{
			_current?.Dispose();
		}
		catch (ObjectDisposedException)
		{
			// Already closed.
		}

		// The listener is not waited for. Waiting would block a thread-pool thread, and this is called from
		// teardown in the tests as well as from shutdown, so enough of them starve the pool and the tests
		// that depend on it deadlock instead of failing. Cancelling the token is enough.
		_listener = null;
	}

	/// <summary>
	/// Releases the cancellation source. The pipe is closed by <see cref="Stop"/> first, so by the time
	/// this runs there is nothing left to wait for.
	/// </summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		Stop();
		_disposed = true;
		_stopping.Dispose();
	}

	private async Task ListenAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				// Created fresh each time, because a NamedPipeServerStream owns its security descriptor and
				// cannot be reconfigured once it exists. NamedPipeServerStreamAcl is the security-aware
				// factory; the plain constructor has no way to express one.
				using var server = _security is null
					? new NamedPipeServerStream(
						_pipeName,
						PipeDirection.InOut,
						NamedPipeServerStream.MaxAllowedServerInstances,
						PipeTransmissionMode.Byte,
						PipeOptions.Asynchronous)
					: NamedPipeServerStreamAcl.Create(
						_pipeName,
						PipeDirection.InOut,
						NamedPipeServerStream.MaxAllowedServerInstances,
						PipeTransmissionMode.Byte,
						PipeOptions.Asynchronous,
						0,
						0,
						_security);

				_current = server;

				await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

				await ServeAsync(server, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				// A failed accept must not end the service. The pipe is recreated and the next attempt made,
				// because a failed accept says nothing about the next one.
				_logger.LogError("A connection could not be served.", exception);
				await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				_current = null;
			}
		}
	}

	/// <summary>
	/// Reads requests until the client disconnects. Each is answered on the same connection, so the plugin
	/// can hold one pipe open for the life of its session rather than reconnecting per call.
	/// </summary>
	private async Task ServeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
	{
		// The writer is built first, so the two wrappers are not constructed around each other while a
		// message is in flight. Fixed rather than incidental so the sequence is obvious.
		await using var writer = new StreamWriter(server, Wire, 1024, leaveOpen: true)
		{
			AutoFlush = true,
		};

		using var reader = new StreamReader(server, Wire, detectEncodingFromByteOrderMarks: false, 1024, leaveOpen: true);

		while (!cancellationToken.IsCancellationRequested && server.IsConnected)
		{
			string? line;

			try
			{
				line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (IOException exception)
			{
				_logger.LogError("A request could not be read.", exception);
				return;
			}

			if (line is null)
			{
				return;
			}

			if (line.Length == 0)
			{
				continue;
			}

			var reply = Handle(line);

			try
			{
				await writer.WriteLineAsync(reply.ToJsonString()).ConfigureAwait(false);
			}
			catch (IOException exception)
			{
				_logger.LogError("A reply could not be written.", exception);
				return;
			}

			if (Protocol.ParseRequest(line)?["op"]?.GetValue<string>() == "shutdown")
			{
				return;
			}
		}
	}

	/// <summary>
	/// Answers one request. Anything unrecognised is refused rather than guessed at: this is the most
	/// privileged process the assistant can reach, and an ambiguous instruction is not something to act on.
	/// </summary>
	public JsonObject Handle(string line)
	{
		var request = Protocol.ParseRequest(line);

		if (request is null)
		{
			_logger.Warning($"A request was refused because it was not a version {Protocol.Version} message.");

			return Protocol.Reply(
				false,
				"The request was not understood. The plugin and the service may be different versions.");
		}

		var operation = request["op"]!.GetValue<string>();
		var arguments = request["args"] as JsonObject ?? new JsonObject();

		try
		{
			return operation switch
			{
				"ping" => Protocol.Reply(true, "pong"),
				"status" => Protocol.Reply(true, ElevatedOperations.Status()),
				"registry_get" => ElevatedOperations.RegistryGet(arguments),
				"registry_set" => ElevatedOperations.RegistrySet(arguments),
				"registry_delete" => ElevatedOperations.RegistryDelete(arguments),
				"shutdown" => Protocol.Reply(true, "Stopping."),

				// An operation this build does not do is named in the answer rather than left to a default,
				// so a newer plugin learns the service is older instead of retrying.
				_ => Protocol.Reply(false, $"'{operation}' is not something this service does."),
			};
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.LogError($"The operation {operation} failed.", exception);
			return Protocol.Reply(false, $"{operation} failed: {exception.Message}");
		}
	}
}