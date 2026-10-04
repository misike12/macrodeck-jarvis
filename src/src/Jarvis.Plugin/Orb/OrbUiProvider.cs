using System.Text.Json;
using Jarvis.Plugin.Core;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using Serilog;

namespace Jarvis.Plugin.Orb;

/// <summary>
/// Draws the orb, and configures it. Two animation channels, each chosen for what the widget profile can
/// actually do: the animated GIF carries all the smooth motion at zero protocol cost, and a
/// <c>ui.transform</c> ring sweep is added on property patches at a rate the host tolerates.
/// </summary>
public sealed class OrbUiProvider(
	AssistantStateHolder state,
	IUiResourceRegistry resources,
	ILogger logger) : IUiProvider
{
	private readonly AssistantStateHolder _state = state;
	private readonly ILogger _logger = logger.ForContext<OrbUiProvider>();
	private readonly OrbAssetCache _assets = new(resources, logger);

	public static IReadOnlyList<UiSurfaceDeclaration> DeclaredSurfaces { get; } =
	[
		new() { Kind = UiSurfaceKinds.Widget, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Preview, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Config, SessionMode = UiSessionModes.Exclusive },
	];

	public IReadOnlyList<UiSurfaceDeclaration> Surfaces => DeclaredSurfaces;

	public Task<IUiSession?> CreateSessionAsync(UiSessionRequest request, CancellationToken cancellationToken)
	{
		if (!Serves(request.Surface))
		{
			return Task.FromResult<IUiSession?>(null);
		}

		var surface = request.Surface;
		var data = ReadData(surface);

		// A widget configuration is a static tree: every field is bound to a state the host fills as the
		// user edits, and the host owns the draft and the save. Nothing here polls, so unlike a live orb
		// this session is a bare adapter over a UiView.
		if (surface.Kind == UiSurfaceKinds.Config)
		{
			return Task.FromResult<IUiSession?>(new OrbConfigSession(new UiView(surface, OrbConfigView.Build(data))));
		}

		return Task.FromResult<IUiSession?>(
			new OrbUiSession(OrbWidgetData.Parse(data), _state, _assets));
	}

	/// <summary>
	/// The widget type is checked rather than assumed. A declined widget configuration leaves the user
	/// with JSON mode only, because unlike an action there is no declared field list to fall back to.
	/// </summary>
	private static bool Serves(UiSurface surface) => surface.Kind switch
	{
		UiSurfaceKinds.Widget => true,
		UiSurfaceKinds.Preview => true,
		UiSurfaceKinds.Config => ReadAttribute(surface, UiConfigSurfaceAttributes.EntryPoint) == UiConfigEntryPoints.WidgetConfig
			&& ReadAttribute(surface, UiConfigSurfaceAttributes.WidgetType) == OrbWidgetTypeProvider.OrbTypeId,
		_ => false,
	};

	private static string? ReadAttribute(UiSurface surface, string name) =>
		surface.Attributes.GetValueOrDefault(name) is { ValueKind: JsonValueKind.String } value
			? value.GetString()
			: null;

	/// <summary>
	/// The widget's configuration travels on the surface rather than being looked up, because a plugin
	/// cannot read the host's widget store. Absent means a fresh widget.
	/// </summary>
	private static JsonElement ReadData(UiSurface surface)
	{
		var raw = surface.Kind == UiSurfaceKinds.Config
			? surface.Attributes.GetValueOrDefault(UiConfigSurfaceAttributes.WidgetData)
			: surface.Attributes.GetValueOrDefault(UiWidgetSurfaceAttributes.Data);

		return raw is { ValueKind: JsonValueKind.Object } element ? element : OrbWidgetData.DefaultElement;
	}
}

/// <summary>
/// Adapts a <see cref="UiView"/> to the session contract. The host raises <c>Changed</c> and drains
/// patches; the view knows nothing about sessions.
/// </summary>
internal sealed class OrbConfigSession : IUiSession
{
	private readonly UiView _view;

	public OrbConfigSession(UiView view)
	{
		_view = view;
		_view.Changed += OnViewChanged;
		_view.HandlerFaulted += OnViewFaulted;
	}

	public event EventHandler? Changed;

	public event EventHandler<UiSessionFaultedEventArgs>? Faulted;

	public UiTree BuildTree() => _view.Tree;

	public IReadOnlyList<UiPatch> DrainPatches() => _view.DrainPatches();

	public void Dispatch(UiEvent uiEvent) => _view.Dispatch(uiEvent);

	private void OnViewChanged(object? sender, EventArgs args) => Changed?.Invoke(this, EventArgs.Empty);

	private void OnViewFaulted(object? sender, UiHandlerFaultEventArgs args) =>
		Faulted?.Invoke(this, new UiSessionFaultedEventArgs(args.NodeId, args.Exception));

	public ValueTask DisposeAsync()
	{
		_view.Changed -= OnViewChanged;
		_view.HandlerFaulted -= OnViewFaulted;
		_view.Dispose();
		return ValueTask.CompletedTask;
	}
}
