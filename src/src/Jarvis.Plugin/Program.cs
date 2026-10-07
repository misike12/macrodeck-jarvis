using Jarvis.Plugin;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;

// Identity, description and icon come from manifest.json at the content root. Localization is registered in
// AddJarvis so the tests resolve strings the same way this process does.
var plugin = MacroDeckPlugin.CreatePlugin(args)
	.UseMacroDeckLogging()
	.RegisterIntegration<PluginIntegration>()
	.AddJarvis()
	.Build();

await plugin.RunAsync();