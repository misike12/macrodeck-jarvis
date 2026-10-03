using MacroDeck.Plugin.Hosting.Transport;
using MacroDeck.Sdk;
using MacroDeck.Sdk.ConfigFlow;
using Serilog;

namespace Jarvis.Plugin.Core;

/// <summary>The config-entry field names, shared by the flow that writes them and the store that reads them.</summary>
public static class JarvisSettingsStoreFields
{
	public const string NvidiaKeyEntryField = "nvidiaApiKey";
	public const string NvidiaBaseUrlField = "nvidiaBaseUrl";
	public const string SelfHostedUrlField = "selfHostedBaseUrl";
	public const string SelfHostedTokenField = "selfHostedToken";
	public const string PicovoiceKeyField = "picovoiceAccessKey";
	public const string LlmProviderField = "llmProvider";
	public const string LlmModelField = "llmModel";
	public const string VisionProviderField = "visionProvider";
	public const string VisionModelField = "visionModel";
	public const string SttProviderField = "speechToText";
	public const string SttModelField = "speechToTextModel";
	public const string TtsProviderField = "textToSpeech";
	public const string TtsModelField = "textToSpeechModel";
	public const string PiperVoiceField = "piperVoice";
	public const string LanguageField = "language";
	public const string WakeEngineField = "wakeWordEngine";
	public const string WakeWordField = "wakeWord";
	public const string WakeSensitivityField = "wakeWordSensitivity";
	public const string HotkeyField = "pushToTalkHotkey";
	public const string MicrophoneIdField = "microphoneId";
	public const string MicrophoneNameField = "microphoneName";
	public const string MicrophoneAlwaysOnField = "microphoneAlwaysOn";
	public const string LifetimeField = "lifetime";
	public const string BargeInField = "bargeIn";
	public const string BargeInThresholdField = "bargeInThreshold";
	public const string SafetyField = "safety";
	public const string ConfirmationField = "voiceConfirmation";
	public const string CancelDepthField = "cancelDepth";
	public const string MaxIterationsField = "maxIterations";
	public const string TimeoutField = "conversationTimeoutSeconds";
	public const string MemoryField = "memory";
	public const string PersonaField = "persona";
	public const string PromptField = "customSystemPrompt";
	public const string NotesField = "notes";
	public const string ServiceEnabledField = "elevatedService";
	public const string ServiceSchedulingField = "elevatedServiceScheduling";
	public const string ServiceAdminField = "elevatedServiceAdminOperations";
}

/// <summary>
/// Merges the three places settings can come from into one object. The config flow wins because it is
/// the path a shipped plugin uses; the developer file and environment variables exist so a key can be
/// present before any flow has been completed.
/// </summary>
public sealed class JarvisSettingsStore
{
	private readonly Lock _gate = new();
	private readonly LocalSettingsFile _local;
	private readonly ILogger _logger;

	private JarvisSettings _current = new();

	public JarvisSettingsStore(ILogger logger)
		: this(logger, local: null)
	{
	}

	/// <summary>
	/// Supplies the developer settings file directly rather than looking for one beside the executable.
	/// Exists so a test can point at a real file without copying a credential into its own output
	/// directory, where every other test would then read it and believe itself configured.
	/// </summary>
	public JarvisSettingsStore(ILogger logger, LocalSettingsFile? local)
	{
		_logger = logger.ForContext<JarvisSettingsStore>();
		_local = local ?? LocalSettingsFile.Load(AppContext.BaseDirectory);
	}

	public JarvisSettings Current
	{
		get
		{
			lock (_gate)
			{
				return _current;
			}
		}
	}

	/// <summary>
	/// Reads every configured value back from the config entry, falling back to the developer file and
	/// then the environment. Host calls are network round trips, so a failure degrades to the local
	/// value instead of leaving the integration without configuration.
	/// </summary>
	public async Task ReloadAsync(IIntegrationContext? context, CancellationToken cancellationToken)
	{
		var nvidiaKey = _local.NvidiaApiKey ?? string.Empty;
		var nvidiaBaseUrl = _local.NvidiaBaseUrl ?? JarvisSettings.DefaultNvidiaBaseUrl;
		var selfHostedUrl = _local.SelfHostedBaseUrl ?? string.Empty;
		var selfHostedToken = _local.SelfHostedToken ?? string.Empty;
		var picovoiceKey = _local.PicovoiceAccessKey ?? string.Empty;

		if (context is not null)
		{
			try
			{
				var entries = await context.Config.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
				var entry = entries.Count > 0 ? entries[0] : (ConfigEntrySnapshot?)null;

				if (entry is { } configured)
				{
					nvidiaKey = FirstNonEmpty(
						await context.Config.GetSecretAsync(configured.Id, JarvisSettingsStoreFields.NvidiaKeyEntryField, cancellationToken).ConfigureAwait(false),
						nvidiaKey);

					picovoiceKey = FirstNonEmpty(
						await context.Config.GetSecretAsync(configured.Id, JarvisSettingsStoreFields.PicovoiceKeyField, cancellationToken).ConfigureAwait(false),
						picovoiceKey);

					selfHostedToken = FirstNonEmpty(
						await context.Config.GetSecretAsync(configured.Id, JarvisSettingsStoreFields.SelfHostedTokenField, cancellationToken).ConfigureAwait(false),
						selfHostedToken);

					nvidiaBaseUrl = FirstNonEmpty(
						await context.Config.GetStringAsync(configured.Id, JarvisSettingsStoreFields.NvidiaBaseUrlField, cancellationToken).ConfigureAwait(false),
						nvidiaBaseUrl);

					selfHostedUrl = FirstNonEmpty(
						await context.Config.GetStringAsync(configured.Id, JarvisSettingsStoreFields.SelfHostedUrlField, cancellationToken).ConfigureAwait(false),
						selfHostedUrl);
				}
			}
			catch (HostInvocationException exception)
			{
				_logger.Warning(exception, "Configuration could not be read; using local values.");
			}
		}

		nvidiaKey = FirstNonEmpty(Environment.GetEnvironmentVariable("JARVIS_NVIDIA_API_KEY"), nvidiaKey);
		picovoiceKey = FirstNonEmpty(Environment.GetEnvironmentVariable("JARVIS_PICOVOICE_KEY"), picovoiceKey);

		var next = new JarvisSettings
		{
			NvidiaApiKey = nvidiaKey,
			NvidiaBaseUrl = nvidiaBaseUrl,
			SelfHostedBaseUrl = selfHostedUrl,
			SelfHostedToken = selfHostedToken,
			PicovoiceAccessKey = picovoiceKey,
		};

		lock (_gate)
		{
			_current = next;
		}

		_logger.Debug("Settings reloaded. LLM {Provider}, model {Model}.", next.Llm, next.LlmModel);
	}

	public void Apply(JarvisSettings settings)
	{
		lock (_gate)
		{
			_current = settings;
		}
	}

	internal static string FirstNonEmpty(params string?[] candidates)
	{
		foreach (var candidate in candidates)
		{
			if (!string.IsNullOrWhiteSpace(candidate))
			{
				return candidate;
			}
		}

		return string.Empty;
	}
}