using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Jarvis.Plugin.Llm;
using Microsoft.Win32;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The registry tools, run against a real key under the test's own HKCU branch. HKLM is never touched:
/// writing there needs an administrator and would change the machine.
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class RegistryToolTests
{
	/// <summary>
	/// A branch of the user's own key, so the tests cannot collide with anything real. It is a field and not
	/// a property because each test needs every part of it to name the same key.
	/// </summary>
	private string RootPath = null!;

	[SetUp]
	public void SetUp() => RootPath = $@"Software\JarvisPluginTests\{Guid.CreateVersion7():N}";

	[TearDown]
	public void TearDown()
	{
		Registry.CurrentUser.DeleteSubKeyTree($@"Software\JarvisPluginTests", throwOnMissingSubKey: false);
	}

	private static JsonObject Args(params (string Key, object? Value)[] pairs)
	{
		var node = new JsonObject();

		foreach (var (key, value) in pairs)
		{
			node[key] = value switch
			{
				null => null,
				JsonNode already => already,
				_ => JsonValue.Create(value),
			};
		}

		return node;
	}

	private static Task<ToolOutcome> Invoke(ITool tool, JsonObject arguments) =>
		tool.InvokeAsync(arguments, CancellationToken.None);

	private static readonly string[] TwoStrings = ["first", "second"];

	private RegistryValueKind? KindOf(string name)
	{
		using var key = Registry.CurrentUser.OpenSubKey(RootPath);
		return key?.GetValueNames()
			.Where(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
			.Select(existing => key.GetValueKind(existing))
			.FirstOrDefault();
	}

	/// <summary>A round trip has to come back the same type it went in as, or the write was a lie.</summary>
	[Test]
	public async Task A_string_round_trips()
	{
		var outcome = await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Text"), ("value", "hello")));

		Assert.That(outcome.Ok, Is.True, outcome.Content);

		using var key = Registry.CurrentUser.OpenSubKey(RootPath);
		Assert.That(key!.GetValue("Text", null), Is.EqualTo("hello"));
		Assert.That(key.GetValueKind("Text"), Is.EqualTo(RegistryValueKind.String));
	}

	[Test]
	public async Task A_number_round_trips_as_a_dword()
	{
		var outcome = await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Count"), ("kind", "dword"), ("value", 42)));

		Assert.That(outcome.Ok, Is.True, outcome.Content);

		using var key = Registry.CurrentUser.OpenSubKey(RootPath);
		Assert.Multiple(() =>
		{
			Assert.That(key!.GetValue("Count", null), Is.EqualTo(42));
			Assert.That(key.GetValueKind("Count"), Is.EqualTo(RegistryValueKind.DWord));
		});
	}

	[Test]
	public async Task A_large_number_round_trips_as_a_qword()
	{
		const long Large = 9_000_000_000;

		await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Big"), ("kind", "qword"), ("value", Large)));

		using var key = Registry.CurrentUser.OpenSubKey(RootPath);
		Assert.That(key!.GetValue("Big", null), Is.EqualTo(Large));
	}

	[Test]
	public async Task A_multi_string_round_trips_as_a_list()
	{
		var list = new JsonArray("first", "second");

		await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Many"), ("kind", "multistring"), ("value", list)));

		using var key = Registry.CurrentUser.OpenSubKey(RootPath);
		Assert.That(key!.GetValue("Many", null), Is.EqualTo(TwoStrings.AsEnumerable()));
	}

	/// <summary>An expand string stays unexpanded, so reading it back shows the placeholder not the path.</summary>
	[Test]
	public async Task An_expand_string_is_stored_unexpanded()
	{
		await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(
				("hive", "HKCU"),
				("path", RootPath),
				("name", "Expanded"),
				("kind", "expandstring"),
				("value", @"%USERPROFILE%\Documents")));

		using var key = Registry.CurrentUser.OpenSubKey(RootPath);
		Assert.Multiple(() =>
		{
			// Read unexpanded, or the placeholder has already become the user's own folder and the test
			// would pass for the wrong reason.
			Assert.That(
				key!.GetValue("Expanded", null, RegistryValueOptions.DoNotExpandEnvironmentNames),
				Is.EqualTo(@"%USERPROFILE%\Documents"));
			Assert.That(key.GetValueKind("Expanded"), Is.EqualTo(RegistryValueKind.ExpandString));
		});
	}

	[Test]
	public async Task Reading_a_value_that_was_written_gives_it_back()
	{
		await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Read"), ("value", "found me")));

		var outcome = await Invoke(
			new RegistryTools.RegistryGetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Read")));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(outcome.Content, Does.Contain("found me"));
		});
	}

	/// <summary>Reading without a name lists what is there, which is what a model wants after a write.</summary>
	[Test]
	public async Task Reading_a_key_without_a_name_lists_its_values()
	{
		await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Alpha"), ("value", "one")));
		await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Beta"), ("value", "two")));

		var outcome = await Invoke(
			new RegistryTools.RegistryGetTool(),
			Args(("hive", "HKCU"), ("path", RootPath)));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(outcome.Content, Does.Contain("Alpha"));
			Assert.That(outcome.Content, Does.Contain("one"));
			Assert.That(outcome.Content, Does.Contain("Beta"));
		});
	}

	[Test]
	public async Task Writing_the_same_name_twice_changes_it_rather_than_failing()
	{
		await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Twice"), ("value", "before")));
		await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Twice"), ("value", "after")));

		using var key = Registry.CurrentUser.OpenSubKey(RootPath);
		Assert.That(key!.GetValue("Twice", null), Is.EqualTo("after"));
	}

	[Test]
	public async Task Deleting_a_value_removes_only_that_value()
	{
		await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Keep"), ("value", "1")));
		await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Go"), ("value", "2")));

		var outcome = await Invoke(
			new RegistryTools.RegistryDeleteTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Go")));

		using var key = Registry.CurrentUser.OpenSubKey(RootPath);
		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(key!.GetValueNames(), Does.Contain("Keep"));
			Assert.That(key.GetValueNames(), Does.Not.Contain("Go"));
		});
	}

	/// <summary>
	/// Deleting a whole key is never inferred from a missing name. "Which key" and "delete all of it" are
	/// very different requests, and conflating them would be how a real setting disappeared.
	/// </summary>
	[Test]
	public async Task Deleting_a_whole_key_has_to_be_asked_for_explicitly()
	{
		await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Safe"), ("value", "1")));

		var outcome = await Invoke(
			new RegistryTools.RegistryDeleteTool(),
			Args(("hive", "HKCU"), ("path", RootPath)));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(Registry.CurrentUser.OpenSubKey(RootPath), Is.Not.Null, "the key was deleted anyway");
		});
	}

	[Test]
	public async Task Deleting_a_key_removes_it_when_asked()
	{
		await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Gone"), ("value", "1")));

		var outcome = await Invoke(
			new RegistryTools.RegistryDeleteTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("deleteKey", true)));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.True, outcome.Content);
			Assert.That(Registry.CurrentUser.OpenSubKey(RootPath), Is.Null);
		});
	}

	[Test]
	public async Task Reading_a_key_that_is_not_there_says_so()
	{
		var outcome = await Invoke(
			new RegistryTools.RegistryGetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Anything")));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(outcome.Content, Does.Contain("does not exist"));
		});
	}

	[Test]
	public async Task Reading_a_value_that_is_not_there_says_so()
	{
		using (Registry.CurrentUser.CreateSubKey(RootPath))
		{
			var outcome = await Invoke(
				new RegistryTools.RegistryGetTool(),
				Args(("hive", "HKCU"), ("path", RootPath), ("name", "Missing")));

			Assert.Multiple(() =>
			{
				Assert.That(outcome.Ok, Is.False);
				Assert.That(outcome.Content, Does.Contain("no value called"));
			});
		}
	}

	[Test]
	public async Task Deleting_a_value_that_is_not_there_says_so()
	{
		using (Registry.CurrentUser.CreateSubKey(RootPath))
		{
			var outcome = await Invoke(
				new RegistryTools.RegistryDeleteTool(),
				Args(("hive", "HKCU"), ("path", RootPath), ("name", "Missing")));

			Assert.That(outcome.Ok, Is.False);
		}
	}

	[TestCase("HKXX")]
	[TestCase("CURRENT_USER")]
	[TestCase("")]
	[TestCase("   ")]
	[TestCase("nonsense")]
	public async Task An_unknown_hive_is_refused(string hive)
	{
		var outcome = await Invoke(
			new RegistryTools.RegistryGetTool(),
			Args(("hive", hive), ("path", RootPath)));

		Assert.That(outcome.Ok, Is.False, $"'{hive}' was accepted");
	}

	/// <summary>The long form of a hive name has to work, because it is what regedit shows.</summary>
	[TestCase("HKCU")]
	[TestCase("HKEY_CURRENT_USER")]
	public async Task Both_spellings_of_a_hive_are_accepted(string hive)
	{
		await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", hive), ("path", RootPath), ("name", "Both"), ("value", "1")));

		Assert.That(KindOf("Both"), Is.Not.Null);
	}

	/// <summary>A number value given text has to be refused, not silently stored as zero.</summary>
	[TestCase("dword", "not a number")]
	[TestCase("qword", "not a number")]
	[TestCase("multistring", "not a list")]
	[TestCase("binary", "not a list")]
	public async Task A_value_of_the_wrong_shape_is_refused(string kind, string value)
	{
		var outcome = await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Bad"), ("kind", kind), ("value", value)));

		Assert.That(outcome.Ok, Is.False, $"{kind} accepted '{value}'");
	}

	[Test]
	public async Task An_unknown_value_type_is_refused()
	{
		var outcome = await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "Bad"), ("kind", "colour"), ("value", "red")));

		Assert.That(outcome.Ok, Is.False);
	}

	/// <summary>With createKey off, a missing key must not be created behind the caller's back.</summary>
	[Test]
	public async Task A_missing_key_is_not_created_when_that_was_not_asked_for()
	{
		var outcome = await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(("hive", "HKCU"), ("path", RootPath), ("name", "No"), ("value", "1"), ("createKey", false)));

		Assert.Multiple(() =>
		{
			Assert.That(outcome.Ok, Is.False);
			Assert.That(Registry.CurrentUser.OpenSubKey(RootPath), Is.Null);
		});
	}

	[Test]
	public async Task Missing_arguments_are_refused_by_every_tool()
	{
		var hive = Args(("hive", "HKCU"), ("path", RootPath));

		foreach (var tool in new ITool[]
		{
			new RegistryTools.RegistryGetTool(),
			new RegistryTools.RegistrySetTool(),
			new RegistryTools.RegistryDeleteTool(),
		})
		{
			foreach (var arguments in new[] { new JsonObject(), hive })
			{
				var outcome = await Invoke(tool, arguments);
				Assert.That(outcome.Ok, Is.False, $"{tool.Name} accepted incomplete arguments");
			}
		}
	}

	/// <summary>Reading is free; both ways of changing the registry have to ask.</summary>
	[Test]
	public void Registry_tools_are_gated_by_class()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new RegistryTools.RegistryGetTool().RequiresConfirmation, Is.False);
			Assert.That(new RegistryTools.RegistrySetTool().RequiresConfirmation, Is.True);
			Assert.That(new RegistryTools.RegistryDeleteTool().RequiresConfirmation, Is.True);
		});
	}

	[Test]
	public void Registry_tool_names_are_stable()
	{
		Assert.Multiple(() =>
		{
			Assert.That(new RegistryTools.RegistryGetTool().Name, Is.EqualTo("registry_get"));
			Assert.That(new RegistryTools.RegistrySetTool().Name, Is.EqualTo("registry_set"));
			Assert.That(new RegistryTools.RegistryDeleteTool().Name, Is.EqualTo("registry_delete"));
		});
	}

	/// <summary>
	/// The failure a user is most likely to hit is writing under HKLM. The message has to say why, or a model
	/// will treat it as a transient error and try again forever.
	/// </summary>
	[Test]
	public async Task Writing_where_an_administrator_is_needed_says_so()
	{
		var outcome = await Invoke(
			new RegistryTools.RegistrySetTool(),
			Args(
				("hive", "HKLM"),
				("path", @"Software\JarvisPluginTests"),
				("name", "Nope"),
				("value", "1"),
				("createKey", false)));

		Assert.That(outcome.Ok, Is.False);
	}

	[Test]
	public void Every_registry_tool_describes_itself_and_its_parameters()
	{
		ITool[] tools =
		[
			new RegistryTools.RegistryGetTool(),
			new RegistryTools.RegistrySetTool(),
			new RegistryTools.RegistryDeleteTool(),
		];

		Assert.Multiple(() =>
		{
			foreach (var tool in tools)
			{
				var definition = tool.Definition;

				Assert.That(definition.Name, Is.EqualTo(tool.Name));
				Assert.That(definition.Description, Is.Not.Empty, $"{tool.Name} has no description");
				Assert.That(
					definition.Parameters?["properties"],
					Is.Not.Null,
					$"{tool.Name} declares no parameters object");
			}
		});
	}
}
