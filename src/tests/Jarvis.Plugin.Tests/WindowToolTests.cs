using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Jarvis.Plugin.Llm;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Window control against the real desktop. Listing is exercised against whatever is actually open right
/// now; focusing and closing are only ever pointed at a window this fixture creates, because a test that
/// closed a real user window would be a genuinely bad test.
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class WindowToolTests
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

	/// <summary>
	/// There is always a desktop window to list. If this ever fails, the enumeration itself is broken
	/// rather than the machine being unusual.
	/// </summary>
	[Test]
	public async Task Listing_windows_returns_something()
	{
		var outcome = await Invoke(new WindowTools.ListWindowsTool(), new JsonObject());

		Assert.That(outcome.Ok, Is.True, outcome.Content);
		Assert.That(outcome.Content, Does.Contain("window(s)"));
	}

	/// <summary>A filter that matches nothing must say so rather than listing everything.</summary>
	[Test]
	public async Task Listing_with_a_filter_narrows_the_list()
	{
		var outcome = await Invoke(
			new WindowTools.ListWindowsTool(),
			Args(("contains", "a-title-no-window-will-ever-have-12345")));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("No open window"));
		});
	}

	[Test]
	public async Task Focusing_a_window_that_is_not_open_says_so_and_suggests_listing()
	{
		var outcome = await Invoke(
			new WindowTools.FocusWindowTool(),
			Args(("title", "a window that definitely does not exist 98765")));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("No open window"));
			Assert.That(outcome.Content, Does.Contain("list_windows"));
		});
	}

	[Test]
	public async Task Closing_a_window_that_is_not_open_says_so()
	{
		var outcome = await Invoke(
			new WindowTools.CloseWindowTool(),
			Args(("title", "a window that definitely does not exist 98765")));

		Assert.That(outcome.Ok, Is.False);
	}

	[TestCase("")]
	[TestCase("   ")]
	public async Task Focusing_and_closing_need_a_title(string title)
	{
		foreach (var tool in new ITool[] { new WindowTools.FocusWindowTool(), new WindowTools.CloseWindowTool() })
		{
			var outcome = await Invoke(tool, Args(("title", title)));
			Assert.That(outcome.Ok, Is.False, $"{tool.Name} accepted a blank title");
		}
	}

	[Test]
	public async Task Opening_nothing_is_refused()
	{
		var outcome = await Invoke(new WindowTools.OpenAppTool(), Args(("name", "  ")));

		Assert.That(outcome.Ok, Is.False);
	}

	/// <summary>An absolute path that does not exist must be refused before the shell is asked.</summary>
	[Test]
	public async Task Opening_a_path_that_does_not_exist_is_refused()
	{
		var outcome = await Invoke(
			new WindowTools.OpenAppTool(),
			Args(("name", Path.Combine(Path.GetTempPath(), $"jarvis-no-such-app-{Guid.CreateVersion7():N}.exe"))));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("does not exist"));
		});
	}

	/// <summary>Reads are free; anything that changes what is on screen has to ask.</summary>
	[Test]
	public void Window_tools_are_gated_by_class()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new WindowTools.ListWindowsTool().RequiresConfirmation, Is.False);
			Assert.That(new WindowTools.FocusWindowTool().RequiresConfirmation, Is.True);
			Assert.That(new WindowTools.CloseWindowTool().RequiresConfirmation, Is.True);
			Assert.That(new WindowTools.OpenAppTool().RequiresConfirmation, Is.True);
		});
	}
}
