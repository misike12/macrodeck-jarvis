using Jarvis.Plugin.Core;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Resources;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using Serilog;

namespace Jarvis.Plugin.Orb;

/// <summary>
/// One open orb. A state change swaps the animated asset and the text, which are property patches on
/// nodes the view already holds; the ring sweep is a timer writing one scalar. Nothing here rebuilds a
/// tree, so a busy conversation costs a handful of patches rather than a resubscribe.
/// </summary>
internal sealed class OrbUiSession : IUiSession
{
	/// <summary>
	/// Twenty-five patches a second. The host refills its bucket at thirty and terminates a session
	/// that overshoots, so the sweep deliberately leaves a fifth of the ceiling as headroom.
	/// </summary>
	private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(40);

	private readonly OrbWidgetData _data;
	private readonly OrbAssetCache _assets;
	private readonly CancellationToken _cancellationToken;
	private readonly UiState<AssistantState> _orbState;
	private readonly UiState<UiResource?> _core;
	private readonly UiState<double> _sweep;
	private readonly UiState<string> _reply;
	private readonly UiState<string> _transcript;
	private readonly UiView _view;
	private readonly IDisposable _subscription;
	private readonly Timer _timer;

	public OrbUiSession(
		OrbWidgetData data,
		AssistantStateHolder state,
		OrbAssetCache assets,
		CancellationToken cancellationToken)
	{
		_data = data;
		_assets = assets;
		_cancellationToken = cancellationToken;

		_orbState = new UiState<AssistantState>(AssistantState.Idle);
		_core = new UiState<UiResource?>(null);
		_sweep = new UiState<double>(0);
		_reply = new UiState<string>(string.Empty);
		_transcript = new UiState<string>(string.Empty);

		var surface = new UiSurface
		{
			Kind = UiSurfaceKinds.Widget,
			SessionMode = UiSessionModes.Shared,
		};

_view = new UiView(surface, OrbView.Build(data, _orbState, _core, _sweep, _reply, _transcript));

		_view.HandlerFaulted += (_, args) =>
			Faulted?.Invoke(this, new UiSessionFaultedEventArgs(args.NodeId, args.Exception));

		_subscription = state.Subscribe(OnSnapshot);
		_timer = new Timer(OnSweep, null, SweepInterval, SweepInterval);
	}

	public event EventHandler? Changed;

	public event EventHandler<UiSessionFaultedEventArgs>? Faulted;

	public UiTree BuildTree() => _view.Tree;

	public IReadOnlyList<UiPatch> DrainPatches() => _view.DrainPatches();

	public void Dispatch(UiEvent uiEvent) => _view.Dispatch(uiEvent);

	private void OnSnapshot(AssistantSnapshot snapshot)
	{
		_orbState.Set(snapshot.State);
		_reply.Set(snapshot.Reply);
		_transcript.Set(snapshot.Transcript);

		Changed?.Invoke(this, EventArgs.Empty);

		var target = snapshot.State;
		var palette = OrbPalette.From(_data);

		_ = _assets.GetAsync(target, palette, _cancellationToken).ContinueWith(
			task =>
			{
				if (task.Status != TaskStatus.RanToCompletion || task.Result is not { } resource)
				{
					return;
				}

				_core.Set(resource);
				Changed?.Invoke(this, EventArgs.Empty);
			},
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private void OnSweep(object? state)
	{
		if (!_data.RingRotation)
		{
			return;
		}

		_sweep.Set((_sweep.Peek() + (_data.RingSpeed * 6.0)) % 360.0);
		Changed?.Invoke(this, EventArgs.Empty);
	}

	public ValueTask DisposeAsync()
	{
		_timer.Dispose();
		_subscription.Dispose();
		_view.Dispose();
		return ValueTask.CompletedTask;
	}
}