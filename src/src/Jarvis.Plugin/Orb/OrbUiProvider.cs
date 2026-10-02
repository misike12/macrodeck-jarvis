using System.Text.Json;
using Jarvis.Plugin.Core;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Model.Surfaces;
using Serilog;

namespace Jarvis.Plugin.Orb;

/// <summary>
/// Draws the orb. Two animation channels, each chosen for what the widget profile can actually do: the
/// animated GIF carries all the smooth motion at zero protocol cost, and a <c>ui.transform</c> ring
/// sweep is added on property patches at a rate the host's token bucket tolerates.
/// </summary>
public sealed class OrbUiProvider(
	AssistantStateHolder state,
	JarvisSettingsStore settings,
	IUiResourceRegistry resources,
	ILogger logger) : IUiProvider
{
	private readonly AssistantStateHolder _state = state;
	private readonly JarvisSettingsStore _settings = settings;
	private readonly IUiResourceRegistry _resources = resources;
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

		var data = ReadData(request.Surface);
		return Task.FromResult<IUiSession?>(new OrbUiSession(data, _state, _assets, cancellationToken));
	}

	private static bool Serves(UiSurface surface) => surface.Kind switch
	{
		UiSurfaceKinds.Widget => true,
		UiSurfaceKinds.Preview => true,
		UiSurfaceKinds.Config => ReadEntryPoint(surface) == UiConfigEntryPoints.WidgetConfig,
		_ => false,
	};

	private static string? ReadEntryPoint(UiSurface surface) =>
		surface.Attributes.GetValueOrDefault(UiConfigSurfaceAttributes.EntryPoint) is { } value
			&& value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

	/// <summary>
	/// The widget's configuration travels on the surface rather than being looked up, because a plugin
	/// cannot read the host's widget store. Absent means a fresh widget.
	/// </summary>
	private static OrbWidgetData ReadData(UiSurface surface)
	{
		var raw = surface.Kind == UiSurfaceKinds.Config
			? surface.Attributes.GetValueOrDefault(UiConfigSurfaceAttributes.WidgetData)
			: surface.Attributes.GetValueOrDefault(UiWidgetSurfaceAttributes.Data);

		return raw is { ValueKind: JsonValueKind.Object } element
			? OrbWidgetData.Parse(element)
			: new OrbWidgetData();
	}
}