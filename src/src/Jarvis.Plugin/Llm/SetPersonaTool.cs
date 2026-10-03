using System.Text.Json.Nodes;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Memory;
using Serilog;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// Lets the user change JARVIS's personality by asking it to, which the prompt explicitly permits.
/// <para>
/// Bounded by construction: the tool writes only the persona field, and the safety rules live in a separate
/// immutable prefix that <see cref="PersonaResolver"/> appends and never reads from settings. There is no
/// argument here that can reach them, which is the point - a bounded capability is one the model cannot
/// argue its way past.
/// </para>
/// <para>
/// Requires confirmation. A change to an assistant's own behaviour is not something it should do to itself
/// on request, and the user should read the new text before it takes effect.
/// </para>
/// </summary>
public sealed class SetPersonaTool(Action<PersonaPreset, string> apply, ILogger logger) : ITool
{
	public string Name => "set_persona";

	public bool RequiresConfirmation => true;

	public ToolDefinition Definition => new()
	{
		Name = Name,
		Description = "Change your own personality, but nothing else. Use this when the user asks how you "
			+ "should speak from now on, for example 'be more formal' or 'stop calling me sir'. You may only "
			+ "change your persona: never your hard rules, and never tool permissions.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["style"] = new JsonObject
				{
					["type"] = "string",
					["enum"] = new JsonArray("classic", "terse", "sarcastic", "formal", "custom"),
					["description"] = "The style to adopt. Choose 'custom' when the user describes something "
						+ "the named styles do not cover.",
				},
				["instruction"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "The exact new persona, in your own words. Required for 'custom', "
						+ "ignored otherwise.",
				},
			},
			["required"] = new JsonArray("style"),
		},
	};

	public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
	{
		var style = arguments["style"]?.GetValue<string>() ?? string.Empty;
		var instruction = arguments["instruction"]?.GetValue<string>() ?? string.Empty;

		if (!Enum.TryParse<PersonaPreset>(Normalise(style), true, out var preset))
		{
			return Task.FromResult(ToolOutcome.Failure(
				$"'{style}' is not a style. Use classic, terse, sarcastic, formal or custom."));
		}

		if (preset == PersonaPreset.Custom && string.IsNullOrWhiteSpace(instruction))
		{
			return Task.FromResult(ToolOutcome.Failure(
				"A custom style needs the instruction describing it."));
		}

		logger.Information("Persona changed to {Preset}.", preset);

		// The persona lives in the notes file rather than in the integration config: it is the assistant's own
		// state about itself, it must survive a config-flow rewrite, and a user can read and edit it.
		apply(preset, preset == PersonaPreset.Custom ? instruction.Trim() : string.Empty);

		return Task.FromResult(ToolOutcome.Success(
			preset == PersonaPreset.Custom
				? "Done. From now on I will follow that instruction."
				: $"Done. I will use the {preset.ToString().ToLowerInvariant()} style from now on."));
	}

	/// <summary>Accepts the kebab spelling the tool schema advertises as well as the CLR name.</summary>
	private static string Normalise(string style) =>
		Enum.TryParse<PersonaPreset>(style.Replace("-", string.Empty, StringComparison.Ordinal), true, out _)
			? style.Replace("-", string.Empty, StringComparison.Ordinal)
			: style;
}