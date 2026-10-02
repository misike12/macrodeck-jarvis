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
public sealed class CheckModelsAction(ChatClient chat, JarvisSettingsStore settings) : IActionDefinition
{
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

	public IActionExecutor CreateExecutor() => new Executor(chat, settings);

	private sealed class Executor(ChatClient chat, JarvisSettingsStore settings) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var selection = ActionParameters.ReadText(context.Parameters, ProviderParameter) ?? "llm";
			var current = settings.Current;
			var lines = new List<string>();

			if (selection is "llm" or "both")
			{
				lines.Add(await ProbeAsync(current.LlmModel, context.CancellationToken).ConfigureAwait(false));
			}

			if (selection is "vision" or "both")
			{
				lines.Add(await ProbeAsync(current.VisionModel, context.CancellationToken).ConfigureAwait(false));
			}

			var report = string.Join('\n', lines);

if (context.Ui is { } ui)
			{
				await ui.ShowModalAsync(
					context.OriginClientId,
					new ModalDefinition
					{
						ViewId = "jarvis-model-report",
						Title = Strings.Actions.CheckModels.ReportTitle(),
						Data = ModelsReport(report),
					},
					context.CancellationToken).ConfigureAwait(false);
			}

			return lines.Any(line => line.Contains("(unreachable)", StringComparison.Ordinal))
				? ActionResult.Failed(ActionErrorCodes.NotConnected, Strings.Errors.ProviderUnreachable())
				: ActionResult.Success();
		}

		private async Task<string> ProbeAsync(string model, CancellationToken cancellationToken)
		{
			var probe = await chat.ProbeAsync(model, cancellationToken).ConfigureAwait(false);
			var verdict = probe.IsReachable ? "ok" : $"({probe.Failure.ToString().ToLowerInvariant()})";

			return $"{model}: {verdict} - {probe.Detail}";
		}

		private static Dictionary<string, JsonElement> ModelsReport(string report)
		{
			return new Dictionary<string, JsonElement>
			{
				["report"] = JsonDocument.Parse(JsonSerializer.Serialize(report)).RootElement.Clone(),
			};
		}
	}
}