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

		if (RequiresApproval(tool, current, typed))
		{
			_logger.Information("{Tool} needs confirmation under safety mode {Mode}.", tool.Name, current.Safety);

			_session.RequestConfirmation(tool.Name, typed.ToJsonString());

			bool approved;

			try
			{
				approved = await _session.WaitForDecisionAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return ToolOutcome.Failure($"{tool.Name} was cancelled before it was approved.");
			}

			if (!approved)
			{
				return ToolOutcome.Failure(
					"That action was not approved, so it was not run. Say so plainly if you are asked about it.");
			}

			// The decision covered this one call. A tool that loops would otherwise inherit a single yes for
			// every iteration, so the gate is re-evaluated for each call.
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

	/// <summary>
	/// Whether this call has to stop and ask. Reads are free and run without asking; anything that acts
	/// asks under every mode except <see cref="SafetyMode.Autonomous"/>, and an allowlisted command runs
	/// straight through.
	/// </summary>
	private static bool RequiresApproval(ITool tool, JarvisSettings settings, JsonObject arguments)
	{
		if (!tool.RequiresConfirmation)
		{
			return false;
		}

		return settings.Safety switch
		{
			SafetyMode.Autonomous => false,
			SafetyMode.Allowlist => !IsAllowlisted(tool, settings, arguments),
			// Tool-permissions is a real branch rather than falling through to the same answer as
			// confirm-all, which is what made the two modes indistinguishable.
			SafetyMode.ToolPermissions => !IsPermitted(tool, settings),
			_ => true,
		};
	}

	/// <summary>
	/// Per-tool-class permission for the mode that trades a single yes/no for a standing decision. The
	/// classes are coarse on purpose: a fine-grained permission list is a settings UI nobody will fill in,
	/// and a coarse one the user can reason about beats a fine one they leave at its default.
	/// </summary>
	private static bool IsPermitted(ITool tool, JarvisSettings settings)
	{
		var classification = Classify(tool);

		return classification switch
		{
			ToolClass.Read => settings.PermitRead,
			ToolClass.Execute => settings.PermitExecute,
			ToolClass.Write => settings.PermitWrite,
			_ => false,
		};
	}

	/// <summary>Which permission class a tool belongs to. Defaults to the most cautious class.</summary>
	internal static ToolClass Classify(ITool tool) => tool switch
	{
		ReadFileTool or ListDirectoryTool or DesktopTools.ListProcessesTool or DesktopTools.ClipboardReadTool
			=> ToolClass.Read,

		ShellTool or DesktopTools.KillProcessTool or DesktopTools.SetVolumeTool => ToolClass.Execute,

		WriteFileTool or DesktopTools.ClipboardWriteTool or ScreenshotTool or SetPersonaTool => ToolClass.Write,

		_ => ToolClass.Other,
	};

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