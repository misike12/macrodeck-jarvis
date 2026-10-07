using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace Jarvis.Plugin.Core;

/// <summary>
/// Runs a command with no console window ever appearing. Output is captured on background threads so
/// a command that writes more than a pipe buffer cannot deadlock, and the process tree is tracked so
/// cancel can terminate it rather than orphan it.
/// </summary>
public sealed class ProcessTracker : IDisposable
{
	private const int ErrorAccessDenied = 5;

	private readonly Process _process;
	private readonly ILogger _logger;
	private readonly StringBuilder _output;
	private readonly Lock _outputGate = new();
	private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly JobObject? _job;

	private ProcessTracker(Process process, ILogger logger, JobObject? job)
	{
		_process = process;
		_logger = logger.ForContext<ProcessTracker>();
		_output = new StringBuilder();
		_job = job;
	}

	/// <summary>
	/// The process id, read from the process rather than captured at construction. A <see cref="Process"/>
	/// has no id until it has been started, so a value taken before <c>Start</c> would throw.
	/// </summary>
	public int Id => _process.Id;

	public bool HasExited
	{
		get
		{
			try
			{
				return _process.HasExited;
			}
			catch (InvalidOperationException)
			{
				return true;
			}
		}
	}

	public int ExitCode
	{
		get
		{
			try
			{
				return _process.HasExited ? _process.ExitCode : -1;
			}
			catch (InvalidOperationException)
			{
				return -1;
			}
		}
	}

	public string Output
	{
		get
		{
			lock (_outputGate)
			{
				return _output.ToString();
			}
		}
	}

	/// <summary>
	/// Starts <paramref name="fileName"/> with no window, no console and no shell. The working
	/// directory is explicit so a relative path in the command means what the caller meant.
	/// </summary>
	public static ProcessTracker StartHidden(
		string fileName,
		IReadOnlyList<string> arguments,
		string? workingDirectory,
		ILogger logger,
		JobObject? job = null)
	{
		var info = new ProcessStartInfo
		{
			FileName = fileName,
			WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = false,
			WindowStyle = ProcessWindowStyle.Hidden,
		};

		foreach (var argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}

		var process = new Process { StartInfo = info, EnableRaisingEvents = true };
		var tracker = new ProcessTracker(process, logger, job);

		process.Exited += (_, _) => tracker._exited.TrySetResult();

		if (!process.Start())
		{
			process.Dispose();
			throw new Win32Exception((int)Marshal.GetLastWin32Error(), "The command could not be started.");
		}

		// Handlers attached before the reads start. BeginOutputReadLine starts reading immediately, so anything
		// the child writes inside that window is delivered with nobody listening and is lost. A fast command
		// produces its whole output in that window, so this is the ordinary case rather than a rare one.
		process.OutputDataReceived += (_, e) => tracker.AppendLine(e.Data);
		process.ErrorDataReceived += (_, e) => tracker.AppendLine(e.Data);

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		// Containment is applied after the start rather than through a suspended process, which is what
		// closes the window between "running" and "in the job". A process that escapes that gap is still
		// killable by tree, so containment is an improvement rather than the only defence.
		if (job is not null && !job.TryAssign(process, process.Id))
		{
			logger.Debug("Process {ProcessId} runs without job containment.", process.Id);
		}

		return tracker;
	}

	private void AppendLine(string? line)
	{
		if (line is null)
		{
			return;
		}

		lock (_outputGate)
		{
			if (_output.Length < MaxCapturedCharacters)
			{
				_output.AppendLine(line);
			}
		}
	}

	public async Task<int> WaitAsync(CancellationToken cancellationToken)
	{
		using var registration = cancellationToken.Register(() => Kill());
		await _exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
		return ExitCode;
	}

	public async Task<(int ExitCode, string Output)> WaitForResultAsync(CancellationToken cancellationToken)
	{
		try
		{
			await WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return (ExitCode, Output);
		}

		return (ExitCode, Output);
	}

