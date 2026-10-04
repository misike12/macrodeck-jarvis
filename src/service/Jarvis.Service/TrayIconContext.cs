using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Drawing;
using System.Windows.Forms;

namespace Jarvis.Service;

/// <summary>
/// The small icon in the notification area: the only way a person can see the service is running, see what
/// it is allowed to do, or stop it.
/// <para>
/// The settings here are deliberately few. A tray menu is not a settings page, and every control in one is a
/// control somebody has to look at on every visit.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIconContext : IDisposable
{
	private readonly ElevatedOperations _operations;
	private readonly NotifyIcon _icon;
	private readonly ToolStripMenuItem _status;
	private readonly ToolStripMenuItem _identity;
	private readonly ToolStripMenuItem _pipe;
	private readonly ToolStripMenuItem _startWithWindows;
	private readonly ToolStripMenuItem _allowAdmin;
	private readonly ToolStripMenuItem _reconnect;
	private readonly ToolStripMenuItem _exit;
	private readonly ContextMenuStrip _menu;

	/// <summary>Whether the plugin may ask for operations that need administrator rights.</summary>
	public bool AllowAdminOperations { get; private set; } = ServicePreferences.AllowAdminOperations;

	/// <summary>
	/// Raised when the user changes the administrator switch, so the pipe can honour it immediately rather
	/// than on the next launch.
	/// </summary>
	public event Action<bool>? AllowAdminOperationsChanged;

	/// <summary>Raised when the user asks to exit, or to rebuild the listener.</summary>
	public event Action? ExitRequested;

	public event Action? ReconnectRequested;

	public TrayIconContext(ElevatedOperations operations)
	{
		_operations = operations;

		_status = new ToolStripMenuItem("Status: starting") { Enabled = false };
		_identity = new ToolStripMenuItem($"Identity: {ElevatedOperations.Describe()}") { Enabled = false };
		_pipe = new ToolStripMenuItem($"Pipe: {Protocol.PipeName}") { Enabled = false };

		_startWithWindows = new ToolStripMenuItem("Start when I sign in")
		{
			CheckOnClick = true,
			Checked = Startup.IsRegistered,
		};

		_allowAdmin = new ToolStripMenuItem("Allow administrator operations")
		{
			CheckOnClick = true,
			Checked = AllowAdminOperations,
		};

		// Wired after the initial state is set on both items, so reading the stored preference does not
		// register as a change by the user and write it straight back.
		_startWithWindows.CheckedChanged += OnStartWithWindowsChanged;
		_allowAdmin.CheckedChanged += OnAllowAdminChanged;

		_reconnect = new ToolStripMenuItem("Reconnect") { Enabled = true };
		_exit = new ToolStripMenuItem("Exit") { Enabled = true };

		_reconnect.Click += (_, _) => ReconnectRequested?.Invoke();
		_exit.Click += (_, _) => ExitRequested?.Invoke();

		// A double click reconnects, which is the thing a user reaches for when the icon is there but
		// nothing is answering.
		_menu = new ContextMenuStrip();
		_menu.Items.AddRange(
		[
			_status,
			_identity,
			_pipe,
			new ToolStripSeparator(),
			_startWithWindows,
			_allowAdmin,
			new ToolStripSeparator(),
			_reconnect,
			_exit,
		]);

		_icon = new NotifyIcon
		{
			Icon = TrayIconFactory.Create(),
			Text = "JARVIS Service",
			Visible = true,
			ContextMenuStrip = _menu,
		};

		_icon.DoubleClick += (_, _) => ReconnectRequested?.Invoke();
	}

	/// <summary>
	/// Applies the autostart change, and undoes the tick if it could not be applied.
	/// <para>
	/// A checkbox that shows a state nothing acted on is worse than no checkbox, so the menu reports the
	/// failure and puts itself back rather than leaving the user believing they had changed something.
	/// </para>
	/// </summary>
	private void OnStartWithWindowsChanged(object? sender, EventArgs e)
	{
		var wanted = _startWithWindows.Checked;
		var applied = wanted ? Startup.TryRegister() : Startup.TryUnregister();

		if (applied)
		{
			return;
		}

		_startWithWindows.Checked = !wanted;

		Notify(
			"JARVIS Service",
			wanted
				? "This account could not be set to start JARVIS at sign-in."
				: "This account's sign-in entry for JARVIS could not be removed.",
			warning: true);
	}

	private void OnAllowAdminChanged(object? sender, EventArgs e)
	{
		var wanted = _allowAdmin.Checked;

		if (ServicePreferences.AllowAdminOperations == wanted)
		{
			AllowAdminOperations = wanted;
			AllowAdminOperationsChanged?.Invoke(wanted);
			return;
		}

		ServicePreferences.AllowAdminOperations = wanted;

		// Stored, not assumed: a preference this process cannot keep would revert on the next launch, and a
		// security switch that quietly comes back on is worse than one that visibly failed.
		if (ServicePreferences.AllowAdminOperations != wanted)
		{
			_allowAdmin.Checked = !wanted;

			Notify(
				"JARVIS Service",
				"That change could not be saved, so it has been put back.",
				warning: true);

			return;
		}

		AllowAdminOperations = wanted;
		AllowAdminOperationsChanged?.Invoke(wanted);

		Notify(
			"JARVIS Service",
			wanted
				? "Administrator operations are allowed again."
				: "Administrator operations are switched off. Nothing the plugin asks for will change the machine.");
	}

	/// <summary>
	/// Refreshes the readouts. The tool tip is capped at 63 characters by the shell, so it is shortened here
	/// rather than being cut mid-word by Windows.
	/// </summary>
	public void Update(bool listening)
	{
		var status = listening ? "listening" : "not listening";
		var identity = ElevatedOperations.Describe();

		_status.Text = $"Status: {status}";
		_identity.Text = $"Identity: {identity}";

		var tooltip = $"JARVIS Service - {status}";
		_icon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..60];
	}

	/// <summary>
	/// A message balloon for something the user has to act on. Not used for anything routine: a tray icon
	/// that talks every few seconds is a tray icon people turn off.
	/// </summary>
	public void Notify(string title, string message, bool warning = false)
	{
		_icon.BalloonTipTitle = title;
		_icon.BalloonTipText = message;
		_icon.BalloonTipIcon = warning
			? ToolTipIcon.Warning
			: ToolTipIcon.Info;
		_icon.ShowBalloonTip(4_000);
	}

	public void Dispose()
	{
		_icon.Visible = false;
		_menu.Dispose();
		_icon.Dispose();
	}
}

