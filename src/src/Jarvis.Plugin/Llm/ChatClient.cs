using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Plugin.Core;
using Serilog;

namespace Jarvis.Plugin.Llm;

public enum ModelFailure
{
	None,
	NotConfigured,
	Unauthorized,
	RateLimited,
	Unreachable,
	Timeout,
	NotFound,
	ProviderRejected,
	EmptyResponse,
}

/// <summary>
/// Talks to any OpenAI-compatible chat completions endpoint. NVIDIA NIM, a self-hosted NIM and a local
/// llama.cpp server all speak this shape, so one client covers every provider and the choice is only
/// a base URL, a model name and a bearer token.
/// </summary>
public sealed class ChatClient(
	IHttpClientFactory httpClientFactory,
	JarvisSettingsStore settings,
	ILogger logger)
{
	private const string HttpClientName = "nim";

	private readonly ILogger _logger = logger.ForContext<ChatClient>();

	public async Task<(ChatCompletion? Completion, ModelFailure Failure, string Detail)> CompleteAsync(
		ChatRequest request,
		Action<string>? onTextDelta,
		CancellationToken cancellationToken)
	{
		var current = settings.Current;

		if (!current.HasLlmCredentials)
		{
			return (null, ModelFailure.NotConfigured, "No provider credentials are configured.");
		}

		var (baseUrl, token) = ResolveEndpoint(current);

		if (baseUrl is null)
		{
			return (null, ModelFailure.NotConfigured, "The local model server address is not set.");
		}

		var client = httpClientFactory.CreateClient(HttpClientName);
		var payload = request.ToJson();

		using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions")
		{
			Content = JsonContent.Create(payload, options: StreamJsonOptions),
		};

		if (token is not null)
		{
			message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}

		HttpResponseMessage response;
		try
		{
			response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (HttpRequestException exception)
		{
			_logger.Warning(exception, "The model endpoint could not be reached.");
			return (null, ModelFailure.Unreachable, exception.Message);
		}
		catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return (null, ModelFailure.Unreachable, "The request timed out.");
		}

		using (response)
		{
			if (!response.IsSuccessStatusCode)
			{
				return await FailureFromResponseAsync(response, cancellationToken).ConfigureAwait(false);
			}

			try
			{
				await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
				var completion = await ReadStreamAsync(stream, onTextDelta, cancellationToken).ConfigureAwait(false);

				return completion is null
					? (null, ModelFailure.EmptyResponse, "The model returned no content.")
					: (completion, ModelFailure.None, string.Empty);
			}
			catch (JsonException exception)
			{
				_logger.Warning(exception, "The model response could not be read.");
				return (null, ModelFailure.ProviderRejected, "The model response could not be read.");
			}
		}
	}

	/// <summary>
	/// Probes whether a model id resolves. Used by the check action so a typo is reported as a typo
	/// rather than as a failed turn.
	/// </summary>
public async Task<ModelProbe> ProbeAsync(string model, CancellationToken cancellationToken)
	{
		var current = settings.Current;

		if (!current.HasLlmCredentials)
		{
			return new ModelProbe(ModelFailure.NotConfigured, "No provider credentials are configured.");
		}

		var (baseUrl, token) = ResolveEndpoint(current);

		if (baseUrl is null)
		{
			return new ModelProbe(ModelFailure.NotConfigured, "The local model server address is not set.");
		}

		var client = httpClientFactory.CreateClient(HttpClientName);

		using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		budget.CancelAfter(ProbeTimeout);

		using var message = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");

		if (token is not null)
		{
			message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}

		try
		{
			using var response = await client.SendAsync(message, budget.Token).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				return new ModelProbe(FailureFromStatus(response.StatusCode), $"HTTP {(int)response.StatusCode}.");
			}

			var body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
			var known = ExtractModelIds(body);

			return known.Count == 0 || known.Contains(model, StringComparer.Ordinal)
				? new ModelProbe(ModelFailure.None, $"Reachable. {known.Count} model(s) offered.")
				: new ModelProbe(ModelFailure.NotFound, $"Reachable, but {model} is not listed.");
		}
		catch (HttpRequestException exception)
		{
			return new ModelProbe(ModelFailure.Unreachable, exception.Message);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return new ModelProbe(ModelFailure.Timeout, $"No answer within {ProbeTimeout.TotalSeconds:0} seconds.");
		}
	}

	/// <summary>
	/// A reachability probe must never hold an invocation open. The shared client is on a two minute
	/// timeout, which is right for a real completion and far too long for a yes/no check.
	/// </summary>
	private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

