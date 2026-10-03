using System.Globalization;

namespace Jarvis.Service;

/// <summary>
/// The service's own logging, deliberately tiny.
/// <para>
/// Not Serilog and not the hosting package's logging: the service is a separate process with no log
/// viewer, so its whole logging requirement is a line to the console that the service control manager
/// captures into the system event log. A logger abstraction here would be a dependency with no second
/// implementation.
/// </para>
/// <para>
/// Writes are serialised because a service handles requests from a pipe and from a tray click at the same
/// time, and two threads writing to one console handle interleave mid-line.
/// </para>
/// </summary>
public static class ServiceLog
{
	private static readonly Lock Gate = new();

	private static bool _consoleOnly = true;

	/// <summary>When true, nothing is written to the system event log.</summary>
	public static bool ConsoleOnly
	{
		get => _consoleOnly;
		set
		{
			lock (Gate)
			{
				_consoleOnly = value;
			}
		}
	}

	/// <summary>The timestamp prefix, exposed so a caller can build one line without logging twice.</summary>
	public static string Stamp() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

	public static void Information(string message) => Write("INF", message);

	public static void Warning(string message) => Write("WRN", message);

	public static void Error(string message, Exception? exception = null) =>
		Write("ERR", exception is null ? message : $"{message} ({exception.Message})");

	public static void Fatal(string message) => Write("FTL", message);

	private static void Write(string level, string message)
	{
		lock (Gate)
		{
			var line = string.Create(
				CultureInfo.InvariantCulture,
				$"[{DateTime.Now:HH:mm:ss}] {level} {message}");

			Console.Out.WriteLine(line);

			if (!_consoleOnly)
			{
				EventLog.Write(level, message);
			}
		}
	}
}

/// <summary>
/// Writes to the Windows event log, which is where a service's output belongs once it is running as a
/// service rather than in a console.
/// <para>
/// Every call is guarded. A service whose only diagnostic is the event log must not fail because the event
/// log is unavailable, which it is for an unprivileged process.
/// </para>
/// </summary>
public static class EventLog
{
	private const string Source = "JarvisService";
	private const string Log = "Application";

	public static void Write(string level, string message)
	{
		try
		{
			var kind = level switch
			{
				"ERR" or "FTL" => System.Diagnostics.EventLogEntryType.Error,
				"WRN" => System.Diagnostics.EventLogEntryType.Warning,
				_ => System.Diagnostics.EventLogEntryType.Information,
			};

			using var log = new System.Diagnostics.EventLog(Log) { Source = Source };
			log.WriteEntry(message, kind);
		}
		catch (Exception exception) when (
			exception is System.Security.SecurityException
				or System.ComponentModel.Win32Exception
				or InvalidOperationException
				or UnauthorizedAccessException)
		{
			// Nothing to do. The console line already happened, and failing here would turn a diagnostic
			// into a fault.
		}
	}
}
