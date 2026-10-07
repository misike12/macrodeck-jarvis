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

	/// <summary>One pipe instance at a time. See the listener for why.</summary>
	private const int MaxAllowedServerInstances = 1;

	/// <summary>How long a connected client may stay silent before the connection is dropped.</summary>
	private static TimeSpan IdleTimeout => TimeSpan.FromSeconds(30);

	private readonly ILogger _logger;
	private readonly CancellationTokenSource _stopping = new();
	private readonly string _pipeName;
	private readonly PipeSecurity? _security;

	private Task? _listener;
	private NamedPipeServerStream? _current;
	private bool _disposed;

	/// <summary>
	/// The rules are the same in both modes: the pipe name no longer names a user, and the account it is
	/// granted to comes from the install in service mode and from this process's own token in tray mode.
	/// <para>
	/// Throws when the descriptor cannot be built. Failing to construct the pipe is a failure to start, not
	/// a reason to serve it with the operating system's default descriptor, which belongs to the LocalSystem
	/// token and is not a set anyone chose.
	/// </para>
	/// </summary>
	public PipeServer(ILogger logger, string? pipeName = null)
	{
		_logger = logger;
		_pipeName = pipeName ?? Protocol.PipeName;
		_security = BuildSecurity()
			?? throw new InvalidOperationException(
				"The pipe's access rules could not be established, so the service will not open a pipe it "
					+ "cannot restrict.");

// Created here rather than inside the listener task so that a name already taken, or a directory
		// that cannot be created, is reported before the service claims to be running.
		_current = CreateServer();
	}

	/// <summary>
	/// Whether the plugin may ask for anything that changes machine state.
	/// <para>
	/// Defaults to true and is only turned off deliberately from the tray menu. <c>ping</c> and
	/// <c>status</c> stay available either way, so a caller can still find out that the switch exists rather
	/// than concluding the service has gone.
	/// </para>
	/// </summary>
	public bool AllowAdminOperations { get; set; } = true;

	/// <summary>Whether the service is accepting connections right now.</summary>
	public bool IsListening => !_disposed && _listener is { IsCompleted: false };

	/// <summary>
	/// Starts accepting.
	/// <para>
	/// The first instance is built in the constructor, so by the time this returns there is a pipe in the
	/// namespace and a client gets a refusal rather than "file not found". Waiting for a client is the
	/// listener's job and happens on its own task.
	/// </para>
	/// </summary>
	public void Start()
	{
		if (_disposed || _listener is not null)
		{
			return;
		}

		var first = _current!;

		_current = null;

		_listener = Task.Run(() => ListenAsync(first, _stopping.Token), CancellationToken.None);
	}

private NamedPipeServerStream CreateServer() =>
		NamedPipeServerStreamAcl.Create(
			_pipeName,
			PipeDirection.InOut,
			MaxAllowedServerInstances,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous,
			0,
			0,
			_security);

