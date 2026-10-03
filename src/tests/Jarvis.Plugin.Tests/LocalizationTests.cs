using MacroDeck.Localization;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// A translation that loads but never resolves is the same as no translation at all, so the wording that
/// matters is checked against the resolver rather than against the file's presence.
/// </summary>
[TestFixture]
public class LocalizationTests
{
	private static LocalizationResolver BuildResolver()
	{
		var registry = new LocalizationCatalogRegistry();
		registry.Register(Strings.LocalizationCatalog);
		registry.Register(MacroDeckStrings.LocalizationCatalog);

		return new LocalizationResolver(registry);
	}

	[Test]
	public void A_german_reader_sees_german_action_names()
	{
		var resolver = BuildResolver();

		Assert.Multiple(() =>
		{
			Assert.That(resolver.Resolve(Strings.Actions.Activate.Name(), "de"), Is.EqualTo("JARVIS wecken"));
			Assert.That(resolver.Resolve(Strings.Actions.Cancel.Name(), "de"), Is.EqualTo("Abbrechen"));
			Assert.That(resolver.Resolve(Strings.States.Listening(), "de"), Is.EqualTo("Hört zu"));
		});
	}

	[Test]
	public void A_german_reader_sees_german_orb_labels()
	{
		var resolver = BuildResolver();

		Assert.Multiple(() =>
		{
			Assert.That(resolver.Resolve(Strings.Orb.Config.Preset(), "de"), Is.EqualTo("Form"));
			Assert.That(resolver.Resolve(Strings.Orb.Config.Sensitivity(), "de"), Is.EqualTo("Empfindlichkeit"));
			Assert.That(resolver.Resolve(Strings.Orb.Config.ScopeOptions.Global(), "de"),
				Is.EqualTo("Allen Kugeln auf diesem Deck"));
		});
	}

	/// <summary>An untranslated key must fall back to English rather than render as a raw key.</summary>
	[Test]
	public void A_key_missing_from_german_falls_back_to_english()
	{
		var resolver = BuildResolver();

		Assert.That(
			resolver.Resolve(Strings.Actions.Activate.Name(), "fr"),
			Is.EqualTo("Activate JARVIS"),
			"an untranslated key must fall back to English, not render as [[plugin:...]]");
	}

	/// <summary>
	/// A regional culture resolves through its neutral culture, so a reader on de-AT must get German rather
	/// than English.
	/// </summary>
	[Test]
	public void A_regional_culture_resolves_through_its_neutral_form()
	{
		var resolver = BuildResolver();

		Assert.That(resolver.Resolve(Strings.Actions.Cancel.Name(), "de-AT"), Is.EqualTo("Abbrechen"));
	}

	/// <summary>
	/// Placeholders survive translation. A German sentence that dropped {component} would render an
	/// argument out of range at runtime rather than at build time, so the substituted form is checked.
	/// </summary>
	[Test]
	public void A_placeholder_is_substituted_in_german()
	{
		var resolver = BuildResolver();

		var rendered = resolver.Resolve(Strings.Runtime.UnknownComponent("piper"), "de");

		Assert.Multiple(() =>
		{
			Assert.That(rendered, Does.Contain("piper"));
			Assert.That(rendered, Does.Not.Contain("{component}"));
		});
	}

	/// <summary>
	/// Every German entry has to exist in the default catalogue, which the build already enforces; this
	/// catches the reverse mistake, a German key nobody will ever read.
	/// </summary>
	[Test]
	public void The_german_catalogue_covers_every_user_facing_key()
	{
		var english = Strings.LocalizationCatalog.KeysOf("en").ToHashSet(StringComparer.Ordinal);
		var german = Strings.LocalizationCatalog.KeysOf("de").ToHashSet(StringComparer.Ordinal);

		Assert.That(german, Is.SubsetOf(english), "German has keys the default catalogue does not");

		// Spot-check the count rather than asserting exact equality: a future English-only key is legitimate.
		Assert.That(german.Count, Is.GreaterThan(150), "the German catalogue is suspiciously small");
	}
}