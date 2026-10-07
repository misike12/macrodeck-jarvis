using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Input;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Tests for the openWakeWord front-end that do not need ONNX Runtime: the conversion that turns
/// microphone floats into the int16-scaled 16 kHz form the models expect.
/// <para>
/// The conversion is the one piece of the engine where a silent drift changes every score the model
/// produces, which is why it is pinned by itself rather than only through end-to-end scores.
/// </para>
/// </summary>
[TestFixture]
public class DownsampleTests
{
	[Test]
	public void A_48k_signal_is_resampled_to_16k()
	{
		// One second: 48000 samples in, 16000 out.
		var samples = new float[48_000];

		var result = Downsample.ToInt16Scale(samples, 48_000);

		Assert.That(result.Length, Is.EqualTo(16_000));
	}

	[Test]
	public void The_output_is_at_int16_scale_not_normalised()
	{
		// A full-scale sine must reach values near 32767 after conversion. A normalised float pipeline
		// (the probe's original mistake) scores a real phrase at 0.006, which is why this is pinned.
		var samples = new float[480];

		for (var index = 0; index < samples.Length; index++)
		{
			samples[index] = (float)Math.Sin(2 * Math.PI * 220 * index / 48_000.0);
		}

		var result = Downsample.ToInt16Scale(samples, 48_000);

		Assert.That(result.Select(value => Math.Abs(value)).Max(), Is.GreaterThan(30_000));
	}

	/// <summary>
	/// The scale multiplies by 32767 on both signs rather than keeping int16's asymmetric -32768: one
	/// least-significant bit is three thousandths of a percent of the range and means nothing to a model,
	/// while a special case for the negative extreme would be one more line to get wrong.
	/// </summary>
	[Test]
	public void Samples_beyond_full_scale_are_clamped()
	{
		var result = Downsample.ToInt16Scale([5f, -5f], 16_000);

		Assert.Multiple(() =>
		{
			Assert.That(result[0], Is.EqualTo(short.MaxValue));
			Assert.That(result[1], Is.EqualTo(-short.MaxValue));
		});
	}

	[Test]
	public void A_16k_signal_passes_through_unchanged()
	{
		var samples = new float[] { 1.5f, -0.5f, 0.25f };

		var result = Downsample.ToInt16Scale(samples, 16_000);

		Assert.That(
			result,
			Is.EqualTo(new float[] { short.MaxValue, -0.5f * short.MaxValue, 0.25f * short.MaxValue }));
	}

	[Test]
	public void Empty_input_produces_empty_output()
	{
		Assert.That(Downsample.ToInt16Scale([], 48_000), Is.Empty);
	}

	/// <summary>
	/// Every capture rate a Windows microphone actually negotiates, because the plugin asks the device for
	/// 48 kHz and a driver that refuses converts down, while a shared-mode endpoint opened elsewhere can
	/// arrive at any of these. A rate that is not handled does not throw: it produces the wrong number of
	/// samples, which the models score as silence.
	/// </summary>
	[TestCase(8_000)]
	[TestCase(11_025)]
	[TestCase(16_000)]
	[TestCase(22_050)]
	[TestCase(24_000)]
	[TestCase(32_000)]
	[TestCase(44_100)]
	[TestCase(48_000)]
	[TestCase(88_200)]
	[TestCase(96_000)]
	[TestCase(192_000)]
	public void Every_rate_a_microphone_negotiates_resamples_to_sixteen(int sourceRate)
	{
		// Fifty milliseconds at the device's own rate, which has to arrive as fifty milliseconds at 16 kHz.
		// The same duration rather than the same sample count, because the whole point is that the rate
		// changes the count and the duration is what the models care about.
		var samples = new float[sourceRate / 20];

		var result = Downsample.ToInt16Scale(samples, sourceRate);

		Assert.That(
			result.Length,
			Is.EqualTo(800).Within(2),
			$"{sourceRate} Hz produced {result.Length} samples for 50 ms where about 800 were expected");
	}

