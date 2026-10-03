using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.IO.Pipes;
using Microsoft.Win32;

namespace Jarvis.Service;

/// <summary>
/// The operations that need administrator rights, and the few that do not.
/// <para>
/// Everything here runs as LocalSystem, which is the whole point of the service and also the reason it
/// is careful: this code can write anywhere in the registry and create scheduled tasks for any user. Every
/// operation therefore re-validates its own arguments rather than trusting that the caller did, because
/// the caller is an assistant whose input is a model's output.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedOperations
{
	private const int MaximumTextCharacters = 8_000;

	private const int MaximumValues = 256;

	public static string Describe()
	{
		var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
		var principal = new System.Security.Principal.WindowsPrincipal(identity);

		return $"Running as {identity.Name} ({(principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "administrator" : "not an administrator")}).";
	}

	public static bool IsAdministrator
	{
		get
		{
			using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
			return new System.Security.Principal.WindowsPrincipal(identity)
				.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
		}
	}

	public static string Status() => Describe();

	/// <summary>
	/// Reads a registry value. HKLM is the reason this exists: an unelevated plugin cannot read half of
	/// the machine's configuration.
	/// </summary>
	public static JsonObject RegistryGet(JsonObject arguments)
	{
		if (!TryHive(arguments, out var hive, out var view, out var failure))
		{
			return Protocol.Reply(false, failure!);
		}

		var path = Text(arguments, "path");

		if (path.Length == 0)
		{
			return Protocol.Reply(false, "A key path is needed.");
		}

		if (!IsSafePath(path))
		{
			return Protocol.Reply(false, "That key path is not allowed.");
		}

		try
		{
			using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);

			if (key is null)
			{
				return Protocol.Reply(false, $"{path} does not exist.");
			}

			var name = Text(arguments, "name");

			if (name.Length == 0)
			{
				var names = key.GetValueNames();

				if (names.Length == 0)
				{
					return Protocol.Reply(false, $"{path} has no values.");
				}

				var listing = new JsonArray();

				foreach (var existing in names.Take(MaximumValues))
				{
					listing.Add(new JsonObject
					{
						["name"] = existing,
						["value"] = ReadValue(key, existing),
						["kind"] = key.GetValueKind(existing).ToString(),
					});
				}

				return Protocol.Reply(true, $"{names.Length} value(s) under {path}.", new JsonObject { ["values"] = listing });
			}

			var match = key.GetValueNames()
				.FirstOrDefault(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));

			if (match is null)
			{
				return Protocol.Reply(false, $"{path} has no value called '{name}'.");
			}

			return Protocol.Reply(true, $"{match} = {ReadValue(key, match)}", new JsonObject
			{
				["name"] = match,
				["value"] = ReadValue(key, match),
				["kind"] = key.GetValueKind(match).ToString(),
			});
		}
		catch (System.Security.SecurityException exception)
		{
			return Protocol.Reply(false, $"{path} could not be read: {exception.Message}");
		}
		catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException or IOException)
		{
			return Protocol.Reply(false, $"{path} could not be read: {exception.Message}");
		}
	}

	public static JsonObject RegistrySet(JsonObject arguments)
	{
		if (!TryHive(arguments, out var hive, out var view, out var failure))
		{
			return Protocol.Reply(false, failure!);
		}

		var path = Text(arguments, "path");
		var name = Text(arguments, "name");

		if (path.Length == 0 || name.Length == 0)
		{
			return Protocol.Reply(false, "A key path and a value name are both needed.");
		}

		if (!IsSafePath(path) || !IsSafeName(name))
		{
			return Protocol.Reply(false, "That key path or value name is not allowed.");
		}

		try
		{
			var kind = KindOf(Text(arguments, "kind"));
			var value = Coerce(kind, arguments["value"]);

			using var key = RegistryKey.OpenBaseKey(hive, view).CreateSubKey(path, writable: true);

			if (key is null)
			{
				return Protocol.Reply(false, $"{path} is not there and was not created.");
			}

			key.SetValue(name, value, kind);

			return Protocol.Reply(true, $"Set {path}\\{name}.");
		}
		catch (Exception exception) when (
			exception is ArgumentException
				or ArgumentOutOfRangeException
				or UnauthorizedAccessException
				or System.Security.SecurityException
				or IOException)
		{
			return Protocol.Reply(false, $"{path} could not be written: {exception.Message}");
		}
	}

	public static JsonObject RegistryDelete(JsonObject arguments)
	{
		if (!TryHive(arguments, out var hive, out var view, out var failure))
		{
			return Protocol.Reply(false, failure!);
		}

		var path = Text(arguments, "path");

		if (path.Length == 0)
		{
			return Protocol.Reply(false, "A key path is needed.");
		}

		if (!IsSafePath(path))
		{
			return Protocol.Reply(false, "That key path is not allowed.");
		}

		try
		{
			if (arguments["deleteKey"]?.GetValue<bool>() == true)
			{
				RegistryKey.OpenBaseKey(hive, view).DeleteSubKeyTree(path, throwOnMissingSubKey: false);
				return Protocol.Reply(true, $"Deleted {path} and anything under it.");
			}

			var name = Text(arguments, "name");

			if (name.Length == 0)
			{
				return Protocol.Reply(false, "Name the value to remove, or pass deleteKey.");
			}

			using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path, writable: true);

			if (key is null)
			{
				return Protocol.Reply(false, $"{path} does not exist.");
			}

			var match = key.GetValueNames()
				.FirstOrDefault(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));

			if (match is null)
			{
				return Protocol.Reply(false, $"{path} has no value called '{name}'.");
			}

			key.DeleteValue(match, throwOnMissingValue: false);

			return Protocol.Reply(true, $"Deleted {path}\\{match}.");
		}
		catch (Exception exception) when (
			exception is ArgumentException
				or UnauthorizedAccessException
				or System.Security.SecurityException
				or IOException)
		{
			return Protocol.Reply(false, $"{path} could not be changed: {exception.Message}");
		}
	}

	/// <summary>
	/// The keys this service will not touch, whatever a caller asks for.
	/// <para>
	/// A denylist rather than an allowlist, because the useful keys are too varied to enumerate and a
	/// denylist still catches the ones that would break the machine rather than the user's settings.
	/// </para>
	/// </summary>
	private static bool IsSafePath(string path)
	{
		if (path.Length > 512 || path.Contains("..", StringComparison.Ordinal))
		{
			return false;
		}

		// Control characters in a key name are how a caller tries to smuggle one path past a log or a
		// console.
		foreach (var character in path)
		{
			if (char.IsControl(character))
			{
				return false;
			}
		}

		string[] forbidden =
		[
			@"\SAM",
			@"\SAM\SAM",
			@"\SECURITY",
			@"\SYSTEM\CurrentControlSet\Services\Schedule",
			@"\Microsoft\Windows NT\CurrentVersion\Svchost",
			@"\Microsoft\Windows NT\CurrentVersion\Winlogon",
			@"\Microsoft\Windows NT\CurrentVersion\Windows",
			@"\Microsoft\Windows NT\CurrentVersion\Shell",
			@"\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
			@"\Microsoft\Windows Defender",
			@"\Boot",
			@"\EFI",
		];

		foreach (var blocked in forbidden)
		{
			if (path.StartsWith(blocked, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsSafeName(string name)
	{
		if (name.Length is 0 or > 512)
		{
			return false;
		}

		foreach (var character in name)
		{
			if (char.IsControl(character))
			{
				return false;
			}
		}

		return true;
	}

	private static bool TryHive(JsonObject arguments, out RegistryHive hive, out RegistryView view, out string? failure)
	{
		// The 64-bit view throughout, because the 32-bit view is a separate set of keys and a caller
		// reading a path out of regedit would otherwise be looking somewhere else entirely.
		failure = null;
		view = RegistryView.Registry64;

		switch (Text(arguments, "hive").ToUpperInvariant())
		{
			case "HKCU":
			case "HKEY_CURRENT_USER":
				hive = RegistryHive.CurrentUser;
				return true;

			case "HKLM":
			case "HKEY_LOCAL_MACHINE":
				hive = RegistryHive.LocalMachine;
				return true;

			case "HKU":
			case "HKEY_USERS":
				hive = RegistryHive.Users;
				return true;

			case "HKCC":
			case "HKEY_CURRENT_CONFIG":
				hive = RegistryHive.CurrentConfig;
				return true;

			default:
				hive = default;
				failure = "'hive' must be HKCU, HKLM, HKU or HKCC.";
				return false;
		}
	}

	private static RegistryValueKind KindOf(string kind) => kind.ToLowerInvariant() switch
	{
		"" or "string" => RegistryValueKind.String,
		"expandstring" => RegistryValueKind.ExpandString,
		"multistring" => RegistryValueKind.MultiString,
		"binary" => RegistryValueKind.Binary,
		"dword" or "int32" => RegistryValueKind.DWord,
		"qword" or "int64" => RegistryValueKind.QWord,
		_ => throw new ArgumentOutOfRangeException(nameof(kind), $"'{kind}' is not a registry value type."),
	};

	private static object Coerce(RegistryValueKind kind, JsonNode? raw) => kind switch
	{
		RegistryValueKind.DWord or RegistryValueKind.QWord => ReadNumber(raw, kind),
		RegistryValueKind.MultiString => ReadList(raw, MaximumTextCharacters),
		RegistryValueKind.Binary => ReadBytes(raw),
		_ => Bounded(raw?.GetValue<string>() ?? string.Empty, MaximumTextCharacters),
	};

	private static string ReadValue(RegistryKey key, string name)
	{
		var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

		return value switch
		{
			null => "(not set)",
			byte[] bytes => Convert.ToHexString(bytes),
			string[] many => string.Join("\n", many),
			_ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "(unreadable)",
		};
	}

	private static long ReadNumber(JsonNode? raw, RegistryValueKind kind)
	{
		long number = raw switch
		{
			JsonValue value when value.TryGetValue<int>(out var small) => small,
			JsonValue value when value.TryGetValue<long>(out var wide) => wide,
			JsonValue value when value.TryGetValue<string>(out var text)
				&& long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var converted) => converted,
			_ => throw new ArgumentException("A number value needs a number."),
		};

		if (kind == RegistryValueKind.DWord)
		{
			if (number is < int.MinValue or > int.MaxValue)
			{
				throw new ArgumentOutOfRangeException(nameof(raw), "That number does not fit a dword.");
			}

			return (int)number;
		}

		return number;
	}

	private static string[] ReadList(JsonNode? raw, int limit)
	{
		if (raw is not JsonArray array)
		{
			throw new ArgumentException("A multi-string value needs a list of strings.");
		}

		var values = new List<string>();

		foreach (var item in array.Take(MaximumValues))
		{
			values.Add(Bounded(item?.GetValue<string>() ?? string.Empty, limit));
		}

		return [.. values];
	}

	private static byte[] ReadBytes(JsonNode? raw)
	{
		if (raw is not JsonArray array)
		{
			throw new ArgumentException("A binary value needs a list of bytes.");
		}

		var bytes = new List<byte>();

		foreach (var item in array.Take(MaximumValues * 16))
		{
			bytes.Add(item is null ? (byte)0 : item.GetValue<byte>());
		}

		return [.. bytes];
	}

	/// <summary>Reads an argument as text, refusing anything oversized rather than truncating a command.</summary>
	private static string Text(JsonObject arguments, string key)
	{
		var node = arguments[key];

		if (node is null)
		{
			return string.Empty;
		}

		return Bounded(node.GetValue<string>(), MaximumTextCharacters);
	}

	private static string Bounded(string value, int limit) =>
		value.Length <= limit ? value : throw new ArgumentException($"That value is longer than {limit} characters.");
}
