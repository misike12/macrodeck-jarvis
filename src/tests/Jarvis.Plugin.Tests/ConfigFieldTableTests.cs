using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Jarvis.Plugin.Core;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The declared setting table, and the drift it exists to prevent.
/// <para>
/// Three hand-maintained lists used to describe the same settings: the flow's step builders, the store's
/// read-back list, and the field-name constants. Nothing connected them, so they drifted silently: thirteen
/// settings were read back but appeared in no step, which meant the code consuming them read a default
/// permanently, and the only symptom was a setting that could not be changed.
/// </para>
/// </summary>
[TestFixture]
public class ConfigFieldTableTests
{
	/// <summary>
	/// The bounds the setup form offers have to be the bounds the store will keep.
	/// <para>
	/// Nothing enforced the declared minimum or maximum, and the two disagreed three times over:
	/// maxIterations was declared 1 to 32, read back against 1 to 12 and clamped again to 1 to 16 at the
	/// point of use, so a user who chose 20 was shown a value that silently became 12. Every number the
	/// form offers is now checked against the range the store applies, and against the range the code that
	/// consumes it clamps to.
	/// </para>
	/// </summary>
	[Test]
	public void Every_numeric_setting_declares_both_bounds_and_a_default_inside_them()
	{
		var unbounded = JarvisFields.All
			.Where(field => field.Kind is JarvisFieldKind.Number)
			.Where(field => field.Minimum is null || field.Maximum is null)
			.Select(field => field.Name)
			.ToArray();

		Assert.That(unbounded, Is.Empty, "a number the form will accept but the store cannot place");

		foreach (var field in JarvisFields.All.Where(field => field.Kind is JarvisFieldKind.Number))
		{
			Assert.That(field.Minimum!, Is.LessThan(field.Maximum!), $"{field.Name} has an empty range");
		}
	}

	/// <summary>
	/// A threshold that accepts zero is a threshold that can never fire, and the form has to say so.
	/// Both thresholds were declared 0 to 1 while the store discarded anything at or below 0.001, so a user
	/// who deliberately asked for maximum sensitivity got the default instead.
	/// </summary>
	[Test]
	public void A_threshold_cannot_be_offered_a_value_that_can_never_be_crossed()
	{
		foreach (var name in new[]
		{
			JarvisSettingsStoreFields.WakeSensitivityField,
			JarvisSettingsStoreFields.BargeInThresholdField,
		})
		{
			var field = JarvisFields.All.Single(candidate => candidate.Name == name);

			Assert.That(field.Minimum!, Is.GreaterThan(0), $"{name} can be set to a level nothing can reach");
			Assert.That(field.Maximum, Is.EqualTo(JarvisFields.MaxThreshold));
		}
	}

	/// <summary>
	/// The conversation runner clamps the iteration count again at the point of use, so a form that offered a
	/// value above that clamp would be offering a setting the turn quietly shortens.
	/// </summary>
	[Test]
	public void The_declared_iteration_bound_matches_the_one_the_runner_applies()
	{
		var runner = File.ReadAllText(ProjectFile(Path.Combine("Core", "ConversationRunner.cs")));
		var match = Regex.Match(runner, @"Math\.Clamp\(current\.MaxIterations,\s*(?<low>[\w.]+),\s*(?<high>[\w.]+)");

		Assert.That(match.Success, Is.True, "the runner no longer clamps MaxIterations in the expected shape");

		var field = JarvisFields.All.Single(candidate => candidate.Name == JarvisSettingsStoreFields.MaxIterationsField);

		Assert.Multiple(() =>
		{
			Assert.That(
				match.Groups["low"].Value,
				Is.EqualTo($"{nameof(JarvisFields)}.{nameof(JarvisFields.MinIterations)}"));
			Assert.That(
				match.Groups["high"].Value,
				Is.EqualTo($"{nameof(JarvisFields)}.{nameof(JarvisFields.MaxIterations)}"));
			Assert.That(field.Minimum, Is.EqualTo((double)JarvisFields.MinIterations));
			Assert.That(field.Maximum, Is.EqualTo((double)JarvisFields.MaxIterations));
		});
	}

	/// <summary>
	/// A setting the safety gate reads and no step offers is a mode that cannot be used: the per-tool
	/// permission switches were read and set by nobody, so choosing that mode asked for every tool forever.
	/// </summary>
	[Test]
	public void Every_safety_mode_the_gate_reads_is_configurable()
	{
		var declared = JarvisFields.All.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);

