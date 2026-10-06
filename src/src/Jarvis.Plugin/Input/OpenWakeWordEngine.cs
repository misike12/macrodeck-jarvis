using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;

namespace Jarvis.Plugin.Input;

/// <summary>
/// The openWakeWord keyword spotter, running in-process on ONNX Runtime.
/// <para>
/// A faithful port of openWakeWord 0.5.1's streaming pipeline, because the keyword models are only as
/// accurate as the audio front-end they were trained behind. Every constant below is one of theirs: the
/// 80 ms chunk, the 480-sample lookback, the mel transform, the 76-frame embedding window, the 16-frame
/// classifier input, and the five-chunk warm-up during which predictions are discarded. The constants
/// were validated against a real "hey jarvis" recording before being written down here - the model
/// scored 0.999 at the phrase, and silence, sine tones, white noise and speech-like babble all scored
/// below 0.01 - which is the only reason this comment gets to say the port is faithful.
/// </para>
/// <para>
/// Audio arrives at the microphone's rate as floats in [-1, 1]. Everything downstream wants 16 kHz PCM
/// at int16 scale, so the conversion happens at the door and the streaming state below only ever sees
/// the one format.
/// </para>
/// </summary>
public sealed class OpenWakeWordEngine : IDisposable
{
	/// <summary>The number of samples one decision covers: 80 ms at 16 kHz, openWakeWord's unit of audio.</summary>
	public const int ChunkSamples = 1280;

	/// <summary>How many raw samples of the previous chunk the mel spectrogram looks back over.</summary>
	private const int LookbackSamples = 480;

	/// <summary>Mel frames produced per chunk: (1280 + 480) / 160 - 3. Wrong by one and the windows slide out of alignment.</summary>
	private const int MelFramesPerChunk = 8;

	/// <summary>The embedding model reads 76 mel frames and produces one feature row.</summary>
	private const int EmbeddingWindowFrames = 76;

	/// <summary>The embedding's output width, fixed by the model.</summary>
	private const int FeatureLength = 96;

	/// <summary>The keyword classifiers read the last 16 feature rows: 1.28 s of context.</summary>
	private const int ClassifierFrames = 16;

	/// <summary>
	/// The classifiers are muted for this many chunks after start, exactly as the reference does. The
	/// feature buffer is already seeded, so this mute only covers the first predictions the models would
	/// otherwise make against a context that is still mostly seed noise.
	/// </summary>
	private const int WarmupChunks = 5;

	/// <summary>Mel frames retained. 970 is the reference's ten seconds; the classifier only ever reads the last 76.</summary>
	private const int MelBufferMaxFrames = 970;

	/// <summary>Feature rows retained. The reference keeps 120; 16 are read at a time.</summary>
	private const int FeatureBufferMaxRows = 120;

	private readonly InferenceSession _mel;
	private readonly InferenceSession _embedding;
	private readonly InferenceSession _classifier;
	private readonly string _classifierInput;
	private readonly string _classifierOutput;
	private readonly ILogger _logger;
	private readonly Lock _gate = new();

	// Streaming state. Mel frames queue because they are enqueued eight at a time and read 76 at a time.
	// Feature rows list because the reference vstacks; a Queue would do, but the trim is list-shaped.
	private readonly Queue<float[]> _melFrames = new();
	private readonly List<float[]> _featureRows = [];
	private float[] _remainder = [];
	private float[] _lookback = [];
	private int _accumulated;
	private int _chunksProcessed;
	private bool _primed;

	/// <summary>Failures in a row before the engine declares itself dead and stops spending CPU on retries.</summary>
	private const int MaxConsecutiveFailures = 5;

	private int _consecutiveFailures;

	/// <summary>Volatile: read by the pump on every packet, written only under the gate on a failure.</summary>
	private volatile bool _dead;

