using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Jarvis.Service.Tests;

/// <summary>
/// The privileged operations' own validation.
/// <para>
/// These run unprivileged, against the current user's registry, which is exactly the condition the guards
/// have to hold under. A guard that only works because the caller happens to be unprivileged is not a guard.
/// </para>
/// </summary>
[TestFixture]
public class ElevatedOperationTests
{
	/// <summary>
	/// The keys that would break the machine rather than the user's settings. Each one is a way to make
	/// Windows fail to boot, to run something as the wrong identity, or to defeat endpoint security.
	/// </summary>
	private static readonly string[] DeniedPaths =
	[
		@"\SAM",
		@"\SAM\SAM\Domains\Builtin\Users",
		@"\SECURITY\Policy",
		@"\SYSTEM\CurrentControlSet\Services\Schedule",
		@"\Microsoft\Windows NT\CurrentVersion\Winlogon",
		@"\Microsoft\Windows NT\CurrentVersion\Windows",
		@"\Microsoft\Windows NT\CurrentVersion\Shell",
		@"\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\notepad",
		@"\Microsoft\Windows Defender",
		@"\Boot\BCD",
		@"\EFI\Boot\bootx64.efi",
	];

	[Test]
	public void The_denied_key_paths_are_refused()
	{
		foreach (var path in DeniedPaths)
		{
			var outcome = ElevatedOperations.RegistryGet(new JsonObject
			{
				["hive"] = "HKLM",
				["path"] = path,
			});

			Assert.That(outcome["ok"]!.GetValue<bool>(), Is.False, $"{path} was allowed");
			Assert.That(outcome["content"]!.GetValue<string>(), Does.Contain("not allowed"), path);
		}
	}

	/// <summary>Every write is a chance to break the machine, so every write is checked, not only reads.</summary>
	[Test]
	public void The_denied_key_paths_are_refused_for_writes_too()
	{
		var arguments = new JsonObject
		{
			["hive"] = "HKLM",
			["path"] = @"\Microsoft\Windows NT\CurrentVersion\Winlogon",
			["name"] = "Shell",
			["value"] = "anything",
		};

		foreach (var outcome in new[]
		{
			ElevatedOperations.RegistrySet(arguments),
			ElevatedOperations.RegistryDelete(arguments),
		})
		{
			Assert.That(outcome["ok"]!.GetValue<bool>(), Is.False);
			Assert.That(outcome["content"]!.GetValue<string>(), Does.Contain("not allowed"));
		}
	}

	/// <summary>
	/// Control characters are how a request tries to smuggle one thing past a log line or a console. They are
	/// refused rather than stripped, because stripping would change the path the caller asked for.
	/// <para>
	/// The paths sit under the allowed root on purpose. Anywhere else they would be refused by the allowlist,
	/// and the test would pass without ever reaching the control character check.
	/// </para>
	/// </summary>
	[TestCase("SOFTWARE\\Jarvis\\Contoso\nInjected")]
	[TestCase("SOFTWARE\\Jarvis\\Contoso\rInjected")]
	[TestCase("SOFTWARE\\Jarvis\\Contoso\0Injected")]
	[TestCase("SOFTWARE\\Jarvis\\Contoso\tInjected")]
	public void Control_characters_in_a_path_are_refused(string path)
	{
		var outcome = ElevatedOperations.RegistryGet(new JsonObject { ["hive"] = "HKLM", ["path"] = path });

		Assert.That(outcome["ok"]!.GetValue<bool>(), Is.False, $"'{path}' was allowed");
	}

	[Test]
	public void A_value_name_with_control_characters_is_refused()
	{
		var outcome = ElevatedOperations.RegistrySet(new JsonObject
		{
			["hive"] = "HKLM",
			["path"] = @"SOFTWARE\Jarvis\ServiceTests",
			["name"] = "Bad\nName",
			["value"] = "x",
		});

		Assert.That(outcome["ok"]!.GetValue<bool>(), Is.False);
	}

	[Test]
	public void Traversal_in_a_path_is_refused()
	{
		var outcome = ElevatedOperations.RegistryGet(new JsonObject
		{
			["hive"] = "HKLM",
			["path"] = @"SOFTWARE\Jarvis\..\..\..\SAM",
		});

		Assert.That(outcome["ok"]!.GetValue<bool>(), Is.False);
	}

