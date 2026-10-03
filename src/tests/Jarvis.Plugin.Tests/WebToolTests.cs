using System.Text.Json.Nodes;
using Jarvis.Plugin.Llm;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The web tools. Nothing here touches the network: what is tested is the reduction from markup to text,
/// which is the part with real logic in it and the part that decides whether the model can read a page at
/// all. A test that fetched a live site would be slow, flaky, and would stop passing when a site changes.
/// </summary>
[TestFixture]
public class WebToolTests
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

	private const string SamplePage = """
		<!DOCTYPE html>
		<html><head>
		<title>The quiet machine</title>
		<style>body { color: red; }</style>
		<script>var tracking = { id: 'abc' }; alert('hi');</script>
		</head>
		<body>
		<h1>A heading worth keeping</h1>
		<p>The first paragraph has some <b>bold</b> text in it.</p>
		<p>The second paragraph is where the answer is.</p>
		<ul><li>First item</li><li>Second item</li></ul>
		<!-- a comment that should not be read -->
		<footer>Copyright nobody</footer>
		</body></html>
		""";

	/// <summary>
	/// The whole reason the tool exists. If the words come out in the wrong order the model answers from a
	/// scrambled page and gives a confident wrong answer, which is the worst outcome available.
	/// </summary>
	[Test]
	public void Html_becomes_readable_text_in_document_order()
	{
		var text = WebTools.ToReadableText(SamplePage, "text/html");

		var heading = text.IndexOf("A heading worth keeping", StringComparison.Ordinal);
		var first = text.IndexOf("The first paragraph", StringComparison.Ordinal);
		var bold = text.IndexOf("bold", StringComparison.Ordinal);
		var second = text.IndexOf("The second paragraph", StringComparison.Ordinal);
		var item = text.IndexOf("Second item", StringComparison.Ordinal);

		Assert.Multiple(() =>
		{
			Assert.That(heading, Is.GreaterThanOrEqualTo(0), "the heading was lost");
			Assert.That(heading, Is.LessThan(first), "the heading came after the body text");
			Assert.That(first, Is.LessThan(bold), "bold text came before its sentence");
			Assert.That(bold, Is.LessThan(second), "paragraphs came out in the wrong order");
			Assert.That(second, Is.LessThan(item), "the list came before the paragraphs");
		});
	}

	/// <summary>Scripts are the largest thing on a page and the only thing that must never be read.</summary>
	[Test]
	public void Scripts_styles_and_comments_are_removed()
	{
		var text = WebTools.ToReadableText(SamplePage, "text/html");

		Assert.Multiple(() =>
		{
			Assert.That(text, Does.Not.Contain("tracking"));
			Assert.That(text, Does.Not.Contain("alert"));
			Assert.That(text, Does.Not.Contain("color: red"));
			Assert.That(text, Does.Not.Contain("should not be read"));
			Assert.That(text, Does.Not.Contain("<"));
			Assert.That(text, Does.Not.Contain(">"));
		});
	}

	/// <summary>The page's own title is how the model knows what it fetched.</summary>
	[Test]
	public void The_title_is_kept_and_labelled()
	{
		var text = WebTools.ToReadableText(SamplePage, "text/html");

		Assert.That(text, Does.Contain("Title: The quiet machine"));
	}

	/// <summary>Entities are decoded, or a model reads &amp; and thinks that is the text.</summary>
	[Test]
	public void Entities_are_decoded()
	{
		var text = WebTools.ToReadableText(
			"<html><body><p>Fish &amp; chips &lt;here&gt;</p></body></html>",
			"text/html");

		Assert.Multiple(() =>
		{
			Assert.That(text, Does.Contain("Fish & chips"));
			Assert.That(text, Does.Contain("<here>"));
		});
	}

