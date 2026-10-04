using System.Globalization;
using Jarvis.Plugin.Speech;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Drives the synthesizer and the player against the real Windows audio stack. It plays at a low
/// volume and a short clip because it runs unattended on a developer machine.
/// </summary>
[TestFixture]
[Explicit("Plays audible audio through the default render device.")]
public class TtsProbe
{
	[Test]
	public async Task SynthesizeAndPlay()
	{
		var sink = new LoggerConfiguration()
			.MinimumLevel.Verbose()
			.WriteTo.Sink(CollectingSink.Instance)
			.CreateLogger();

		var synth = new WindowsSynthesizer(sink);
		var voices = synth.InstalledVoicesAsync(CancellationToken.None).GetAwaiter().GetResult();

		TestContext.Out.WriteLine($"voices = {voices.Length}: {string.Join(" | ", voices)}");
		Assert.That(voices, Is.Not.Empty, "Windows reported no speech voices.");

		var synthWav = Path.Combine(Path.GetTempPath(), $"jarvis-probe-{Guid.CreateVersion7():N}.wav");

		try
		{
			var duration = await synth.SynthesizeAsync(
				"At your service, sir. All systems are nominal.",
				synthWav,
				voices[0],
				0,
				100,
				CancellationToken.None);

			using var header = new WaveFileReaderHeader(synthWav);

			TestContext.Out.WriteLine(
				$"synth: {new FileInfo(synthWav).Length} bytes, reported {duration.TotalSeconds:F2}s, " +
				$"header {header.Duration.TotalSeconds:F2}s");

			Assert.That(header.Duration, Is.GreaterThan(TimeSpan.Zero), "The rendered clip has no duration.");
		}
		finally
		{
			File.Delete(synthWav);
		}

		var playWav = Path.Combine(Path.GetTempPath(), $"jarvis-play-{Guid.CreateVersion7():N}.wav");

		await synth.SynthesizeAsync(
			"Systems nominal.",
			playWav,
			voices[^1],
			0,
			100,
			CancellationToken.None);

		try
		{
			var levels = new List<double>();
			var peak = 0.0;
			using var player = new SpeechPlayer(sink);

			var started = DateTimeOffset.UtcNow;
			var accepted = player.Play(playWav, level =>
			{
				peak = Math.Max(peak, level);
				lock (levels)
				{
					levels.Add(level);
				}
			}, 15);

			TestContext.Out.WriteLine($"Play accepted = {accepted}, duration = {player.Duration.TotalSeconds:F2}s");
			Assert.That(accepted, Is.True, "The player refused the clip.");

			await player.WaitAsync(CancellationToken.None);
			var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;

			int reports;

			lock (levels)
			{
				reports = levels.Count;
			}

			TestContext.Out.WriteLine(
				$"playback: {elapsed:F2}s elapsed, IsPlaying = {player.IsPlaying}, " +
				$"loopback reports = {reports}, peak = {peak:F3}");

			Assert.That(elapsed, Is.GreaterThan(0.1), "Playback returned immediately.");
			Assert.That(player.IsPlaying, Is.False, "The player still holds the clip after it ended.");
		}
		finally
		{
			File.Delete(playWav);
		}
	}

	[Test]
	public async Task StopInterruptsPlayback()
	{
		var sink = new LoggerConfiguration()
			.MinimumLevel.Verbose()
			.WriteTo.Sink(CollectingSink.Instance)
			.CreateLogger();

		var synth = new WindowsSynthesizer(sink);
		var voices = synth.InstalledVoicesAsync(CancellationToken.None).GetAwaiter().GetResult();
		var wav = Path.Combine(Path.GetTempPath(), $"jarvis-stop-{Guid.CreateVersion7():N}.wav");

		await synth.SynthesizeAsync(
			"This is a deliberately long sentence so that the interruption test has something to cut short.",
			wav,
			voices[0],
			-2,
			100,
			CancellationToken.None);

		try
		{
			using var player = new SpeechPlayer(sink);

			var started = DateTimeOffset.UtcNow;
			Assert.That(player.Play(wav, _ => { }, 15), Is.True);

			await Task.Delay(200, CancellationToken.None);
			player.Stop();

			await player.WaitAsync(CancellationToken.None);
			var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;
			var full = player.Duration.TotalSeconds;

			TestContext.Out.WriteLine(
				$"stop: {elapsed:F2}s of {full:F2}s elapsed, IsPlaying = {player.IsPlaying}");

			Assert.That(player.IsPlaying, Is.False);
			Assert.That(elapsed, Is.LessThan(full), "Stop did not cut the clip short.");
		}
		finally
		{
			File.Delete(wav);
		}
	}
}

internal sealed class CollectingSink : ILogEventSink
{
	public static CollectingSink Instance { get; } = new();

	public void Emit(LogEvent logEvent)
	{
		TestContext.Out.WriteLine($"  log[{logEvent.Level}] {logEvent.RenderMessage(CultureInfo.InvariantCulture)}");
	}
}