	/// <summary>
	/// A rate below the target genuinely grows the buffer, since it has to invent the samples in between.
	/// That is correct rather than a leak, but it has to be bounded: a device negotiating something absurd
	/// must not be able to make one packet allocate without limit.
	/// </summary>
	[Test]
	public void Upsampling_is_bounded_by_the_rate_ratio()
	{
		foreach (var rate in new[] { 1_000, 4_000, 8_000 })
		{
			var samples = new float[rate];

			var result = Downsample.ToInt16Scale(samples, rate);

			Assert.That(
				result.Length,
				Is.EqualTo(16_000).Within(2),
				$"{rate} Hz produced {result.Length} samples for one second");
		}
	}

	/// <summary>
	/// The signal has to survive the conversion, not merely the sample count. A rate handled by producing
	/// the right length of silence would pass every other test here and score the wake word at zero.
	/// </summary>
	[TestCase(8_000)]
	[TestCase(44_100)]
	[TestCase(48_000)]
	[TestCase(96_000)]
	[TestCase(192_000)]
	public void A_tone_survives_the_conversion_at_any_rate(int sourceRate)
	{
		var samples = new float[sourceRate];

		for (var index = 0; index < samples.Length; index++)
		{
			samples[index] = 0.8f * (float)Math.Sin(2 * Math.PI * 220 * index / sourceRate);
		}

		var result = Downsample.ToInt16Scale(samples, sourceRate);

		Assert.Multiple(() =>
		{
			Assert.That(result.Length, Is.GreaterThan(1_000), $"{sourceRate} Hz produced {result.Length} samples");
			Assert.That(
				result.Select(value => Math.Abs(value)).Max(),
				Is.GreaterThan(24_000),
				$"a full-scale tone arrived at {sourceRate} Hz with almost nothing left of it");
		});
	}

	/// <summary>
	/// A rate that is not a positive number has to be refused rather than dividing by it. The device is the
	/// only source of the rate, so this cannot happen in practice, and a NaN length would throw from deep
	/// inside a conversion rather than say what was wrong.
	/// </summary>
	[TestCase(0)]
	[TestCase(-48_000)]
	public void An_impossible_rate_is_refused(int sourceRate)
	{
		Assert.That(
			() => Downsample.ToInt16Scale([0.1f, 0.2f], sourceRate),
			Throws.TypeOf<ArgumentOutOfRangeException>());
	}
}

/// <summary>
/// The rate a capture device actually delivers, as far as the plugin can tell without hardware.
/// <para>
/// The plugin asks for 48 kHz because that is what its meter and its ring buffer were sized for, but shared
/// mode converts for most devices and converts nothing for several 96 kHz and 192 kHz interfaces. Everything
/// downstream therefore has to be told the rate the device agreed to rather than assuming the one requested:
/// treating a 96 kHz stream as 48 kHz halves its pitch, which still reads as speech on a meter and scores
/// the wake word at nothing at all.
/// </para>
/// </summary>
[TestFixture]
public class CaptureRateTests
{
	/// <summary>The rates a Windows capture endpoint is seen to negotiate.</summary>
	public static IEnumerable<int> NegotiatedRates =>
	[
		8_000, 11_025, 16_000, 22_050, 24_000, 32_000, 44_100, 48_000, 88_200, 96_000, 192_000,
	];

	/// <summary>Whatever the device hands over, the ring buffer has to be sized for that and no other.</summary>
	[TestCaseSource(nameof(NegotiatedRates))]
	public void The_wake_word_buffer_follows_the_capture_rate(int rate)
	{
		using var detector = new WakeWordDetector(RuntimeTestLog.Logger, TimeSpan.FromSeconds(1));

		detector.UseSampleRate(rate);

		Assert.Multiple(() =>
		{
			Assert.That(detector.Buffer.SampleRate, Is.EqualTo(rate));
			Assert.That(
				detector.Buffer.Capacity,
				Is.EqualTo(rate).Within(1),
				"the buffer does not hold one second of the device's own rate");
		});
	}

