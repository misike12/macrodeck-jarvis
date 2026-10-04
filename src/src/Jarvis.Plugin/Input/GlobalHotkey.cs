using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace Jarvis.Plugin.Input;

/// <summary>The modifier keys a chord can use.</summary>
[Flags]
public enum ModifierKeys
{
	None = 0,
	Alt = 1,
	Control = 2,
	Shift = 4,
	Win = 8,
}

/// <summary>
/// A global hotkey: a chord that works while another application owns the keyboard focus.
/// <para>
/// Registered with a null window handle, which binds the chord to this plugin's own thread, and delivered
/// by pumping WM_HOTKEY on a dedicated thread. A dedicated thread matters because a turn can hold a worker
/// for seconds, and a hotkey that stops answering during an answer is not a hotkey.
/// </para>
/// <para>
/// Registration can fail because another program already owns the chord, which is common and is not the
/// plugin's fault. That is reported rather than swallowed: a hotkey that silently does nothing looks
/// exactly like a broken plugin.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GlobalHotkey : IDisposable
{
	private const uint WmHotkey = 0x0312;

	/// <summary>ERROR_HOTKEY_ALREADY_REGISTERED, the only failure a user can actually do something about.</summary>
	private const int ErrorHotkeyAlreadyRegistered = 1409;

	private readonly ILogger _logger;
	private readonly ManualResetEventSlim _ready = new(false);
	private Thread? _pump;
	private int _pumpThreadId;
	private int _pumpStarted;

	private HotkeyChord? _chord;
	private int _registrationId;
	private volatile bool _running = true;

	public GlobalHotkey(ILogger logger)
	{
		_logger = logger.ForContext<GlobalHotkey>();
	}

	/// <summary>
	/// Starts the message-pump thread, once.
	/// <para>
	/// Deliberately not in the constructor. The host constructs every integration and handler as part of
	/// <c>Build()</c> validation, so a thread started there is a thread started during a configuration check
	/// on a graph that may be discarded, and it belongs to an object nobody asked for.
	/// </para>
	/// </summary>
	private void EnsurePump()
	{
		if (Interlocked.Exchange(ref _pumpStarted, 1) != 0)
		{
			return;
		}

		_pump = new Thread(Pump) { IsBackground = true, Name = "JARVIS hotkey" };
		_pumpThreadId = _pump.ManagedThreadId;
		_pump.Start();
	}

	/// <summary>The chord currently registered, or null when nothing is.</summary>
	public HotkeyChord? Active => _chord;

	/// <summary>Why the last registration failed, for a user to act on.</summary>
	public string? LastError { get; private set; }

	/// <summary>Raised on the pump thread when the chord is pressed.</summary>
	public event Action? Pressed;

	/// <summary>
	/// Registers a chord, replacing any previous one. Returns false when the key is already taken.
	/// </summary>
	public bool Register(HotkeyChord chord)
	{
		Unregister();

		EnsurePump();

		if (!_ready.Wait(TimeSpan.FromSeconds(5)))
		{
			LastError = "The hotkey listener did not start.";
			return false;
		}

		var id = Interlocked.Increment(ref _nextId);

		if (!NativeMethods.RegisterHotKey(0, id, ToNativeModifiers(chord.Modifiers), (uint)chord.Key))
		{
			LastError = NativeMethods.GetLastError() == ErrorHotkeyAlreadyRegistered
				? "That key combination is already used by another program."
				: "That key combination could not be registered.";

			_logger.Warning("{Chord} could not be registered: {Reason}", chord, LastError);
			return false;
		}

		_chord = chord;
		_registrationId = id;
		LastError = null;

		_logger.Information("Global hotkey {Chord} registered.", chord);
		return true;
	}

	public void Unregister()
	{
		var id = _registrationId;

		if (id == 0)
		{
			return;
		}

		NativeMethods.UnregisterHotKey(0, id);
		_registrationId = 0;
		_chord = null;
	}

	private static int _nextId;

	private static uint ToNativeModifiers(ModifierKeys modifiers)
	{
		uint native = 0;

		if (modifiers.HasFlag(ModifierKeys.Alt))
		{
			native |= 0x0001;
		}

		if (modifiers.HasFlag(ModifierKeys.Control))
		{
			native |= 0x0002;
		}

		if (modifiers.HasFlag(ModifierKeys.Shift))
		{
			native |= 0x0004;
		}

		if (modifiers.HasFlag(ModifierKeys.Win))
		{
			native |= 0x0008;
		}

		return native;
	}

	private void Pump()
	{
		_ready.Set();

		var message = default(NativeMethods.Message);

		while (_running
			&& NativeMethods.GetMessage(ref message, 0, 0, 0) > 0)
		{
			if (message.Value == WmHotkey && (int)message.WParam == _registrationId)
			{
				try
				{
					Pressed?.Invoke();
				}
				catch (Exception exception) when (exception is not OutOfMemoryException)
				{
					// A handler that throws here would unwind the pump and kill the hotkey silently, so it is
					// contained here rather than allowed to escape.
					_logger.Warning(exception, "A hotkey handler failed.");
				}
			}
		}
	}

	public void Dispose()
	{
		_running = false;
		Unregister();

		// Nothing to wake or join if the pump was never started, which is the normal case for a plugin whose
		// hotkey was never configured.
		if (_pump is { } pump)
		{
			// Posted to the pump's own thread, not to whichever thread happens to be disposing. The old code
			// used the current thread's id, so the wake went nowhere and the join waited out its full two
			// seconds on every shutdown.
			NativeMethods.PostThreadMessage((uint)_pumpThreadId, 0, 0, 0);
			pump.Join(TimeSpan.FromSeconds(2));
		}

		_ready.Dispose();
		GC.SuppressFinalize(this);
	}

	private static class NativeMethods
	{
		[StructLayout(LayoutKind.Sequential)]
		internal struct Message
		{
			public nint Window;
			public uint Value;
			public nint WParam;
			public nint LParam;
			public uint Time;
			public nint Point;
		}

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool UnregisterHotKey(nint window, int id);

		[DllImport("user32.dll")]
		internal static extern int GetMessage(ref Message message, nint window, uint min, uint max);

		[DllImport("user32.dll", SetLastError = true)]
		internal static extern bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);

		[DllImport("kernel32.dll")]
		internal static extern uint GetLastError();
	}
}