using System.Collections.Concurrent;
using Jarvis.Plugin.Core;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Model.Resources;
using Serilog;

namespace Jarvis.Plugin.Orb;

/// <summary>
/// Builds and caches one animated GIF per state, then registers each with the host's UI resource store.
/// Generating on demand rather than at startup means a fresh install answers its health check
/// immediately, and a state nobody has reached is never paid for.
/// </summary>
public sealed class OrbAssetCache(IUiResourceRegistry resources, ILogger logger)
{
	private const int IdleFrames = 18;
	private const int BusyFrames = 24;
	private const double QuietAmplitude = 0.12;

	private static readonly AssistantState[] AnimatedStates =
	[
		AssistantState.Idle,
		AssistantState.Listening,
		AssistantState.Thinking,
		AssistantState.Speaking,
		AssistantState.Executing,
		AssistantState.Confirming,
		AssistantState.Error,
	];

	private readonly IUiResourceRegistry _resources = resources;
	private readonly ILogger _logger = logger.ForContext<OrbAssetCache>();
	private readonly ConcurrentDictionary<AssistantState, Lazy<Task<UiResource?>>> _cache = new();

	public async Task<UiResource?> GetAsync(AssistantState target, OrbPalette palette, CancellationToken cancellationToken)
	{
		if (!AnimatedStates.Contains(target))
		{
			return null;
		}

		var lazy = _cache.GetOrAdd(target, key => new Lazy<Task<UiResource?>>(
			() => BuildAsync(key, palette, cancellationToken),
			LazyThreadSafetyMode.ExecutionAndPublication));

		return await lazy.Value.ConfigureAwait(false);
	}

	private async Task<UiResource?> BuildAsync(AssistantState target, OrbPalette palette, CancellationToken cancellationToken)
	{
		try
		{
			var frames = target == AssistantState.Idle ? IdleFrames : BusyFrames;
			var gif = AnimatedGif.Create(OrbFrameRenderer.Size, OrbFrameRenderer.Size);
			var amplitude = target is AssistantState.Listening or AssistantState.Speaking
				? QuietAmplitude
				: 0;

			for (var frame = 0; frame < frames; frame++)
			{
				cancellationToken.ThrowIfCancellationRequested();

				var phase = (frame / (double)frames) * Math.Tau;
				gif.AddFrame(OrbFrameRenderer.Render(target, phase, palette, amplitude));
			}

			var bytes = gif.Encode();
			var resource = await _resources
				.RegisterAsync($"orb-{target.ToString().ToLowerInvariant()}", bytes, "image/gif", cancellationToken)
				.ConfigureAwait(false);

			_logger.Information("Built orb asset for {State}: {Bytes} bytes, {Frames} frames.", target, bytes.Length, frames);
			return resource;
		}
		catch (OperationCanceledException)
		{
			_cache.TryRemove(target, out _);
			return null;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			// A missing orb must never take the widget down; the reader draws the placeholder instead.
			_logger.Warning(exception, "The orb asset for {State} could not be built.", target);
			_cache.TryRemove(target, out _);
			return null;
		}
	}
}