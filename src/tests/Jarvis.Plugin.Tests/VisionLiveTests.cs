using Jarvis.Plugin.Core;
using Jarvis.Plugin.Vision;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Asks the configured vision model about the actual screen. The key is read from the developer's local
/// settings file, which is gitignored, so this only runs where one has been configured.
/// </summary>
[TestFixture]
[Explicit("Sends a real screenshot to the configured vision model.")]
public class VisionLiveTests
{
	[Test]
	[CancelAfter(300_000)]
	public async Task The_vision_model_describes_the_screen()
	{
		var capture = ScreenCaptureService.Capture(CaptureTarget.PrimaryScreen, RuntimeTestLog.Logger);

		var (settings, store) = await StubSettingsAsync(TestContext.CurrentContext.CancellationToken);

		Assert.That(settings.HasLlmCredentials, Is.True, "no API key is configured for the live test");

		using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
		var vision = new VisionClient(client, store, RuntimeTestLog.Logger);

		var result = await vision.DescribeScreenAsync(
			capture, "What is on this screen?", TestContext.CurrentContext.CancellationToken);

		TestContext.Out.WriteLine($"model: {settings.VisionModel}");
		TestContext.Out.WriteLine($"reply: {result.Text}");

		Assert.That(result.Ok, Is.True, $"vision failed: {result.Failure} {result.Detail}");

		// A model that answers with nothing has been reached but not understood, which is a different
		// failure from one that could not be reached at all.
		Assert.That(result.Text.Trim(), Is.Not.Empty);
	}

	/// <summary>
	/// The developer settings file lives beside the plugin build rather than beside the tests, so it is
	/// found by walking up and handed to the store directly. Copying it into the test output instead would
	/// leave a credential there that every other test would then read and believe itself configured.
	/// </summary>
	private static async Task<(JarvisSettings Settings, JarvisSettingsStore Store)> StubSettingsAsync(
		CancellationToken cancellationToken)
	{
		var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, "src", "Jarvis.Plugin", "bin", "Debug", "net10.0");

			if (File.Exists(Path.Combine(candidate, "jarvis.settings.json")))
			{
				var store = new JarvisSettingsStore(RuntimeTestLog.Logger, LocalSettingsFile.Load(candidate));

				// The store only applies what it has read during a reload, so constructing it is not enough.
				await store.ReloadAsync(context: null, cancellationToken);

				return (store.Current, store);
			}

			directory = directory.Parent;
		}

		Assert.Fail("no developer settings file was found beside the plugin build");
		throw new InvalidOperationException("unreachable");
	}
}