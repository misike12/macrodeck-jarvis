using System.Threading.Channels;
using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Runtime;
using Serilog;

// The host implements Dispose through Deactivate below.

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
	/// <summary>Stops the pump. The engine itself is disposed by the pump that owns it.</summary>
	public void Dispose() => Deactivate();

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
			// A new engine per activation. The old one is NOT disposed here: its pump may be inside
			// engine.Process on another thread right now, and freeing an ONNX session mid-Run faults in
			// native code - the NullReferenceException on every reconfiguration came from exactly that.
			// Each pump owns the engine it was built with and disposes it when its loop exits.
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
						var score = engine.Process(packet, MicrophoneMonitor.SampleRate);

						if (engine.IsDead)
						{
							// The engine gave up; draining more packets would spend CPU on inference that
							// can never answer. The engine has already said why.
							break;
						}

						if (score is { } value)
						{
							ScoreReady?.Invoke(value);
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
				finally
				{
					// Owned here, and only ever freed here: the loop has left Process by the time it
					// runs, whether it left through the channel completing, the token cancelling, or
					// the engine dying.
					engine.Dispose();

					lock (_gate)
					{
						if (ReferenceEquals(_engine, engine))
						{
							IsActive = false;
						}
					}
				}
			}, CancellationToken.None);
		}

		return true;
	}

	/// <summary>Stops the pump and frees the engine. Safe to call from any state.</summary>
	public void Deactivate()
	{
		StopPump();

		lock (_gate)
		{
			IsActive = false;

			// The reference is dropped, not disposed: the pump that holds this engine disposes it when
			// its loop exits, which is the only moment disposal is provably outside Process.
			_engine = null;
		}
	}

/// <summary>
	/// The keyword models this build pins, and the words each one listens for.
	/// <para>
	/// A table rather than a switch because a custom wake word is the point of the setting, and a switch
	/// with a default arm is how "my word is not on the list" turns into "it listens for a phrase the user
	/// never said". The name is matched on letters only, because what the user types carries a greeting and
	/// punctuation more often than not: "hey jarvis", "Hey, Jarvis!" and "jarvis" are the same word here.
	/// </para>
	/// </summary>
	private static readonly Dictionary<string, string> KeywordModels =
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["jarvis"] = "wakeword-model-hey-jarvis",
			["heyjarvis"] = "wakeword-model-hey-jarvis",
			["alexa"] = "wakeword-model-alexa",
			["mycroft"] = "wakeword-model-hey-mycroft",
			["heymycroft"] = "wakeword-model-hey-mycroft",
		};

	/// <summary>
	/// Maps the configured wake word onto a pinned keyword model.
	/// <para>
	/// A word with no model returns the default one and <c>exact: false</c>, so the caller can say so rather
	/// than silently listening for a phrase the user never spoke. A custom word is still honoured: the caller
	/// is expected to fall back to the transcript engine, which matches any word, so setting "computer" gets
	/// "computer" rather than "hey jarvis".
	/// </para>
	/// </summary>
	internal static (string AssetId, bool Exact) ModelAssetFor(string wakeWord)
	{
		var letters = new string(wakeWord.ToLowerInvariant().Where(char.IsLetter).ToArray());

		return KeywordModels.TryGetValue(letters, out var asset)
			? (asset, true)
			: (DefaultModelAsset, false);
	}

	/// <summary>
	/// Whether a pinned model can actually hear the configured word. Read by the integration to decide whether
	/// to fall back to the transcript engine, so a custom wake word works instead of being quietly replaced.
	/// </summary>
	internal static bool HasModelFor(string wakeWord) => ModelAssetFor(wakeWord).Exact;

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
