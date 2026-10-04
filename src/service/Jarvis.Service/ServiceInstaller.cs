using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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

	/// <summary>
	/// Whether this process is elevated, and who it is running as.
	/// <para>
	/// A separate entry point because "the service would not install" and "the shell did not elevate" look
	/// identical from outside, and the difference is one question to ask.
	/// </para>
	/// </summary>
	public static int Diagnose(ILogger log)
	{
		using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
		var principal = new System.Security.Principal.WindowsPrincipal(identity);

		var elevated = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

		Console.WriteLine("JARVIS Service diagnostics");
		Console.WriteLine($"  Identity            {identity.Name}");
		Console.WriteLine($"  Elevated            {elevated}");
		Console.WriteLine($"  Service installed   {IsInstalled()}");
		Console.WriteLine($"  Service running     {IsRunning()}");
		Console.WriteLine($"  Executable          {Environment.ProcessPath}");
		Console.WriteLine($"  Pipe name           {Protocol.PipeName}");

		if (!elevated)
		{
			Console.WriteLine();
			Console.WriteLine("Registering the service needs an elevated shell.");
			Console.WriteLine("Close this window, right-click PowerShell, choose Run as administrator, and try again.");
			return ErrorAccessDenied;
		}

		return Success;
	}

	/// <summary>
	/// Creates the service by asking <c>sc.exe</c> to do it.
	/// <para>
	/// Deliberately not the service control manager's own entry point. The direct call returns a null handle
	/// with a last error of <c>ERROR_SUCCESS</c> when it fails, which is no diagnosis at all, and that is
	/// exactly what it did. <c>sc.exe</c> is the tool Windows ships for this, it validates the same
	/// parameters, and it explains which parameter it disliked.
	/// </para>
	/// <para>
	/// Updating an existing registration still goes through the API, because that has no equivalent
	/// <c>sc</c> verb and the update path is not the one that failed.
	/// </para>
	/// </summary>
	private static int CreateWithSc(
		string name,
		string displayName,
		string binaryPath,
		string startType,
		string account,
		string description)
	{
var start = startType.Equals("Automatic", StringComparison.OrdinalIgnoreCase)
			? "auto"
			: startType.Equals("Manual", StringComparison.OrdinalIgnoreCase) ? "demand" : "auto";

		// An existing registration is updated rather than treated as a reason to refuse. The common case is a
		// reinstall after a build, and the binary path changes whenever the service moves out of the build
		// output, so refusing to update left the manager pointing at a path that no longer existed: a stale
		// registration that reported success and then failed to start with nothing to explain it.
		var existing = Run("sc.exe", ["query", name]);

		if (existing.ExitCode == 0)
		{
			var configured = Run(
				"sc.exe",
				[
					"config",
					name,
					"binPath=", binaryPath,
					"start=", start,
					"obj=", account,
					"DisplayName=", displayName,
				]);

			if (configured.ExitCode != 0)
			{
				Console.Error.WriteLine(
					$"The service is already registered and could not be updated (exit code {configured.ExitCode}). "
						+ "Close Services.msc or any handle holding the service open, then run this again.");

				Console.Error.WriteLine(configured.Output.TrimEnd());

				return configured.ExitCode;
			}

			Console.WriteLine($"Updated the existing {Program.ServiceDisplayName} registration.");

			// The description is set here too, because returning early would leave an existing registration
			// without one. It was not being set at all: the create below failed with ERROR_SERVICE_EXISTS on
			// every reinstall, so the install reported failure and never reached the -Start step, even though
			// the configuration it cared about had just been applied successfully.
			SetDescription(name, description);

			return Success;
		}

		// Each option name is its own argument, with the value as the argument after it. sc.exe says so
		// itself: "the option name includes the equal sign, a space is required between the equal sign and
		// the value". Passing "type= own" as one argument is what produced "Invalid type= field".
		var create = Run(
			"sc.exe",
			[
				"create",
				name,
				"binPath=", binaryPath,
				"type=", "own",
				"start=", start,
				"obj=", account,
				"DisplayName=", displayName,
			]);

		if (create.ExitCode != 0)
		{
			Console.Error.WriteLine($"sc.exe create failed with exit code {create.ExitCode}:");
			Console.Error.WriteLine(create.Output.TrimEnd());
			return create.ExitCode;
		}

		// The description is what a user sees in the Services list, so it is set after the fact. A failure
		// here is not worth failing the install over.
		SetDescription(name, description);

		return Success;
	}

	/// <summary>
	/// Sets the description shown in the Services list. Best effort, because a description is cosmetic and the
	/// registration itself matters far more.
	/// </summary>
	private static void SetDescription(string name, string description)
	{
		var described = Run("sc.exe", ["description", name, description]);

		if (described.ExitCode != 0)
		{
			Console.WriteLine("The service description could not be set.");
			Console.WriteLine(described.Output.TrimEnd());
		}
	}

	/// <summary>Runs a program and captures everything it said, which is the point of using it.</summary>
	private static (int ExitCode, string Output) Run(string fileName, IReadOnlyList<string> arguments)
	{
		var info = new System.Diagnostics.ProcessStartInfo
		{
			FileName = fileName,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		foreach (var argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}

		using var process = System.Diagnostics.Process.Start(info);

		if (process is null)
		{
			return (Failed, $"{fileName} could not be started.");
		}

		// Both streams drained at once. Reading one to completion before starting the other is the classic
		// two-pipe deadlock: a child that fills the stderr buffer while the parent is blocked reading stdout
		// waits for a reader that never arrives. sc.exe is quiet today, which is exactly why this has never
		// mattered and would not survive a future change.
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();

		if (!process.WaitForExit(30_000))
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException)
			{
				// Already gone between the timeout and the kill.
			}

			return (Failed, $"{fileName} did not finish within thirty seconds.");
		}

		return (process.ExitCode, string.Concat(stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult()));
	}

	public static bool IsAdministrator()
	{
		using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();

		return new System.Security.Principal.WindowsPrincipal(identity)
			.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
	}

	/// <summary>
	/// The refusal to give when an action needs an administrator, or an empty string when it does not.
	/// <para>
	/// Split out from the token check so the decision itself can be tested. The bug this guards against was
	/// an ``is { }`` pattern, which matches an empty string, so an elevated shell was refused with an empty
	/// reason and the only symptom was an exit code with nothing behind it.
	/// </para>
	/// </summary>
	public static string RefusalFor(bool isAdministrator, string what)
	{
		if (isAdministrator)
		{
			return string.Empty;
		}

		var message = $"{what} needs an administrator command prompt. Open PowerShell as administrator "
			+ "and run the same command again.";

		Console.Error.WriteLine(message);

		return message;
	}

	private static string RequireAdministrator(string what) => RefusalFor(IsAdministrator(), what);

	/// <summary>
	/// Installs the service.
	/// <para>
	/// The installer reads the binary path out of the service itself rather than writing one, because the
	/// control manager is the authority on what it will run and a path this code guesses at is a path that
	/// can be wrong in a way that only shows up at start time.
	/// </para>
	/// </summary>
