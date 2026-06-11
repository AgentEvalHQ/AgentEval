// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text;
using AgentEval.Core;       // IEvaluableAgent, AgentResponse, ISessionResettableAgent
using AgentEval.Testing;    // Turn

namespace AgentEval.RedTeam;

/// <summary>
/// Drives a one-shot <see cref="IEvaluableAgent"/> as a conversation by flattening the running transcript into a
/// single prompt each turn (Wave C). Lower fidelity — the SUT sees a transcript, not a real session — so it reports
/// <see cref="ConversationFidelity.Flattened"/>. If the agent is <see cref="ISessionResettableAgent"/> it is reset on
/// dispose so a reused instance does not leak state across seeds.
/// </summary>
internal sealed class StatelessConversationAdapter(IEvaluableAgent agent) : IAgentConversation
{
    private readonly List<Turn> _history = [];

    /// <inheritdoc />
    public ConversationFidelity Fidelity => ConversationFidelity.Flattened;

    /// <inheritdoc />
    public IReadOnlyList<Turn> History => _history;

    /// <inheritdoc />
    public async Task<AgentResponse> SendAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userMessage);
        _history.Add(Turn.User(userMessage));

        var sb = new StringBuilder();
        foreach (var t in _history)
            sb.Append(t.Role).Append(": ").AppendLine(t.Content);
        sb.Append("assistant:");   // cue the next assistant turn

        var response = await agent.InvokeAsync(sb.ToString(), cancellationToken).ConfigureAwait(false);
        _history.Add(Turn.Assistant(response.Text));
        return response;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (agent is ISessionResettableAgent resettable)
            await resettable.ResetSessionAsync().ConfigureAwait(false);
    }
}
