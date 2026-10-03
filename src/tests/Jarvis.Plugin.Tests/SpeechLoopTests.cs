using Jarvis.Plugin.Runtime;
using Jarvis.Plugin.Speech;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The whole speech loop, driven end to end: Piper synthesises a sentence, and whisper.cpp transcribes the
/// audio back. Using one engine's output as the other's input is what makes this a real test rather than
/// two units tested separately that agree on a shared fiction.
/// </summary>
[TestFixture]
[Explicit("Downloads Piper and Whisper, then synthesises and transcribes real audio.")]
public class SpeechLoopTests
{
	[Test]
	[CancelAfter(900_000)]
	public async Task Piper_audio_is_transcribed_back_to_the_same_words()
	{
		const string Sentence = "Jarvis, what is the capital of France?";

		var root = Path.Combine(Path.GetTempPath(), $"jarvis-loop-{Guid.CreateVersion7():N}");

		try
		{
			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
			using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(12));

			var manager = new RuntimeManager(client, RuntimeTestLog.Logger, new RuntimePaths(root));

			foreach (var component in new[] { AssetCatalog.Piper, AssetCatalog.Whisper })
			{
				var install = await manager.EnsureComponentAsync(component, manager.Progress, cancellation.Token);
				Assert.That(install.Installed, Is.True, $"{component}: {install.Failure} {install.Detail}");
			}

			var piper = new PiperSynthesizer(manager, RuntimeTestLog.Logger);
			var whisper = new WhisperTranscriber(manager, RuntimeTestLog.Logger);

			Assert.Multiple(() =>
			{
				Assert.That(piper.IsAvailable, Is.True);
				Assert.That(whisper.IsAvailable, Is.True);
				Assert.That(piper.Voices, Is.Not.Empty);
				Assert.That(whisper.InstalledModels, Is.Not.Empty, "no speech model was discovered");
			});

			var spoken = Path.Combine(root, "spoken.wav");

			Assert.That(
				await piper.TrySynthesizeAsync(Sentence, spoken, piper.Voices[0].Name, cancellation.Token),
				Is.True,
				"Piper produced no audio to transcribe");

			var transcribed = await whisper.TranscribeAsync(spoken, "en", cancellation.Token);

			Assert.That(transcribed.Ok, Is.True, $"transcription failed: {transcribed.Failure} {transcribed.Detail}");
			TestContext.Out.WriteLine($"asked: {Sentence}");
			TestContext.Out.WriteLine($"heard: {transcribed.Text}");

			// Compared loosely on purpose. The recogniser is allowed to differ on case and punctuation, and
			// asserting an exact string match would fail on a phrasing difference that does not matter.
			Assert.That(
				Normalise(transcribed.Text),
				Is.EqualTo(Normalise(Sentence)),
				"the transcript did not come back as the words that were spoken");
		}
		finally
		{
			TryDelete(root);
		}
	}

	/// <summary>Lower-cased with punctuation and spacing collapsed, so only the words are compared.</summary>
	private static string Normalise(string text) =>
		string.Join(' ', text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()).Trim();

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