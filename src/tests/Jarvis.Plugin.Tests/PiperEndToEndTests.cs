using Jarvis.Plugin.Core;
using Jarvis.Plugin.Runtime;
using Jarvis.Plugin.Speech;
using NUnit.Framework;
using Serilog;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Installs Piper through the runtime manager and speaks with it. This is the only test that proves the
/// two halves fit together: the manager's unpacked layout has to match what the synthesizer expects to
/// find inside it, and that expectation is exactly the sort of thing a unit test on each half separately
/// will happily agree on while disagreeing with reality.
/// </summary>
[TestFixture]
[Explicit("Downloads Piper and its voice, then speaks audibly.")]
public class PiperEndToEndTests
{
	[Test]
	[CancelAfter(900_000)]
	public async Task Piper_renders_and_a_voice_on_disk_is_discovered()
	{
		var root = Path.Combine(Path.GetTempPath(), $"jarvis-piper-{Guid.CreateVersion7():N}");

		try
		{
			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
			using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));

			var manager = new RuntimeManager(client, RuntimeTestLog.Logger, new RuntimePaths(root));

			var install = await manager.EnsureComponentAsync(
				AssetCatalog.Piper, manager.Progress, cancellation.Token);

			Assert.That(install.Installed, Is.True, $"{install.Failure}: {install.Detail}");

			var piper = new PiperSynthesizer(manager, RuntimeTestLog.Logger);

			Assert.Multiple(() =>
			{
				Assert.That(piper.IsAvailable, Is.True, "the binary did not land where the synthesizer looks");
				Assert.That(piper.Voices, Is.Not.Empty, "no voice was discovered on disk");
			});

			var voice = piper.Voices[0];
			var wav = Path.Combine(root, "piper-out.wav");

			var rendered = await piper.TrySynthesizeAsync(
				"At your service, sir. All systems are nominal.", wav, voice.Name, cancellation.Token);

			Assert.That(rendered, Is.True);

			using var header = new WaveFileReaderHeader(wav);

			Assert.Multiple(() =>
			{
				Assert.That(new FileInfo(wav).Length, Is.GreaterThan(1_000), "the rendered clip is implausibly small");
				Assert.That(header.Duration, Is.GreaterThan(TimeSpan.FromMilliseconds(200)));
			});

			TestContext.Out.WriteLine(
				$"piper voice {voice.Name}: {new FileInfo(wav).Length} bytes, {header.Duration.TotalSeconds:F2}s");

			// The path that matters is the configured default, not any voice that happened to be on disk: a
			// default that names a voice nothing installs means a silent fallback for every user.
			var configured = new JarvisSettings().PiperVoice;

			Assert.That(
				piper.Voices.Select(candidate => candidate.Name),
				Contains.Item(configured),
				$"the default voice '{configured}' is not among the installed voices");

			var configuredWav = Path.Combine(root, "configured.wav");
			Assert.That(
				await piper.TrySynthesizeAsync("Systems nominal.", configuredWav, configured, cancellation.Token),
				Is.True,
				"the default voice did not render");
		}
		finally
		{
			TryDelete(root);
		}
	}

	/// <summary>
	/// A machine with no Piper installed must report itself unavailable and render nothing, rather than
	/// throwing. This is the state almost every user is in before the first install, so it is the one that
	/// has to be graceful.
	/// </summary>
	[Test]
	public void An_uninstalled_piper_reports_unavailable_without_throwing()
	{
		var root = Path.Combine(Path.GetTempPath(), $"jarvis-empty-{Guid.CreateVersion7():N}");

		try
		{
			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
			var manager = new RuntimeManager(client, RuntimeTestLog.Logger, new RuntimePaths(root));
			var piper = new PiperSynthesizer(manager, RuntimeTestLog.Logger);

			var rendered = piper.TrySynthesizeAsync(
				"This should not be audible.",
				Path.Combine(root, "out.wav"),
				"en_GB-alan-medium",
				TestContext.CurrentContext.CancellationToken).GetAwaiter().GetResult();

			Assert.Multiple(() =>
			{
				Assert.That(piper.IsAvailable, Is.False);
				Assert.That(piper.Voices, Is.Empty);
				Assert.That(rendered, Is.False, "an absent component produced audio");
			});
		}
		finally
		{
			TryDelete(root);
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			TestContext.Out.WriteLine($"could not clean up {path}: {exception.Message}");
		}
	}
}
