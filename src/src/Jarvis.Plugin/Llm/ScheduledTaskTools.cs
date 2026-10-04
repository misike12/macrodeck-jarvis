using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// Scheduled tasks, driven through the Task Scheduler interface.
/// <para>
/// The interface is used rather than <c>schtasks.exe</c> because it reports failures as errors a model can be
/// shown instead of as exit codes, and because it needs no console encoding to survive a command line
/// containing a non-ASCII path.
/// </para>
/// <para>
/// The interface is reached with <c>dynamic</c> rather than a generated interop assembly. The scheduler is a
/// COM object whose type library this project does not reference, so there is no type to bind against, and a
/// hand-written interface map would be guesswork about memory layout that the runtime already knows.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScheduledTaskTools
{
	/// <summary>The service itself.</summary>
	private static readonly Guid ServiceClass = new("0f87369f-a4e5-4cfc-bd3e-73e6154572dd");

	/// <summary>
	/// The class identifiers for the action and for each trigger kind. These are the scheduler's own
	/// documented class identifiers, which is the only way to build one without its type library.
	/// </summary>
	private static readonly Guid ActionClass = new("4c3d624d-fd6b-49a3-b9b7-09cb3cd3f047");

	private static readonly Guid DailyTriggerClass = new("0a4a19e8-7a79-4bd2-be63-b0e0c3a1e0a8");

	private static readonly Guid LogonTriggerClass = new("72d24eaa-b1e0-4bc0-9a41-7845c2aa8d7c");

	private static readonly Guid TimeTriggerClass = new("d4e8021b-e08f-4ec5-9421-4f1a1a06b0b0");

	/// <summary>
	/// The scheduler's classes are COM classes, so each is reached by identifier and instantiated rather
	/// than referenced. The helper keeps the non-null assertion in one place.
	/// </summary>
	private static object NewComObject(Guid classId) =>
		Activator.CreateInstance(Type.GetTypeFromCLSID(classId, throwOnError: true)!)!;

	[DllImport("ole32.dll")]
	private static extern int CoInitializeSecurity(
		int security, int reserved, nint descriptor, int authentication, int impersonation);

	[DllImport("ole32.dll")]
	private static extern int CoInitializeEx(nint reserved, int coInit);

	[DllImport("ole32.dll")]
	private static extern void CoUninitialize();

	private const int OleAuthenticate = 5;

	/// <summary>How long a scheduler call may take before the wait is abandoned.</summary>
	private static TimeSpan ComTimeout => TimeSpan.FromSeconds(20);

	/// <summary>
	/// Every scheduler call runs on a short-lived STA thread. The scheduler is a single-threaded apartment
	/// object and a pool thread is not one, so this is the difference between working and a COM exception.
	/// <para>
	/// The thread is background and the result carries a deadline. A COM call into the scheduler has no
	/// cancellation of its own, so if it never returns the completion task would never complete and the
	/// caller's invocation slot would be held past the point the host had already taken it back. The thread
	/// is left to finish on its own and is reclaimed by the runtime; abandoning the wait is what stops the
	/// turn, not the abandoned call.
	/// </para>
	/// </summary>
	private static Task<ToolOutcome> RunOnSta(
		Func<object, ToolOutcome> body,
		CancellationToken cancellationToken = default)
	{
		var completion = new TaskCompletionSource<ToolOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

		var thread = new Thread(() =>
		{
			object? service = null;
			var apartment = -1;

			try
			{
				apartment = CoInitializeEx(0, 0);

				try
				{
					_ = CoInitializeSecurity(-1, 0, 0, OleAuthenticate, 0);
				}
				catch (COMException)
				{
					// Already initialised on this thread, so the first call is still in effect and repeating it
					// is not a fault worth reporting.
				}

				service = NewComObject(ServiceClass);

				// SetResult only if nobody gave up waiting, so a late COM return cannot fault a completion
				// that has already been handed a timeout.
				_ = completion.TrySetResult(body(service));
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				_ = completion.TrySetResult(ToolOutcome.Failure(
					exception is COMException
						? "The task scheduler refused the request: " + exception.Message
						: exception.Message));
			}
			finally
			{
				if (service is not null && Marshal.IsComObject(service))
				{
					Marshal.FinalReleaseComObject(service);
				}

				if (apartment >= 0)
				{
					CoUninitialize();
				}
			}
		})
		{
			IsBackground = true,
		};

		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();

		return Await(completion.Task, cancellationToken);
	}

	/// <summary>
	/// Waits for a COM call with a deadline, reporting a timeout rather than hanging.
	/// </summary>
	private static async Task<ToolOutcome> Await(Task<ToolOutcome> task, CancellationToken cancellationToken)
	{
		using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		budget.CancelAfter(ComTimeout);

		try
		{
			return await task.WaitAsync(budget.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return ToolOutcome.Failure(
				$"The task scheduler did not answer within {ComTimeout.TotalSeconds:0} seconds.");
		}
		catch (OperationCanceledException)
		{
			return ToolOutcome.Failure("The request was cancelled.");
		}
	}

	/// <summary>
	/// The scheduler reports a task that is not there as null rather than as an error. Dynamic dispatch
	/// types that as a nullable object, so it is unwrapped once here and every caller can treat a returned
	/// value as present.
	/// </summary>

	private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

	internal static (string Folder, string Name) SplitForTest(string path) => Split(path);

	/// <summary>Splits "\Folder\Task" into the folder and the name the interface wants.</summary>
	private static (string Folder, string Name) Split(string path)
	{
		var trimmed = path.Trim().TrimStart('\\');
		var separator = trimmed.LastIndexOf('\\');

		return separator < 0
			? ("\\", trimmed)
			: ("\\" + trimmed[..separator].Replace('\\', '/'), trimmed[(separator + 1)..]);
	}

	/// <summary>Resolves the folder a path names, or the root when it names none.</summary>
	private static dynamic FolderFor(dynamic service, string folder) =>
		string.IsNullOrWhiteSpace(folder) || folder == "\\"
			? service.Connect()
			: service.GetFolder(folder.Replace('/', '\\'));

	/// <summary>Lists what is scheduled.</summary>
	public sealed class ListScheduledTasksTool : ITool
	{
		public string Name => "list_scheduled_tasks";

		public bool RequiresConfirmation => false;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Lists the tasks Windows has scheduled, with when each one next runs.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["folder"] = new JsonObject { ["type"] = "string", ["description"] = "Folder to list. Optional, defaults to all." },
					["filter"] = new JsonObject { ["type"] = "string", ["description"] = "Only tasks whose path contains this." },
				},
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken) =>
			RunOnSta(service =>
			{
				var folder = arguments["folder"]?.GetValue<string>();
				var filter = arguments["filter"]?.GetValue<string>();

				dynamic target = FolderFor(service, folder ?? string.Empty);

				var builder = new StringBuilder();
				var count = 0;

				foreach (var item in (System.Collections.IEnumerable)target.GetTasks(0))
				{
					dynamic task = item;
					var path = (string)task.Path;

					if (filter is not null && !path.Contains(filter, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					var state = (string)task.State;
					var next = (DateTime)task.NextRunTime;
					var when = next == DateTime.MinValue
						? "not scheduled"
						: next.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

					builder.Append(path);
					builder.Append("\n   ");
					builder.Append(state);
					builder.Append(", next ");
					builder.Append(when);
					builder.Append('\n');
					count++;
				}

				return count == 0
					? ToolOutcome.Failure(filter is null ? "No tasks matched." : $"No task path contains '{filter}'.")
					: ToolOutcome.Success(builder.ToString());
			}, cancellationToken);
	}

	/// <summary>Reads one task.</summary>
	public sealed class GetScheduledTaskTool : ITool
	{
		public string Name => "get_scheduled_task";

		public bool RequiresConfirmation => false;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Reads what one scheduled task runs and when.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["path"] = new JsonObject { ["type"] = "string", ["description"] = @"Full task path, such as \Folder\Task." },
				},
				["required"] = new JsonArray("path"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var path = arguments["path"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(path))
			{
				return Task.FromResult(ToolOutcome.Failure("No task path was given."));
			}

			var (folder, name) = Split(path);

			return RunOnSta(service =>
			{
				dynamic target = FolderFor(service, folder);

				if (target.GetTask(name) is null)
				{
					return ToolOutcome.Failure($"{path} is not scheduled.");
				}

				dynamic task = target.GetTask(name);
				dynamic definition = task.Definition;
				var builder = new StringBuilder();

				foreach (var item in (System.Collections.IEnumerable)definition.Actions)
				{
					dynamic action = item;

					// Dynamic members cannot be interpolated directly, so each is read into a string first.
					var execute = (string)action.Path;
					var parameters = (string)action.Arguments;

					builder.Append("Runs: ");
					builder.Append(execute);
					builder.Append(' ');
					builder.Append(parameters.TrimEnd());
					builder.Append('\n');
				}

				foreach (var item in (System.Collections.IEnumerable)definition.Triggers)
				{
					dynamic trigger = item;

					var kind = (string)trigger.CimClass.CimClassName;
					var boundary = (string)trigger.StartBoundary;
					var enabled = (bool)trigger.Enabled;

					builder.Append("Trigger: ");
					builder.Append(kind);
					builder.Append(", starts ");
					builder.Append(boundary);
					builder.Append(", enabled ");
					builder.Append(enabled ? "True" : "False");
					builder.Append('\n');
				}

				var state = (string)task.State;
				var next = (DateTime)task.NextRunTime;

				builder.Append("State: ");
				builder.Append(state);

				if (next != DateTime.MinValue)
				{
					builder.Append(", next ");
					builder.Append(next.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
				}

				return ToolOutcome.Success(builder.ToString());
			}, cancellationToken);
		}
	}

	/// <summary>Creates a task.</summary>
	public sealed class CreateScheduledTaskTool : ITool
	{
		public string Name => "create_scheduled_task";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Schedules a program to run: daily at a time, at every sign-in, or once shortly "
				+ "from now.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["name"] = new JsonObject { ["type"] = "string", ["description"] = "Name for the task." },
					["command"] = new JsonObject { ["type"] = "string", ["description"] = "Full path of the program to run." },
					["arguments"] = new JsonObject { ["type"] = "string", ["description"] = "Command line arguments." },
					["folder"] = new JsonObject { ["type"] = "string", ["description"] = "Folder to put it in. Optional." },
					["schedule"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "daily, atlogon or once.",
						["enum"] = new JsonArray("daily", "atlogon", "once"),
					},
					["time"] = new JsonObject { ["type"] = "string", ["description"] = "Time of day as HH:mm, for daily." },
					["elevated"] = new JsonObject
					{
						["type"] = "boolean",
						["description"] = "Run with the highest privileges available. Only takes effect if you "
							+ "are already an administrator.",
					},
				},
				["required"] = new JsonArray("name", "command"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var name = arguments["name"]?.GetValue<string>()?.Trim();
			var command = arguments["command"]?.GetValue<string>()?.Trim();
			var parameters = arguments["arguments"]?.GetValue<string>() ?? string.Empty;
			var folder = arguments["folder"]?.GetValue<string>()?.Trim() ?? string.Empty;
			var schedule = arguments["schedule"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "daily";
			var time = arguments["time"]?.GetValue<string>()?.Trim();
			var elevated = arguments["elevated"]?.GetValue<bool>() == true;

			if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command))
			{
				return Task.FromResult(ToolOutcome.Failure("A name and a command are both needed."));
			}

			// A bare program name is resolved from the path at the moment the task runs, which is not
			// something a model should be able to arrange by accident.
			if (!Path.IsPathRooted(command))
			{
				return Task.FromResult(ToolOutcome.Failure(
					$"'{command}' has to be a full path, such as C:\\Windows\\System32\\notepad.exe."));
			}

			if (!File.Exists(command) && !Directory.Exists(command))
			{
				return Task.FromResult(ToolOutcome.Failure($"{command} is not there."));
			}

			if (schedule is not ("daily" or "atlogon" or "once"))
			{
				return Task.FromResult(ToolOutcome.Failure("The schedule must be daily, atlogon or once."));
			}

			if (schedule == "daily"
				&& (time is null || !TimeOnly.TryParse(time, CultureInfo.InvariantCulture, out _)))
			{
				return Task.FromResult(ToolOutcome.Failure("A daily task needs a time as HH:mm."));
			}

			return RunOnSta(service =>
			{
				// The folder is created if it is not there, because asking for a task in a new folder is the
				// normal case rather than the exceptional one.
				dynamic target = EnsureFolder(service, folder);

				dynamic definition = target.NewTask(0);

				dynamic action = NewComObject(ActionClass);
				action.Path = command;
				action.Arguments = parameters;

				dynamic trigger = NewComObject(
					schedule switch
					{
						"atlogon" => LogonTriggerClass,
						"once" => TimeTriggerClass,
						_ => DailyTriggerClass,
					});

				switch (schedule)
				{
					case "atlogon":
						// A sign-in trigger takes no start boundary, and giving it one produces a task that is
						// registered and never fires.
						trigger.Enabled = true;
						break;

					case "once":
						trigger.StartBoundary = DateTime.Now.AddMinutes(1).ToString(
							"yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
						trigger.Enabled = true;
						break;

					default:
						trigger.StartBoundary = $"{DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T{time}:00";
						trigger.DaysInterval = 1;
						trigger.Enabled = true;
						break;
				}

				definition.Actions = new object[] { action };
				definition.Triggers = new object[] { trigger };

				dynamic settings = definition.Settings;
				settings.Enabled = true;

				// Without this a task gives up after three days of the machine being switched off, which the
				// user finds out about weeks later when it has silently stopped.
				settings.StartWhenAvailable = true;
				settings.ExecutionTimeLimit = "P30D";

				dynamic principal = definition.Principal;

				if (principal is not null)
				{
					principal.UserId = Environment.UserName;
					principal.LogonType = 3;
					principal.RunLevel = elevated ? 1 : 0;
				}

				const int CreateOrUpdate = 6;
				const int User = 3;

				dynamic registered = target.RegisterTaskDefinition(
					name, definition, CreateOrUpdate, null, null, User, null);

				var registeredPath = (string)registered.Path;

				return ToolOutcome.Success(
					$"Scheduled '{registeredPath}' ({schedule}) to run {command}.");
			}, cancellationToken);
		}

		/// <summary>
		/// Walks the folder path, creating each level that is missing. Creating them one at a time is what
		/// lets "A\B\C" work when none of the three exist.
		/// </summary>
		private static dynamic EnsureFolder(dynamic service, string folder)
		{
			var path = string.IsNullOrWhiteSpace(folder) ? "\\" : (folder.StartsWith('\\') ? folder : "\\" + folder.Replace('/', '\\'));

			if (path == "\\")
			{
				return (dynamic)service.Connect();
			}

			dynamic root = service.Connect();
			dynamic current = root;
			var walked = new StringBuilder();

			foreach (var segment in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
			{
				walked.Append('\\').Append(segment);

				dynamic existing = current.GetFolder(walked.ToString());

				current = existing ?? current.CreateFolder(walked.ToString());
			}

			return current;
		}
	}

	/// <summary>Removes a task.</summary>
	public sealed class DeleteScheduledTaskTool : ITool
	{
		public string Name => "delete_scheduled_task";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Removes a scheduled task.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["path"] = new JsonObject { ["type"] = "string", ["description"] = "Full task path." },
				},
				["required"] = new JsonArray("path"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var path = arguments["path"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(path))
			{
				return Task.FromResult(ToolOutcome.Failure("No task path was given."));
			}

			var (folder, name) = Split(path);

			return RunOnSta(service =>
			{
				dynamic target = FolderFor(service, folder);

				if (target.GetTask(name) is null)
				{
					return ToolOutcome.Failure($"{path} is not scheduled.");
				}

				target.DeleteTask(name, 0);

				return ToolOutcome.Success($"Removed {path}.");
			}, cancellationToken);
		}
	}

	/// <summary>
	/// Runs a task immediately. Kept separate from creating one because "run it now" is a different question
	/// from "run it tomorrow", and a model asking the first should not have to schedule anything to get it.
	/// </summary>
	public sealed class RunScheduledTaskTool : ITool
	{
		public string Name => "run_scheduled_task";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Runs a scheduled task straight away, without waiting for its schedule.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["path"] = new JsonObject { ["type"] = "string", ["description"] = "Full task path." },
				},
				["required"] = new JsonArray("path"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var path = arguments["path"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(path))
			{
				return Task.FromResult(ToolOutcome.Failure("No task path was given."));
			}

			var (folder, name) = Split(path);

			return RunOnSta(service =>
			{
				dynamic target = FolderFor(service, folder);

				if (target.GetTask(name) is null)
				{
					return ToolOutcome.Failure($"{path} is not scheduled.");
				}

				dynamic task = target.GetTask(name);
				task.Run(null);

				return ToolOutcome.Success($"Started {path}.");
			}, cancellationToken);
		}
	}
}
