using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// Fetching pages and searching the web.
/// <para>
/// HTML is reduced to text before it reaches the model. A page's markup is mostly navigation, scripts and
/// styles that cost tokens and tell the model nothing, so tags are dropped and the handful of elements that
/// carry meaning are kept as labelled lines.
/// </para>
/// <para>
/// Anything can be fetched, including localhost and private addresses, because that is genuinely useful
/// against a local service and the caller already has those rights. It does mean a model can reach anything
/// the plugin process can, which is why every fetch asks first.
/// </para>
/// </summary>
public static class WebTools
{
	/// <summary>
	/// Bounded because a model asking for a whole site would otherwise be a way to spend unbounded time and
	/// unbounded context on one tool call.
	/// </summary>
	private const int MaximumCharacters = 40_000;

	private const int DefaultTimeoutSeconds = 20;

	/// <summary>
	/// The one page reader. Text and JSON are handed through untouched; everything else is reduced to
	/// readable text, because a model cannot answer a question from markup and the markup is mostly noise.
	/// </summary>
	internal static string ToReadableText(string body, string contentType)
	{
		if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
			|| contentType.Contains("plain", StringComparison.OrdinalIgnoreCase)
			|| contentType.Contains("markdown", StringComparison.OrdinalIgnoreCase))
		{
			return body;
		}

