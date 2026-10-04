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

	/// <summary>
	/// The qualified id the host assigned, in the form <c>integrationId::localId</c>.
	/// <para>
	/// This is what arrives on a surface, and it is the reason the widget's visual configuration used to be
	/// unavailable: the surface was compared against <see cref="OrbTypeId"/>, the local half, so it never
	/// matched, no configuration session was created, and the host showed "this widget's configuration is
	/// temporarily unavailable" with nothing in the log to explain it. The host derives the qualified form
	/// from the authenticated registration, so it has to be taken from the registration rather than built here.
	/// </para>
	/// </summary>
	public string QualifiedTypeId { get; private set; } = OrbTypeId;

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
			QualifiedTypeId = registration.WidgetTypeId;
		}

		_logger.Information("Registered widget type {WidgetTypeId}.", registration.WidgetTypeId);
	}

	/// <summary>
	/// Whether a surface names this widget type. The qualified id is authoritative, and the suffix is
	/// accepted too so a configuration still opens before the registration has come back.
	/// </summary>
	public bool IsThisWidgetType(string? widgetType) =>
		widgetType is not null
		&& (string.Equals(widgetType, QualifiedTypeId, StringComparison.Ordinal)
			|| widgetType.EndsWith("::" + OrbTypeId, StringComparison.Ordinal));

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

			// Deliberately not WidgetAppearanceProperty.AccentColor. The orb has its own accentColor setting,
			// which is one of the colours the renderer builds its palette from, so declaring the host's accent
			// as well produced two inputs with the same node id. Node ids have to be unique across the whole
			// tree, so the configuration view threw while materializing and the host fell back to reporting the
			// configuration as unavailable, with the throw swallowed into a generic message.
		],
	};
}