// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Microsoft365;

/// <summary>
/// A Microsoft 365 agent (over Work IQ A2A) as an <see cref="IEvaluableAgent"/>. Each <see cref="InvokeAsync"/>
/// runs one turn on <see cref="Conversation"/> under <see cref="Options"/>: the turn budget is checked first
/// (<see cref="WorkIqTurnBudgetExceededException"/> before any send), the conversation policy decides whether the
/// context is reset, then the completion gate applies — an <see cref="AgentResponse"/> is returned only for
/// <see cref="WorkIqTurnOutcome.Completed"/> (text = selected answer, <c>AdditionalProperties[Microsoft365Evidence.Key]</c>
/// = evidence; <c>RawMessages</c>, <c>TokenUsage</c> and <c>ModelId</c> stay null because nothing is inferred);
/// every other outcome throws <see cref="WorkIqTurnNotCompletedException"/>, and a local cancellation throws
/// <see cref="WorkIqTurnCanceledException"/>. The optional <c>onTurn</c> sink receives every immutable result
/// <b>before</b> an exception is thrown, so a run can persist interrupted evidence. The agent never approves a
/// card, never signs in inside a metric and never retries a send. Not thread-safe: one agent, one conversation.
/// </summary>
public sealed class WorkIqEvaluableAgent : IEvaluableAgent, ISessionResettableAgent, IAsyncDisposable
{
    private readonly Action<WorkIqTurnResult>? _onTurn;

    /// <summary>Creates the agent over a conversation (the live path uses <see cref="Microsoft365AgentFactory.BuildLive"/>; tests pass a fake).</summary>
    /// <param name="conversation">The conversation the agent owns and disposes.</param>
    /// <param name="name">The report-facing name (non-blank).</param>
    /// <param name="options">Runtime options; null means defaults. Validated here.</param>
    /// <param name="onTurn">Optional sink invoked with every turn result (completed or not) before the agent returns or throws.</param>
    public WorkIqEvaluableAgent(IWorkIqConversation conversation, string name, WorkIqOptions? options = null, Action<WorkIqTurnResult>? onTurn = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Conversation = conversation;
        Name = name;
        Options = options ?? new WorkIqOptions();
        Options.Validate();
        _onTurn = onTurn;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>The conversation this agent runs its turns on.</summary>
    public IWorkIqConversation Conversation { get; }

    /// <summary>The validated runtime options.</summary>
    public WorkIqOptions Options { get; }

    /// <summary>Completed turns counted so far (the quantity <see cref="WorkIqOptions.MaxTurns"/> caps); turns are counted, consumption is not metered.</summary>
    public int TurnsUsed { get; private set; }

    /// <summary>Whether a turn sink was supplied.</summary>
    internal bool HasTurnSink => _onTurn is not null;

    /// <inheritdoc/>
    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(M24)");

    /// <inheritdoc/>
    public Task ResetSessionAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(M24)");

    /// <inheritdoc/>
    public ValueTask DisposeAsync() =>
        throw new NotImplementedException("TODO(M24)");
}
