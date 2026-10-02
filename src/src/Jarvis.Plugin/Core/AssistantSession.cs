using Jarvis.Plugin.Llm;
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
	WaitForWakeWord,
}

/// <summary>
/// Owns one assistant turn from end to end. Actions, the config flow and the widget all go through
/// here, so there is exactly one place that can move the state machine and exactly one cancellation
/// token per turn.
/// </summary>
public sealed class AssistantSession : IAsyncDisposable
{
	private readonly AssistantStateHolder _state;
	private readonly JarvisSettingsStore _settings;
	private readonly Func<ConversationRunner> _conversation;
	private readonly VoiceService _voice;
	private readonly ILogger _logger;
	private readonly Lock _turnGate = new();
	private readonly Dictionary<Guid, ProcessTracker> _runningCommands = [];
	private readonly List<ChatMessage> _history = [];

	private CancellationTokenSource? _turnCts;
	private Guid? _turnMarker;
	private string _lastOutput = string.Empty;

	public AssistantSession(
		AssistantStateHolder state,
		JarvisSettingsStore settings,
		Func<ConversationRunner> conversation,
		VoiceService voice,
		ILogger logger)
	{
		_state = state;
		_settings = settings;
		_conversation = conversation;
		_voice = voice;
		_logger = logger.ForContext<AssistantSession>();
	}

	public string LastOutput => _lastOutput;

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

	public ActionResult Activate(ActivateMode mode, string? prompt, bool waitForWakeWord, int timeoutSeconds)
	{
		lock (_turnGate)
		{
			if (_turnCts is not null)
			{
				return ActionResult.Accepted(Strings.Errors.AlreadyRunning());
			}
		}

		var settings = _settings.Current;

		if (!settings.HasLlmCredentials)
		{
			return ActionResult.Failed(ActionErrorCodes.NotConfigured, Strings.Errors.NoModelConfigured());
		}

		var effectiveMode = waitForWakeWord ? ActivateMode.WaitForWakeWord : mode;

		_state.Transition(
			effectiveMode == ActivateMode.WaitForWakeWord
				? AssistantState.Listening
				: AssistantState.Thinking,
			statusLine: effectiveMode.ToString(),
			transcript: prompt ?? string.Empty,
			reply: string.Empty,
			amplitude: 0);

		BeginTurn();

		return ActionResult.Success();
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

	public ActionResult Toggle(ActivateMode mode)
	{
		return IsRunning ? Cancel(false) : Activate(mode, null, false, _settings.Current.ConversationTimeoutSeconds);
	}

	public async Task<ActionResult> SayAsync(string prompt, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(prompt))
		{
			return ActionResult.Failed(
				ActionErrorCodes.InvalidParameter,
				MacroDeckStrings.Validation.Required(Strings.Actions.Say.Prompt.Label()));
		}

		var settings = _settings.Current;

		if (!settings.HasLlmCredentials)
		{
			return ActionResult.Failed(ActionErrorCodes.NotConfigured, Strings.Errors.NoModelConfigured());
		}

		BeginTurn();

		var token = CurrentToken ?? cancellationToken;
		_state.Transition(AssistantState.Thinking, statusLine: "text", transcript: prompt, reply: string.Empty, amplitude: 0);

		try
		{
			ChatMessage[] history;
			lock (_turnGate)
			{
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
			}

			await SpeakReplyAsync(result.Reply, token).ConfigureAwait(false);

			_state.Transition(AssistantState.Idle, reply: result.Reply);
			return ActionResult.Success();
		}
		catch (OperationCanceledException)
		{
			_state.Reset();
			return ActionResult.Success();
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
		if (!_settings.Current.SpeakReplies || string.IsNullOrWhiteSpace(reply))
		{
			return;
		}

		try
		{
			await _voice.SpeakAsync(reply, cancellationToken).ConfigureAwait(false);
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

	private const int MaxHistoryMessages = 20;

	public void RecordCommandOutput(string output) => _lastOutput = output;

	/// <summary>
	/// The safety gate refused an action, so the turn moves to <see cref="AssistantState.Confirming"/>
	/// and holds the request for the user to approve or dismiss.
	/// </summary>
	public void RequestConfirmation(string toolName, string argumentsJson)
	{
		PendingConfirmation = new PendingConfirmation(toolName, argumentsJson);
		_state.Transition(AssistantState.Confirming, statusLine: toolName);
	}

	public PendingConfirmation? PendingConfirmation { get; private set; }

	public void ResolveConfirmation(bool approved)
	{
		PendingConfirmation = null;
		_state.Reset();
	}

	public IReadOnlyList<string> RunningCommandIds()
	{
		lock (_turnGate)
		{
			return _runningCommands.Keys.Select(id => id.ToString()).ToArray();
		}
	}

	public void BeginTurn()
	{
		lock (_turnGate)
		{
			_turnCts?.Dispose();
			_turnCts = new CancellationTokenSource();
			_turnMarker = Guid.CreateVersion7();
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
		return ValueTask.CompletedTask;
	}
}