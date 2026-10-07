using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Input;
using Jarvis.Plugin.Llm;
using Jarvis.Plugin.Runtime;
using Jarvis.Plugin.Speech;
using MacroDeck.Plugin.Testing;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NUnit.Framework;
using Serilog;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Measures every step of the voice pipeline with precise timings.
/// Run with: dotnet test --filter LatencyMeasurementTool
/// </summary>
[TestFixture]
[Explicit("Measures end-to-end latency of every pipeline step")]
public class LatencyMeasurementTool
{
    private static readonly ILogger Logger = Log.ForContext<LatencyMeasurementTool>();	private static readonly List<Measurement> Measurements = [];
	private static readonly Stopwatch TotalSw = Stopwatch.StartNew();
	private static readonly string ReportPath = Path.Combine(
		Path.GetTempPath(),
		$"jarvis-latency-report-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt");

    private record Measurement(string Step, TimeSpan Duration, string Details = "");	private static void Record(string step, TimeSpan duration, string details = "")
	{
		var sw = Stopwatch.GetTimestamp();
		Measurements.Add(new(step, duration, details));
		Logger.Information("⏱ {Step}: {Duration:F3}ms {Details}",
			step, duration.TotalMilliseconds, details);
	}

	private static void SaveReport()
	{
		try
		{
			using var writer = new StreamWriter(ReportPath);
			writer.WriteLine("================================================================================");
			writer.WriteLine("  JARVIS VOICE PIPELINE LATENCY REPORT");
			writer.WriteLine($"  Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC");
			writer.WriteLine($"  Process: {Environment.ProcessId}");
			writer.WriteLine($"  Machine: {Environment.MachineName}");
			writer.WriteLine($"  CPU: {Environment.ProcessorCount} cores");
			writer.WriteLine($"  OS: {Environment.OSVersion}");
			writer.WriteLine("================================================================================");
			writer.WriteLine();

			foreach (var m in Measurements)
			{
				writer.WriteLine($"  ⏱ {m.Step,-50} {m.Duration.TotalMilliseconds,10:F2} ms  {m.Details}");
			}

			writer.WriteLine();
			writer.WriteLine("================================================================================");
			writer.WriteLine("  SUMMARY");
			writer.WriteLine("================================================================================");

			// Group by category
			var categories = Measurements
				.GroupBy(m => m.Step.Split(' ')[0])
				.OrderBy(g => g.Key);

			foreach (var cat in categories)
			{
				var total = cat.Sum(m => m.Duration.TotalMilliseconds);
				var avg = cat.Average(m => m.Duration.TotalMilliseconds);
				var min = cat.Min(m => m.Duration.TotalMilliseconds);
				var max = cat.Max(m => m.Duration.TotalMilliseconds);
	
				writer.WriteLine();
				writer.WriteLine($"  {cat.Key}:");
				writer.WriteLine($"    Count:  {cat.Count()}");
				writer.WriteLine($"    Total:  {total,10:F2} ms");
				writer.WriteLine($"    Average: {avg,10:F2} ms");
				writer.WriteLine($"    Min:    {min,10:F2} ms");
				writer.WriteLine($"    Max:    {max,10:F2} ms");
			}

			writer.WriteLine();
			var grandTotal = Measurements.Sum(m => m.Duration.TotalMilliseconds);
			writer.WriteLine("--------------------------------------------------------------------------------");
			writer.WriteLine($"  GRAND TOTAL: {grandTotal,10:F2} ms ({grandTotal / 1000:F2} s)");
			writer.WriteLine("--------------------------------------------------------------------------------");

			writer.WriteLine();
			writer.WriteLine("================================================================================");
			writer.WriteLine("  FULL MEASUREMENTS (chronological)");
			writer.WriteLine("================================================================================");
			for (int i = 0; i < Measurements.Count; i++)
			{
				var m = Measurements[i];
				writer.WriteLine($"  [{i + 1,3}] {m.Step,-55} {m.Duration.TotalMilliseconds,10:F2} ms");
				if (!string.IsNullOrEmpty(m.Details))
					writer.WriteLine($"         Details: {m.Details}");
			}
			writer.WriteLine();           	writer.WriteLine($"  Report saved to: {ReportPath}");
		}
		catch (Exception ex)
		{
			Logger.Warning(ex, "Could not save latency report to {Path}", ReportPath);
		}
	}

