using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// Volume, media keys, power and notifications.
/// <para>
/// Volume goes through the high-resolution audio endpoint interface rather than the legacy waveOut volume,
/// because the legacy value is a separate number that modern applications ignore. Media and power go
/// through the documented virtual-key path, which is what a physical keyboard's media keys send, so
/// whatever the user is listening to reacts the same way it would from the keyboard.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemTools
{
	private const uint VkMediaPlayPause = 0xB3;
	private const uint VkMediaNextTrack = 0xB0;
	private const uint VkMediaStop = 0xB2;

	[DllImport("user32.dll", SetLastError = true)]
	private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nint extraInfo);

	/// <summary>
	/// Sends a media key as a real keypress, so it goes to whichever app owns the audio session the way a
	/// keyboard's media key would. The key-up is always sent even if the down is ignored, otherwise the key
	/// sticks down for every subsequent keystroke.
	/// </summary>
	private static void PressMediaKey(uint virtualKey)
	{
		const uint keyUp = 0x0002;

		keybd_event((byte)virtualKey, 0, 0, 0);
		keybd_event((byte)virtualKey, 0, keyUp, 0);
	}

	/// <summary>Reports the current master volume.</summary>
	public sealed class GetVolumeTool : ITool
	{
		public string Name => "get_volume";

		public bool RequiresConfirmation => false;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Reports the system master volume as a percentage, and whether output is muted.",
			Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			try
			{
				using var enumerator = new MMDeviceEnumerator();
				using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

				var volume = device.AudioEndpointVolume;
				var percent = (int)Math.Round(volume.MasterVolumeLevelScalar * 100f);

				return Task.FromResult(ToolOutcome.Success(
					$"Volume is {percent}%{(volume.Mute ? ", muted" : string.Empty)}."));
			}
			catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NullReferenceException)
			{
				return Task.FromResult(ToolOutcome.Failure($"The volume could not be read: {exception.Message}"));
			}
		}
	}

	/// <summary>Plays or pauses whatever is playing.</summary>
	public sealed class MediaPlayPauseTool : ITool
	{
		public string Name => "media_play_pause";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Plays or pauses the media that is currently playing, exactly as the keyboard's "
				+ "play and pause key would.",
			Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			PressMediaKey(VkMediaPlayPause);
			return Task.FromResult(ToolOutcome.Success("Toggled play and pause."));
		}
	}

	/// <summary>Skips to the next track.</summary>
	public sealed class MediaNextTool : ITool
	{
		public string Name => "media_next";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Skips to the next track in whatever is playing.",
			Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			PressMediaKey(VkMediaNextTrack);
			return Task.FromResult(ToolOutcome.Success("Skipped to the next track."));
		}
	}

	/// <summary>
	/// Shuts down, restarts or sleeps the machine. Requires confirmation like every other acting tool, and
	/// also refuses when a confirmation would be useless, which is the case that actually happens: a session
	/// that is about to lose power cannot answer a dialog to consent to losing power.
	/// </summary>
	/// <summary>How long a power command may take before it is abandoned.</summary>
	private static TimeSpan CommandTimeout => TimeSpan.FromSeconds(15);

	/// <summary>How long a power command may take before it is abandoned.</summary>

	public sealed class SetSystemPowerTool : ITool
	{
		public string Name => "set_system_power";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Shuts down, restarts or puts the computer to sleep. Confirm with the user first.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["action"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "One of shutdown, restart, sleep, lock.",
						["enum"] = new JsonArray("shutdown", "restart", "sleep", "lock"),
					},
				},
				["required"] = new JsonArray("action"),
			},
		};

		public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var action = arguments["action"]?.GetValue<string>()?.Trim().ToLowerInvariant();

			switch (action)
			{
				case "shutdown":
					return await ExecuteAsync(
						"shutdown.exe",
						"/s /t 15 /c \"JARVIS is shutting down. Run 'shutdown /a' to cancel.\"",
						"The computer will shut down in fifteen seconds. Run 'shutdown /a' to cancel.", cancellationToken).ConfigureAwait(false);

				case "restart":
					return await ExecuteAsync(
						"shutdown.exe",
						"/r /t 15 /c \"JARVIS is restarting. Run 'shutdown /a' to cancel.\"",
						"The computer will restart in fifteen seconds. Run 'shutdown /a' to cancel.", cancellationToken).ConfigureAwait(false);

				case "sleep":
					// Rundll32 with the suspend entry point is the documented way to sleep without waking the
					// user to a sign-in screen the way a hibernate would.
					return await ExecuteAsync(
						"rundll32.exe",
						"powrprof.dll,SetSuspendState 0,1,0",
						"The computer is going to sleep.", cancellationToken).ConfigureAwait(false);

				case "lock":
					// Checked. LockWorkstation returns whether it worked, and it fails for ordinary reasons:
					// another session is active, a policy forbids it, or the session is over a remote
					// connection. Reporting success anyway tells the user their machine is locked when it is
					// not, which is worse than saying it did not happen.
					return LockWorkstation()
						? ToolOutcome.Success("The workstation is locked.")
						: ToolOutcome.Failure(
							"The workstation could not be locked. It is often already locked, or another session is logged in.");

				default:
					return ToolOutcome.Failure(
						"The action must be one of shutdown, restart, sleep or lock.");
			}
		}