	/// <summary>
	/// Whether the engine has given up. A session that cannot run - a model file replaced underneath it,
	/// a native fault - fails every chunk, and retrying at chunk rate means an exception and a stack
	/// trace eighty times a second. Five in a row is not bad luck; the engine stops and says so.
	/// </summary>
	public bool IsDead => _dead;

	/// <summary>How many raw samples of lookback the mel computation needs, carried across chunks.</summary>
	private const int RawBufferNeed = LookbackSamples + ChunkSamples;

	public OpenWakeWordEngine(string preprocessorDirectory, string classifierPath, ILogger logger)
	{
		_logger = logger.ForContext<OpenWakeWordEngine>();

		// One thread per session, sequential execution. These models are tiny, and ONNX Runtime's default
		// pool sizes itself to the machine's cores and spins its threads between runs: measured on this
		// machine, that idle spin burned tens of percent of CPU for milliseconds of actual work. The
		// reference Python package pins its sessions to one thread for the same reason.
		var options = new SessionOptions
		{
			IntraOpNumThreads = 1,
			InterOpNumThreads = 1,
			ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
		};

		_mel = new InferenceSession(Path.Combine(preprocessorDirectory, "melspectrogram.onnx"), options);
		_embedding = new InferenceSession(Path.Combine(preprocessorDirectory, "embedding_model.onnx"), options);
		_classifier = new InferenceSession(classifierPath, options);

		(_classifierInput, _classifierOutput) = (_classifier.InputMetadata.Keys.First(), _classifier.OutputMetadata.Keys.First());

		// The reference seeds its mel buffer with ones, not silence: the models were trained against a
		// front-end that starts full, and seeding with zeros scores a spoken word a hair differently.
		for (var frame = 0; frame < EmbeddingWindowFrames; frame++)
		{
			_melFrames.Enqueue(Ones);
		}

		// The reference seeds its feature buffer with 4 s of random noise rather than zeros, again because
		// that is what the training pipeline held before the first real audio arrived.
		var noise = new Random(20260202);
		var buffer = new float[16_000 * 4];

		for (var index = 0; index < buffer.Length; index++)
		{
			buffer[index] = noise.Next(-1000, 1000);
		}

		for (var offset = 0; offset + ChunkSamples <= buffer.Length; offset += ChunkSamples)
		{
			AcceptChunkCore(buffer[offset..(offset + ChunkSamples)]);
		}

		_chunksProcessed = 0;
		_primed = false;
	}

	/// <summary>The keyword models are muted until the buffers hold enough history to classify against.</summary>
	public bool IsPrimed
	{
		get
		{
			lock (_gate)
			{
				return _primed;
			}
		}
	}

	/// <summary>
	/// Builds the engine. <paramref name="preprocessorDirectory"/> holds the two shared models;
	/// <paramref name="classifierPath"/> is the full path of the keyword model to listen for, resolved by
	/// the caller from whichever model the settings selected.
	/// </summary>
	/// <para>
	/// Returns null while the engine is still priming: its buffers are filling, so any score it would
	/// report is about the warm-up noise and about nothing the microphone heard.
	/// </para>
	/// </summary>
	public float? Process(float[] samples48k, int sourceRate)
	{
		ArgumentNullException.ThrowIfNull(samples48k);

		var pcm = Downsample.ToInt16Scale(samples48k, sourceRate);

		lock (_gate)
		{
			if (_dead)
			{
				return null;
			}

			try
			{
				float? score = null;

				// The remainder carries the tail that is not a whole chunk yet, so a caller feeding 10 ms
				// packets still gets exactly one decision per 1280 accumulated samples rather than none.
				var combined = new float[_remainder.Length + pcm.Length];
				_remainder.CopyTo(combined, 0);
				pcm.CopyTo(combined, _remainder.Length);

				var offset = 0;
				while (offset + ChunkSamples <= combined.Length)
				{
					score = AcceptChunkCore(combined[offset..(offset + ChunkSamples)]);
					offset += ChunkSamples;
				}

				_remainder = combined[offset..];
				_consecutiveFailures = 0;
				return _primed ? score : null;
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				// A failed inference must not kill the caller: the pump's own loop publishes here, and the
				// wake word is a convenience. The chunk's state rolls forward by dropping the remainder, so
				// a transient fault re-primes; a persistent one stops the engine after a handful of tries
				// rather than spending an exception and a stack trace on every chunk forever.
				_remainder = [];
				_primed = false;
				_chunksProcessed = 0;
				_consecutiveFailures++;

				if (_consecutiveFailures >= MaxConsecutiveFailures)
				{
					_dead = true;

					_logger.Warning(
						exception,
						"The wake word engine failed {Count} inferences in a row and has stopped listening. "
						+ "Reconfigure the wake word to rebuild it.",
						_consecutiveFailures);
				}
				else
				{
					_logger.Warning(exception, "The wake word inference failed; the engine will re-prime.");
				}

				return null;
			}
		}
	}

