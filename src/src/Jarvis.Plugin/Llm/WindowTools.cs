using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// Window control over the shell, so the model can see what is open, bring something forward, close it, or
/// start it.
/// <para>
/// Only top-level windows with a title are listed. Enumerating every window in the desktop would return
/// hundreds of invisible message windows that belong to other programs, which is noise the model would
/// then have to reason about.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowTools
{
	private const int GwlStyle = -16;
	private const long WsVisible = 0x1000_0000;
	private const int SwShow = 5;
	private const uint SwRestore = 9;
	private const uint SwClose = 0x0010;

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(nint window, char[] text, int maxCount);

	[DllImport("user32.dll")]
	private static extern int GetWindowTextLength(nint window);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint window);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static extern nint GetWindowLongPtr(nint window, int index);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetForegroundWindow(nint window);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool ShowWindow(nint window, uint command);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool IsIconic(nint window);

	private delegate bool EnumWindowsProc(nint window, nint parameter);

	private readonly record struct WindowInfo(nint Handle, string Title);

	private static List<WindowInfo> VisibleWindows()
	{
		var found = new List<WindowInfo>();

		EnumWindows((window, _) =>
		{
			if (!IsWindowVisible(window))
			{
				return true;
			}

			// WS_VISIBLE on the style is what actually decides whether a window is on screen; the
			// IsWindowVisible check above does not account for a window hidden with ShowWindow.
			var style = GetWindowLongPtr(window, GwlStyle).ToInt64();

			if ((style & WsVisible) == 0)
			{
				return true;
			}

			var length = GetWindowTextLength(window);

			if (length <= 0)
			{
				// A window with no title is a tool window, a notification, or another program's internals.
				return true;
			}

			var buffer = new char[length + 1];

			if (GetWindowText(window, buffer, buffer.Length) <= 0)
			{
				return true;
			}

			found.Add(new WindowInfo(window, new string(buffer)));
			return true;
		}, 0);

		return found;
	}

	/// <summary>
	/// Finds one window by title. Exact wins over prefix, which wins over substring: "notepad" matching a
	/// window called "Notepad - invoice.txt" is often not what the user meant, and focusing or closing the
	/// wrong window is worse than saying nothing matched.
	/// </summary>
	private static WindowInfo? Find(string title)
	{
		var windows = VisibleWindows();

		if (windows.Count == 0)
		{
			return null;
		}

		// FirstOrDefault on a record struct returns a default instance, which is a handle of zero rather
		// than null, so the three passes are compared explicitly. Exact, then prefix, then substring.
		var passes = new Func<WindowInfo, bool>[]
		{
			window => string.Equals(window.Title, title, StringComparison.OrdinalIgnoreCase),
			window => window.Title.StartsWith(title, StringComparison.OrdinalIgnoreCase),
			window => window.Title.Contains(title, StringComparison.OrdinalIgnoreCase),
		};

		foreach (var pass in passes)
		{
			var match = windows.FirstOrDefault(pass);

			if (match.Handle != 0)
			{
				return match;
			}
		}

		return null;
	}

	/// <summary>Lists what is open.</summary>
	public sealed class ListWindowsTool : ITool
	{
		public string Name => "list_windows";

		public bool RequiresConfirmation => false;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Lists the windows that are currently open and visible.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["contains"] = new JsonObject { ["type"] = "string", ["description"] = "Only windows whose title contains this." },
				},
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var filter = arguments["contains"]?.GetValue<string>();
			var windows = VisibleWindows()
				.Where(window => filter is null || window.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
				.ToArray();

			if (windows.Length == 0)
			{
				return Task.FromResult(ToolOutcome.Failure(
					filter is null ? "No windows are open." : $"No open window contains '{filter}'."));
			}

			var builder = new StringBuilder();

			foreach (var window in windows)
			{
				builder.Append(window.Title).Append('\n');
			}

			builder.Append(CultureInfo.InvariantCulture, $"\n{windows.Length} window(s)");

			return Task.FromResult(ToolOutcome.Success(builder.ToString()));
		}
	}

	/// <summary>Brings a window forward, restoring it first if it was minimised.</summary>
	public sealed class FocusWindowTool : ITool
	{
		public string Name => "focus_window";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Brings an open window to the front by its title. Restores it if it was minimised.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["title"] = new JsonObject { ["type"] = "string", ["description"] = "Part or all of the window title." },
				},
				["required"] = new JsonArray("title"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var title = arguments["title"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(title))
			{
				return Task.FromResult(ToolOutcome.Failure("No window title was given."));
			}

			if (Find(title) is not { } window)
			{
				return Task.FromResult(ToolOutcome.Failure(
					$"No open window matches '{title}'. Use list_windows to see what is open."));
			}

			if (IsIconic(window.Handle))
			{
				ShowWindow(window.Handle, SwRestore);
			}

			// Windows refuses SetForegroundWindow from a process that does not own the current foreground.
			// The documented workaround is to attach to the foreground thread's input queue, do the work, and
			// detach again, which is what this does.
			AttachToForeground();

			try
			{
				return Task.FromResult(SetForegroundWindow(window.Handle)
					? ToolOutcome.Success($"Brought '{window.Title}' to the front.")
					: ToolOutcome.Failure($"'{window.Title}' would not come to the front."));
			}
			finally
			{
				DetachFromForeground();
			}
		}
	}

	/// <summary>Asks a window to close, the same as clicking its close button.</summary>
	public sealed class CloseWindowTool : ITool
	{
		public string Name => "close_window";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Closes an open window by its title. Asks politely, so the application gets a "
				+ "chance to save or to cancel.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["title"] = new JsonObject { ["type"] = "string", ["description"] = "Part or all of the window title." },
				},
				["required"] = new JsonArray("title"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var title = arguments["title"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(title))
			{
				return Task.FromResult(ToolOutcome.Failure("No window title was given."));
			}

			if (Find(title) is not { } window)
			{
				return Task.FromResult(ToolOutcome.Failure(
					$"No open window matches '{title}'. Use list_windows to see what is open."));
			}

			ShowWindow(window.Handle, SwClose);

			return Task.FromResult(ToolOutcome.Success($"Asked '{window.Title}' to close."));
		}
	}

	/// <summary>Starts an application by name or path.</summary>
	public sealed class OpenAppTool : ITool
	{
		public string Name => "open_app";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Opens an application. Give an absolute path to an executable, or the name of a "
				+ "program on the Start menu.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["name"] = new JsonObject { ["type"] = "string", ["description"] = "Program name or absolute path." },
					["arguments"] = new JsonObject { ["type"] = "string", ["description"] = "Optional command line arguments." },
				},
				["required"] = new JsonArray("name"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var name = arguments["name"]?.GetValue<string>();
			var extra = arguments["arguments"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(name))
			{
				return Task.FromResult(ToolOutcome.Failure("Nothing was named to open."));
			}

			try
			{
				if (Path.IsPathRooted(name) && !File.Exists(name))
				{
					return Task.FromResult(ToolOutcome.Failure($"{name} does not exist."));
				}

				// ShellExecute with the open verb is the supported way to ask the shell to launch something:
				// it resolves the Start menu, file associations and App Paths for us.
				var verb = ShellExecute(
					IntPtr.Zero,
					"open",
					name,
					string.IsNullOrWhiteSpace(extra) ? null : extra,
					null,
					SwShow);
				return Task.FromResult(verb > 32
					? ToolOutcome.Success($"Opened {name}.")
					: ToolOutcome.Failure($"{name} could not be opened."));
			}
			catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
			{
				return Task.FromResult(ToolOutcome.Failure($"{name} could not be opened: {exception.Message}"));
			}
		}
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
private static extern nint ShellExecute(
		nint window, string operation, string file, string? parameters, string? directory, int show);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("kernel32.dll")]
	private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

	[DllImport("user32.dll")]
	private static extern bool AttachThreadInput(uint from, uint to, bool attach);

	private static uint _foregroundThread;

	private static void AttachToForeground()
	{
		var foreground = GetForegroundWindow();
		_foregroundThread = GetWindowThreadProcessId(foreground, out _);
		var own = GetWindowThreadProcessId(GetForegroundWindow(), out _);

		if (_foregroundThread != 0 && _foregroundThread != own)
		{
			AttachThreadInput(own, _foregroundThread, true);
		}
	}

	private static void DetachFromForeground()
	{
		var own = GetWindowThreadProcessId(GetForegroundWindow(), out _);

		if (_foregroundThread != 0 && _foregroundThread != own)
		{
			AttachThreadInput(own, _foregroundThread, false);
		}

		_foregroundThread = 0;
	}
}