/// <summary>
	/// Builds the pipe's security.
	/// <para>
	/// Rules are added one at a time rather than parsed from a descriptor string, because the set of
	/// processes that can open this pipe is the trust boundary to a service running as LocalSystem. Written
	/// out, the boundary is visible: LocalSystem and administrators have full control, and exactly one named
	/// user account has read and write.
	/// </para>
	/// <para>
	/// That account is recorded at install time. It is deliberately not the interactive group: S-1-5-4 is in
	/// the token of every process in every signed-in session, so granting it would let any other user on a
	/// shared machine send this service a registry write and have it applied as SYSTEM.
	/// </para>
	/// <para>
	/// Returns null when the descriptor cannot be built, which is a failure to start rather than a fallback
	/// to the operating system's default: that default belongs to the LocalSystem token and is not a set
	/// anyone chose.
	/// </para>
	/// </summary>
	private static PipeSecurity? BuildSecurity()
	{
		string? granted;

		try
		{
			granted = ReadGrantedUserSid();
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			ServiceLog.Error($"The account allowed to open the pipe could not be read: {exception.Message}");
			return null;
		}

		if (granted is null)
		{
			ServiceLog.Error(
				$"No installing account was recorded, so there is nobody to grant the pipe to. "
					+ $"Expected it at {ServiceInstaller.UserSidPath}. Re-run the install script.");

			return null;
		}

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

			security.AddAccessRule(new PipeAccessRule(
				new SecurityIdentifier(granted),
				PipeAccessRights.ReadWrite,
				AccessControlType.Allow));

			return security;
		}
		catch (ArgumentException exception)
		{
			ServiceLog.Error($"The pipe security could not be built: {exception.Message}");
			return null;
		}
	}

	/// <summary>
	/// The account recorded by the installer, or null when there is none.
	/// <para>
	/// In tray mode there is nothing to read: the process is already running as the user who owns the pipe,
	/// so it is granted directly from its own token and no file is involved.
	/// </para>
	/// </summary>
	private static string? ReadGrantedUserSid()
	{
		using var identity = WindowsIdentity.GetCurrent();

		if (!identity.IsSystem)
		{
			return identity.User?.Value;
		}

		if (!File.Exists(ServiceInstaller.UserSidPath))
		{
			return null;
		}

		var sid = File.ReadAllText(ServiceInstaller.UserSidPath).Trim();

		return string.IsNullOrWhiteSpace(sid) ? null : sid;
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
	public async Task Stop()
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

		// Awaited with a bound rather than dropped. Dropping it leaves the listener holding a pipe instance
		// on this name, and a reconnect starts a second one; the next client to arrive could then be answered
		// by a listener nobody is reading. The bound is what keeps teardown from blocking on a thread-pool
		// thread, which the tests exercise repeatedly enough to matter.
		var listener = _listener;

		_listener = null;

		if (listener is not null)
		{
			try
			{
				await listener.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				_logger.Warning("The pipe listener did not stop within two seconds; leaving it to finish.");
			}
			catch (OperationCanceledException)
			{
				// Stopped as asked.
			}
		}
	}

	/// <summary>
	/// Releases the cancellation source. The pipe is closed by <see cref="Stop"/> first, and the listener
	/// is awaited there, so by the time this runs there is nothing left to wait for.
	/// </summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		// Synchronous by design: Dispose cannot await, and Stop already bounds its own wait at two seconds,
		// so blocking here cannot hang a shutdown.
		try
		{
			Stop().GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
			// Stopped as asked.
		}

		_disposed = true;
		_stopping.Dispose();
	}

	private async Task ListenAsync(NamedPipeServerStream first, CancellationToken cancellationToken)
	{
		var server = first;

		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
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
				// A failed accept must not end the service, but it is bounded: an endless retry loop on a
				// name that cannot be created leaves the service Running with no pipe and the same message
				// in the log forever, which is indistinguishable from working.
				_logger.LogError("A connection could not be served.", exception);

				try
				{
					await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					return;
				}
			}
			finally
			{
				if (ReferenceEquals(_current, server))
				{
					_current = null;
				}

				server.Dispose();
				server = null!;
			}

			// Replaced only on the way forward. Creating the next instance inside the loop body rather than
			// the finally block is what stops a shutting-down listener from leaving a fresh pipe behind: the
			// service allows one instance per name, so an instance created on the way out blocked the next
			// start with an access-denied that looked like a permissions fault.
			if (cancellationToken.IsCancellationRequested)
			{
				return;
			}

			server = CreateServer();
		}
	}

	/// <summary>
	/// Reads requests until the client disconnects. Each is answered on the same connection, so the plugin
	/// can hold one pipe open for the life of its session rather than reconnecting per call.
	/// </summary>
