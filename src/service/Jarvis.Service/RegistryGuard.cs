namespace Jarvis.Service;

/// <summary>
/// Decides which registry keys the elevated service will write.
/// <para>
/// This is the whole reason the service is not a general purpose way to run as SYSTEM. A caller that
/// reaches the pipe can already cause a write with the service's token, so what this class permits is
/// exactly the blast radius of a compromise of the plugin process. An allowlist is used rather than a
/// denylist because the interesting keys to block are the ones nobody thinks of: a denylist written by
/// someone enumerating dangerous paths is missing at least one, and the paths that matter most are short.
/// </para>
/// <para>
/// The plugin has no feature that writes HKLM. The only caller is the model's generic registry tool, which
/// exists so the model can change machine-wide settings. So the permitted set is small on purpose, and
/// adding a root here is a decision about what the plugin may do as SYSTEM rather than a refactor.
/// </para>
/// </summary>
public static class RegistryGuard
{
	/// <summary>
	/// The roots the service may write, as normalized paths below HKLM.
	/// <para>
	/// Matching is on segment boundaries, so <c>SOFTWARE\Jarvis</c> permits <c>SOFTWARE\Jarvis\Settings</c>
	/// and refuses <c>SOFTWARE\JarvisEvil</c>.
	/// </para>
	/// </summary>
	private static readonly string[] AllowedRoots = [@"SOFTWARE\Jarvis"];

	private const int MaxLength = 512;

	/// <summary>
	/// Whether a value may be written, or a key created, at this path.
	/// </summary>
	public static bool IsWritable(string? path) => AllowedRootFor(path) is not null;

	/// <summary>
	/// Whether a whole subtree may be deleted at this path.
	/// <para>
	/// One root stricter than <see cref="IsWritable"/>, because deleting the subtree under a root removes
	/// every key beneath it, while writing a value at the root touches exactly the one value named.
	/// </para>
	/// </summary>
	public static bool IsSubtreeDeletable(string? path) =>
		TryNormalize(path, out var normalized)
			&& Array.Exists(AllowedRoots, root => IsStrictlyUnder(root, normalized));

	/// <summary>
	/// The allowed root that covers this path, or null when the path is refused.
	/// <para>
	/// The path is normalized first, so a caller cannot reach an allowed root through a spelling the
	/// registry treats as equivalent: a leading space, a leading separator, doubled separators, or a <c>.</c>
	/// component all resolve to the same key as the bare path but would not match a naive comparison.
	/// </para>
	/// </summary>
	private static string? AllowedRootFor(string? path)
	{
		if (!TryNormalize(path, out var normalized))
		{
			return null;
		}

		foreach (var root in AllowedRoots)
		{
			if (IsStrictlyUnder(root, normalized) || string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase))
			{
				return root;
			}
		}

		return null;
	}

	/// <summary>
	/// Whether <paramref name="path"/> is inside <paramref name="root"/> rather than merely starting with
	/// the same characters.
	/// </summary>
	private static bool IsStrictlyUnder(string root, string path) =>
		path.Length > root.Length
			&& path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
			&& path[root.Length] == '\\';

	/// <summary>
	/// Reduces a key path to a canonical form, or reports it as unusable.
	/// <para>
	/// <c>..</c> is refused rather than collapsed. A path that needs to climb out of where it started is not
	/// one this service should be resolving on a caller's behalf, and collapsing it would mean implementing
	/// the resolution rather than declining it.
	/// </para>
	/// </summary>
	public static bool TryNormalize(string? path, out string normalized)
	{
		normalized = string.Empty;

		if (string.IsNullOrWhiteSpace(path) || path.Length > MaxLength)
		{
			return false;
		}

		foreach (var character in path)
		{
			if (char.IsControl(character))
			{
				return false;
			}
		}

		var segments = new List<string>();

		foreach (var segment in path.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (segment == ".")
			{
				continue;
			}

			if (segment == "..")
			{
				return false;
			}

			segments.Add(segment);
		}

		if (segments.Count == 0)
		{
			return false;
		}

		normalized = string.Join('\\', segments);

		return true;
	}
}