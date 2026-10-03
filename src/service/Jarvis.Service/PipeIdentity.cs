using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Jarvis.Service;

/// <summary>
/// Who the pipe is for.
/// <para>
/// The service runs as LocalSystem, so <see cref="Environment.UserName"/> is "SYSTEM" and a pipe named after
/// it is one the plugin, running as the person at the keyboard, cannot reach. The pipe is therefore named
/// after the signed-in user's security identifier, and the descriptor on it grants that user access.
/// </para>
/// <para>
/// This is also why the pipe cannot use <see cref="System.IO.Pipes.PipeOptions.CurrentUserOnly"/>. That flag
/// restricts a pipe to whoever created it, which here is the service itself, so it would lock the plugin out
/// by construction rather than by accident.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PipeIdentity
{
	/// <summary>The administrators group, which is the only thing that should be able to replace the pipe.</summary>
	private const string AdministratorsSid = "BA";

	/// <summary>The local system account, which owns the pipe.</summary>
	private const string SystemSid = "SY";

	private const int TokenInformationClassUser = 1;

	private const int SessionStateActive = 0;

	/// <summary>
	/// The security identifier of the user with an interactive session, or null when there is none.
	/// <para>
	/// Found by enumerating sessions rather than by asking for the console session. The console-session
	/// entry point is not exported on client Windows, only on Server, so a service that used it would work
	/// on a machine nobody runs an assistant on and fail on every machine that does.
	/// </para>
	/// </summary>
	public static string? InteractiveUserSid()
	{
		// A process that is not the service is the user. Asking the session list for a token it does not
		// have the rights to read would fail, and its own identity is the right answer anyway.
		using var identity = WindowsIdentity.GetCurrent();

		if (!identity.IsSystem)
		{
			return identity.User?.Value;
		}

		return ActiveUserSid();
	}

	private static string? ActiveUserSid()
	{
		if (!TryEnumerateSessions(out var sessions, out var count))
		{
			return null;
		}

		try
		{
			var size = Marshal.SizeOf<WtsSessionInfo>();

			for (var index = 0; index < count; index++)
			{
				var info = Marshal.PtrToStructure<WtsSessionInfo>(sessions + (index * size));

				// A disconnected session has nobody at the keyboard, so its pipe would never be opened.
				if (info.State != SessionStateActive)
				{
					continue;
				}

				var sid = UserSidForSession(info.SessionId);

				if (sid is not null)
				{
					return sid;
				}
			}

			return null;
		}
		finally
		{
			if (sessions != IntPtr.Zero)
			{
				WTSFreeMemory(sessions);
			}
		}
	}

	/// <summary>
	/// Wrapped because a missing entry point throws rather than returning a failure code, and this runs on
	/// the path that decides whether the pipe can be reached at all.
	/// </summary>
	private static bool TryEnumerateSessions(out IntPtr sessions, out uint count)
	{
		sessions = IntPtr.Zero;
		count = 0;

		try
		{
			return WTSEnumerateSessions(WtsCurrentServerHandle, 0, 1, out sessions, out count);
		}
		catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
		{
			ServiceLog.Warning("The session list could not be read: " + exception.Message);
			sessions = IntPtr.Zero;
			count = 0;
			return false;
		}
	}

	/// <summary>
	/// The security identifier behind a session, or null when it cannot be read.
	/// <para>
	/// Every failure here is a null rather than an exception. This runs while the service is starting, so
	/// anything it throws stops the service from starting at all, which is a far worse answer than a pipe
	/// named for LocalSystem that the plugin reports it cannot reach.
	/// </para>
	/// </summary>
	private static string? UserSidForSession(uint session)
	{
		try
		{
			return ReadUserSidForSession(session);
		}
		catch (Exception exception) when (
			exception is EntryPointNotFoundException or DllNotFoundException or System.ComponentModel.Win32Exception)
		{
			ServiceLog.Warning($"Session {session} has no readable user: " + exception.Message);
			return null;
		}
	}

	private static string? ReadUserSidForSession(uint session)
	{
		if (!WTSQueryUserToken(session, out var token) || token == IntPtr.Zero)
		{
			return null;
		}

		try
		{
			if (!GetTokenInformation(token, TokenInformationClassUser, IntPtr.Zero, 0, out var needed)
				|| needed == 0)
			{
				return null;
			}

			var buffer = Marshal.AllocHGlobal(needed);

			try
			{
				if (!GetTokenInformation(token, TokenInformationClassUser, buffer, needed, out _))
				{
					return null;
				}

				// TOKEN_USER is a SID_AND_ATTRIBUTES, so the identifier is a pointer inside this block.
				var user = Marshal.PtrToStructure<TokenUser>(buffer);

				return user.Sid.ToString();
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
			}
		}
		finally
		{
			_ = CloseHandle(token);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct TokenUser
	{
		public IntPtr Sid;
		public uint Attributes;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WtsSessionInfo
	{
		public uint SessionId;
		public IntPtr WinStationName;
		public uint State;
	}

	private static IntPtr WtsCurrentServerHandle => IntPtr.Zero;

	[DllImport("wtsapi32.dll", SetLastError = true)]
	private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

	[DllImport("wtsapi32.dll", SetLastError = true)]
	private static extern bool WTSEnumerateSessions(
		IntPtr server, uint reserved, uint version, out IntPtr sessions, out uint count);

	[DllImport("wtsapi32.dll")]
	private static extern void WTSFreeMemory(IntPtr memory);

	// advapi32, not kernel32. Named here because declaring it against kernel32 compiles, runs fine in tray mode
	// where the early return above skips it, and then throws EntryPointNotFound inside the service, where the
	// only symptom was the service failing to start.
	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool GetTokenInformation(
		IntPtr token, int informationClass, IntPtr information, int length, out int needed);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);

	/// <summary>
	/// The security descriptor string for the pipe.
	/// <para>
	/// Read and write for the signed-in user, full control for LocalSystem and administrators, and nothing
	/// more. This pipe is the request channel to a service that runs as LocalSystem, so the set of processes
	/// that can open it is the trust boundary. Granting it to "everyone authenticated" would give every
	/// process on the machine a line into it.
	/// </para>
	/// </summary>
	public static string BuildSddl(string? userSid)
	{
		var user = string.IsNullOrWhiteSpace(userSid) ? SystemSid : userSid;

		return $"O:{SystemSid}G:{AdministratorsSid}D:(A;FA;;;{SystemSid})(A;FA;;;{AdministratorsSid})(A;GRGW;;;{user})";
	}

	/// <summary>
	/// The descriptor to use outside the elevated service, which is the tray mode. There the creating user
	/// already has access, so it only has to keep other users out.
	/// </summary>
	public static string BuildUserSddl()
	{
		using var identity = WindowsIdentity.GetCurrent();

		return BuildSddl(identity.User?.Value);
	}
}