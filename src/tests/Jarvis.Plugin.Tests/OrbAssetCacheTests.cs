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

private static (OrbAssetCache Cache, RecordingResourceRegistry Registry, Sink Logs) NewCache()
	{
		var sink = new Sink([]);
		var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
		var registry = new RecordingResourceRegistry();

		return (new OrbAssetCache(registry, logger), registry, sink);
	}

	/// <summary>
	/// The property itself: for every state the assistant can be in, the cache returns a resource.
	/// </summary>
	[Test]
	public async Task Every_state_the_assistant_can_be_in_produces_an_asset()
	{
		var (cache, _, _) = NewCache();

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
		var (cache, _, _) = NewCache();

		var resource = await cache.GetAsync(
			AssistantState.Unavailable,
			OrbPalette.From(new OrbWidgetData()),
			0,
			OrbPreset.ArcReactor,
			CancellationToken.None);

		Assert.That(resource, Is.Not.Null);
	}

	/// <summary>
	/// A setting that changes a pixel has to change the cache entry too, or the previous frames are served
	/// and the setting appears to do nothing.
	/// <para>
	/// The key used to name state, preset, palette and amplitude band only. Ring count, ring speed, ring
	/// rotation and glow were all in the widget configuration and none of them in the key, so switching the
	/// glow off still served the frames built with it on.
	/// </para>
	/// </summary>
	[Test]
	public async Task Each_setting_produces_its_own_asset()
	{
		var (cache, registry, _) = NewCache();

		await cache.GetAsync(AssistantState.Idle, OrbPalette.Default, 0, OrbPreset.ArcReactor, CancellationToken.None);
		await cache.GetAsync(
			AssistantState.Idle, OrbPalette.Default, 0, OrbPreset.ArcReactor, CancellationToken.None, ringCount: 1);
		await cache.GetAsync(
			AssistantState.Idle, OrbPalette.Default, 0, OrbPreset.ArcReactor, CancellationToken.None, ringRotation: false);
		await cache.GetAsync(
			AssistantState.Idle, OrbPalette.Default, 0, OrbPreset.ArcReactor, CancellationToken.None, glow: false);

		Assert.That(
			registry.Names.Distinct().Count(),
			Is.EqualTo(4),
			"the settings shared a cache entry, so one of them was served the wrong picture: "
				+ string.Join(", ", registry.Names));
	}

	/// <summary>The frames have to differ, not merely be registered under different names.</summary>
	[Test]
	public async Task Turning_the_glow_off_changes_the_pixels()
	{
		var (lit, litRegistry, _) = NewCache();
		var (unlit, unlitRegistry, _) = NewCache();

		await lit.GetAsync(AssistantState.Idle, OrbPalette.Default, 0, OrbPreset.ArcReactor, CancellationToken.None);
		await unlit.GetAsync(
			AssistantState.Idle, OrbPalette.Default, 0, OrbPreset.ArcReactor, CancellationToken.None, glow: false);

		Assert.That(
			unlitRegistry.Bytes.Single(),
			Is.Not.EqualTo(litRegistry.Bytes.Single()),
			"the glow setting changed the name of the asset but not one pixel of it");
	}

/// <summary>
	/// A refused upload is retried through the gate, and then left alone for a while.
	/// <para>
	/// The host answers overlapping uploads with QUEUE_OVERFLOW, and a failure used to evict the cache entry
	/// immediately, so the next state change rebuilt and re-uploaded at once and a full queue stayed full for
	/// as long as the assistant kept changing state. Four hundred warnings in fourteen minutes, and a widget
	/// that never drew.
	/// </para>
	/// </summary>
	[Test]
	public async Task A_refused_upload_does_not_retry_on_the_next_call()
	{
		var sink = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new ListSink()).CreateLogger();
		var registry = new RefusingRegistry();
		var cache = new OrbAssetCache(registry, sink);

		var first = await cache.GetAsync(
			AssistantState.Idle, OrbPalette.Default, 0, OrbPreset.ArcReactor, CancellationToken.None);

		var second = await cache.GetAsync(
			AssistantState.Idle, OrbPalette.Default, 0, OrbPreset.ArcReactor, CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(first, Is.Null);
			Assert.That(second, Is.Null);
			Assert.That(
				registry.Attempts,
				Is.EqualTo(3),
				$"the refused upload was attempted {registry.Attempts} times; 3 is the bound, and a second call must add none");
		});
	}

	private sealed class ListSink : Serilog.Core.ILogEventSink
	{
		public void Emit(Serilog.Events.LogEvent logEvent)
		{
		}
	}

	private sealed class RefusingRegistry : IUiResourceRegistry
	{
		public int Attempts { get; private set; }

		public Task<UiResource> RegisterAsync(
			string name,
			ReadOnlyMemory<byte> content,
			string mediaType,
			CancellationToken cancellationToken = default)
		{
			Attempts++;
			throw new InvalidOperationException("QUEUE_OVERFLOW");
		}

		public Task RemoveAsync(string name, CancellationToken cancellationToken = default) =>
			Task.CompletedTask;
	}

	/// <summary>
	/// A cancelled session must not leave a half-built asset behind, and must not throw into the
	/// continuation that swallows it.
	/// </summary>
	[Test]
	public async Task A_cancelled_request_does_not_throw()
	{
		var (cache, _, _) = NewCache();

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
public List<string> Names { get; } = [];

		public List<byte[]> Bytes { get; } = [];

		public Task<UiResource> RegisterAsync(
			string name,
			ReadOnlyMemory<byte> content,
			string mediaType,
			CancellationToken cancellationToken = default)
		{
			Names.Add(name);
			Bytes.Add(content.ToArray());

			// Built without a constructor because the type has none the plugin can see: the host creates these
			// and hands them back. What matters here is that a resource came back at all, not its contents.
			return Task.FromResult(
				(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UiResource)) as UiResource)!);
		}

		public Task RemoveAsync(string name, CancellationToken cancellationToken = default) =>
			Task.CompletedTask;
	}
}