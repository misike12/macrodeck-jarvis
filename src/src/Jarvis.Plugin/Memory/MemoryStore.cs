using System.Text;
using System.Globalization;
using Jarvis.Plugin.Core;
using Serilog;

namespace Jarvis.Plugin.Memory;

/// <summary>One remembered exchange.</summary>
public sealed record MemoryEntry(DateTimeOffset At, string Role, string Text)
{
	public string ToLine() => $"{At:O}\t{Role}\t{Text.ReplaceLineEndings(" ")}";
}

/// <summary>
/// Conversation history and notes, on disk.
/// <para>
/// Everything lives under the plugin data directory, never beside the executable: the install directory is
/// immutable per version, so a memory written there would be deleted by the next update and a user would
/// watch their assistant forget everything.
/// </para>
/// <para>
/// Writes are append-only and end with a newline, and a malformed trailing line is discarded rather than
/// throwing. A memory file that a crash left half-written should cost the user one remembered message, not
/// the whole history.
/// </para>
/// </summary>
public sealed class MemoryStore
{
	private const string TranscriptFile = "transcript.tsv";
	private const string NotesFile = "notes.md";

	/// <summary>
	/// History is capped. An assistant that has read every conversation since the day it was installed has
	/// worse judgement than one that has read the last hundred turns, and the prompt grows without bound
	/// otherwise.
	/// </summary>
	private const int MaxEntries = 500;

	private readonly Lock _gate = new();
	private readonly string _directory;
	private readonly ILogger _logger;

	public MemoryStore(JarvisSettingsStore settings, ILogger logger)
	{
		_logger = logger.ForContext<MemoryStore>();
		_directory = ResolveDirectory();
		Notes = ReadNotes();
	}

	/// <summary>A hand-editable note file, injected into the system prompt when the mode asks for it.</summary>
	public string Notes { get; private set; }

	/// <summary>History accumulated during this process only, gone when it exits.</summary>
	public List<MemoryEntry> Session { get; } = [];

	/// <summary>Where the memory lives, shown in diagnostics.</summary>
	public string Location => _directory;

	public string TranscriptPath => Path.Combine(_directory, TranscriptFile);

	public string NotesPath => Path.Combine(_directory, NotesFile);

	private static string ResolveDirectory()
	{
		// The host guarantees this directory survives updates and rollback.
		var root = Environment.GetEnvironmentVariable("MACRO_DECK_PLUGIN_DATA_DIRECTORY")
			?? Path.Combine(Path.GetTempPath(), "jarvis");

		return Path.Combine(root, "memory");
	}

	/// <summary>
	/// Reads persisted history, newest last. A mode of <see cref="MemoryMode.None"/> reads nothing, so a
	/// user who turns memory off is not still loading a transcript they asked not to keep.
	/// </summary>
	public IReadOnlyList<MemoryEntry> LoadHistory(MemoryMode mode)
	{
		if (mode == MemoryMode.None)
		{
			return [];
		}

		lock (_gate)
		{
			if (mode == MemoryMode.Session)
			{
				return [.. Session];
			}

			if (!File.Exists(TranscriptPath))
			{
				return [.. Session];
			}

			var entries = new List<MemoryEntry>();

			try
			{
				foreach (var line in File.ReadLines(TranscriptPath))
				{
					if (TryParse(line, out var entry))
					{
						entries.Add(entry);
					}
				}
			}
			catch (IOException exception)
			{
				_logger.Warning(exception, "The remembered transcript could not be read.");
				return [.. Session];
			}

			entries.AddRange(Session);
			return entries.Count <= MaxEntries ? entries : entries[^MaxEntries..];
		}
	}

	/// <summary>Records an exchange. Persistent only when the mode asks for it.</summary>
	public void Remember(string role, string text, MemoryMode mode)
	{
		var entry = new MemoryEntry(DateTimeOffset.UtcNow, role, text);

		lock (_gate)
		{
			Session.Add(entry);

			if (mode is not (MemoryMode.Persistent or MemoryMode.PersistentNotes) || Session.Count > MaxEntries)
			{
				return;
			}

			try
			{
				Directory.CreateDirectory(_directory);

				if (Session.Count == 1)
				{
					// Start clean rather than appending to a transcript from a previous run that may be a
					// different conversation entirely.
					File.WriteAllText(TranscriptPath, string.Empty);
				}

				File.AppendAllText(TranscriptPath, entry.ToLine() + Environment.NewLine, Encoding.UTF8);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				_logger.Warning(exception, "An exchange could not be remembered.");
			}
		}
	}

	/// <summary>
	/// Replaces the notes file. The text is shown back to the model before it is stored, so the user can
	/// see exactly what the assistant decided about itself.
	/// </summary>
	public void SetNotes(string notes)
	{
		lock (_gate)
		{
			Notes = notes;
			Directory.CreateDirectory(_directory);
			File.WriteAllText(NotesPath, notes, Encoding.UTF8);
		}

		_logger.Information("The notes file was rewritten ({Length} characters).", notes.Length);
	}

	private string ReadNotes()
	{
		try
		{
			return File.Exists(NotesPath) ? File.ReadAllText(NotesPath, Encoding.UTF8) : string.Empty;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_logger.Warning(exception, "The notes file could not be read.");
			return string.Empty;
		}
	}

	private static bool TryParse(string line, out MemoryEntry entry)
	{
		entry = null!;

		// Tab separated with the timestamp first; a message containing a tab survives because only the first
		// two tabs are separators.
		var first = line.IndexOf('\t', StringComparison.Ordinal);
		var second = first < 0 ? -1 : line.IndexOf('\t', first + 1);

		if (first <= 0 || second <= first)
		{
			return false;
		}

		if (!DateTimeOffset.TryParse(line[..first], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
		{
			return false;
		}

		entry = new MemoryEntry(at, line[(first + 1)..second], line[(second + 1)..]);
		return true;
	}
}