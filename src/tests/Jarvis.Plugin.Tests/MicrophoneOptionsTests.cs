using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Core;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The microphone dropdown. Endpoint ids are opaque Windows strings nobody should have to paste, so the
/// voice step offers the live devices as a choice whose values are those ids.
/// </summary>
[TestFixture]
public class MicrophoneOptionsTests
{
	[Test]
	public void The_first_option_is_the_system_default_with_an_empty_value()
	{
		var options = JarvisFields.MicrophoneOptions([]);

		Assert.That(options, Has.Count.EqualTo(1));
		Assert.That(options[0].Value, Is.Empty);
		Assert.That(options[0].Label == Strings.ConfigFlow.Voice.MicrophoneDefault(), Is.True);
	}

	[Test]
	public void Every_device_becomes_an_option_valued_by_its_id()
	{
		var options = JarvisFields.MicrophoneOptions(
		[
			new AudioDevice("id-one", "Desk Microphone", false),
			new AudioDevice("id-two", "Headset", false),
		]);

		Assert.That(options.Select(option => option.Value), Is.EqualTo(["", "id-one", "id-two"]));
		Assert.That(options[1].Label.Literal, Is.EqualTo("Desk Microphone"));
		Assert.That(options[2].Label.Literal, Is.EqualTo("Headset"));
	}

	[Test]
	public void The_default_device_is_marked_as_such()
	{
		var options = JarvisFields.MicrophoneOptions(
		[
			new AudioDevice("id-one", "Desk Microphone", true),
		]);

		Assert.That(
			options[1].Label == Strings.ConfigFlow.Voice.MicrophoneIsDefault("Desk Microphone"),
			Is.True);
	}

	[Test]
	public void The_declared_microphone_setting_is_a_choice_with_live_options()
	{
		var field = JarvisFields.All.Single(field => field.Name == JarvisSettingsStoreFields.MicrophoneIdField);

		Assert.Multiple(() =>
		{
			Assert.That(field.Kind, Is.EqualTo(JarvisFieldKind.Choice));
			Assert.That(field.OptionsSource, Is.Not.Null);
		});
	}
}
