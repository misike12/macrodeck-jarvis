using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Llm;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The desktop tools, run against the real machine. Each of these touches the live session, so every test
/// here either reads or refuses: nothing in this fixture ends a process or changes the volume.
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class DesktopToolTests
{
	[Test]
	public async Task Listing_processes_returns_something_that_looks_like_a_process_list()
	{
		var outcome = await new DesktopTools.ListProcessesTool()
			.InvokeAsync(new JsonObject(), TestContext.CurrentContext.CancellationToken);

		Assert.That(outcome.Ok, Is.True, outcome.Content);

		var lines = outcome.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

		Assert.That(lines, Is.Not.Empty);
		Assert.That(lines[0].Split('\t'), Has.Length.EqualTo(3), "expected name, id and memory");
	}

	[Test]
	public async Task A_process_filter_narrows_the_list()
	{
		var outcome = await new DesktopTools.ListProcessesTool()
			.InvokeAsync(new JsonObject { ["filter"] = "explorer" }, TestContext.CurrentContext.CancellationToken);

		Assert.That(outcome.Ok, Is.True, outcome.Content);
		Assert.That(outcome.Content, Does.Contain("explorer").IgnoreCase);
	}

	/// <summary>
	/// The refusal is the point of this test. Ending explorer or winlogon does not close a window, it
	/// closes the session, and a tool that can do that on request is not a tool that should exist.
	/// </summary>
	[TestCase("explorer.exe")]
	[TestCase("winlogon.exe")]
	[TestCase("csrss.exe")]
	public async Task A_process_that_holds_the_session_together_is_refused(string name)
	{
		var outcome = await new DesktopTools.KillProcessTool()
			.InvokeAsync(new JsonObject { ["name"] = name }, TestContext.CurrentContext.CancellationToken);

		Assert.That(outcome.Ok, Is.False, $"'{name}' was allowed to be ended");
		Assert.That(outcome.Content, Does.Contain("cannot be ended").IgnoreCase);
	}

	[Test]
	public async Task Killing_nothing_reports_a_failure_rather_than_success()
	{
		var outcome = await new DesktopTools.KillProcessTool()
			.InvokeAsync(
				new JsonObject { ["name"] = "no-such-process-anywhere-jarvis" },
				TestContext.CurrentContext.CancellationToken);

		Assert.That(outcome.Ok, Is.False);
	}

	[Test]
	public async Task Killing_without_naming_anything_is_refused()
	{
		var outcome = await new DesktopTools.KillProcessTool()
			.InvokeAsync(new JsonObject(), TestContext.CurrentContext.CancellationToken);

		Assert.That(outcome.Ok, Is.False);
	}

	/// <summary>
	/// The clipboard round trip is the real check: writing is a Win32 sequence with a memory handle whose
	/// ownership changes halfway, and only a round trip proves the handle was handed over correctly.
	/// </summary>
	[Test]
	public async Task The_clipboard_round_trips_text()
	{
		var writer = new DesktopTools.ClipboardWriteTool();
		var reader = new DesktopTools.ClipboardReadTool();
		var token = TestContext.CurrentContext.CancellationToken;
		const string Text = "JARVIS clipboard check 12345";

		var written = await writer.InvokeAsync(new JsonObject { ["text"] = Text }, token);
		Assert.That(written.Ok, Is.True, written.Content);

		var read = await reader.InvokeAsync(new JsonObject(), token);

		Assert.Multiple(() =>
		{
			Assert.That(read.Ok, Is.True, read.Content);
			Assert.That(read.Content, Is.EqualTo(Text));
		});
	}

	[Test]
	public async Task Writing_nothing_to_the_clipboard_fails()
	{
		var outcome = await new DesktopTools.ClipboardWriteTool()
			.InvokeAsync(new JsonObject { ["text"] = string.Empty }, TestContext.CurrentContext.CancellationToken);

		Assert.That(outcome.Ok, Is.False);
	}

	[Test]
	public async Task A_volume_outside_the_range_is_refused_before_anything_is_changed()
	{
		var tool = new DesktopTools.SetVolumeTool();

		foreach (var percent in new[] { -1, 101, 1000 })
		{
			var outcome = await tool.InvokeAsync(
				new JsonObject { ["percent"] = percent },
				TestContext.CurrentContext.CancellationToken);

			Assert.That(outcome.Ok, Is.False, $"{percent} was accepted");
		}
	}

	/// <summary>Reads are free and actions are not. That split is the safety model in one assertion.</summary>
	[Test]
	public void Only_reading_tools_are_free()
	{
		var free = new ITool[]
		{
			new DesktopTools.ListProcessesTool(),
			new DesktopTools.ClipboardReadTool(),
		};

		var confirmed = new ITool[]
		{
			new DesktopTools.ClipboardWriteTool(),
			new DesktopTools.KillProcessTool(),
			new DesktopTools.SetVolumeTool(),
			new ScreenshotTool(null!, RuntimeTestLog.Logger),
			new SetPersonaTool((_, _) => { }, RuntimeTestLog.Logger),
		};

		foreach (var tool in free)
		{
			Assert.That(tool.RequiresConfirmation, Is.False, $"{tool.Name} asks to confirm a read");
		}

		foreach (var tool in confirmed)
		{
			Assert.That(tool.RequiresConfirmation, Is.True, $"{tool.Name} acts without confirming");
		}
	}
}