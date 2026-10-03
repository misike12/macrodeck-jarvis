using System.ServiceProcess;

namespace Jarvis.Service;

/// <summary>
/// The service host.
/// <para>
/// Built on <see cref="ServiceBase"/> rather than on hand-written calls to the service control manager.
/// The dispatcher handshake is genuinely fiddly: the table has to be terminated inside its own buffer, the
/// entry points have to be bound as Unicode to match the name in that table, and a mistake in any of it
/// fails as a fast-fail crash inside the operating system with nothing in the event log to say why. That is
/// exactly what went wrong, three times over. The framework already does all of it.
/// </para>
/// </summary>
internal sealed class JarvisServiceHost : ServiceBase
{
	private readonly ILogger _log;
	private PipeServer? _pipe;

	public JarvisServiceHost(ILogger log)
	{
		_log = log;
		ServiceName = ServiceNames.Name;
	}

	protected override void OnStart(string[] args)
	{
		_log.Information($"{ServiceNames.DisplayName} is starting.");

		try
		{
			// Elevated, so the pipe is named after the signed-in user and carries a descriptor granting that
			// user access. Without this the pipe would belong to LocalSystem and the plugin could not open it.
			_pipe = new PipeServer(_log, elevated: true);
			_pipe.Start();

			_log.Information($"{ServiceNames.DisplayName} is listening on {Protocol.PipeName}.");
		}
		catch (Exception exception)
		{
			// Left to escape, the only record is the service control manager saying the dispatch failed, which
			// names no cause. The reason is written here first so the next start says what actually went wrong.
			ServiceLog.Error($"{ServiceNames.DisplayName} could not start: {exception}");

			_pipe?.Dispose();
			_pipe = null;

			throw;
		}
	}

	protected override void OnStop()
	{
		_log.Information($"{ServiceNames.DisplayName} was asked to stop.");

		_pipe?.Dispose();
		_pipe = null;
	}

	/// <summary>
	/// A stop or shutdown that arrives while the service is starting is delivered before the pipe exists.
	/// Nothing to do beyond letting it happen.
	/// </summary>
	protected override void OnShutdown()
	{
		_pipe?.Dispose();
		_pipe = null;
	}
}

/// <summary>The registered name and the display name, kept in one place because they must agree.</summary>
internal static class ServiceNames
{
	public const string Name = "JarvisService";

	public const string DisplayName = "JARVIS Service";
}