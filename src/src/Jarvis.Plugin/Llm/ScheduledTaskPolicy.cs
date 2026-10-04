namespace Jarvis.Plugin.Llm;

/// <summary>
/// What a scheduled task is allowed to run, when the task is going to run with the highest privileges the
/// account has.
/// <para>
/// A normal scheduled task runs as the user, with the same rights the user has right now, so it can do
/// nothing the user could not do by opening the program. An elevated task is different in one specific way:
/// it runs unattended, with no UAC prompt, at a time when nobody is watching. That combination is what turns
/// "run this program" into "run this program as an administrator, repeatedly, forever", which is a
/// persistence mechanism as much as a scheduling one.
/// </para>
/// <para>
/// So an elevated task may only name a program from a directory the user cannot replace. The two roots
/// below are the ones a normal machine install puts there, and both are administrator-owned by default.
/// </para>
/// </summary>
public static class ScheduledTaskPolicy
{
	/// <summary>
	/// The reason given when a path is refused, so the model can tell the user what to do about it rather
	/// than just retrying.
	/// </summary>
	public const string ElevatedPathRefusal =
		"A task that runs with the highest privileges can only run a program from Program Files or Windows "
			+ "System32, because it runs unattended with no prompt. Put the program there, or schedule it "
			+ "without the elevated option.";

	/// <summary>
	/// Whether an elevated task may run this command.
	/// <para>
	/// The path is fully resolved before it is compared, so a relative segment cannot walk out of an allowed
	/// directory and back into somewhere else, and the comparison is on a directory boundary so that
	/// <c>C:\Program Files Evil\x.exe</c> does not pass as being inside <c>C:\Program Files</c>.
	/// </para>
	/// </summary>
	public static bool IsElevatedCommandAllowed(string? command)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return false;
		}

		foreach (var character in command)
		{
			if (char.IsControl(character))
			{
				return false;
			}
		}

		string resolved;

		try
		{
			resolved = Path.GetFullPath(command);
		}
		catch (Exception exception) when (
			exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}

		return IsUnder(resolved, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
			|| IsUnder(resolved, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"));
	}

	private static bool IsUnder(string path, string root)
	{
		if (string.IsNullOrWhiteSpace(root))
		{
			return false;
		}

		var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

		return path.Length > trimmed.Length
			&& path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
			&& (path[trimmed.Length] == Path.DirectorySeparatorChar
				|| path[trimmed.Length] == Path.AltDirectorySeparatorChar);
	}
}