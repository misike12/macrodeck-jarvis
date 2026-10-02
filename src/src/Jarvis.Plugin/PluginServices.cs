using Jarvis.Plugin.Actions;
using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Llm;
using Jarvis.Plugin.Runtime;
using Jarvis.Plugin.Speech;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;
using Serilog;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Plugin;

/// <summary>
/// The plugin's service registrations. Shared with the tests so the two cannot drift: a harness that
/// registered a different graph than the real host would be testing something nobody runs.
/// </summary>
public static class PluginServices
{
	public static PluginHostBuilder AddJarvis(this PluginHostBuilder builder)
	{
		builder.Services.AddSingleton<JarvisSettingsStore>();
		builder.Services.AddSingleton<AssistantStateHolder>();
		builder.Services.AddSingleton<ConversationRunner>();

		builder.Services.AddSingleton<WindowsSynthesizer>();
		builder.Services.AddSingleton<PiperSynthesizer>();
		builder.Services.AddSingleton<SpeechPlayer>();
		builder.Services.AddSingleton<VoiceService>();

		builder.Services.AddSingleton(provider => new AssistantSession(
			provider.GetRequiredService<AssistantStateHolder>(),
			provider.GetRequiredService<JarvisSettingsStore>(),
			() => provider.GetRequiredService<ConversationRunner>(),
			provider.GetRequiredService<VoiceService>(),
			provider.GetRequiredService<ILogger>()));

		builder.Services.AddSingleton<ChatClient>();
		builder.Services.AddSingleton<MicrophoneMonitor>();

		builder.Services.AddSingleton(provider => new RuntimeManager(
			provider.GetRequiredService<IHttpClientFactory>().CreateClient("nim"),
			provider.GetRequiredService<ILogger>()));

		builder.Services.AddSingleton<ToolRegistry>(provider =>
		{
			var registry = new ToolRegistry(
				provider.GetRequiredService<JarvisSettingsStore>(),
				provider.GetRequiredService<AssistantSession>(),
				provider.GetRequiredService<ILogger>());

			var logger = provider.GetRequiredService<ILogger>();

			registry.Register(new ShellTool(provider.GetRequiredService<AssistantSession>(), logger));
			registry.Register(new ReadFileTool());
			registry.Register(new WriteFileTool());
			registry.Register(new ListDirectoryTool());

			return registry;
		});

		builder.Services.AddHttpClient("nim", client =>
		{
			client.Timeout = TimeSpan.FromSeconds(120);
			client.DefaultRequestHeaders.UserAgent.ParseAdd("MacroDeck-Jarvis/0.1");
		});

		return builder;
	}
}