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

	// Published as a whole. A read that failed part way through leaves the previous dictionary in place,
	// so a field the host did not answer keeps its last known value instead of becoming empty.
	private Dictionary<string, string> _read = new(StringComparer.Ordinal);

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
	/// The value the host last reported for a field, or null when it has none.
	/// <para>
	/// Read by the setup flow to seed each field's declared default. The host's setup UI prefills a re-opened
	/// form from the stored entry but discards that prefill on every step after the first, so the form is
	/// rebuilt from the declared defaults instead: without this, a re-opened flow shows a microphone nobody
	/// chose and a model nobody picked, and the user is told their settings did not save when they did.
	/// </para>
	/// </summary>
	public string? StoredValue(string field) => _read.GetValueOrDefault(field);

	/// <summary>
	/// The host refuses a plugin that calls back too often, so a run of reads is spaced out. Comfortably
	/// above the host's 100 ms per-token refill, because the run has to survive being interrupted rather
	/// than merely not be the sole cause of a refusal: at 60 ms this loop demanded more than the host
	/// refills, drained the bucket, and every reload ended in a rate-limit failure.
	/// </summary>
	private static readonly TimeSpan HostCallSpacing = TimeSpan.FromMilliseconds(150);

	/// <summary>First wait after a refused call, doubled on each further attempt up to <see cref="HostMaxRetryDelay"/>.</summary>
	private static readonly TimeSpan HostRetryDelay = TimeSpan.FromMilliseconds(120);

	private static readonly TimeSpan HostMaxRetryDelay = TimeSpan.FromMilliseconds(600);

	/// <summary>
	/// Ceiling on how long one reload may spend waiting. Without it a host that refuses everything turns a
	/// reload into forty fields times five attempts times the backoff, which is over a minute, and a reload
	/// happens on every reconnect.
	/// </summary>
	private static readonly TimeSpan HostReadBudget = TimeSpan.FromSeconds(20);

	private const int MaxHostReadAttempts = 4;

	/// <summary>
	/// Serialises every config read across all stores in the process, so two integrations cannot combine
	/// into one burst either.
	/// </summary>
	private static readonly SemaphoreSlim HostCalls = new(1, 1);

	/// <summary>
	/// Issues one host callback, spaced so that a run of them stays under the host's rate limit.
	/// <para>
	/// This is not defensive. The host answers a plugin that calls back too quickly with
	/// <c>HostInvocationException: This plugin is calling back into the host too quickly.</c>, and reading the
	/// roughly forty fields the setup flow writes is one call per field, because the config API exposes only
	/// <c>GetStringAsync</c> and <c>GetSecretAsync</c> for a single key with no bulk form. Issued back to back
	/// they tripped that limit, the whole reload was abandoned, and the plugin came up with no API key: the
	/// assistant sat in <c>Unavailable</c>, so it would not talk and the orb had nothing to draw, while the
	/// only sign was one warning line reading as though nothing had gone wrong.
	/// </para>
	/// <para>
	/// The wait happens while the gate is held, which is what makes the spacing real. Retries are for the
	/// calls that are still refused, and cover only what the exception marks retryable, so a refusal the host
	/// means as final fails immediately rather than after four waits. Retrying also stops once the reload's
	/// whole budget is gone, so an unreachable host costs the budget rather than a multiple of it.
	/// </para>
	/// </summary>
	private async Task<T> ReadFromHostAsync<T>(
		Func<Task<T>> call,
		long budgetExpiresAt,
		CancellationToken cancellationToken)
	{
		var delay = HostRetryDelay;

		for (var attempt = 1; ; attempt++)
		{
			await HostCalls.WaitAsync(cancellationToken).ConfigureAwait(false);

			try
			{
				await Task.Delay(HostCallSpacing, cancellationToken).ConfigureAwait(false);

				return await call().ConfigureAwait(false);
			}
			catch (HostInvocationException exception)
				when (exception.Retryable
					&& attempt < MaxHostReadAttempts
					&& Environment.TickCount64 < budgetExpiresAt)
			{
				_logger.Debug(
					"Host refused a callback as too frequent ({Code}), attempt {Attempt}; waiting {Delay}.",
					exception.Code,
					attempt,
					delay);

				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
				delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, HostMaxRetryDelay.TotalMilliseconds));
			}
			finally
			{
				HostCalls.Release();
			}
		}
	}

