using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Widgets;
using MacroDeck.Ui.Config;
using Serilog;

namespace Jarvis.Plugin.Orb;

/// <summary>
/// Offers one widget type. The local id is frozen: every JARVIS orb already on a deck names it, and
/// changing it would strand all of them.
/// </summary>
public sealed class OrbWidgetTypeProvider(ILogger logger) : IWidgetTypeProvider
{
	/// <summary>Frozen. Never rename, never reuse for something else.</summary>
	public const string OrbTypeId = "jarvis-orb";

	private readonly Lock _gate = new();
	private readonly ILogger _logger = logger.ForContext<OrbWidgetTypeProvider>();
	private readonly List<WidgetTypeDescriptor> _registered = [];

	public string ProviderName => "JARVIS";

	public async Task InitializeAsync(IWidgetTypeProviderContext context, CancellationToken cancellationToken = default)
	{
		var registration = await context
			.RegisterWidgetTypeAsync(Descriptor(), cancellationToken)
			.ConfigureAwait(false);

		lock (_gate)
		{
			_registered.Clear();
			_registered.Add(Descriptor());
		}

		_logger.Information("Registered widget type {WidgetTypeId}.", registration.WidgetTypeId);
	}

	public IReadOnlyList<WidgetTypeDescriptor> GetWidgetTypes()
	{
		lock (_gate)
		{
			return _registered.ToArray();
		}
	}

	private static WidgetTypeDescriptor Descriptor() => new(
		OrbTypeId,
		Strings.Orb.TypeName(),
		Strings.Orb.TypeDescription(),
		OrbWidgetData.DefaultJson,
		OrbWidgetData.Schema,
		HasConfiguration: true)
	{
		SupportsFlows = true,
		AppearanceProperties =
		[
			WidgetAppearanceProperty.BackgroundColor,
			WidgetAppearanceProperty.Label,
			WidgetAppearanceProperty.LabelColor,
			WidgetAppearanceProperty.Font,
			WidgetAppearanceProperty.AccentColor,
		],
	};
}