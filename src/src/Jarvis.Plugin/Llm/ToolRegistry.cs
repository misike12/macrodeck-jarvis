using System.Text.Json.Nodes;
using Jarvis.Plugin.Core;
using Serilog;

namespace Jarvis.Plugin.Llm;

public sealed record ToolOutcome(bool Ok, string Content)
{
	public static ToolOutcome Success(string content) => new(true, content);

	public static ToolOutcome Failure(string content) => new(false, content);
}

/// <summary>
/// A capability JARVIS can be asked to use. <see cref="RequiresConfirmation"/> is what the safety
/// mode is applied to, so the decision lives with the tool rather than being re-derived per call site.
/// </summary>
public interface ITool
{
	string Name { get; }

	ToolDefinition Definition { get; }

	bool RequiresConfirmation { get; }

	Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Holds the tools the model may call and enforces the configured safety mode before any of them runs.
/// A refusal is returned to the model as a tool result rather than thrown, so the model can explain
/// itself instead of the turn dying.
/// </summary>
public sealed class ToolRegistry(
	JarvisSettingsStore settings,
	AssistantSession session,
	ILogger logger)
{
	private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);
	private readonly JarvisSettingsStore _settings = settings;
	private readonly AssistantSession _session = session;
	private readonly ILogger _logger = logger.ForContext<ToolRegistry>();

	public IReadOnlyCollection<string> Names => _tools.Keys;

	public void Register(ITool tool) => _tools[tool.Name] = tool;

	public IReadOnlyList<ToolDefinition> Definitions =>
		_tools.Values.Select(tool => tool.Definition).ToArray();

	public async Task<ToolOutcome> InvokeAsync(ToolCall call, CancellationToken cancellationToken)
	{
		if (!_tools.TryGetValue(call.Name, out var tool))
		{
			return ToolOutcome.Failure($"There is no tool called {call.Name}.");
		}

		var arguments = call.ParseArguments();

		if (arguments is not JsonObject typed)
		{
			return ToolOutcome.Failure($"The arguments for {call.Name} were not a JSON object.");
		}

		var current = _settings.Current;

		if (!IsAllowed(tool, current, typed))
		{
			_logger.Information("Blocked {Tool} by safety mode {Mode}.", tool.Name, current.Safety);
			_session.RequestConfirmation(tool.Name, typed.ToJsonString());
			return ToolOutcome.Failure("That action needs confirmation and none was given, so it was not run.");
		}

		try
		{
			return await tool.InvokeAsync(typed, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return ToolOutcome.Failure($"{tool.Name} was cancelled.");
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Warning(exception, "{Tool} failed.", tool.Name);
			return ToolOutcome.Failure($"{tool.Name} failed: {exception.Message}");
		}
	}

	private static bool IsAllowed(ITool tool, JarvisSettings settings, JsonObject arguments)
	{
		if (!tool.RequiresConfirmation)
		{
			return true;
		}

		return settings.Safety switch
		{
			SafetyMode.Autonomous => true,
			SafetyMode.Allowlist => IsAllowlisted(tool, settings, arguments),
			_ => false,
		};
	}

	private static bool IsAllowlisted(ITool tool, JarvisSettings settings, JsonObject arguments)
	{
		if (tool is not ShellTool)
		{
			return false;
		}

		var command = arguments["command"]?.GetValue<string>();

		if (string.IsNullOrWhiteSpace(command))
		{
			return false;
		}

		var executable = command
			.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault() ?? string.Empty;

		return settings.CommandAllowlist.Any(allowed =>
			string.Equals(allowed, executable, StringComparison.OrdinalIgnoreCase));
	}
}