	/// <summary>A buffer holding one rate while claiming another trims the front off the word.</summary>
	[TestCase(96_000)]
	[TestCase(192_000)]
	public void A_retimed_buffer_keeps_the_audio_it_already_had(int rate)
	{
		using var detector = new WakeWordDetector(RuntimeTestLog.Logger, TimeSpan.FromSeconds(1));

		detector.Buffer.Append(new float[4_800]);
		detector.UseSampleRate(rate);

		// Four thousand eight hundred samples is 100 ms at 48 kHz and 25 ms at 192 kHz. Carried across
		// unchanged, so the count proves the audio survived and the rate proves it is now interpreted.
		Assert.Multiple(() =>
		{
			Assert.That(detector.Buffer.SampleRate, Is.EqualTo(rate));
			Assert.That(detector.Buffer.Count, Is.EqualTo(4_800));
		});
	}

	/// <summary>Retiming on every configuration must not keep discarding the audio the recogniser needs.</summary>
	[Test]
	public void Retiming_to_the_same_rate_twice_keeps_the_buffer()
	{
		using var detector = new WakeWordDetector(RuntimeTestLog.Logger, TimeSpan.FromSeconds(1));

		detector.UseSampleRate(44_100);
		detector.Buffer.Append(new float[1_000]);
		detector.UseSampleRate(44_100);

		Assert.That(detector.Buffer.Count, Is.EqualTo(1_000), "an unchanged rate discarded the audio anyway");
	}

	/// <summary>A rate that is not a positive number would size the buffer at nothing, so it is ignored.</summary>
	[TestCase(0)]
	[TestCase(-48_000)]
	public void An_impossible_capture_rate_leaves_the_buffer_alone(int rate)
	{
		using var detector = new WakeWordDetector(RuntimeTestLog.Logger, TimeSpan.FromSeconds(1));
		var before = detector.Buffer.SampleRate;

		detector.UseSampleRate(rate);

		Assert.That(detector.Buffer.SampleRate, Is.EqualTo(before));
	}

	/// <summary>
	/// The recording is handed to whisper at 16 kHz whatever the device gave us, so the rate the caller
	/// passes has to decide how much audio comes out. Getting it wrong does not fail loudly: it writes a
	/// clip that is the right length in bytes and plays back at the wrong speed.
	/// </summary>
	[TestCase(8_000)]
	[TestCase(44_100)]
	[TestCase(48_000)]
	[TestCase(96_000)]
	[TestCase(192_000)]
	public void A_recording_keeps_its_duration_at_whichever_rate_the_device_ran_at(int sourceRate)
	{
		// One hundred milliseconds at the device's own rate.
		var samples = new float[sourceRate / 10];
		var path = Path.Combine(Path.GetTempPath(), $"jarvis-rate-{sourceRate}.wav");

		try
		{
			var written = Jarvis.Plugin.Speech.UtteranceAudio.WriteWav(samples, sourceRate);
			File.Move(written, path, overwrite: true);

			var headerRate = BitConverter.ToInt32(File.ReadAllBytes(path), 24);
			var dataBytes = File.ReadAllBytes(path).Length;

			// 16-bit mono, so two bytes per sample.
			var samplesWritten = (dataBytes - 44) / 2;

			Assert.Multiple(() =>
			{
				Assert.That(headerRate, Is.EqualTo(16_000), "whisper is not given the rate it expects");
				Assert.That(
					samplesWritten,
					Is.EqualTo(1_600).Within(40),
					$"{sourceRate} Hz produced {samplesWritten} samples for 100 ms");
			});
		}
		finally
		{
			try
			{
				File.Delete(path);
			}
			catch (IOException)
			{
				// A leftover temp file is not worth a failed test.
			}
		}
	}

