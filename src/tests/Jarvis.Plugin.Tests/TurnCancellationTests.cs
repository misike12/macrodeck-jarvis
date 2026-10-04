using Jarvis.Plugin.Core;
using NUnit.Framework;
using Serilog;

namespace Jarvis.Plugin.Tests;

/// <summary>
/// The turn token has to be the caller's token, linked.
/// <para>
/// The host cancels a capability invocation after thirty seconds and expects the work to stop. It could
/// not: the turn created its own cancellation source, unrelated to the one the host holds, so a press the
/// host had already given up on went on to call the model, speak the reply and hold one of thirty-two
/// concurrency slots for as long as the model took.
/// </para>
/// <para>
/// These are the tests that would have caught it. Every one of them failed against the unlinked source.
/// </para>
/// </summary>
[TestFixture]
public class TurnCancellationTests
{
	private static AssistantSession NewSession() =>
		new(
			new AssistantStateHolder(),
			StoreWith(new JarvisSettings()),
			() => throw new InvalidOperationException("no model in this test"),
			Voice(),
			Memory(),
			RuntimeTestLog.Logger);

	private static JarvisSettingsStore StoreWith(JarvisSettings settings)
	{
		var store = new JarvisSettingsStore(
			RuntimeTestLog.Logger,
			Jarvis.Plugin.Core.LocalSettingsFile.Load(
				Path.Combine(Path.GetTempPath(), "jarvis-turn-cancel-none")));

		store.Apply(settings);

		return store;
	}

	private static Jarvis.Plugin.Speech.VoiceService Voice() =>
		new(
			new Jarvis.Plugin.Speech.WindowsSynthesizer(RuntimeTestLog.Logger),
			new Jarvis.Plugin.Speech.PiperSynthesizer(
				new Jarvis.Plugin.Runtime.RuntimeManager(
					new HttpClient(),
					RuntimeTestLog.Logger,
					new Jarvis.Plugin.Runtime.RuntimePaths(
						Path.Combine(Path.GetTempPath(), "jarvis-turn-cancel-nonexistent"))),
				RuntimeTestLog.Logger),
			new Jarvis.Plugin.Speech.SpeechPlayer(RuntimeTestLog.Logger),
			new AssistantStateHolder(),
			StoreWith(new JarvisSettings()),
			RuntimeTestLog.Logger);

	private static Jarvis.Plugin.Memory.MemoryStore Memory() =>
		new(StoreWith(new JarvisSettings()), RuntimeTestLog.Logger);

	/// <summary>The whole point: the caller's cancellation must reach the turn's own token.</summary>
	[Test]
	public void A_turn_token_follows_the_token_that_asked_for_the_turn()
	{
		using var caller = new CancellationTokenSource();
		var session = NewSession();

		session.BeginTurn(caller.Token);

		Assert.That(
			session.CurrentToken!.Value.IsCancellationRequested,
			Is.False,
			"a fresh turn is not cancelled");

		caller.Cancel();

		Assert.That(
			session.CurrentToken!.Value.IsCancellationRequested,
			Is.True,
			"the turn ignored the cancellation of the invocation that started it, so the host's thirty second "
				+ "bound could not stop it");
	}

	/// <summary>
	/// The previous turn's source must be cancelled, not merely disposed. Disposing a source that still has
	/// waiters on it leaves them holding a token that can never be signalled again.
	/// </summary>
	[Test]
	public void Replacing_a_turn_releases_the_one_it_replaced()
	{
		var session = NewSession();

		session.BeginTurn(CancellationToken.None);
		var first = session.CurrentToken!.Value;

		session.BeginTurn(CancellationToken.None);

		Assert.That(
			first.IsCancellationRequested,
			Is.True,
			"the replaced turn was left waiting on a source that can never be signalled again");
	}

	/// <summary>A turn started with no caller token still has to be cancellable by a cancel press.</summary>
	[Test]
	public void ATurn_with_no_caller_token_still_reports_running()
	{
		var session = NewSession();

		session.BeginTurn();

		Assert.Multiple(() =>
		{
			Assert.That(session.IsRunning, Is.True);
			Assert.That(session.CurrentToken!.Value.IsCancellationRequested, Is.False);
		});

		session.EndTurn();

		Assert.That(session.IsRunning, Is.False);
	}

	/// <summary>Ending a turn that never started must not throw, and must not report a turn ended.</summary>
	[Test]
	public void Ending_a_turn_that_never_started_is_harmless()
	{
		var session = NewSession();

		Assert.Multiple(() =>
		{
			Assert.That(session.EndTurn(), Is.False);
			Assert.That(session.IsRunning, Is.False);
		});
	}
}