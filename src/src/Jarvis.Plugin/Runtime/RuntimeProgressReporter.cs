namespace Jarvis.Plugin.Runtime;

/// <summary>
/// The current state of a download, in the shape a widget or a variable can read. Progress is published
/// rather than polled because nothing here should have to ask "is something downloading" to stay correct.
/// </summary>
public sealed record RuntimeProgress(string AssetId, double Percent, string Phase, bool Active)
{
	public static RuntimeProgress Idle { get; } = new(string.Empty, 0, "idle", false);
}

/// <summary>
/// Bridges download callbacks to whatever is showing progress. It is an <see cref="IProgress{T}"/> so it
/// can be handed straight to the downloader, and it keeps the last value so a variable read or a widget
/// opened mid-download reports something true.
/// </summary>
public sealed class RuntimeProgressReporter : IProgress<DownloadProgress>
{
	private readonly Lock _gate = new();

	private RuntimeProgress _current = RuntimeProgress.Idle;

	public event Action<RuntimeProgress>? Changed;

	public RuntimeProgress Current
	{
		get
		{
			lock (_gate)
			{
				return _current;
			}
		}
	}

	public void Report(DownloadProgress value)
	{
		var next = new RuntimeProgress(
			value.AssetId,
			// A total the server did not send cannot produce a percentage, and reporting zero for an
			// unknown total would look like a stalled download rather than an unsized one.
			value.Fraction is { } fraction ? Math.Round(fraction * 100) : 0,
			value.Phase,
			Active: true);

		bool changed;

		lock (_gate)
		{
			changed = _current != next;
			_current = next;
		}

		if (changed)
		{
			Changed?.Invoke(next);
		}
	}

	/// <summary>Marks the current download finished. Called when an install settles either way.</summary>
	public void Finish(string assetId)
	{
		RuntimeProgress next;

		lock (_gate)
		{
			if (!_current.Active && _current.AssetId == assetId)
			{
				return;
			}

			next = new RuntimeProgress(assetId, 100, "done", Active: false);
			_current = next;
		}

		Changed?.Invoke(next);
	}
}
