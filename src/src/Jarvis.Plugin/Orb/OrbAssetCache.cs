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
			var resource = await _resources
				.RegisterAsync(name, bytes, "image/gif", cancellationToken)
				.ConfigureAwait(false);

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
			_logger.Warning(exception, "The orb asset for {State} could not be built.", key.State);
			_cache.TryRemove(key, out _);
			return null;
		}
	}
}