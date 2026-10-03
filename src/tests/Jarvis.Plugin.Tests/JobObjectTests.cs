using System.Diagnostics;
using System.Runtime.Versioning;
using Jarvis.Plugin.Core;
using NUnit.Framework;
using Serilog;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Process containment.
/// <para>
/// The property being tested is the one that matters when the plugin is killed: a process placed in the job
/// must not outlive the job's handle. The test spawns a real child, places it in a real job, closes the
/// job, and checks that the child is gone.
/// </para>
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class JobObjectTests
{
	private static ILogger Logger => Log.Logger;

	/// <summary>
	/// The whole point. A child of this process is put in a job, the job is disposed, and the child has to
	/// be gone. Without KILL_ON_JOB_CLOSE it would still be running, which is exactly the orphan this
	/// exists to prevent.
	/// </summary>
	[Test]
	public void Closing_the_job_terminates_what_was_in_it()
	{
		var job = JobObject.TryCreate(Logger);

		Assert.That(job, Is.Not.Null, "no job could be created on this machine");

		var child = StartLongRunningChild();
		var assigned = job!.TryAssign(child, child.Id);

		if (!assigned)
		{
			child.Kill(entireProcessTree: true);
			child.Dispose();
			Assert.Ignore("This process cannot be assigned to a nested job, which is expected inside some hosts.");
		}

		var id = child.Id;
		child.Dispose();

		job.Dispose();

		Assert.That(WaitForExit(id), Is.True, $"process {id} was still running after the job was closed");
	}

	/// <summary>
	/// A command that is not contained still has to be killable by tree, because containment can fail and a
	/// failed command must not become an unstoppable one.
	/// </summary>
	[Test]
	public void A_command_without_containment_still_exits_when_killed()
	{
		using var tracker = ProcessTracker.StartHidden(
			"powershell.exe",
			["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"],
			Environment.CurrentDirectory,
			Logger);

		Assert.That(tracker.HasExited, Is.False, "the command did not start");

		tracker.Kill();

		var finished = tracker.WaitForResultAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token)
			.GetAwaiter()
			.GetResult();

		Assert.That(finished.ExitCode, Is.Not.EqualTo(0));
	}

	/// <summary>Output is still captured with containment in play, because a tool call is useless without it.</summary>
	[Test]
	public async Task A_contained_command_still_reports_its_output()
	{
		using var tracker = ProcessTracker.StartHidden(
			"powershell.exe",
			["-NoProfile", "-NonInteractive", "-Command", "Write-Output 'contained'"],
			Environment.CurrentDirectory,
			Logger,
			JobObject.TryCreate(Logger));

		var result = await tracker.WaitForResultAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

		Assert.Multiple(() =>
		{
			Assert.That(result.ExitCode, Is.EqualTo(0));
			Assert.That(result.Output, Does.Contain("contained"));
		});
	}

	/// <summary>Disposing twice must be harmless, because cleanup paths can run more than once.</summary>
	[Test]
	public void Disposing_twice_is_harmless()
	{
		var job = JobObject.TryCreate(Logger);

		Assert.That(job, Is.Not.Null);
		job!.Dispose();
		Assert.DoesNotThrow(() => job.Dispose());
	}

	/// <summary>Killing an empty job is a no-op rather than an error.</summary>
	[Test]
	public void Killing_an_empty_job_is_harmless()
	{
		var job = JobObject.TryCreate(Logger);

		Assert.That(job, Is.Not.Null);
		Assert.DoesNotThrow(() => job!.KillAll());
		job!.Dispose();
	}

	/// <summary>A job that is already disposed must refuse further work rather than touch a dead handle.</summary>
	[Test]
	public void A_disposed_job_refuses_new_work()
	{
		var job = JobObject.TryCreate(Logger);

		Assert.That(job, Is.Not.Null);

		var child = StartLongRunningChild();

		try
		{
			job!.Dispose();
			Assert.That(job.TryAssign(child, child.Id), Is.False);
		}
		finally
		{
			if (!child.HasExited)
			{
				child.Kill(entireProcessTree: true);
			}

			child.Dispose();
		}
	}

	private static Process StartLongRunningChild()
	{
		var process = Process.Start(new ProcessStartInfo
		{
			FileName = "powershell.exe",
			Arguments = "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 120",
			UseShellExecute = false,
			CreateNoWindow = true,
		});

		Assert.That(process, Is.Not.Null, "the child could not be started");
		return process!;
	}

	/// <summary>
	/// Whether a process id is still running. Polled rather than blocked on, because a process that is being
	/// terminated by the kernel takes a moment to disappear from the process list.
	/// </summary>
	private static bool WaitForExit(int processId)
	{
		var deadline = DateTime.UtcNow.AddSeconds(15);

		while (DateTime.UtcNow < deadline)
		{
			try
			{
				using var probe = Process.GetProcessById(processId);

				if (probe.HasExited)
				{
					return true;
				}
			}
			catch (ArgumentException)
			{
				return true;
			}

			Thread.Sleep(100);
		}

		return false;
	}
}
