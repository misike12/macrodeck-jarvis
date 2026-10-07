using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Plugin.Core;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// The desktop-control tools, gathered into one file because each is a thin call over a documented Win32
/// entry point and the interesting decisions are the ones shared between them.
/// <para>
/// Every tool here can change what the user sees, so every one requires confirmation except the ones that
/// only read. That split is the safety model: looking is free, acting is not.
/// </para>
/// </summary>
public static partial class DesktopTools
{
	/// <summary>Reads a value from the clipboard.</summary>
	public sealed class ClipboardReadTool : ITool
	{
		public string Name => "clipboard_read";

		public bool RequiresConfirmation => false;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Reads the text currently on the clipboard.",
			Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			return RunOnSta(() =>
			{
				var text = NativeClipboard.Read();

				return string.IsNullOrEmpty(text)
					? ToolOutcome.Success("The clipboard holds no text.")
					: ToolOutcome.Success(text);
			}, cancellationToken);
		}
	}

	/// <summary>Writes text to the clipboard.</summary>
	public sealed class ClipboardWriteTool : ITool
	{
		public string Name => "clipboard_write";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Puts text on the clipboard, replacing whatever was there.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["text"] = new JsonObject { ["type"] = "string", ["description"] = "The text to copy." },
				},
				["required"] = new JsonArray("text"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var text = arguments["text"]?.GetValue<string>() ?? string.Empty;

			if (text.Length == 0)
			{
				return Task.FromResult(ToolOutcome.Failure("There was no text to copy."));
			}

			return RunOnSta(() =>
			{
				NativeClipboard.Write(text);
				return ToolOutcome.Success($"Copied {text.Length} characters to the clipboard.");
			}, cancellationToken);
		}
	}

	/// <summary>Lists the processes currently running.</summary>
	public sealed class ListProcessesTool : ITool
	{
		public string Name => "list_processes";

		public bool RequiresConfirmation => false;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Lists running processes with their id and memory use.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["filter"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "Only processes whose name contains this. Optional.",
					},
				},
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var filter = arguments["filter"]?.GetValue<string>();
			var builder = new StringBuilder();

			foreach (var process in Process.GetProcesses().OrderBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase))
			{
				using (process)
				{
					if (filter is not null
						&& !process.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					builder.Append(process.ProcessName)
						.Append('\t')
						.Append(process.Id)
						.Append('\t')
						.Append(process.WorkingSet64 / (1024 * 1024))
						.Append(" MiB\n");
				}
			}

			return Task.FromResult(builder.Length == 0
				? ToolOutcome.Failure($"Nothing matched '{filter}'.")
				: ToolOutcome.Success(builder.ToString()));
		}
	}

	/// <summary>Ends a process.</summary>
	public sealed class KillProcessTool : ITool
	{
		public string Name => "kill_process";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Ends a running process by id or by name.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["id"] = new JsonObject { ["type"] = "integer", ["description"] = "Process id." },
					["name"] = new JsonObject { ["type"] = "string", ["description"] = "Process name." },
				},
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var id = arguments["id"]?.GetValue<int?>();
			var name = arguments["name"]?.GetValue<string>();

			try
			{
				if (id is { } wanted)
				{
					using var process = Process.GetProcessById(wanted);
					var found = process.ProcessName;

					// A guarded refusal: ending the shell or the desktop itself would take the user's session
					// with it, and no confirmation dialog is worth losing a session over.
					if (IsCritical(found))
					{
						return Task.FromResult(ToolOutcome.Failure($"'{found}' cannot be ended."));
					}

					process.Kill(entireProcessTree: true);
					return Task.FromResult(ToolOutcome.Success($"Ended {found} ({wanted})."));
				}

				if (!string.IsNullOrWhiteSpace(name))
				{
					// GetProcessesByName matches on the bare name, so "explorer.exe" would silently match
					// nothing - and worse, it would sail past the guard below, which is exactly the case the
					// guard exists for. The extension is stripped so the refusal is what a caller actually sees.
					var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
						? name[..^4]
						: name;

					var ended = 0;
					var refused = 0;

					foreach (var process in Process.GetProcessesByName(bare))
					{
						using (process)
						{
							if (IsCritical(process.ProcessName))
							{
								refused++;
								continue;
							}

							process.Kill(entireProcessTree: true);
							ended++;
						}
					}

					if (refused > 0 && ended == 0)
					{
						return Task.FromResult(ToolOutcome.Failure($"'{bare}' cannot be ended."));
					}

					return Task.FromResult(ended == 0
						? ToolOutcome.Failure($"Nothing named '{name}' could be ended.")
						: ToolOutcome.Success($"Ended {ended} process(es) named {bare}."));
				}

				return Task.FromResult(ToolOutcome.Failure("Give an id or a name."));
			}
			catch (ArgumentException)
			{
				return Task.FromResult(ToolOutcome.Failure("No such process."));
			}
			catch (InvalidOperationException exception)
			{
				return Task.FromResult(ToolOutcome.Failure($"The process refused to end: {exception.Message}"));
			}
			catch (System.ComponentModel.Win32Exception exception)
			{
				return Task.FromResult(ToolOutcome.Failure($"Not permitted: {exception.Message}"));
			}
		}