	/// <summary>
	/// Only HKLM belongs to the service. The plugin runs as the signed-in user, so it can reach that user's own
	/// hive itself; the service accepting HKCU would have silently written LocalSystem's hive under a name
	/// that reads as the caller's.
	/// </summary>
	[TestCase("HKCU", "HKEY_CURRENT_USER")]
	[TestCase("HKU", "HKEY_USERS")]
	[TestCase("HKCC", "HKEY_CURRENT_CONFIG")]
	public void A_hive_that_is_not_HKLM_is_refused(string hive, string longForm)
	{
		var outcome = ElevatedOperations.RegistryGet(new JsonObject
		{
			["hive"] = hive,
			["path"] = @"SOFTWARE\Jarvis\ServiceTests",
		});

		Assert.Multiple(() =>
		{
			Assert.That(outcome["ok"]!.GetValue<bool>(), Is.False, $"'{hive}' was allowed");
			Assert.That(outcome["content"]!.GetValue<string>(), Does.Contain("only writes HKLM"), hive);
			Assert.That(longForm, Is.Not.Empty);
		});
	}

	[TestCase("HKXX")]
	[TestCase("CURRENT_USER")]
	[TestCase("")]
	[TestCase("   ")]
	[TestCase("hklm ")]
	public void An_unknown_hive_is_refused(string hive)
	{
		var outcome = ElevatedOperations.RegistryGet(new JsonObject
		{
			["hive"] = hive,
			["path"] = @"Software\Contoso",
		});

		Assert.That(outcome["ok"]!.GetValue<bool>(), Is.False, $"'{hive}' was allowed");
	}

	/// <summary>
	/// Both spellings have to work, because regedit shows the long form and a model is as likely to copy
	/// that as the abbreviation. Reaching the registry at all is the pass condition, which is why the
	/// assertion is that the answer names the key rather than that it merely failed.
	/// </summary>
	[TestCase("HKLM")]
	[TestCase("hklm")]
	[TestCase("HKEY_LOCAL_MACHINE")]
	[TestCase("hkey_local_machine")]
	public void Both_spellings_of_the_hive_are_accepted(string hive)
	{
		var outcome = ElevatedOperations.RegistryGet(new JsonObject
		{
			["hive"] = hive,
			["path"] = @"SOFTWARE\Jarvis\ServiceTests\Nothing",
			["name"] = "Nothing",
		});

		Assert.That(outcome["content"]!.GetValue<string>(), Does.Contain(@"Jarvis\ServiceTests\Nothing"), hive);
	}

	/// <summary>
	/// The allowlist, which is the whole of what bounds the damage if the plugin process is compromised.
	/// </summary>
	/// <remarks>
	/// A sibling whose name merely starts with an allowed root has to be refused. That single case is why
	/// matching is on segment boundaries rather than a string comparison.
	/// </remarks>
	[TestCase(@"SOFTWARE\Jarvis", true)]
	[TestCase(@"SOFTWARE\Jarvis\Settings", true)]
	[TestCase(@"SOFTWARE\Jarvis\Settings\Deep\Value", true)]
	[TestCase(@"SOFTWARE\JarvisEvil", false)]
	[TestCase(@"SOFTWARE\Jarvis2\Settings", false)]
	[TestCase(@"SOFTWARE\Jarv", false)]
	[TestCase(@"SOFTWARE", false)]
	[TestCase(@"SYSTEM\CurrentControlSet\Services\Evil\Start", false)]
	[TestCase(@"SYSTEM\CurrentControlSet\Control\Session Manager\BootExecute", false)]
	[TestCase(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false)]
	[TestCase(@"SOFTWARE\Classes\CLSID\{0}\LocalServer32", false)]
	public void Only_the_allowed_roots_are_writable(string path, bool expected)
	{
		Assert.That(RegistryGuard.IsWritable(path), Is.EqualTo(expected), path);
	}