/// <summary>
	/// Where the configured provider lives and what to authenticate with. Public because the vision client
	/// talks to the same endpoints and must not be able to disagree with the text client about which URL a
	/// provider means.
	/// </summary>
	public static (string? BaseUrl, string? Token) ResolveEndpoint(JarvisSettings current) => current.Llm switch
	{
		LlmProvider.NvidiaNim => (current.NvidiaBaseUrl, current.NvidiaApiKey),
		LlmProvider.SelfHostedNim => (current.SelfHostedBaseUrl, CurrentToken(current)),
		LlmProvider.LocalLlamaCpp => (LocalLlamaBaseUrl, null),
		_ => (null, null),
	};

	/// <summary>The full chat-completions URL for the configured provider, or null when there is none.</summary>
	public static string? ResolveChatUrl(JarvisSettings current) =>
		ResolveEndpoint(current).BaseUrl is { } baseUrl ? $"{baseUrl.TrimEnd('/')}/chat/completions" : null;

	private static string? CurrentToken(JarvisSettings current) =>
		string.IsNullOrWhiteSpace(current.SelfHostedToken) ? null : current.SelfHostedToken;

	private const string LocalLlamaBaseUrl = "http://127.0.0.1:8080/v1";

	private static readonly JsonSerializerOptions StreamJsonOptions = new()
	{
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	private static async Task<(ChatCompletion?, ModelFailure, string)> FailureFromResponseAsync(
		HttpResponseMessage response,
		CancellationToken cancellationToken)
	{
		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		var detail = ExtractErrorMessage(body);
		return (null, FailureFromStatus(response.StatusCode), detail);
	}

	private static ModelFailure FailureFromStatus(System.Net.HttpStatusCode status) => (int)status switch
	{
		401 or 403 => ModelFailure.Unauthorized,
		429 => ModelFailure.RateLimited,
		>= 500 => ModelFailure.Unreachable,
		_ => ModelFailure.ProviderRejected,
	};

	private static string ExtractErrorMessage(string body)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			return string.Empty;
		}

		try
		{
			if (JsonNode.Parse(body) is JsonObject root
				&& root["error"] is JsonObject error
				&& error["message"]?.GetValue<string>() is { Length: > 0 } message)
			{
				return message;
			}
		}
		catch (JsonException)
		{
			return body.Length > 300 ? body[..300] : body;
		}

		return body.Length > 300 ? body[..300] : body;
	}

	private static HashSet<string> ExtractModelIds(string body)
	{
		var ids = new HashSet<string>(StringComparer.Ordinal);

		try
		{
			if (JsonNode.Parse(body) is not JsonObject root || root["data"] is not JsonArray data)
			{
				return ids;
			}

			foreach (var item in data)
			{
				if (item?["id"]?.GetValue<string>() is { Length: > 0 } id)
				{
					ids.Add(id);
				}
			}
		}
		catch (JsonException)
		{
			return ids;
		}

		return ids;
	}

	/// <summary>
	/// Reads a server-sent event stream. Tool calls arrive as argument fragments keyed by index, so
	/// they are accumulated by index and only parsed once the stream ends.
	/// </summary>
	private static async Task<ChatCompletion?> ReadStreamAsync(
		Stream stream,
		Action<string>? onTextDelta,
		CancellationToken cancellationToken)
	{
		var content = new StringBuilder();
		var callNames = new Dictionary<int, string>();
		var callArguments = new Dictionary<int, StringBuilder>();
		var callIds = new Dictionary<int, string>();
		var finishReason = string.Empty;
		var sawAnything = false;

		await foreach (var line in ReadLinesAsync(stream, cancellationToken).ConfigureAwait(false))
		{
			if (line.Length == 0 || line[0] != ':')
			{
				if (!line.StartsWith("data:", StringComparison.Ordinal))
				{
					continue;
				}
			}

			var payload = line.StartsWith("data:", StringComparison.Ordinal) ? line[5..].Trim() : string.Empty;

			if (payload.Length == 0)
			{
				continue;
			}

			if (payload == "[DONE]")
			{
				break;
			}

			JsonNode? node;
			try
			{
				node = JsonNode.Parse(payload);
			}
			catch (JsonException)
			{
				continue;
			}

			if (node?["choices"] is not JsonArray choices || choices.Count == 0)
			{
				continue;
			}

			var choice = choices[0];
			sawAnything = true;

			if (choice?["finish_reason"]?.GetValue<string>() is { Length: > 0 } reason)
			{
				finishReason = reason;
			}

			if (choice?["delta"] is not JsonObject delta)
			{
				continue;
			}

			if (delta["content"]?.GetValue<string>() is { Length: > 0 } text)
			{
				content.Append(text);
				onTextDelta?.Invoke(text);
			}

			if (delta["tool_calls"] is JsonArray fragments)
			{
				foreach (var fragment in fragments)
				{
					if (fragment is not JsonObject call)
					{
						continue;
					}

					var index = call["index"]?.GetValue<int>() ?? 0;

					if (call["id"]?.GetValue<string>() is { Length: > 0 } id)
					{
						callIds[index] = id;
					}

					if (call["function"]?["name"]?.GetValue<string>() is { Length: > 0 } name)
					{
						callNames[index] = name;
					}

					if (call["function"]?["arguments"]?.GetValue<string>() is { Length: > 0 } arguments)
					{
						if (!callArguments.TryGetValue(index, out var builder))
						{
							builder = new StringBuilder();
							callArguments[index] = builder;
						}

						builder.Append(arguments);
					}
				}
			}
		}

		if (!sawAnything)
		{
			return null;
		}

		var calls = new List<ToolCall>();

		foreach (var index in callNames.Keys.Order())
		{
			calls.Add(new ToolCall
			{
				Id = callIds.TryGetValue(index, out var id) ? id : $"call_{index}",
				Name = callNames[index],
				Arguments = callArguments.TryGetValue(index, out var arguments) ? arguments.ToString() : "{}",
			});
		}

		return new ChatCompletion(
			content.ToString(),
			calls,
			finishReason.Equals("length", StringComparison.OrdinalIgnoreCase));
	}

	private static async IAsyncEnumerable<string> ReadLinesAsync(
		Stream stream,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);

		while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
		{
			yield return line;
		}
	}
}

public sealed record ModelProbe(ModelFailure Failure, string Detail)
{
	public bool IsReachable => Failure == ModelFailure.None;
}