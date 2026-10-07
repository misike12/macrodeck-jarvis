using Jarvis.Plugin.Core;
using NUnit.Framework;
using MacroDeck.Sdk;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The settings round trip.
/// <para>
/// The setup flow wrote about thirty fields and the store read back six of them, so every setting a user
/// chose - model, language, persona, memory mode, safety mode, hotkey, wake word - was silently discarded
/// and the assistant ran on defaults while appearing configured. Nothing about that looks broken from the
/// outside, which is why it needs a test rather than a look.
/// </para>
/// </summary>
[TestFixture]
[FastHostReads]
public class SettingsRoundTripTests
{
	private static JarvisSettingsStore NewStore() =>
		new(RuntimeTestLog.Logger, LocalSettingsFile.Load(Path.Combine(Path.GetTempPath(), "jarvis-settings-none")));

	/// <summary>
	/// Every field the setup flow writes must have a reader. Compared by reflection so adding a field to the
	/// flow without a reader fails here rather than in production.
	/// </summary>
	[Test]
	public void Every_field_the_flow_writes_is_read_back()
	{
		var written = System.Text.RegularExpressions.Regex
			.Matches(
				System.IO.File.ReadAllText(FlowPath()),
			 @"JarvisSettingsStoreFields\.(\w+)")
			.Select(match => match.Groups[1].Value)
			.Distinct(StringComparer.Ordinal)
			.ToHashSet(StringComparer.Ordinal);

		var readers = System.Text.RegularExpressions.Regex
			.Matches(
				System.IO.File.ReadAllText(StorePath()),
			 @"JarvisSettingsStoreFields\.(\w+)")
			.Select(match => match.Groups[1].Value)
			.ToHashSet(StringComparer.Ordinal);

		// These three are read as secrets through their own path, not through the string list.
		written.Remove("NvidiaKeyEntryField");
		written.Remove("PicovoiceKeyField");
		written.Remove("SelfHostedTokenField");

		var unread = written.Except(readers).Order(StringComparer.Ordinal).ToArray();

		Assert.That(unread, Is.Empty, "written by the flow but never read: " + string.Join(", ", unread));
	}

	/// <summary>
	/// The completion hands back the final step's input rather than a set accumulated across steps.
	/// <para>
	/// This asserted the opposite for a while, on the theory that a multi-step completion with no values
	/// persisted nothing. Reading the host source and then its database disproved that: the host merges
	/// every submitted form field itself, and a completed setup had written every field to the entry.
	/// Accumulating across steps was also worse than useless, because input is cumulative and the host
	/// rolls it back when the user steps back, so a retracted value would be written straight back over it.
	/// </para>
	/// </summary>
	[Test]
	public void Completing_the_flow_hands_back_the_last_steps_input()
	{
		var source = System.IO.File.ReadAllText(FlowPath());

		Assert.Multiple(() =>
		{
			Assert.That(
				source,
				Does.Match(@"ConfigFlowResult\.Complete\([^)]*,"),
				"the completion hands back no values, so a value the plugin computes has no way to persist");

			Assert.That(
				source,
				Does.Not.Contain("_collected"),
				"the flow accumulates across steps, which can resurrect a value the user retracted by stepping back");
		});
	}

