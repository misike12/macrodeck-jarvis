using Microsoft.Win32;

namespace Jarvis.Service;

/// <summary>
/// The one preference the tray icon owns, kept per user.
/// <para>
/// Per user rather than per machine because that is the only place a tray-mode process can write without
/// help: the tray runs as the signed-in user, while the machine-wide state directory is deliberately
/// writable only by SYSTEM and administrators. A setting the user can change has to be storable by the user
/// who changed it, or the checkbox silently reverts on the next launch.
/// </para>
/// </summary>
public static class ServicePreferences
{
	private const string KeyPath = @"Software\Jarvis\Service";

	private const string ValueName = "AllowAdminOperations";

	/// <summary>
	/// Whether machine-changing operations are permitted. True when nothing has been recorded, so a fresh
	/// install behaves as before rather than refusing everything until someone finds the menu.
	/// </summary>
	public static bool AllowAdminOperations
	{
		get => Read() ?? true;
		set => Write(value);
	}

	private static bool? Read()
	{
		try
		{
			using var key = Registry.CurrentUser.OpenSubKey(KeyPath);

			return key?.GetValue(ValueName) switch
			{
				int stored => stored != 0,
				string text when bool.TryParse(text, out var parsed) => parsed,
				_ => null,
			};
		}
		catch (Exception exception) when (
			exception is System.Security.SecurityException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static bool Write(bool value)
	{
		try
		{
			using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);

			key.SetValue(ValueName, value ? 1 : 0, RegistryValueKind.DWord);

			return true;
		}
		catch (Exception exception) when (
			exception is System.Security.SecurityException or UnauthorizedAccessException)
		{
			return false;
		}
	}
}