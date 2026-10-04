using Jarvis.Plugin.Llm;
using Jarvis.Plugin.Memory;
using Jarvis.Plugin.Speech;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace Jarvis.Plugin.Core;

public enum ActivateMode
{
	OneShot,
	Conversation,
}

/// <summary>
/// Owns one assistant turn from end to end. Actions, the config flow and the widget all go through
/// here, so there is exactly one place that can move the state machine and exactly one cancellation
/// token per turn.
/// </summary>
/// <remarks>
/// On the first turn of a process, and only then, the history the model is given is seeded from the
/// persisted transcript and the persisted notes. Without this the transcript and the notes file were
/// written on every turn and never read back, so the plugin remembered things and then behaved as
/// though it had not.
/// </remarks>
public sealed class AssistantSession : IAsyncDisposable
{
	private readonly AssistantStateHolder _state;
	private readonly JarvisSettingsStore _settings;
	private readonly Func<ConversationRunner> _conversation;
private readonly VoiceService _voice;
	private readonly MemoryStore _memory;
	private readonly BargeInDetector? _bargeIn;
	private readonly ILogger _logger;
	private readonly Lock _turnGate = new();
	private readonly Dictionary<Guid, ProcessTracker> _runningCommands = [];

	/// <summary>
	/// Holds every command the plugin starts, so that closing the job terminates them even if this process
	/// is killed outright and no cleanup of ours runs.
	/// </summary>
	private JobObject? _job;
	private readonly List<ChatMessage> _history = [];

	private CancellationTokenSource? _turnCts;
	private TaskCompletionSource<bool>? _decision;
	private Guid? _turnMarker;
	private string _lastOutput = string.Empty;

	public AssistantSession(
		AssistantStateHolder state,
		JarvisSettingsStore settings,
		Func<ConversationRunner> conversation,
VoiceService voice,
		MemoryStore memory,
		ILogger logger,
		BargeInDetector? bargeIn = null)
	{
		_state = state;
		_settings = settings;
		_conversation = conversation;
		_voice = voice;
		_memory = memory;
_bargeIn = bargeIn;
		_logger = logger.ForContext<AssistantSession>();
	}

	public string LastOutput => _lastOutput;

	/// <summary>
	/// The job every command is placed in. Exposed so the shell tool can contain the processes it starts;
	/// the tool has no way to create a job of its own that outlives one call.
	/// <para>
	/// Created on first use rather than in the constructor. A job is a kernel object with a handle, and the
	/// host constructs every integration and handler as part of <c>Build()</c> validation, so creating one
	/// there meant a handle was opened during a configuration check on a graph that might be thrown away.
	/// One per session rather than per command, because one per command would leak handles for the life of
	/// the process.
	/// </para>
	/// </summary>
	public JobObject? Job => _job ??= JobObject.TryCreate(_logger);

	public AssistantSnapshot StateSnapshot => _state.Current;

	public bool IsRunning
	{
		get
		{
			lock (_turnGate)
			{
				return _turnCts is not null;
			}
		}
	}

public ActionResult Cancel(bool killRunningCommand)
	{
		var settings = _settings.Current;
		var depth = killRunningCommand || settings.CancelDepth == CancelDepth.StopRunningCommand
			? CancelDepth.StopRunningCommand
			: CancelDepth.SpeechAndStream;

		EndTurn();

		if (depth == CancelDepth.StopRunningCommand)
		{
			KillRunningCommands();
		}

		_state.Reset();

		return ActionResult.Success();
	}

public async Task<ActionResult> SayAsync(string prompt, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(prompt))
		{
			return ActionResult.Failed(
				ActionErrorCodes.InvalidParameter,
				MacroDeckStrings.Validation.Required(Strings.Actions.Say.Prompt.Label()));
		}

// The notes file is read here so the prompt is built from what is actually on disk. Doing it per turn
		// rather than at construction is what lets a user edit notes.txt by hand and have the next turn
		// notice without restarting the plugin.
		var settings = _settings.Current with { NotesFileText = _memory.Notes };

		if (!settings.HasLlmCredentials)
		{
			return ActionResult.Failed(ActionErrorCodes.NotConfigured, Strings.Errors.NoModelConfigured());
		}

BeginTurn(cancellationToken);

		// The turn token, which is linked to the caller's. Reading it back rather than using the caller's
		// directly is what lets a cancel press stop the turn as well as the host.
		var token = CurrentToken ?? cancellationToken;
		_state.Transition(AssistantState.Thinking, statusLine: "text", transcript: prompt, reply: string.Empty, amplitude: 0);

