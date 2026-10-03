using System.Runtime.Versioning;
using Jarvis.Plugin.Input;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Drives the global hotkey against the real window manager. Registration and delivery are two different
/// things that can each fail on their own, and only the pair proves the feature works.
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class GlobalHotkeyTests
{
	[Test]
	public void A_chord_round_trips_through_its_text_form()
	{
		var chord = HotkeyChord.Parse("Ctrl+Alt+J");

		Assert.That(chord, Is.Not.Null);
		Assert.That(chord!.Modifiers, Is.EqualTo(ModifierKeys.Control | ModifierKeys.Alt));
		Assert.That(chord.Key, Is.EqualTo(VirtualKey.J));
		Assert.That(HotkeyChord.Parse(chord.ToString()), Is.EqualTo(chord));
	}

	/// <summary>
	/// A bare key would take that key away from every application on the machine for as long as Macro Deck
	/// runs, so an unmodified chord is not a legal hotkey.
	/// </summary>
	[TestCase("J")]
	[TestCase("")]
	[TestCase("   ")]
	[TestCase(null)]
	[TestCase("Ctrl+")]
	[TestCase("Ctrl+NotAKey")]
	[TestCase("Ctrl+Alt")]
	public void An_unusable_chord_is_refused_rather_than_guessed(string? text)
	{
		Assert.That(HotkeyChord.Parse(text), Is.Null);
	}

	[Test]
	public void A_registration_succeeds_and_is_then_released()
	{
		using var hotkey = new GlobalHotkey(RuntimeTestLog.Logger);

		// Ctrl+F24 is not a chord anything else is likely to own, so a failure here means the registration
		// mechanism is broken rather than that the key was taken.
		var chord = new HotkeyChord(ModifierKeys.Control | ModifierKeys.Alt, VirtualKey.F12);

		Assert.That(hotkey.Register(chord), Is.True, hotkey.LastError);
		Assert.That(hotkey.Active, Is.EqualTo(chord));

		hotkey.Unregister();

		Assert.Multiple(() =>
		{
			Assert.That(hotkey.Active, Is.Null);
			Assert.That(hotkey.LastError, Is.Null);
		});
	}

	/// <summary>
	/// A chord another process already owns must be reported as taken, not accepted quietly. Registering
	/// the same chord twice here is the closest reproducible stand-in for another application holding it.
	/// </summary>
	[Test]
	public void A_chord_already_in_use_is_reported_rather_than_silently_accepted()
	{
		using var first = new GlobalHotkey(RuntimeTestLog.Logger);
		using var second = new GlobalHotkey(RuntimeTestLog.Logger);

		var chord = new HotkeyChord(ModifierKeys.Control | ModifierKeys.Shift, VirtualKey.F11);

		Assert.That(first.Register(chord), Is.True, first.LastError);
		Assert.That(second.Register(chord), Is.False, "the same chord was accepted twice");
		Assert.That(second.LastError, Is.Not.Null);
	}
}