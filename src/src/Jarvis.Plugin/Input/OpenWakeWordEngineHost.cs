using System.Threading.Channels;
using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Runtime;
using Serilog;

namespace Jarvis.Plugin.Input;

/// <summary>
/// Owns the keyword engine's lifetime: models downloaded on demand, one engine per selected model, and
/// one ordered audio pump turning raw microphone packets into scores.
/// <para>
/// The pump is the reason this class exists. Inference takes milliseconds and the capture callback must
/// return before the next packet, so packets are queued on the capture thread and drained by a single
/// consumer that preserves their order - a keyword spotted in reordered audio is a keyword the model
/// never heard. The queue is bounded and lossy on purpose: a pump that falls behind must drop audio
/// rather than lag, because a wake word that fires on audio several seconds old is not responding to
/// the room.
/// </para>
/// </summary>
public sealed class OpenWakeWordEngineHost : IDisposable
{
	private const string MelspectrogramAsset = "wakeword-melspectrogram";
	private const string EmbeddingAsset = "wakeword-embedding";
	private const string DefaultModelAsset = "wakeword-model-hey-jarvis";

	/// <summary>
	/// Packets buffered ahead of the pump. 32 packets is about a third of a second at the device's packet
	/// rate: enough that a momentary scheduling hiccup loses nothing, small enough that a genuinely stuck
	/// pump cannot build latency the user can hear.
	/// </summary>
	private const int PacketQueueCapacity = 32;

	private readonly RuntimeManager _runtime;
	private readonly ILogger _logger;
	private readonly Lock _gate = new();

	private OpenWakeWordEngine? _engine;
	private Channel<float[]>? _packets;
	private CancellationTokenSource? _stopping;

	public OpenWakeWordEngineHost(RuntimeManager runtime, ILogger logger)
	{
		_runtime = runtime;
		_logger = logger.ForContext<OpenWakeWordEngineHost>();
	}

	/// <summary>Whether a built engine is listening. False whenever another engine is selected or off.</summary>
	public bool IsActive { get; private set; }

	/// <summary>A keyword score for one completed 80 ms chunk, raised on the pump thread.</summary>
	public event Action<float>? ScoreReady;

	/// <summary>
	/// One raw microphone packet, handed over on the capture thread. Cheap by design: a bounded
	/// <c>TryWrite</c>, no allocation, no waiting. When the queue is full the oldest packet is dropped,
	/// which costs a few milliseconds of context and buys a wake word that reacts to now.
	/// </summary>
	public void Accept(float[] samples)
	{
		if (!IsActive || _packets is not { } packets)
		{
			return;
		}

		packets.Writer.TryWrite(samples);
	}

	/// <summary>
	/// Brings the keyword spotter up: downloads whichever three pinned models the settings select,
	/// builds the engine, and starts the pump. Returns false when a model could not be installed, in
	/// which case the download manager has already raised an issue the user can act on.
	/// </summary>
	public async Task<bool> ActivateAsync(JarvisSettings settings, CancellationToken cancellationToken)
	{
		StopPump();

		var (assetId, exact) = ModelAssetFor(settings.WakeWord);

		if (!exact)
		{
			// Logged, not hidden: a user who set the word to "computer" must be able to find out why the
			// deck reacts to "hey jarvis" instead.
			_logger.Information(
				"No keyword model exists for '{Word}', so the wake word listens for 'hey jarvis' instead.",
				settings.WakeWord);
		}

		foreach (var asset in new[] { MelspectrogramAsset, EmbeddingAsset, assetId })
		{
			var result = await _runtime.EnsureAsync(asset, progress: null, cancellationToken).ConfigureAwait(false);

			if (!result.Installed)
			{
				// The manager has turned this into an issue with a retry button. Here it only has to
				// leave the wake word off rather than pretend to listen.
				_logger.Warning(
					"The wake word cannot start: {Asset} did not install. {Detail}",
					asset,
					result.Detail);
				return false;
			}
		}

		var preprocessors = Path.GetDirectoryName(_runtime.AssetPath(MelspectrogramAsset));
		var classifier = _runtime.AssetPath(assetId);

		if (preprocessors is null || classifier is null)
		{
			_logger.Warning("The wake word cannot start: the installed models could not be located on disk.");
			return false;
		}

		lock (_gate)
		{
			// One engine at a time, rebuilt per selected model. Selecting a different wake word is the
			// only thing that rebuilds; sensitivity and threshold live in the detector and need nothing
			// here beyond the model the settings already chose.
			_engine?.Dispose();
			_engine = new OpenWakeWordEngine(preprocessors, classifier, _logger);

			_packets = Channel.CreateBounded<float[]>(new BoundedChannelOptions(PacketQueueCapacity)
			{
				SingleReader = true,
				FullMode = BoundedChannelFullMode.DropOldest,
			});

			var reader = _packets.Reader;
			var engine = _engine;
			var stopping = (_stopping = new CancellationTokenSource()).Token;

			IsActive = true;

			_ = Task.Run(async () =>
			{
				try
				{
					await foreach (var packet in reader.ReadAllAsync(stopping).ConfigureAwait(false))
					{
						if (engine.Process(packet, MicrophoneMonitor.SampleRate) is { } score)
						{
							ScoreReady?.Invoke(score);
						}
					}
				}
				catch (OperationCanceledException)
				{
					// Deactivated, which is the pump's normal exit.
				}
				catch (Exception exception) when (exception is not OutOfMemoryException)
				{
					_logger.Warning(exception, "The wake word audio pump stopped unexpectedly.");
				}
			}, CancellationToken.None);
		}

		return true;
	}

	/// <summary>Stops the pump and frees the engine. Safe to call from any state.</summary>
	public void Dispose() => Deactivate();

	/// <summary>Stops the pump and frees the engine. Safe to call from any state.</summary>
	public void Deactivate()
	{
		StopPump();

		lock (_gate)
		{
			IsActive = false;
			_engine?.Dispose();
			_engine = null;
		}
	}

	/// <summary>
	/// Maps the configured wake word onto a pinned keyword model. Word matching is deliberately blunt:
	/// models exist for three phrases, and anything else falls back to the default one - logged, because
	/// a wake word that silently listens for a phrase the user never said is worse than either option.
	/// A custom word is the transcript engine's job, and the flow's description says so.
	/// </summary>
	internal static (string AssetId, bool Exact) ModelAssetFor(string wakeWord)
	{
		var letters = new string(wakeWord.ToLowerInvariant().Where(char.IsLetter).ToArray());

		return letters switch
		{
			"jarvis" or "heyjarvis" => ("wakeword-model-hey-jarvis", true),
			"alexa" => ("wakeword-model-alexa", true),
			"mycroft" or "heymycroft" => ("wakeword-model-hey-mycroft", true),
			_ => (DefaultModelAsset, false),
		};
	}

	private void StopPump()
	{
		Channel<float[]>? packets;

		lock (_gate)
		{
			IsActive = false;
			packets = _packets;
			_packets = null;
		}

		_stopping?.Cancel();

		// Completing the writer ends the pump's read loop without waiting for it; the cancellation token
		// covers the case where it is already blocked in a read. The pump touches nothing after either.
		packets?.Writer.TryComplete();
	}
}
