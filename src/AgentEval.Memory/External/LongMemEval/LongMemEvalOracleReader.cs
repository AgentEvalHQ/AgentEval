// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using Microsoft.Extensions.AI;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Retrieval-bypassing answer adapter used only for evaluator-projected oracle history.
/// It owns no memory provider and clears all per-question state after every invocation.
/// </summary>
internal sealed class LongMemEvalOracleReader(IChatClient answerClient)
    : IEvaluableAgent, IHistoryInjectableAgent, ISessionResettableAgent
{
    private readonly IChatClient _answerClient =
        answerClient ?? throw new ArgumentNullException(nameof(answerClient));
    private readonly List<(string UserMessage, string AssistantResponse)> _history = [];

    public string Name => "LongMemEval Oracle Reader";

    public void InjectConversationHistory(
        IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns)
    {
        ArgumentNullException.ThrowIfNull(conversationTurns);
        _history.Clear();
        _history.AddRange(conversationTurns);
    }

    public async Task<AgentResponse> InvokeAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var messages = new List<ChatMessage>(_history.Count * 2 + 1);
        foreach (var (userMessage, assistantResponse) in _history)
        {
            messages.Add(new ChatMessage(ChatRole.User, userMessage));
            messages.Add(new ChatMessage(ChatRole.Assistant, assistantResponse));
        }
        messages.Add(new ChatMessage(ChatRole.User, prompt));

        try
        {
            var response = await _answerClient.GetResponseAsync(
                messages,
                cancellationToken: cancellationToken);
            return new AgentResponse { Text = response.Text ?? string.Empty };
        }
        finally
        {
            _history.Clear();
        }
    }

    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _history.Clear();
        return Task.CompletedTask;
    }
}