	/// <summary>The rate the plugin asks for has to be one Windows can actually be asked for.</summary>
	[Test]
	public void The_requested_capture_rate_is_one_windows_negotiates()
	{
		Assert.That(NegotiatedRates, Contains.Item(Jarvis.Plugin.Audio.MicrophoneMonitor.SampleRate));
	}
}

/// <summary>
/// The keyword engine against the real pinned models, when they are installed. This is the check that
/// the port scores the phrase high and everything else low; without the models installed the fixture
/// reports inconclusive, because asserting the maths against nothing would be theatre.
/// </summary>
[TestFixture]
public class OpenWakeWordEngineLiveTests
{
	/// <summary>
	/// The temp-path runtime root the plugin uses wherever the host has not supplied a data directory.
	/// The tests do not download: they verify what a real install produces, and the download itself is
	/// covered by the live download fixture.
	/// </summary>
	private static string? ModelDirectory()
	{
		var candidate = Path.Combine(Path.GetTempPath(), "jarvis-runtime", "wakeword", "preprocessors");

		return Directory.Exists(candidate) ? candidate : null;
	}

	private static string? ClassifierPath()
	{
		var candidate = Path.Combine(
			Path.GetTempPath(), "jarvis-runtime", "wakeword", "hey-jarvis", "hey_jarvis_v0.1.onnx");

		return File.Exists(candidate) ? candidate : null;
	}

	private static OpenWakeWordEngine? NewEngine(out string reason)
	{
		reason = string.Empty;
		var directory = ModelDirectory();
		var classifier = ClassifierPath();

		if (directory is null || classifier is null)
		{
			reason = "the openWakeWord models are not installed";
			return null;
		}

		return new OpenWakeWordEngine(directory, classifier, RuntimeTestLog.Logger);
	}

	/// <summary>A 440 Hz tone at conversational loudness. A keyword model that scores this even 0.3 would interrupt music all day.</summary>
	private static float[] Tone(int hz, double seconds, float amplitude)
	{
		var samples = new float[(int)(48_000 * seconds)];

		for (var index = 0; index < samples.Length; index++)
		{
			samples[index] = amplitude * (float)Math.Sin(2 * Math.PI * hz * index / 48_000.0);
		}

		return samples;
	}

	[Test]
	public void The_engine_primes_after_enough_chunks_and_not_before()
	{
		if (NewEngine(out var reason) is not { } engine)
		{
			Assert.Ignore($"skipped: {reason}");
			return;
		}

		using (engine)
		{
			Assert.That(engine.IsPrimed, Is.False, "freshly built, the engine is still priming");

			var silence = new float[OpenWakeWordEngine.ChunkSamples * 48_000 / 16_000];

			var primed = false;

			for (var chunk = 0; chunk < 40; chunk++)
			{
				engine.Process(silence, 48_000);
				primed |= engine.IsPrimed;
			}

			Assert.That(primed, Is.True, "forty chunks of audio never primed the engine");
		}
	}

	[Test]
	public void Silence_scores_below_any_usable_threshold()
	{
		if (NewEngine(out var reason) is not { } engine)
		{
			Assert.Ignore($"skipped: {reason}");
			return;
		}

		using (engine)
		{
			var silence = new float[48_000 * 6];
			var highest = 0f;

			for (var offset = 0; offset + 4800 <= silence.Length; offset += 4800)
			{
				var score = engine.Process(silence[offset..(offset + 4800)], 48_000);

				if (score is { } value)
				{
					highest = Math.Max(highest, value);
				}
			}

			Assert.That(highest, Is.LessThan(0.3f), $"silence scored {highest:0.000}");
		}
	}

