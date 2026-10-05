using System.Collections.Concurrent;
using Jarvis.Plugin.Core;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Model.Resources;
using Serilog;

namespace Jarvis.Plugin.Orb;

/// <summary>
/// Builds and caches one animated GIF per state, then registers each with the host's UI resource store.
/// <para>
/// The cache key has to name everything that changes a pixel. It used to leave out the four settings a user
/// can change from the widget configuration, so turning the glow off or asking for a different number of
/// rings still served the frames built for the previous settings, and the setting appeared to do nothing.
/// </para>
/// </summary>
public sealed class OrbAssetCache(IUiResourceRegistry resources, ILogger logger)
{
	private const int IdleFrames = 18;
	private const int BusyFrames = 24;

	private static readonly AssistantState[] AnimatedStates =
	[
		AssistantState.Idle,
		AssistantState.Listening,
		AssistantState.Thinking,
		AssistantState.Speaking,
		AssistantState.Executing,
		AssistantState.Confirming,
		AssistantState.Error,

		// Every state, deliberately. Unavailable is the state a freshly installed plugin is in, because the
		// configuration flow has not been completed, so leaving it out made the widget blank for exactly the
		// user who had just installed it and had least context for why. The renderer already draws it as a
		// dimmed, slow core; the guard below only ever had the seven states above in it.
		AssistantState.Unavailable,
	];

	private readonly IUiResourceRegistry _resources = resources;
	private readonly ILogger _logger = logger.ForContext<OrbAssetCache>();

	/// <summary>
	/// Keyed on state, preset, palette, the settings that change the picture, and an amplitude band rather
	/// than on state alone. The key has to include all of them: keying on state alone was why every preset
	/// produced the same picture, because whichever preset was asked for first filled the cache and the rest
	/// were served that one.
	/// </summary>
	private readonly ConcurrentDictionary<AssetKey, Lazy<Task<UiResource?>>> _cache = new();

	/// <summary>
/// One upload at a time, across every session in the process.
/// <para>
/// The host answers overlapping asset uploads with QUEUE_OVERFLOW, and an upload that fails leaves no image
/// behind, which the next state change rebuilds and re-uploads, which fails again. That loop ran for minutes
/// with a blank widget and one warning every two seconds. Serialising the uploads is what stops the first
/// half, and a cooldown after a failure is what stops the second: without either, a bigger asset is enough to
/// tip a working widget into never drawing again.
/// </para>
/// </summary>
	private static readonly SemaphoreSlim Uploads = new(1, 1);

	/// <summary>
	/// Keys whose upload last failed, and when. A failed upload must not be retried on the next state change,
	/// or the failure above repeats as fast as the assistant changes state.
	/// </summary>
	private readonly ConcurrentDictionary<AssetKey, long> _uploadCooldowns = new();

	private static readonly TimeSpan UploadCooldown = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan UploadRetryDelay = TimeSpan.FromMilliseconds(400);
	private const int MaxUploadAttempts = 3;

	private readonly record struct AssetKey(
		AssistantState State,
		OrbPreset Preset,
		string Accent,
		string Core,
		int AmplitudeBand,
		int RingCount,
		int RingSpeed,
		bool RingRotation,
		bool Glow);

	public async Task<UiResource?> GetAsync(
		AssistantState target,
		OrbPalette palette,
		double amplitude,
		OrbPreset preset,
		CancellationToken cancellationToken,
		int ringCount = 3,
		double ringSpeed = 1.0,
		bool ringRotation = true,
		bool glow = true)
	{
		if (!AnimatedStates.Contains(target))
		{
			return null;
		}

		// Amplitude is quantised into a small number of bands. A distinct asset per amplitude value would
		// rebuild the animation continuously as a voice rises and falls, which is both expensive and
		// invisible: the difference between two adjacent levels cannot be seen at this size.
		var band = Band(amplitude);
		var speedBand = (int)Math.Round(Math.Clamp(ringSpeed, 0, 4) * 10);
		var key = new AssetKey(
			target, preset, Accent(palette), Core(palette), band, ringCount, speedBand, ringRotation, glow);

		if (_uploadCooldowns.TryGetValue(key, out var failedAt)
			&& Environment.TickCount64 - failedAt < (long)UploadCooldown.TotalMilliseconds)
		{
			return null;
		}

		var lazy = _cache.GetOrAdd(key, entry => new Lazy<Task<UiResource?>>(
			() => BuildAsync(entry, palette, cancellationToken),
			LazyThreadSafetyMode.ExecutionAndPublication));

		return await lazy.Value.ConfigureAwait(false);
	}