	/// <summary>
	/// Every spelling the registry treats as the same key has to reach the same verdict. A denylist keyed on
	/// <c>StartsWith</c> was bypassed by all of these while still reading as protected.
	/// </summary>
	[TestCase(@"\SOFTWARE\Jarvis\Settings")]
	[TestCase(@"  \SOFTWARE\Jarvis\Settings")]
	[TestCase(@".\SOFTWARE\Jarvis\Settings")]
	[TestCase(@"SOFTWARE\\Jarvis\\Settings")]
	[TestCase(@"SOFTWARE\.\Jarvis\.\Settings")]
	[TestCase(@"\.\SOFTWARE\Jarvis\.\Settings")]
	[TestCase("SOFTWARE\\Jarvis \\Settings")]
	[TestCase(@"SOFTWARE\Jarvis\Settings\")]
	public void A_path_that_resolves_to_an_allowed_root_is_still_writable(string path)
	{
		Assert.That(RegistryGuard.TryNormalize(path, out var normalized), Is.True, path);
		Assert.That(RegistryGuard.IsWritable(path), Is.True, path);
		Assert.That(normalized, Is.EqualTo(@"SOFTWARE\Jarvis\Settings"), path);
	}

	/// <summary>The same spellings applied to a key that is not allowed stay refused.</summary>
	[TestCase(@"\SYSTEM\CurrentControlSet\Services\Schedule")]
	[TestCase(@"  \SYSTEM\CurrentControlSet\Services\Schedule")]
	[TestCase(@".\SYSTEM\CurrentControlSet\Services\Schedule")]
	[TestCase(@"SYSTEM\.\CurrentControlSet\Services\Schedule")]
	[TestCase(@"SYSTEM//CurrentControlSet\\Services\Schedule")]
	public void A_denied_path_stays_denied_however_it_is_spelled(string path)
	{
		Assert.That(RegistryGuard.IsWritable(path), Is.False, path);
	}

	[TestCase(@"SOFTWARE\Jarvis\..\Windows")]
	[TestCase(@"..\SOFTWARE\Jarvis")]
	[TestCase(@"SOFTWARE\Jarvis\..\..\SAM")]
	public void Traversal_out_of_an_allowed_root_is_refused(string path)
	{
		Assert.That(RegistryGuard.TryNormalize(path, out _), Is.False, path);
		Assert.That(RegistryGuard.IsWritable(path), Is.False, path);
	}

	/// <summary>
	/// Deleting a subtree under an allowed root is allowed, because that is a real cleanup. Deleting the root
	/// itself is not, because it takes every key beneath it rather than the one that was named.
	/// </summary>
	[TestCase(@"SOFTWARE\Jarvis", false)]
	[TestCase(@"\.\SOFTWARE\Jarvis\", false)]
	[TestCase(@"SOFTWARE\Jarvis\Settings", true)]
	[TestCase(@"SOFTWARE\Jarvis\Settings\Deep", true)]
	public void A_subtree_delete_may_not_remove_an_allowed_root(string path, bool expected)
	{
		Assert.That(RegistryGuard.IsSubtreeDeletable(path), Is.EqualTo(expected), path);
	}

	/// <summary>
	/// Deleting a whole key has to be asked for. Conflating "which value" with "all of it" is how a real
	/// setting disappears.
	/// </summary>
	[Test]
	public void Deleting_a_whole_key_has_to_be_asked_for()
	{
		var outcome = ElevatedOperations.RegistryDelete(new JsonObject
		{
			["hive"] = "HKLM",
			["path"] = @"SOFTWARE\Jarvis\ServiceTests",
		});

		Assert.Multiple(() =>
		{
			Assert.That(outcome["ok"]!.GetValue<bool>(), Is.False);
			Assert.That(outcome["content"]!.GetValue<string>(), Does.Contain("deleteKey"));
		});
	}

	/// <summary>A number value given text has to be refused rather than silently stored as zero.</summary>
	[TestCase("dword", "not a number")]
	[TestCase("qword", "not a number")]
	public void A_number_value_of_the_wrong_shape_is_refused(string kind, string value)
	{
		var outcome = ElevatedOperations.RegistrySet(new JsonObject
		{
			["hive"] = "HKLM",
			["path"] = @"SOFTWARE\Jarvis\ServiceTests",
			["name"] = "Number",
			["kind"] = kind,
			["value"] = value,
		});

		Assert.That(outcome["ok"]!.GetValue<bool>(), Is.False, $"{kind} accepted '{value}'");
		Assert.That(outcome["content"]!.GetValue<string>(), Does.Contain("needs a number"), kind);
	}

	/// <summary>A value too large for its type must be refused, not wrapped round.</summary>
	[Test]
	public void A_number_too_large_for_a_dword_is_refused()
	{
		var outcome = ElevatedOperations.RegistrySet(new JsonObject
		{
			["hive"] = "HKLM",
			["path"] = @"SOFTWARE\Jarvis\ServiceTests",
			["name"] = "TooBig",
			["kind"] = "dword",
			["value"] = 4_294_967_296L,
		});

		Assert.Multiple(() =>
		{
			Assert.That(outcome["ok"]!.GetValue<bool>(), Is.False);

			// The reason matters, not the outcome: unprivileged, the write would fail too, so ok alone
			// cannot tell a refused value from a refused caller.
			Assert.That(outcome["content"]!.GetValue<string>(), Does.Contain("does not fit a dword"));
		});
	}

	[Test]
	public void An_unknown_value_type_is_refused()
	{
		var outcome = ElevatedOperations.RegistrySet(new JsonObject
		{
			["hive"] = "HKLM",
			["path"] = @"SOFTWARE\Jarvis\ServiceTests",
			["name"] = "Bad",
			["kind"] = "colour",
			["value"] = "red",
		});

		Assert.Multiple(() =>
		{
			Assert.That(outcome["ok"]!.GetValue<bool>(), Is.False);
			Assert.That(outcome["content"]!.GetValue<string>(), Does.Contain("is not a registry value type"));
		});
	}

	[Test]
	public void The_pipe_server_refuses_anything_it_does_not_understand()
	{
		using var pipe = new PipeServer(NullLogger.Instance, UniquePipeName());

		var refused = pipe.Handle("this is not a request");
		var wrongVersion = pipe.Handle(new JsonObject { ["v"] = 99, ["op"] = "ping" }.ToJsonString());

		Assert.Multiple(() =>
		{
			Assert.That(refused["ok"]!.GetValue<bool>(), Is.False);
			Assert.That(wrongVersion["ok"]!.GetValue<bool>(), Is.False);
			Assert.That(refused["content"]!.GetValue<string>(), Does.Contain("not understood"));
		});
	}

	[Test]
	public void The_pipe_server_answers_a_ping()
	{
		using var pipe = new PipeServer(NullLogger.Instance, UniquePipeName());

		var reply = pipe.Handle(Protocol.Request("ping").ToJsonString());

		Assert.Multiple(() =>
		{
			Assert.That(reply["ok"]!.GetValue<bool>(), Is.True);
			Assert.That(reply["content"]!.GetValue<string>(), Is.EqualTo("pong"));
		});
	}

	/// <summary>
	/// An operation this build does not have is refused as unreadable rather than half-recognised.
	/// <para>
	/// The service used to list six names it had no code path for, so they parsed as valid requests and were
	/// then refused by the dispatcher. That is a different answer from the one an operation nobody has ever
	/// implemented deserves, and it is the answer that matters: a plugin should learn the service is not the
	/// build it expected.
	/// </para>
	/// </summary>
	[TestCase("scheduled_task_create")]
	[TestCase("scheduled_task_list")]
	[TestCase("settings_get")]
	public void An_operation_that_does_not_exist_is_refused_as_unreadable(string operation)
	{
		using var pipe = new PipeServer(NullLogger.Instance, UniquePipeName());

		var reply = pipe.Handle(Protocol.Request(operation).ToJsonString());

		Assert.Multiple(() =>
		{
			Assert.That(reply["ok"]!.GetValue<bool>(), Is.False);
			Assert.That(reply["content"]!.GetValue<string>(), Does.Contain("not understood"));
		});
	}

	/// <summary>
	/// Every name the service declares must be one the dispatcher actually handles.
	/// <para>
	/// This is the check that stops the declared list drifting back into describing an interface that does
	/// not exist. It reads the two lists the plugin and the service each keep, because the plugin cannot
	/// reference the service assembly, and the two have to agree exactly or a caller builds a request the
	/// other side will not accept.
	/// </para>
	/// </summary>
	[Test]
	public void The_two_sides_declare_exactly_the_same_operations()
	{
		Assert.That(
			Jarvis.Plugin.Core.ServiceProtocol.Operations.Order(StringComparer.Ordinal),
			Is.EqualTo(Protocol.Operations.Order(StringComparer.Ordinal)));
	}

	[Test]
	public void Every_declared_operation_is_implemented()
	{
		using var pipe = new PipeServer(NullLogger.Instance, UniquePipeName());

		var unimplemented = Protocol.Operations
			.Where(operation => pipe
				.Handle(Protocol.Request(operation).ToJsonString())["content"]!
				.GetValue<string>()
				.Contains("is not something this service does", StringComparison.Ordinal))
			.ToArray();

		Assert.That(unimplemented, Is.Empty, "declared but not implemented");
	}

	[Test]
	public void A_status_is_answered_even_without_administrator_rights()
	{
		using var pipe = new PipeServer(NullLogger.Instance, UniquePipeName());

		var reply = pipe.Handle(Protocol.Request("status").ToJsonString());

		Assert.Multiple(() =>
		{
			Assert.That(reply["ok"]!.GetValue<bool>(), Is.True);
			Assert.That(reply["content"]!.GetValue<string>(), Does.Contain("Running as"));
		});
	}

	/// <summary>
	/// The tray's administrator switch, which used to exist only as a tick box.
	/// <para>
	/// Every machine-changing operation has to be refused while it is off, and the two that change nothing
	/// have to keep working, or a caller cannot discover that the switch is the reason.
	/// </para>
	/// </summary>
	[TestCase("registry_get")]
	[TestCase("registry_set")]
	[TestCase("registry_delete")]
	public void Administrator_operations_are_refused_while_the_switch_is_off(string operation)
	{
		using var pipe = new PipeServer(NullLogger.Instance, UniquePipeName())
		{
			AllowAdminOperations = false,
		};

		var reply = pipe.Handle(Protocol.Request(operation, new JsonObject
		{
			["hive"] = "HKLM",
			["path"] = @"SOFTWARE\Jarvis\ServiceTests",
			["name"] = "Anything",
			["value"] = "anything",
		}).ToJsonString());

		Assert.Multiple(() =>
		{
			Assert.That(reply["ok"]!.GetValue<bool>(), Is.False, operation);
			Assert.That(reply["content"]!.GetValue<string>(), Does.Contain("switched off"));
		});
	}

	[TestCase("ping")]
	[TestCase("status")]
	public void A_call_that_changes_nothing_still_works_while_the_switch_is_off(string operation)
	{
		using var pipe = new PipeServer(NullLogger.Instance, UniquePipeName())
		{
			AllowAdminOperations = false,
		};

		var reply = pipe.Handle(Protocol.Request(operation).ToJsonString());

		Assert.That(reply["ok"]!.GetValue<bool>(), Is.True, operation);
	}

	[Test]
	public void The_switch_is_on_by_default_so_a_fresh_install_is_not_refusing_everything()
	{
		using var pipe = new PipeServer(NullLogger.Instance, UniquePipeName());

		Assert.That(pipe.AllowAdminOperations, Is.True);
	}

	/// <summary>
	/// A pipe name of this test's own.
	/// <para>
	/// Not the production name: the service allows one instance per pipe name, so a test that used the real
	/// one would fail whenever the service happened to be installed and running, which is exactly when a
	/// developer most wants to run the tests.
	/// </para>
	/// </summary>
	private static string UniquePipeName() =>
		Protocol.PipeNameForSuffix(UniqueSuffix());

	/// <summary>
	/// A pipe-name suffix that is actually unique.
	/// <para>
	/// The first eight hex characters of a version 7 GUID are the high bits of its timestamp, so two
	/// identifiers minted in the same millisecond share them. They looked unique and were not, which only
	/// stayed hidden because the service allowed unlimited instances per pipe name: every test in a fixture
	/// was quietly talking to the first server the fixture started.
	/// </para>
	/// </summary>
	private static string UniqueSuffix() => Guid.CreateVersion7().ToString("N")[^8..];
}
