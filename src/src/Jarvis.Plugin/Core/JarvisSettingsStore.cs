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
/// Merges the host's configuration with the defaults into one immutable snapshot.
/// <para>
/// The host is the only place a setting comes from. There used to be a developer file loaded from beside the
/// executable and two environment variables, and this comment used to describe all three as supported ways
/// to supply a value. Neither of the other two could ever be reached, because the integration requires a
/// configuration before the host will start it, so they only ever left a place for a credential to be
/// written in plaintext. The injectable constructor below still accepts a settings object so tests can
/// exercise a configured store without a real key.
/// </para>
/// </summary>
public sealed class JarvisSettingsStore
{
	private readonly Lock _gate = new();
	private readonly LocalSettingsFile _local;
	private readonly ILogger _logger;

	private readonly Dictionary<string, string> _read = new(StringComparer.Ordinal);

	private JarvisSettings _current = new();

	/// <summary>
	/// The production constructor. Nothing is read from disk: the host's encrypted store is the only source,
	/// so a plugin directory has no settings file to hold a key in.
	/// </summary>
	public JarvisSettingsStore(ILogger logger)
		: this(logger, LocalSettingsFile.Empty)
	{
	}

	/// <summary>
	/// Supplies the settings directly. For tests, which need a configured store and must not depend on a file
	/// that every other test in the assembly would then also read.
	/// </summary>
	public JarvisSettingsStore(ILogger logger, LocalSettingsFile? local)
	{
		_logger = logger.ForContext<JarvisSettingsStore>();
		_local = local ?? LocalSettingsFile.Empty;
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
/// Everything the setup flow writes, taken from the one declared table rather than restated here.
	/// A list written out separately is a list that drifts, and when it drifts the store reads back a setting
	/// the flow cannot write, so the code that consumes it reads a default forever and nothing says so.
	/// </summary>
	private static readonly string[] StringFields = JarvisFields.ReadBackAsText;

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
	/// Reads every configured value back from the host.
	/// <para>
	/// The host's encrypted secret store is the only source of a credential. There used to be two more: a
	/// <c>jarvis.settings.json</c> beside the binary, and two <c>JARVIS_*_KEY</c> environment variables. Both
	/// are gone, because neither could ever have been reached. This integration declares
	/// <c>RequiresConfiguration</c>, so the host does not start it until a configuration exists, which means
	/// the "get a key in before completing the flow" path those two offered did not exist. All they did was
	/// leave a place for a key to be written down in plaintext, next to the plugin or in a script that starts
	/// it.
	/// </para>
	/// <para>
	/// The injectable overload below still accepts a settings object, because tests need to exercise a
	/// configured store without putting a real key in a file.
	/// </para>
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
			Lifetime = ReadEnum(JarvisSettingsStoreFields.LifetimeField, current.Lifetime),

			// The three elevated-service switches were read into the dictionary and then never copied into
			// the snapshot, so the code that consulted them saw false forever. That is why a user who turned
			// the service off in the settings still had a model calling it: the setting had nowhere to land.
			ElevatedServiceEnabled = ReadFlag(
				JarvisSettingsStoreFields.ServiceEnabledField, current.ElevatedServiceEnabled),
			ElevatedServiceScheduling = ReadFlag(
				JarvisSettingsStoreFields.ServiceSchedulingField, current.ElevatedServiceScheduling),
			ElevatedServiceAdminOperations = ReadFlag(
				JarvisSettingsStoreFields.ServiceAdminField, current.ElevatedServiceAdminOperations),
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