	public void Reset()
	{
		lock (_gate)
		{
			_remainder = [];
			_lookback = [];
			_accumulated = 0;
			_chunksProcessed = 0;
			_primed = false;
			_melFrames.Clear();

			for (var frame = 0; frame < EmbeddingWindowFrames; frame++)
			{
				_melFrames.Enqueue(Ones);
			}

			_featureRows.Clear();

			// Re-seeded with the same deterministic noise the constructor used, so a reset engine is the
			// engine as first built rather than a subtly different one.
			var noise = new Random(20260202);
			var buffer = new float[16_000 * 4];

			for (var index = 0; index < buffer.Length; index++)
			{
				buffer[index] = noise.Next(-1000, 1000);
			}

			for (var offset = 0; offset + ChunkSamples <= buffer.Length; offset += ChunkSamples)
			{
				AcceptChunkCore(buffer[offset..(offset + ChunkSamples)]);
			}

			_chunksProcessed = 0;
			_primed = false;
		}
	}

	/// <summary>
	/// One whole chunk through the pipeline. Called with exactly <see cref="ChunkSamples"/> samples at
	/// 16 kHz int16 scale, which is the reference's own unit of work.
	/// </summary>
	private float? AcceptChunkCore(float[] chunk)
	{
		// The mel spectrogram is computed over this chunk plus the previous 480 samples, which is why the
		// reference accumulates before computing rather than computing per packet.
		var withLookback = new float[_lookback.Length + chunk.Length];
		_lookback.CopyTo(withLookback, 0);
		chunk.CopyTo(withLookback, _lookback.Length);
		AppendMelFrames(MelSpectrogram(withLookback));

		// Only the last 480 samples carry over, exactly the reference's lookback: a whole previous chunk
		// here would append thirteen mel frames per chunk instead of eight and slide every window after it
		// out of the alignment the models were trained with.
		_lookback = chunk[^LookbackSamples..];

		// One embedding window per chunk: the newest 76 mel frames. Read straight out of the queue -
		// snapshotting the whole 970-frame buffer per chunk was a megabyte of garbage a second for nothing.
		var embedding = Embed(_melFrames.Skip(Math.Max(0, _melFrames.Count - EmbeddingWindowFrames)));
		_featureRows.Add(embedding);

		if (_featureRows.Count > FeatureBufferMaxRows)
		{
			_featureRows.RemoveRange(0, _featureRows.Count - FeatureBufferMaxRows);
		}

		_chunksProcessed++;
		_primed = _chunksProcessed >= WarmupChunks;

		return _primed ? Classify() : null;
	}

	private void AppendMelFrames(float[] flatFrames)
	{
		for (var offset = 0; offset + 32 <= flatFrames.Length; offset += 32)
		{
			var frame = new float[32];
			Array.Copy(flatFrames, offset, frame, 0, 32);
			_melFrames.Enqueue(frame);
		}

		while (_melFrames.Count > MelBufferMaxFrames)
		{
			_melFrames.Dequeue();
		}
	}