		try
		{
			ChatMessage[] history;
			lock (_turnGate)
			{
				SeedFromMemoryOnce(settings);

				history = _history.TakeLast(MaxHistoryMessages).ToArray();
			}

			var result = await _conversation().RunAsync(prompt, history, token).ConfigureAwait(false);

			if (result.Failure == ModelFailure.RateLimited)
			{
				return ActionResult.Failed(ActionErrorCodes.ProviderError, Strings.Errors.RateLimited());
			}

			if (result.Failure == ModelFailure.Unauthorized)
			{
				return ActionResult.Failed(ActionErrorCodes.PermissionDenied, Strings.Errors.ProviderRejected());
			}

			if (result.Failure is ModelFailure.Unreachable or ModelFailure.Timeout)
			{
				return ActionResult.Failed(ActionErrorCodes.NotConnected, Strings.Errors.ProviderUnreachable());
			}

			if (!result.Ok && result.Detail == "confirmation-required")
			{
				return ActionResult.Accepted(Strings.States.Confirming());
			}

			lock (_turnGate)
			{
				_history.Add(ChatMessage.User(prompt));
				_history.Add(ChatMessage.Assistant(result.Reply, []));

				// Remembered after the turn succeeded, so a failed call is not written into history as
				// something that was said.
				_memory.Remember("user", prompt, _settings.Current.Memory);
				_memory.Remember("assistant", result.Reply, _settings.Current.Memory);
			}

			await SpeakReplyAsync(result.Reply, token).ConfigureAwait(false);

			_state.Transition(AssistantState.Idle, reply: result.Reply);
			return ActionResult.Success();
		}
catch (OperationCanceledException)
		{
			// Not a success. The turn was asked to stop and produced no answer, and a host that released the
			// slot would record that as a completed press.
			_state.Reset();
			return ActionResult.Failed(ActionErrorCodes.Timeout, Strings.Errors.TurnCancelled());
		}
		finally
		{
			EndTurn();
		}
	}

	/// <summary>
	/// Speaks the reply while the turn is still held, so the orb stays in <c>speaking</c> until the
	/// clip ends and a cancel press interrupts it. Speaking never decides the action result: a silent
	/// failure here must not turn a good answer into a failed turn.
	/// </summary>
