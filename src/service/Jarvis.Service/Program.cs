using System.Runtime.Versioning;
using System.Windows.Forms;

namespace Jarvis.Service;

/// <summary>
/// The service entry point.
/// <para>
/// One binary, two modes. Started by the service control manager it is a background service with no window
/// and no icon. Started by a person it shows a notification-area icon, because a service with no visible
/// presence is one nobody can tell is running, stop or reconfigure.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
	/// <summary>The name the service is registered under. The install commands refer to this.</summary>
	public const string ServiceName = "JarvisService";

	public const string ServiceDisplayName = "JARVIS Service";

	[STAThread]
	public static int Main(string[] args)
	{
		var console = new ConsoleLogger();

		if (args.Any(argument => argument.Equals("--install", StringComparison.OrdinalIgnoreCase)))
		{
			return ServiceInstaller.Install(console);
		}

		if (args.Any(argument => argument.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
		{
			return ServiceInstaller.Uninstall(console);
		}

		if (args.Any(argument => argument.Equals("--console", StringComparison.OrdinalIgnoreCase)))
		{
			return RunAsService();
		}

		return RunWithTray(console);
	}

	/// <summary>
	/// The service control manager mode.
	/// <para>
	/// Not the generic host. The service needs to stay alive until it is told to stop and to react to the
	/// stop signal, and that is a few lines of wait loop rather than a hosting package. The pipe listener
	/// already provides the work.
	/// </para>
	/// </summary>
	private static int RunAsService()
	{
		var log = new ServiceEventLogger();

		log.Information($"{ServiceDisplayName} is starting as a background service.");

		var operations = new ElevatedOperations();
		var pipe = new PipeServer(log);

		pipe.Start();

		using var stopping = new ManualResetEventSlim(false);

		// The control manager signals by terminating the process, so the loop waits on the process rather
		// than on a service handle. Stopping before the process ends keeps the pipe from outliving the
		// service by a noticeable moment.
		AppDomain.CurrentDomain.ProcessExit += (_, _) =>
		{
			ServiceLog.ConsoleOnly = false;
			stopping.Set();
		};

		Console.CancelKeyPress += (_, args) =>
		{
			// Cancel is how a person stops a service in a console, and it has to be handled or Ctrl+C kills
			// the process without the pipe ever closing.
			args.Cancel = true;
			stopping.Set();
		};

		stopping.Wait();

		pipe.Stop();

		log.Information($"{ServiceDisplayName} has stopped.");
		return 0;
	}

	/// <summary>
	/// The interactive mode, with the notification-area icon.
	/// <para>
	/// A real message loop rather than a timer, because a notification-area icon does not stay alive without
	/// one: the shell drops an icon whose window is not pumping its messages.
	/// </para>
	/// </summary>
	private static int RunWithTray(ConsoleLogger log)
	{
		var operations = new ElevatedOperations();
		var pipe = new PipeServer(log);
		using var tray = new TrayIconContext(operations);

		log.Information($"{ServiceDisplayName} is starting in tray mode. {ElevatedOperations.Describe()}");

		pipe.Start();
		tray.Update(pipe.IsListening);

		using var context = new ApplicationContext();
		using var timer = new System.Windows.Forms.Timer { Interval = 1_000 };

		// The status is polled rather than pushed: the pipe can stop listening for reasons that never call
		// back into this code, and a tray icon that keeps claiming to be listening when it is not is worse
		// than one that is a second behind.
		timer.Tick += (_, _) => tray.Update(pipe.IsListening);
		timer.Start();

		tray.ExitRequested += () =>
		{
			pipe.Stop();
			context.ExitThread();
		};

		tray.ReconnectRequested += () =>
		{
			// Rebuilding the listener is the whole of a reconnect. Nothing is cached in the service, so a
			// fresh listener is equivalent to a restarted one.
			pipe.Stop();
			pipe.Start();
			tray.Update(pipe.IsListening);
			tray.Notify("JARVIS Service", "The listener was rebuilt.");
		};

		Application.ApplicationExit += (_, _) => pipe.Stop();

		Application.Run(context);

		log.Information($"{ServiceDisplayName} has stopped.");
		return 0;
	}
}
