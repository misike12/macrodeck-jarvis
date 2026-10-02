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
	private const int StillActive = 259;
	private const int ErrorAccessDenied = 5;

	private readonly Process _process;
	private readonly ILogger _logger;
	private readonly StringBuilder _output;
	private readonly Lock _outputGate = new();
	private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private ProcessTracker(Process process, ILogger logger)
	{
		_process = process;
		_logger = logger.ForContext<ProcessTracker>();
		_output = new StringBuilder();
		Id = process.Id;
	}

	public int Id { get; }

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
		ILogger logger)
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
		var tracker = new ProcessTracker(process, logger);

		process.Exited += (_, _) => tracker._exited.TrySetResult();

		if (!process.Start())
		{
			process.Dispose();
			throw new Win32Exception((int)Marshal.GetLastWin32Error(), "The command could not be started.");
		}

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		process.OutputDataReceived += (_, e) => tracker.AppendLine(e.Data);
		process.ErrorDataReceived += (_, e) => tracker.AppendLine(e.Data);

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

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool IsProcessRunning(int processId, out int exitCode);

	[DllImport("taskkill.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern int TaskKill(int processId, int exitCode, uint flags);

	private void KillTree(int processId)
	{
		if (!IsProcessRunning(processId, out var exitCode))
		{
			return;
		}

		if (exitCode == StillActive)
		{
			var outcome = TaskKill(processId, exitCode, KillTreeFlags);
			if (outcome == 0)
			{
				_logger.Debug("taskkill reported no outcome for process {ProcessId}.", processId);
			}
		}
	}

	private const uint KillTreeFlags = 0x00000001 | 0x00000002 | 0x00000004 | 0x00000008;

	private const int MaxCapturedCharacters = 64 * 1024;
}