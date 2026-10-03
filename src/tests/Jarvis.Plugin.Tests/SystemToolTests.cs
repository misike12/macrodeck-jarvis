using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Jarvis.Plugin.Llm;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The system tools. Nothing here changes the machine: power and notifications are only ever checked for
/// argument handling and refusal, never actually run, because a test suite that can shut down the computer
/// it is running on is not a test suite anyone should run twice.
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class SystemToolTests
{
	private static JsonObject Args(params (string Key, object? Value)[] pairs)
	{
		var node = new JsonObject();

		foreach (var (key, value) in pairs)
		{
			node[key] = value is null ? null : JsonValue.Create(value);
		}

		return node;
	}

	private static Task<ToolOutcome> Invoke(ITool tool, JsonObject arguments) =>
		tool.InvokeAsync(arguments, CancellationToken.None);

	[Test]
	public async Task Volume_can_be_read()
	{
		var outcome = await Invoke(new SystemTools.GetVolumeTool(), new JsonObject());

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(outcome.Content, Does.Contain("%"));
		});
	}

	/// <summary>
	/// Power is the one tool where the argument gate matters most, because every accepted value ends the
	/// session. An unrecognised action must be refused rather than defaulted to anything.
	/// </summary>
	[TestCase("hibernate")]
	[TestCase("explode")]
	[TestCase("")]
	[TestCase("   ")]
	public async Task Power_refuses_anything_that_is_not_one_of_the_four(string action)
	{
		var outcome = await Invoke(new SystemTools.SetSystemPowerTool(), Args(("action", action)));

		Assert.That(outcome.Ok, Is.False, $"'{action}' was accepted");
	}

	[Test]
	public async Task Power_requires_an_action()
	{
		var outcome = await Invoke(new SystemTools.SetSystemPowerTool(), new JsonObject());

		Assert.That(outcome.Ok, Is.False);
	}

	/// <summary>Both power and notifications are destructive enough to need a yes first.</summary>
	[Test]
	public void System_tools_that_act_require_confirmation()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new SystemTools.GetVolumeTool().RequiresConfirmation, Is.False);
			Assert.That(new SystemTools.MediaPlayPauseTool().RequiresConfirmation, Is.True);
			Assert.That(new SystemTools.MediaNextTool().RequiresConfirmation, Is.True);
			Assert.That(new SystemTools.SetSystemPowerTool().RequiresConfirmation, Is.True);
			Assert.That(new SystemTools.SendNotificationTool().RequiresConfirmation, Is.True);
		});
	}

	[Test]
	public async Task A_notification_needs_both_a_title_and_a_message()
	{
		var tool = new SystemTools.SendNotificationTool();

		foreach (var arguments in new[]
		{
			new JsonObject(),
			Args(("title", "hello")),
			Args(("message", "hello")),
			Args(("title", "  "), ("message", "hello")),
			Args(("title", "hello"), ("message", "  ")),
		})
		{
			var outcome = await Invoke(tool, arguments);
			Assert.That(outcome.Ok, Is.False);
		}
	}

	/// <summary>
	/// Every tool the plan calls for must be reachable by the exact name the plan documents, because the
	/// model is told the names in the system prompt. A rename that only changed the class would leave the
	/// model calling a tool that does not exist.
	/// </summary>
	[Test]
	public void Tool_names_match_the_plan()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new SystemTools.GetVolumeTool().Name, Is.EqualTo("get_volume"));
			Assert.That(new SystemTools.MediaPlayPauseTool().Name, Is.EqualTo("media_play_pause"));
			Assert.That(new SystemTools.MediaNextTool().Name, Is.EqualTo("media_next"));
			Assert.That(new SystemTools.SetSystemPowerTool().Name, Is.EqualTo("set_system_power"));
			Assert.That(new SystemTools.SendNotificationTool().Name, Is.EqualTo("send_notification"));
		});
	}

	/// <summary>
	/// A parameter object with no description is useless to a model choosing between tools, so every tool
	/// has to declare what it does in its own definition rather than relying on the system prompt.
	/// </summary>
	[Test]
	public void Every_system_tool_describes_itself_and_its_parameters()
	{
		ITool[] tools =
		[
			new SystemTools.GetVolumeTool(),
			new SystemTools.MediaPlayPauseTool(),
			new SystemTools.MediaNextTool(),
			new SystemTools.SetSystemPowerTool(),
			new SystemTools.SendNotificationTool(),
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
}