	private float[] MelSpectrogram(float[] pcm)
	{
		var tensor = new DenseTensor<float>(pcm, [1, pcm.Length]);
		using var results = _mel.Run([NamedOnnxValue.CreateFromTensor(_mel.InputMetadata.Keys.First(), tensor)]);

		var output = results.First().AsTensor<float>();
		var frames = output.Dimensions[2];
		var flat = new float[frames * 32];

		for (var frame = 0; frame < frames; frame++)
		{
			for (var bin = 0; bin < 32; bin++)
			{
				// The reference transforms the ONNX mel output by x/10 + 2 to line it up with the TensorFlow
				// front-end the models were trained against. Skipping it scores a real phrase at 0.006.
				flat[frame * 32 + bin] = output[0, 0, frame, bin] / 10f + 2f;
			}
		}

		return flat;
	}

	private float[] Embed(IEnumerable<float[]> window)
	{
		var flat = new float[EmbeddingWindowFrames * 32];
		var offset = 0;

		foreach (var frame in window)
		{
			frame.CopyTo(flat, offset);
			offset += 32;
		}

		var tensor = new DenseTensor<float>(flat, [1, EmbeddingWindowFrames, 32, 1]);
		using var results = _embedding.Run([NamedOnnxValue.CreateFromTensor(_embedding.InputMetadata.Keys.First(), tensor)]);

		var output = results.First().AsTensor<float>().ToArray();
		var row = new float[FeatureLength];
		Array.Copy(output, row, FeatureLength);
		return row;
	}

	private float Classify()
	{
		var input = new float[ClassifierFrames * FeatureLength];
		var offset = 0;

		foreach (var row in _featureRows.TakeLast(ClassifierFrames))
		{
			row.CopyTo(input, offset);
			offset += FeatureLength;
		}

		var tensor = new DenseTensor<float>(input, [1, ClassifierFrames, FeatureLength]);
		using var results = _classifier.Run([NamedOnnxValue.CreateFromTensor(_classifierInput, tensor)]);

		return results.First().AsTensor<float>().ToArray()[0];
	}

	private static readonly float[] Ones = Enumerable.Repeat(1f, 32).ToArray();

	public void Dispose()
	{
		_mel.Dispose();
		_embedding.Dispose();
		_classifier.Dispose();
		GC.SuppressFinalize(this);
	}
}

/// <summary>
/// Converts microphone floats to the int16-scaled 16 kHz form the wake word models expect.
/// <para>
/// Split out because it is the one piece of the engine worth testing without ONNX Runtime: a conversion
/// that drifts by one bit of scale scores a spoken word differently from training, and nothing downstream
/// would say so.
/// </para>
/// </summary>
public static class Downsample
{
	/// <summary>
	/// Linear-interpolated resampling to 16 kHz, then a scale to int16's range. The interpolation is the
	/// same trade the rest of this plugin makes for speech: good enough at these ratios, and the models
	/// were trained on audio that had been through worse.
	/// </summary>
	public static float[] ToInt16Scale(float[] samples, int sourceRate)
	{
		if (samples.Length == 0)
		{
			return [];
		}

		const int targetRate = 16_000;
		float[] resampled;

		if (sourceRate == targetRate)
		{
			resampled = samples;
		}
		else
		{
			var length = (int)Math.Round(samples.Length * (double)targetRate / sourceRate, MidpointRounding.AwayFromZero);
			resampled = new float[length];
			var step = (double)sourceRate / targetRate;

			for (var index = 0; index < length; index++)
			{
				var position = index * step;
				var left = (int)position;

				if (left >= samples.Length - 1)
				{
					resampled[index] = samples[^1];
					continue;
				}

				var fraction = (float)(position - left);
				resampled[index] = (samples[left] * (1 - fraction)) + (samples[left + 1] * fraction);
			}
		}

		var result = new float[resampled.Length];

		for (var index = 0; index < resampled.Length; index++)
		{
			result[index] = Math.Clamp(resampled[index], -1f, 1f) * short.MaxValue;
		}

		return result;
	}
}
