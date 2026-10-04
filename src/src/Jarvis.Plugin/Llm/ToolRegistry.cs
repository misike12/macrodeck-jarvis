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
/// A tool that is not always worth offering.
/// <para>
/// The registry is built once, so a tool whose availability depends on a setting or on another process
/// cannot be registered or unregistered to match. Reading <see cref="IsAvailable"/> at the moment the
/// catalogue is built means a change takes effect on the next turn rather than after a restart, which is
/// the difference between a setting that works and one that appears not to.
/// </para>
/// </summary>
public interface IConditionalTool
{
	/// <summary>
	/// Whether the tool should be offered right now, and why not when it should not. The reason is shown to
	/// the model so it can tell the user something better than "that failed".
	/// </summary>
	(bool Available, string UnavailableReason) DescribeAvailability();
}

/// <summary>
/// A tool that is not available, reduced to the two things the registry needs.
/// </summary>
public readonly record struct ToolAvailability(bool Available, string UnavailableReason)
{
	public static ToolAvailability Always => new(true, string.Empty);

	public static ToolAvailability Never(string reason) => new(false, reason);
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

	/// <summary>
	/// How long a tool call waits for a person to answer before it gives up.
	/// <para>
	/// Short on purpose. This used to be ten minutes on the reasoning that a person has to notice the
	/// question and reach for the pad, which is true, and on the bound being needed at all, which is also
	/// true, but the two do not compose: every caller gives up first. The say action is cancelled by the host
	/// after twenty seconds, so a ten minute wait could only ever end as the caller being cancelled. The turn
	/// died with "that was cancelled before JARVIS answered" and the refusal this bound exists to produce was
	/// never written, so an unanswered question cost the user both the answer and the reason.
	/// </para>
	/// <para>
	/// Twelve seconds is what a person actually needs to answer a yes or no, and it is short enough to land
	/// inside the caller's own patience, so the turn ends with the refusal the model can explain rather than
	/// with a timeout the user cannot interpret.
	/// </para>
	/// </summary>
	private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(12);

	public IReadOnlyCollection<string> Names => _tools.Keys;

	public void Register(ITool tool) => _tools[tool.Name] = tool;

	/// <summary>
	/// The tools the model may call, skipping any that is currently unavailable.
	/// </summary>
	public IReadOnlyList<ToolDefinition> Definitions =>
		_tools.Values
			.Select(Describe)
			.Where(tool => tool.Definition is not null)
			.Select(tool => tool.Definition!)
			.ToArray();

	/// <summary>
	/// A tool and, when it is unavailable, why. Null is used rather than a filtered second pass so the
	/// reason is available to a caller that got the tool by name, which is what an invoke does.
	/// </summary>
	private (ToolDefinition? Definition, string Reason) Describe(ITool tool)
	{
		if (tool is not IConditionalTool conditional)
		{
			return (tool.Definition, string.Empty);
		}

		var (available, reason) = conditional.DescribeAvailability();

		return available ? (tool.Definition, string.Empty) : (null, reason);
	}

	/// <summary>Why a tool by name is not currently callable, or an empty string when it is.</summary>
	public string UnavailableReason(string name) =>
		_tools.TryGetValue(name, out var tool) ? Describe(tool).Reason : $"There is no tool called {name}.";

	public async Task<ToolOutcome> InvokeAsync(ToolCall call, CancellationToken cancellationToken)
	{
		if (!_tools.TryGetValue(call.Name, out var tool))
		{
			return ToolOutcome.Failure($"There is no tool called {call.Name}.");
		}

		var availability = Describe(tool);

		if (availability.Definition is null)
		{
			// Refused with the reason rather than as an exception. The model is the one that has to explain
			// this to the user, so it needs to know which of "switched off" and "not installed" applies.
			return ToolOutcome.Failure(availability.Reason);
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

			// Bounded, because nothing else is. The turn's own token covers a cancel, but if the
			// confirmation is never shown, or is shown on a surface that is not being looked at, the wait
			// has no natural end and the turn never finishes. A generous bound still lets a person take
			// their time, and an unanswered question becomes a refusal rather than a wedged session.
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			deadline.CancelAfter(ConfirmationTimeout);

			try
			{
				approved = await _session.WaitForDecisionAsync(deadline.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return ToolOutcome.Failure($"{tool.Name} was cancelled before it was approved.");
			}
			catch (OperationCanceledException)
			{
				_logger.Warning("{Tool} was never approved within {Seconds} seconds.", tool.Name, ConfirmationTimeout.TotalSeconds);

				_session.ResolveConfirmation(approved: false);

				return ToolOutcome.Failure(
					$"{tool.Name} timed out waiting for approval, so it was not run.");
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