	private const int AmplitudeBands = 4;

	/// <summary>
	/// Quiet is its own band so that silence looks like silence rather than like a very quiet voice, and
	/// loud is its own band so a shout is visibly at the top rather than pinned there.
	/// </summary>
	private static int Band(double amplitude) => amplitude switch
	{
		< 0.08 => 0,
		< 0.30 => 1,
		< 0.65 => 2,
		_ => 3,
	};

	private static string Accent(OrbPalette palette) =>
		$"{palette.Accent.R:F3},{palette.Accent.G:F3},{palette.Accent.B:F3}";

	private static string Core(OrbPalette palette) =>
		$"{palette.Core.R:F3},{palette.Core.G:F3},{palette.Core.B:F3}";

	private async Task<UiResource?> BuildAsync(AssetKey key, OrbPalette palette, CancellationToken cancellationToken)
	{
		try
		{
			var frames = key.State == AssistantState.Idle ? IdleFrames : BusyFrames;
			var gif = AnimatedGif.Create(OrbFrameRenderer.Size, OrbFrameRenderer.Size);

			// The middle of the band, not its floor, so a band renders as the level it represents.
			var amplitude = key.AmplitudeBand switch
			{
				0 => 0.0,
				1 => 0.19,
				2 => 0.47,
				_ => 0.82,
			};

			for (var frame = 0; frame < frames; frame++)
			{
				cancellationToken.ThrowIfCancellationRequested();

				var phase = (frame / (double)frames) * Math.Tau;

				gif.AddFrame(OrbFrameRenderer.Render(
					key.State,
					phase,
					palette,
					amplitude,
					key.Preset,
					key.RingCount,
					key.RingSpeed / 10.0,
					key.RingRotation,
					key.Glow));
			}

			var bytes = gif.Encode();
			var name = $"orb-{key.State.ToString().ToLowerInvariant()}-{key.Preset.ToString().ToLowerInvariant()}-{key.AmplitudeBand}-{key.RingCount}-{key.RingSpeed}-{(key.RingRotation ? 'r' : 's')}-{(key.Glow ? 'g' : 'n')}";
			var resource = await UploadAsync(name, bytes, cancellationToken).ConfigureAwait(false);

			_logger.Debug(
				"Built orb asset for {State}/{Preset} at band {Band} with {Rings} rings: {Bytes} bytes, {Frames} frames.",
				key.State,
				key.Preset,
				key.AmplitudeBand,
				key.RingCount,
				bytes.Length,
				frames);
			return resource;
		}
		catch (OperationCanceledException)
		{
			_cache.TryRemove(key, out _);
			return null;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			// A missing orb must never take the widget down; the reader draws the placeholder instead.
			// The cooldown is recorded too, or the next state change rebuilds and re-uploads immediately and
			// a full queue stays full for as long as the assistant keeps changing state.
			_logger.Warning(exception, "The orb asset for {State} could not be built.", key.State);
			_cache.TryRemove(key, out _);
			NoteUploadFailure(key);
			return null;
		}
	}

	/// <summary>
	/// Registers one asset, alone and with patience.
	/// <para>
	/// The semaphore is what keeps the host from answering QUEUE_OVERFLOW: uploads from every session in this
	/// process go through one gate, so two state changes in quick succession cannot put two uploads on the
	/// wire at once. The retries are for the queue being momentarily full rather than empty; the cooldown a
	/// caller records afterwards is for it being full for a while. Both are bounded, because an upload that
	/// cannot complete is not worth wedging a widget open over.
	/// </para>
	/// </summary>
	private async Task<UiResource?> UploadAsync(string name, byte[] bytes, CancellationToken cancellationToken)
	{
		await Uploads.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			for (var attempt = 1; ; attempt++)
			{
				try
				{
					return await _resources
						.RegisterAsync(name, bytes, "image/gif", cancellationToken)
						.ConfigureAwait(false);
				}
				catch (Exception)
					when (attempt < MaxUploadAttempts)
				{
					_logger.Debug(
						"Asset upload attempt {Attempt} was refused; waiting {Delay} before retrying.",
						attempt,
						UploadRetryDelay);

					await Task.Delay(UploadRetryDelay, cancellationToken).ConfigureAwait(false);
				}
			}
		}
		finally
		{
			Uploads.Release();
		}
	}

	private void NoteUploadFailure(AssetKey key) =>
		_uploadCooldowns[key] = Environment.TickCount64;
}
