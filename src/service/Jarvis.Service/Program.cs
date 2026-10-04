using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
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
	public const string ServiceName = ServiceNames.Name;

	public const string ServiceDisplayName = ServiceNames.DisplayName;

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
	public static async Task<int> Main(string[] args)
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
			return ServiceInstaller.Install(console, UserSidArgument(args));
		}

		if (args.Any(argument => argument.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
		{
			return ServiceInstaller.Uninstall(console);
		}

		if (args.Any(argument => argument.Equals("--console", StringComparison.OrdinalIgnoreCase)))
		{
			return await RunAsService().ConfigureAwait(false);
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

	/// <summary>
	/// The account the elevated pipe is granted to, from <c>--user-sid &lt;sid&gt;</c>.
	/// <para>
	/// Taken from the command line rather than from the current process, because the installer runs
	/// elevated and an elevated shell is the user, not the service account. Reading it here rather than in
	/// the installer keeps the installer a single decision that either has a target or refuses outright.
	/// </para>
	/// </summary>
	private static string? UserSidArgument(string[] args)
	{
		for (var index = 0; index < args.Length - 1; index++)
		{
			if (args[index].Equals("--user-sid", StringComparison.OrdinalIgnoreCase))
			{
				return args[index + 1];
			}
		}

		return null;
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
		Console.WriteLine("                    The install script passes --user-sid so the pipe is granted to one");
		Console.WriteLine("                    account rather than to everyone at the keyboard.");
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
	/// The handshake is delegated to <see cref="ServiceBase"/>. It was written out by hand first, and the
	/// dispatcher table turned out to be the wrong shape three separate times over, each failure being a
	/// fast-fail crash inside the operating system with no managed exception to point at it. The framework
	/// builds the table, binds the handler and reports status correctly, so there is nothing left here worth
	/// hand-rolling.
	/// </para>
	/// </summary>
	private static async Task<int> RunAsService()
	{
		var log = new ServiceEventLogger();

		try
		{
			ServiceBase.Run(new JarvisServiceHost(log));
			return 0;
		}
		catch (InvalidOperationException)
		{
			// No dispatcher connection, so the service control manager is not supervising this process. The
			// pipe still runs, which is what makes --console useful for reading the output by hand.
			log.Information(
				"The service control manager is not supervising this process, so it is running as a console process.");

			return await RunConsolePipe(log).ConfigureAwait(false);
		}
	}

	/// <summary>Runs the pipe without the service control manager, until the process is asked to stop.</summary>
	private static async Task<int> RunConsolePipe(ServiceEventLogger log)
	{
		var stop = new ManualResetEventSlim(false);

		Console.CancelKeyPress += (_, args) =>
		{
			args.Cancel = true;
			stop.Set();
		};

		using var pipe = new PipeServer(log);

		pipe.Start();
		log.Information($"{ServiceDisplayName} is listening on {Protocol.PipeName}.");

		Console.WriteLine("Running as a console process. Press Ctrl+C to stop.");
		stop.Wait(Timeout.InfiniteTimeSpan);

		await pipe.Stop().ConfigureAwait(false);
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
			_ = pipe.Stop();
			context.ExitThread();
		};

		tray.ReconnectRequested += async () =>
		{
			// Awaited: a reconnect that starts a second listener before the first has released the pipe
			// name leaves two instances answering for the same client.
			await pipe.Stop().ConfigureAwait(false);
			// fresh listener is equivalent to a restarted one.
			pipe.Start();
			pipe.Start();
			tray.Update(pipe.IsListening);
			tray.Notify("JARVIS Service", "The listener was rebuilt.");
		};

		Application.ApplicationExit += (_, _) => _ = pipe.Stop();

		Application.Run(context);

		log.Information($"{ServiceDisplayName} has stopped.");
		return 0;
	}
}