	[Test]
	public void Tones_and_noise_score_below_any_usable_threshold()
	{
		if (NewEngine(out var reason) is not { } engine)
		{
			Assert.Ignore($"skipped: {reason}");
			return;
		}

		using (engine)
		{
			foreach (var signal in new[] { Tone(440, 6, 0.4f), Tone(1000, 6, 0.4f), Noise(6, 0.3f) })
			{
				var highest = 0f;

				for (var offset = 0; offset + 4800 <= signal.Length; offset += 4800)
				{
					var score = engine.Process(signal[offset..(offset + 4800)], 48_000);

					if (score is { } value)
					{
						highest = Math.Max(highest, value);
					}
				}

				Assert.That(highest, Is.LessThan(0.3f), $"a non-speech signal scored {highest:0.000}");
			}
		}
	}

	/// <summary>
	/// The positive case, on synthesized speech. A human recording cannot be pinned in the repository, so
	/// the stand-in is the same SAPI voice Windows ships, which is honest about its limits: it proves the
	/// port scores the phrase through the real models, not that every human voice on earth is recognised.
	/// The rejection tests above are the half that catches a port gone subtly wrong; this is the half that
	/// catches one gone deaf.
	/// </summary>
	[Test]
	public async Task A_spoken_phrase_scores_high()
	{
		if (NewEngine(out var reason) is not { } engine)
		{
			Assert.Ignore($"skipped: {reason}");
			return;
		}

		using (engine)
		{
			var path = Path.Combine(Path.GetTempPath(), $"jarvis-ww-{Guid.CreateVersion7():N}.wav");

			try
			{
				if (!TrySynthesizePhrase(path))
				{
					Assert.Ignore("skipped: no SAPI voice was available to synthesize the phrase");
					return;
				}

				var pcm = ReadWav(path);
				var highest = 0f;

				// Fed in 100 ms packets the way the microphone delivers them, through the same conversion
				// path the engine applies to live audio.
				for (var offset = 0; offset + 1600 <= pcm.Length; offset += 1600)
				{
					if (engine.Process(pcm[offset..(offset + 1600)], 16_000) is { } score)
					{
						highest = Math.Max(highest, score);
					}
				}

				Assert.That(
					highest,
					Is.GreaterThan(0.5f),
					$"the synthesized phrase never scored above the threshold (peak {highest:0.000})");
			}
			finally
			{
				try
				{
					File.Delete(path);
				}
				catch (IOException)
				{
					// A leftover temp file is not worth a failed test.
				}
			}
		}
	}

	/// <summary>Renders the phrase with the machine's default SAPI voice, at 16 kHz mono.</summary>
	private static bool TrySynthesizePhrase(string path)
	{
		var script =
			"Add-Type -AssemblyName System.Speech; " +
			"$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
			"$f = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, " +
			"[System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, " +
			"[System.Speech.AudioFormat.AudioChannel]::Mono); " +
			"$s.SetOutputToWaveFile('" + path.Replace("'", "''") + "', $f); " +
			"$s.Speak('Hey Jarvis. Hey Jarvis, what time is it.'); " +
			"$s.Dispose()";

		try
		{
			using var process = System.Diagnostics.Process.Start(
				new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -NonInteractive -Command " + script)
				{
					UseShellExecute = false,
					CreateNoWindow = true,
				});

			if (process is null || !process.WaitForExit(30_000) || process.ExitCode != 0 || !File.Exists(path))
			{
				return false;
			}

			return true;
		}
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}

	/// <summary>Reads a 16-bit PCM WAV into normalized floats, which is what the engine's door expects.</summary>
	private static float[] ReadWav(string path)
	{
		var bytes = File.ReadAllBytes(path);

		for (var position = 12; position + 8 <= bytes.Length;)
		{
			var id = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
			var size = BitConverter.ToInt32(bytes, position + 4);

			if (id == "data")
			{
				var count = size / 2;
				var samples = new float[count];

				for (var index = 0; index < count; index++)
				{
					samples[index] = BitConverter.ToInt16(bytes, position + 8 + index * 2) / 32768f;
				}

				return samples;
			}

			position += 8 + size + (size & 1);
		}

		throw new InvalidDataException($"{path} has no data chunk.");
	}

