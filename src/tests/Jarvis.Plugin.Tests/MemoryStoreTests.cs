using Jarvis.Plugin.Core;
using Jarvis.Plugin.Memory;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The memory file, exercised against a real directory. A memory that corrupts itself silently is worse
/// than one that does not work, because the user only finds out when JARVIS has forgotten something.
/// </summary>
[TestFixture]
public class MemoryStoreTests
{
	private string _root = null!;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), $"jarvis-mem-{Guid.CreateVersion7():N}");
		Directory.CreateDirectory(_root);
	}

	[TearDown]
	public void TearDown()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing over.
		}
	}

	private MemoryStore NewStore()
	{
		Environment.SetEnvironmentVariable("MACRO_DECK_PLUGIN_DATA_DIRECTORY", _root);
		return new MemoryStore(new JarvisSettingsStore(RuntimeTestLog.Logger), RuntimeTestLog.Logger);
	}

	/// <summary>Memory off must mean memory off: nothing read, nothing written, no file created.</summary>
	[Test]
	public void Memory_off_writes_nothing_at_all()
	{
		var store = NewStore();

		store.Remember("user", "remember this", MemoryMode.None);

		Assert.Multiple(() =>
		{
			Assert.That(store.LoadHistory(MemoryMode.None), Is.Empty);
			Assert.That(store.TranscriptPath, Does.Not.Exist);
		});
	}

	/// <summary>Session mode is for this process only, so a restart must not resurrect it.</summary>
	[Test]
	public void Session_memory_does_not_reach_the_disk()
	{
		var store = NewStore();
		store.Remember("user", "in passing", MemoryMode.Session);

		Assert.Multiple(() =>
		{
			Assert.That(store.LoadHistory(MemoryMode.Session), Is.Not.Empty);
			Assert.That(store.TranscriptPath, Does.Not.Exist);
		});
	}

	/// <summary>
	/// A message containing a tab must survive the round trip. Only the first two tabs separate fields, so
	/// splitting on all of them would truncate the text.
	/// </summary>
	[Test]
	public void A_message_containing_a_tab_survives_the_round_trip()
	{
		var store = NewStore();
		store.Remember("user", "column one\tcolumn two", MemoryMode.Persistent);

		var reloaded = NewStore().LoadHistory(MemoryMode.Persistent);

		Assert.That(reloaded, Has.Count.EqualTo(1));
		Assert.That(reloaded[0].Text, Is.EqualTo("column one\tcolumn two"));
		Assert.That(reloaded[0].Role, Is.EqualTo("user"));
	}

	[Test]
	public void A_message_containing_a_newline_stays_on_one_line()
	{
		var store = NewStore();
		store.Remember("assistant", "first line\nsecond line", MemoryMode.Persistent);

		var reloaded = NewStore().LoadHistory(MemoryMode.Persistent);

		Assert.Multiple(() =>
		{
			Assert.That(reloaded, Has.Count.EqualTo(1), "a newline split one message into two");
			Assert.That(reloaded[0].Text, Is.EqualTo("first line second line"));
		});
	}

	/// <summary>
	/// A crash can leave a half-written last line: a timestamp and one separator but no role or no text.
	/// That must cost one remembered message, not the whole history, because throwing here would make the
	/// assistant unable to start at all.
	/// </summary>
	[Test]
	public void A_half_written_trailing_line_is_discarded_rather_than_fatal()
	{
		var store = NewStore();
		store.Remember("user", "the first one", MemoryMode.Persistent);
		store.Remember("assistant", "the second one", MemoryMode.Persistent);

		File.AppendAllText(store.TranscriptPath, "2026-01-01T00:00:00Z\tuser" + Environment.NewLine);

		var reloaded = NewStore().LoadHistory(MemoryMode.Persistent);

		Assert.That(reloaded, Has.Count.EqualTo(2));
	}

	/// <summary>A line with no separators at all is not an entry either, and must not be.</summary>
	[Test]
	public void A_line_with_no_separators_is_discarded()
	{
		var store = NewStore();
		store.Remember("user", "the first one", MemoryMode.Persistent);

		File.AppendAllText(store.TranscriptPath, "not an entry at all" + Environment.NewLine);

		Assert.That(NewStore().LoadHistory(MemoryMode.Persistent), Has.Count.EqualTo(1));
	}

	[Test]
	public void The_notes_file_round_trips()
	{
		var store = NewStore();
		store.SetNotes("Call the user 'sir' before eleven.");

		Assert.That(NewStore().Notes, Is.EqualTo("Call the user 'sir' before eleven."));
	}

	/// <summary>
	/// The persona is stored here rather than in the integration config, so a config-flow rewrite must not
	/// be able to erase what the user asked the assistant to become.
	/// </summary>
	[Test]
	public void A_settings_reload_does_not_erase_the_notes()
	{
		var store = NewStore();
		store.SetNotes("stay formal");

		var settingsStore = new JarvisSettingsStore(RuntimeTestLog.Logger, LocalSettingsFile.Load(_root));
		settingsStore.Apply(settingsStore.Current with { Persona = PersonaPreset.Terse });

		Assert.That(NewStore().Notes, Is.EqualTo("stay formal"));
	}
}