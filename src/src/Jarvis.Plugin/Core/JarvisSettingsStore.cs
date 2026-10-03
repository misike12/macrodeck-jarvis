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
	public const string WakeWordEnabledField = "wakeWordEnabled";
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

	private readonly Dictionary<string, string> _read = new(StringComparer.Ordinal);

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
	/// <summary>
	/// The non-secret fields the setup flow writes, read back in one pass. Keeping them in one list means a
	/// setting added to the flow is read by editing the same place, rather than two that can drift.
	/// </summary>
	private static readonly string[] StringFields =
	[
		JarvisSettingsStoreFields.NvidiaBaseUrlField,
		JarvisSettingsStoreFields.SelfHostedUrlField,
		JarvisSettingsStoreFields.LlmProviderField,
		JarvisSettingsStoreFields.LlmModelField,
		JarvisSettingsStoreFields.VisionProviderField,
		JarvisSettingsStoreFields.VisionModelField,
		JarvisSettingsStoreFields.SttProviderField,
		JarvisSettingsStoreFields.SttModelField,
		JarvisSettingsStoreFields.TtsProviderField,
		JarvisSettingsStoreFields.TtsModelField,
		JarvisSettingsStoreFields.PiperVoiceField,
		JarvisSettingsStoreFields.LanguageField,
		JarvisSettingsStoreFields.WakeEngineField,
		JarvisSettingsStoreFields.WakeWordField,
		JarvisSettingsStoreFields.WakeWordEnabledField,
		JarvisSettingsStoreFields.WakeSensitivityField,
		JarvisSettingsStoreFields.HotkeyField,
		JarvisSettingsStoreFields.MicrophoneIdField,
		JarvisSettingsStoreFields.MicrophoneNameField,
		JarvisSettingsStoreFields.MicrophoneAlwaysOnField,
		JarvisSettingsStoreFields.SafetyField,
		JarvisSettingsStoreFields.ConfirmationField,
		JarvisSettingsStoreFields.CancelDepthField,
		JarvisSettingsStoreFields.BargeInField,
		JarvisSettingsStoreFields.BargeInThresholdField,
		JarvisSettingsStoreFields.MemoryField,
		JarvisSettingsStoreFields.PersonaField,
		JarvisSettingsStoreFields.PromptField,
		JarvisSettingsStoreFields.NotesField,
		JarvisSettingsStoreFields.MaxIterationsField,
		JarvisSettingsStoreFields.TimeoutField,
	];

