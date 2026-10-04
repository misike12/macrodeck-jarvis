using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Jarvis.Service;

/// <summary>
/// The service's own logging, deliberately tiny.
/// <para>
/// Not Serilog and not the hosting package's logging: the service is a separate process with no log
/// viewer, so its whole logging requirement is a line to the console that the service control manager
/// captures into the system event log, plus a file that can be read straight after a failed start.
/// </para>
/// <para>
/// Writes are serialised because a service handles requests from a pipe and from a tray click at the same
/// time, and two threads writing to one console handle interleave mid-line.
/// </para>
/// </summary>
public static class ServiceLog
{
	/// <summary>
	/// The point at which the log is rotated. Without a bound this file is the one thing that grows
	/// forever on a machine where the service is restarted often, which is exactly what happens while it is
	/// misconfigured.
	/// </summary>
	public const int MaximumLogBytes = 1024 * 1024;

	public const string DirectoryName = "Jarvis";

	public const string LogFileName = "Jarvis.Service.log";

	private static readonly Lock Gate = new();

	/// <summary>
	/// Whether the console has ever accepted a write.
	/// <para>
	/// Null until the first attempt. A service started by the service control manager has no console, and a
	/// write to the handle it reports can fail; guessing once and remembering is cheaper than catching on
	/// every line, and one failure means there is nothing there to write to.
	/// </para>
	/// </summary>
	private static bool? _consoleWorks;

	/// <summary>The timestamp prefix, exposed so a caller can build one line without logging twice.</summary>
	public static string Stamp() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

	/// <summary>The directory the service logs into and keeps its recorded identifier in.</summary>
	public static string LogDirectory =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), DirectoryName);

	/// <summary>The log file's full path.</summary>
	public static string LogFile => Path.Combine(LogDirectory, LogFileName);

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

			TryAppendToConsole(line);

			// Also written to a file. A service has no console, and the service control manager only reports
			// that the process ended, so without this the only trace of why a start was rejected is an event
			// saying nothing happened. A file needs no registration and is readable straight away.
			TryAppendToFile(line);
		}
	}

	/// <summary>
	/// Writes to the console when there is one.
	/// <para>
	/// Every diagnostic the service produces goes through here, so an unguarded write means a service that
	/// fails to log fails to start. Under the service control manager there is no console to write to.
	/// </para>
	/// </summary>
	private static void TryAppendToConsole(string line)
	{
		if (_consoleWorks is false)
		{
			return;
		}

		try
		{
			Console.Out.WriteLine(line);
			_consoleWorks = true;
		}
		catch (Exception exception) when (
			exception is IOException
				or ObjectDisposedException
				or System.Security.SecurityException
				or PlatformNotSupportedException)
		{
			_consoleWorks = false;
		}
	}

	private static void TryAppendToFile(string line)
	{
		try
		{
			var directory = LogDirectory;

			if (!Directory.Exists(directory))
			{
				Directory.CreateDirectory(directory);
				RestrictDirectory(directory);
			}

			var file = LogFile;
			RotateIfOversized(file);

			File.AppendAllText(file, line + Environment.NewLine);
		}
		catch (Exception exception) when (
			exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
		{
			// Nothing to do. The console line already happened, and a logger that throws takes the service
			// down with it.
		}
	}

	/// <summary>
	/// Renames an oversized log so the next write starts a new one, keeping exactly one previous file.
	/// </summary>
	private static void RotateIfOversized(string file)
	{
		if (!File.Exists(file) || new FileInfo(file).Length < MaximumLogBytes)
		{
			return;
		}

		File.Move(file, file + ".1", overwrite: true);
	}

	/// <summary>
	/// Removes write access for everyone who is not SYSTEM or an administrator.
	/// <para>
	/// The default descriptor for a directory under Program Data lets every local user read it, which for a
	/// log of this service means disclosing the recorded account identifier and the paths it was asked to
	/// touch. The descriptor is applied at creation and is not inherited, so it is the whole list.
	/// </para>
	/// </summary>
	public static void RestrictDirectory(string directory)
	{
		var security = new DirectorySecurity();

		security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

		const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

		foreach (var wellKnown in new[]
		{
			WellKnownSidType.LocalSystemSid,
			WellKnownSidType.BuiltinAdministratorsSid,
		})
		{
			security.AddAccessRule(new FileSystemAccessRule(
				new SecurityIdentifier(wellKnown, null),
				FileSystemRights.FullControl,
				inheritance,
				PropagationFlags.None,
				AccessControlType.Allow));
		}

		new DirectoryInfo(directory).SetAccessControl(security);
	}
}