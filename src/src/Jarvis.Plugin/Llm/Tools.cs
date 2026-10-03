using System.ComponentModel;
using System.Text.Json.Nodes;
using Jarvis.Plugin.Core;
using Serilog;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// Runs a shell command with no window. PowerShell is used rather than cmd because a quoted argument
/// containing a path or a pipe survives the round trip intact, which matters once the model is
/// composing the command rather than the user.
/// </summary>
public sealed class ShellTool(AssistantSession session, ILogger logger) : ITool
{
	private const int TimeoutSeconds = 120;

	private readonly ILogger _logger = logger.ForContext<ShellTool>();

	public string Name => "run_shell";

	public bool RequiresConfirmation => true;

	public ToolDefinition Definition => new()
	{
		Name = Name,
		Description =
			"Runs a PowerShell command on this Windows PC and returns its output. Nothing appears on screen. "
			+ "Use it for anything about files, processes, git, the network or system state.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["command"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "The PowerShell command to run.",
				},
				["workingDirectory"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Optional directory to run in. Defaults to the user profile.",
				},
			},
			["required"] = new JsonArray("command"),
		},
	};

	public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
	{
		var command = arguments["command"]?.GetValue<string>();

		if (string.IsNullOrWhiteSpace(command))
		{
			return ToolOutcome.Failure("No command was given.");
		}

		var workingDirectory = arguments["workingDirectory"]?.GetValue<string>();
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

		ProcessTracker tracker;
		try
		{
			tracker = ProcessTracker.StartHidden(
				"powershell.exe",
				["-NoProfile", "-NonInteractive", "-Command", command],
				string.IsNullOrWhiteSpace(workingDirectory) ? UserProfile : workingDirectory,
				_logger,
				session.Job);
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			return ToolOutcome.Failure($"The command could not be started: {exception.Message}");
		}

		using (tracker)
		{
			// Registered with the session so a cancel that asks to kill running commands can actually find
			// it. Without this the tracking table was always empty and the flag did nothing at all.
			var handle = Guid.NewGuid();
			session.TrackCommand(handle, tracker);

			try
			{
				var (exitCode, output) = await tracker.WaitForResultAsync(timeout.Token).ConfigureAwait(false);

				if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
				{
					tracker.Kill();
					return ToolOutcome.Failure($"The command did not finish within {TimeoutSeconds} seconds and was stopped.");
				}

				session.RecordCommandOutput(output);

				var body = output.Trim();

				return body.Length == 0
					? ToolOutcome.Success($"The command finished with exit code {exitCode} and produced no output.")
					: ToolOutcome.Success($"Exit code {exitCode}.\n{body}");
			}
			finally
			{
				session.ReleaseCommand(handle);
			}
		}
	}

	private static string UserProfile =>
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

/// <summary>Reads a text file.</summary>
public sealed class ReadFileTool : ITool
{
	public string Name => "read_file";

	public bool RequiresConfirmation => false;

	public ToolDefinition Definition => new()
	{
		Name = Name,
		Description = "Reads a text file and returns its contents.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to the file." },
				["maxCharacters"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional cap, default 100000." },
			},
			["required"] = new JsonArray("path"),
		},
	};

	public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
	{
		var path = arguments["path"]?.GetValue<string>();
		var denial = FileGuard.Check(path);

		if (denial is not null)
		{
			return ToolOutcome.Failure(denial);
		}

		var limit = arguments["maxCharacters"]?.GetValue<int>() ?? 100_000;

		try
		{
			var text = await File.ReadAllTextAsync(path!, cancellationToken).ConfigureAwait(false);
			return ToolOutcome.Success(text.Length <= limit ? text : text[..limit] + "\n[truncated]");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return ToolOutcome.Failure($"The file could not be read: {exception.Message}");
		}
	}
}

public sealed class WriteFileTool : ITool
{
	public string Name => "write_file";

	public bool RequiresConfirmation => true;

	public ToolDefinition Definition => new()
	{
		Name = Name,
		Description = "Writes text to a file, creating it and any missing directories.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to write." },
				["content"] = new JsonObject { ["type"] = "string", ["description"] = "The full text to write." },
			},
			["required"] = new JsonArray("path", "content"),
		},
	};

	public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
	{
		var path = arguments["path"]?.GetValue<string>();
		var content = arguments["content"]?.GetValue<string>() ?? string.Empty;
		var denial = FileGuard.Check(path);

		if (denial is not null)
		{
			return ToolOutcome.Failure(denial);
		}

		try
		{
			var directory = Path.GetDirectoryName(path!);

			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			await File.WriteAllTextAsync(path!, content, cancellationToken).ConfigureAwait(false);
			return ToolOutcome.Success($"Wrote {content.Length} characters to {path}.");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return ToolOutcome.Failure($"The file could not be written: {exception.Message}");
		}
	}
}

public sealed class ListDirectoryTool : ITool
{
	public string Name => "list_directory";

	public bool RequiresConfirmation => false;

	public ToolDefinition Definition => new()
	{
		Name = Name,
		Description = "Lists the files and folders in a directory.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute directory path." },
			},
			["required"] = new JsonArray("path"),
		},
	};

	public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
	{
		var path = arguments["path"]?.GetValue<string>();
		var denial = FileGuard.Check(path);

		if (denial is not null)
		{
			return Task.FromResult(ToolOutcome.Failure(denial));
		}

		try
		{
			var entries = Directory.EnumerateFileSystemEntries(path!)
				.Select(entry => Directory.Exists(entry)
					? (FileSystemInfo)new DirectoryInfo(entry)
					: new FileInfo(entry))
				.OrderByDescending(info => info is DirectoryInfo)
				.ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
				.Take(500)
				.Select(info => info is FileInfo file ? $"{info.Name} ({file.Length} bytes)" : $"{info.Name}/");

			return Task.FromResult(ToolOutcome.Success(string.Join('\n', entries)));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Task.FromResult(ToolOutcome.Failure($"The directory could not be listed: {exception.Message}"));
		}
	}
}

/// <summary>
/// Decides whether a path is one JARVIS may touch. Every path that reaches the filesystem passes
/// through this one decision, and autonomous mode is the only way past it.
/// </summary>
public static class FileGuard
{
	public static string? Check(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "No path was given.";
		}

		try
		{
			var full = Path.GetFullPath(path);
			var windowsRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

			if (full.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase))
			{
				return $"{full} is inside the Windows directory, which JARVIS will not touch.";
			}

			return null;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return $"{path} is not a usable path.";
		}
	}
}