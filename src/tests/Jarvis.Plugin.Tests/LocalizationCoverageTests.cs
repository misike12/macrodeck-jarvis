using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Every declared string is used, and every translation belongs to a key that exists.
/// <para>
/// The generator flags a key that exists only in a translation, which catches one direction of drift. Nothing
/// caught the other: a key in the default catalogue that no code path reads. That is how the catalogue grew
/// to carry two copies of the same microphone error, a seven-key group for an action that had been replaced,
/// and five heading labels for section headers the widget editor never emitted.
/// </para>
/// </summary>
[TestFixture]
public class LocalizationCoverageTests
{
	/// <summary>
	/// Keys allowed to be unreferenced, each with why it still exists.
	/// <para>
	/// Kept as a list rather than deleted because a key can be genuinely reserved for a caller outside this
	/// assembly. Anything added here has to say why, so the list cannot become a dumping ground.
	/// </para>
	/// </summary>
	private static readonly Dictionary<string, string> Reserved = [];

	[Test]
	public void Every_declared_string_is_used_somewhere()
	{
		var declared = KeysIn("Strings.resx");
		var source = ReadSource();

		var dead = declared
			.Where(key => !Reserved.ContainsKey(key))
			.Where(key => !LeafIsCalled(key, source))
			.ToArray();

		Assert.That(dead, Is.Empty, "declared but never referenced. Wire it, or delete it and its translations.");
	}

	[Test]
	public void Every_translation_belongs_to_a_declared_key()
	{
		var declared = KeysIn("Strings.resx").ToHashSet(StringComparer.Ordinal);

		var orphans = KeysIn("Strings.de.resx")
			.Where(key => !declared.Contains(key))
			.ToArray();

		Assert.That(orphans, Is.Empty, "translated but not declared, so the translation can never be reached");
	}

	/// <summary>
	/// The placeholder names have to match exactly, because they are the method's parameters. A translation
	/// that drops one compiles away to a different signature and fails only at the point of use.
	/// </summary>
	[Test]
	public void Every_translation_keeps_the_placeholders_its_key_declares()
	{
		var declared = PlaceholdersByKey("Strings.resx");
		var translated = PlaceholdersByKey("Strings.de.resx");

		var mismatched = translated
			.Select(pair => (pair.Key, Actual: pair.Value, Expected: declared.GetValueOrDefault(pair.Key)))
			.Where(triple => triple.Expected is not null
				&& !triple.Actual.Order(StringComparer.Ordinal).SequenceEqual(triple.Expected.Order(StringComparer.Ordinal)))
			.Select(triple => triple.Key)
			.ToArray();

		Assert.That(mismatched, Is.Empty, "placeholder names differ from the declared key");
	}

	/// <summary>Every key is dotted, because an underscore makes it one flat identifier instead of a group.</summary>
	[Test]
	public void Every_key_is_dotted_and_has_no_underscore()
	{
		var underscored = KeysIn("Strings.resx").Where(key => key.Contains('_', StringComparison.Ordinal)).ToArray();

		Assert.That(underscored, Is.Empty);
	}

	private static bool LeafIsCalled(string key, string source)
	{
		// A plural family is reached through one generated method named after the family, not through the
		// One and Other leaves, so a family is used when its base name is called.
		var leaf = key.Split('.').Last() is { } name && (name is "One" or "Other")
			? key[..^(name.Length + 1)]
			: key;

		var method = leaf.Split('.').Last();

		// A dotted key becomes a nested class, so the reference is Strings.Group.Leaf(), never
		// Strings.Group.Leaf. Testing for the literal dotted form finds nothing at all.
		return Regex.IsMatch(source, $@"\.\s*{Regex.Escape(method)}\s*\(");
	}

	private static IReadOnlyList<string> KeysIn(string fileName) =>
	[
		.. Regex.Matches(
				File.ReadAllText(LocalizationFile(fileName)),
				@"<data name=""([^""]+)""")
			.Select(match => match.Groups[1].Value),
	];

	private static Dictionary<string, List<string>> PlaceholdersByKey(string fileName)
	{
		var text = File.ReadAllText(LocalizationFile(fileName));
		var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);

		foreach (Match data in Regex.Matches(text, "(?s)<data name=\"([^\"]+)\".*?</data>"))
		{
			var value = Regex.Match(data.Value, "(?s)<value>(.*?)</value>").Groups[1].Value;

			found[data.Groups[1].Value] =
			[
				.. Regex.Matches(value, @"\{(\w+)\}")
					.Select(match => match.Groups[1].Value)
					.Distinct(StringComparer.Ordinal),
			];
		}

		return found;
	}

	private static string ReadSource() =>
		string.Join("\n", Directory
			.EnumerateFiles(PluginSourceDirectory(), "*.cs", SearchOption.AllDirectories)
			.Select(File.ReadAllText));

	private static string LocalizationFile(string fileName) =>
		Path.Combine(PluginSourceDirectory(), "Localization", fileName);

	private static string PluginSourceDirectory()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);

		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
		{
			directory = directory.Parent;
		}

		return Path.Combine(
			directory?.FullName ?? throw new InvalidOperationException("The repository root was not found."),
			"src",
			"Jarvis.Plugin");
	}
}