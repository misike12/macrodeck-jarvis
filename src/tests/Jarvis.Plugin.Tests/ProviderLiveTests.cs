using System.Runtime.Versioning;
using Jarvis.Plugin.Core;
using Jarvis.Plugin.Llm;
using MacroDeck.Sdk;
using NUnit.Framework;

namespace Jarvis.Plugin.Tests;

/// <summary>
	/// Talks to the configured provider for real.
	/// <para>
	/// The key is read from the developer's local settings file beside the plugin build, which is gitignored,
	/// so this only runs where one has been configured and the key never reaches the repository. Explicit
	/// because it costs money and reaches a network.
	/// </para>
	/// </summary>
	[TestFixture]
	[Explicit("Sends a real request to the configured model provider.")]
	[SupportedOSPlatform("windows")]
	public class ProviderLiveTests
	{
/// <summary>
	/// A store that has read a developer configuration.
	/// <para>
	/// The reload matters: the store only holds what it has read, so constructing one is not enough and every
	/// field reads as its default until it happens.
	/// </para>
	/// </summary>
	private static async Task<(JarvisSettings Settings, JarvisSettingsStore Store)> ConfiguredStoreAsync(
		CancellationToken cancellationToken)
	{
		var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, "src", "Jarvis.Plugin", "bin", "Debug", "net10.0");

			if (File.Exists(Path.Combine(candidate, "jarvis.settings.json")))
			{
				var store = new JarvisSettingsStore(RuntimeTestLog.Logger, LocalSettingsFile.Load(candidate));

				await store.ReloadAsync(context: null, cancellationToken).ConfigureAwait(false);

				return (store.Current, store);
			}

			directory = directory.Parent;
		}

		Assert.Fail("no developer settings file was found beside the plugin build");
		throw new InvalidOperationException("unreachable");
	}

	/// <summary>
	/// The chat path, end to end through the client the turn actually uses: streaming deltas, tool
	/// definitions present, and a real answer rather than a hand-written one.
	/// </summary>
	[Test]
	[CancelAfter(180_000)]
	public async Task The_configured_model_answers()
	{
		var (settings, store) = await ConfiguredStoreAsync(TestContext.CurrentContext.CancellationToken);

		Assert.That(settings.HasLlmCredentials, Is.True, "no API key is configured for the live test");

		var chat = new ChatClient(new SingleClientFactory(), store, RuntimeTestLog.Logger);
		var reply = new System.Text.StringBuilder();

		var (completion, failure, detail) = await chat.CompleteAsync(
			new ChatRequest
			{
				Model = settings.LlmModel,
				Messages = [ChatMessage.User("Reply with the single word: ready")],
			},
			delta => reply.Append(delta),
			TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);

		TestContext.Out.WriteLine($"model: {settings.LlmModel}");
		TestContext.Out.WriteLine($"reply: {reply}");

		Assert.Multiple(() =>
		{
			Assert.That(failure, Is.EqualTo(ModelFailure.None), $"{failure}: {detail}");
			Assert.That(completion, Is.Not.Null);
			Assert.That(reply.ToString().Trim(), Is.Not.Empty, "the model produced no text at all");
		});
	}

	/// <summary>
	/// Hands the client the one <see cref="HttpClient"/> it asks for. A factory rather than a bare client
	/// because that is the shape the chat client takes, and its own timeout because a request to a large model
	/// over a real network needs one that a unit test never has to wait for.
	/// </summary>
	private sealed class SingleClientFactory : IHttpClientFactory
	{
		public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(120) };
	}
	/// <summary>
	/// The model the plugin asks for by default has to exist, and it has to answer in a reasonable time. The
	/// default vision model was a 90 billion parameter model that did not return a description of a
	/// screenshot inside the client's timeout, so the screenshot tool failed on first use while looking
	/// correctly configured. Both halves matter: the model existing, and being usable.
	/// </summary>
	[Test]
	[CancelAfter(180_000)]
	public async Task The_default_vision_model_answers_in_time()
	{
var (settings, store) = await ConfiguredStoreAsync(TestContext.CurrentContext.CancellationToken);

		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
		var client = new Jarvis.Plugin.Vision.VisionClient(http, store, RuntimeTestLog.Logger);

		var capture = Jarvis.Plugin.Vision.ScreenCaptureService.Capture(
			Jarvis.Plugin.Vision.CaptureTarget.PrimaryScreen,
			RuntimeTestLog.Logger);

		var result = await client.DescribeScreenAsync(
			capture, "Name the single largest window.", TestContext.CurrentContext.CancellationToken)
			.ConfigureAwait(false);

		TestContext.Out.WriteLine($"model: {JarvisSettings.DefaultVisionModel}");
		TestContext.Out.WriteLine($"reply: {result.Text}");

		Assert.Multiple(() =>
		{
			Assert.That(result.Ok, Is.True, $"{result.Failure} {result.Detail}");
			Assert.That(result.Text.Trim(), Is.Not.Empty);
		});
	}
}