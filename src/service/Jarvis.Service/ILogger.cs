namespace Jarvis.Service;

/// <summary>
/// The service's logging contract. One method per level, and an optional exception, because that is
/// everything the service needs to say anything.
/// </summary>
public interface ILogger
{
	void Information(string message);

	void Warning(string message);

	void LogError(string message, Exception? exception = null);

	void Fatal(string message);
}

/// <summary>
/// The console logger, used by the tray mode and by the install and uninstall commands where there is a
/// console to write to.
/// </summary>
public sealed class ConsoleLogger : ILogger
{
	public void Information(string message) => ServiceLog.Information(message);

	public void Warning(string message) => ServiceLog.Warning(message);

	public void LogError(string message, Exception? exception = null) => ServiceLog.Error(message, exception);

	public void Fatal(string message) => ServiceLog.Fatal(message);
}

/// <summary>
/// The logger a service uses: the console line the service control manager captures, plus the event log so
/// the lines are still there a week later.
/// </summary>
public sealed class ServiceEventLogger : ILogger
{
	public void Information(string message) => ServiceLog.Information(message);

	public void Warning(string message) => ServiceLog.Warning(message);

	public void LogError(string message, Exception? exception = null) => ServiceLog.Error(message, exception);

	public void Fatal(string message) => ServiceLog.Fatal(message);
}

/// <summary>
/// A logger that discards everything, used by the tests so a test does not fill the console with lines about
/// refused requests.
/// </summary>
public sealed class NullLogger : ILogger
{
	public static NullLogger Instance { get; } = new();

	public void Information(string message)
	{
	}

	public void Warning(string message)
	{
	}

	public void LogError(string message, Exception? exception = null)
	{
	}

	public void Fatal(string message)
	{
	}
}
