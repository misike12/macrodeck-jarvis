using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// One message in a conversation. Held as a <see cref="JsonObject"/> rather than a typed record
/// because an assistant message can carry tool calls, a tool result must echo its call id verbatim,
/// and a system message may carry only text: one shape with optional members round-trips all three
/// without losing anything the provider sent.
/// </summary>
public sealed record ChatMessage
{
	public required string Role { get; init; }

	public string? Content { get; init; }

	public string? ToolCallId { get; init; }

	public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

	public static ChatMessage System(string content) => new() { Role = "system", Content = content };

	public static ChatMessage User(string content) => new() { Role = "user", Content = content };

	public static ChatMessage Assistant(string? content, IReadOnlyList<ToolCall> toolCalls) =>
		new() { Role = "assistant", Content = content, ToolCalls = toolCalls };

	public static ChatMessage ToolResult(string toolCallId, string content) =>
		new() { Role = "tool", ToolCallId = toolCallId, Content = content };

	public JsonObject ToJson()
	{
		var node = new JsonObject { ["role"] = Role };

		if (Content is not null)
		{
			node["content"] = Content;
		}

		if (ToolCallId is not null)
		{
			node["tool_call_id"] = ToolCallId;
		}

		if (ToolCalls.Count > 0)
		{
			var calls = new JsonArray();
			foreach (var call in ToolCalls)
			{
				calls.Add(new JsonObject
				{
					["id"] = call.Id,
					["type"] = "function",
					["function"] = new JsonObject
					{
						["name"] = call.Name,
						["arguments"] = call.Arguments,
					},
				});
			}

			node["tool_calls"] = calls;
		}

		return node;
	}
}

public sealed record ToolCall
{
	public required string Id { get; init; }

	public required string Name { get; init; }

	public required string Arguments { get; init; }

	public JsonNode? ParseArguments()
	{
		if (string.IsNullOrWhiteSpace(Arguments))
		{
			return new JsonObject();
		}

		try
		{
			return JsonNode.Parse(Arguments);
		}
		catch (JsonException)
		{
			return null;
		}
	}
}

/// <summary>A tool the model may call, described in the shape OpenAI-compatible endpoints expect.</summary>
public sealed record ToolDefinition
{
	public required string Name { get; init; }

	public required string Description { get; init; }

	public required JsonObject Parameters { get; init; }

	public JsonObject ToJson() => new()
	{
		["type"] = "function",
		["function"] = new JsonObject
		{
			["name"] = Name,
			["description"] = Description,
			["parameters"] = Parameters.DeepClone(),
		},
	};
}

/// <summary>How a single streamed completion ended, or why it did not.</summary>
public sealed record ChatCompletion(
	string Content,
	IReadOnlyList<ToolCall> ToolCalls,
	bool Truncated)
{
	public bool WantsTools => ToolCalls.Count > 0;
}

public sealed class ChatRequest
{
	public required string Model { get; init; }

	public required IReadOnlyList<ChatMessage> Messages { get; init; }

	public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];

	public double Temperature { get; init; } = 0.4;

	public int MaxTokens { get; init; } = 1024;

	public JsonObject ToJson()
	{
		var messages = new JsonArray();
		foreach (var message in Messages)
		{
			messages.Add(message.ToJson());
		}

		var node = new JsonObject
		{
			["model"] = Model,
			["messages"] = messages,
			["stream"] = true,
			["temperature"] = Temperature,
			["max_tokens"] = MaxTokens,
		};

		if (Tools.Count > 0)
		{
			var tools = new JsonArray();
			foreach (var tool in Tools)
			{
				tools.Add(tool.ToJson());
			}

			node["tools"] = tools;
			node["tool_choice"] = "auto";
		}

		return node;
	}
}