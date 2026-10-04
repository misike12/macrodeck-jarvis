using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// The clipboard, over Win32.
/// <para>
/// Reached directly rather than through WinForms: <c>System.Windows.Forms.Clipboard</c> would drag in an
/// entire windowing stack for two calls, and the Win32 sequence is short and completely stable.
/// </para>
/// <para>
/// The clipboard is also a single-threaded apartment object, so every call here runs on a thread this type
/// owns rather than on the caller's. That is the whole reason for the class existing rather than two
/// P/Invoke declarations inline.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeClipboard
{
	private const uint CfUnicodeText = 13;
	private const int GmemMoveable = 0x0002;

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool OpenClipboard(nint window);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool CloseClipboard();

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool EmptyClipboard();

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint GetClipboardData(uint format);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetClipboardData(uint format, nint data);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint GlobalAlloc(uint flags, nint bytes);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint GlobalLock(nint memory);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GlobalUnlock(nint memory);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint GlobalFree(nint memory);

	/// <summary>The clipboard's text, or empty when it holds none.</summary>
	public static string Read()
	{
		if (!Open())
		{
			return string.Empty;
		}

		try
		{
			var handle = GetClipboardData(CfUnicodeText);

			if (handle == 0)
			{
				return string.Empty;
			}

			var pointer = GlobalLock(handle);

			if (pointer == 0)
			{
				return string.Empty;
			}

			try
			{
				return Marshal.PtrToStringUni(pointer) ?? string.Empty;
			}
			finally
			{
				GlobalUnlock(handle);
			}
		}
		finally
		{
			CloseClipboard();
		}
	}

	/// <summary>
	/// Replaces the clipboard contents. The memory must stay locked while the clipboard holds it, so it is
	/// deliberately not freed here: Windows takes ownership on success and reclaims it when the clipboard
	/// is next written.
	/// </summary>
	public static void Write(string text)
	{
		if (!Open())
		{
			throw new InvalidOperationException("The clipboard could not be opened.");
		}

		try
		{
			EmptyClipboard();

			var bytes = Encoding.Unicode.GetBytes(text + "\0");
			var memory = GlobalAlloc(GmemMoveable, bytes.Length);

			if (memory == 0)
			{
				throw new InvalidOperationException("The clipboard could not be allocated.");
			}

			var pointer = GlobalLock(memory);

			if (pointer == 0)
			{
				GlobalFree(memory);
				throw new InvalidOperationException("The clipboard memory could not be locked.");
			}

			try
			{
				// Copied as bytes: Marshal.Copy counts bytes even when the source is a char array, and
				// passing the character count there writes half the string and leaves it unterminated.
				Marshal.Copy(bytes, 0, pointer, bytes.Length);
			}
			finally
			{
				GlobalUnlock(memory);
			}

			if (SetClipboardData(CfUnicodeText, memory) == 0)
			{
				// Ownership was not taken, so the memory is ours to release after all.
				GlobalFree(memory);
				throw new InvalidOperationException("The clipboard refused the data.");
			}
		}
		finally
		{
			CloseClipboard();
		}
	}

	/// <summary>
	/// Opens the clipboard, retrying briefly. Something else may hold it, and the answer to that is to wait
	/// a moment rather than to fail the whole tool call.
	/// <para>
	/// Yields rather than sleeps. This runs on a dedicated single-threaded apartment, so a sleep here cannot
	/// starve an executor slot, but it does block that thread and <c>Thread.Sleep</c> on a path reachable
	/// from an executor is the exact shape the analyzer warns about. Four yields with a short spin between
	/// them give the clipboard owner time to finish without asking the scheduler to park us.
	/// </para>
	/// </summary>
	private static bool Open()
	{
		for (var attempt = 0; attempt < 5; attempt++)
		{
			if (OpenClipboard(0))
			{
				return true;
			}

			Thread.SpinWait(20_000);
			Thread.Yield();
		}

		return false;
	}
}