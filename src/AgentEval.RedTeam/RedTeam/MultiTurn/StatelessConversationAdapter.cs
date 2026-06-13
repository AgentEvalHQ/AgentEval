// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text;
using AgentEval.Core;       // IEvaluableAgent, AgentResponse, ISessionResettableAgent
using AgentEval.Testing;    // Turn

namespace AgentEval.RedTeam;   // CanaryTool / IToolCapableAgent share this namespace (under RedTeam/Harness)

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

    /// <summary>
    /// Tool-aware send (Wave B↔C): if the wrapped agent is an <see cref="IToolCapableAgent"/>, the flattened
    /// transcript is sent over its real tool channel so a multi-turn <see cref="IToolAwareAttack"/> can exercise a
    /// genuine tool boundary; otherwise it degrades honestly to the text-only path (the base default, tools ignored).
    /// </summary>
    public async Task<AgentResponse> SendAsync(string userMessage, IReadOnlyList<CanaryTool> tools, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userMessage);
        ArgumentNullException.ThrowIfNull(tools);

        if (agent is not IToolCapableAgent toolAgent || tools.Count == 0)
            return await SendAsync(userMessage, cancellationToken).ConfigureAwait(false);

        _history.Add(Turn.User(userMessage));

        var sb = new StringBuilder();
        foreach (var t in _history)
            sb.Append(t.Role).Append(": ").AppendLine(t.Content);
        sb.Append("assistant:");   // cue the next assistant turn

        var response = await toolAgent.InvokeWithToolsAsync(sb.ToString(), tools, cancellationToken).ConfigureAwait(false);
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
