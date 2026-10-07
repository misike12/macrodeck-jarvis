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
	private readonly OrbWidgetData _data;
	private readonly OrbAssetCache _assets;
	private readonly OrbButtonActions _buttons;

	/// <summary>
	/// The session's own cancellation, not the invocation's.
	/// <para>
	/// The token handed to <c>CreateSessionAsync</c> is scoped to that one <c>ui.create</c> call, and a widget
	/// session outlives it by hours. Once the invocation completed the host cancelled it, so every later
	/// asset fetch threw, the failure was swallowed, the entry was evicted and the orb silently froze on its
	/// last frame at Debug level, the moment the user next spoke.
	/// </para>
	/// </summary>
	private readonly CancellationTokenSource _lifetime = new();
	private readonly UiState<AssistantState> _orbState;
	private readonly UiState<UiResource?> _core;
	private readonly UiState<string> _reply;
	private readonly UiState<string> _transcript;
	private readonly UiView _view;
	private readonly IDisposable _subscription;

public OrbUiSession(
		OrbWidgetData data,
		AssistantStateHolder state,
		OrbAssetCache assets,
		OrbButtonActions buttons)
	{
		_data = data;
		_assets = assets;
		_buttons = buttons;

		_orbState = new UiState<AssistantState>(AssistantState.Idle);
		_core = new UiState<UiResource?>(null);
		_reply = new UiState<string>(string.Empty);
		_transcript = new UiState<string>(string.Empty);

		var surface = new UiSurface
		{
			Kind = UiSurfaceKinds.Widget,
			SessionMode = UiSessionModes.Shared,
		};

_view = new UiView(surface, OrbView.Build(data, _orbState, _core, _reply, _transcript, buttons));

		_view.HandlerFaulted += (_, args) =>
			Faulted?.Invoke(this, new UiSessionFaultedEventArgs(args.NodeId, args.Exception));

		_subscription = state.Subscribe(OnSnapshot);
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

		// The measured amplitude is passed through, so a voice actually moves the orb. It is read from the
		// snapshot rather than fetched from the meter here, because the snapshot already carries the value
		// the deadband decided was worth publishing.
		//
		// The four settings go to the renderer rather than to a layer above the image, because the asset
		// already contains the glow and the rings. They are part of the cache key on the other side of this
		// call, so changing one and reopening the widget produces a different picture rather than the frames
		// built for the previous setting.
		_ = _assets.GetAsync(
			target,
			palette,
			snapshot.Amplitude,
			_data.Preset,
			_lifetime.Token,
			_data.RingCount,
			_data.RingSpeed,
			_data.RingRotation,
			_data.Glow).ContinueWith(
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

public ValueTask DisposeAsync()
	{
		// Cancelled before anything is torn down, so an asset fetch in flight stops rather than completing
		// against a registry this session is already detaching from.
		_lifetime.Cancel();

_subscription.Dispose();
		_view.Dispose();
		_lifetime.Dispose();

		return ValueTask.CompletedTask;
	}
}