namespace Jarvis.Plugin.Input;

/// <summary>
/// The virtual-key codes a hotkey chord may name. Only the ordinary keys are listed: a hotkey bound to
/// Pause or PrintScreen would be indistinguishable from the system using it, and a chord nobody can type
/// is not a feature.
/// </summary>
public enum VirtualKey
{
	None = 0,
	A = 0x41, B = 0x42, C = 0x43, D = 0x44, E = 0x45, F = 0x46, G = 0x47, H = 0x48,
	I = 0x49, J = 0x4A, K = 0x4B, L = 0x4C, M = 0x4D, N = 0x4E, O = 0x4F, P = 0x50,
	Q = 0x51, R = 0x52, S = 0x53, T = 0x54, U = 0x55, V = 0x56, W = 0x57, X = 0x58,
	Y = 0x59, Z = 0x5A,
	D0 = 0x30, D1 = 0x31, D2 = 0x32, D3 = 0x33, D4 = 0x34,
	D5 = 0x35, D6 = 0x36, D7 = 0x37, D8 = 0x38, D9 = 0x39,
	F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75,
	F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B,
	Space = 0x20, Enter = 0x0D, Tab = 0x09, Backspace = 0x08, Escape = 0x1B,
	OemTilde = 0xC0, OemMinus = 0xBD, OemPlus = 0xBB, OemOpenBrackets = 0xDB,
	OemCloseBrackets = 0xDD, OemPipe = 0xDC, OemSemicolon = 0xBA, OemQuotes = 0xDE,
	OemComma = 0xBC, OemPeriod = 0xBE, OemQuestion = 0xBF,
}

/// <summary>
/// How a hotkey chord is written down and parsed back. Kept as a string in settings so a chord survives a
/// round trip through configuration without needing a serialisable key-code mapping.
/// </summary>
public sealed record HotkeyChord(ModifierKeys Modifiers, VirtualKey Key)
{
	/// <summary>
	/// Parses "Ctrl+Alt+J". A chord with no modifier is rejected: a bare letter would swallow that key
	/// everywhere, in every application, for as long as Macro Deck is running.
	/// </summary>
	public static HotkeyChord? Parse(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}

		var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		if (parts.Length < 2)
		{
			return null;
		}

		var modifiers = ModifierKeys.None;

		foreach (var part in parts[..^1])
		{
			if (!TryParseModifier(part, out var modifier))
			{
				return null;
			}

			modifiers |= modifier;
		}

		return Enum.TryParse<VirtualKey>(parts[^1], true, out var key) && key != VirtualKey.None
			? new HotkeyChord(modifiers, key)
			: null;
	}

	/// <summary>
	/// Accepts the spellings people actually type. "Ctrl" is what every keyboard and every settings dialog
	/// calls it, and rejecting that would make the obvious chord the one chord that cannot be configured.
	/// </summary>
	private static bool TryParseModifier(string text, out ModifierKeys modifier)
	{
		if (Enum.TryParse<ModifierKeys>(text, true, out modifier) && modifier != ModifierKeys.None)
		{
			return true;
		}

		switch (text.ToLowerInvariant())
		{
			case "ctrl":
				modifier = ModifierKeys.Control;
				return true;
			case "cmd":
			case "super":
			case "meta":
			case "windows":
				modifier = ModifierKeys.Win;
				return true;
			default:
				modifier = ModifierKeys.None;
				return false;
		}
	}

	public override string ToString() => $"{Modifiers}+{Key}";
}