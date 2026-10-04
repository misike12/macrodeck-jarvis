using Jarvis.Plugin.Audio;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Orb;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Model.Resources;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Every state the assistant can be in produces an orb asset.
/// <para>
/// The list that decides this was missing <c>Unavailable</c>, which is the state a freshly installed plugin
/// sits in until its configuration flow is completed. The orb therefore drew nothing at all for exactly the
/// user who had just installed it: a blank widget with no error anywhere, because the cache answered
/// "nothing to draw" and the renderer was never asked.
/// </para>
/// <para>
/// The check is by enumeration rather than by listing states, so a state added to the enum cannot be
/// forgotten here.
/// </para>
/// </summary>
[TestFixture]
public class OrbAssetCacheTests
{
	private sealed record Sink(List<LogEvent> Events) : ILogEventSink
	{
		public void Emit(LogEvent logEvent) => Events.Add(logEvent);
	}

	private static (OrbAssetCache Cache, Sink Logs) NewCache()
	{
		var sink = new Sink([]);
		var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

		return (new OrbAssetCache(new RecordingResourceRegistry(), logger), sink);
	}

	/// <summary>
	/// The property itself: for every state the assistant can be in, the cache returns a resource.
	/// </summary>
	[Test]
	public async Task Every_state_the_assistant_can_be_in_produces_an_asset()
	{
		var (cache, _) = NewCache();

		foreach (var state in Enum.GetValues<AssistantState>())
		{
			var resource = await cache.GetAsync(
				state, OrbPalette.From(new OrbWidgetData()), 0, OrbPreset.ArcReactor, CancellationToken.None);

			Assert.That(resource, Is.Not.Null, $"{state} produced no orb asset, so the widget renders empty");
		}
	}

	/// <summary>
	/// The state a plugin is in before its configuration is finished, which is the one that was broken.
	/// </summary>
	[Test]
	public async Task An_unconfigured_assistant_still_produces_an_asset()
	{
		var (cache, _) = NewCache();

		var resource = await cache.GetAsync(
			AssistantState.Unavailable,
			OrbPalette.From(new OrbWidgetData()),
			0,
			OrbPreset.ArcReactor,
			CancellationToken.None);

		Assert.That(resource, Is.Not.Null);
	}

	/// <summary>
	/// A cancelled session must not leave a half-built asset behind, and must not throw into the
	/// continuation that swallows it.
	/// </summary>
	[Test]
	public async Task A_cancelled_request_does_not_throw()
	{
		var (cache, _) = NewCache();

		using var cancelled = new CancellationTokenSource();
		await cancelled.CancelAsync();

		var outcome = await cache.GetAsync(
			AssistantState.Idle,
			OrbPalette.From(new OrbWidgetData()),
			0,
			OrbPreset.ArcReactor,
			cancelled.Token);

		Assert.That(outcome, Is.Null);
	}

	private sealed class RecordingResourceRegistry : IUiResourceRegistry
	{
public Task<UiResource> RegisterAsync(
			string name,
			ReadOnlyMemory<byte> content,
			string mediaType,
			CancellationToken cancellationToken = default) =>
			// Built without a constructor because the type has none the plugin can see: the host creates these
			// and hands them back. What matters here is that a resource came back at all, not its contents.
			Task.FromResult(
				(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UiResource)) as UiResource)!);

		public Task RemoveAsync(string name, CancellationToken cancellationToken = default) =>
			Task.CompletedTask;
	}
}