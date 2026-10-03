using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Llm;
using Serilog;

namespace Jarvis.Plugin.Vision;

/// <summary>Why a vision request could not be answered.</summary>
public enum VisionFailure
{
	/// <summary>No vision model is configured, or no credentials for one.</summary>
	NotConfigured,

	/// <summary>The endpoint could not be reached.</summary>
	Unreachable,

	/// <summary>The endpoint answered with an error.</summary>
	Rejected,
}

/// <summary>The outcome of asking a vision model about an image.</summary>
public sealed record VisionResult
{
	public required bool Ok { get; init; }

	public string Text { get; init; } = string.Empty;

	public VisionFailure? Failure { get; init; }

	public string? Detail { get; init; }

	public static VisionResult Success(string text) => new() { Ok = true, Text = text };

	public static VisionResult Failed(VisionFailure failure, string? detail = null) =>
		new() { Ok = false, Failure = failure, Detail = detail };
}

/// <summary>
/// Asks the configured vision model what is in a picture.
/// <para>
/// The image travels as an inline data URI in an OpenAI-compatible chat message, which is the shape the
/// NIM vision endpoint accepts. It is deliberately not uploaded anywhere first: there is no store, no
/// retention, and nothing left behind when the request finishes.
/// </para>
/// </summary>
public sealed class VisionClient(HttpClient http, JarvisSettingsStore settings, ILogger logger)
{
	private readonly HttpClient _http = http;
	private readonly JarvisSettingsStore _settings = settings;
	private readonly ILogger _logger = logger.ForContext<VisionClient>();

	/// <summary>
	/// Vision requests are slower and much larger than a text turn, so they get their own budget rather
	/// than inheriting the shared client's timeout.
	/// </summary>
	private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

	public async Task<VisionResult> DescribeAsync(
		ScreenCapture capture,
		string question,
		CancellationToken cancellationToken)
	{
		var settings = _settings.Current;

		// The same endpoint resolution the text client uses, so vision can never point somewhere the
		// rest of the plugin does not.
		if (!settings.HasLlmCredentials || ChatClient.ResolveChatUrl(settings) is not { } url)
		{
			return VisionResult.Failed(VisionFailure.NotConfigured);
		}

		var request = new ChatRequest
		{
			Model = settings.VisionModel,
			MaxTokens = 400,
			Messages =
			[
				new ChatMessage
				{
					Role = "user",
					Content =
					[
						new ContentPart { Type = "text", Text = Question },
						new ContentPart { Type = "image_url", ImageUrl = new ImageUrl { Url = capture.ToDataUri() } },
					],
				},
			],
		};

		using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		budget.CancelAfter(Budget);

		try
		{
			using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
			{
				Content = JsonContent.Create(request),
			};

			if (ChatClient.ResolveEndpoint(settings).Token is { } token)
			{
				httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			}

			using var response = await _http.SendAsync(httpRequest, budget.Token).ConfigureAwait(false);
			var body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				_logger.Warning("Vision request failed with {Status}.", (int)response.StatusCode);
				return VisionResult.Failed(VisionFailure.Rejected, Summarize(body));
			}

			var text = ReadAnswer(body);

			return string.IsNullOrWhiteSpace(text)
				? VisionResult.Failed(VisionFailure.Rejected, "the model returned no text")
				: VisionResult.Success(text);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return VisionResult.Failed(VisionFailure.Unreachable, "the vision request timed out");
		}
		catch (HttpRequestException exception)
		{
			return VisionResult.Failed(VisionFailure.Unreachable, exception.Message);
		}
	}

	/// <summary>
	/// Asks about the screen itself, which is what the tool the model can call does. The question is
	/// written for a model that has never been told what an operating system is.
	/// </summary>
	public Task<VisionResult> DescribeScreenAsync(
		ScreenCapture capture,
		string question,
		CancellationToken cancellationToken) =>
		DescribeAsync(capture, Question, cancellationToken);

	private const string Question = """
		Describe what is on this screen. Name the applications that are open and any text or
		error messages that are visible. Be specific and factual: if something is unreadable, say
		so rather than guessing. Answer in the same language the question was asked in.
		""";

	private static string ReadAnswer(string body)
	{
		using var document = JsonDocument.Parse(body);

		if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
		{
			return string.Empty;
		}

		return choices[0]
			.GetProperty("message")
			.GetProperty("content")
			.GetString() ?? string.Empty;
	}

	/// <summary>Keeps an endpoint's error readable without pasting a whole stack trace into a reply.</summary>
	private static string Summarize(string body)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			return "the endpoint returned no detail";
		}

		try
		{
			using var document = JsonDocument.Parse(body);

			if (document.RootElement.TryGetProperty("error", out var error)
				&& error.TryGetProperty("message", out var message))
			{
				return message.GetString() ?? body;
			}
		}
		catch (JsonException)
		{
			// Not JSON. The first line is usually the useful part.
		}

		var newline = body.IndexOf('\n', StringComparison.Ordinal);
		return newline > 0 ? body[..newline].Trim() : body.Trim();
	}

	private sealed record ChatRequest
	{
		[JsonPropertyName("model")]
		public required string Model { get; init; }

		[JsonPropertyName("messages")]
		public required IReadOnlyList<ChatMessage> Messages { get; init; }

		[JsonPropertyName("max_tokens")]
		public int MaxTokens { get; init; }
	}

	private sealed record ChatMessage
	{
		[JsonPropertyName("role")]
		public required string Role { get; init; }

		[JsonPropertyName("content")]
		public required IReadOnlyList<ContentPart> Content { get; init; }
	}

	private sealed record ContentPart
	{
		[JsonPropertyName("type")]
		public required string Type { get; init; }

		[JsonPropertyName("text")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Text { get; init; }

		[JsonPropertyName("image_url")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public ImageUrl? ImageUrl { get; init; }
	}

	private sealed record ImageUrl
	{
		[JsonPropertyName("url")]
		public required string Url { get; init; }
	}
}