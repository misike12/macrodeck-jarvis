using System.Globalization;
using Jarvis.Plugin.Actions;
using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Input;
using Jarvis.Plugin.Llm;
using Jarvis.Plugin.Orb;
using Jarvis.Plugin.Runtime;
using Jarvis.Plugin.Speech;
using MacroDeck.Localization;
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
/// <remarks>
/// No <c>IPluginCatalogNotifier</c> is injected, deliberately. Every catalogue this integration serves is
/// fixed once the process starts: the widget types come from a static list, the variable definitions are
/// six constants, and no instance or event catalogue is declared at all. Nothing outside a host-initiated
/// invocation can change what a later <c>describe</c> would answer, so there is no staleness to announce.
///
/// The one thing that does change with the environment, whether the elevated service is answering, is not
/// a host catalogue. It governs JARVIS's own model-facing tool list, which the host never sees, so it is
/// read when that list is built rather than pushed.
/// </remarks>
public sealed class PluginIntegration : IPluginIntegration, IConfigFlowProvider, IVariableProvider, IWidgetTypeProvider, IUiProvider, IIntegrationIssueProvider
{
	private readonly ILogger _logger;
	private readonly JarvisSettingsStore _settings;
	private readonly AssistantStateHolder _state;
	private readonly AssistantSession _session;
	private readonly OrbWidgetTypeProvider _widgetTypes;
	private readonly MicrophoneMonitor _microphone;	private readonly RuntimeManager _runtime;
	private readonly NimSpeechTranscriber _nimTranscriber;
	private readonly ListeningPipeline _listening;
	private readonly GlobalHotkey _hotkey;
	private readonly WakeWordDetector _wakeWord;
	private readonly OpenWakeWordEngineHost _wakeEngine;
	private readonly WhisperTranscriber _transcriber;
	private readonly ServiceAvailability _serviceAvailability;

	/// <summary>
	/// The host's UI resource registry, written by InitializeAsync and read by every session.
	/// <para>
	/// Volatile because those are different threads and there is no lock between them: the host runs up to
	/// 32 invocations concurrently, and a session created while a reconnect is establishing can otherwise
	/// read a stale reference and build a session against the previous registry.
	/// </para>
	/// </summary>
	private volatile IUiResourceRegistry? _resources;

	/// <summary>
	/// Why the microphone could not be opened, or null when it could. Held rather than logged and forgotten
	/// so it can become an issue the user can see and retry, which a log line in a viewer they may not have
	/// open is not.
	/// </summary>
	private string? _microphoneFailure;

	/// <summary>Why the global hotkey could not be registered, or null when it could.</summary>
	private string? _hotkeyFailure;

	private const string MicrophoneIssueId = "microphone-unavailable";

	private const string HotkeyIssueId = "hotkey-unavailable";

