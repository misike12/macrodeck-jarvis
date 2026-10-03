using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Jarvis.Service;

/// <summary>
/// The service control manager calls, reached through <c>advapi32</c>.
/// <para>
/// Driven by the service control manager's own API rather than by <c>sc.exe</c>, because that produces
/// structured errors instead of exit codes and because it is the only way to create a service that runs as
/// LocalSystem without a hand-built command line. The command-line tool remains available for starting and
/// stopping, which it is good at.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeServiceControl
{
	private const uint ScManagerAllAccess = 0x000F003F;
	private const uint ServiceAllAccess = 0x000F01FF;
	private const uint CreateService = 0x0002;

	private const uint QueryConfig = 0x0001;
	private const uint StopServiceControl = 0x0020;
	private const uint QueryStatus = 0x0004;

	private const uint ServiceWin32OwnProcess = 0x00000010;
	private const uint ServiceAutoStart = 0x00000002;
	private const uint ServiceErrorNormal = 0x00000001;
	private const uint ServiceStopped = 0x00000001;

	private const int ErrorServiceExists = 1073;
	private const int ErrorServiceDoesNotExist = 1060;
	private const int ErrorServiceMarkedForDelete = 1072;
	private const int ErrorServiceNotActive = 1062;

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
	private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
	private static extern IntPtr OpenService(IntPtr manager, string name, uint access);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateServiceW")]
	private static extern IntPtr CreateServiceEntry(
		IntPtr manager,
		string name,
		string displayName,
		uint desiredAccess,
		uint serviceType,
		uint startType,
		uint errorControl,
		string binaryPath,
		string? loadGroup,
		nint tagId,
		string? dependencies,
		string? serviceStartName,
		string? password);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "DeleteService")]
	private static extern bool DeleteService(IntPtr service, string name);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool CloseServiceHandle(IntPtr handle);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfigW")]
	private static extern bool ChangeServiceConfig(
		IntPtr service,
		uint serviceType,
		uint startType,
		uint errorControl,
		string? binaryPath,
		string? loadGroup,
		nint tagId,
		string? dependencies,
		string? serviceStartName,
		string? password,
		string? displayName);

	[StructLayout(LayoutKind.Sequential)]
	private struct ServiceStatus
	{
		public uint ServiceType;
		public uint CurrentState;
		public uint ControlsAccepted;
		public uint Win32ExitCode;
		public uint ServiceSpecificExitCode;
		public uint CheckPoint;
		public uint WaitHint;
		public uint ServiceProcessId;
		public uint ServiceFlags;
	}

	/// <summary>
	/// Creates the service, or updates it if it is already there. Updating rather than failing is deliberate:
	/// reinstalling after a build is the normal case, and a second install should just pick up the new path.
	/// </summary>
	internal static int Create(
		string name,
		string displayName,
		string binaryPath,
		string account,
		string startType,
		string description)
	{
		var manager = OpenSCManager(null, null, ScManagerAllAccess);

		if (manager == IntPtr.Zero)
		{
			Console.Error.WriteLine($"The service manager could not be opened: {LastError()}");
			return 1;
		}

		try
		{
			var existing = OpenService(manager, name, ServiceAllAccess);

			if (existing != IntPtr.Zero)
			{
				try
				{
					var start = startType.Equals("Automatic", StringComparison.OrdinalIgnoreCase)
						? ServiceAutoStart
						: startType.Equals("Manual", StringComparison.OrdinalIgnoreCase)
							? 0x00000003
							: ServiceAutoStart;

					if (!ChangeServiceConfig(
						existing,
						ServiceWin32OwnProcess,
						start,
						ServiceErrorNormal,
						binaryPath,
						null,
						0,
						null,
						account,
						null,
						displayName))
					{
						Console.Error.WriteLine($"The existing service could not be updated: {LastError()}");
						return 1;
					}

					Console.WriteLine($"Updated {displayName}. Stop it before replacing the binary.");
					return 0;
				}
				finally
				{
					_ = CloseServiceHandle(existing);
				}
			}

			var created = CreateServiceEntry(
				manager,
				name,
				displayName,
				ServiceAllAccess,
				ServiceWin32OwnProcess,
				ServiceAutoStart,
				ServiceErrorNormal,
				binaryPath,
				null,
				0,
				null,
				account,
				null);

			if (created == IntPtr.Zero)
			{
				var code = Marshal.GetLastWin32Error();

				if (code == ErrorServiceExists)
				{
					Console.WriteLine($"{displayName} is already installed.");
					return 0;
				}

				Console.Error.WriteLine($"The service could not be created: {LastError()}");
				return 1;
			}

			try
			{
				// The description is what a user sees in the Services list, so it is set here rather than
				// left empty. A failure is not worth aborting for.
				_ = NativeMethods.ChangeServiceConfig2(
					created,
					1 /* SERVICE_CONFIG_DESCRIPTION */,
					IntPtr.Zero,
					description,
					IntPtr.Zero,
					IntPtr.Zero,
					IntPtr.Zero,
					IntPtr.Zero,
					IntPtr.Zero,
					IntPtr.Zero);
			}
			finally
			{
				_ = CloseServiceHandle(created);
			}

			return 0;
		}
		finally
		{
			_ = CloseServiceHandle(manager);
		}
	}

	/// <summary>Stops and removes the service. Returns the reason it could not, or null on success.</summary>
	internal static string? Delete(string name, ILogger log)
	{
		var manager = OpenSCManager(null, null, ScManagerAllAccess);

		if (manager == IntPtr.Zero)
		{
			return $"The service manager could not be opened: {LastError()}";
		}

		try
		{
			var service = OpenService(manager, name, ServiceAllAccess);

			if (service == IntPtr.Zero)
			{
				return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
					? $"{Program.ServiceDisplayName} is not installed."
					: $"The service could not be opened: {LastError()}";
			}

			try
			{
				StopService(service, name, log);

				if (!DeleteService(service, name))
				{
					var code = Marshal.GetLastWin32Error();

					return code switch
					{
						ErrorServiceDoesNotExist => $"{Program.ServiceDisplayName} is not installed.",
						ErrorServiceMarkedForDelete =>
							$"{Program.ServiceDisplayName} is already marked for removal and will go once "
							+ "nothing holds it open.",
						_ => $"The service could not be removed: {LastError()}",
					};
				}

				return null;
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

	/// <summary>Stops the service and waits for it to stop, rather than assuming it did.</summary>
	private static void StopService(IntPtr service, string name, ILogger log)
	{
		if (!QueryServiceStatus(service, out var status) || status.CurrentState == ServiceStopped)
		{
			return;
		}

		var request = new ServiceStatus { CurrentState = 0 };

		if (!ControlService(service, StopServiceControl, ref request))
		{
			var code = Marshal.GetLastWin32Error();

			if (code is ErrorServiceNotActive or ErrorServiceDoesNotExist)
			{
				return;
			}

			log.Warning($"Stopping {name} reported {LastError()}.");
			return;
		}

		// Waited for rather than slept for: deleting a service that is still stopping leaves it registered
		// and dead, which to a user looks exactly like a crash.
		var deadline = DateTime.UtcNow.AddSeconds(15);

		while (DateTime.UtcNow < deadline)
		{
			if (QueryServiceStatus(service, out status) && status.CurrentState == ServiceStopped)
			{
				return;
			}

			Thread.Sleep(200);
		}

		log.Warning($"{name} did not stop within fifteen seconds.");
	}

	private static string LastError() =>
		new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;

	/// <summary>
	/// The second configuration change, used only for the description. Kept in its own class because it has
	/// a different shape from the first and mixing the two is how the argument order goes wrong.
	/// </summary>
	private static class NativeMethods
	{
		[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfig2W")]
		internal static extern bool ChangeServiceConfig2(
			IntPtr service,
			uint level,
			IntPtr info,
			string description,
			IntPtr startType,
			IntPtr errorControl,
			IntPtr binaryPath,
			IntPtr loadOrderGroup,
			IntPtr tagId,
			IntPtr dependencies);
	}
}
