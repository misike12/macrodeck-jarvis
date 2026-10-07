using System.Text;
using Jarvis.Plugin.Llm;
using Serilog;

namespace Jarvis.Plugin.Core;

public sealed record TurnResult(bool Ok, string Reply, ModelFailure Failure, string Detail);

/// <summary>
/// Runs one conversation turn: ask the model, let it call tools, feed the results back, repeat until it
/// produces a final answer or the iteration cap is reached. The cap exists because the NVIDIA free
/// tier is rate limited per model and an uncapped loop would exhaust it in one turn.
/// </summary>
public sealed class ConversationRunner(
	ChatClient chat,
	ToolRegistry tools,
	AssistantSession session,
	AssistantStateHolder state,
	JarvisSettingsStore settings,
	ILogger logger)
{
	private readonly ILogger _logger = logger.ForContext<ConversationRunner>();

	public async Task<TurnResult> RunAsync(
		string userText,
		IReadOnlyList<ChatMessage> history,
		CancellationToken cancellationToken)
	{
		var current = settings.Current;
		var conversation = new List<ChatMessage>(history)
		{
			ChatMessage.System(PersonaResolver.BuildSystemPrompt(current)),
			ChatMessage.User(userText),
		};

		var finalReply = new StringBuilder();
		var maxIterations = Math.Clamp(current.MaxIterations, JarvisFields.MinIterations, JarvisFields.MaxIterations);

		for (var iteration = 0; iteration < maxIterations; iteration++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var request = new ChatRequest
			{
				Model = current.LlmModel,
				Messages = conversation,
				Tools = tools.Definitions,
			};

			state.Transition(AssistantState.Thinking);

			var builder = new StringBuilder();
			var (completion, failure, detail) = await chat.CompleteAsync(
				request,
				delta =>
				{
					builder.Append(delta);
					state.Transition(AssistantState.Thinking, reply: builder.ToString());
				},
				cancellationToken).ConfigureAwait(false);

			if (failure != ModelFailure.None || completion is null)
			{
				state.Transition(AssistantState.Error, statusLine: failure.ToString());
				return new TurnResult(false, builder.ToString(), failure, detail);
			}

			if (!completion.WantsTools)
			{
				finalReply.Append(completion.Content);
				state.Transition(AssistantState.Speaking, reply: finalReply.ToString());
				return new TurnResult(true, finalReply.ToString(), ModelFailure.None, string.Empty);
			}

			conversation.Add(ChatMessage.Assistant(completion.Content, completion.ToolCalls));

			foreach (var call in completion.ToolCalls)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (session.PendingConfirmation is not null)
				{
					return new TurnResult(false, finalReply.ToString(), ModelFailure.None, "confirmation-required");
				}

				state.Transition(AssistantState.Executing, statusLine: call.Name);

				var outcome = await tools.InvokeAsync(call, cancellationToken).ConfigureAwait(false);
				conversation.Add(ChatMessage.ToolResult(call.Id, outcome.Content));

				_logger.Debug("Tool {Tool} ok={Ok}.", call.Name, outcome.Ok);
			}
		}

		finalReply.Append("I stopped after the number of steps allowed for one request.");
		state.Transition(AssistantState.Speaking, reply: finalReply.ToString());

		return new TurnResult(true, finalReply.ToString(), ModelFailure.None, string.Empty);
	}
}