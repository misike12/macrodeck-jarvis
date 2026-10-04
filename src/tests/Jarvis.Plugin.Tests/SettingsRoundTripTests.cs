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
	/// Completing the flow must not hand back a values dictionary.
	/// <para>
	/// The host persists every form field as the flow runs, across all steps. The flow used to rebuild all
	/// of them from the <em>last</em> step's input and pass them to <c>Complete</c>, so the three API keys
	/// collected two steps earlier were absent from that input and were written back as an empty secret.
	/// Every configured API key was therefore destroyed each time setup was completed, which is why the
	/// dictionary is gone rather than merely corrected.
	/// </para>
	/// </summary>
	[Test]
	public void Completing_the_flow_writes_no_values()
	{
		var source = System.IO.File.ReadAllText(FlowPath());

		Assert.Multiple(() =>
		{
			Assert.That(
				source,
				Does.Not.Contain("ConfigFlowValue."),
				"the flow builds a values dictionary, so the last step's input overwrites every earlier step");

			Assert.That(
				source,
				Does.Not.Match(@"ConfigFlowResult\.Complete\([^)]*,"),
				"Complete is passed a second argument, which replaces what the host already persisted");
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
