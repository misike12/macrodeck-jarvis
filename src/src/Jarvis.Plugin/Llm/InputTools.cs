using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;

namespace Jarvis.Plugin.Llm;

/// <summary>
/// Mouse and keyboard control.
/// <para>
/// Input is built and delivered with <c>SendInput</c>. That is the supported path for synthesising input
/// from user mode, it is what accessibility tooling uses, and it delivers to whatever window the desktop
/// considers focused. A kernel-mode driver would need to be signed, installed and trusted, would survive
/// nothing about the session being torn down cleanly, and is the sort of thing anti-cheat and endpoint
/// security exist to detect. It buys nothing here.
/// </para>
/// <para>
/// The one real hazard with synthesised input is a stuck modifier: if a key-down is sent and the matching
/// key-up is lost, every later keystroke is silently wrong. <see cref="ReleaseAllModifiers"/> exists so the
/// tools can never leave one down.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class InputTools
{
	private const uint InputMouse = 0;
	private const uint InputKeyboard = 1;

	private const uint MouseMove = 0x0001;
	private const uint MouseAbsolute = 0x8000;
	private const uint MouseLeftDown = 0x0002;
	private const uint MouseLeftUp = 0x0004;
	private const uint MouseRightDown = 0x0008;
	private const uint MouseRightUp = 0x0010;
	private const uint MouseMiddleDown = 0x0020;
	private const uint MouseMiddleUp = 0x0040;
	private const uint MouseWheel = 0x0800;
	private const uint MouseHorizontalWheel = 0x01000;

	private const uint KeyExtended = 0x0001;
	private const uint KeyUp = 0x0002;
	private const uint KeyUnicode = 0x0004;

	private const uint MouseVirtualDesk = 0x4000;

	private const int SmCxScreen = 0;
	private const int SmCyScreen = 1;

	private const int SmXVirtualScreen = 76;
	private const int SmYVirtualScreen = 77;
	private const int SmCxVirtualScreen = 78;
	private const int SmCyVirtualScreen = 79;

	private const uint MapVkToVsc = 0;

	private static readonly nint NoExtraInfo = 0;

	[StructLayout(LayoutKind.Sequential)]
	private struct Input
	{
		public uint Type;
		public InputUnion Data;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct InputUnion
	{
		[FieldOffset(0)] public MouseInput Mouse;
		[FieldOffset(0)] public KeyboardInput Keyboard;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MouseInput
	{
		public int X;
		public int Y;
		public uint Data;
		public uint Flags;
		public uint Time;
		public nint ExtraInfo;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct KeyboardInput
	{
		public ushort VirtualKey;
		public ushort ScanCode;
		public uint Flags;
		public uint Time;
		public nint ExtraInfo;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint count, Input[] inputs, int size);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetCursorPos(out NativePoint point);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint MapVirtualKey(uint code, uint mapType);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint
	{
		public int X;
		public int Y;
	}

	/// <summary>
	/// Delivers a batch and reports how many structures the desktop accepted. A short count means the rest
	/// was refused, which is the case where a modifier key-down would be left without its key-up.
	/// </summary>
	private static int Send(Input[] inputs) =>
		(int)SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

	private static Input Mouse(uint flags, int x, int y, uint data = 0) => new()
	{
		Type = InputMouse,
		Data = new InputUnion
		{
			Mouse = new MouseInput { X = x, Y = y, Data = data, Flags = flags, ExtraInfo = NoExtraInfo },
		},
	};

	/// <summary>
	/// The whole desktop, which is not the same as the primary monitor and can have a negative origin.
	/// </summary>
	private static (int X, int Y, int Width, int Height) VirtualDesktop =>
	(
		GetSystemMetrics(SmXVirtualScreen),
		GetSystemMetrics(SmYVirtualScreen),
		GetSystemMetrics(SmCxVirtualScreen),
		GetSystemMetrics(SmCyVirtualScreen));

	/// <summary>
	/// Absolute coordinates are 0 to 65535 rather than pixels, because that is what SendInput's absolute
	/// mode means, and they are scaled over the whole virtual desktop rather than the primary monitor.
	/// <para>
	/// A second monitor placed above or to the left gives the virtual desktop a negative origin, so the
	/// origin has to be added before scaling and the result clamped to the virtual rectangle. Mapping over
	/// the primary monitor instead puts the pointer in the wrong place on any multi-monitor desktop that is
	/// not an arrangement starting at zero, which is most of them.
	/// </para>
	/// </summary>
	private static (int X, int Y) ToAbsolute(int pixelsX, int pixelsY)
	{
		var (left, top, width, height) = VirtualDesktop;

		if (width <= 0 || height <= 0)
		{
			// No desktop metrics at all, which happens in a session with no display attached.
			(width, height) = (GetSystemMetrics(SmCxScreen), GetSystemMetrics(SmCyScreen));
			(left, top) = (0, 0);
		}

		var x = ((pixelsX - left) * 65535L) / Math.Max(1, width - 1);
		var y = ((pixelsY - top) * 65535L) / Math.Max(1, height - 1);

		return (
			(int)Math.Clamp(x, 0, 65535),
			(int)Math.Clamp(y, 0, 65535));
	}

	private static readonly Dictionary<string, ushort> VirtualKeys = new(StringComparer.OrdinalIgnoreCase)
	{
		["enter"] = 0x0D,
		["return"] = 0x0D,
		["tab"] = 0x09,
		["escape"] = 0x1B,
		["esc"] = 0x1B,
		["space"] = 0x20,
		["backspace"] = 0x08,
		["delete"] = 0x2E,
		["insert"] = 0x2D,
		["home"] = 0x24,
		["end"] = 0x23,
		["pageup"] = 0x21,
		["pagedown"] = 0x22,
		["up"] = 0x26,
		["down"] = 0x28,
		["left"] = 0x25,
		["right"] = 0x27,
		["shift"] = 0x10,
		["ctrl"] = 0x11,
		["control"] = 0x11,
		["alt"] = 0x12,
		["win"] = 0x5B,
		["meta"] = 0x5B,
		["windows"] = 0x5B,
		["printscreen"] = 0x2C,
		["capslock"] = 0x14,
		["f1"] = 0x70,
		["f2"] = 0x71,
		["f3"] = 0x72,
		["f4"] = 0x73,
		["f5"] = 0x74,
		["f6"] = 0x75,
		["f7"] = 0x76,
		["f8"] = 0x77,
		["f9"] = 0x78,
		["f10"] = 0x79,
		["f11"] = 0x7A,
		["f12"] = 0x7B,
	};

	private static readonly string[] ModifierNames = ["shift", "ctrl", "alt", "win"];

	/// <summary>Keys that only work as a combination, so sending them alone would be meaningless.</summary>
	private static readonly HashSet<string> ModifierSet =
		new(ModifierNames, StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Releases every modifier. Called after every keyboard tool and before every mouse tool that involves
	/// a click, because a stuck Ctrl turns a left click into a right-click somewhere else entirely.
	/// </summary>
	private static void ReleaseAllModifiers()
	{
		foreach (var name in ModifierNames)
		{
			var key = VirtualKeys[name];
			Send([Key(key, KeyExtended | KeyUp), Key(key, KeyUp)]);
		}
	}

	private static Input Key(ushort virtualKey, uint flags = 0) => new()
	{
		Type = InputKeyboard,
		Data = new InputUnion
		{
			Keyboard = new KeyboardInput
			{
				VirtualKey = virtualKey,
				ScanCode = (ushort)MapVirtualKey(virtualKey, MapVkToVsc),
				Flags = flags,
				ExtraInfo = NoExtraInfo,
			},
		},
	};

	/// <summary>
	/// A named key press, as one down and one up. Extended is set for keys that live on the extended part of
	/// the keyboard, because without it the arrow keys and the numpad keys arrive as their numpad twins.
	/// </summary>
	private static bool PressNamed(string name)
	{
		if (!VirtualKeys.TryGetValue(name, out var virtualKey))
		{
			return false;
		}

		const uint ExtendedKeys =
			0x21 | 0x22 | 0x23 | 0x24 | 0x25 | 0x26 | 0x27 | 0x28 | 0x2D | 0x2E | 0x5B | 0x5C;

		var flags = (uint)(virtualKey & ExtendedKeys);

		Send([Key(virtualKey, flags), Key(virtualKey, flags | KeyUp)]);
		return true;
	}

	internal static (int X, int Y, int Width, int Height) VirtualDesktopForTest() => VirtualDesktop;

	internal static (int X, int Y) ToAbsoluteForTest(int pixelsX, int pixelsY) => ToAbsolute(pixelsX, pixelsY);

	/// <summary>Reads the current cursor position, for reporting rather than for acting.</summary>
	internal static bool TryGetCursorPosition(out int x, out int y)
	{
		if (GetCursorPos(out var point))
		{
			x = point.X;
			y = point.Y;
			return true;
		}

		x = 0;
		y = 0;
		return false;
	}

	/// <summary>Moves the pointer, and reports where it ended up.</summary>
	public sealed class MouseMoveTool : ITool
	{
		public string Name => "mouse_move";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Moves the mouse pointer. Give absolute pixel coordinates, or a single "
				+ "direction word to nudge it.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["x"] = new JsonObject { ["type"] = "integer", ["description"] = "Horizontal position in pixels." },
					["y"] = new JsonObject { ["type"] = "integer", ["description"] = "Vertical position in pixels." },
					["direction"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "up, down, left or right, to nudge by a step instead.",
						["enum"] = new JsonArray("up", "down", "left", "right"),
					},
					["step"] = new JsonObject { ["type"] = "integer", ["description"] = "Pixels to nudge. Default 40." },
				},
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var x = arguments["x"]?.GetValue<int?>();
			var y = arguments["y"]?.GetValue<int?>();
			var direction = arguments["direction"]?.GetValue<string>()?.Trim().ToLowerInvariant();
			var step = arguments["step"]?.GetValue<int?>() ?? 40;

			if (!TryGetCursorPosition(out var currentX, out var currentY))
			{
				return Task.FromResult(ToolOutcome.Failure("The mouse position could not be read."));
			}

			var targetX = x ?? currentX;
			var targetY = y ?? currentY;

			if (direction is not null)
			{
				switch (direction)
				{
					case "up": targetY -= step; break;
					case "down": targetY += step; break;
					case "left": targetX -= step; break;
					case "right": targetX += step; break;
					default:
						return Task.FromResult(ToolOutcome.Failure(
							"The direction must be up, down, left or right."));
				}
			}
			else if (x is null && y is null)
			{
				return Task.FromResult(ToolOutcome.Failure("Give a position or a direction to move towards."));
			}

			var (absoluteX, absoluteY) = ToAbsolute(targetX, targetY);
			Send([Mouse(MouseMove | MouseAbsolute | MouseVirtualDesk, absoluteX, absoluteY)]);

			// Read back rather than reporting the requested position: the desktop clamps, and a tool that
			// says it moved somewhere it did not is worse than one that admits the clamp.
			return TryGetCursorPosition(out var landedX, out var landedY)
				? Task.FromResult(ToolOutcome.Success($"Mouse is at {landedX}, {landedY}."))
				: Task.FromResult(ToolOutcome.Success($"Moved to {targetX}, {targetY}."));
		}
	}

	/// <summary>Clicks at a position, or where the pointer already is.</summary>
	public sealed class MouseClickTool : ITool
	{
		public string Name => "mouse_click";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Clicks the mouse, optionally moving to a position first. The button is left, "
				+ "right, middle or double.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["x"] = new JsonObject { ["type"] = "integer", ["description"] = "Position to move to first." },
					["y"] = new JsonObject { ["type"] = "integer", ["description"] = "Position to move to first." },
					["button"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "left, right, middle or double. Default left.",
						["enum"] = new JsonArray("left", "right", "middle", "double"),
					},
				},
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var x = arguments["x"]?.GetValue<int?>();
			var y = arguments["y"]?.GetValue<int?>();
			var button = arguments["button"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "left";

			var (down, up) = button switch
			{
				"left" => (MouseLeftDown, MouseLeftUp),
				"right" => (MouseRightDown, MouseRightUp),
				"middle" => (MouseMiddleDown, MouseMiddleUp),
				"double" => (MouseLeftDown, MouseLeftUp),
				_ => (0u, 0u),
			};

			if (down == 0)
			{
				return Task.FromResult(ToolOutcome.Failure(
					"The button must be left, right, middle or double."));
			}

			// A stuck modifier turns this click into a different click entirely, so the modifiers go first.
			ReleaseAllModifiers();

			if (x is not null && y is not null)
			{
				var (absoluteX, absoluteY) = ToAbsolute(x.Value, y.Value);
				Send([Mouse(MouseMove | MouseAbsolute | MouseVirtualDesk, absoluteX, absoluteY)]);
			}

			Send([Mouse(down, 0, 0), Mouse(up, 0, 0)]);

			if (button == "double")
			{
				Send([Mouse(down, 0, 0), Mouse(up, 0, 0)]);
			}

			return Task.FromResult(ToolOutcome.Success(
				button == "double" ? "Double clicked." : $"Clicked {button}."));
		}
	}

	/// <summary>Scrolls the wheel.</summary>
	public sealed class MouseScrollTool : ITool
	{
		public string Name => "mouse_scroll";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Scrolls the wheel. Positive clicks scroll up, negative scroll down.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["clicks"] = new JsonObject { ["type"] = "integer", ["description"] = "Wheel clicks, positive or negative." },
					["horizontal"] = new JsonObject
					{
						["type"] = "boolean",
						["description"] = "Scroll sideways instead of up and down.",
					},
				},
				["required"] = new JsonArray("clicks"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var clicks = arguments["clicks"]?.GetValue<int?>();
			var horizontal = arguments["horizontal"]?.GetValue<bool>() == true;

			if (clicks is not { } value || value == 0)
			{
				return Task.FromResult(ToolOutcome.Failure("Give a non-zero number of clicks to scroll."));
			}

			// WHEEL_DELTA is 120 per notch. The conversion is done here so a caller asking for 3 gets exactly
			// three notches rather than three units of three hundredths of a notch.
			const int WheelDelta = 120;
			const int MaximumClicks = 100;

			var clamped = Math.Clamp(value, -MaximumClicks, MaximumClicks);
			var data = unchecked((uint)(clamped * WheelDelta));
			var flags = horizontal ? MouseHorizontalWheel : MouseWheel;

			Send([Mouse(flags, 0, 0, data)]);

			return Task.FromResult(ToolOutcome.Success(
				$"Scrolled {clicks} {(clicks == 1 ? "click" : "clicks")} {(horizontal ? "sideways" : "vertically")}."));
		}
	}

	/// <summary>Presses, drags, and releases.</summary>
	public sealed class MouseDragTool : ITool
	{
		public string Name => "mouse_drag";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Drags with the mouse held down, from one point to another. The movement is "
				+ "staged in small steps, which is what drag and drop actually needs.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["fromX"] = new JsonObject { ["type"] = "integer", ["description"] = "Where the drag starts." },
					["fromY"] = new JsonObject { ["type"] = "integer", ["description"] = "Where the drag starts." },
					["toX"] = new JsonObject { ["type"] = "integer", ["description"] = "Where the drag ends." },
					["toY"] = new JsonObject { ["type"] = "integer", ["description"] = "Where the drag ends." },
				},
				["required"] = new JsonArray("fromX", "fromY", "toX", "toY"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var fromX = arguments["fromX"]?.GetValue<int?>();
			var fromY = arguments["fromY"]?.GetValue<int?>();
			var toX = arguments["toX"]?.GetValue<int?>();
			var toY = arguments["toY"]?.GetValue<int?>();

			if (fromX is null || fromY is null || toX is null || toY is null)
			{
				return Task.FromResult(ToolOutcome.Failure("A drag needs a start and an end position."));
			}

			ReleaseAllModifiers();

			const int Step = 8;
			const int MinimumSteps = 4;

			var (startX, startY) = ToAbsolute(fromX.Value, fromY.Value);
			Send([Mouse(MouseMove | MouseAbsolute | MouseVirtualDesk, startX, startY), Mouse(MouseLeftDown, 0, 0)]);

			// A drag sent as a single jump is usually ignored: applications track the pointer and only start
			// a drag once they have seen it move while the button is down.
			var distance = Math.Max(Math.Abs(toX.Value - fromX.Value), Math.Abs(toY.Value - fromY.Value));
			var steps = Math.Clamp(distance / Step, MinimumSteps, 60);

			for (var step = 1; step <= steps; step++)
			{
				cancellationToken.ThrowIfCancellationRequested();

				var progress = step / (double)steps;
				var (moveX, moveY) = ToAbsolute(
					(int)Math.Round(fromX.Value + (toX.Value - fromX.Value) * progress),
					(int)Math.Round(fromY.Value + (toY.Value - fromY.Value) * progress));

				Send([Mouse(MouseMove | MouseAbsolute | MouseVirtualDesk, moveX, moveY)]);
			}

			Send([Mouse(MouseLeftUp, 0, 0)]);

			return Task.FromResult(ToolOutcome.Success(
				$"Dragged from {fromX}, {fromY} to {toX}, {toY}."));
		}
	}

	/// <summary>Types text as if it were typed.</summary>
	public sealed class KeyboardTypeTool : ITool
	{
		/// <summary>
		/// Bounded, because typing is a loop over characters and a model that asked for a million would
		/// otherwise be waiting a very long time for a very long string.
		/// </summary>
		private const int MaximumCharacters = 8_000;

		public string Name => "keyboard_type";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Types text into whatever window is focused, as keystrokes rather than by "
				+ "pasting, so the target sees the same events a person would produce.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["text"] = new JsonObject { ["type"] = "string", ["description"] = "The text to type." },
				},
				["required"] = new JsonArray("text"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			var text = arguments["text"]?.GetValue<string>();

			if (string.IsNullOrEmpty(text))
			{
				return Task.FromResult(ToolOutcome.Failure("There was no text to type."));
			}

			if (text.Length > MaximumCharacters)
			{
				return Task.FromResult(ToolOutcome.Failure(
					$"That is {text.Length} characters and the limit is {MaximumCharacters}."));
			}

			ReleaseAllModifiers();

			foreach (var character in text)
			{
				cancellationToken.ThrowIfCancellationRequested();
				TypeCharacter(character);
			}

			return Task.FromResult(ToolOutcome.Success($"Typed {text.Length} character(s)."));
		}

		/// <summary>
		/// Unicode packets carry the character directly, which is how characters outside the keyboard layout
		/// are typed. That is also why nothing is typed with held Shift: the layout's own dead keys and AltGr
		/// combinations have already been resolved by the time the text arrives, and synthesising them would
		/// fight with that.
		/// </summary>
		private static void TypeCharacter(char character)
		{
			Send(
			[
				new Input
				{
					Type = InputKeyboard,
					Data = new InputUnion
					{
						Keyboard = new KeyboardInput { VirtualKey = 0, ScanCode = character, Flags = KeyUnicode, ExtraInfo = NoExtraInfo },
					},
				},
				new Input
				{
					Type = InputKeyboard,
					Data = new InputUnion
					{
						Keyboard = new KeyboardInput { VirtualKey = 0, ScanCode = character, Flags = KeyUnicode | KeyUp, ExtraInfo = NoExtraInfo },
					},
				},
			]);
		}
	}

	/// <summary>Presses one combination, for example ctrl+s.</summary>
	public sealed class KeyboardComboTool : ITool
	{
		public string Name => "keyboard_combo";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Presses a key combination such as ctrl+s or alt+shift+delete. Use this for "
				+ "shortcuts, and keyboard_type for text.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["keys"] = new JsonObject
					{
						["type"] = "array",
						["description"] = "Keys to hold together, for example [\"ctrl\", \"s\"].",
						["items"] = new JsonObject { ["type"] = "string" },
					},
				},
				["required"] = new JsonArray("keys"),
			},
		};

		public Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			if (arguments["keys"]?.AsArray() is not { } requested)
			{
				return Task.FromResult(ToolOutcome.Failure("Give the keys to combine, such as ctrl+s."));
			}

			var names = requested
				.Select(node => node?.GetValue<string>()?.Trim())
				.Where(name => !string.IsNullOrWhiteSpace(name))
				.Select(name => name!.ToLowerInvariant())
				.ToArray();

			if (names.Length == 0)
			{
				return Task.FromResult(ToolOutcome.Failure("Give the keys to combine, such as ctrl+s."));
			}

			if (!TryResolve(names, out var codes, out var unknown))
			{
				return Task.FromResult(ToolOutcome.Failure(
					$"Unknown key '{unknown}'. Use names like ctrl, shift, alt, enter, tab, f1 to f12, "
					+ "or a single letter or digit."));
			}

			if (codes.Length == 1)
			{
				// A single key that is a modifier on its own would leave it stuck down, so it is refused.
				return Task.FromResult(ToolOutcome.Failure(
					$"'{names[0]}' is a modifier on its own. Combine it with another key."));
			}

			ReleaseAllModifiers();

			// Modifiers go down first and up last, so the final key is always seen as modified.
			var held = codes.Take(codes.Length - 1).ToArray();
			var final = codes[^1];

			Send([.. held.Select(key => Key(key)), Key(final), Key(final, KeyUp)]);

			// Releasing unconditionally means a cancelled or partially failed send cannot leave a modifier
			// held down for the rest of the session.
			ReleaseAllModifiers();

			return Task.FromResult(ToolOutcome.Success($"Pressed {string.Join('+', names)}."));
		}

		internal static bool TryResolve(string[] names, out ushort[] codes, out string unknown)
		{
			unknown = string.Empty;
			var resolved = new List<ushort>(names.Length);

			foreach (var name in names)
			{
				if (VirtualKeys.TryGetValue(name, out var code))
				{
					resolved.Add(code);
					continue;
				}

				if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
				{
					// The letter row and the number row sit at their literal virtual-key codes, which is why
					// a single character needs no layout mapping here.
					resolved.Add((ushort)char.ToUpperInvariant(name[0]));
					continue;
				}

				codes = [];
				unknown = name;
				return false;
			}

			codes = [.. resolved];
			unknown = string.Empty;
			return true;
		}
	}

	/// <summary>Sends several keys or combinations in order.</summary>
	public sealed class KeyboardSequenceTool : ITool
	{
		public string Name => "keyboard_sequence";

		public bool RequiresConfirmation => true;

		public ToolDefinition Definition => new()
		{
			Name = Name,
			Description = "Sends several keys or combinations in order, waiting a moment between each. "
				+ "Use this for shortcuts that need to happen one after another.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["steps"] = new JsonObject
					{
						["type"] = "array",
						["description"] = "Each step is a key name, or a plus-separated combination.",
						["items"] = new JsonObject { ["type"] = "string" },
					},
					["delayMs"] = new JsonObject
					{
						["type"] = "integer",
						["description"] = "Milliseconds between steps. Default 120.",
					},
				},
				["required"] = new JsonArray("steps"),
			},
		};

		public async Task<ToolOutcome> InvokeAsync(JsonObject arguments, CancellationToken cancellationToken)
		{
			if (arguments["steps"]?.AsArray() is not { } requested)
			{
				return ToolOutcome.Failure("Give the steps to send.");
			}

			var steps = requested
				.Select(node => node?.GetValue<string>()?.Trim())
				.Where(step => !string.IsNullOrWhiteSpace(step))
				.Select(step => step!.ToLowerInvariant())
				.ToArray();

			if (steps.Length == 0)
			{
				return ToolOutcome.Failure("Give the steps to send.");
			}

			const int MaximumSteps = 50;

			if (steps.Length > MaximumSteps)
			{
				return ToolOutcome.Failure($"That is {steps.Length} steps and the limit is {MaximumSteps}.");
			}

			var delay = Math.Clamp(arguments["delayMs"]?.GetValue<int?>() ?? 120, 0, 2_000);

			// Every step is resolved before anything is sent, so a typo in the last step cannot leave the
			// first three already fired.
			foreach (var step in steps)
			{
				var names = step.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

				if (names.Length == 0)
				{
					return ToolOutcome.Failure($"'{step}' names no keys at all.");
				}

				if (!KeyboardComboTool.TryResolve(names, out var codes, out var unknown))
				{
					return ToolOutcome.Failure($"'{step}' is not a key I know. '{unknown}' is not a key name.");
				}

				if (codes.Length == 1)
				{
					if (!VirtualKeys.ContainsKey(names[0]))
					{
						return ToolOutcome.Failure(
							$"'{step}' is a letter or digit, so it needs a key name like a, enter or f5.");
					}

					if (ModifierSet.Contains(names[0]))
					{
						return ToolOutcome.Failure($"'{step}' is a modifier on its own.");
					}
				}
			}

			var sent = 0;

			foreach (var step in steps)
			{
				cancellationToken.ThrowIfCancellationRequested();

				var names = step.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				KeyboardComboTool.TryResolve(names, out var codes, out _);

				if (codes.Length == 1)
				{
					const uint ExtendedKeys =
						0x21 | 0x22 | 0x23 | 0x24 | 0x25 | 0x26 | 0x27 | 0x28 | 0x2D | 0x2E | 0x5B | 0x5C;
					var flags = (uint)(codes[0] & ExtendedKeys);
					Send([Key(codes[0], flags), Key(codes[0], flags | KeyUp)]);
				}
				else
				{
					var held = codes.Take(codes.Length - 1).ToArray();
					var final = codes[^1];
					Send([.. held.Select(code => Key(code)), Key(final), Key(final, KeyUp)]);
					ReleaseAllModifiers();
				}

				sent++;

				if (delay > 0 && sent < steps.Length)
				{
					await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
				}
			}

			ReleaseAllModifiers();
			return ToolOutcome.Success($"Sent {sent} step(s).");
		}
	}
}