/// <summary>
	/// Reads one secret, keeping the previous value if the host refuses or cannot answer.
	/// <para>
	/// Its own error handling, unlike the plain fields. A credential that cannot be read this time must
	/// not disturb the settings that were read successfully, so a failure here returns the value already
	/// in hand rather than propagating into the block that publishes them.
	/// </para>
	/// </summary>
	private async Task<string> ReadSecretAsync(
		IIntegrationConfig config,
		Guid entryId,
		string field,
		string previous,
		long budgetExpiresAt,
		CancellationToken cancellationToken)
	{
		try
		{
			return FirstNonEmpty(
				await ReadFromHostAsync(
					() => config.GetSecretAsync(entryId, field, cancellationToken),
					budgetExpiresAt,
					cancellationToken).ConfigureAwait(false),
				previous);
		}
		catch (HostInvocationException exception)
		{
			_logger.Warning(exception, "Secret {Field} could not be read; keeping the previous value.", field);
			return previous;
		}
	}

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
		// Seeded from the previous snapshot rather than from empty. A reload that fails part way through has
		// to leave the last known values standing; clearing first meant a failure cost every plain setting
		// at once, while the secrets already read into their locals survived, which is precisely the shape of
		// the bug this replaced: the API keys worked and nothing else did.
		var nvidiaKey = FirstNonEmpty(_local.NvidiaApiKey, _current.NvidiaApiKey);
		var nvidiaBaseUrl = FirstNonEmpty(_local.NvidiaBaseUrl, _current.NvidiaBaseUrl, JarvisSettings.DefaultNvidiaBaseUrl);
		var selfHostedUrl = FirstNonEmpty(_local.SelfHostedBaseUrl, _current.SelfHostedBaseUrl);
		var selfHostedToken = FirstNonEmpty(_local.SelfHostedToken, _current.SelfHostedToken);
		var picovoiceKey = FirstNonEmpty(_local.PicovoiceAccessKey, _current.PicovoiceAccessKey);

		if (context is not null)
		{
			try
			{
				var budgetExpiresAt = Environment.TickCount64 + (long)HostReadBudget.TotalMilliseconds;

				var entries = await ReadFromHostAsync(
					() => context.Config.GetEntriesAsync(cancellationToken), budgetExpiresAt, cancellationToken).ConfigureAwait(false);

				// Counted because a second entry would split the world in two: the UI and the read-back
				// could each be looking at a different one, which looks exactly like "nothing saves".
				_logger.Information("Configuration holds {Count} entries.", entries.Count);

				var entry = entries is { Count: > 0 } ? entries[0] : (ConfigEntrySnapshot?)null;

				if (entry is { } configured)
				{
					// Collected into a local dictionary and published only once every field has arrived, so a
					// burst that trips the host's rate limit half way through leaves the previous values in
					// place instead of a half-filled set that reads as "not answered".
					var read = new Dictionary<string, string>(StringComparer.Ordinal);

					foreach (var field in StringFields)
					{
						read[field] = await ReadFromHostAsync(
							() => context.Config.GetStringAsync(configured.Id, field, cancellationToken),
							budgetExpiresAt,
							cancellationToken).ConfigureAwait(false) ?? string.Empty;
					}

					// Published before the credentials are read, not after. A refused secret read must not be
					// able to cost the plain values, which is what made a working API key the only setting
					// that ever survived a rate-limited reload.
					_read = read;

					var config = context.Config;

					nvidiaKey = await ReadSecretAsync(
						config, configured.Id, JarvisSettingsStoreFields.NvidiaKeyEntryField, nvidiaKey, budgetExpiresAt, cancellationToken)
						.ConfigureAwait(false);

					picovoiceKey = await ReadSecretAsync(
						config, configured.Id, JarvisSettingsStoreFields.PicovoiceKeyField, picovoiceKey, budgetExpiresAt, cancellationToken)
						.ConfigureAwait(false);

					selfHostedToken = await ReadSecretAsync(
						config, configured.Id, JarvisSettingsStoreFields.SelfHostedTokenField, selfHostedToken, budgetExpiresAt, cancellationToken)
						.ConfigureAwait(false);
				}
			}
			catch (HostInvocationException exception)
			{
				_logger.Warning(
					exception,
					"Configuration could not be read within {Budget} over {Attempts} attempts per field; keeping the previous values.",
					HostReadBudget,
					MaxHostReadAttempts);
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
		_logger.Debug("Microphone configured as id {Id} name {Name}.", next.MicrophoneId, next.MicrophoneName);
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