	[Test]
	public void A_reset_engine_behaves_like_a_fresh_one()
	{
		if (NewEngine(out var reason) is not { } engine)
		{
			Assert.Ignore($"skipped: {reason}");
			return;
		}

		using (engine)
		{
			var silence = new float[48_000];

			for (var packet = 0; packet < 100; packet++)
			{
				engine.Process(silence, 48_000);
			}

			engine.Reset();

			Assert.That(engine.IsPrimed, Is.False, "reset left the engine believing it had history");
		}
	}

	private static float[] Noise(double seconds, float amplitude)
	{
		var random = new Random(9);
		var samples = new float[(int)(48_000 * seconds)];

		for (var index = 0; index < samples.Length; index++)
		{
			samples[index] = amplitude * (random.NextSingle() * 2f - 1f);
		}

		return samples;
	}
}

/// <summary>
/// The detector's score path: fire after a run of confident chunks, not on one, and not while cooling
/// down. The scores are handed over the way the pump does, because what is under test here is the
/// counting and the cooldown, not the model - the model's own scores are asserted in the fixture above.
/// </summary>
[TestFixture]
public class WakeWordScorePathTests
{
	private static WakeWordDetector NewDetector() =>
		new(RuntimeTestLog.Logger, TimeSpan.FromSeconds(2)) { Enabled = true };

	[Test]
	public void A_run_of_three_confident_chunks_fires_once_and_then_cools_down()
	{
		using var detector = NewDetector();

		var fired = 0;
		detector.Detected += () => fired++;

		for (var chunk = 0; chunk < 10; chunk++)
		{
			detector.OfferScore(0.9f);
		}

		Assert.That(fired, Is.EqualTo(1), "the run fired more than once");
	}

	[Test]
	public void One_confident_chunk_does_not_fire()
	{
		using var detector = NewDetector();

		var fired = 0;
		detector.Detected += () => fired++;

		detector.OfferScore(0.9f);

		Assert.That(fired, Is.Zero, "a single confident chunk started a turn");
	}

	[Test]
	public void A_chunk_below_the_threshold_restarts_the_run()
	{
		using var detector = NewDetector();

		var fired = 0;
		detector.Detected += () => fired++;

		// Two confident, one miss, two confident: never three in a row, so never a fire.
		detector.OfferScore(0.9f);
		detector.OfferScore(0.9f);
		detector.OfferScore(0.1f);
		detector.OfferScore(0.9f);
		detector.OfferScore(0.9f);

		Assert.That(fired, Is.Zero, "a run broken by a miss still fired");
	}

	[Test]
	public void A_disabled_detector_never_fires()
	{
		using var detector = NewDetector();
		detector.Enabled = false;

		var fired = 0;
		detector.Detected += () => fired++;

		for (var chunk = 0; chunk < 10; chunk++)
		{
			detector.OfferScore(0.9f);
		}

		Assert.That(fired, Is.Zero);
	}

	[Test]
	public async Task The_transcript_path_still_works_alongside_the_score_path()
	{
		// The transcript engine remains selectable and shares the same event, so it must be unaffected by
		// the score path's arrival.
		using var detector = new WakeWordDetector(RuntimeTestLog.Logger, TimeSpan.FromSeconds(1))
		{
			Enabled = true,
			Sensitivity = 0.2,
		};

		detector.Buffer.Append(Samples.Speech());
		detector.Recognizer = (_, _) => Task.FromResult<string?>("hey jarvis");

		var fired = 0;
		detector.Detected += () => fired++;

		await detector.OfferAsync(0.9, TestContext.CurrentContext.CancellationToken);
		await Task.Delay(200, TestContext.CurrentContext.CancellationToken);
		await detector.OfferAsync(0.0, TestContext.CurrentContext.CancellationToken);

		Assert.That(fired, Is.EqualTo(1), "the transcript engine stopped working when the score path was added");
	}
}
