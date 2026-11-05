// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using AgentEval.Core;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365;

/// <summary>
/// A Microsoft 365 agent (over Work IQ A2A) as an <see cref="IEvaluableAgent"/>. Each <see cref="InvokeAsync"/>
/// runs one turn on <see cref="Conversation"/> under <see cref="Options"/>: the turn budget is checked first
/// (<see cref="WorkIqTurnBudgetExceededException"/> before any send), the conversation policy decides whether the
/// context is reset, then the completion gate applies — an <see cref="AgentResponse"/> is returned only for
/// <see cref="WorkIqTurnOutcome.Completed"/> (text = selected answer, <c>AdditionalProperties[Microsoft365Evidence.Key]</c>
/// = evidence; <c>RawMessages</c>, <c>TokenUsage</c> and <c>ModelId</c> stay null because nothing is inferred; a turn
/// that completed with no selected answer is marked with <see cref="CompletedNoAnswerFinishReason"/> and
/// <c>AdditionalProperties[Microsoft365Evidence.NoAnswerSelectedKey] = true</c>, so its empty text is never mistaken
/// for an empty answer);
/// every other outcome throws <see cref="WorkIqTurnNotCompletedException"/>, and a local cancellation throws
/// <see cref="WorkIqTurnCanceledException"/>. The optional <c>onTurn</c> sink receives every immutable result
/// <b>before</b> an exception is thrown, so a run can persist interrupted evidence. The agent never approves a
/// card, never signs in inside a metric and never retries a send. Not thread-safe: one agent, one conversation.
/// </summary>
public sealed class WorkIqEvaluableAgent : IEvaluableAgent, ISessionResettableAgent, IAsyncDisposable
{
    /// <summary>The <see cref="AgentResponse.FinishReason"/> of a completed turn with a selected answer (an empty answer included).</summary>
    public const string CompletedFinishReason = "completed";

    /// <summary>
    /// The <see cref="AgentResponse.FinishReason"/> of a turn that completed with <b>no selected answer</b>: the task
    /// carried several answer candidates and none was chosen (<c>Ambiguous</c>), or no answer artifact at all
    /// (<c>None</c>). The response text is then an empty string that is not the agent's answer, so a grader sees an empty
    /// string; <c>AdditionalProperties[<see cref="Microsoft365Evidence.NoAnswerSelectedKey"/>]</c> is <see langword="true"/>
    /// and the evidence document keeps every candidate. Not an error: the task did complete.
    /// </summary>
    public const string CompletedNoAnswerFinishReason = "completed-no-answer";

    private readonly Action<WorkIqTurnResult>? _onTurn;
    private readonly List<IAsyncDisposable> _owned = [];
    private int _caseCounter;
    private int _disposed;

    /// <summary>Creates the agent over a conversation (the live path uses <see cref="Microsoft365AgentFactory.BuildLive"/>; tests pass a fake).</summary>
    /// <param name="conversation">The conversation the agent owns and disposes.</param>
    /// <param name="name">The report-facing name (non-blank).</param>
    /// <param name="options">Runtime options; null means defaults. Validated here.</param>
    /// <param name="onTurn">Optional sink invoked with every turn result (completed or not) before the agent returns or throws.</param>
    /// <param name="diagnostics">The diagnostics the agent traces budget, policy and outcome events through; null means <see cref="WorkIqDiagnostics.None"/>.</param>
    public WorkIqEvaluableAgent(IWorkIqConversation conversation, string name, WorkIqOptions? options = null, Action<WorkIqTurnResult>? onTurn = null, WorkIqDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Conversation = conversation;
        Name = name;
        Options = options ?? new WorkIqOptions();
        Options.Validate();
        _onTurn = onTurn;
        Diagnostics = diagnostics ?? WorkIqDiagnostics.None;
    }