	/// <summary>
	/// A value this build does not recognise must not stop the plugin starting, and must not blank the
	/// setting either: a downgrade should leave the stored value for the build that understands it.
	/// </summary>
	[Test]
	public async Task An_unrecognised_stored_value_keeps_the_default()
	{
		var store = NewStore();
		store.Apply(store.Current with { Persona = PersonaPreset.Formal, Safety = SafetyMode.ConfirmAll });

		// A context with nothing in it is the degenerate case: every field reads as absent.
		await store.ReloadAsync(context: null, CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(store.Current.Persona, Is.EqualTo(PersonaPreset.Formal));
			Assert.That(store.Current.Safety, Is.EqualTo(SafetyMode.ConfirmAll));
		});
	}

	/// <summary>
	/// Reloading twice must be stable rather than drifting towards defaults each time. Compared field by
	/// field rather than as whole records: the settings carry a collection, and record equality compares
	/// that by reference, so two identical reloads would never be equal.
	/// </summary>
	[Test]
	public async Task Reloading_twice_is_stable()
	{
		var store = NewStore();

		await store.ReloadAsync(context: null, CancellationToken.None);
		var first = store.Current;

		await store.ReloadAsync(context: null, CancellationToken.None);
		var second = store.Current;

		Assert.Multiple(() =>
		{
			Assert.That(second.Llm, Is.EqualTo(first.Llm));
			Assert.That(second.LlmModel, Is.EqualTo(first.LlmModel));
			Assert.That(second.Persona, Is.EqualTo(first.Persona));
			Assert.That(second.Memory, Is.EqualTo(first.Memory));
			Assert.That(second.Safety, Is.EqualTo(first.Safety));
			Assert.That(second.Language, Is.EqualTo(first.Language));
			Assert.That(second.WakeWordSensitivity, Is.EqualTo(first.WakeWordSensitivity));
			Assert.That(second.MaxIterations, Is.EqualTo(first.MaxIterations));
		});
	}

	/// <summary>
	/// The default wake word sensitivity has to be crossable by a voice. It was 0.6 while measured room
	/// tone is 0.036 and a normal speaking voice peaks well below 0.6, so with default settings the wake
	/// word could never fire at all.
	/// </summary>
	[Test]
	public void The_default_wake_word_sensitivity_is_actually_reachable()
	{
		var sensitivity = new JarvisSettings().WakeWordSensitivity;

		Assert.That(
			sensitivity,
			Is.InRange(0.001, 0.3),
			$"{sensitivity} is above a typical speaking voice; the wake word would never fire");
	}

	/// <summary>A sensitivity outside the usable range must be discarded, not clamped into uselessness.</summary>
	[TestCase(0)]
	[TestCase(-1)]
	[TestCase(5000)]
	public void An_unusable_sensitivity_is_refused_by_the_settings_shape(double value)
	{
		var settings = new JarvisSettings();

		Assert.That(
			JarvisSettingsStore.Clamp(value, settings.WakeWordSensitivity),
			Is.EqualTo(settings.WakeWordSensitivity),
			"an out-of-range sensitivity was accepted and would silently disable the wake word");
	}

	/// <summary>
	/// The stored spelling of every enum setting has to be readable back.
	/// <para>
	/// This is the check that was missing, and the bug it would have caught was severe. The setup flow writes
	/// option <em>values</em>, which are kebab-case: "self-hosted-nim", "tool-permissions", "yes-no". The
	/// store parsed with <c>Enum.TryParse</c> and nothing else, which matches neither a hyphen nor anything
	/// but the exact member name. Every one of those settings therefore reverted to its default on the very
	/// next reload, silently: a user who chose a self-hosted provider got NVIDIA, and their self-hosted token
	/// was posted to a host it was never meant for. Two of the four safety modes happened to parse, so the
	/// mode silently changed behaviour depending on which word had been picked.
	/// </para>
	/// </summary>
	[TestCase("nvidia-nim", LlmProvider.NvidiaNim)]
	[TestCase("self-hosted-nim", LlmProvider.SelfHostedNim)]
	[TestCase("local-llama-cpp", LlmProvider.LocalLlamaCpp)]
	[TestCase("SelfHostedNim", LlmProvider.SelfHostedNim)]
	[TestCase("whisper-cpp-local", SpeechToTextProvider.WhisperCppLocal)]
	[TestCase("nvidia-nim", SpeechToTextProvider.NvidiaNim)]
	[TestCase("windows-sapi", SpeechToTextProvider.WindowsSapi)]
	[TestCase("piper-local", TextToSpeechProvider.PiperLocal)]
	[TestCase("nvidia-nim", TextToSpeechProvider.NvidiaNim)]
	[TestCase("openwakeword", WakeWordEngine.OpenWakeWord)]
	[TestCase("transcript", WakeWordEngine.Transcript)]
	[TestCase("confirm-all", SafetyMode.ConfirmAll)]
	[TestCase("allowlist", SafetyMode.Allowlist)]
	[TestCase("tool-permissions", SafetyMode.ToolPermissions)]
	[TestCase("autonomous", SafetyMode.Autonomous)]
	[TestCase("yes-no", VoiceConfirmation.YesNo)]
	[TestCase("spoken-challenge", VoiceConfirmation.SpokenChallenge)]
	[TestCase("hybrid", VoiceConfirmation.Hybrid)]
	[TestCase("speech-and-stream", CancelDepth.SpeechAndStream)]
	[TestCase("stop-running-command", CancelDepth.StopRunningCommand)]
	[TestCase("none", MemoryMode.None)]
	[TestCase("session", MemoryMode.Session)]
	[TestCase("persistent", MemoryMode.Persistent)]
	[TestCase("persistent-notes", MemoryMode.PersistentNotes)]
	[TestCase("classic-jarvis", PersonaPreset.ClassicJarvis)]
	[TestCase("terse", PersonaPreset.Terse)]
	[TestCase("sarcastic", PersonaPreset.Sarcastic)]
	[TestCase("formal", PersonaPreset.Formal)]
	[TestCase("custom", PersonaPreset.Custom)]
	public void Every_stored_enum_spelling_the_flow_writes_reads_back<TEnum>(string stored, TEnum expected)
		where TEnum : struct, System.Enum
	{
		Assert.That(JarvisSettingsStore.TryParseStoredEnum(stored, out TEnum parsed), Is.True, $"'{stored}' did not parse");
		Assert.That(parsed, Is.EqualTo(expected), $"'{stored}' parsed as {parsed}");
	}

	/// <summary>
	/// A numeric string parses into a member the enum does not have, and every switch over these then falls
	/// to its default arm: the safety mode silently becomes "ask about everything" and a provider fails every
	/// turn with "no credentials configured". A value that names nothing is refused instead.
	/// </summary>
	[TestCase("7", LlmProvider.NvidiaNim)]
	[TestCase("9", SafetyMode.ConfirmAll)]
	[TestCase("99", SafetyMode.ConfirmAll)]
	[TestCase("-1", PersonaPreset.ClassicJarvis)]
	[TestCase("not-a-mode", SafetyMode.ConfirmAll)]
	[TestCase("", SafetyMode.ConfirmAll)]
	[TestCase(null, SafetyMode.ConfirmAll)]
	public void A_stored_value_naming_no_member_is_refused<TEnum>(string? stored, TEnum fallback)
		where TEnum : struct, System.Enum
	{
		Assert.That(JarvisSettingsStore.TryParseStoredEnum<TEnum>(stored, out _), Is.False, $"'{stored}' was accepted");
	}

	/// <summary>
	/// Every enum value the setup flow actually offers has to read back. The table above is written by hand
	/// and would drift from the options, so this is generated from the same place the options come from.
	/// </summary>
	[Test]
	public void Every_option_the_flow_offers_is_readable_back()
	{
		var unreadable = JarvisFields.All
			.Where(field => EnumFields.Contains(field.Name))
			.SelectMany(field => field.Options ?? [])
			.Where(option => !NamesAnyMember(option.Value))
			.Select(option => option.Value)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();

		Assert.That(unreadable, Is.Empty, "the flow offers a value no enum member can be read from");
	}

	/// <summary>
	/// Whether a value names a member of any enum the store reads. A flow that offered "magpie" for the
	/// speech provider was offering something that parsed as nothing at all, so the setting silently stayed on
	/// its default and the user was told their choice had been made.
	/// </summary>
	private static bool NamesAnyMember(string value) =>
		JarvisSettingsStore.TryParseStoredEnum<LlmProvider>(value, out _)
		|| JarvisSettingsStore.TryParseStoredEnum<VisionProvider>(value, out _)
		|| JarvisSettingsStore.TryParseStoredEnum<SpeechToTextProvider>(value, out _)
		|| JarvisSettingsStore.TryParseStoredEnum<TextToSpeechProvider>(value, out _)
		|| JarvisSettingsStore.TryParseStoredEnum<WakeWordEngine>(value, out _)
		|| JarvisSettingsStore.TryParseStoredEnum<SafetyMode>(value, out _)
		|| JarvisSettingsStore.TryParseStoredEnum<VoiceConfirmation>(value, out _)
		|| JarvisSettingsStore.TryParseStoredEnum<CancelDepth>(value, out _)
		|| JarvisSettingsStore.TryParseStoredEnum<MemoryMode>(value, out _)
		|| JarvisSettingsStore.TryParseStoredEnum<PersonaPreset>(value, out _);

	/// <summary>The fields the store parses as an enum, which is what the check above applies to.</summary>
	private static readonly HashSet<string> EnumFields =
	[
		JarvisSettingsStoreFields.LlmProviderField,
		JarvisSettingsStoreFields.VisionProviderField,
		JarvisSettingsStoreFields.SttProviderField,
		JarvisSettingsStoreFields.TtsProviderField,
		JarvisSettingsStoreFields.WakeEngineField,
		JarvisSettingsStoreFields.SafetyField,
		JarvisSettingsStoreFields.ConfirmationField,
		JarvisSettingsStoreFields.CancelDepthField,
		JarvisSettingsStoreFields.MemoryField,
		JarvisSettingsStoreFields.PersonaField,
	];

	/// <summary>
	/// The language the assistant answers in is a different thing from the language the recogniser listens
	/// for. It was never assigned from anything, so every prompt carried the record's hard-coded "en" and a
	/// user with a French recogniser got a French ear and an English mouth.
	/// </summary>
	[Test]
	public async Task The_answer_language_follows_the_recogniser_language()
	{
		var store = NewStore();
		store.Apply(store.Current with { SttLanguage = "fr" });

		await store.ReloadAsync(context: null, TestContext.CurrentContext.CancellationToken);

		Assert.Multiple(() =>
		{
			Assert.That(store.Current.SttLanguage, Is.EqualTo("fr"));
			Assert.That(store.Current.Language, Is.EqualTo("fr"), "the assistant would answer in the hard-coded default");
		});
	}

	/// <summary>
	/// The two languages are read from one value, so changing it has to change both on the same reload.
	/// Deriving the answer language from the previous snapshot instead would leave the assistant one
	/// reload behind, which reads as the setting having not been saved.
	/// </summary>
	[Test]
	public async Task Changing_the_language_takes_effect_on_the_same_reload()
	{
		var store = NewStore();

		// Two languages derived from one field, so they cannot disagree after any reload. Deriving the answer
		// language from the previous snapshot instead would leave the assistant one reload behind, which reads
		// as the setting having not been saved.
		await store.ReloadAsync(context: null, TestContext.CurrentContext.CancellationToken);

		Assert.That(
			store.Current.Language,
			Is.EqualTo(store.Current.SttLanguage),
			"the assistant's language and the recogniser's disagree");
	}

	/// <summary>Memory off must genuinely mean off, including on a settings reload.</summary>
	[Test]
	public async Task Memory_mode_survives_a_reload()
	{
		var store = NewStore();
		store.Apply(store.Current with { Memory = MemoryMode.None });

		await store.ReloadAsync(context: null, CancellationToken.None);

		Assert.That(store.Current.Memory, Is.EqualTo(MemoryMode.None));
	}

	/// <summary>
	/// The allowlist a user types has to reach the gate in the shape the gate matches against.
	/// <para>
	/// There was no way to set one at all: the safety gate consulted a list of eight fixed commands, so the
	/// allowlist mode permitted the same things whatever the user chose. These cover the shapes a person
	/// actually types, since the field is free text.
	/// </para>
	/// </summary>
	[TestCase("git,dotnet", new[] { "git", "dotnet" })]
	[TestCase("git, dotnet ,  code", new[] { "git", "dotnet", "code" })]
	[TestCase("git\ndotnet;code", new[] { "git", "dotnet", "code" })]
	[TestCase("git", new[] { "git" })]
	public void The_command_allowlist_is_split_on_everything_a_person_uses(string stored, string[] expected)
	{
		Assert.That(
			JarvisSettingsStore.ParseCommandAllowlist(stored),
			Is.EqualTo(expected));
	}

	/// <summary>
	/// A field that parses to nothing must not permit nothing silently: the user who switched to the allowlist
	/// mode expressed no opinion about which commands, so the read-only default list applies.
	/// </summary>
	[TestCase(null)]
	[TestCase("")]
	[TestCase("   ")]
	[TestCase(",,,")]
	[TestCase("git *")]
	public void An_empty_allowlist_keeps_the_read_only_default(string? stored)
	{
		Assert.That(
			JarvisSettingsStore.ParseCommandAllowlist(stored),
			Is.EqualTo(JarvisSettings.DefaultCommandAllowlist));
	}

	/// <summary>
	/// The default list is the safety mode's whole point, so it must not contain a shell, a scheduler or a
	/// downloader. It shipped with git, dotnet and code in it.
	/// </summary>
	[Test]
	public void The_default_allowlist_permits_nothing_that_changes_the_machine()
	{
		var dangerous = new[] { "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "reg", "schtasks", "curl", "wget", "git", "dotnet", "code", "npm", "python", "certutil", "bitsadmin" };

		var permitted = JarvisSettings.DefaultCommandAllowlist;

		Assert.That(
			permitted.Intersect(dangerous, StringComparer.OrdinalIgnoreCase),
			Is.Empty,
			"the default allowlist permits a command that can change the machine or reach the network");
	}

	/// <summary>
	/// Every numeric bound the form declares has to be one a person can actually satisfy, and the
	/// iteration bound has to agree with the one the turn applies. Both were stated three different ways.
	/// </summary>
	[Test]
	public void A_declared_numeric_bound_can_actually_be_chosen()
	{
		foreach (var field in JarvisFields.All.Where(field => field.Kind is JarvisFieldKind.Number))
		{
			Assert.That(field.Minimum, Is.Not.Null, $"{field.Name} has no minimum");
			Assert.That(field.Maximum, Is.Not.Null, $"{field.Name} has no maximum");
			Assert.That(field.Minimum!, Is.LessThan(field.Maximum!), $"{field.Name} has an empty range");
		}
	}

	/// <summary>
	/// Walks up to the project directory rather than counting levels: the test output nests one level
	/// differently per configuration, and a wrong count produces a file-not-found rather than a real
	/// failure, which is a miserable way to discover a broken test.
	/// </summary>
	private static string ProjectFile(string relative)
	{
		var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, "src", "Jarvis.Plugin", relative);

			if (File.Exists(candidate))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new FileNotFoundException($"Could not locate {relative} above the test output directory.");
	}

	private static string FlowPath() => ProjectFile("JarvisConfigFlow.cs");

	private static string StorePath() => ProjectFile(Path.Combine("Core", "JarvisSettingsStore.cs"));
}
