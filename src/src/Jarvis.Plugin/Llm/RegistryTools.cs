using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// Reading and changing registry values.
/// <para>
/// Both hives are reachable, because there are real settings that only exist under HKLM, such as the
/// per-machine entry for a service to start at boot. What HKLM cannot offer is writing to it from an
/// unelevated plugin, and that failure is reported plainly rather than as a generic error: the model needs
/// to know the difference between "that key does not exist" and "you are not allowed to write there", or it
/// will keep retrying.
/// </para>
/// <para>
/// <c>Default</c> is deliberately absent as a writable value. Deleting it and putting a different one back
/// does not restore the value the key shipped with, so a round trip through these tools would quietly
/// change the machine.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class RegistryTools
{
	/// <summary>
	/// The kinds a value can have, named the way the registry itself names them rather than after a CLR
	/// type, because that is what the model will have seen in a regedit screenshot.
	/// </summary>
	private static RegistryValueKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
	{
		null or "" or "string" => RegistryValueKind.String,
		"expandstring" => RegistryValueKind.ExpandString,
		"multistring" => RegistryValueKind.MultiString,
		"binary" => RegistryValueKind.Binary,
		"dword" or "int32" or "number" => RegistryValueKind.DWord,
		"qword" or "int64" => RegistryValueKind.QWord,
		_ => throw new ArgumentOutOfRangeException(nameof(kind), $"'{kind}' is not a registry value type."),
	};

	private static string ReadValue(object? value) => value switch
	{
		null => "(not set)",
		byte[] bytes => Convert.ToHexString(bytes),
		string[] many => string.Join("\n", many),
		_ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "(unreadable)",
	};

	/// <summary>
	/// Reads a number, refusing text that is not one. <c>GetValue&lt;int&gt;</c> would happily turn the word
	/// "not a number" into zero, and storing a silent zero where the caller meant something else is worse
	/// than refusing.
	/// </summary>
	private static object? ReadNumber(JsonNode? raw) => raw switch
	{
		// A JsonValue holds whatever it was created from and will only hand back that exact type, so an
		// int has to be widened by hand rather than asked for as a long.
		JsonValue value when value.TryGetValue<int>(out var small) => (long)small,
		JsonValue value when value.TryGetValue<long>(out var parsed) => parsed,
		JsonValue value when value.TryGetValue<string>(out var text)
			&& long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var converted) => converted,
		_ => null,
	};

	/// <summary>Turns an argument into the type the chosen kind needs.</summary>
	private static object Coerce(string kind, JsonNode? raw) => ParseKind(kind) switch
	{
		// A number is read once as a long and then narrowed, so "4294967296" is refused for a dword rather than
		// silently wrapping round to zero.
		RegistryValueKind.DWord or RegistryValueKind.QWord => ReadNumber(raw) switch
		{
			long wide when ParseKind(kind) == RegistryValueKind.QWord => wide,
			long wide when wide is >= int.MinValue and <= int.MaxValue => (int)wide,
			long => throw new ArgumentException(
				$"That number does not fit a {ParseKind(kind)}."),
			_ => throw new ArgumentException("A number value needs a number."),
		},

		RegistryValueKind.MultiString => raw is JsonArray array
			? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray()
			: throw new ArgumentException("A multi-string value needs a list of strings."),

		RegistryValueKind.Binary => raw is JsonArray bytes
			? bytes.Select(item => item is null ? (byte)0 : item.GetValue<byte>()).ToArray()
			: throw new ArgumentException("A binary value needs a list of bytes."),

		_ => raw is null
			? throw new ArgumentException("A text value needs some text.")
			: raw.GetValue<string>(),
	};

	private static (RegistryHive Hive, RegistryView View) ResolveHive(string hive)
	{
		var name = hive.Trim().ToUpperInvariant();

		// The 64-bit view is used throughout, because the 32-bit view is a separate set of keys that a
		// caller reading a path out of regedit on 64-bit Windows would then not be looking at.
		return name switch
		{
			"HKCU" or "HKEY_CURRENT_USER" => (RegistryHive.CurrentUser, RegistryView.Registry64),
			"HKLM" or "HKEY_LOCAL_MACHINE" => (RegistryHive.LocalMachine, RegistryView.Registry64),
			"HKU" or "HKEY_USERS" => (RegistryHive.Users, RegistryView.Registry64),
			"HKCC" or "HKEY_CURRENT_CONFIG" => (RegistryHive.CurrentConfig, RegistryView.Registry64),
			_ => throw new ArgumentException($"'{hive}' is not a registry hive."),
		};
	}

	/// <summary>Reads a value, reporting clearly when the key or the value is simply not there.</summary>
	public sealed class RegistryGetTool : ITool
	{
		public string Name => "registry_get";

		public bool RequiresConfirmation => false;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Reads one registry value. Use HKCU for your own settings and HKLM for machine-wide ones.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["hive"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "HKCU, HKLM, HKU or HKCC.",
						["enum"] = new JsonArray("HKCU", "HKLM", "HKU", "HKCC"),
					},
					["path"] = new JsonObject { ["type"] = "string", ["description"] = "Key path below the hive, such as Software\\Contoso." },
					["name"] = new JsonObject { ["type"] = "string", ["description"] = "Value name. Omit to list every value in the key." },
				},
				["required"] = new JsonArray("hive", "path"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var hiveName = arguments["hive"]?.GetValue<string>();
			var path = arguments["path"]?.GetValue<string>();
			var name = arguments["name"]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(hiveName) || string.IsNullOrWhiteSpace(path))
			{
				return Task.FromResult(ToolOutcome.Failure("A hive and a key path are both needed."));
			}

			try
			{
				var (hive, view) = ResolveHive(hiveName);

				using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);

				if (key is null)
				{
					return Task.FromResult(ToolOutcome.Failure($"{hiveName}\\{path} does not exist."));
				}

				// With no name given, listing what is in the key is the more useful answer than refusing.
				if (string.IsNullOrWhiteSpace(name))
				{
					var names = key.GetValueNames();

					if (names.Length == 0)
					{
						return Task.FromResult(ToolOutcome.Failure($"{hiveName}\\{path} has no values."));
					}

					var listing = new System.Text.StringBuilder();

					foreach (var existing in names)
					{
						listing.Append(existing)
							.Append(" = ")
							.Append(ReadValue(key.GetValue(existing, null, RegistryValueOptions.DoNotExpandEnvironmentNames)))
							.Append('\n');
					}

					return Task.FromResult(ToolOutcome.Success(listing.ToString()));
				}

				var found = key.GetValueNames()
					.FirstOrDefault(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));

				if (found is null)
				{
					return Task.FromResult(ToolOutcome.Failure($"{hiveName}\\{path} has no value called '{name}'."));
				}

				var value = key.GetValue(found, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

				return Task.FromResult(ToolOutcome.Success(
					$"{found} = {ReadValue(value)} ({(value is null ? "not set" : key.GetValueKind(found).ToString())})"));
			}
			catch (ArgumentException exception)
			{
				return Task.FromResult(ToolOutcome.Failure(exception.Message));
			}
			catch (System.Security.SecurityException)
			{
				return Task.FromResult(ToolOutcome.Failure(
					$"{hiveName}\\{path} could not be read: this build of Windows denies the request."));
			}
		}
	}

	/// <summary>Creates or changes one value.</summary>
	public sealed class RegistrySetTool : ITool
	{
		public string Name => "registry_set";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Creates or changes one registry value. Writing under HKLM needs an elevated "
				+ "process, which a plugin does not have.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["hive"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "HKCU, HKLM, HKU or HKCC.",
						["enum"] = new JsonArray("HKCU", "HKLM", "HKU", "HKCC"),
					},
					["path"] = new JsonObject { ["type"] = "string", ["description"] = "Key path below the hive." },
					["name"] = new JsonObject { ["type"] = "string", ["description"] = "Value name." },
					["kind"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "string, expandstring, multistring, binary, dword or qword.",
						["enum"] = new JsonArray("string", "expandstring", "multistring", "binary", "dword", "qword"),
					},
					["value"] = new JsonObject { ["description"] = "The value. A number for dword and qword, a list for multistring and binary." },
					["createKey"] = new JsonObject
					{
						["type"] = "boolean",
						["description"] = "Create the key if it is not there. Default true.",
					},
				},
				["required"] = new JsonArray("hive", "path", "name", "value"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var hiveName = arguments["hive"]?.GetValue<string>();
			var path = arguments["path"]?.GetValue<string>();
			var name = arguments["name"]?.GetValue<string>();
			var create = arguments["createKey"]?.GetValue<bool>() ?? true;

			if (string.IsNullOrWhiteSpace(hiveName) || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name))
			{
				return Task.FromResult(ToolOutcome.Failure("A hive, a key path and a value name are all needed."));
			}

			object value;

			try
			{
				value = Coerce(arguments["kind"]?.GetValue<string>() ?? "string", arguments["value"]);
			}
			catch (ArgumentException exception)
			{
				return Task.FromResult(ToolOutcome.Failure(exception.Message));
			}

			try
			{
				var (hive, view) = ResolveHive(hiveName);
				var root = RegistryKey.OpenBaseKey(hive, view);

				// CreateSubKey creates the key whether or not it was there, even when asked for a
				// read-only handle. That is the wrong behaviour when the caller said not to create, so the
				// key is opened first and only created when it is genuinely absent.
				using var writable = root.OpenSubKey(path, writable: true);

				RegistryKey? key;

				if (writable is not null)
				{
					key = writable;
				}
				else if (create)
				{
					key = root.CreateSubKey(path, writable: true);
				}
				else
				{
					return Task.FromResult(ToolOutcome.Failure(
						$"{hiveName}\\{path} does not exist, and was not created."));
				}

				if (key is null)
				{
					return Task.FromResult(ToolOutcome.Failure(
						$"{hiveName}\\{path} could not be opened for writing."));
				}

				using (key)
				{
					key.SetValue(name, value, ParseKind(arguments["kind"]?.GetValue<string>()));
				}

				return Task.FromResult(ToolOutcome.Success($"Set {hiveName}\\{path}\\{name}."));
			}
			catch (ArgumentOutOfRangeException exception)
			{
				return Task.FromResult(ToolOutcome.Failure(exception.Message.Split('(')[0].Trim()));
			}
			catch (UnauthorizedAccessException)
			{
				// Distinct wording on purpose: this is the answer to "why did it not work", and a model that
				// is told "not permitted" will stop rather than try the same write again.
				return Task.FromResult(ToolOutcome.Failure(
					$"{hiveName}\\{path} could not be written: writing there needs an administrator, "
					+ "and a plugin runs as you."));
			}
			catch (System.Security.SecurityException exception)
			{
				return Task.FromResult(ToolOutcome.Failure($"{hiveName}\\{path} could not be written: {exception.Message}"));
			}
		}
	}

	/// <summary>Removes one value, leaving the key itself alone.</summary>
	public sealed class RegistryDeleteTool : ITool
	{
		public string Name => "registry_delete";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Removes one registry value. The value goes, not the key, unless the key is empty "
				+ "afterwards and deleteKey is true.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["hive"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "HKCU, HKLM, HKU or HKCC.",
						["enum"] = new JsonArray("HKCU", "HKLM", "HKU", "HKCC"),
					},
					["path"] = new JsonObject { ["type"] = "string", ["description"] = "Key path below the hive." },
					["name"] = new JsonObject { ["type"] = "string", ["description"] = "Value name to remove." },
					["deleteKey"] = new JsonObject
					{
						["type"] = "boolean",
						["description"] = "Also remove the key. Default false.",
					},
				},
				["required"] = new JsonArray("hive", "path", "name"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var hiveName = arguments["hive"]?.GetValue<string>();
			var path = arguments["path"]?.GetValue<string>();
			var name = arguments["name"]?.GetValue<string>();
			var deleteKey = arguments["deleteKey"]?.GetValue<bool>() == true;

			if (string.IsNullOrWhiteSpace(hiveName) || string.IsNullOrWhiteSpace(path))
			{
				return Task.FromResult(ToolOutcome.Failure("A hive and a key path are both needed."));
			}

			if (string.IsNullOrWhiteSpace(name) && !deleteKey)
			{
				// Deleting a whole key is never inferred from a missing name, because "which key?" and
				// "delete all of it" are very different requests.
				return Task.FromResult(ToolOutcome.Failure("Name the value to remove, or pass deleteKey."));
			}

			try
			{
				var (hive, view) = ResolveHive(hiveName);

				if (deleteKey)
				{
					RegistryKey.OpenBaseKey(hive, view).DeleteSubKeyTree(path, throwOnMissingSubKey: false);
					return Task.FromResult(ToolOutcome.Success($"Deleted {hiveName}\\{path} and anything under it."));
				}

				using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path, writable: true);

				if (key is null)
				{
					return Task.FromResult(ToolOutcome.Failure($"{hiveName}\\{path} does not exist."));
				}

				var existing = key.GetValueNames()
					.FirstOrDefault(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));

				if (existing is null)
				{
					return Task.FromResult(ToolOutcome.Failure($"{hiveName}\\{path} has no value called '{name}'."));
				}

				key.DeleteValue(existing, throwOnMissingValue: false);

				return Task.FromResult(ToolOutcome.Success($"Deleted {hiveName}\\{path}\\{existing}."));
			}
			catch (ArgumentException exception)
			{
				return Task.FromResult(ToolOutcome.Failure(exception.Message));
			}
			catch (UnauthorizedAccessException)
			{
				return Task.FromResult(ToolOutcome.Failure(
					$"{hiveName}\\{path} could not be written: that needs an administrator."));
			}
			catch (System.Security.SecurityException exception)
			{
				return Task.FromResult(ToolOutcome.Failure($"{hiveName}\\{path} could not be changed: {exception.Message}"));
			}
		}
	}
}