    /// <summary>The diagnostics this agent traces through (<see cref="WorkIqDiagnostics.None"/> when none was given); never null.</summary>
    public WorkIqDiagnostics Diagnostics { get; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>The conversation this agent runs its turns on.</summary>
    public IWorkIqConversation Conversation { get; }

    /// <summary>The validated runtime options.</summary>
    public WorkIqOptions Options { get; }

    /// <summary>
    /// Turns counted so far (the quantity <see cref="WorkIqOptions.MaxTurns"/> caps): every turn that returned a typed
    /// result, completed or not — a failed, paused, canceled or indeterminate turn, and also one that failed before its
    /// prompt went out (a sign-in or discovery failure). Only a turn refused before it started (the cap itself, a disposed
    /// agent) is not counted. Turns are counted, consumption is not metered.
    /// </summary>
    public int TurnsUsed { get; private set; }

    /// <summary>
    /// The case id the <b>next</b> <see cref="InvokeAsync"/> records in its turn identity (one-shot: consumed by that
    /// call). Null means the agent numbers the turn itself (<c>case-1</c>, <c>case-2</c>, …). Set it per dataset case
    /// so the evidence and the trace can be correlated with the harness's own case id.
    /// </summary>
    public string? NextCaseId { get; set; }

    /// <summary>Whether a turn sink was supplied.</summary>
    internal bool HasTurnSink => _onTurn is not null;

    /// <summary>Resources the factory handed over, disposed (in reverse order) after the conversation by <see cref="DisposeAsync"/>.</summary>
    internal IList<IAsyncDisposable> Owned => _owned;

    /// <inheritdoc/>
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ThrowIfDisposed();

        if (Options.MaxTurns > 0 && TurnsUsed >= Options.MaxTurns)
        {
            var budget = new WorkIqTurnBudgetExceededException(TurnsUsed, Options.MaxTurns);
            Diagnostics.Trace(WorkIqTraceStage.Budget, "budget.exceeded", budget.Message, WorkIqTraceLevel.Error);
            Diagnostics.Issue(WorkIqIssue.Create(
                WorkIqTraceStage.Budget,
                WorkIqIssueCategory.Budget,
                "budget.exceeded",
                $"MaxTurns ({Options.MaxTurns}) reached after {TurnsUsed} turn(s); the next send was refused and the run stops.",
                "The agent's MaxTurns cap bounds the number of sends per agent instance; raise it only after checking the tenant's own usage reporting."));
            throw budget;
        }

        var caseId = TakeCaseId();
        var turnId = Guid.NewGuid().ToString("D");
        Diagnostics.Trace(
            WorkIqTraceStage.Outcome,
            "agent.turn_started",
            $"turn {TurnsUsed + 1}{(Options.MaxTurns > 0 ? $" of at most {Options.MaxTurns}" : "")} under {Options.ConversationPolicy} ({prompt.Length} prompt characters)",
            caseId: caseId,
            turnId: turnId);

        if (Options.ConversationPolicy == ConversationPolicy.FreshContextPerInvocation)
        {
            await Conversation.ResetSessionAsync(cancellationToken).ConfigureAwait(false);
            Diagnostics.Trace(WorkIqTraceStage.Outcome, "agent.context_reset", "local context dropped before the turn (FreshContextPerInvocation)", caseId: caseId, turnId: turnId);
        }

        var request = new WorkIqTurnRequest(prompt, caseId, turnId);
        WorkIqTurnResult result;
        try
        {
            result = await Conversation.RunTurnAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IWorkIqTurnResultCarrier carrier)
        {
            // The turn reached the service (a typed result exists): it counts, and the sink sees it before the throw.
            TurnsUsed++;
            Observe(carrier.TurnResult, caseId, turnId);
            Diagnostics.Trace(WorkIqTraceStage.Outcome, "agent.turn_mapped", $"turn ended with {carrier.TurnResult.Outcome}; rethrowing {ex.GetType().Name}", WorkIqTraceLevel.Warning, caseId: caseId, turnId: turnId, taskId: carrier.TurnResult.Task?.TaskId, contextId: carrier.TurnResult.ContextId);
            throw;
        }

        TurnsUsed++;
        Observe(result, caseId, turnId);

        if (result.IsCompleted)
        {
            var noAnswer = result.AnswerText is null;
            if (noAnswer)
            {
                Diagnostics.Trace(
                    WorkIqTraceStage.Outcome,
                    "agent.turn_mapped",
                    $"Completed with NO selected answer (answer {result.AnswerCompleteness}) → AgentResponse with empty text and FinishReason '{CompletedNoAnswerFinishReason}'; " +
                    "the empty text is not the agent's answer (the evidence document keeps the candidates)",
                    WorkIqTraceLevel.Warning,
                    caseId: caseId,
                    turnId: turnId,
                    taskId: result.Task?.TaskId,
                    contextId: result.ContextId);
            }
            else
            {
                Diagnostics.Trace(
                    WorkIqTraceStage.Outcome,
                    "agent.turn_mapped",
                    $"Completed → AgentResponse ({result.AnswerText!.Length} answer characters, answer {result.AnswerCompleteness}, telemetry {result.Telemetry})",
                    caseId: caseId,
                    turnId: turnId,
                    taskId: result.Task?.TaskId,
                    contextId: result.ContextId);
            }

            return new AgentResponse
            {
                Text = result.AnswerText ?? "",
                AdditionalProperties = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [Microsoft365Evidence.Key] = result.Evidence,
                    [Microsoft365Evidence.NoAnswerSelectedKey] = noAnswer,
                    [Microsoft365Evidence.AnswerCompletenessKey] = result.AnswerCompleteness.ToString(),
                },
                RawMessages = null,
                TokenUsage = null,
                ModelId = null,
                FinishReason = noAnswer ? CompletedNoAnswerFinishReason : CompletedFinishReason,
            };
        }

