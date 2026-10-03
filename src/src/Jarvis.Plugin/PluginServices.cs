using Jarvis.Plugin.Actions;
using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Input;
using Jarvis.Plugin.Llm;
using Jarvis.Plugin.Memory;
using Jarvis.Plugin.Runtime;
using Jarvis.Plugin.Speech;
using Jarvis.Plugin.Vision;
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
		builder.Services.AddSingleton<WhisperTranscriber>();
		builder.Services.AddSingleton<VoiceRecorder>();
		builder.Services.AddSingleton<ListeningPipeline>();
		builder.Services.AddSingleton<GlobalHotkey>();
		builder.Services.AddSingleton<WakeWordDetector>();
		builder.Services.AddSingleton<SpeechPlayer>();
		builder.Services.AddSingleton<VoiceService>();

		builder.Services.AddSingleton(provider => new AssistantSession(
			provider.GetRequiredService<AssistantStateHolder>(),
			provider.GetRequiredService<JarvisSettingsStore>(),
			() => provider.GetRequiredService<ConversationRunner>(),
			provider.GetRequiredService<VoiceService>(),
			provider.GetRequiredService<MemoryStore>(),
			provider.GetRequiredService<ILogger>()));

		builder.Services.AddSingleton<ChatClient>();
		builder.Services.AddSingleton<VisionClient>();
		builder.Services.AddSingleton<MemoryStore>();
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
			registry.Register(new FileTools.FileExistsTool());
			registry.Register(new FileTools.SearchFilesTool());
			registry.Register(new FileTools.MovePathTool());
			registry.Register(new FileTools.DeletePathTool());
			registry.Register(new WindowTools.ListWindowsTool());
			registry.Register(new WindowTools.FocusWindowTool());
			registry.Register(new WindowTools.CloseWindowTool());
			registry.Register(new WindowTools.OpenAppTool());
			registry.Register(new SystemTools.GetVolumeTool());
			registry.Register(new SystemTools.MediaPlayPauseTool());
			registry.Register(new SystemTools.MediaNextTool());
			registry.Register(new SystemTools.SetSystemPowerTool());
			registry.Register(new SystemTools.SendNotificationTool());
			registry.Register(new InputTools.MouseMoveTool());
			registry.Register(new InputTools.MouseClickTool());
			registry.Register(new InputTools.MouseScrollTool());
			registry.Register(new InputTools.MouseDragTool());
			registry.Register(new InputTools.KeyboardTypeTool());
			registry.Register(new InputTools.KeyboardComboTool());
			registry.Register(new InputTools.KeyboardSequenceTool());
			registry.Register(new ScreenshotTool(provider.GetRequiredService<VisionClient>(), logger));

			// The persona tool rewrites only the persona field, through the same immutable-prefix resolver
			// every prompt is built from, so it structurally cannot reach the safety rules.
			var settingsStore = provider.GetRequiredService<JarvisSettingsStore>();
			var memory = provider.GetRequiredService<MemoryStore>();

			registry.Register(new DesktopTools.ListProcessesTool());
			registry.Register(new DesktopTools.ClipboardReadTool());
			registry.Register(new DesktopTools.ClipboardWriteTool());
			registry.Register(new DesktopTools.KillProcessTool());
			registry.Register(new DesktopTools.SetVolumeTool());

			registry.Register(new SetPersonaTool(
				(preset, instruction) =>
				{
					settingsStore.Apply(settingsStore.Current with
					{
						Persona = preset,
						CustomSystemPrompt = preset == PersonaPreset.Custom ? instruction : string.Empty,
					});

					memory.SetNotes(instruction);
				},
				logger));

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