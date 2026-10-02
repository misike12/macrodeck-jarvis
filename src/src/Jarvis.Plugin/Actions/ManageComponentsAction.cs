using Jarvis.Plugin.Runtime;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace Jarvis.Plugin.Actions;

/// <summary>
/// Installs one of the native components on demand. This is the user-facing trigger for the download
/// manager: nothing is fetched at start-up, so a component that is not installed has to be asked for
/// explicitly, by a button or by the setup flow.
/// <para>
/// The result is truthful about which component it managed, because a component is several files and a
/// partial component is not a usable one: the action fails with what was missing rather than claiming a
/// success the next turn cannot build on.
/// </para>
public sealed class ManageComponentsAction(RuntimeManager runtime) : IActionDefinition
{
	private const string ComponentParameter = "component";

	public string Id => "jarvis-manage-components";

	public LocalizedText Name => Strings.Actions.ManageComponents.Name();

	public LocalizedText Description => Strings.Actions.ManageComponents.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Choice(
			ComponentParameter,
			[.. AssetCatalog.Components.Select(component => new ActionParameterOption
			{
				Value = component,
				Label = Describe(component),
			})],
			label: Strings.Actions.ManageComponents.Component.Label(),
			required: true),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows;

	public IActionExecutor CreateExecutor() => new Executor(runtime);

	/// <summary>A component is named by what it does, not by the folder it lands in.</summary>
	private static LocalizedText Describe(string component) => component switch
	{
		AssetCatalog.Piper => Strings.Actions.ManageComponents.Component.Piper(),
		AssetCatalog.Whisper => Strings.Actions.ManageComponents.Component.Whisper(),
		_ => component,
	};

	private sealed class Executor(RuntimeManager runtime) : IActionExecutor
	{
		private readonly ILogger _logger = runtime.Logger;

		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var component = ActionParameters.ReadText(context.Parameters, ComponentParameter);

			if (string.IsNullOrWhiteSpace(component))
			{
				return ActionResult.Failed(
					ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Actions.ManageComponents.Component.Label()));
			}

			var assets = AssetCatalog.ForComponent(component);

			if (assets.Count == 0)
			{
				return ActionResult.Failed(
					ActionErrorCodes.InvalidParameter,
					Strings.Runtime.UnknownComponent(component));
			}

			var result = await runtime
				.EnsureComponentAsync(component, runtime.Progress, context.CancellationToken)
				.ConfigureAwait(false);

			if (result.Installed)
			{
				return result.AlreadyPresent
					? ActionResult.Success()
					: ActionResult.Accepted(Strings.Runtime.Installed(component));
			}

			// The manager has already turned this into an issue the user can retry from, so the action only
			// has to say plainly that nothing was installed.
			return ActionResult.Failed(
				result.Failure == AssetFailure.Unreachable ? ActionErrorCodes.NotConnected : ActionErrorCodes.ProviderError,
				Strings.Runtime.InstallFailed(component));
		}
	}
}
