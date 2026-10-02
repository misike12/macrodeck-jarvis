using System.Security.Cryptography;

namespace Jarvis.Plugin.Runtime;

/// <summary>
/// Where downloaded assets live, and how that location is decided.
/// <para>
/// Everything goes under the plugin data directory rather than beside the executable: the install
/// directory is immutable per version and disappears on the next update, so a runtime tree written there
/// would be silently deleted the first time the plugin was upgraded. When the host has not supplied a data
/// directory the temp path is used, which keeps the plugin working in a test harness rather than
/// throwing where the writable root is genuinely unknown.
/// </para>
/// </summary>
public sealed class RuntimePaths
{
	private const string DataDirectoryVariable = "MACRO_DECK_PLUGIN_DATA_DIRECTORY";

	public RuntimePaths(string root)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);
		Root = Path.GetFullPath(root);
	}

	/// <summary>Resolves the runtime root from the host's data directory, falling back to the temp path.</summary>
	public static RuntimePaths Resolve()
	{
		var root = Environment.GetEnvironmentVariable(DataDirectoryVariable);

		return new RuntimePaths(string.IsNullOrWhiteSpace(root)
			? Path.Combine(Path.GetTempPath(), "jarvis-runtime")
			: Path.Combine(root, "runtime"));
	}

	public string Root { get; }

	/// <summary>Per-component directory, for example <c>runtime\piper\</c>.</summary>
	public string ComponentDirectory(string component)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(component);
		return Path.Combine(Root, Guard(component));
	}

	/// <summary>
	/// Where an install group's contents land inside its component, for example
	/// <c>runtime\piper\voice-en_GB-alan-medium\</c>. Two assets sharing a group share this directory, which
	/// is how a voice's model and its config end up beside each other rather than in two folders no
	/// synthesiser would ever pair up.
	/// </summary>
	public string ComponentInstallDirectory(string component, string group)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(group);
		return Path.Combine(ComponentDirectory(component), Guard(group));
	}

	/// <summary>
	/// Where a partially written download accumulates. Kept beside its target rather than in a shared
	/// folder so two components downloading at once cannot collide, and named so it can never be mistaken
	/// for an installed file.
	/// </summary>
	public string PartialPath(string component, string fileName) =>
		Path.Combine(ComponentDirectory(component), fileName + ".part");

	public string ManifestPath => Path.Combine(Root, "manifest.json");

	/// <summary>Creates the directories an install needs. Safe to call repeatedly.</summary>
	public void EnsureComponentDirectory(string component) =>
		Directory.CreateDirectory(ComponentDirectory(component));

	/// <summary>
	/// Rejects a component or group name that would escape its own directory. The names are pinned rather
	/// than user-supplied today, so this is a cheap invariant to hold rather than a real attack surface.
	/// </summary>
	private static string Guard(string value)
	{
		if (value.Contains("..", StringComparison.Ordinal)
			|| value.Contains('/', StringComparison.Ordinal)
			|| value.Contains('\\', StringComparison.Ordinal))
		{
			throw new ArgumentException($"'{value}' is not a usable runtime path segment.", nameof(value));
		}

		return value;
	}
}

/// <summary>Hashing helpers. Kept separate so the verification rule is stated once.</summary>
public static class AssetDigest
{
	/// <summary>The digest of a file on disk, lowercase hex.</summary>
	public static async Task<string> OfFileAsync(string path, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(
			path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);

		var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
		return Convert.ToHexStringLower(hash);
	}

	/// <summary>
	/// Digest comparison is case-insensitive: a pin is written by a human in a message template, so it may
	/// arrive in either case.
	/// </summary>
	public static bool Matches(string expected, string actual) =>
		string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}