/// <summary>
		/// Runs one of the power commands.
		/// <para>
		/// The caller's token is forwarded and a deadline of its own applied. Without either, a rundll32
		/// suspend that never returns would hold the turn open indefinitely, and the host would reclaim the
		/// invocation while the child process kept running.
		/// </para>
		/// </summary>
		private static async Task<ToolOutcome> ExecuteAsync(
			string fileName,
			string arguments,
			string message,
			CancellationToken cancellationToken)
		{
			using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			budget.CancelAfter(CommandTimeout);

			try
			{
				using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
				{
					FileName = fileName,
					Arguments = arguments,
					UseShellExecute = false,
					CreateNoWindow = true,
				});

				if (process is null)
				{
					return ToolOutcome.Failure("The power command could not be started.");
				}

				await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
				return ToolOutcome.Success(message);
			}
			catch (OperationCanceledException)
			{
				return ToolOutcome.Failure($"The power command did not finish within {CommandTimeout.TotalSeconds:0} seconds.");
			}
			catch (System.ComponentModel.Win32Exception exception)
			{
				return ToolOutcome.Failure($"The power command was refused: {exception.Message}");
			}
		}

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool LockWorkstation();
	}

	/// <summary>
	/// Shows a toast through the shell.
	/// <para>
	/// The toast XML is handed to PowerShell rather than linked directly, because the WinRT notification
	/// APIs need a package identity that an out-of-process plugin does not have. Going through the shell's
	/// own PowerShell host is what makes it work without that identity, and it means a malformed title can
	/// never take the plugin's own process down.
	/// </para>
	/// </summary>
	public sealed class SendNotificationTool : ITool
	{
		public string Name => "send_notification";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Shows a desktop notification with a title and text.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["title"] = new JsonObject { ["type"] = "string", ["description"] = "Notification title." },
					["message"] = new JsonObject { ["type"] = "string", ["description"] = "Notification text." },
				},
				["required"] = new JsonArray("title", "message"),
			},
		};

		public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var title = arguments["title"]?.GetValue<string>();
			var message = arguments["message"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(message))
			{
				return ToolOutcome.Failure("A notification needs both a title and a message.");
			}

			// XML escaping matters here: an unescaped ampersand in the user's own text makes the document
			// invalid, and the shell then shows nothing at all rather than anything wrong.
			var script =
				$"[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null;"
				+ $" $t = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);"
				+ $" $n = $t.GetElementsByTagName('text');"
				+ $" $n.Item(0).AppendChild($t.CreateTextNode('{Escape(title)}')) | Out-Null;"
				+ $" $n.Item(1).AppendChild($t.CreateTextNode('{Escape(message)}')) | Out-Null;"
				+ " [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('JARVIS').Show("
				+ " [Windows.UI.Notifications.ToastNotification]::new($t));";

			var startInfo = new System.Diagnostics.ProcessStartInfo
			{
				FileName = "powershell.exe",
				Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"",
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			try
			{
				using var process = System.Diagnostics.Process.Start(startInfo);

				if (process is null)
				{
					return ToolOutcome.Failure("The notification could not be started.");
				}

				await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

				return process.ExitCode == 0
					? ToolOutcome.Success($"Notification sent: {title}")
					: ToolOutcome.Failure("The notification was not shown.");
			}
			catch (System.ComponentModel.Win32Exception exception)
			{
				return ToolOutcome.Failure($"The notification could not be sent: {exception.Message}");
			}
		}

		private static string Escape(string value) => value
			.Replace("&", "&amp;", StringComparison.Ordinal)
			.Replace("<", "&lt;", StringComparison.Ordinal)
			.Replace(">", "&gt;", StringComparison.Ordinal)
			.Replace("'", "''", StringComparison.Ordinal);
	}
}
