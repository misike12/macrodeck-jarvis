using Jarvis.Plugin;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;

// Identity, description and icon come from manifest.json at the content root. Strings is generated from
// Localization/*.resx, so UseLocalization is what makes every label resolve in the user's language.
var plugin = MacroDeckPlugin.CreatePlugin(args)
	.UseMacroDeckLogging()
	.UseLocalization(Strings.LocalizationCatalog)
	.RegisterIntegration<PluginIntegration>()
	.AddJarvis()
	.Build();

await plugin.RunAsync();