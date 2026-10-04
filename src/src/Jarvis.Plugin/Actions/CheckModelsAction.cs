using System.Text.Json;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Llm;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Ui;
using Serilog;

namespace Jarvis.Plugin.Actions;

/// <summary>
/// Probes each configured endpoint and model so a typo or an unreachable provider is reported as
/// itself rather than as a failed turn later.
/// </summary>
public sealed class CheckModelsAction(ChatClient chat, JarvisSettingsStore settings, ILogger logger) : IActionDefinition
{
	private readonly ILogger Logger = logger;

	private const string ProviderParameter = "provider";

	public string Id => "jarvis-check-models";

	public LocalizedText Name => Strings.Actions.CheckModels.Name();

	public LocalizedText Description => Strings.Actions.CheckModels.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Choice(
			ProviderParameter,
			[
				new ActionParameterOption { Value = "llm", Label = Strings.Actions.CheckModels.Llm.Label() },
				new ActionParameterOption { Value = "vision", Label = Strings.Actions.CheckModels.Vision.Label() },
				new ActionParameterOption { Value = "both", Label = Strings.Actions.CheckModels.Both.Label() },
			],
			label: Strings.Actions.CheckModels.Provider.Label(),
			defaultValue: "llm",
			required: true),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows;

	public IActionExecutor CreateExecutor() => new Executor(chat, settings, Logger);

private sealed class Executor(ChatClient chat, JarvisSettingsStore settings, ILogger logger) : IActionExecutor
	{
		private readonly ILogger _logger = logger.ForContext<CheckModelsAction>();

		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var selection = ActionParameters.ReadText(context.Parameters, ProviderParameter) ?? "llm";

			if (selection is not ("llm" or "vision" or "both"))
			{
				return ActionResult.Failed(
					ActionErrorCodes.InvalidParameter,
					Strings.Actions.CheckModels.Provider.Unknown(selection));
			}

			var current = settings.Current;
			var probes = new List<(string Model, ModelProbe Probe)>();

			if (selection is "llm" or "both")
			{
				probes.Add((
					current.LlmModel,
					await chat.ProbeAsync(current.LlmModel, context.CancellationToken).ConfigureAwait(false)));
			}

			if (selection is "vision" or "both")
			{
				probes.Add((
					current.VisionModel,
					await chat.ProbeAsync(current.VisionModel, context.CancellationToken).ConfigureAwait(false)));
			}

			// Logged rather than shown in a modal. The modal named a view the plugin never declared, so it
			// could not render, and it also blocked on a person dismissing it inside the host's thirty second
			// capability bound. The result code carries the answer; the detail belongs in the log.
			foreach (var (model, probe) in probes)
			{
				_logger.Information("{Report}", Describe(model, probe));
			}

			// Every failure is mapped, not just an unreachable endpoint. Recognising one of nine and letting
			// the other eight fall through to success reported a rejected key, a rate limit, a misspelled
			// model and an unconfigured provider all as a working setup.
			string? worst = null;
			var worstRank = 0;

			foreach (var (_, probe) in probes)
			{
				var (rank, code) = Rank(probe.Failure);

				if (rank > worstRank)
				{
					worstRank = rank;
					worst = code;
				}
			}

			return worst is null
				? ActionResult.Success()
				: ActionResult.Failed(worst, Strings.Errors.ProviderUnreachable());
		}

		/// <summary>
		/// Ranks a failure so the most specific one wins when probes disagree, and carries the code to report.
		/// Ranked by hand because the codes are strings, and a null code means the probe succeeded.
		/// </summary>
		private static (int Rank, string? Code) Rank(ModelFailure failure) => failure switch
		{
			ModelFailure.None => (0, null),
			ModelFailure.NotConfigured => (1, ActionErrorCodes.NotConfigured),
			ModelFailure.Unauthorized => (2, ActionErrorCodes.PermissionDenied),
			ModelFailure.ProviderRejected => (3, ActionErrorCodes.ProviderRejected),
			ModelFailure.RateLimited => (4, ActionErrorCodes.Unavailable),
			ModelFailure.NotFound => (5, ActionErrorCodes.NotFound),
			ModelFailure.Unreachable => (6, ActionErrorCodes.NotConnected),
			ModelFailure.Timeout => (7, ActionErrorCodes.Timeout),
			_ => (8, ActionErrorCodes.ProviderError),
		};

		private static LocalizedText Describe(string model, ModelProbe probe)
		{
			var verdict = probe.Failure switch
			{
				ModelFailure.None => Strings.Actions.CheckModels.Verdict.Reachable(),
				ModelFailure.NotConfigured => Strings.Actions.CheckModels.Verdict.NotConfigured(),
				ModelFailure.Unauthorized => Strings.Actions.CheckModels.Verdict.Unauthorized(),
				ModelFailure.RateLimited => Strings.Actions.CheckModels.Verdict.RateLimited(),
				ModelFailure.Unreachable => Strings.Actions.CheckModels.Verdict.Unreachable(),
				ModelFailure.Timeout => Strings.Actions.CheckModels.Verdict.Timeout(),
				ModelFailure.NotFound => Strings.Actions.CheckModels.Verdict.NotFound(),
				ModelFailure.ProviderRejected => Strings.Actions.CheckModels.Verdict.Rejected(),
				_ => Strings.Actions.CheckModels.Verdict.Failed(),
			};

			return Strings.Actions.CheckModels.ReportLine(model, verdict);
		}
	}
}