		if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
			|| body.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal))
		{
			return FromHtml(body);
		}

		return body;
	}

	/// <summary>
	/// Turns markup into text. Written as a single pass rather than with an HTML parser because no parser is
	/// available without a dependency, and a regex-based reader only has to be good enough to leave the
	/// words in the right order - which this does, while dropping the parts that are never content.
	/// </summary>
	private static string FromHtml(string html)
	{
		var builder = new StringBuilder(html.Length / 4);

// The title is read before the head is removed below, because the head is one of the elements stripped
		// and the title is the one line on the page that says what the page is.
		var titleMatch = System.Text.RegularExpressions.Regex.Match(
			html,
			"<title\\b[^>]*>(.*?)</title>",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

		var title = titleMatch.Success
			? WebUtility.HtmlDecode(StripTags(titleMatch.Groups[1].Value)).Trim()
			: string.Empty;

		if (title.Length > 0)
		{
			builder.Append("Title: ").Append(title).Append('\n');
		}

		html = System.Text.RegularExpressions.Regex.Replace(
			html, "<(script|style|noscript|svg|head)\\b[^>]*>.*?</\\1>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
		html = System.Text.RegularExpressions.Regex.Replace(
			html, "<!--.*?-->", " ", System.Text.RegularExpressions.RegexOptions.Singleline);

		// Headings are the page's own structure, so they survive as plain lines above the body text.
		foreach (System.Text.RegularExpressions.Match match in
			System.Text.RegularExpressions.Regex.Matches(
				html, "<h[1-6]\\b[^>]*>(.*?)</h[1-6]>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline))
		{
			var inner = WebUtility.HtmlDecode(StripTags(match.Groups[1].Value)).Trim();

			if (inner.Length > 0)
			{
				builder.Append(inner).Append('\n');
			}
		}


		var body = System.Text.RegularExpressions.Regex.Replace(
			html, "<(a|li|tr|p|div|br|section|article|td|th)[^>]*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		body = StripTags(body);
		body = System.Net.WebUtility.HtmlDecode(body);

		foreach (var line in body.Split('\n'))
		{
			var trimmed = line.Trim();

			if (trimmed.Length == 0)
			{
				if (builder.Length > 0 && builder[^1] != '\n')
				{
					builder.Append('\n');
				}

				continue;
			}

			builder.Append(trimmed).Append('\n');
		}

		return builder.ToString().Trim();
	}

	private static string StripTags(string html) =>
		System.Text.RegularExpressions.Regex.Replace(
			html, "<[^>]+>", " ", System.Text.RegularExpressions.RegexOptions.Singleline);

	private static async Task<(HttpResponseMessage? Response, string? Error)> TryFetchAsync(
		HttpClient client,
		string url,
		CancellationToken cancellationToken)
	{
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, url);

			// A couple of sites serve a bare 403 to anything that does not look like a browser, which would
			// read to the model as "this page is unavailable" rather than "try differently".
			request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Jarvis/1.0");
			request.Headers.TryAddWithoutValidation("Accept", "text/html,application/json;q=0.9,*/*;q=0.8");

			var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			return (response, null);
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
		{
			return (null, exception.Message);
		}
	}

	private static ToolOutcome Truncate(string text)
	{
		if (text.Length <= MaximumCharacters)
		{
			return ToolOutcome.Success(text);
		}

		return ToolOutcome.Success(text[..MaximumCharacters] + "\n[truncated]");
	}

	/// <summary>Fetches one page.</summary>
	public sealed class WebFetchTool : ITool
	{
		public string Name => "web_fetch";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Fetches a web page or API and returns it as readable text. Use this to read "
				+ "something, never to act on a site.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["url"] = new JsonObject { ["type"] = "string", ["description"] = "The address to fetch." },
					["timeoutSeconds"] = new JsonObject
					{
						["type"] = "integer",
						["description"] = $"How long to wait. Default {DefaultTimeoutSeconds}.",
					},
				},
				["required"] = new JsonArray("url"),
			},
		};

		public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var url = arguments["url"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(url))
			{
				return ToolOutcome.Failure("No address was given.");
			}

			if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			{
				return ToolOutcome.Failure($"'{url}' is not an address.");
			}

			if (uri.Scheme is not ("http" or "https"))
			{
				return ToolOutcome.Failure("Only http and https can be fetched.");
			}

			var seconds = Math.Clamp(arguments["timeoutSeconds"]?.GetValue<int?>() ?? DefaultTimeoutSeconds, 1, 120);

			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds) };
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

			var (response, error) = await TryFetchAsync(client, uri.ToString(), timeout.Token).ConfigureAwait(false);

			if (response is null)
			{
				return ToolOutcome.Failure($"{uri} could not be fetched: {error}");
			}

			using (response)
			{
				if (!response.IsSuccessStatusCode)
				{
					return ToolOutcome.Failure($"{uri} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
				}

				var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
				var text = ToReadableText(body, response.Content.Headers.ContentType?.MediaType ?? string.Empty);

				return string.IsNullOrWhiteSpace(text)
					? ToolOutcome.Failure($"{uri} returned no readable text.")
					: Truncate($"{uri}\n\n{text}");
			}
		}
	}

	/// <summary>Searches and returns the results.</summary>
	public sealed class WebSearchTool : ITool
	{
		public string Name => "web_search";

		public bool RequiresConfirmation => false;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Searches the web and returns the results as a list of titles, addresses and "
				+ "snippets. Reading a page is a separate step.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["query"] = new JsonObject { ["type"] = "string", ["description"] = "What to search for." },
					["count"] = new JsonObject
					{
						["type"] = "integer",
						["description"] = "How many results, 1 to 20. Default 8.",
					},
				},
				["required"] = new JsonArray("query"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken) =>
			SearchAsync(arguments, cancellationToken);

		internal static async Task<ToolOutcome> SearchAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var query = arguments["query"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(query))
			{
				return ToolOutcome.Failure("No search text was given.");
			}

			var count = Math.Clamp(arguments["count"]?.GetValue<int?>() ?? 8, 1, 20);
			var results = await SearchEngine.QueryAsync(query, count, cancellationToken).ConfigureAwait(false);

			return results;
		}
	}

	/// <summary>
	/// Searches and reads the top results in one call, for when the answer is in the snippets but the model
	/// should not have to spend a round trip per page.
	/// </summary>
	public sealed class WebSearchAndReadTool : ITool
	{
		public string Name => "web_search_and_read";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Searches the web and reads the top few results in one step. Use this when you want "
				+ "an answer from what is published rather than from one known page.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["query"] = new JsonObject { ["type"] = "string", ["description"] = "What to search for." },
					["read"] = new JsonObject
					{
						["type"] = "integer",
						["description"] = "How many results to read in full, 1 to 5. Default 3.",
					},
				},
				["required"] = new JsonArray("query"),
			},
		};

		public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var query = arguments["query"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(query))
			{
				return ToolOutcome.Failure("No search text was given.");
			}

			var read = Math.Clamp(arguments["read"]?.GetValue<int?>() ?? 3, 1, 5);
			var found = await SearchEngine.QueryAsync(query, 8, cancellationToken).ConfigureAwait(false);

			if (!found.Ok)
			{
				return found;
			}

			var links = LinksFrom(found.Content);
			var builder = new StringBuilder(found.Content);

			var readCount = 0;

			foreach (var link in links)
			{
				if (readCount >= read)
				{
					break;
				}

				cancellationToken.ThrowIfCancellationRequested();

				// One unreadable result must not abort the whole call: the point of this tool is to come back
				// with something even when one of the pages is down or blocking.
				try
				{
					using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
					using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
					timeout.CancelAfter(TimeSpan.FromSeconds(15));

					var (response, _) = await TryFetchAsync(client, link, timeout.Token).ConfigureAwait(false);

					if (response is null)
					{
						continue;
					}

					using (response)
					{
						if (!response.IsSuccessStatusCode)
						{
							continue;
						}

						var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
						var text = ToReadableText(body, response.Content.Headers.ContentType?.MediaType ?? string.Empty);

						if (string.IsNullOrWhiteSpace(text))
						{
							continue;
						}

						const int PerPageLimit = 4_000;

						builder.Append(CultureInfo.InvariantCulture, $"\n\n--- {link} ---\n");
						builder.Append(text.Length <= PerPageLimit ? text : text[..PerPageLimit] + "\n[truncated]");
						readCount++;
					}
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception)
				{
					// A single page failing is expected and not worth surfacing over the pages that worked.
				}
			}

			if (readCount == 0)
			{
				builder.Append("\n\n[no result could be read in full]");
			}

			return Truncate(builder.ToString());
		}
	}

	/// <summary>
	/// The search itself. DuckDuckGo's HTML endpoint is used because it needs no key, no account and no
	/// tracking, and returns markup this can parse; the JSON endpoint that would be nicer needs a partner
	/// token for the general case.
	/// </summary>
	private static class SearchEngine
	{
		internal static async Task<ToolOutcome> QueryAsync(string query, int count, CancellationToken cancellationToken)
		{
			var url = "https://html.duckduckgo.com/html/?q="
				+ WebUtility.UrlEncode(query);

			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(20));

			var (response, error) = await TryFetchAsync(client, url, timeout.Token).ConfigureAwait(false);

			if (response is null)
			{
				return ToolOutcome.Failure($"The search could not be run: {error}");
			}

			using (response)
			{
				if (!response.IsSuccessStatusCode)
				{
					return ToolOutcome.Failure(
						$"The search returned {(int)response.StatusCode} {response.ReasonPhrase}.");
				}

				var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
				var results = Parse(body, count);

				return results.Count == 0
					? ToolOutcome.Failure($"Nothing was found for '{query}'.")
					: ToolOutcome.Success(string.Join('\n', results));
			}
		}

		/// <summary>Pulls the result links and snippets out of the results page.</summary>
		private static List<string> Parse(string html, int count)
		{
			var results = new List<string>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			// Each result is a link with a snippet in a sibling element. The href carries the real address
			// behind DuckDuckGo's redirect, which is unwrapped so the model gets a link it can actually fetch.
			foreach (System.Text.RegularExpressions.Match match in
				System.Text.RegularExpressions.Regex.Matches(
					html,
					"<a[^>]+class=\"[^\"]*result__a[^\"]*\"[^>]*href=\"(?<href>[^\"]+)\"[^>]*>(?<title>.*?)</a>",
					System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline))
			{
				if (results.Count >= count)
				{
					break;
				}

				var link = Unwrap(match.Groups["href"].Value);

				if (link.Length == 0 || !seen.Add(link))
				{
					continue;
				}

				var title = WebUtility.HtmlDecode(StripTags(match.Groups["title"].Value)).Trim();

				results.Add($"{results.Count + 1}. {title}\n   {link}");
			}

			return results;
		}

		private static string Unwrap(string href)
		{
			var value = WebUtility.HtmlDecode(href);

			// The redirect form is /l/?uddg=<encoded address>; the plain form is already the address.
			if (Uri.TryCreate(value, UriKind.Absolute, out var parsed))
			{
				if (parsed.Query.Contains("uddg=", StringComparison.Ordinal))
				{
					var query = HttpUtility.ParseQueryString(parsed.Query);

					if (query["uddg"] is { Length: > 0 } target && Uri.TryCreate(target, UriKind.Absolute, out var unwrapped))
					{
						return unwrapped.ToString();
					}
				}

				return parsed.Scheme is "http" or "https" ? parsed.ToString() : string.Empty;
			}

return string.Empty;
		}
	}

	/// <summary>
	/// Recovers the addresses from an already formatted result list, so the read step knows what to fetch.
	/// </summary>
	internal static List<string> LinksFrom(string results) =>
	[.. results
		.Split('\n')
		.Select(line => line.Trim())
		.Where(line => line.StartsWith("http", StringComparison.Ordinal))];

	internal static class HttpUtility
	{
		/// <summary>
		/// Decodes a query string value. Plus is a space only in a query string, and a percent sequence that
		/// is not valid is left alone rather than throwing, because a malformed address should come back as a
		/// link that does not work rather than as an exception.
		/// </summary>
		internal static Dictionary<string, string> ParseQueryString(string query)
		{
			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
			{
				var separator = pair.IndexOf('=', StringComparison.Ordinal);

				if (separator <= 0)
				{
					continue;
				}

				result[Decode(pair[..separator])] = Decode(pair[(separator + 1)..]);
			}

			return result;
		}

		private static string Decode(string value)
		{
			var builder = new StringBuilder(value.Length);

			for (var index = 0; index < value.Length; index++)
			{
				var character = value[index];

				if (character == '+')
				{
					builder.Append(' ');
					continue;
				}

				if (character == '%' && index + 2 < value.Length
					&& int.TryParse(value.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var code))
				{
					builder.Append((char)code);
					index += 2;
					continue;
				}

				builder.Append(character);
			}

			return builder.ToString();
		}
	}
}