		string[] required =
		[
			JarvisSettingsStoreFields.PermitReadField,
			JarvisSettingsStoreFields.PermitWriteField,
			JarvisSettingsStoreFields.PermitExecuteField,
			JarvisSettingsStoreFields.CommandAllowlistField,
		];

		var missing = required.Where(name => !declared.Contains(name)).ToArray();

		Assert.That(missing, Is.Empty, "read by the safety gate but set by no step, so the mode cannot be used");
	}

	/// <summary>
	/// A declared field and the type the store parses it as have to agree. <c>lifetime</c> was a number with a
	/// default of 30, parsed as a three-valued enum, so it never parsed and no code read the result anyway.
	/// </summary>
	[Test]
	public void Every_field_the_store_parses_as_an_enum_is_declared_as_a_choice()
	{
		var source = File.ReadAllText(ProjectFile(Path.Combine("Core", "JarvisSettingsStore.cs")));

		var parsedAsEnum = Regex
			.Matches(source, @"ReadEnum\(JarvisSettingsStoreFields\.(\w+?)(?:Entry)?Field")
			.Select(match => ConstantValue(match.Groups[1].Value))
			.Where(name => name.Length > 0)
			.ToHashSet(StringComparer.Ordinal);

		var byName = JarvisFields.All.ToDictionary(field => field.Name, StringComparer.Ordinal);

		var wrongKind = parsedAsEnum
			.Where(name => byName.TryGetValue(name, out var field) && field.Kind is not JarvisFieldKind.Choice)
			.Order(StringComparer.Ordinal)
			.ToArray();

		Assert.That(
			wrongKind,
			Is.Empty,
			"parsed as an enum but offered as something a person cannot pick from a list");
	}

	[Test]
	public void Every_declared_setting_has_a_unique_name()
	{
		var names = JarvisFields.All.Select(field => field.Name).ToArray();

		Assert.That(names, Is.Unique);
	}

	[Test]
	public void Every_declared_setting_belongs_to_a_step_that_exists()
	{
		var known = new[]
		{
			JarvisFields.ProviderStep,
			JarvisFields.KeysStep,
			JarvisFields.ModelsStep,
			JarvisFields.VoiceStep,
			JarvisFields.BehaviourStep,
		};

		var orphans = JarvisFields.All
			.Select(field => field.Step)
			.Distinct(StringComparer.Ordinal)
			.Where(step => !known.Contains(step, StringComparer.Ordinal))
			.ToArray();

		Assert.That(orphans, Is.Empty, "declared against a step the flow never builds");
	}

	/// <summary>
	/// The check that was missing. Every name the store reads has to be a setting a user can actually change,
	/// and every setting a user can change has to be one the store reads.
	/// </summary>
	[Test]
	public void Every_setting_the_store_reads_is_reachable_from_the_flow()
	{
		var declared = JarvisFields.All.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);

		var unreachable = ReadBackFieldsInSource()
			.Where(field => !declared.Contains(field))
			.ToArray();

		Assert.That(unreachable, Is.Empty, "read back but not in JarvisFields, so no step can set it");
	}

	[Test]
	public void Every_setting_the_flow_offers_is_read_back()
	{
		var read = JarvisFields.ReadBackAsText.ToHashSet(StringComparer.Ordinal);
		var secrets = JarvisFields.All
			.Where(field => field.Kind is JarvisFieldKind.Secret)
			.Select(field => field.Name)
			.ToHashSet(StringComparer.Ordinal);

		var unread = JarvisFields.All
			.Select(field => field.Name)
			.Where(field => !read.Contains(field) && !secrets.Contains(field))
			.ToArray();

		Assert.That(unread, Is.Empty, "offered by a step but never read back, so it does nothing");
	}

	/// <summary>
	/// The three elevated-service switches are read back through the same table, so they have to be in it.
	/// They were declared as constants, read by the store, and shown by no step at all.
	/// </summary>
	[Test]
	public void The_elevated_service_switches_are_all_configurable()
	{
		var names = JarvisFields.All.Select(field => field.Name).ToArray();

		Assert.Multiple(() =>
		{
			Assert.That(names, Contains.Item(JarvisSettingsStoreFields.ServiceEnabledField));
			Assert.That(names, Contains.Item(JarvisSettingsStoreFields.ServiceSchedulingField));
			Assert.That(names, Contains.Item(JarvisSettingsStoreFields.ServiceAdminField));
		});
	}

	[Test]
	public void The_wake_word_can_be_switched_off()
	{
		Assert.That(
			JarvisFields.All.Select(field => field.Name),
			Contains.Item(JarvisSettingsStoreFields.WakeWordEnabledField));
	}

	/// <summary>
	/// The mirror of the read-back check, and the one that would have caught the worse half of the drift.
	/// Reading a field into a dictionary proves nothing: four settings were read every reload and then never
	/// copied into the snapshot, so everything that consulted them saw the default forever.
	/// </summary>
	[Test]
	public void Every_setting_the_store_reads_is_applied_to_the_snapshot()
	{
		var snapshot = SettingsSnapshotFieldsInSource();

		var notApplied = JarvisFields.ReadBackAsText
			.Where(field => !snapshot.Contains(field))
			.ToArray();

		Assert.That(notApplied, Is.Empty, "read back but never assigned to JarvisSettings, so it does nothing");
	}

	/// <summary>The field names assigned inside the store's snapshot construction, read from its source.</summary>
	private static HashSet<string> SettingsSnapshotFieldsInSource() =>
		new(AssignedFieldsInSettingsSnapshot(), StringComparer.Ordinal);

	/// <summary>
	/// The field names the store copies into the snapshot it builds.
	/// <para>
	/// Found by reading the assignment block out of the source rather than by reflection, because the block
	/// is a single object initialiser spanning thirty properties and a test that only counted them would not
	/// notice one being dropped.
	/// </para>
	/// </summary>
	private static IReadOnlyList<string> AssignedFieldsInSettingsSnapshot()
	{
		var text = File.ReadAllText(ProjectFile(Path.Combine("Core", "JarvisSettingsStore.cs")));
		var start = text.IndexOf("var next = new JarvisSettings", StringComparison.Ordinal);

		if (start < 0)
		{
			throw new InvalidOperationException("The settings snapshot could not be found in the store source.");
		}

		var block = text[start..];

		// The first closing brace at the start of a line ends the initialiser.
		var end = block.IndexOf("\n\t\t};", StringComparison.Ordinal);

		return
		[
			.. Regex.Matches(block[..end], @"JarvisSettingsStoreFields\.(\w+)")
				.Select(match => ConstantValue(match.Groups[1].Value))
				.Where(value => value.Length > 0)
				.Distinct(StringComparer.Ordinal),
		];
	}

	private static string ConstantValue(string identifierFragment)
	{
		var field = typeof(JarvisSettingsStoreFields)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.FirstOrDefault(candidate =>
				string.Equals(candidate.Name, identifierFragment, StringComparison.Ordinal));

		return (string?)field?.GetRawConstantValue() ?? string.Empty;
	}

	/// <summary>Every constant in the field-name class has to be declared somewhere, or it is a phantom.</summary>
	[Test]
	public void No_field_name_constant_is_unused()
	{
		var declared = JarvisFields.All.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);

		var constants = typeof(JarvisSettingsStoreFields)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Select(field => (string?)field.GetRawConstantValue() ?? string.Empty)
			.ToArray();

		var phantom = constants.Where(name => !declared.Contains(name)).ToArray();

		Assert.That(phantom, Is.Empty, "declared as a field name but no setting uses it");
	}

	/// <summary>
	/// The field names the store's read-back loop iterates, found by reading its own source.
	/// <para>
	/// Read from source rather than reflected because the loop is a literal list inside a method, and a
	/// rename that broke it would compile. Comparing the declared table against what is actually iterated is
	/// the assertion that would have caught the original drift.
	/// </para>
	/// </summary>
	private static IReadOnlyList<string> ReadBackFieldsInSource()
	{
		var source = ProjectFile(Path.Combine("Core", "JarvisSettingsStore.cs"));
		var text = File.ReadAllText(source);

		// The names used with the Fields suffix inside the read loop.
		return
		[
			.. Regex.Matches(text, @"JarvisSettingsStoreFields\.(\w+?)(?:Entry)?Field")
				.Select(match => ConstantValue(match.Groups[1].Value))
				.Where(name => name.Length > 0)
				.Distinct(StringComparer.Ordinal),
		];
	}

	private static string ProjectFile(params string[] segments) =>
		Path.Combine(FindRepositoryRoot(), Path.Combine([Path.Combine("src", "Jarvis.Plugin"), .. segments]));

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);

		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
	}
}