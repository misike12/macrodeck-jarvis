using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace Jarvis.Plugin.Core;

/// <summary>
/// A Job Object, which is the only reliable way to make sure a process tree is gone when this plugin
/// stops.
/// <para>
/// The reason this exists rather than a list of processes: if the plugin is killed, its finally blocks
/// never run, so anything it remembered killing is still running. A Job Object is held by the operating
/// system, and when the last handle to it closes every process inside it is terminated. It also has a
/// hard limit, so a runaway command runs out of memory and is killed rather than taking the session with
/// it.
/// </para>
/// <para>
/// The nested form is used deliberately. A process is only assigned when the limit allows it, and Windows
/// nests a process into the job of every ancestor that has one, so a plugin cannot escape by starting a
/// child that is itself spawned inside a job.
/// </para>
/// </summary>
public sealed class JobObject : IDisposable
{
	private IntPtr _handle;
	private bool _disposed;

	/// <summary>The per-process memory ceiling. Chosen to be far above anything legitimate and well below
	/// what would make the machine unresponsive.</summary>
	private const ulong MemoryLimitBytes = 4UL * 1024 * 1024 * 1024;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);

	[StructLayout(LayoutKind.Sequential)]
	private struct IoCounters
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	/// <summary>
	/// The documented layout, in the documented order. The field names and sizes are not negotiable, which
	/// is exactly why this is written out rather than reused from a library that might reorder it.
	/// </summary>
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct JobObjectExtendedLimitInformationNative
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public UIntPtr MinimumWorkingSetSize;
		public UIntPtr MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public UIntPtr Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
		public IoCounters IoInfo;
		public UIntPtr ProcessMemoryLimit;
		public UIntPtr JobMemoryLimit;
		public UIntPtr PeakProcessMemoryUsed;
		public UIntPtr PeakJobMemoryUsed;
	}

	private const int JobObjectExtendedLimitInformation = 9;

	/// <summary>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.</summary>
	private const uint LimitKillOnJobClose = 0x00002000;

	/// <summary>JOB_OBJECT_LIMIT_JOB_MEMORY.</summary>
	private const uint LimitJobMemory = 0x00000200;

	/// <summary>JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION.</summary>
	private const uint LimitDieOnUnhandledException = 0x00000400;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr GetCurrentProcess();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool IsProcessInJob(
		IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

	/// <summary>
	/// Whether the plugin is already inside a job. It usually is, because Macro Deck's own supervisor puts
	/// each plugin process in one, and nested jobs are only supported from Windows 8.
	/// </summary>
	private static bool AlreadyInAJob()
	{
		if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob))
		{
			// If the answer cannot be had, assume it is, because a failed query is not a reason to skip
			// containment entirely.
			return true;
		}

		return inJob;
	}

	/// <summary>Creates a job, or returns null when one cannot be made on this machine.</summary>
	public static JobObject? TryCreate(ILogger logger)
	{
		try
		{
			var job = new JobObject(logger);

			if (!job.Init())
			{
				job.Dispose();
				logger.Warning("A process job could not be created, so cancellation may leave a process behind.");
				return null;
			}

			return job;
		}
		catch (Exception exception) when (exception is OutOfMemoryException or InvalidOperationException)
		{
			logger.Warning(exception, "A process job could not be created.");
			return null;
		}
	}

	private readonly ILogger _logger;

	private JobObject(ILogger logger) => _logger = logger.ForContext<JobObject>();

	private bool Init()
	{
		if (AlreadyInAJob())
		{
			// Nesting is attempted anyway, because Windows 8 and later support it and a failure is reported
			// per process rather than here.
			_logger.Debug("The plugin is already inside a job, so the child job will be nested.");
		}

		_handle = CreateJobObject(IntPtr.Zero, null);

		if (_handle == IntPtr.Zero)
		{
			return false;
		}

		var limits = new JobObjectExtendedLimitInformationNative
		{
			LimitFlags = LimitKillOnJobClose | LimitDieOnUnhandledException | LimitJobMemory,
			JobMemoryLimit = new UIntPtr(MemoryLimitBytes),
		};

		var size = (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationNative>();
		var buffer = Marshal.AllocHGlobal((int)size);

		try
		{
			Marshal.StructureToPtr(limits, buffer, false);

			if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, size))
			{
				return false;
			}
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}

		return true;
	}

	/// <summary>
	/// Puts a process into the job. Returns false rather than throwing, because the caller still has a
	/// working command without containment and that is better than failing the tool call outright.
	/// </summary>
	public bool TryAssign(Process process, int processId)
	{
		if (_disposed || _handle == IntPtr.Zero)
		{
			return false;
		}

		try
		{
			using var owned = Process.GetProcessById(processId);

			if (!AssignProcessToJobObject(_handle, owned.Handle))
			{
				_logger.Debug(
					"The error assigning process {ProcessId} to the job was {Error}.",
					processId,
					Marshal.GetLastWin32Error());

				return false;
			}

			return true;
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
		{
			_logger.Debug(exception, "Process {ProcessId} could not be assigned to the job.", processId);
			return false;
		}
	}

	/// <summary>Terminates everything currently in the job.</summary>
	public void KillAll()
	{
		if (_disposed || _handle == IntPtr.Zero)
		{
			return;
		}

		try
		{
			_ = TerminateJobObject(_handle, 1);
		}
		catch (EntryPointNotFoundException)
		{
			// Not present before Windows 8, where the close-on-handle behaviour below still applies.
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

	/// <summary>
	/// Closing the last handle terminates every process in the job. This is the property that makes the
	/// plugin safe to kill: nothing in this process has to run for the cleanup to happen.
	/// </summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;

		if (_handle == IntPtr.Zero)
		{
			return;
		}

		_ = CloseHandle(_handle);
		_handle = IntPtr.Zero;
	}
}