	/// <summary>
	/// Terminates the whole tree. Killing only the parent leaves grandchildren running, and a shell
	/// that started a compiler is exactly the case where that matters.
	/// </summary>
	public void Kill()
	{
		if (HasExited)
		{
			return;
		}

		try
		{
			KillTree(_process.Id);
		}
		catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorAccessDenied)
		{
			try
			{
				_process.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException)
			{
				return;
			}
		}
		catch (InvalidOperationException)
		{
			return;
		}
	}

	public void Dispose()
	{
		try
		{
			_process.Dispose();
		}
		catch (InvalidOperationException)
		{
			return;
		}
	}

	/// <summary>
	/// taskkill is asked to take the tree, which is the only way to reach a grandchild that killing the
	/// parent alone would leave running.
	/// </summary>
	/// <summary>
	/// Terminates the tree by walking it from the inside out, killing every child before its parent.
	/// <para>
	/// This is done by process enumeration rather than by shelling out to taskkill. Two reasons: the shell
	/// out needs a console and a path, and the enumeration is synchronous, so a child that spawns a
	/// grandchild during the walk is still caught by the parent-first ordering.
	/// </para>
	/// </summary>
	private static void KillTree(int processId)
	{
		// The child walk is isolated, because a failure to enumerate it must not stop the parent from being
		// killed. Leaving the parent alive is the one outcome this method cannot produce.
		try
		{
			foreach (var child in ChildProcessIds(processId))
			{
				KillTree(child);
			}
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
		{
			// Not being able to enumerate the children is not a reason to leave this process running.
		}

		try
		{
			using var process = Process.GetProcessById(processId);
			process.Kill();
		}
		catch (ArgumentException)
		{
			// Already gone, which is the desired outcome.
		}
		catch (InvalidOperationException)
		{
			// Already exited.
		}
		catch (Win32Exception)
		{
			// The process ended between the enumeration and the kill.
		}
	}

	/// <summary>
	/// The direct children of a process, read from the process snapshot rather than from
	/// <c>Process.GetProcesses</c> and parent ids, which costs a call per process and races far more.
	/// </summary>
	private static List<int> ChildProcessIds(int parentId)
	{
		var children = new List<int>();
		var snapshot = Process.GetProcesses();

		try
		{
			foreach (var candidate in snapshot)
			{
				try
				{
					if (candidate.Id != parentId && HasParent(candidate.Id, parentId))
					{
						children.Add(candidate.Id);
					}
				}
catch (Exception exception) when (
					exception is ArgumentException or InvalidOperationException)
				{
					continue;
				}
				finally
				{
					candidate.Dispose();
				}
			}
		}
		finally
		{
			foreach (var remaining in snapshot)
			{
				remaining.Dispose();
			}
		}

		return children;
	}

	private static bool HasParent(int processId, int parentId)
	{
		// The parent is read from the toolhelp snapshot rather than from a Process handle. Opening the
		// process first would throw for anything that exited between the snapshot and this call, and that
		// exception escaped the walk and out of Kill, which is the one path that must not throw: a caller
		// cancelling a tool call would get an exception instead of a terminated process.
		using var parent = TryGetParent(processId);

		return parent is not null && parent.Id == parentId;
	}

	/// <summary>
	/// The parent of a process. The toolhelp snapshot is used rather than WMI, which is orders of
	/// magnitude slower and is not available in every hosting context.
	/// </summary>
	private static Process? TryGetParent(int processId)
	{
		IntPtr snapshot = IntPtr.Zero;

		try
		{
			snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);

			if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
			{
				return null;
			}

			var entry = new ProcessEntry32
			{
				Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
			};

			if (!Process32First(snapshot, ref entry))
			{
				return null;
			}

			do
			{
				if (entry.ProcessId == (uint)processId && entry.ParentProcessId != 0)
				{
					try
					{
						return Process.GetProcessById((int)entry.ParentProcessId);
					}
					catch (ArgumentException)
					{
						// The parent has already gone, which is itself the answer.
						return null;
					}
				}
			}
			while (Process32Next(snapshot, ref entry));

			return null;
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		finally
		{
			if (snapshot != IntPtr.Zero && snapshot != InvalidHandleValue)
			{
				_ = CloseHandle(snapshot);
			}
		}
	}

	private const uint SnapshotProcesses = 0x00000002;

	private static readonly IntPtr InvalidHandleValue = new(-1);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ProcessEntry32
	{
		public uint Size;
		public uint Usage;
		public uint ProcessId;
		public nint DefaultHeapId;
		public uint ModuleId;
		public uint Threads;
		public uint ParentProcessId;
		public int PriorityClassBase;
		public uint Flags;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string ExeFile;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);


	private const int MaxCapturedCharacters = 64 * 1024;
}