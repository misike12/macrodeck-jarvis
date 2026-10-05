using Jarvis.Plugin.Core;
using MacroDeck.Sdk;
using MacroDeck.Plugin.Hosting.Transport;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Decks;
using MacroDeck.Sdk.Events;
using MacroDeck.Sdk.Messaging;
using MacroDeck.Sdk.Notifications;
using MacroDeck.Sdk.Scripts;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Variables;
using MacroDeck.Sdk.Widgets;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// Reading the configuration back from the host.
/// <para>
/// Every other settings test passes <c>context: null</c>, so the branch that actually talks to the host had
/// no coverage at all. It read about forty fields as one call each, back to back, because
/// <see cref="IIntegrationConfig"/> offers only <c>GetStringAsync</c> and <c>GetSecretAsync</c> for a single
/// key and no bulk form. The host answers a plugin that calls back too quickly with a
/// <c>HostInvocationException</c>, the whole reload was abandoned, and the plugin came up with no API key:
/// the assistant sat in <c>Unavailable</c>, would not talk, and the orb had nothing to draw.
/// </para>
/// </summary>
[TestFixture]
public class SettingsHostReadTests
{
/// <summary>
	/// Stands in for the host's config API, refusing more than <c>burst</c> calls in any one second. That is
	/// the shape of the real limit: a rate over a sliding window, not a total, and not a ban on the first N
	/// calls, which is what a naive fake models and what no real limiter does.
	/// </summary>
	private sealed class RateLimitedConfig(IReadOnlyList<ConfigEntrySnapshot> entries, int burst) : IIntegrationConfig
	{
		private readonly Queue<long> _recent = new();

		public int Calls { get; private set; }

		public int Refusals { get; private set; }

		public Task<IReadOnlyList<ConfigEntrySnapshot>> GetEntriesAsync(CancellationToken cancellationToken) =>
			Task.FromResult(entries);

		public Task<string?> GetStringAsync(Guid entryId, string key, CancellationToken cancellationToken) =>
			Answer(key);

		public Task<string?> GetSecretAsync(Guid entryId, string key, CancellationToken cancellationToken) =>
			Answer("secret-for-" + key);