	[OneTimeTearDown]
	public void SaveFinalReport()
	{
		SaveReport();
	}

    private static void Section(string title)
    {
        Logger.Information("");
        Logger.Information("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Logger.Information("  {Title}", title);
        Logger.Information("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    }

    // ── 1. Whisper transcription latency ────────────────────────────

    [Test]
    public async Task Measure_Whisper_Transcription_Latency()
    {
        Section("1. WHISPER TRANSCRIPTION LATENCY");
        var root = Path.Combine(Path.GetTempPath(), $"jarvis-latency-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            var manager = new RuntimeManager(client, Logger, new RuntimePaths(root));

            var install = await manager.EnsureComponentAsync(AssetCatalog.Whisper, manager.Progress, cancellation.Token);
            Assert.That(install.Installed, Is.True, $"{AssetCatalog.Whisper}: {install.Failure} {install.Detail}");

            var transcriber = new WhisperTranscriber(manager, Logger);
            Assert.That(transcriber.IsAvailable, Is.True);

            var model = transcriber.InstalledModels.FirstOrDefault();
            Logger.Information("Model: {Model}", model);

            var piper = new PiperSynthesizer(manager, Logger);
            await manager.EnsureComponentAsync(AssetCatalog.Piper, manager.Progress, cancellation.Token);

            var sentence = "Jarvis, what is the capital of France?";
            var wavPath = Path.Combine(root, "test.wav");

            var renderSw = Stopwatch.StartNew();
            var ok = await piper.TrySynthesizeAsync(sentence, wavPath, piper.Voices[0].Name, cancellation.Token);
            renderSw.Stop();
            Assert.That(ok, Is.True);
            var fileSize = new FileInfo(wavPath).Length;
            var durationSec = GetWavDuration(wavPath).TotalSeconds;
            Record("Piper render WAV", renderSw.Elapsed, $"size={fileSize}B dur={durationSec:F2}s");

            // Warm up
            for (int i = 0; i < 2; i++)
            {
                _ = await transcriber.TranscribeAsync(wavPath, "en", cancellation.Token);
            }

            // Measure over multiple runs
            var durations = new List<TimeSpan>();
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                var result = await transcriber.TranscribeAsync(wavPath, "en", cancellation.Token);
                sw.Stop();
                durations.Add(sw.Elapsed);
                Record($"Whisper run {i + 1}", sw.Elapsed, $"text='{result.Text}' ok={result.Ok}");
            }

            var avg = TimeSpan.FromTicks((long)durations.Average(t => t.Ticks));
            var min = durations.Min();
            var max = durations.Max();        Logger.Information("");
		Logger.Information("Whisper Summary: avg={Avg:F0}ms min={Min:F0}ms max={Max:F0}ms",
			avg.TotalMilliseconds, min.TotalMilliseconds, max.TotalMilliseconds);
		Record("Whisper AVERAGE", avg, $"min={min.TotalMilliseconds:F0}ms max={max.TotalMilliseconds:F0}ms");
		SaveReport();
	}
	finally
	{
		TryDelete(root);
	}
}

    // ── 2. LLM completion latency ───────────────────────────────────

    [Test]
    public async Task Measure_LLM_Completion_Latency()
    {
        Section("2. LLM COMPLETION LATENCY (NVIDIA NIM)");
        var settings = new JarvisSettings
        {
            Llm = LlmProvider.NvidiaNim,
            LlmModel = "nvidia/nemotron-3-super-120b-a120b",
            NvidiaApiKey = await ReadKeyAsync(),
            NvidiaBaseUrl = JarvisSettings.DefaultNvidiaBaseUrl,
        };

        var store = new JarvisSettingsStore(Logger, LocalSettingsFile.Empty);
        store.Apply(settings);

        var chat = new ChatClient(new DefaultHttpClientFactory(), store, Logger);
        var tools = new ToolRegistry(store, null!, Logger);
        var state = new AssistantStateHolder();
        var session = new AssistantSession(state, store, () => throw new InvalidOperationException(), null!, null!, Logger);

        var prompt = "What is the capital of France? Keep your answer under 15 words.";
        var history = new List<ChatMessage>();

        var sw = Stopwatch.StartNew();
        var (completion, failure, detail) = await chat.CompleteAsync(
            new ChatRequest
            {
                Model = settings.LlmModel,
                Messages = [ChatMessage.System(PersonaResolver.BuildSystemPrompt(settings)), ChatMessage.User(prompt)],
                Tools = tools.Definitions,
            },
            delta => { /* stream */ },
            CancellationToken.None);

        sw.Stop();
        var totalMs = sw.Elapsed.TotalMilliseconds;

        if (completion is not null)
        {
            Record("LLM FULL RESPONSE", TimeSpan.FromMilliseconds(totalMs),
                $"text={completion.Content.Length}chars first_token={completion.Content.Split(' ')[0]}");
            Logger.Information("LLM Response: {Text}", completion.Content);
        }
        else
        {
            Record("LLM FAILED", TimeSpan.FromMilliseconds(totalMs), $"{failure} {detail}");
        }

        // Try a smaller model
        var fastSettings = settings with { LlmModel = "nvidia/nemotron-3.5-lightning-30b-a3b" };
        store.Apply(fastSettings);

        var sw2 = Stopwatch.StartNew();
        var (completion2, failure2, detail2) = await chat.CompleteAsync(
            new ChatRequest
            {
                Model = fastSettings.LlmModel,
                Messages = [ChatMessage.System(PersonaResolver.BuildSystemPrompt(fastSettings)), ChatMessage.User(prompt)],
                Tools = tools.Definitions,
            },
            delta => { },
            CancellationToken.None);
        sw2.Stop();

        if (completion2 is not null)
        {
            Record("LLM FAST MODEL", TimeSpan.FromMilliseconds(sw2.Elapsed.TotalMilliseconds),
                $"text={completion2.Content.Length}chars");
            Logger.Information("Fast model response: {Text}", completion2.Content);
        }
        else
        {
            Record("LLM FAST FAILED", TimeSpan.FromMilliseconds(sw2.Elapsed.TotalMilliseconds), $"{failure2} {detail2}");
        }
    }

    // ── 3. Piper TTS latency ────────────────────────────────────────

    [Test]
    public async Task Measure_Piper_TTS_Latency()
    {
        Section("3. PIPER TTS LATENCY");
        var root = Path.Combine(Path.GetTempPath(), $"jarvis-latency-tts-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            var manager = new RuntimeManager(client, Logger, new RuntimePaths(root));
            await manager.EnsureComponentAsync(AssetCatalog.Piper, manager.Progress, cancellation.Token);

            var piper = new PiperSynthesizer(manager, Logger);
            Assert.That(piper.IsAvailable, Is.True);
            Assert.That(piper.Voices.Count, Is.GreaterThan(0));

            var testPhrases = new[]
            {
                "Hello.",
                "The quick brown fox jumps over the lazy dog.",
                "Jarvis, what is the capital of France and who was the first president of the United States?",
            };

            foreach (var phrase in testPhrases)
            {
                var wavPath = Path.Combine(root, $"tts-{Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant()}.wav");
                var sw = Stopwatch.StartNew();
                var ok = await piper.TrySynthesizeAsync(phrase, wavPath, piper.Voices[0].Name, cancellation.Token);
                sw.Stop();

                if (ok && File.Exists(wavPath))
                {
                    var dur = GetWavDuration(wavPath);
                    var size = new FileInfo(wavPath).Length;
                    var realTimeFactor = dur.TotalSeconds > 0
                        ? sw.Elapsed.TotalSeconds / dur.TotalSeconds
                        : 0;
                    Record($"Piper: '{phrase.Length} chars'", sw.Elapsed,
                        $"wav_dur={dur.TotalSeconds:F1}s size={size}B RTF={realTimeFactor:F2}x");
                }
                else
                {
                    Record($"Piper: '{phrase.Length} chars' FAILED", sw.Elapsed, "no output");
                }
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ── 3b. NIM STT latency (if API key available) ──────────────────

	[Test]
	public async Task Measure_NimSTT_Latency()
	{
		Section("3b. NVIDIA NIM SPEECH-TO-TEXT LATENCY");
		var settings = new JarvisSettings
		{
			Llm = LlmProvider.NvidiaNim,
			NvidiaApiKey = await ReadKeyAsync(),
			NvidiaBaseUrl = JarvisSettings.DefaultNvidiaBaseUrl,
		};

		var store = new JarvisSettingsStore(Logger, LocalSettingsFile.Empty);
		store.Apply(settings);

		using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
		using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
		var manager = new RuntimeManager(client, Logger, new RuntimePaths(Path.Combine(Path.GetTempPath(), $"jarvis-nim-{Guid.CreateVersion7():N}")));

		var nim = new NimSpeechTranscriber(manager, Logger);
		var available = await nim.ProbeAsync(cancellation.Token);
		if (!available)
		{
			Logger.Information("NIM STT not available, skipping.");
			Assert.Ignore("NIM STT endpoint not reachable");
			return;
		}

		// Generate a test WAV with piper
		await manager.EnsureComponentAsync(AssetCatalog.Piper, manager.Progress, cancellation.Token);
		var piper = new PiperSynthesizer(manager, Logger);
		var testWav = Path.Combine(Path.GetTempPath(), $"nim-test-{Guid.CreateVersion7():N}.wav");
		var sentence = "Jarvis, what is the capital of France?";
		var piperOk = await piper.TrySynthesizeAsync(sentence, testWav, piper.Voices[0].Name, cancellation.Token);
		Assert.That(piperOk, Is.True);

		// Warm up
		for (int i = 0; i < 2; i++)
		{
			_ = await nim.TranscribeAsync(testWav, "en", cancellation.Token);
		}

		// Measure
		var durations = new List<TimeSpan>();
		for (int i = 0; i < 5; i++)
		{
			var sw = Stopwatch.StartNew();
			var result = await nim.TranscribeAsync(testWav, "en", cancellation.Token);
			sw.Stop();
			durations.Add(sw.Elapsed);
			Record($"NIM STT run {i + 1}", sw.Elapsed, $"text='{result.Text}' ok={result.Ok}");
		}

		var avg = TimeSpan.FromTicks((long)durations.Average(t => t.Ticks));
		var min = durations.Min();
		var max = durations.Max();
		Logger.Information("");
		Logger.Information("NIM STT Summary: avg={Avg:F0}ms min={Min:F0}ms max={Max:F0}ms vs whisper avg=9800ms",
			avg.TotalMilliseconds, min.TotalMilliseconds, max.TotalMilliseconds);
		Record("NIM STT AVERAGE", avg, $"min={min.TotalMilliseconds:F0}ms max={max.TotalMilliseconds:F0}ms");
	}

	// ── 4. Full pipeline simulation ─────────────────────────────────

    [Test]
    public async Task Measure_Full_Pipeline_Simulation()
    {
        Section("4. FULL PIPELINE SIMULATION (estimated end-to-end)");
        var root = Path.Combine(Path.GetTempPath(), $"jarvis-latency-full-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));

            var manager = new RuntimeManager(client, Logger, new RuntimePaths(root));

            // Install whisper
            var whisperInstall = await manager.EnsureComponentAsync(AssetCatalog.Whisper, manager.Progress, cancellation.Token);
            Assert.That(whisperInstall.Installed, Is.True);

            // Install piper
            var piperInstall = await manager.EnsureComponentAsync(AssetCatalog.Piper, manager.Progress, cancellation.Token);
            Assert.That(piperInstall.Installed, Is.True);

            var transcriber = new WhisperTranscriber(manager, Logger);
            var piperSynth = new PiperSynthesizer(manager, Logger);

            var sentence = "Jarvis, what is the capital of France?";
            var wavPath = Path.Combine(root, "pipeline-test.wav");

            // Step 1: Synthesize test audio
            var t1 = Stopwatch.StartNew();
            await piperSynth.TrySynthesizeAsync(sentence, wavPath, piperSynth.Voices[0].Name, cancellation.Token);
            t1.Stop();
            var synthMs = t1.Elapsed.TotalMilliseconds;
            var wavDur = GetWavDuration(wavPath);
            Record("Step 1: TTS generate test audio", TimeSpan.FromMilliseconds(synthMs),
                $"wav_dur={wavDur.TotalSeconds:F1}s");

            // Step 2: Transcribe
            var t2 = Stopwatch.StartNew();
            var transcript = await transcriber.TranscribeAsync(wavPath, "en", cancellation.Token);
            t2.Stop();
            var transMs = t2.Elapsed.TotalMilliseconds;
            Record("Step 2: Whisper transcribe", TimeSpan.FromMilliseconds(transMs),
                $"text={transcript.Text}");

            // Step 3: LLM (using fast model for measurement)
            var settings = new JarvisSettings
            {
                Llm = LlmProvider.NvidiaNim,
                LlmModel = "nvidia/nemotron-3.5-lightning-30b-a3b",
                NvidiaApiKey = await ReadKeyAsync(),
                NvidiaBaseUrl = JarvisSettings.DefaultNvidiaBaseUrl,
            };
            var store = new JarvisSettingsStore(Logger, LocalSettingsFile.Empty);
            store.Apply(settings);
            var chat = new ChatClient(new DefaultHttpClientFactory(), store, Logger);
            var tools = new ToolRegistry(store, null!, Logger);

            var systemPrompt = PersonaResolver.BuildSystemPrompt(settings);
            var t3 = Stopwatch.StartNew();
            var (completion, failure, detail) = await chat.CompleteAsync(
                new ChatRequest
                {
                    Model = settings.LlmModel,
                    Messages = [ChatMessage.System(systemPrompt), ChatMessage.User(transcript.Text)],
                    Tools = tools.Definitions,
                },
                delta => { },
                CancellationToken.None);
            t3.Stop();
            var llmMs = t3.Elapsed.TotalMilliseconds;
            var llmText = completion?.Content ?? "(failed)";
            Record("Step 3: LLM generate response", TimeSpan.FromMilliseconds(llmMs),
                $"chars={llmText.Length} failure={failure}");

            // Step 4: TTS response
            var responseWav = Path.Combine(root, "response.wav");
            var t4 = Stopwatch.StartNew();
            var ttsOk = await piperSynth.TrySynthesizeAsync(llmText, responseWav, piperSynth.Voices[0].Name, cancellation.Token);
            t4.Stop();
            var ttsMs = t4.Elapsed.TotalMilliseconds;
            var ttsDur = ttsOk ? GetWavDuration(responseWav) : TimeSpan.Zero;
            Record("Step 4: Piper TTS response", TimeSpan.FromMilliseconds(ttsMs),
                $"wav_dur={ttsDur.TotalSeconds:F1}s ok={ttsOk}");

            // Summary
            var total = TimeSpan.FromMilliseconds(synthMs + transMs + llmMs + ttsMs);
            Logger.Information("");
            Logger.Information("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Logger.Information("  FULL PIPELINE TOTAL: {Total:F0}ms ({Total:F1}s)", total.TotalMilliseconds, total.TotalSeconds);
            Logger.Information("  Breakdown: synth={S:F0}ms + trans={T:F0}ms + llm={L:F0}ms + tts={P:F0}ms",
                synthMs, transMs, llmMs, ttsMs);
            Logger.Information("  LLM is {LlmPct:F0}% of total", llmMs / total.TotalMilliseconds * 100);
            Logger.Information("  Whisper is {WhitPct:F0}% of total", transMs / total.TotalMilliseconds * 100);
            Logger.Information("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Record("TOTAL END-TO-END", total,
                $"synth={synthMs:F0}ms trans={transMs:F0}ms llm={llmMs:F0}ms tts={ttsMs:F0}ms");
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ── 5. Wake word detector timing ────────────────────────────────

    [Test]
    public void Measure_WakeWordDetector_Silence_Detection()
    {
        Section("5. WAKE WORD DETECTOR SILENCE DETECTION");
        var detector = new WakeWordDetector(Logger, window: TimeSpan.FromSeconds(3));
        detector.Word = "jarvis";
        detector.Sensitivity = 0.06;

        var sampleRate = 16000;
        var silent = new float[16000 * 2]; // 2 seconds of silence
        var speech = new float[16000 * 3]; // 3 seconds
        for (int i = 0; i < speech.Length; i++)
        {
            // Simulate speech: modulated sine wave
            var t = (double)i / sampleRate;
            speech[i] = (float)(0.1 * Math.Sin(2 * Math.PI * 200 * t) *
                (1 + 0.5 * Math.Sin(2 * Math.PI * 5 * t))); // 200Hz with 5Hz amplitude mod
        }

        // Feed silence first, then speech, then silence
        var sw = Stopwatch.StartNew();
        detector.Buffer.Append(silent);
        detector.Buffer.Append(speech);
        detector.Buffer.Append(silent);
        sw.Stop();

        var samples = WakeWordDetector.Trim(detector.Buffer.TakeLast(5 * 16000), 16000);
        Record("Buffer fill + trim", sw.Elapsed,
            $"input=5s trimmed={samples.Length} samples ({samples.Length / (double)sampleRate:F2}s)");
        Record("Trimmed audio length", TimeSpan.FromMilliseconds(samples.Length / (double)sampleRate * 1000),
            $"raw would be 5000ms, trimmed to {samples.Length / (double)sampleRate * 1000:F0}ms");
    }

    // ── 6. WASAPI latency measurement ───────────────────────────────

    [Test]
    public void Measure_WASAPI_Capture_Latency()
    {
        Section("6. WASAPI CAPTURE LATENCY");
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            Logger.Information("Capture device: {Name}", device.FriendlyName);

            var sw = Stopwatch.StartNew();
            using var recorder = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithPollingSync()
                .Build();

            recorder.DataAvailable += (buf, flags, _, _) => { };
            recorder.StartRecording();
            Thread.Sleep(100);
            recorder.StopRecording();
            sw.Stop();
            Record("WASAPI start+stop overhead", sw.Elapsed, "device open + start + stop");

            // Measure actual capture latency
            var packets = new List<(long Timestamp, int Length)>();

            var start = Stopwatch.StartNew();
            using var rec2 = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithPollingSync()
                .Build();

            rec2.DataAvailable += (buf, flags, _, _) =>
            {
                packets.Add((DateTimeOffset.UtcNow.Ticks, buf.Length));
            };
            rec2.StartRecording();
            Thread.Sleep(200);
            rec2.StopRecording();
            start.Stop();

            if (packets.Count > 0)
            {
                var first = packets.First();
                var last = packets.Last();
                var captureSpan = TimeSpan.FromTicks(last.Timestamp - first.Timestamp);
                var expectedSpan = TimeSpan.FromMilliseconds(200);
                var latency = captureSpan - expectedSpan;
                Record("WASAPI capture latency (over 200ms)", captureSpan,
                    $"packets={packets.Count} first={(first.Timestamp - packets.First().Timestamp)/10000.0:F0}ms " +
                    $"latency_overhead={Math.Abs(latency.TotalMilliseconds):F0}ms");
            }
        }
        catch (Exception ex)
        {
            Record("WASAPI measurement FAILED", TimeSpan.Zero, ex.Message);
        }
    }

    // ── 7. Process spawn latency ────────────────────────────────────

    [Test]
    public void Measure_Process_Spawn_Latency()
    {
        Section("7. PROCESS SPAWN LATENCY (whisper/piper startup cost)");
        var processes = new[] { "cmd", "powershell" };
        foreach (var proc in processes)
        {
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = proc,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = "/c echo test",
            })!;
            p.WaitForExit();
            sw.Stop();
            Record($"Process spawn: {proc}", sw.Elapsed,
                $"exit={p.ExitCode} real_process_startup={sw.Elapsed.TotalMilliseconds:F0}ms");
        }        }

	// ── Helpers ─────────────────────────────────────────────────────

	private static async Task<string> ReadKeyAsync()
	{
		// Try multiple possible locations for the key file
		var candidates = new[]
		{
			Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nvapi-key.txt"),
			Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "nvapi-key.txt"),
			Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "nvapi-key.txt"),
			Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "nvapi-key.txt"),
			Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "nvapi-key.txt"),
			Path.Combine(TestContext.CurrentContext.TestDirectory, "nvapi-key.txt"),
			Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "nvapi-key.txt"),
			Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "nvapi-key.txt"),
			Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "nvapi-key.txt"),
			Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "nvapi-key.txt"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "nvapi-key.txt"),
			"C:\\Users\\Misu\\Desktop\\ideas\\JARVIS\\src\\src\\Jarvis.Plugin\\Speech\\nvapi-key.txt",
		};
		foreach (var path in candidates)
		{
			try
			{
				var fullPath = Path.GetFullPath(path);
				if (File.Exists(fullPath))
				{
					var key = File.ReadAllText(fullPath).Trim();
					if (!string.IsNullOrEmpty(key))
					{
						Logger.Information("Found API key at {Path}", fullPath);
						return key;
					}
				}
			}
			catch { }
		}
		Logger.Warning("API key file not found in any expected location");
		return "";
	}

    private static TimeSpan GetWavDuration(string path)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            return reader.TotalTime;
        }
        catch { return TimeSpan.Zero; }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private sealed class DefaultHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(120) };
    }
}
