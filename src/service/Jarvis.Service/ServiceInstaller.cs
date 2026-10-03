using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;

namespace Jarvis.Service;

/// <summary>
/// Installs and removes the service itself.
/// <para>
/// This is the only part of the project that needs an administrator shell, and it says so in plain words
/// rather than failing with an access-denied code the user has to decode. Everything else in the service
/// runs unprivileged.
/// </para>
/// <para>
/// The service runs as LocalSystem, because that is the identity which can write under HKLM and register
/// scheduled tasks without a prompt per operation. On a shared machine a dedicated service account with
/// write access to only the two registry locations needed would be the better arrangement, and this is the
/// place that choice is made.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceInstaller
{
	public const int Success = 0;

	public const int Failed = 1;

	private const int ErrorAccessDenied = 5;
	private const int ErrorServiceExists = 1073;
	private const int ErrorServiceDoesNotExist = 1060;
	private const int ErrorServiceMarkedForDelete = 1072;
	private const int ErrorServiceRequestTimeout = 1053;

	private const uint ServiceAllAccess = 0x000F01FF;

	private const uint ServiceRunning = 0x00000004;

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern IntPtr OpenService(IntPtr manager, string service, uint access);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool DeleteService(IntPtr service, string name);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool StartService(IntPtr service, uint argc, nint argv);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool CloseServiceHandle(IntPtr handle);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, uint size, out uint needed);

	[StructLayout(LayoutKind.Sequential)]
	private struct ServiceStatus
	{
		public uint State;
		public uint ControlsAccepted;
		public uint Win32ExitCode;
		public uint ServiceSpecificExitCode;
		public uint CheckPoint;
		public uint WaitHint;
	}

	public static bool IsAdministrator()
	{
		using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();

		return new System.Security.Principal.WindowsPrincipal(identity)
			.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
	}

	private static string RequireAdministrator(string what)
	{
		if (IsAdministrator())
		{
			return string.Empty;
		}

		var message = $"{what} needs an administrator command prompt. Open PowerShell as administrator "
			+ "and run the same command again.";

		Console.Error.WriteLine(message);
		return message;
	}

	/// <summary>
	/// Installs the service.
	/// <para>
	/// The installer reads the binary path out of the service itself rather than writing one, because the
	/// control manager is the authority on what it will run and a path this code guesses at is a path that
	/// can be wrong in a way that only shows up at start time.
	/// </para>
	/// </summary>
	public static int Install(ILogger log)
	{
		if (RequireAdministrator("Installing the service") is { } denial)
		{
			log.Fatal(denial);
			return ErrorAccessDenied;
		}

		var executable = Environment.ProcessPath;

		if (string.IsNullOrWhiteSpace(executable))
		{
			Console.Error.WriteLine("The service could not work out its own path. Run the built binary directly.");
			return Failed;
		}

		if (AlreadyInstalled(log, out var existing) is { } failure)
		{
			Console.Error.WriteLine(failure);
			return failure.Contains("already installed", StringComparison.Ordinal) ? Success : Failed;
		}

		_ = existing;

		// The service mode is selected by the --console argument. Without it the binary would show a tray
		// icon as LocalSystem, where nobody could ever see it.
		var command = $"\"{executable}\" --console";

		var exit = NativeServiceControl.Create(
			Program.ServiceName,
			Program.ServiceDisplayName,
			command,
			"SYSTEM",
			"Automatic",
			"Runs the operations JARVIS needs administrator rights for, on request from the plugin.");

		if (exit != Success)
		{
			return exit;
		}

		Console.WriteLine($"Installed {Program.ServiceDisplayName}.");
		Console.WriteLine($"Start it with:  sc start {Program.ServiceName}");

		log.Information($"The service {Program.ServiceName} was installed with command line {command}.");
		return Success;
	}

	/// <summary>
	/// Removes the service. The binary is stopped first, because deleting a running service leaves it
	/// registered and dead, which to a user is indistinguishable from a crash.
	/// </summary>
	public static int Uninstall(ILogger log)
	{
		if (RequireAdministrator("Removing the service") is { } denial)
		{
			log.Fatal(denial);
			return ErrorAccessDenied;
		}

		if (NativeServiceControl.Delete(Program.ServiceName, log) is { } failure)
		{
			if (failure.Contains("not installed", StringComparison.Ordinal))
			{
				Console.WriteLine($"{Program.ServiceDisplayName} is not installed.");
				return Success;
			}

			Console.Error.WriteLine(failure);
			return Failed;
		}

		Console.WriteLine($"Removed {Program.ServiceDisplayName}.");

		log.Information($"The service {Program.ServiceName} was removed.");
		return Success;
	}

	/// <summary>Whether the service is registered. Null when it is, a reason when it is not.</summary>
	private static string? AlreadyInstalled(ILogger log, out string binaryPath)
	{
		binaryPath = string.Empty;

		var manager = OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);

		if (manager == IntPtr.Zero)
		{
			return $"The service manager could not be opened: {LastErrorText()}";
		}

		try
		{
			var service = OpenService(manager, Program.ServiceName, 0x0001 /* SERVICE_QUERY_CONFIG */);

			if (service == IntPtr.Zero)
			{
				var code = Marshal.GetLastWin32Error();

				return code == ErrorServiceDoesNotExist
					? null
					: $"The service could not be inspected: {LastErrorText()}";
			}

			try
			{
				binaryPath = NativeServiceControl.ReadBinaryPath(service);
				log.Information($"{Program.ServiceDisplayName} is already installed at {binaryPath}.");
				return $"{Program.ServiceDisplayName} is already installed.";
			}
			finally
			{
				_ = CloseServiceHandle(service);
			}
		}
		finally
		{
			_ = CloseServiceHandle(manager);
		}
	}

	/// <summary>Whether the service is registered, for the plugin and the tray icon to ask.</summary>
	public static bool IsInstalled()
	{
		var manager = OpenSCManager(null, null, 0x0001);

		if (manager == IntPtr.Zero)
		{
			return false;
		}

		try
		{
			var service = OpenService(manager, Program.ServiceName, 0x0001);

			if (service == IntPtr.Zero)
			{
				return false;
			}

			_ = CloseServiceHandle(service);
			return true;
		}
		finally
		{
			_ = CloseServiceHandle(manager);
		}
	}

	/// <summary>Whether the service is installed and running, which is the only state that is useful.</summary>
	public static bool IsRunning()
	{
		var manager = OpenSCManager(null, null, 0x0001);

		if (manager == IntPtr.Zero)
		{
			return false;
		}

		try
		{
			var service = OpenService(manager, Program.ServiceName, 0x0001);

			if (service == IntPtr.Zero)
			{
				return false;
			}

			try
			{
				return QueryServiceStatus(service, out var status) && status.State == ServiceRunning;
			}
			finally
			{
				_ = CloseServiceHandle(service);
			}
		}
		finally
		{
			_ = CloseServiceHandle(manager);
		}
	}

	/// <summary>
	/// Starts the service.
	/// <para>
	/// Polled rather than assumed: a caller that returned before the pipe was listening would report the
	/// service as unavailable at the one moment it was about to be available.
	/// </para>
	/// </summary>
	public static bool TryStart(out string detail)
	{
		detail = string.Empty;

		if (!IsAdministrator())
		{
			detail = "Starting the service needs an administrator command prompt.";
			return false;
		}

		var manager = OpenSCManager(null, null, ServiceAllAccess);

		if (manager == IntPtr.Zero)
		{
			detail = $"The service manager could not be opened: {LastErrorText()}";
			return false;
		}

		var service = OpenService(manager, Program.ServiceName, ServiceAllAccess);

		if (service == IntPtr.Zero)
		{
			var code = Marshal.GetLastWin32Error();
			_ = CloseServiceHandle(manager);

			detail = code == ErrorServiceDoesNotExist
				? $"{Program.ServiceDisplayName} is not installed."
				: LastErrorText();

			return false;
		}

		try
		{
			if (QueryServiceStatus(service, out var status) && status.State == ServiceRunning)
			{
				detail = "Already running.";
				return true;
			}

			if (!StartService(service, 0, 0))
			{
				var code = Marshal.GetLastWin32Error();

				detail = code == ErrorServiceRequestTimeout
					? "The service did not start in time."
					: LastErrorText();

				return false;
			}

			var deadline = DateTime.UtcNow.AddSeconds(15);

			while (DateTime.UtcNow < deadline)
			{
				if (QueryServiceStatus(service, out status) && status.State == ServiceRunning)
				{
					detail = "Started.";
					return true;
				}

				Thread.Sleep(200);
			}

			detail = "The service did not reach the running state in time.";
			return false;
		}
		finally
		{
			_ = CloseServiceHandle(service);
			_ = CloseServiceHandle(manager);
		}
	}

	private static string LastErrorText() =>
		new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;

	internal static int DescribeKnownError(int code) => code switch
	{
		ErrorAccessDenied => ErrorAccessDenied,
		ErrorServiceExists => Success,
		ErrorServiceDoesNotExist => ErrorServiceDoesNotExist,
		ErrorServiceMarkedForDelete => ErrorServiceMarkedForDelete,
		_ => Failed,
	};
}
