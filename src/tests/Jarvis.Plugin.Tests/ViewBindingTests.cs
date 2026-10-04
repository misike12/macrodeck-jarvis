using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// A computed property is only reactive if it subscribes, and subscribing is a side effect of reading
/// <c>Value</c> while the tree is built.
/// <para>
/// Every binding in the orb read <c>Peek()</c>, which is documented as the reading that does <em>not</em>
/// subscribe. The tree was therefore correct once and never again: the core asset arrived, the state was
/// written, nothing was listening, and the widget stayed empty. There was no error to find, because every
/// line involved was doing exactly what it said.
/// </para>
/// </summary>
[TestFixture]
public class ViewBindingTests
{
	private static UiView ViewOver(UiElement root)
	{
		var surface = new UiSurface
		{
			Kind = UiSurfaceKinds.Widget,
			SessionMode = UiSessionModes.Shared,
		};

		return new UiView(surface, root);
	}

	/// <summary>
	/// The contract the orb depends on: writing a state re-evaluates the properties that read it.
	/// </summary>
	[Test]
	public void A_property_reading_Value_is_re_evaluated_when_the_state_is_written()
	{
		var state = new UiState<string>("before");
		using var view = ViewOver(new UiTextRun { Key = "t", Text = UiText.From(() => state.Value) });

		state.Set("after");

		Assert.That(
			view.DrainPatches(),
			Is.Not.Empty,
			"writing the state produced no patch, so the view would keep showing the value it was built with");
	}

	/// <summary>
	/// The trap, stated as a test so the difference is not rediscovered by a user looking at an empty widget.
	/// </summary>
	[Test]
	public void A_property_reading_Peek_is_not_re_evaluated()
	{
		var state = new UiState<string>("before");
		using var view = ViewOver(new UiTextRun { Key = "t", Text = UiText.From(() => state.Peek()) });

		state.Set("after");

		Assert.That(
			view.DrainPatches(),
			Is.Empty,
			"Peek subscribed after all, so the reason the orb was blank is not the one on record");
	}

	/// <summary>
	/// An equal write changes nothing, which is what makes an unbound property look plausible: the tree looks
	/// fine right up until the one value that matters.
	/// </summary>
	[Test]
	public void An_equal_write_is_free()
	{
		var state = new UiState<string>("same");
		using var view = ViewOver(new UiTextRun { Key = "t", Text = UiText.From(() => state.Value) });

		state.Set("same");

		Assert.That(view.DrainPatches(), Is.Empty);
	}

	/// <summary>
	/// The orb itself must not bind through <c>Peek</c> anywhere. Checked over the file rather than one
	/// property, because every instance of this mistake looks correct and none of them fails a test that only
	/// exercises the state it happens to bind.
	/// </summary>
	[Test]
	public void The_orb_binds_no_property_through_Peek()
	{
		var source = File.ReadAllText(Path.Combine(PluginDirectory(), "Orb", "OrbView.cs"));

		Assert.That(
			System.Text.RegularExpressions.Regex.Count(source, @"From\(\(\) =>[^)]*\.Peek\(\)"),
			Is.Zero,
			"a computed property reads Peek, so it will never be re-evaluated and whatever it draws is frozen "
				+ "at whatever the value was when the widget opened");
	}

	private static string PluginDirectory()
	{
		var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, "src", "Jarvis.Plugin");

			if (Directory.Exists(candidate))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("Could not locate the plugin directory above the test output.");
	}
}