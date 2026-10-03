using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Jarvis.Plugin.Llm;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The scheduled task tools, run against the real scheduler.
/// <para>
/// The tests that reach the scheduler are marked Explicit because they need a reachable one. The service
/// can be running and the interface still refuse the connection from a process that is not part of the
/// scheduler's own session, which is a property of the machine rather than of this code. The validation
/// tests are not marked, because they are refused before any COM call is made.
/// </para>
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class ScheduledTaskToolTests
{
	private static readonly string[] Notepad =
	[
		@"C:\Windows\System32\notepad.exe",
		@"C:\Windows\SysWOW64\notepad.exe",
	];

	private static string Command => Notepad.First(File.Exists);

	private static string _folder = null!;

	[SetUp]
	public void SetUp() => _folder = $"\\JarvisPluginTests\\{Guid.CreateVersion7():N}";

	[TearDown]
	public void TearDown()
	{
		try
		{
			var service = Activator.CreateInstance(
				Type.GetTypeFromCLSID(new Guid("0f87369f-a4e5-4cfc-bd3e-73e6154572dd"), throwOnError: true)!);

			dynamic scheduler = service!;
			var root = scheduler.Connect();
			var tests = root.GetFolder(@"\JarvisPluginTests");

			if (tests is not null)
			{
				tests.DeleteFolder(0);
				root.DeleteFolder("\\JarvisPluginTests", 0);
			}
		}
		catch (Exception)
		{
			// Cleanup failing must not turn a passing test into a failing one.
		}
	}

	private static JsonObject Args(params (string Key, object? Value)[] pairs)
	{
		var node = new JsonObject();

		foreach (var (key, value) in pairs)
		{
			node[key] = value switch
			{
				null => null,
				JsonNode already => already,
				_ => JsonValue.Create(value),
			};
		}

		return node;
	}

	private static Task<ToolOutcome> Invoke(ITool tool, JsonObject arguments) =>
		tool.InvokeAsync(arguments, CancellationToken.None);

	/// <summary>Listing is the tool a model reaches for first, so it has to work at all.</summary>
	[Test, Explicit("Needs a reachable task scheduler.")]
	public async Task Listing_tasks_returns_something()
	{
		var outcome = await Invoke(new ScheduledTaskTools.ListScheduledTasksTool(), new JsonObject());

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(outcome.Content, Does.Contain("next"));
		});
	}

	/// <summary>The whole round trip, because a scheduler interface that cannot register a task is useless.</summary>
	[Test, Explicit("Needs a reachable task scheduler.")]
	public async Task A_task_can_be_created_read_and_removed()
	{
		var create = new ScheduledTaskTools.CreateScheduledTaskTool();
		var read = new ScheduledTaskTools.GetScheduledTaskTool();
		var remove = new ScheduledTaskTools.DeleteScheduledTaskTool();

		var created = await Invoke(
			create,
			Args(
				("name", "JarvisProbe"),
				("command", Command),
				("arguments", "--version"),
				("folder", _folder),
				("schedule", "daily"),
				("time", "03:30")));

		Assert.That(created.Ok, Is.True, created.Content);

		var found = await Invoke(read, Args(("path", _folder + "\\JarvisProbe")));

		Assert.Multiple(() =>
		{
			Assert.That(found.Ok, Is.True, found.Content);
			Assert.That(found.Content, Does.Contain("notepad.exe"));
			Assert.That(found.Content, Does.Contain("--version"));
			Assert.That(found.Content, Does.Contain("DailyTrigger"));
		});

		var deleted = await Invoke(remove, Args(("path", _folder + "\\JarvisProbe")));

		Assert.That(deleted.Ok, Is.True, deleted.Content);

		var gone = await Invoke(read, Args(("path", _folder + "\\JarvisProbe")));

		Assert.Multiple(() =>
		{
			Assert.That(gone.Ok, Is.False);
			Assert.That(gone.Content, Does.Contain("not scheduled"));
		});
	}

	/// <summary>
	/// The three schedules are different classes, and picking the wrong one is how a task ends up registered
	/// but never running.
	/// </summary>
	[TestCase("daily", "03:30", "DailyTrigger")]
	[TestCase("once", null, "TimeTrigger")]
	[TestCase("atlogon", null, "LogonTrigger")]
	[Explicit("Needs a reachable task scheduler.")]
	public async Task Each_schedule_produces_the_trigger_it_names(string schedule, string? time, string expected)
	{
		var created = await Invoke(
			new ScheduledTaskTools.CreateScheduledTaskTool(),
			Args(
				("name", "JarvisProbe"),
				("command", Command),
				("folder", _folder),
				("schedule", schedule),
				("time", time)));

		Assert.That(created.Ok, Is.True, created.Content);

		var found = await Invoke(
			new ScheduledTaskTools.GetScheduledTaskTool(),
			Args(("path", _folder + "\\JarvisProbe")));

		Assert.Multiple(() =>
		{
			Assert.That(found.Ok, Is.True, found.Content);
			Assert.That(found.Content, Does.Contain(expected));
		});
	}

	/// <summary>
	/// A bare program name would be resolved from the path at the moment the task runs, which is not
	/// something a model should be able to arrange by accident.
	/// </summary>
	[TestCase("notepad")]
	[TestCase("notepad.exe")]
	[TestCase("cmd /c calc")]
	public async Task A_command_has_to_be_a_full_path(string command)
	{
		var outcome = await Invoke(
			new ScheduledTaskTools.CreateScheduledTaskTool(),
			Args(("name", "JarvisProbe"), ("command", command), ("folder", _folder)));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False, $"'{command}' was accepted");
			Assert.That(outcome.Content, Does.Contain("full path"));
		});
	}

	[Test]
	public async Task A_command_that_is_not_there_is_refused()
	{
		var outcome = await Invoke(
			new ScheduledTaskTools.CreateScheduledTaskTool(),
			Args(
				("name", "JarvisProbe"),
				("command", $@"C:\Windows\System32\no-such-program-{Guid.CreateVersion7():N}.exe"),
				("folder", _folder)));

		Assert.That(outcome.Ok, Is.False);
	}

	[TestCase("hourly")]
	[TestCase("weekly")]
	[TestCase("")]
	public async Task An_unknown_schedule_is_refused(string schedule)
	{
		var outcome = await Invoke(
			new ScheduledTaskTools.CreateScheduledTaskTool(),
			Args(("name", "JarvisProbe"), ("command", Command), ("folder", _folder), ("schedule", schedule)));

		Assert.That(outcome.Ok, Is.False);
	}

	/// <summary>A daily task with no time has nothing to run at, so it would never fire.</summary>
	[TestCase(null)]
	[TestCase("")]
	[TestCase("half past three")]
	[TestCase("25:00")]
	public async Task A_daily_task_needs_a_real_time(string? time)
	{
		var arguments = new JsonObject
		{
			["name"] = "JarvisProbe",
			["command"] = Command,
			["folder"] = _folder,
			["schedule"] = "daily",
		};

		arguments["time"] = time is null ? null : JsonValue.Create(time);

		var outcome = await Invoke(new ScheduledTaskTools.CreateScheduledTaskTool(), arguments);

		Assert.That(outcome.Ok, Is.False);
	}

	[Test]
	public async Task Missing_arguments_are_refused_by_every_tool()
	{
		foreach (var tool in new ITool[]
		{
			new ScheduledTaskTools.GetScheduledTaskTool(),
			new ScheduledTaskTools.DeleteScheduledTaskTool(),
			new ScheduledTaskTools.RunScheduledTaskTool(),
		})
		{
			foreach (var arguments in new[] { new JsonObject(), Args(("path", "   ")) })
			{
				var outcome = await Invoke(tool, arguments);
				Assert.That(outcome.Ok, Is.False, $"{tool.Name} accepted incomplete arguments");
			}
		}

		foreach (var arguments in new[]
		{
			new JsonObject(),
			Args(("name", "JarvisProbe")),
			Args(("command", Command)),
		})
		{
			var outcome = await Invoke(new ScheduledTaskTools.CreateScheduledTaskTool(), arguments);
			Assert.That(outcome.Ok, Is.False);
		}
	}

	[Test, Explicit("Needs a reachable task scheduler.")]
	public async Task Removing_a_task_that_is_not_there_says_so()
	{
		var outcome = await Invoke(
			new ScheduledTaskTools.DeleteScheduledTaskTool(),
			Args(("path", "\\JarvisPluginTests\\NothingHere")));

		Assert.That(outcome.Ok, Is.False);
	}

	[Test, Explicit("Needs a reachable task scheduler.")]
	public async Task Running_a_task_that_is_not_there_says_so()
	{
		var outcome = await Invoke(
			new ScheduledTaskTools.RunScheduledTaskTool(),
			Args(("path", "\\JarvisPluginTests\\NothingHere")));

		Assert.That(outcome.Ok, Is.False);
	}

	/// <summary>
	/// Listing with a filter that matches nothing has to say so rather than listing everything, which is
	/// what an unfiltered list would do and is the wrong answer to "which of my tasks mentions this".
	/// </summary>
	[Test, Explicit("Needs a reachable task scheduler.")]
	public async Task A_filter_that_matches_nothing_says_so()
	{
		var outcome = await Invoke(
			new ScheduledTaskTools.ListScheduledTasksTool(),
			Args(("filter", "no-task-path-contains-this-12345")));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("No task path contains"));
		});
	}

	/// <summary>Reading is free; everything that changes the scheduler's contents has to ask.</summary>
	[Test]
	public void Scheduled_task_tools_are_gated_by_class()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new ScheduledTaskTools.ListScheduledTasksTool().RequiresConfirmation, Is.False);
			Assert.That(new ScheduledTaskTools.GetScheduledTaskTool().RequiresConfirmation, Is.False);
			Assert.That(new ScheduledTaskTools.CreateScheduledTaskTool().RequiresConfirmation, Is.True);
			Assert.That(new ScheduledTaskTools.DeleteScheduledTaskTool().RequiresConfirmation, Is.True);
			Assert.That(new ScheduledTaskTools.RunScheduledTaskTool().RequiresConfirmation, Is.True);
		});
	}

	[Test]
	public void Scheduled_task_tool_names_are_stable()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new ScheduledTaskTools.ListScheduledTasksTool().Name, Is.EqualTo("list_scheduled_tasks"));
			Assert.That(new ScheduledTaskTools.GetScheduledTaskTool().Name, Is.EqualTo("get_scheduled_task"));
			Assert.That(new ScheduledTaskTools.CreateScheduledTaskTool().Name, Is.EqualTo("create_scheduled_task"));
			Assert.That(new ScheduledTaskTools.DeleteScheduledTaskTool().Name, Is.EqualTo("delete_scheduled_task"));
			Assert.That(new ScheduledTaskTools.RunScheduledTaskTool().Name, Is.EqualTo("run_scheduled_task"));
		});
	}

	[Test]
	public void Every_scheduled_task_tool_describes_itself_and_its_parameters()
	{
		ITool[] tools =
		[
			new ScheduledTaskTools.ListScheduledTasksTool(),
			new ScheduledTaskTools.GetScheduledTaskTool(),
			new ScheduledTaskTools.CreateScheduledTaskTool(),
			new ScheduledTaskTools.DeleteScheduledTaskTool(),
			new ScheduledTaskTools.RunScheduledTaskTool(),
		];

		Assert.Multiple(() =>
		{
			foreach (var tool in tools)
			{
				var definition = tool.Definition;

				Assert.That(definition.Name, Is.EqualTo(tool.Name));
				Assert.That(definition.Description, Is.Not.Empty, $"{tool.Name} has no description");
				Assert.That(
					definition.Parameters?["properties"],
					Is.Not.Null,
					$"{tool.Name} declares no parameters object");
			}
		});
	}

	/// <summary>
	/// The path is split into the folder and the name the scheduler wants. Getting the split wrong means
	/// every lookup looks in the wrong place, so it is pinned rather than left to the round trip.
	/// </summary>
	[TestCase("\\Folder\\Task", "\\Folder", "Task")]
	[TestCase("\\Task", "\\", "Task")]
	[TestCase("Folder\\Task", "\\Folder", "Task")]
	public void A_task_path_splits_into_a_folder_and_a_name(string path, string folder, string name)
	{
		var (actualFolder, actualName) = ScheduledTaskTools.SplitForTest(path);

		Assert.Multiple(() =>
		{
			Assert.That(actualFolder, Is.EqualTo(folder));
			Assert.That(actualName, Is.EqualTo(name));
		});
	}
}