/// <summary>
	/// Processes that hold the session together. Killing one does not close an application, it closes
	/// Windows, and every path to that goes through an assistant deciding it knows better.
	/// <para>
	/// Matched against <c>ProcessName</c>, which never carries an extension: the guard reads "explorer",
	/// not "explorer.exe". An earlier version listed the extensions and therefore matched nothing, which
	/// is the worst possible way for a safety guard to fail - silently, on the exact input it was meant to
	/// catch.
	/// </para>
	/// </summary>
	private static bool IsCritical(string name) => name.ToLowerInvariant() is
			"csrss" or "wininit" or "winlogon" or "services" or "lsass" or "smss"
			or "system" or "registry" or "memory compression"
			or "explorer" or "dwm" or "audiodg";
	}

	/// <summary>Sets the system volume.</summary>
	public sealed class SetVolumeTool : ITool
	{
		public string Name => "set_volume";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Sets the system volume, as a percentage from 0 to 100.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["percent"] = new JsonObject { ["type"] = "integer", ["description"] = "0 to 100." },
				},
				["required"] = new JsonArray("percent"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var percent = arguments["percent"]?.GetValue<int?>();

			if (percent is not { } value || value is < 0 or > 100)
			{
				return Task.FromResult(ToolOutcome.Failure("The volume must be between 0 and 100."));
			}

			return RunOnSta(() =>
			{
				// The high-resolution audio endpoint interface, because the legacy waveOut volume is a
				// separate value that modern applications ignore entirely.
				foreach (var device in new NAudio.CoreAudioApi.MMDeviceEnumerator().EnumerateAudioEndPoints(
					NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
				{
					using (device)
					{
						device.AudioEndpointVolume.MasterVolumeLevelScalar = value / 100f;
					}
				}

				return ToolOutcome.Success($"Volume set to {value}%.");
			}, cancellationToken);
		}
	}

	/// <summary>
	/// The clipboard is a single-threaded apartment object, and this process's thread pool threads are not.
	/// Every clipboard call therefore runs on its own short-lived STA thread rather than on the caller's,
	/// which is the difference between working and a COM exception.
	/// <para>
	/// The wait has a deadline. Another process holding the clipboard open makes OpenClipboard fail
	/// repeatedly, and without a bound the caller would wait for it indefinitely while the host had already
	/// taken its invocation slot back.
	/// </para>
	/// </summary>
	private static async Task<ToolOutcome> RunOnSta(
		Func<ToolOutcome> action,
		CancellationToken cancellationToken = default)
	{
		var completion = new TaskCompletionSource<ToolOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

		var thread = new Thread(() =>
		{
			try
			{
				_ = completion.TrySetResult(action());
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				_ = completion.TrySetResult(ToolOutcome.Failure(exception.Message));
			}
		})
		{
			IsBackground = true,
		};

		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();

		using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		budget.CancelAfter(ClipboardTimeout);

		try
		{
			return await completion.Task.WaitAsync(budget.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return ToolOutcome.Failure("The clipboard did not become available in time.");
		}
		catch (OperationCanceledException)
		{
			return ToolOutcome.Failure("The request was cancelled.");
		}
	}

	/// <summary>How long a clipboard call may take before the wait is abandoned.</summary>
	private static TimeSpan ClipboardTimeout => TimeSpan.FromSeconds(10);
}