private static readonly string[] TwoLines = ["one", "two"];

	private static readonly string[] TwoLinks = ["https://example.com/one", "https://example.org/two"];

	[Test]
	public void Blank_lines_are_collapsed()
	{
		var text = WebTools.ToReadableText("<p>one</p><p></p><p></p><p>two</p>", "text/html");
		var lines = text.Split('\n').Where(line => line.Trim().Length > 0).ToArray();

		Assert.That(lines, Is.EqualTo(TwoLines));
	}

	/// <summary>JSON and plain text are handed through untouched, because they are already readable.</summary>
	[Test]
	public void Json_is_passed_through_unchanged()
	{
		const string Body = """{"name":"value","nested":{"n":1}}""";

		Assert.That(WebTools.ToReadableText(Body, "application/json"), Is.EqualTo(Body));
	}

	[Test]
	public void Plain_text_is_passed_through_unchanged()
	{
		Assert.That(WebTools.ToReadableText("just words", "text/plain"), Is.EqualTo("just words"));
	}

	/// <summary>
	/// A body with no declared type is still markdown rather than markup, so the leading-brace test is what
	/// keeps a bare JSON response from being run through the tag stripper.
	/// </summary>
	[Test]
	public void Markup_is_detected_even_when_the_server_declares_nothing()
	{
		var text = WebTools.ToReadableText("<html><body><p>found anyway</p></body></html>", string.Empty);

		Assert.That(text, Does.Contain("found anyway"));
		Assert.That(text, Does.Not.Contain("<p>"));
	}

	/// <summary>An unclosed tag is not a reason to lose the page.</summary>
	[Test]
	public void Malformed_markup_still_yields_text()
	{
		var text = WebTools.ToReadableText("<p>one<p>two</p><div><span>three", "text/html");

		Assert.Multiple(() =>
		{
			Assert.That(text, Does.Contain("one"));
			Assert.That(text, Does.Contain("two"));
			Assert.That(text, Does.Contain("three"));
		});
	}

	/// <summary>Anything that can reach the network has to be validated before the request is made.</summary>
	[TestCase("")]
	[TestCase("   ")]
	[TestCase("not a url")]
	[TestCase("javascript:alert(1)")]
	[TestCase("file:///C:/Windows/win.ini")]
	[TestCase("ftp://example.com")]
	public async Task Only_http_addresses_are_fetched(string url)
	{
		var outcome = await Invoke(new WebTools.WebFetchTool(), Args(("url", url)));

		Assert.That(outcome.Ok, Is.False, $"'{url}' was accepted");
	}

	[Test]
	public async Task Fetching_nothing_is_refused()
	{
		var outcome = await Invoke(new WebTools.WebFetchTool(), new JsonObject());

		Assert.That(outcome.Ok, Is.False);
	}

	[Test]
	public async Task Searching_for_nothing_is_refused()
	{
		foreach (var tool in new ITool[] { new WebTools.WebSearchTool(), new WebTools.WebSearchAndReadTool() })
		{
			var outcome = await Invoke(tool, new JsonObject());
			Assert.That(outcome.Ok, Is.False, $"{tool.Name} accepted an empty query");
		}
	}

	/// <summary>
	/// Fetching something the user did not ask for is a real risk, so it confirms. A search only reads
	/// what is already public, so it does not.
	/// </summary>
	[Test]
	public void Web_tools_are_gated_by_class()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new WebTools.WebFetchTool().RequiresConfirmation, Is.True);
			Assert.That(new WebTools.WebSearchTool().RequiresConfirmation, Is.False);
			Assert.That(new WebTools.WebSearchAndReadTool().RequiresConfirmation, Is.True);
		});
	}

	[Test]
	public void Web_tool_names_are_stable()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new WebTools.WebFetchTool().Name, Is.EqualTo("web_fetch"));
			Assert.That(new WebTools.WebSearchTool().Name, Is.EqualTo("web_search"));
			Assert.That(new WebTools.WebSearchAndReadTool().Name, Is.EqualTo("web_search_and_read"));
		});
	}

	/// <summary>
	/// A query string arrives percent-encoded and has to survive the round trip, because a search address
	/// with a broken query returns the wrong page rather than an error.
	/// </summary>
	[Test]
	public void A_query_string_is_decoded()
	{
		var parsed = WebTools.HttpUtility.ParseQueryString("?uddg=https%3A%2F%2Fexample.com%2Fa+b&other=1");

		Assert.Multiple(() =>
		{
			Assert.That(parsed["uddg"], Is.EqualTo("https://example.com/a b"));
			Assert.That(parsed["other"], Is.EqualTo("1"));
		});
	}

	[Test]
	public void A_malformed_percent_sequence_is_left_alone_rather_than_throwing()
	{
		var parsed = WebTools.HttpUtility.ParseQueryString("?a=%zz&b=100%");

		Assert.That(parsed["a"], Is.EqualTo("%zz"));
	}

	/// <summary>
	/// The search and read tool recovers the addresses from the result list so it can fetch them. If that
	/// recovery broke, the tool would silently return search results with no pages read.
	/// </summary>
	[Test]
	public void Result_links_are_recovered_for_the_read_step()
	{
		var links = WebTools.LinksFrom(
			"1. First\n   https://example.com/one\n2. Second\n   https://example.org/two\n\n2 result(s)");

		Assert.Multiple(() =>
		{
			Assert.That(links, Is.EqualTo(TwoLinks));
		});
	}

	[Test]
	public void Every_web_tool_describes_itself_and_its_parameters()
	{
		ITool[] tools =
		[
			new WebTools.WebFetchTool(),
			new WebTools.WebSearchTool(),
			new WebTools.WebSearchAndReadTool(),
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