private async Task ServeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
	{
		// An idle deadline per connection. Without one, a client that connects and then says nothing holds the
		// only pipe instance for as long as it likes, and the plugin's own calls queue behind it until their
		// timeouts. Reset on every request, so a busy client is never interrupted.
		using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		idle.CancelAfter(IdleTimeout);

		while (!idle.Token.IsCancellationRequested && server.IsConnected)
		{
			string? request;

			try
			{
				request = await Protocol.ReadFrameAsync(server, idle.Token).ConfigureAwait(false);
			}
			catch (InvalidDataException exception)
			{
				// Nothing was read, so the body the peer is still sending has no framing left to
				// resynchronise against. The caller is told why, and the connection ends.
				_logger.Warning(exception.Message);

				await TryReplyAsync(server, Protocol.Reply(false, exception.Message), cancellationToken)
					.ConfigureAwait(false);

				return;
			}
			catch (IOException exception)
			{
				_logger.LogError("A request could not be read.", exception);
				return;
			}
			catch (OperationCanceledException)
			{
				_logger.Warning(
					$"A connected client sent nothing for {IdleTimeout.TotalSeconds:0} seconds, so the connection was closed.");

				return;
			}

			if (request is null)
			{
				return;
			}

var reply = Handle(request);

			if (!await TryReplyAsync(server, reply, cancellationToken).ConfigureAwait(false))
			{
				return;
			}

			// No special case for shutdown here. The reply is written first and the stop is requested from
			// inside Handle, so the plugin is answered before the service goes; the loop's next read then
			// fails because the connection is going away, which is the correct outcome.
		}
	}

	/// <summary>
	/// Writes one reply, reporting a failed write as false rather than throwing. A client that has gone away
	/// mid-answer is ordinary, and it should end the connection rather than surface as a fault in the service.
	/// </summary>
	private async Task<bool> TryReplyAsync(
		NamedPipeServerStream server,
		JsonObject reply,
		CancellationToken cancellationToken)
	{
		try
		{
			await Protocol.WriteFrameAsync(server, reply.ToJsonString(), cancellationToken).ConfigureAwait(false);
			return true;
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException)
		{
			_logger.LogError("A reply could not be written.", exception);
			return false;
		}
	}

	/// <summary>
	/// Answers one request. Anything unrecognised is refused rather than guessed at: this is the most
	/// privileged process the assistant can reach, and an ambiguous instruction is not something to act on.
	/// </summary>
	public JsonObject Handle(string line)
	{
		// Declared out here so the catch can name the operation in its log line. Null until it is read.
		string? operation = null;

		// The whole shape is read inside the guard. Reading it outside meant a message with, say, a string
		// where the version number belongs threw before the try, escaped to the listener's catch, and was
		// reported as a failed accept: one packet closed the connection and logged a misleading reason.
		try
		{
			var request = Protocol.ParseRequest(line);

			if (request is null)
			{
				_logger.Warning($"A request was refused because it was not a version {Protocol.Version} message.");

				return Protocol.Reply(
					false,
					"The request was not understood. The plugin and the service may be different versions.");
			}

			operation = request["op"] is JsonValue opValue && opValue.TryGetValue<string>(out var named)
				? named
				: null;

			if (operation is null)
			{
				return Protocol.Reply(false, "The request did not name an operation.");
			}

var arguments = request["args"] as JsonObject ?? new JsonObject();

			// The user's switch, honoured before anything else runs. It existed on the menu and did nothing:
			// un-ticking it left every registry operation working, which is the one thing a switch labelled
			// with a security claim must never do.
			if (!AllowAdminOperations && operation is not ("ping" or "status"))
			{
				return Protocol.Reply(false,
					"Administrator operations are switched off, so nothing was changed. Turn them back on in the "
						+ "JARVIS tray icon to allow this.");
			}

			return operation switch
			{
				"ping" => Protocol.Reply(true, "pong"),
				"status" => Protocol.Reply(true, ElevatedOperations.Status()),
				"registry_get" => ElevatedOperations.RegistryGet(arguments),
				"registry_set" => ElevatedOperations.RegistrySet(arguments),
				"registry_delete" => ElevatedOperations.RegistryDelete(arguments),

				// Actually stops the service, through the host that owns it. The reply is sent first, and
				// the stop is requested after it, so the plugin learns the answer rather than losing the
				// connection to a service that vanished mid-exchange.
				"shutdown" => StopRequested(),
				_ => Refuse(operation),
			};
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			// Logged in full, returned in summary. The detail is a registry key path, an NTSTATUS string or an
			// internal name, none of which belongs in a reply that the plugin hands to a language model.
			_logger.LogError($"The operation {operation} failed.", exception);

			return Protocol.Reply(false, $"{operation} did not succeed. See the service log for the reason.");
		}
	}

	/// <summary>
	/// An operation this build does not do, named in the answer rather than left to a default, so a newer
	/// plugin learns the service is older instead of retrying.
	/// </summary>
	private static JsonObject Refuse(string operation) =>
		Protocol.Reply(false, $"'{operation}' is not something this service does.");

	/// <summary>
	/// Asks the service host to stop, once the reply has been written.
	/// <para>
	/// The callback is registered by the host rather than reached through a static, so the pipe has no
	/// opinion about how the service is controlled and can be exercised in a test without one.
	/// </para>
	/// </summary>
	private JsonObject StopRequested()
	{
		StopRequestedCallback?.Invoke();

		return Protocol.Reply(true, "Stopping.");
	}

	/// <summary>
	/// Set by the host so a shutdown request reaches the service control manager.
	/// <para>
	/// Named apart from <see cref="Stop"/> because that method stops the listener; this one asks the
	/// service to stop, and conflating them made a shutdown request that did nothing look like one that had.
	/// </para>
	/// </summary>
	internal Action? StopRequestedCallback { get; set; }
}