        if (cancellationToken.IsCancellationRequested && result.Failure?.Category == WorkIqFailureCategory.Canceled)
        {
            var canceled = new WorkIqTurnCanceledException(result, cancellationToken);
            Diagnostics.Trace(WorkIqTraceStage.Outcome, "agent.turn_mapped", $"{result.Outcome} after a local cancellation → {nameof(WorkIqTurnCanceledException)}", WorkIqTraceLevel.Warning, caseId: caseId, turnId: turnId, taskId: result.Task?.TaskId, contextId: result.ContextId);
            throw canceled;
        }

        var notCompleted = new WorkIqTurnNotCompletedException(result);
        Diagnostics.Trace(WorkIqTraceStage.Outcome, "agent.turn_mapped", $"{result.Outcome} → {nameof(WorkIqTurnNotCompletedException)}: {notCompleted.Message}", WorkIqTraceLevel.Warning, caseId: caseId, turnId: turnId, taskId: result.Task?.TaskId, contextId: result.ContextId);
        throw notCompleted;
    }

    /// <inheritdoc/>
    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Conversation.ResetSessionAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await Conversation.DisposeAsync().ConfigureAwait(false);
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            await _owned[i].DisposeAsync().ConfigureAwait(false);
        }

        _owned.Clear();
    }

    private string TakeCaseId()
    {
        var explicitId = NextCaseId;
        NextCaseId = null;
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            return explicitId.Trim();
        }

        var n = Interlocked.Increment(ref _caseCounter);
        return string.Create(CultureInfo.InvariantCulture, $"case-{n}");
    }

    private void Observe(WorkIqTurnResult result, string caseId, string turnId)
    {
        if (_onTurn is null)
        {
            return;
        }

        try
        {
            _onTurn(result);
        }
        catch (Exception ex)
        {
            Diagnostics.Trace(WorkIqTraceStage.Outcome, "agent.sink_failed", $"the onTurn sink threw {ex.GetType().Name}; the turn result is still on the exception/response", WorkIqTraceLevel.Error, caseId: caseId, turnId: turnId);
            throw;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WorkIqEvaluableAgent));
        }
    }
}