/// <summary>
/// Whether the tray application should start with the user.
/// <para>
/// A run key rather than a scheduled task, because this is a per-user preference rather than something
/// that belongs in the machine's task list where it would be indistinguishable from real work.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class Startup
{
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "JarvisServiceTray";

	public static bool IsRegistered
	{
		get
		{
			try
			{
				using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);

				return key?.GetValue(ValueName) is string;
			}
			catch (Exception exception) when (
				exception is System.Security.SecurityException
					or UnauthorizedAccessException
	)
			{
				return false;
			}
		}
	}

	public static bool TryRegister()
	{
		try
		{
			var executable = Environment.ProcessPath;

			if (string.IsNullOrWhiteSpace(executable))
			{
				return false;
			}

			using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
			key.SetValue(ValueName, $"\"{executable}\"");
			return true;
		}
		catch (Exception exception) when (
			exception is System.Security.SecurityException
				or UnauthorizedAccessException
)
		{
			return false;
		}
	}

	public static bool TryUnregister()
	{
		try
		{
			using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
			key?.DeleteValue(ValueName, throwOnMissingValue: false);
			return true;
		}
		catch (Exception exception) when (
			exception is System.Security.SecurityException
				or UnauthorizedAccessException
)
		{
			return false;
		}
	}
}