	public PluginIntegration(
		ILogger logger,
		ChatClient chat,
		JarvisSettingsStore settings,
		AssistantStateHolder state,
		AssistantSession session,
		MicrophoneMonitor microphone,
		RuntimeManager runtime,
		ListeningPipeline listening,
		GlobalHotkey hotkey,
		WakeWordDetector wakeWord,
		OpenWakeWordEngineHost wakeEngine,
WhisperTranscriber transcriber,
		ServiceAvailability serviceAvailability)
	{
		_logger = logger.ForContext<PluginIntegration>();
		_settings = settings;
		_state = state;
		_session = session;
		_microphone = microphone;
		_runtime = runtime;
		_listening = listening;
		_hotkey = hotkey;
		_wakeWord = wakeWord;
		_wakeEngine = wakeEngine;
		_transcriber = transcriber;
		_nimTranscriber = new NimSpeechTranscriber(runtime, logger);
		_transcriber.NimTranscriber = _nimTranscriber;
		_serviceAvailability = serviceAvailability;

		_widgetTypes = new OrbWidgetTypeProvider(logger);

		Actions =
		[
			new ActivateAction(session, listening),
			new CancelAction(session),
			new ToggleAction(session, listening),
			new SayAction(session),
			new CheckModelsAction(chat, settings, logger),
			new ManageComponentsAction(runtime),
			new ConfirmAction(session, logger),
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
public async Task<IReadOnlyList<IntegrationIssue>> GetIssuesAsync(CancellationToken cancellationToken = default)
	{
		var issues = new List<IntegrationIssue>(await _runtime.GetIssuesAsync(cancellationToken).ConfigureAwait(false));

		// The two standing conditions. Both used to be a log line and nothing else, so a microphone that
		// would not open looked exactly like a microphone that was not needed: the orb simply sat still and
		// the log said why to whoever happened to be reading it.
		if (_microphoneFailure is { } microphone)
		{
			issues.Add(new IntegrationIssue
			{
				Id = MicrophoneIssueId,
				Title = Strings.Runtime.Issue.Microphone.Title(),
				Description = Strings.Runtime.Issue.Microphone.Description(microphone),
				ActionLabel = Strings.Runtime.Issue.Action(),
				Severity = IntegrationIssueSeverity.Warning,
			});
		}

		if (_hotkeyFailure is { } hotkey)
		{
			issues.Add(new IntegrationIssue
			{
				Id = HotkeyIssueId,
				Title = Strings.Runtime.Issue.Hotkey.Title(),
				Description = Strings.Runtime.Issue.Hotkey.Description(hotkey),
				ActionLabel = Strings.Runtime.Issue.Action(),
				Severity = IntegrationIssueSeverity.Warning,
			});
		}

		return issues;
	}

	public async Task<IssueResolution> ResolveIssueAsync(string issueId, CancellationToken cancellationToken = default)
	{
		switch (issueId)
		{
			case MicrophoneIssueId:
			{
				var settings = _settings.Current;

				if (_microphone.Start(settings.MicrophoneId, settings.MicrophoneName))
				{
					_microphoneFailure = null;
					return IssueResolution.Ok();
				}

				return IssueResolution.Failed(Strings.Runtime.IssueStillFailing());
			}

			case HotkeyIssueId:
				// The same path initialization takes, rather than a second implementation of it, so a retry
				// cannot succeed under different rules than the attempt that failed.
				RegisterHotkey(_settings.Current);

				return _hotkeyFailure is null
					? IssueResolution.Ok()
					: IssueResolution.Failed(Strings.Runtime.IssueStillFailing());

			default:
				return await _runtime.ResolveIssueAsync(issueId, cancellationToken).ConfigureAwait(false);
		}
	}

	// The store is handed over so a re-opened setup form can show what is actually configured. The host
	// prefills the first step from the stored entry and then discards that prefill, rebuilding each later
	// step from the declared defaults, so without this the form contradicts the running configuration.
	public IConfigFlow CreateConfigFlow() => new JarvisConfigFlow(_logger, _settings);

	public bool AllowsMultipleConfigurations => false;

	/// <summary>
	/// Stated rather than inherited. The interface default is true, which happens to be correct, but three
	/// separate places in this codebase used to describe ways to supply a key "before any flow has been
	/// completed". While the host will not start an unconfigured integration, none of those ways is
	/// reachable, so they described a capability that could not be used.
	/// </summary>
#pragma warning disable CA1822 // An interface implementation cannot be static, whatever it reads.
	public bool RequiresConfiguration => true;
#pragma warning restore CA1822

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

		// Rebuilt per connection rather than per widget, so the asset cache inside it is the process-wide one
		// it is meant to be.
		_orbProvider = context.UiResources is { } registry
			? new OrbUiProvider(_state, registry, _logger, _widgetTypes, OrbButtonActions())
			: null;

		await _settings.ReloadAsync(context, cancellationToken).ConfigureAwait(false);

		var settings = _settings.Current;

		_logger.Information(
			"Initialized. Provider {Provider}, model {Model}, configured {Configured}.",
			settings.Llm,
			settings.LlmModel,
			settings.HasLlmCredentials);

		await ProbeElevatedServiceAsync(settings, cancellationToken).ConfigureAwait(false);

		// Probe NIM STT so we know whether to use it for faster transcription.
		await _nimTranscriber.ProbeAsync(cancellationToken).ConfigureAwait(false);
		if (_nimTranscriber.IsAvailable)
		{
			_logger.Information("NIM speech-to-text is available; using it for faster transcription.");
		}
		else
		{
			_logger.Information("NIM speech-to-text is not available; using local whisper.");
		}

		if (settings.MicrophoneAlwaysOn)
		{
			if (_microphone.Start(settings.MicrophoneId, settings.MicrophoneName))
			{
				_microphoneFailure = null;
				_logger.Information("Microphone is live on {Device}.", _microphone.ActiveDeviceName);
			}
			else
			{
				_microphoneFailure = _microphone.LastError ?? "no reason was reported";
				_logger.Warning("No microphone: the orb will not react to audio. {Error}", _microphoneFailure);
			}
		}
		else
		{
			// The stop belongs here, not only in ShutdownAsync. This method runs again on every reconnect and
			// on a configuration change, so without it a user who turned the setting off kept an open,
			// recording microphone and a live tap, which is the opposite of what they asked for.
			_microphone.Stop();
			_microphoneFailure = null;
		}

		RegisterHotkey(settings);

		await ConfigureWakeWordAsync(settings, cancellationToken).ConfigureAwait(false);

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
	/// Binds the global hotkey, and unbinds whatever was bound before.
	/// <para>
	/// Unregistering first is what makes this converge. The parse-failure path used to return before
	/// unregistering, so clearing the chord left the previous one registered with the operating system and
	/// a hotkey the user had removed from their settings still started turns.
	/// </para>
	/// </summary>
	private void RegisterHotkey(JarvisSettings settings)
	{
		_hotkey.Pressed -= OnHotkeyPressed;
		_hotkey.Unregister();

		if (HotkeyChord.Parse(settings.PushToTalkHotkey) is not { } chord)
		{
			_hotkeyFailure = null;
			_logger.Debug("No global hotkey is configured.");
			return;
		}

		_hotkey.Pressed += OnHotkeyPressed;

		if (_hotkey.Register(chord))
		{
			_hotkeyFailure = null;
			return;
		}

		_hotkeyFailure = _hotkey.LastError ?? "no reason was reported";
		_logger.Warning("The global hotkey {Chord} is not available. {Reason}", chord, _hotkeyFailure);
	}

	/// <summary>
	/// The hotkey does what a press of the button does. The press is fire-and-forget because it runs on
	/// the hotkey's own message thread: blocking there would stop the hotkey from being seen again.
	/// </summary>
	private void OnHotkeyPressed() => _ = RunUnattendedTurnAsync("The hotkey turn");

	/// <summary>
	/// Wires the selected wake word engine. The keyword spotter scores the raw stream in-process: the
	/// models download the first time it is selected, the pump feeds them every packet in order, and a
	/// run of confident chunks starts a turn. The transcript engine is the original design - level, then
	/// recognise, then match - and stays selectable because it matches any word the settings name.
	/// <para>
	/// The two paths share nothing but the detector's cooldown and event, so a switch between them
	/// leaves the same teardown whichever was running before.
	/// </para>
	/// </summary>
	private async Task ConfigureWakeWordAsync(JarvisSettings settings, CancellationToken cancellationToken)
	{
		_wakeWord.Word = settings.WakeWord;
		_wakeWord.Sensitivity = settings.WakeWordSensitivity;

		// Detached before anything is decided, so every path below leaves the same teardown as the last one
		// ran. Leaving a tap attached and a closure capturing the previous settings is how a wake word keeps
		// working after it has been switched off.
		_wakeWord.Detected -= OnWakeWordDetected;
		_microphone.LevelPublished -= OnMicrophoneLevel;
		_microphone.SamplesCaptured -= OnMicrophoneSamples;
		_microphone.DetachTap();
		_wakeWord.Recognizer = null;
		_wakeWord.Enabled = false;
		_wakeEngine.ScoreReady -= _wakeWord.OfferScore;
		_wakeEngine.Deactivate();

		if (!settings.WakeWordEngineEnabled || !settings.MicrophoneAlwaysOn)
		{
			// Named rather than returning in silence. This was a bare return, so a user who expected to be
			// spoken to got a live microphone, a plugin that looked healthy, and no wake word at all, with
			// nothing anywhere saying why. A deck button still worked, so the plugin read as working.
			_logger.Information(
				"The wake word is off: {Reason}. Turn it on in the settings to be spoken to.",
				!settings.WakeWordEngineEnabled
					? "the wake word is disabled"
					: "the microphone is not always on, so nothing is listening between turns");

			return;
		}

		// A custom wake word has no keyword model, and the score path can only hear the three phrases this
		// build pins. Choosing it and hearing "hey jarvis" back is the wrong answer twice over, so the
		// transcript engine takes over instead: it matches any word in the settings.
		var usesScorePath = settings.WakeWordEngine == WakeWordEngine.OpenWakeWord
			&& OpenWakeWordEngineHost.HasModelFor(settings.WakeWord);

		if (settings.WakeWordEngine == WakeWordEngine.OpenWakeWord && !usesScorePath)
		{
			_logger.Information(
				"No keyword model hears '{Word}', so the wake word uses the transcript engine, which matches any word.",
				settings.WakeWord);
		}

		if (usesScorePath)
		{
			_wakeEngine.SourceRate = _microphone.ActiveSampleRate;

			if (!await _wakeEngine.ActivateAsync(settings, cancellationToken).ConfigureAwait(false))
			{
				// The models did not install; the download manager has raised an issue the user can act on.
				// The wake word stays off rather than pretending to listen.
				return;
			}

			_wakeEngine.ScoreReady += _wakeWord.OfferScore;
			_microphone.SamplesCaptured += OnMicrophoneSamples;
		}
		else
		{
			// The detector's buffer is sized from the rate, so it has to be told what the device is really
			// delivering before the transcript path reads from it.
			_wakeWord.UseSampleRate(_microphone.ActiveSampleRate);

			// Assigned unconditionally rather than only on the enabled path, and it reads the current settings
			// from the field rather than capturing the argument. A closure over a parameter holds the settings
			// from the initialization that created it, so a later configuration change was recognised with the
			// old language and sensitivity.
			_wakeWord.Recognizer = RecognizeWakeWordAsync;
		}

		_wakeWord.Enabled = true;
		_wakeWord.Detected += OnWakeWordDetected;

		if (!usesScorePath)
		{
			// The feed itself. Everything above configures a detector that waits to be given levels, and
			// without this line it waits for the rest of its life: the tap keeps the audio and the detector
			// never sees it. The keyword path does not need the tap - its packets arrive through
			// SamplesCaptured - so the ring buffer is only filled when something reads it.
			_microphone.LevelPublished -= OnMicrophoneLevel;
			_microphone.LevelPublished += OnMicrophoneLevel;

			_microphone.AttachTap(_wakeWord.Buffer);
		}
	}

	/// <summary>
	/// Feeds one raw packet to the keyword engine. On the capture thread, deliberately without a task:
	/// <see cref="OpenWakeWordEngineHost.Accept"/> is a bounded queue write, and the packet order that
	/// the pump depends on is only guaranteed if the handover is as synchronous as the callback itself.
	/// </summary>
	private void OnMicrophoneSamples(float[] samples)
	{
		try
		{
			_wakeEngine.Accept(samples);
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Debug(exception, "A microphone packet could not be fed to the wake word.");
		}
	}

	/// <summary>
	/// Feeds one published level to the wake word. Fire and forget on purpose: the detector's own method is
	/// async and runs a speech recogniser, and the publish tick is a 50 ms timer that also owns the
	/// amplitude the orb is drawing.
	/// </summary>
	private void OnMicrophoneLevel(double level)
	{
		if (!_wakeWord.Enabled)
		{
			return;
		}

		_ = Task.Run(async () =>
		{
			try
			{
				await _wakeWord.OfferAsync(level, CancellationToken.None).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// Shutting down.
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				_logger.Debug(exception, "A wake word check failed.");
			}
		});
	}

	/// <summary>
	/// The recogniser the wake word detector calls, resolved against the settings current when the audio
	/// arrives rather than the ones current when the closure was made.
	/// </summary>
	private async Task<string?> RecognizeWakeWordAsync(float[] samples, CancellationToken token)
	{
		if (!_transcriber.IsAvailable)
		{
			// Said once, then never again: this is reached on every recognition attempt, and a wake word
			// that cannot work because no recogniser is installed is otherwise completely silent. Reported
			// rather than thrown because the microphone is legitimately open and the rest of the plugin is
			// healthy; it just cannot hear a word.
			if (!_wakeWordWithoutRecognizerReported)
			{
				_wakeWordWithoutRecognizerReported = true;
				_logger.Warning(
					"The wake word cannot work: no speech recognition runtime is installed, so nothing can be transcribed. "
					+ "Speech to text is set to {Provider}.",
					_settings.Current.SpeechToText);
			}

			return null;
		}

		var language = _settings.Current.SttLanguage;
		// The rate the device actually delivered, not the one that was asked for. A 96 kHz stream written into
		// a header claiming 48 kHz plays back at half speed, which a small recogniser answers with nonsense.
		var wav = UtteranceAudio.WriteWav(samples, _microphone.ActiveSampleRate);

		try
		{
			var result = await _transcriber.TranscribeAsync(wav, language, token).ConfigureAwait(false);

			return result.Ok ? result.Text : null;
		}
		finally
		{
			UtteranceAudio.Delete(wav);
		}
	}

	/// <summary>Whether the missing-recognizer warning has already been reported, so it is said once.</summary>
	private bool _wakeWordWithoutRecognizerReported;

	/// <summary>A wake word starts a turn exactly as a button press does.</summary>
	private void OnWakeWordDetected(string? transcribed) => StartListeningTurn(transcribed);

	/// <summary>
	/// What the orb's own buttons do. The same two paths a deck button and the wake word use, so a press on
	/// the widget is indistinguishable from a press on a tile: one turn at a time, started on its own token,
	/// and cancelled the same way.
	/// </summary>
	private OrbButtonActions OrbButtonActions() =>
		new(() => StartListeningTurn(null), () => _session.Cancel(killRunningCommand: false));

	/// <summary>
	/// Runs a turn without blocking the caller. Used by the hotkey and the wake word, both of which fire on
	/// threads that must stay free: a blocked hotkey thread stops the hotkey being seen again.
	/// </summary>
	private void StartListeningTurn(string? prompt) => _ = RunUnattendedTurnAsync("An unattended turn", prompt);

	/// <summary>
	/// Runs a turn nobody is waiting on, and reports how it ended either way.
	/// <para>
	/// The returned result used to be dropped, which made every failure invisible. There is no button press
	/// to show a red result, no log line because the failure was never inspected, and no speech because it
	/// happened before any reply existed. A user who pressed the hotkey or said the wake word got nothing at
	/// all: no sound, no movement, and not one line in the log. Saying the wake word and watching the orb
	/// sit still is indistinguishable from the plugin not running.
	/// </para>
	/// <para>
	/// The orb is the one surface guaranteed to be on screen, since it needs neither speech recognition nor
	/// speech synthesis, so a failure goes there as well as into the log. The next turn clears the message.
	/// </para>
	/// </summary>
	private async Task RunUnattendedTurnAsync(string what, string? prompt = null)
	{
		ActionResult result;

		try
		{
			result = await _listening.ListenAndAnswerAsync(prompt, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Warning(exception, "{What} threw.", what);
			ShowUnattendedFailure(Readable(Strings.Errors.TurnFailed()));
			return;
		}

		if (result.Status == ActionResultStatus.Succeeded)
		{
			_logger.Information("{What} completed.", what);
			return;
		}

		// The reason, not just "it failed". The usual cause is a component that was never downloaded, and
		// naming it is the difference between a user who knows which button to press and one who gives up.
		// LocalizedText is a struct, so emptiness is asked rather than tested against null.
		var reason = Readable(result.ErrorMessage.IsEmpty ? result.Message : result.ErrorMessage);

		_logger.Warning("{What} did not complete: {Reason} ({Code})", what, reason, result.ErrorCode);
		ShowUnattendedFailure(reason);
	}

	/// <summary>
	/// Resolves localized text for a person to read, because <c>LocalizedText.ToString()</c> does not.
	/// <para>
	/// Built from this plugin's own catalog rather than injected: <c>UseLocalization</c> does not register
	/// <see cref="ILocalizationResolver"/> in the service graph, and a scope that resolves here but not in
	/// the harness would mean the tests never saw the text a user sees.
	/// </para>
	/// </summary>
	private static readonly LocalizationResolver Text = BuildResolver();

	private static LocalizationResolver BuildResolver()
	{
		var registry = new LocalizationCatalogRegistry();
		registry.Register(Strings.LocalizationCatalog);

		return new LocalizationResolver(registry);
	}

	/// <summary>
	/// Turns a localized value into the words to show.
	/// <para>
	/// <c>LocalizedText.ToString()</c> deliberately renders the descriptor, not the translation: it answers
	/// <c>plugin:com.misike12.jarvis:Errors.ComponentMissing(component)</c> and leaves the placeholder
	/// unfilled. That is the right thing for a diagnostic and the wrong thing anywhere a person reads, so the
	/// text goes through the resolver instead of being stringified.
	/// </para>
	/// </summary>
	private static string Readable(LocalizedText text) =>
		Text.Resolve(text, CultureInfo.CurrentUICulture.Name) ?? text.ToString();

	/// <summary>
	/// Puts an unattended turn's failure on the orb, the one surface that is always visible. Returns the
	/// text so the caller logs the same words the user sees.
	/// </summary>
	private void ShowUnattendedFailure(string reason) =>
		_state.Transition(AssistantState.Error, statusLine: reason);

	/// <summary>
	/// Finds out whether the elevated service is there, so the tools that need it are either offered or not
	/// for a stated reason.
	/// <para>
	/// This was never called before, so the answer was never known: the tool was registered unconditionally
	/// and a user who had switched the service off still had a model that confidently called it. The probe
	/// is skipped entirely when the setting says the service is off, since there is nothing to ask.
	/// </para>
	/// </summary>
	private async Task ProbeElevatedServiceAsync(JarvisSettings settings, CancellationToken cancellationToken)
	{
		if (!settings.ElevatedServiceEnabled)
		{
			_logger.Information(
				"The elevated service is switched off, so the tools that need it will not be offered.");

			return;
		}

		try
		{
			if (await _serviceAvailability.RefreshAsync(cancellationToken).ConfigureAwait(false))
			{
				_logger.Information("The elevated service is answering.");
			}
			else
			{
				_logger.Warning(
					"The elevated service is not answering. Machine-wide registry changes will not be offered "
						+ "until it does. Run scripts\\install-service.ps1 -Start from an elevated PowerShell.");
			}
		}
		catch (OperationCanceledException)
		{
			// The session is going away, which is not a fault in the probe.
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Warning(exception, "The elevated service could not be probed.");
		}
	}

	public async Task ShutdownAsync()
	{
		// Everything detached, not just the microphone stopped. Between this and the next InitializeAsync the
		// plugin is still live, so a hotkey left registered would start a full voice turn with no session, on
		// CancellationToken.None, holding a concurrency slot and the microphone for its whole length.
		_hotkey.Pressed -= OnHotkeyPressed;
		_hotkey.Unregister();

		_wakeWord.Detected -= OnWakeWordDetected;
		_wakeWord.Recognizer = null;
		_wakeWord.Enabled = false;

		_wakeEngine.ScoreReady -= _wakeWord.OfferScore;
		_microphone.SamplesCaptured -= OnMicrophoneSamples;
		_wakeEngine.Deactivate();

		_microphone.LevelPublished -= OnMicrophoneLevel;
		_microphone.DetachTap();
		_microphone.Stop();

		_session.Cancel(false);
		_state.Reset();

		_orbProvider = null;

		// The comment on this type claimed widget types "are withdrawn when the integration stops", and nothing
		// withdrew them. The host holds the registration until the provider says otherwise, so a stopped
		// integration left a widget type a user could still put on a deck, pointing at a plugin that was gone.
		if (_widgetTypeContext is { } widgetTypes)
		{
			// Short, and logged, because this is the only thing shutdown does that leaves the process. It is
			// a round trip to the host, on the path where the host is already tearing the session down, so it
			// is bounded tightly and its outcome is recorded rather than hoped for.
			using var unregister = new CancellationTokenSource(TimeSpan.FromSeconds(2));

			try
			{
				await widgetTypes.UnregisterWidgetTypeAsync(
					OrbWidgetTypeProvider.OrbTypeId, unregister.Token).ConfigureAwait(false);

				_logger.Information("The orb widget type was withdrawn.");
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				_logger.Warning(exception, "The orb widget type could not be withdrawn.");
			}
		}
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

			// Unavailable rather than zero or an empty label. A widget bound to these cannot tell "nothing is
			// downloading" from "the download is at zero percent", and a progress bar that reads 0 forever
			// after a finished download looks like a download that never started.
			"download-percent" => download.Active
				? VariableReading.Of(download.Percent, 0, 100, 1)
				: VariableReading.Unavailable,
			"download-label" => download.Active
				? VariableReading.Of(Describe(download))
				: VariableReading.Unavailable,
			_ => VariableReading.Unavailable,
		};

		return ValueTask.FromResult(reading);
	}

	/// <summary>
	/// The label names what is happening and how far it got, so a button bound to it says something useful
	/// rather than only moving a percentage bar somewhere else.
	/// <para>
	/// Localized rather than composed from the asset id. This string is bound to a widget a user reads, and
	/// it was three English fragments with a raw enum phase name in the middle. It goes through the resolver
	/// rather than <c>ToString()</c>, which would show the raw resource descriptor on the widget.
	/// </para>
	/// </summary>
	private static string Describe(RuntimeProgress progress) => progress.AssetId switch
	{
		"" or null => string.Empty,
		_ when !progress.Active => Readable(Strings.Variables.DownloadFinished(progress.AssetId)),
		_ => Readable(Strings.Variables.DownloadRunning(
			progress.AssetId,
			((int)Math.Round(progress.Percent)).ToString(CultureInfo.InvariantCulture))),
	};

	/// <summary>
	/// Widget types register after the integration initializes and before any other provider hook, and
	/// are withdrawn when the integration stops.
	/// </summary>
	public async Task InitializeAsync(IWidgetTypeProviderContext context, CancellationToken cancellationToken = default)
	{
		_widgetTypeContext = context;

		await _widgetTypes.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
	}

	public IReadOnlyList<WidgetTypeDescriptor> GetWidgetTypes() => _widgetTypes.GetWidgetTypes();

	public IReadOnlyList<UiSurfaceDeclaration> Surfaces => OrbUiProvider.DeclaredSurfaces;

	/// <summary>
	/// The orb provider, and with it the asset cache, built once per connection rather than once per widget.
	/// <para>
	/// It used to be constructed inside <c>CreateSessionAsync</c>, so every widget got its own
	/// <c>OrbAssetCache</c> whose entire purpose is to be process-wide. Opening a second orb re-encoded every
	/// GIF, and because each re-encode registered under the same resource name the host accumulated duplicate
	/// registrations for the same resource.
	/// </para>
	/// </summary>
	private OrbUiProvider? _orbProvider;

	/// <summary>
	/// The widget type registration, held so it can be withdrawn. It is handed to the host once and the host
	/// keeps it until the provider says otherwise, so without this a stopped integration left a widget type
	/// the user could still add to a deck.
	/// </summary>
	private IWidgetTypeProviderContext? _widgetTypeContext;

	public Task<IUiSession?> CreateSessionAsync(UiSessionRequest request, CancellationToken cancellationToken) =>
		_orbProvider is { } provider
			? provider.CreateSessionAsync(request, cancellationToken)
			: Task.FromResult<IUiSession?>(null);
}