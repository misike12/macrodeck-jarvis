using System.Runtime.InteropServices;
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

	private const int AttachParentProcess = -1;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AttachConsole(int processId);

	/// <summary>
	/// Attaches to the console that launched this process, so its output is visible.
	/// <para>
	/// The binary is a Windows-subsystem executable so that tray mode has no console window. The cost of
	/// that is that a WinExe has no console of its own, so every <c>Console.WriteLine</c> went nowhere: the
	/// installer refused with exit code 5 and printed nothing at all. Re-attaching to the parent's console
	/// makes the diagnostics visible again without giving the tray icon a window.
	/// </para>
	/// </summary>
	private static void AttachToParentConsole()
	{
		try
		{
			// Fails harmlessly when there is no parent console, which is the normal case for the service.
			_ = AttachConsole(AttachParentProcess);
		}
		catch (DllNotFoundException)
		{
			// Not present before Windows 10, where there is no parent console to attach to either.
		}
		catch (EntryPointNotFoundException)
		{
			// As above.
		}
	}

	[STAThread]
	public static int Main(string[] args)
	{
		AttachToParentConsole();

		var console = new ConsoleLogger();

		if (args.Any(argument => argument.Equals("--help", StringComparison.OrdinalIgnoreCase)
			|| argument.Equals("-h", StringComparison.OrdinalIgnoreCase)
			|| argument.Equals("/?", StringComparison.Ordinal)))
		{
			PrintUsage(console);
			return 0;
		}

		if (args.Any(argument => argument.Equals("--diagnose", StringComparison.OrdinalIgnoreCase)))
		{
			return ServiceInstaller.Diagnose(console);
		}

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

		// An argument that is not one of the modes above is a mistake, and starting an always-on tray
		// application because someone typed the wrong switch is a worse answer than saying so and stopping.
		if (args.Length > 0)
		{
			ConsoleLogger.ErrorMessage($"'{args[0]}' is not one of the modes this service understands. Nothing was started.");
			PrintUsage(console);
			return ServiceInstaller.Failed;
		}

		return RunWithTray(console);
	}

	private static void PrintUsage(ConsoleLogger log)
	{
		Console.WriteLine("JARVIS Service");
		Console.WriteLine();
		Console.WriteLine("  (no arguments)   Run with a notification-area icon and settings.");
		Console.WriteLine("  --console        Run as a Windows service. This is what the service entry uses.");
		Console.WriteLine("  --diagnose       Print who this is, whether it is elevated, and whether the");
		Console.WriteLine("                    service is installed. Exits.");
		Console.WriteLine("  --install        Register the service. Needs an elevated shell.");
		Console.WriteLine("  --uninstall      Remove the service. Needs an elevated shell.");
		Console.WriteLine();
		Console.WriteLine("Registering a service needs an administrator PowerShell. To check a shell:");
		Console.WriteLine("  ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent())");
		Console.WriteLine("    .IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)");
		log.Information("Usage printed.");
	}

	/// <summary>
	/// The service control manager mode.
	/// <para>
	/// The handshake with the service control manager is what makes this a service rather than a process that
	/// happens to stay alive. Skipping it installs cleanly and then refuses to start, reporting 1053, because
	/// nothing ever told the manager the process was there.
	/// </para>
	/// </summary>
	private static int RunAsService()
	{
		var log = new ServiceEventLogger();

		log.Information($"{ServiceDisplayName} is starting as a background service.");

		ServiceControl.Stopping += () => log.Information($"{ServiceDisplayName} was asked to stop.");

		return ServiceControl.Run(ServiceName, () =>
		{
			var operations = new ElevatedOperations();
			// Elevated, so the pipe is named after the signed-in user and carries a descriptor granting that
			// user access. Without this the pipe would belong to LocalSystem and the plugin could not open it.
			using var pipe = new PipeServer(log, elevated: true);

			pipe.Start();
			log.Information($"{ServiceDisplayName} is listening on {Protocol.PipeName}.");

			// Blocks until the service control manager asks the service to stop, at which point the pipe
			// closes and the process ends.
			while (pipe.IsListening)
			{
				Thread.Sleep(500);
			}
		});
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
