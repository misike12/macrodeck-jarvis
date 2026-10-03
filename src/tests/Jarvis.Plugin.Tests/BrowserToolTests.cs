using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Jarvis.Plugin.Llm;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The browser tools.
/// <para>
/// None of these tests launch or drive a browser. The plugin under test would open a visible window and
/// navigate a real page, which is not something a test suite should do to the machine it runs on. What is
/// tested is everything decidable without a browser: the tool surface, the names the model is told, the
/// argument validation, and the page-side script that finds an element to click.
/// </para>
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class BrowserToolTests
{
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

	[Test]
	public void Browser_tool_names_are_stable()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new BrowserTools.BrowserNavigateTool().Name, Is.EqualTo("browser_navigate"));
			Assert.That(new BrowserTools.BrowserReadTool().Name, Is.EqualTo("browser_read"));
			Assert.That(new BrowserTools.BrowserKeyTool().Name, Is.EqualTo("browser_key"));
			Assert.That(new BrowserTools.BrowserTypeTool().Name, Is.EqualTo("browser_type"));
			Assert.That(new BrowserTools.BrowserClickTool().Name, Is.EqualTo("browser_click"));
			Assert.That(new BrowserTools.BrowserTabsTool().Name, Is.EqualTo("browser_tabs"));
		});
	}

	/// <summary>
	/// Reading the page and listing tabs change nothing. Everything else submits something to a site, which
	/// is the thing that can log the user in as them or place an order, so it confirms.
	/// </summary>
	[Test]
	public void Browser_tools_are_gated_by_class()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new BrowserTools.BrowserReadTool().RequiresConfirmation, Is.False);
			Assert.That(new BrowserTools.BrowserTabsTool().RequiresConfirmation, Is.False);
			Assert.That(new BrowserTools.BrowserNavigateTool().RequiresConfirmation, Is.True);
			Assert.That(new BrowserTools.BrowserKeyTool().RequiresConfirmation, Is.True);
			Assert.That(new BrowserTools.BrowserTypeTool().RequiresConfirmation, Is.True);
			Assert.That(new BrowserTools.BrowserClickTool().RequiresConfirmation, Is.True);
		});
	}

	/// <summary>
	/// Only http addresses are navigable. Anything else could reach a local file or a scheme that executes,
	/// and the browser is the one component here that would act on it.
	/// </summary>
	[TestCase("")]
	[TestCase("   ")]
	[TestCase("about:blank")]
	[TestCase("file:///C:/Windows/win.ini")]
	[TestCase("javascript:alert(1)")]
	[TestCase("chrome://settings")]
	[TestCase("not a url")]
	public async Task Only_http_addresses_are_navigable(string url)
	{
		var outcome = await Invoke(new BrowserTools.BrowserNavigateTool(), Args(("url", url)));

		Assert.That(outcome.Ok, Is.False, $"'{url}' was accepted");
	}

	[Test]
	public async Task Navigating_with_no_argument_is_refused()
	{
		var outcome = await Invoke(new BrowserTools.BrowserNavigateTool(), new JsonObject());

		Assert.That(outcome.Ok, Is.False);
	}

	[Test]
	public async Task Typing_nothing_is_refused()
	{
		foreach (var arguments in new[] { new JsonObject(), Args(("text", "")), Args(("text", (string?)null)) })
		{
			var outcome = await Invoke(new BrowserTools.BrowserTypeTool(), arguments);
			Assert.That(outcome.Ok, Is.False);
		}
	}

	[TestCase("")]
	[TestCase("   ")]
	public async Task Clicking_nothing_is_refused(string text)
	{
		var outcome = await Invoke(new BrowserTools.BrowserClickTool(), Args(("text", text)));

		Assert.That(outcome.Ok, Is.False);
	}

	[TestCase("")]
	[TestCase("   ")]
	public async Task Sending_no_key_is_refused(string key)
	{
		var outcome = await Invoke(new BrowserTools.BrowserKeyTool(), Args(("key", key)));

		Assert.That(outcome.Ok, Is.False);
	}

	[Test]
	public void Every_browser_tool_describes_itself_and_its_parameters()
	{
		ITool[] tools =
		[
			new BrowserTools.BrowserNavigateTool(),
			new BrowserTools.BrowserReadTool(),
			new BrowserTools.BrowserKeyTool(),
			new BrowserTools.BrowserTypeTool(),
			new BrowserTools.BrowserClickTool(),
			new BrowserTools.BrowserTabsTool(),
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
	/// The click is found by running a script in the page. That script is assembled from strings, and the
	/// text the model asked for is interpolated into it, so the escaping is what stands between a button
	/// label and a script injection.
	/// </summary>
	/// <summary>
	/// The text the model asked for is interpolated into a script that runs in the page. It is encoded as
	/// JSON first, which is what makes a label containing a quote harmless rather than a way out of the
	/// string it sits in.
	/// </summary>
	[Test]
	public void The_click_script_encodes_the_text_it_looks_for()
	{
		var expression = ThBrowser.BuildClickExpressionForTest("'; alert(1); //");

		// The quote that mattered became an escape, so the payload can no longer terminate the string it
		// sits in. The single quotes elsewhere in the script are the script's own and are expected.
		Assert.Multiple(() =>
		{
			Assert.That(expression, Does.Contain("\\u0027; alert(1); //"));
			Assert.That(expression, Does.Not.Contain("const wanted = ''; alert"));
		});
	}

	/// <summary>
	/// The direction of the match is the difference between finding a button and never finding one: a
	/// request for "Sign" has to match a button that reads "Sign in", not the other way round.
	/// </summary>
	[Test]
	public void A_partial_click_searches_for_the_text_inside_the_element()
	{
		var expression = ThBrowser.BuildClickExpressionForTest("Sign", exact: false);

		Assert.That(expression, Does.Contain("t.includes(wanted)"));
	}

	[Test]
	public void An_exact_click_requires_the_whole_text_to_match()
	{
		var expression = ThBrowser.BuildClickExpressionForTest("Sign in", exact: true);

		Assert.That(expression, Does.Contain("t === wanted"));
	}

	/// <summary>
	/// The script has to be valid JavaScript in every case, because a syntax error in it reads to the model
	/// as "the element is not there" rather than as a bug.
	/// </summary>
	[TestCase("Sign in")]
	[TestCase("with \"quotes\"")]
	[TestCase("with 'single'")]
	[TestCase("with \\ backslash")]
	[TestCase("with ` backtick")]
	[TestCase("with ${interpolation}")]
	[TestCase("multi\nline")]
	public void The_click_script_stays_balanced(string text)
	{
		var expression = ThBrowser.BuildClickExpressionForTest(text);

		Assert.Multiple(() =>
		{
			Assert.That(expression, Does.StartWith("(() => {"));
			Assert.That(expression, Does.EndWith("})()"));
			Assert.That(expression, Does.Contain("getBoundingClientRect"));
		});
	}
}
