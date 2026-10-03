using Jarvis.Plugin.Actions;
using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Input;
using Jarvis.Plugin.Llm;
using Jarvis.Plugin.Orb;
using Jarvis.Plugin.Runtime;
using Jarvis.Plugin.Speech;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Issues;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Variables;
using MacroDeck.Sdk.Widgets;
using Serilog;

namespace Jarvis.Plugin;

/// <summary>
/// The integration. Capability opt-in happens by implementing the interface here; each one is
/// registered automatically by <c>RegisterIntegration</c> so no capability handler is written by hand.
/// </summary>
public sealed class PluginIntegration : IPluginIntegration, IConfigFlowProvider, IVariableProvider, IWidgetTypeProvider, IUiProvider, IIntegrationIssueProvider
{
	private readonly ILogger _logger;
	private readonly JarvisSettingsStore _settings;
	private readonly AssistantStateHolder _state;
	private readonly AssistantSession _session;
	private readonly OrbWidgetTypeProvider _widgetTypes;
	private readonly MicrophoneMonitor _microphone;
private readonly RuntimeManager _runtime;
	private readonly ListeningPipeline _listening;
	private readonly GlobalHotkey _hotkey;
	private IUiResourceRegistry? _resources;

	public PluginIntegration(
		ILogger logger,
		ChatClient chat,
		JarvisSettingsStore settings,
		AssistantStateHolder state,
		AssistantSession session,
		MicrophoneMonitor microphone,
		RuntimeManager runtime,
		ListeningPipeline listening,
		GlobalHotkey hotkey)
	{
		_logger = logger.ForContext<PluginIntegration>();
		_settings = settings;
		_state = state;
		_session = session;
		_microphone = microphone;
		_runtime = runtime;
		_listening = listening;
		_hotkey = hotkey;

		_widgetTypes = new OrbWidgetTypeProvider(logger);

		Actions =
		[
			new ActivateAction(session, listening),
			new CancelAction(session),
			new ToggleAction(session),
			new SayAction(session),
			new CheckModelsAction(chat, settings),
			new ManageComponentsAction(runtime),
		];

		Variables =
		[
			VariableDefinition.Eager("jarvis_state", VariableType.Text) with { Id = "state", Description = Strings.Variables.State.Description() },
			VariableDefinition.Eager("jarvis_transcript", VariableType.Text) with { Id = "transcript", Description = Strings.Variables.Transcript.Description() },
			VariableDefinition.Eager("jarvis_reply", VariableType.Text) with { Id = "reply", Description = Strings.Variables.Reply.Description() },
			VariableDefinition.Eager("jarvis_amplitude", VariableType.Numeric, decimalPlaces: 3) with { Id = "amplitude", Description = Strings.Variables.Amplitude.Description() },
			VariableDefinition.Eager("jarvis_download_percent", VariableType.Numeric, decimalPlaces: 0, refreshInterval: TimeSpan.FromSeconds(1)) with
			{
				Id = "download-percent",
				Description = Strings.Variables.DownloadPercent.Description(),
				SemanticKind = VariableSemanticKinds.Percentage,
			},
			VariableDefinition.Eager("jarvis_download_label", VariableType.Text, refreshInterval: TimeSpan.FromSeconds(1)) with
			{
				Id = "download-label",
				Description = Strings.Variables.DownloadLabel.Description(),
			},
		];
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	public IReadOnlyList<VariableDefinition> Variables { get; }

	/// <summary>
	/// Download problems surface here rather than as a failed action. The host polls this, so the manager
	/// holds the state and the integration only forwards it.
	/// </summary>
	public Task<IReadOnlyList<IntegrationIssue>> GetIssuesAsync(CancellationToken cancellationToken = default) =>
		_runtime.GetIssuesAsync(cancellationToken);

	public Task<IssueResolution> ResolveIssueAsync(string issueId, CancellationToken cancellationToken = default) =>
		_runtime.ResolveIssueAsync(issueId, cancellationToken);

	public IConfigFlow CreateConfigFlow() => new JarvisConfigFlow();

	public bool AllowsMultipleConfigurations => false;

	/// <summary>
	/// Runs on every session establishment, not once at process start, so it must be safe to repeat.
	/// </summary>
	public Task InitializeAsync(IIntegrationContext context)
	{
		return InitializeCoreAsync(context, CancellationToken.None);
	}

private async Task InitializeCoreAsync(IIntegrationContext context, CancellationToken cancellationToken)
	{
		_resources = context.UiResources;
		await _settings.ReloadAsync(context, cancellationToken).ConfigureAwait(false);

		var settings = _settings.Current;

		_logger.Information(
			"Initialized. Provider {Provider}, model {Model}, configured {Configured}.",
			settings.Llm,
			settings.LlmModel,
			settings.HasLlmCredentials);

		if (settings.MicrophoneAlwaysOn)
		{
			if (_microphone.Start(settings.MicrophoneId, settings.MicrophoneName))
			{
				_logger.Information("Microphone is live on {Device}.", _microphone.ActiveDeviceName);
			}
			else
			{
				_logger.Warning("No microphone: the orb will not react to audio. {Error}", _microphone.LastError);
			}
		}

		RegisterHotkey(settings);

		if (!settings.HasLlmCredentials)
		{
			_state.Transition(AssistantState.Unavailable);
		}
		else
		{
			_state.Reset();
		}
	}

	/// <summary>
	/// Binds the global hotkey. Registration can legitimately fail because another program owns the chord,
	/// so the failure becomes an issue the user can act on rather than a hotkey that silently does nothing.
	/// </summary>
	private void RegisterHotkey(JarvisSettings settings)
	{
		if (HotkeyChord.Parse(settings.PushToTalkHotkey) is not { } chord)
		{
			_logger.Debug("No global hotkey is configured.");
			return;
		}

		_hotkey.Pressed -= OnHotkeyPressed;
		_hotkey.Pressed += OnHotkeyPressed;

		if (_hotkey.Register(chord))
		{
			return;
		}

		_logger.Warning("The global hotkey {Chord} is not available. {Reason}", chord, _hotkey.LastError);
	}

	/// <summary>
	/// The hotkey does what a press of the button does. The press is fire-and-forget because it runs on
	/// the hotkey's own message thread: blocking there would stop the hotkey from being seen again.
	/// </summary>
	private void OnHotkeyPressed()
	{
		_ = Task.Run(async () =>
		{
			try
			{
				await _listening.ListenAndAnswerAsync(prompt: null, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				_logger.Warning(exception, "The hotkey turn failed.");
			}
		});
	}

	public Task ShutdownAsync()
	{
		_microphone.Stop();
		_session.Cancel(false);
		_state.Reset();
		return Task.CompletedTask;
	}

	public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var snapshot = _state.Current;
		var download = _runtime.CurrentProgress;

		var reading = localId switch
		{
			"state" => VariableReading.Of(snapshot.State.ToString().ToLowerInvariant()),
			"transcript" => VariableReading.Of(snapshot.Transcript),
			"reply" => VariableReading.Of(snapshot.Reply),
			"amplitude" => VariableReading.Of(snapshot.Amplitude, 0, 1, 0.01),
			"download-percent" => VariableReading.Of(download.Active ? download.Percent : 0d, 0, 100, 1),
			"download-label" => VariableReading.Of(Describe(download)),
			_ => VariableReading.Unavailable,
		};

		return ValueTask.FromResult(reading);
	}

	/// <summary>
	/// The label names what is happening and how far it got, so a button bound to it says something useful
	/// rather than only moving a percentage bar somewhere else.
	/// </summary>
	private static string Describe(RuntimeProgress progress) => progress.AssetId switch
	{
		"" or null => string.Empty,
		_ when !progress.Active => $"{progress.AssetId} done",
		_ => $"{progress.AssetId} {progress.Phase} {progress.Percent}%",
	};

	/// <summary>
	/// Widget types register after the integration initializes and before any other provider hook, and
	/// are withdrawn when the integration stops.
	/// </summary>
	public Task InitializeAsync(IWidgetTypeProviderContext context, CancellationToken cancellationToken = default) =>
		_widgetTypes.InitializeAsync(context, cancellationToken);

	public IReadOnlyList<WidgetTypeDescriptor> GetWidgetTypes() => _widgetTypes.GetWidgetTypes();

	public IReadOnlyList<UiSurfaceDeclaration> Surfaces => OrbUiProvider.DeclaredSurfaces;

	public Task<IUiSession?> CreateSessionAsync(UiSessionRequest request, CancellationToken cancellationToken) =>
		_resources is { } registry
			? new OrbUiProvider(_state, registry, _logger).CreateSessionAsync(request, cancellationToken)
			: Task.FromResult<IUiSession?>(null);
}