/// <summary>
	/// Parses a stored enum, keeping the current value for one this build does not recognise. A stored
	/// value from a newer release, or a typo, must not stop the plugin from starting.
	/// </summary>
	private TEnum ReadEnum<TEnum>(string field, TEnum current) where TEnum : struct, System.Enum =>
		System.Enum.TryParse<TEnum>(Stored(field), true, out var parsed) ? parsed : current;

	/// <summary>Reads a stored flag. Anything unparseable keeps the current value rather than becoming false.</summary>
	private bool ReadFlag(string field, bool current) =>
		bool.TryParse(Stored(field), out var parsed) ? parsed : current;

	/// <summary>
	/// Reads a stored number inside a usable range. Out-of-range is discarded rather than clamped: a
	/// sensitivity above 1 can never be crossed by a voice, and clamping to 1 would leave the user with a
	/// feature that silently never fires.
	/// </summary>
	private double ReadRange(string field, double current, double minimum, double maximum) =>
		Clamp(
			double.TryParse(
				Stored(field),
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture,
				out var parsed)
				? parsed
				: double.NaN,
			current,
			minimum,
			maximum);

	/// <summary>
	/// One rule for every stored number: out-of-range is discarded rather than clamped. Clamping a
	/// sensitivity of 5 down to a maximum of 1 would leave the user with a feature that silently never
	/// fires, which is harder to notice than a value that visibly did not stick.
	/// </summary>
	public static double Clamp(double value, double current, double minimum = 0.001, double maximum = 1) =>
		double.IsNaN(value) || value < minimum || value > maximum ? current : value;

	/// <summary>Reads a stored whole number inside a usable range.</summary>
	private int ReadCount(string field, int current, int minimum, int maximum) =>
		(int)ReadRange(field, current, minimum, maximum);

	/// <summary>Reads a value written during this reload.</summary>
	private string Stored(string field) => _read.GetValueOrDefault(field) ?? string.Empty;

	/// <summary>
	/// Reads every configured value back from the host, falling back to the developer file and then the
	/// environment.
	/// </summary>
	public async Task ReloadAsync(IIntegrationContext? context, CancellationToken cancellationToken)
	{
		var nvidiaKey = _local.NvidiaApiKey ?? string.Empty;
		var nvidiaBaseUrl = _local.NvidiaBaseUrl ?? JarvisSettings.DefaultNvidiaBaseUrl;
		var selfHostedUrl = _local.SelfHostedBaseUrl ?? string.Empty;
		var selfHostedToken = _local.SelfHostedToken ?? string.Empty;
		var picovoiceKey = _local.PicovoiceAccessKey ?? string.Empty;

		// Everything the setup flow writes. It used to read back six of thirty, so almost every setting a
		// user chose was silently discarded and the assistant ran on defaults while appearing configured.
		_read.Clear();

		if (context is not null)
		{
			try
			{
				var entries = await context.Config.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
				var entry = entries.Count > 0 ? entries[0] : (ConfigEntrySnapshot?)null;

				if (entry is { } configured)
				{
					async Task<string?> GetStringAsync(string field) =>
						await context.Config.GetStringAsync(configured.Id, field, cancellationToken).ConfigureAwait(false);

					foreach (var field in StringFields)
					{
						_read[field] = await GetStringAsync(field).ConfigureAwait(false) ?? string.Empty;
					}

					nvidiaKey = FirstNonEmpty(
						await context.Config.GetSecretAsync(configured.Id, JarvisSettingsStoreFields.NvidiaKeyEntryField, cancellationToken).ConfigureAwait(false),
						nvidiaKey);

					picovoiceKey = FirstNonEmpty(
						await context.Config.GetSecretAsync(configured.Id, JarvisSettingsStoreFields.PicovoiceKeyField, cancellationToken).ConfigureAwait(false),
						picovoiceKey);

					selfHostedToken = FirstNonEmpty(
						await context.Config.GetSecretAsync(configured.Id, JarvisSettingsStoreFields.SelfHostedTokenField, cancellationToken).ConfigureAwait(false),
						selfHostedToken);
				}
			}
			catch (HostInvocationException exception)
			{
				_logger.Warning(exception, "Configuration could not be read; using local values.");
			}
		}

		nvidiaKey = FirstNonEmpty(Environment.GetEnvironmentVariable("JARVIS_NVIDIA_API_KEY"), nvidiaKey);
		picovoiceKey = FirstNonEmpty(Environment.GetEnvironmentVariable("JARVIS_PICOVOICE_KEY"), picovoiceKey);

		// Read back against whatever is current, so a field the host does not carry keeps its default
		// rather than being blanked.
		var current = _current;

		// Strings that are not secrets. Kept as one list so adding a setting to the flow and reading it back
		// are the same edit rather than two that can drift.
		string? Text(string field) => _read.GetValueOrDefault(field);

		var next = new JarvisSettings
		{
			NvidiaApiKey = nvidiaKey,
			NvidiaBaseUrl = FirstNonEmpty(Text(JarvisSettingsStoreFields.NvidiaBaseUrlField), nvidiaBaseUrl),
			SelfHostedBaseUrl = FirstNonEmpty(Text(JarvisSettingsStoreFields.SelfHostedUrlField), selfHostedUrl),
			SelfHostedToken = selfHostedToken,
			PicovoiceAccessKey = picovoiceKey,

			Llm = ReadEnum(JarvisSettingsStoreFields.LlmProviderField, current.Llm),
			LlmModel = FirstNonEmpty(Text(JarvisSettingsStoreFields.LlmModelField), current.LlmModel),
			Vision = ReadEnum(JarvisSettingsStoreFields.VisionProviderField, current.Vision),
			VisionModel = FirstNonEmpty(Text(JarvisSettingsStoreFields.VisionModelField), current.VisionModel),
			SpeechToText = ReadEnum(JarvisSettingsStoreFields.SttProviderField, current.SpeechToText),
			NimSpeechToTextModel = FirstNonEmpty(Text(JarvisSettingsStoreFields.SttModelField), current.NimSpeechToTextModel),
			TextToSpeech = ReadEnum(JarvisSettingsStoreFields.TtsProviderField, current.TextToSpeech),
			NimTextToSpeechModel = FirstNonEmpty(Text(JarvisSettingsStoreFields.TtsModelField), current.NimTextToSpeechModel),
			PiperVoice = FirstNonEmpty(Text(JarvisSettingsStoreFields.PiperVoiceField), current.PiperVoice),
			SttLanguage = FirstNonEmpty(Text(JarvisSettingsStoreFields.LanguageField), current.SttLanguage),

			WakeWordEngine = ReadEnum(JarvisSettingsStoreFields.WakeEngineField, current.WakeWordEngine),
			WakeWord = FirstNonEmpty(Text(JarvisSettingsStoreFields.WakeWordField), current.WakeWord),
			PushToTalkHotkey = FirstNonEmpty(Text(JarvisSettingsStoreFields.HotkeyField), current.PushToTalkHotkey),

			Safety = ReadEnum(JarvisSettingsStoreFields.SafetyField, current.Safety),
			Confirmation = ReadEnum(JarvisSettingsStoreFields.ConfirmationField, current.Confirmation),
			CancelDepth = ReadEnum(JarvisSettingsStoreFields.CancelDepthField, current.CancelDepth),
			Memory = ReadEnum(JarvisSettingsStoreFields.MemoryField, current.Memory),
			Persona = ReadEnum(JarvisSettingsStoreFields.PersonaField, current.Persona),
			CustomSystemPrompt = FirstNonEmpty(Text(JarvisSettingsStoreFields.PromptField), current.CustomSystemPrompt),
			Notes = FirstNonEmpty(Text(JarvisSettingsStoreFields.NotesField), current.Notes),

			BargeInEnabled = ReadFlag(JarvisSettingsStoreFields.BargeInField, current.BargeInEnabled),
			WakeWordEngineEnabled = ReadFlag(JarvisSettingsStoreFields.WakeWordEnabledField, current.WakeWordEngineEnabled),
			MicrophoneAlwaysOn = ReadFlag(JarvisSettingsStoreFields.MicrophoneAlwaysOnField, current.MicrophoneAlwaysOn),

			// A sensitivity of 1 or more can never be crossed by a normal speaking voice, so a value
			// outside the usable range is discarded rather than silently disabling the feature.
			WakeWordSensitivity = ReadRange(JarvisSettingsStoreFields.WakeSensitivityField, current.WakeWordSensitivity, 0.001, 1),
			BargeInThreshold = ReadRange(JarvisSettingsStoreFields.BargeInThresholdField, current.BargeInThreshold, 0.001, 1),

			MicrophoneId = FirstNonEmpty(Text(JarvisSettingsStoreFields.MicrophoneIdField), current.MicrophoneId),
			MicrophoneName = FirstNonEmpty(Text(JarvisSettingsStoreFields.MicrophoneNameField), current.MicrophoneName),

			MaxIterations = ReadCount(JarvisSettingsStoreFields.MaxIterationsField, current.MaxIterations, 1, 12),
			ConversationTimeoutSeconds = ReadCount(
				JarvisSettingsStoreFields.TimeoutField, current.ConversationTimeoutSeconds, 5, 600),
		};

		lock (_gate)
		{
			_current = next;
		}

		_logger.Debug("Settings reloaded. LLM {Provider}, model {Model}.", next.Llm, next.LlmModel);
	}

	/// <summary>
	/// Replaces the current settings in memory, for something the assistant changes about itself at
	/// runtime. Deliberately does not persist: the integration config stays the user's to edit, and
	/// anything that must outlive the process is written to the notes file instead.
	/// </summary>
	public void Apply(JarvisSettings settings)
	{
		lock (_gate)
		{
			_current = settings;
		}
	}

	internal static string FirstNonEmpty(params string?[] candidates)
	{		foreach (var candidate in candidates)
		{
			if (!string.IsNullOrWhiteSpace(candidate))
			{
				return candidate;
			}
		}

		return string.Empty;
	}
}