/// <summary>
	/// Where the installing user's identifier is recorded.
	/// <para>
	/// The pipe is granted to this one account rather than to the interactive group, so the file is what
	/// decides who can reach a LocalSystem service. It is written with a descriptor that only LocalSystem
	/// and administrators can read, because anyone who could change it could grant themselves the pipe.
	/// </para>
	/// </summary>
	public static string UserSidPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
		@"Jarvis\ServiceUser.sid");

	/// <summary>
	/// Records the account the pipe is granted to.
	/// <para>
	/// Written before the service is registered, so a start that cannot read it fails loudly rather than
	/// silently falling back to a wider group.
	/// </para>
	/// </summary>
	private static string? RecordUserSid(string? sid, ILogger log)
	{
		if (string.IsNullOrWhiteSpace(sid))
		{
			Console.Error.WriteLine(
				"No user identifier was supplied, so there is no account to grant the pipe to. "
					+ "Run the install script, which captures it from the elevated shell.");

			return "The installing user's identifier was not supplied.";
		}

		// Parsed rather than pattern-matched, because the constructor is what rejects a malformed identifier and
		// the alternative is writing a validator.
		try
		{
			_ = new SecurityIdentifier(sid);
		}
		catch (ArgumentException)
		{
			Console.Error.WriteLine($"'{sid}' is not a security identifier.");

			return $"'{sid}' is not a security identifier.";
		}

		try
		{
			var directory = Path.GetDirectoryName(UserSidPath)!;

			Directory.CreateDirectory(directory);

			// Written owner-only rather than inheriting ProgramData's default, which lets any local user read
			// it. The file is small enough to write whole, so no temporary file dance is needed.
			File.WriteAllText(UserSidPath, sid.Trim());

			var security = new FileSecurity();

			security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

			foreach (var wellKnown in new[]
			{
				WellKnownSidType.LocalSystemSid,
				WellKnownSidType.BuiltinAdministratorsSid,
			})
			{
				security.AddAccessRule(new FileSystemAccessRule(
					new SecurityIdentifier(wellKnown, null),
					FileSystemRights.FullControl,
					AccessControlType.Allow));
			}

			new FileInfo(UserSidPath).SetAccessControl(security);

			log.Information($"The pipe will be granted to {sid.Trim()}.");
			Console.WriteLine($"The elevated pipe will be reachable only by {sid.Trim()}.");

			return null;
		}
		catch (Exception exception) when (
			exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
		{
			return $"The installing user's identifier could not be recorded: {exception.Message}";
		}
	}

	public static int Install(ILogger log, string? userSid = null)
	{
		// The refusal is a non-empty string and success is an empty one, so the test is on the length.
		// An `is { }` pattern matches an empty string too, which made an elevated shell fail with a
		// refusal that had no reason in it.
		if (RefusalFor(IsAdministrator(), "Installing the service") is { Length: > 0 } denial)
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

		if (RecordUserSid(userSid, log) is { Length: > 0 } refused)
		{
			Console.Error.WriteLine(refused);
			return Failed;
		}

		// The service mode is selected by the --console argument. Without it the binary would show a tray
		// icon as LocalSystem, where nobody could ever see it.
		var command = $"\"{executable}\" --console";

		// LocalSystem is the account name the service control manager expects. "SYSTEM" is accepted by
		// sc.exe on the command line but is not the name the API takes, and using it here was one of the
		// reasons registration could fail.
		var exit = CreateWithSc(
			Program.ServiceName,
			Program.ServiceDisplayName,
			command,
			"Automatic",
			"LocalSystem",
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
		if (RefusalFor(IsAdministrator(), "Removing the service") is { Length: > 0 } denial)
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

	/// <summary>
	/// Whether the service is registered. Null when it is, a reason when it is not.
	/// <para>
	/// It deliberately does not read the registered configuration back. Those string fields are pointers into
	/// the buffer the service control manager allocated, and treating them as offsets from its own address
	/// produces a wild pointer and an access violation. It was only ever used for a log line, so it is gone
	/// rather than fixed. <c>sc.exe qc JarvisService</c> shows the same thing safely.
	/// </para>
	/// </summary>
	private static string? AlreadyInstalled(ILogger log)
	{
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

			_ = CloseServiceHandle(service);

			log.Information($"{Program.ServiceDisplayName} is already installed.");
			return $"{Program.ServiceDisplayName} is already installed.";
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
