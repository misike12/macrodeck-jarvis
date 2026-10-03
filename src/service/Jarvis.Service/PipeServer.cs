using System.IO.Pipes;
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
/// The pipe is created with <see cref="PipeOptions.CurrentUserOnly"/>. A machine-wide named pipe is
/// reachable by every process on the machine, and without this any of them could ask the service to write
/// under HKLM. The kernel enforces the user restriction, so it does not depend on the plugin behaving.
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

	private Task? _listener;
	private NamedPipeServerStream? _current;
	private bool _disposed;

	public PipeServer(ILogger logger, string? pipeName = null)
	{
		_logger = logger;
		_pipeName = pipeName ?? Protocol.PipeName;
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
	/// Stops accepting and closes the current connection. The listener task is not awaited: a client that
	/// has gone away mid-request must not be able to hold up a shutdown.
	/// <para>
	/// Safe to call more than once. A cleanup path and an explicit teardown both reaching this is normal,
	/// and a second call throwing on a disposed cancellation source would turn a successful run into a
	/// failure at the end of it.
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
		// teardown in the tests as well as from shutdown, so enough of these starve the pool and the tests
		// that depend on it deadlock instead of failing. Cancelling the token is enough: the listener
		// observes it and returns on its own, and it owns nothing that needs collecting here.
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
				using var server = new NamedPipeServerStream(
					_pipeName,
					PipeDirection.InOut,
					NamedPipeServerStream.MaxAllowedServerInstances,
					PipeTransmissionMode.Byte,
					PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

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
				// A failed accept must not end the service. The pipe is recreated and the next attempt made.
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
		// The writer is built first. Order matters only in that it keeps the two wrappers from being
		// constructed around each other while a message is in flight, but it is fixed so the sequence is
		// obvious rather than incidental.
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