private async Task SpeakReplyAsync(string reply, CancellationToken cancellationToken)
	{
		var current = _settings.Current;

		if (!current.SpeakReplies || string.IsNullOrWhiteSpace(reply))
		{
			return;
		}

try
		{
			// Barge-in only watches while there is something to interrupt, and only when the user asked for
			// it. Both are checked here rather than inside the detector so the reason it is off is visible in
			// this one place.
			var bargeIn = _bargeIn;
			var watching = bargeIn is not null && current.BargeInEnabled && _voice.IsSpeaking;

			if (watching)
			{
				bargeIn!.Threshold = current.BargeInThreshold;
				bargeIn.Start();
			}

			try
			{
				await _voice.SpeakAsync(reply, cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				bargeIn?.Stop();
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Warning(exception, "The reply could not be spoken.");
		}
	}

	/// <summary>
	/// How long one turn may run before it is abandoned.
	/// <para>
	/// The host cancels a capability invocation after thirty seconds and takes the concurrency slot back, so
	/// a turn that keeps going past that point is work nobody is waiting for. The margin is deliberate: the
	/// budget has to expire early enough to unwind, log and return a result before the host gives up on the
	/// slot itself.
	/// </para>
	/// <para>
	/// Applied by the action executors and not here, because the limit belongs to a deck button press. The
	/// hotkey and the wake word are not capability invocations, have no thirty second ceiling, and are
	/// legitimately allowed to run for as long as a person takes to speak a sentence and hear the answer.
	/// </para>
	/// </summary>
	public static TimeSpan ActionBudget => TimeSpan.FromSeconds(20);

	private const int MaxHistoryMessages = 20;

	private bool _seeded;

	/// <summary>
	/// Loads the persisted transcript into the live history, once per process.
	/// <para>
	/// Skipped for a mode of <see cref="MemoryMode.None"/>, and skipped once memory is off the persisted
	/// file is not read at all, because a user who turned memory off should not have their old transcript
	/// read back into a prompt just because the process happened to restart.
	/// </para>
	/// </summary>
	private void SeedFromMemoryOnce(JarvisSettings settings)
	{
		if (_seeded || settings.Memory == MemoryMode.None)
		{
			return;
		}

		_seeded = true;

		foreach (var entry in _memory.LoadHistory(settings.Memory))
		{
			if (string.IsNullOrWhiteSpace(entry.Text))
			{
				continue;
			}

			_history.Add(entry.Role switch
			{
				"user" => ChatMessage.User(entry.Text),
				_ => ChatMessage.Assistant(entry.Text, []),
			});
		}

		if (_history.Count > 0)
		{
			_logger.Information("Resumed with {Count} remembered message(s).", _history.Count);
		}
	}

	public void RecordCommandOutput(string output) => _lastOutput = output;

	/// <summary>
	/// The safety gate refused an action, so the turn moves to <see cref="AssistantState.Confirming"/>
	/// and holds the request for the user to approve or dismiss.
	/// </summary>
public PendingConfirmation? PendingConfirmation { get; private set; }

	/// <summary>
	/// Completes when the user answers the pending confirmation. Absent until something asks, so a caller
	/// that reaches here with nothing pending is waiting for nothing.
	/// </summary>
	public Task<bool> WaitForDecisionAsync(CancellationToken cancellationToken) =>
		_decision is null
			? Task.FromResult(false)
			: _decision.Task.WaitAsync(cancellationToken);

	/// <summary>
	/// Records that a tool wants confirmation and moves to <see cref="AssistantState.Confirming"/>.
	/// <para>
	/// This used to be the end of the road: nothing ever answered, so under the default safety mode every
	/// tool that needed confirmation was permanently blocked and the assistant could only read things. The
	/// turn now waits here instead, which is what makes "confirm everything" a policy rather than a denial.
	/// </para>
	/// </summary>
	public void RequestConfirmation(string toolName, string argumentsJson)
	{
		lock (_turnGate)
		{
			PendingConfirmation = new PendingConfirmation(toolName, argumentsJson);
			_decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		_state.Transition(AssistantState.Confirming, statusLine: toolName);
	}

	/// <summary>
	/// Answers the pending confirmation. Approving lets the waiting turn continue; refusing is treated
	/// exactly like a refusal by the user, which is the point.
	/// </summary>
	public void ResolveConfirmation(bool approved)
	{
		TaskCompletionSource<bool>? decision;

		lock (_turnGate)
		{
			PendingConfirmation = null;
			decision = _decision;
			_decision = null;
		}

		decision?.TrySetResult(approved);

		if (!approved)
		{
			_state.Reset();
		}
	}

	/// <summary>
	/// Fails whatever is waiting, so a turn that is waiting on a decision does not outlive a cancel or a
	/// config reload. Called from <see cref="EndTurn"/>.
	/// </summary>
	private void ReleaseDecision()
	{
		lock (_turnGate)
		{
			PendingConfirmation = null;
			_decision?.TrySetResult(false);
			_decision = null;
		}
	}

	public IReadOnlyList<string> RunningCommandIds()
	{
		lock (_turnGate)
		{
			return _runningCommands.Keys.Select(id => id.ToString()).ToArray();
		}
	}

/// <summary>
	/// Starts a turn.
	/// <para>
	/// <paramref name="request"/> is the token of whatever asked for the turn, and the turn token is linked
	/// to it. That link is the only reason the host's thirty-second capability bound can stop a turn: an
	/// unlinked source cannot be cancelled from outside, so a press the host gave up on would keep
	/// answering, still holding one of thirty-two concurrency slots.
	/// </para>
	/// </summary>
	public void BeginTurn(CancellationToken? request = null)
	{
		CancellationTokenSource? previous;

		lock (_turnGate)
		{
			previous = _turnCts;

			_turnCts = request is { } requested && requested.CanBeCanceled
				? CancellationTokenSource.CreateLinkedTokenSource(requested)
				: new CancellationTokenSource();

			_turnMarker = Guid.CreateVersion7();
		}

		// Cancelled before disposal. Disposing a source that still has waiters on it leaves them holding a
		// token that can never be signalled again, so a turn replaced by a racing second press would hang
		// rather than stop.
		if (previous is not null)
		{
			previous.Cancel();
			previous.Dispose();
		}
	}

	public CancellationToken? CurrentToken
	{
		get
		{
			lock (_turnGate)
			{
				return _turnCts?.Token;
			}
		}
	}

	public bool EndTurn()
	{
		CancellationTokenSource? cts;

		lock (_turnGate)
		{
			cts = _turnCts;
			_turnCts = null;
			_turnMarker = null;
		}

if (cts is null)
		{
			return false;
		}

		// A turn waiting on a confirmation must be released even when there is no turn token to cancel,
		// or the waiting tool call outlives the press that started it.
		ReleaseDecision();

		_voice.Stop();

		try
		{
			cts.Cancel();
		}
		catch (ObjectDisposedException)
		{
			return false;
		}
		finally
		{
			cts.Dispose();
		}

		return true;
	}

	private void KillRunningCommands()
	{
		ProcessTracker[] trackers;

		lock (_turnGate)
		{
			trackers = _runningCommands.Values.ToArray();
			_runningCommands.Clear();
		}

		foreach (var tracker in trackers)
		{
			try
			{
				tracker.Kill();
			}
			catch (InvalidOperationException exception)
			{
				_logger.Debug(exception, "Command {CommandId} had already exited.", tracker.Id);
			}
		}
	}

	public void TrackCommand(Guid id, ProcessTracker tracker)
	{
		lock (_turnGate)
		{
			_runningCommands[id] = tracker;
		}
	}

	public void ReleaseCommand(Guid id)
	{
		lock (_turnGate)
		{
			_runningCommands.Remove(id);
		}
	}

public ValueTask DisposeAsync()
	{
		EndTurn();
		KillRunningCommands();

		// Closing the job is what guarantees no command outlives the plugin, including when this dispose
		// never runs because the process was killed instead.
		_job?.Dispose();
		_job = null;

		return ValueTask.CompletedTask;
	}
}