		public Task SetSecretAsync(Guid entryId, string key, string? value, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task SetStringAsync(Guid entryId, string key, string? value, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		/// <summary>Answers with the key's own name, so a test can see which field a value came from.</summary>
		private Task<string?> Answer(string value)
		{
			Calls++;

			var now = Environment.TickCount64;

			while (_recent.Count > 0 && now - _recent.Peek() >= WindowMs)
			{
				_recent.Dequeue();
			}

			if (_recent.Count >= burst)
			{
				Refusals++;
				throw HostInvocationException.CreateRetryable(
					"rate_limited", "This plugin is calling back into the host too quickly.");
			}

			_recent.Enqueue(now);
			return Task.FromResult<string?>(value);
		}

private const long WindowMs = 1000;
	}

	/// <summary>Only <see cref="Config"/> is ever touched by a settings reload.</summary>
	private sealed class StubIntegrationContext(IIntegrationConfig config) : IIntegrationContext
	{
		public IIntegrationConfig Config => config;

		public IVariableApi Variables => throw new NotSupportedException();

		public IUserVariableApi UserVariables => throw new NotSupportedException();

		public IDeckNavigator Deck => throw new NotSupportedException();

		public IScriptApi Scripts => throw new NotSupportedException();

		public IWidgetApi Widgets => throw new NotSupportedException();

		public IEventPublisher Events => throw new NotSupportedException();

		public IUserNotifier Notifications => throw new NotSupportedException();

		public IMessageChannel Messages => throw new NotSupportedException();

		public IUiResourceRegistry UiResources => throw new NotSupportedException();
	}

	private static JarvisSettingsStore NewStore() =>
		new(RuntimeTestLog.Logger, LocalSettingsFile.Empty);

	private static async Task<(JarvisSettingsStore Store, RateLimitedConfig Config)> ReloadAsync(int burst)
	{
		var entries = new[] { new ConfigEntrySnapshot(Guid.NewGuid(), "JARVIS") };
		var config = new RateLimitedConfig(entries, burst);
		var store = NewStore();

		await store.ReloadAsync(new StubIntegrationContext(config), CancellationToken.None);

		return (store, config);
	}

	/// <summary>
	/// The failure this fixes: a host that refuses a burst must not cost the plugin its credentials.
	/// </summary>
	[Test]
	public async Task A_rate_limited_host_still_yields_the_api_key()
	{
		var (store, config) = await ReloadAsync(burst: 4);

		Assert.Multiple(() =>
		{
			Assert.That(config.Refusals, Is.GreaterThan(0), "the fake host never refused anything, so this proves nothing");
			Assert.That(
				store.Current.NvidiaApiKey,
				Is.EqualTo("secret-for-nvidiaApiKey"),
				"the NVIDIA key was lost, so the assistant reports itself unconfigured and cannot be talked to");
		});
	}

	/// <summary>Every field has to survive, not only the key that made the symptom visible.</summary>
	[Test]
	public async Task A_rate_limited_host_still_yields_every_field()
	{
		var (store, config) = await ReloadAsync(burst: 4);

		Assert.Multiple(() =>
		{
			Assert.That(config.Calls, Is.GreaterThan(30), "far too few reads, so the fake host was never really used");
			Assert.That(store.Current.LlmModel, Is.EqualTo("llmModel"));
			Assert.That(store.Current.WakeWord, Is.EqualTo("wakeWord"));
			Assert.That(store.Current.PicovoiceAccessKey, Is.EqualTo("secret-for-picovoiceAccessKey"));
			Assert.That(store.Current.SelfHostedToken, Is.EqualTo("secret-for-selfHostedToken"));
		});
	}

	/// <summary>
	/// The reads have to be spread out rather than only retried after each refusal. The host's limit is a
	/// rate, not a total, so a caller that only backs off once it has been knocked still keeps knocking.
	/// </summary>
	[Test]
	public async Task Reads_are_spaced_in_time()
	{
		var started = System.Diagnostics.Stopwatch.StartNew();
		var (_, config) = await ReloadAsync(burst: 0);
		started.Stop();

		Assert.Multiple(() =>
		{
			Assert.That(config.Refusals, Is.GreaterThan(0));
			Assert.That(
				started.ElapsedMilliseconds,
				Is.GreaterThan(500),
				"the reload finished faster than forty reads could have been spaced, so they were a burst");
		});
	}

	/// <summary>A host that refuses every attempt must not take the previous settings down with it.</summary>
	[Test]
	public async Task A_host_that_never_answers_keeps_the_previous_values()
	{
		var store = NewStore();
		store.Apply(store.Current with { LlmModel = "kept-from-before", WakeWord = "Computer" });

		var entries = new[] { new ConfigEntrySnapshot(Guid.NewGuid(), "JARVIS") };
		await store.ReloadAsync(
			new StubIntegrationContext(new RateLimitedConfig(entries, burst: 0)),
			CancellationToken.None);

Assert.Multiple(() =>
		{
			Assert.That(store.Current.LlmModel, Is.EqualTo("kept-from-before"));
			Assert.That(store.Current.WakeWord, Is.EqualTo("Computer"));
		});
	}

	/// <summary>
	/// The regression behind "only the secrets save".
	/// <para>
	/// The reload cleared its read dictionary before reading anything and published the results only after
	/// the credentials had been read, so a refusal anywhere in the run cost every plain setting while the
	/// secrets already sitting in a local survived. The user saw a working API key and thirty settings
	/// that reverted to their defaults, on every configuration, and concluded the flow was not saving.
	/// A second reload must carry the plain settings forward untouched.
	/// </para>
	/// </summary>
	[Test]
	public async Task A_failed_reload_keeps_the_plain_settings_that_a_previous_one_read()
	{
		var entries = new[] { new ConfigEntrySnapshot(Guid.NewGuid(), "JARVIS") };
		var store = NewStore();

		// An unlimited first reload, so there is a known good set of values to protect.
		await store.ReloadAsync(
			new StubIntegrationContext(new RateLimitedConfig(entries, burst: int.MaxValue)),
			CancellationToken.None);

		var readOnce = store.Current;

		Assert.That(readOnce.MicrophoneName, Is.EqualTo("microphoneName"), "the first reload read nothing");

		// A host that now refuses every call must not undo what the first reload established.
		await store.ReloadAsync(
			new StubIntegrationContext(new RateLimitedConfig(entries, burst: 0)),
			CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(
				store.Current.MicrophoneName,
				Is.EqualTo(readOnce.MicrophoneName),
				"a failed reload wiped a plain setting, which is the bug that made only secrets appear to save");
			Assert.That(
				store.Current.NvidiaApiKey,
				Is.EqualTo(readOnce.NvidiaApiKey),
				"a failed reload lost the credential that had already been read");
		});
	}
}