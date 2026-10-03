using System.Runtime.InteropServices;

namespace Jarvis.Service;

/// <summary>
/// The service control manager handshake.
/// <para>
/// A Windows service is not a process that stays alive. It has to connect to the service control manager
/// and then tell it what state it is in, and SCM gives up on a process that never does either. Skipping
/// this produces a service that installs cleanly, refuses to start, and reports 1053, which is what a
/// plain "wait forever" loop does.
/// </para>
/// <para>
/// The dispatcher blocks until SCM asks the service to stop, so it owns the calling thread and the work
/// runs alongside it.
/// </para>
/// </summary>
internal static class ServiceControl
{
	private const int ServiceWin32OwnProcess = 0x00000010;
	private const int ServiceStopped = 0x00000001;
	private const int ServiceStartPending = 0x00000002;
	private const int ServiceStopPending = 0x00000003;
	private const int ServiceRunning = 0x00000004;

	private const uint AcceptStop = 0x00000001;
	private const uint AcceptShutdown = 0x00000004;

	private const uint ControlStop = 0x00000001;
	private const uint ControlShutdown = 0x00000005;

	private static readonly ManualResetEventSlim StopRequested = new(false);

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
	}

	/// <summary>One entry of the dispatch table. Both fields are pointers, so the name is unmanaged memory.</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct ServiceTableEntry
	{
		public IntPtr ServiceName;
		public IntPtr ServiceProcedure;
	}

	private delegate void ServiceHandler(uint control);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool StartServiceCtrlDispatcher(IntPtr table);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool RegisterServiceCtrlHandler(string serviceName, ServiceHandler handler, IntPtr context);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool SetServiceStatus(IntPtr status, ref ServiceStatus statusBuffer);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern IntPtr GetServiceStatusHandle();

	private static IntPtr _statusHandle;

	/// <summary>Raised when the service control manager asks the service to stop.</summary>
	public static event Action? Stopping;

	/// <summary>
	/// Connects to the service control manager and blocks until it asks the service to stop.
	/// <para>
	/// The work runs on its own thread because the dispatcher owns this one. The returned value is the
	/// process exit code: a non-zero from the dispatcher means the handshake failed, which is the one case
	/// where pretending to be a service would hide the reason.
	/// </para>
	/// </summary>
	public static int Run(string serviceName, Action work)
	{
		Handler = OnControl;

		var name = Marshal.StringToHGlobalUni(serviceName);

		try
		{
			// The dispatch table is an array of name/procedure pairs, terminated by the absence of a further
			// entry. Exactly two entries are written: the real one and nothing after it. Writing an explicit
			// null terminator a third entry along runs past the allocation, and the resulting heap corruption
			// kills the process with 0xc0000409 the moment the manager calls into it.
			var entrySize = IntPtr.Size * 2;
			var table = Marshal.AllocHGlobal(entrySize * 2);

			try
			{
				Marshal.WriteIntPtr(table, name);
				Marshal.WriteIntPtr(table, entrySize, Marshal.GetFunctionPointerForDelegate(Handler));

				if (!StartServiceCtrlDispatcher(table))
				{
					var code = Marshal.GetLastWin32Error();

					// 1063 is "the service process could not connect to the service controller", which is what
					// happens when this is run by hand rather than by the service control manager. It is not a
					// fault, so it is not reported as one.
					if (code == 1063)
					{
						ServiceLog.Information(
							"The service control manager is not supervising this process, so it is running as a console process.");

						return ConsoleFallback(work);
					}

					return Fail($"The service control manager refused the connection (error {code}).");
				}
			}
			finally
			{
				Marshal.FreeHGlobal(table);
			}
		}
		finally
		{
			Marshal.FreeHGlobal(name);
		}

		Report(ServiceRunning, AcceptStop | AcceptShutdown);

		// Registered after the dispatcher connects, because that is when the manager will start sending
		// control messages. Without it a stop request is never delivered and the service has to be killed.
		if (!RegisterServiceCtrlHandler(serviceName, Handler, IntPtr.Zero))
		{
			ServiceLog.Warning(
				"The stop handler could not be registered (error " + Marshal.GetLastWin32Error()
				+ "). The service will have to be stopped by terminating the process.");
		}

		var worker = new Thread(() => work())
		{
			IsBackground = false,
			Name = "JARVIS service work",
		};

		worker.Start();

		// Blocked here rather than returning, because returning from the dispatcher is what tells the service
		// control manager the service has ended. The hint is zero so SCM never times the service out.
		StopRequested.Wait(Timeout.InfiniteTimeSpan);

		Report(ServiceStopPending, 0);

		// Given a moment to finish what it is doing, so a stop does not cut a pipe write in half.
		if (!worker.Join(TimeSpan.FromSeconds(10)))
		{
			ServiceLog.Warning("The service worker did not finish within ten seconds of being asked to stop.");
		}

		Report(ServiceStopped, 0);
		return 0;
	}

	[ThreadStatic]
	private static ServiceHandler? _handler;

	private static ServiceHandler Handler
	{
		get => _handler!;
		set => _handler = value;
	}

	private static void OnControl(uint control)
	{
		switch (control)
		{
			case ControlStop:
			case ControlShutdown:
				// The status has to move to StopPending with a zero hint before the work is interrupted, or
				// the service control manager reports a hung service on the way down.
				Report(ServiceStopPending, 0);
				Stopping?.Invoke();
				StopRequested.Set();
				break;

			default:
				break;
		}
	}

	private static void Report(uint state, uint accepted)
	{
		if (_statusHandle == IntPtr.Zero)
		{
			_statusHandle = GetServiceStatusHandle();
		}

		var status = new ServiceStatus
		{
			ServiceType = ServiceWin32OwnProcess,
			CurrentState = state,
			ControlsAccepted = accepted,
			Win32ExitCode = 0,
			ServiceSpecificExitCode = 0,
			CheckPoint = 0,

			// A zero wait hint tells the service control manager to wait indefinitely rather than to decide
			// the service is hung while it is doing something legitimate.
			WaitHint = 0,
		};

		_ = SetServiceStatus(_statusHandle, ref status);
	}

	private static int Fail(string message)
	{
		ServiceLog.Error(message);
		Console.Error.WriteLine(message);
		return 1;
	}

	/// <summary>
	/// Used when the service control manager is not supervising this process, which is what happens when the
	/// binary is run by hand with <c>--console</c>. Runs the work and waits for Ctrl+C, so the mode is useful
	/// for seeing the service's output rather than only for the service control manager's log.
	/// </summary>
	private static int ConsoleFallback(Action work)
	{
		var stop = new ManualResetEventSlim(false);

		Console.CancelKeyPress += (_, args) =>
		{
			args.Cancel = true;
			stop.Set();
		};

		var worker = new Thread(() => work())
		{
			IsBackground = false,
			Name = "JARVIS console work",
		};

		worker.Start();

		Console.WriteLine("Running as a console process. Press Ctrl+C to stop.");
		stop.Wait(Timeout.InfiniteTimeSpan);

		return 0;
	}

	/// <summary>Reports that the service is starting, before the dispatcher connects.</summary>
	public static void ReportStarting() => Report(ServiceStartPending, 0);
}
