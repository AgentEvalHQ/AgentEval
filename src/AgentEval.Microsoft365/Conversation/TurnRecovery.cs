// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using A2A;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// Reconciles a turn whose task is still running. It polls <c>GetTask</c> within
/// <see cref="WorkIqOptions.RecoveryBudget"/>, records a local stop, and sends the opt-in <c>CancelTask</c> after a
/// timeout (<see cref="WorkIqOptions.CancelRemoteTaskOnTimeout"/>). The input is never resent.
/// </summary>
internal sealed class TurnRecovery
{
    /// <summary>The bound of the opt-in <c>CancelTask</c> sent after a local timeout.</summary>
    internal static readonly TimeSpan RemoteCancelTimeout = TimeSpan.FromSeconds(30);

    private readonly IWorkIqA2AClient _client;
    private readonly WorkIqOptions _options;
    private readonly TimeProvider _clock;
    private readonly ConversationState _state;
    private readonly TurnObserver _observer;
    private readonly TurnFailures _failures;

    public TurnRecovery(IWorkIqA2AClient client, WorkIqOptions options, TimeProvider clock, ConversationState state, TurnObserver observer, TurnFailures failures)
    {
        _client = client;
        _options = options;
        _clock = clock;
        _state = state;
        _observer = observer;
        _failures = failures;
    }

    /// <summary>Polls <c>GetTask</c> until the task leaves <c>SUBMITTED</c>/<c>WORKING</c>, the budget runs out, or the turn stops.</summary>
    /// <param name="ctx">The turn. It needs a task handle.</param>
    /// <param name="tokens">The turn's tokens.</param>
    public async Task ReconcileAsync(TurnContext ctx, TurnTokens tokens)
    {
        var handle = ctx.Handle ?? throw new InvalidOperationException("Reconciliation needs a task handle.");
        var taskId = handle.TaskId;
        var recoveryStart = _clock.GetUtcNow();
        var budget = _options.RecoveryBudget;
        ctx.Trace(
            WorkIqTraceStage.GetTask,
            "recovery.started",
            $"task is {ctx.Snapshot?.RawState ?? "(absent)"}: polling GetTask every {_options.RecoveryPollInterval} within a {budget} budget (the input is never resent)",
            taskId: taskId,
            contextId: ctx.ContextId,
            a2aState: ctx.Snapshot?.RawState);

        while (ctx.Snapshot is { State: WorkIqTaskState.Submitted or WorkIqTaskState.Working } current)
        {
            var remaining = budget - (_clock.GetUtcNow() - recoveryStart);
            if (remaining <= TimeSpan.Zero)
            {
                await ExhaustBudgetAsync(ctx, taskId, current, budget).ConfigureAwait(false);
                return;
            }

            if (!await WaitToPollAsync(ctx, tokens, taskId, current, remaining, budget).ConfigureAwait(false))
            {
                return;
            }

            // A poll runs only within the budget that is left after the wait, and under that bound: a GetTask that the
            // client retries (429/503 with Retry-After) cannot run past the budget.
            remaining = budget - (_clock.GetUtcNow() - recoveryStart);
            if (remaining <= TimeSpan.Zero)
            {
                continue;
            }

            if (!await PollAsync(ctx, tokens, taskId, current, remaining, budget).ConfigureAwait(false))
            {
                return;
            }
        }

        ctx.Trace(
            WorkIqTraceStage.GetTask,
            "recovery.finished",
            $"task reached {ctx.Snapshot?.RawState ?? "(absent)"} after {ctx.RecoveryRequests} GetTask poll(s)",
            taskId: taskId,
            contextId: ctx.ContextId,
            a2aState: ctx.Snapshot?.RawState);
    }

    /// <summary>
    /// After a stream or a subscription ended: a task still running is reconciled through <c>GetTask</c>, and a
    /// stream that brought neither a task nor a direct message fails as interrupted.
    /// </summary>
    /// <param name="ctx">The turn.</param>
    /// <param name="tokens">The turn's tokens.</param>
    public async Task ReconcileAfterStreamAsync(TurnContext ctx, TurnTokens tokens)
    {
        if (ctx.Failure is not null || ctx.Stopped || ctx.DirectMessage is not null)
        {
            // A failed or stopped turn is not reconciled, and a direct message ends the turn's remote work.
            return;
        }

        var operation = ctx.Operation;
        if (ctx.Snapshot is { State: WorkIqTaskState.Submitted or WorkIqTaskState.Working })
        {
            ctx.Trace(
                WorkIqIssueClassifier.StageFor(operation),
                "stream.ended_before_terminal",
                $"the {operation} stream ended without a terminal state; reconciling through GetTask (intermediate events may be missing)",
                WorkIqTraceLevel.Warning,
                taskId: ctx.Handle?.TaskId,
                contextId: ctx.ContextId,
                a2aState: ctx.Snapshot.RawState);
            await ReconcileAsync(ctx, tokens).ConfigureAwait(false);
            if (ctx.Failure is null)
            {
                ctx.MarkRecovered();
            }
        }
        else if (ctx.Snapshot is null && ctx.DirectMessage is null)
        {
            var failure = new WorkIqFailure(
                WorkIqFailureCategory.Transport,
                $"{operation}: the stream ended before a task id or a direct message arrived; the input was not resent",
                OutcomeCertainty.Indeterminate,
                operation,
                RpcRequestId: ctx.Identity.RpcRequestId,
                ContextId: ctx.SentContextId);
            ctx.Fail(failure, TransportOutcome.Interrupted);
        }
    }

    /// <summary>
    /// Sends the opt-in <c>CancelTask</c> after a timeout, under its own <see cref="RemoteCancelTimeout"/> bound. The
    /// outcome becomes canceled only if the service confirms it. A failed <c>CancelTask</c> is traced; the timeout stands.
    /// </summary>
    /// <param name="ctx">The turn.</param>
    private async Task TryCancelRemoteOnTimeoutAsync(TurnContext ctx)
    {
        if (!_options.CancelRemoteTaskOnTimeout || ctx.Failure?.Category != WorkIqFailureCategory.Timeout || ctx.Handle is null)
        {
            return;
        }

        var taskId = ctx.Handle.TaskId;
        ctx.Trace(
            WorkIqTraceStage.Cancel,
            "cancel.requested",
            $"CancelRemoteTaskOnTimeout: sending CancelTask under its own {RemoteCancelTimeout} bound (the outcome is Canceled only if the service confirms it)",
            WorkIqTraceLevel.Warning,
            taskId: taskId,
            contextId: ctx.ContextId);
        using var bound = new CancellationTokenSource(RemoteCancelTimeout, _clock);
        try
        {
            // A failed CancelTask is traced (cancel.failed below); the turn registers the issue of its outcome. A reply
            // that cannot be applied counts as a failed CancelTask too.
            var reply = await _client.CancelTaskAsync(taskId, bound.Token, ctx.Call()).ConfigureAwait(false);
            ApplyCancelReply(ctx, taskId, reply);
        }
        catch (Exception ex)
        {
            ctx.RecoveryRequests++;
            var failure = _failures.Classify(ex, WorkIqOperation.CancelTask, taskId, ctx.ContextId, bound.Token);
            ctx.AddCapture(failure.Capture);
            ctx.Trace(
                WorkIqTraceStage.Cancel,
                "cancel.failed",
                $"CancelTask after timeout failed: {failure.Category}: {failure.SafeMessage}; the timeout outcome stands",
                WorkIqTraceLevel.Warning,
                taskId: taskId,
                contextId: ctx.ContextId,
                httpStatus: failure.HttpStatus,
                rpcErrorCode: failure.RpcErrorCode);
        }
    }

    /// <summary>
    /// Adopts the task the <c>CancelTask</c> reply carries. The turn is canceled only if that state says so. A reply that
    /// shows the task already ended or paused on its own makes that observed state the turn's outcome: the timeout no
    /// longer applies, so it is cleared and the result's failure is derived from the state (a failed or rejected task
    /// carries its remote error, never the local budget).
    /// </summary>
    private void ApplyCancelReply(TurnContext ctx, string taskId, WorkIqRpcResult<AgentTask> reply)
    {
        ctx.RecoveryRequests++;
        ctx.AddCapture(reply.Capture);
        _observer.ApplyTask(ctx, reply.Value, WorkIqSnapshotSource.GetTask, reply.Capture, reply.ReceivedAt);
        if (ctx.Snapshot is { IsTerminal: true } or { IsSuspended: true })
        {
            ctx.MarkRecovered();
            if (ctx.Snapshot.State != WorkIqTaskState.Canceled)
            {
                ctx.SetTerminalFailure(null);
            }
        }

        var confirmed = ctx.Snapshot?.State == WorkIqTaskState.Canceled;
        if (confirmed)
        {
            _state.ConfirmCanceled(taskId);
        }

        ctx.Trace(
            WorkIqTraceStage.Cancel,
            confirmed ? "cancel.confirmed" : "cancel.not_confirmed",
            confirmed ? "the service confirmed TASK_STATE_CANCELED" : $"the service replied with {ctx.Snapshot?.RawState ?? "(absent)"}: not canceled",
            confirmed ? WorkIqTraceLevel.Info : WorkIqTraceLevel.Warning,
            taskId: taskId,
            contextId: ctx.ContextId,
            requestId: reply.RequestId,
            httpStatus: reply.HttpStatus,
            a2aState: ctx.Snapshot?.RawState);
    }

    /// <summary>The recovery budget ran out: the turn times out, unless the opt-in <c>CancelTask</c> shows the task already ended on its own.</summary>
    private async Task ExhaustBudgetAsync(TurnContext ctx, string taskId, WorkIqTaskSnapshot current, TimeSpan budget)
    {
        var failure = new WorkIqFailure(
            WorkIqFailureCategory.Timeout,
            $"GetTask: the recovery budget ({budget}) is exhausted after {ctx.RecoveryRequests} poll(s); last observed state {current.RawState ?? "(absent)"}; the input was not resent",
            OutcomeCertainty.Indeterminate,
            WorkIqOperation.GetTask,
            RemoteTaskState: current.RawState,
            RpcRequestId: ctx.Identity.RpcRequestId,
            TaskId: taskId,
            ContextId: ctx.ContextId,
            Capture: ctx.LastCapture)
        {
            LocalBudget = WorkIqLocalBudget.RecoveryBudget,
        };
        ctx.Fail(failure, TransportOutcome.Interrupted);
        ctx.Trace(WorkIqTraceStage.Budget, "budget.exceeded", failure.SafeMessage, WorkIqTraceLevel.Error, taskId: taskId, contextId: ctx.ContextId, a2aState: current.RawState);

        // The opt-in CancelTask comes first. Its reply may show the task had already finished on its own (the
        // completed race), and then the register must not keep an Error for a turn that ended well.
        await TryCancelRemoteOnTimeoutAsync(ctx).ConfigureAwait(false);
        if (ctx.Snapshot is { State: not WorkIqTaskState.Canceled } finished && (finished.IsTerminal || finished.IsSuspended))
        {
            RegisterRecoveredAfterCancel(ctx, taskId, current, finished, budget);
            return;
        }

        // Traced only: the turn registers the one issue of its outcome, classified Budget with the RecoveryBudget
        // cause (the failure carries WorkIqLocalBudget.RecoveryBudget).
        ctx.Trace(
            WorkIqTraceStage.Budget,
            "budget.recovery_exhausted",
            $"{failure.SafeMessage}; the turn's outcome issue carries this failure",
            WorkIqTraceLevel.Error,
            taskId: taskId,
            contextId: ctx.ContextId,
            a2aState: ctx.Snapshot?.RawState ?? current.RawState);
    }

    private static void RegisterRecoveredAfterCancel(TurnContext ctx, string taskId, WorkIqTaskSnapshot current, WorkIqTaskSnapshot finished, TimeSpan budget)
    {
        var recovered = $"the recovery budget ({budget}) ran out with the task {current.RawState ?? "(absent)"}, but the CancelTask reply showed it had already reached {finished.RawState ?? "(absent)"} on its own: " +
            "that observed state is the turn's outcome (the timeout no longer applies); the input was not resent";
        ctx.Trace(
            WorkIqTraceStage.Budget,
            "budget.recovered_after_cancel",
            recovered,
            WorkIqTraceLevel.Warning,
            taskId: taskId,
            contextId: ctx.ContextId,
            a2aState: finished.RawState);
        ctx.Diagnostics.Issue(
            WorkIqIssue.Create(
                WorkIqTraceStage.Budget,
                WorkIqIssueCategory.Budget,
                "budget.recovered_after_cancel",
                recovered,
                "The task needed longer than RecoveryBudget but finished on its own; raise RecoveryBudget so the answer is read by polling instead of by the CancelTask race.") with
            {
                CaseId = ctx.Request.CaseId,
                TurnId = ctx.Request.TurnId,
                TaskId = taskId,
                ContextId = ctx.ContextId,
                A2AState = finished.RawState,
            },
            WorkIqTraceLevel.Warning);
    }

    /// <summary>Sleeps until the next poll, on the injected clock.</summary>
    /// <returns>False when a local stop ended the wait (recorded on the turn).</returns>
    private async Task<bool> WaitToPollAsync(TurnContext ctx, TurnTokens tokens, string taskId, WorkIqTaskSnapshot current, TimeSpan remaining, TimeSpan budget)
    {
        var wait = _options.RecoveryPollInterval < remaining ? _options.RecoveryPollInterval : remaining;
        try
        {
            if (wait > TimeSpan.Zero)
            {
                // Arm the poll timer on the injected clock before announcing the sleep. Whoever drives that clock (a
                // test, a replay) can then advance it on "recovery.waiting" knowing the timer is already registered,
                // instead of stepping blindly while a GetTask round-trip is still in flight.
                var poll = Task.Delay(wait, _clock, tokens.Token);
                ctx.Trace(
                    WorkIqTraceStage.GetTask,
                    "recovery.waiting",
                    $"sleeping {wait} before GetTask poll #{ctx.RecoveryRequests + 1}; {remaining} of the {budget} recovery budget remain",
                    taskId: taskId,
                    contextId: ctx.ContextId,
                    a2aState: current.RawState);
                await poll.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _failures.RecordLocalStop(ctx, tokens, WorkIqOperation.GetTask, "while waiting to poll GetTask");
            await TryCancelRemoteOnTimeoutAsync(ctx).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Sends one <c>GetTask</c> poll and adopts the reply. The poll runs under the turn's token and the recovery budget
    /// that is left (<paramref name="remaining"/>): when that budget runs out first (a slow reply, or the client's
    /// 429/503 retries), the recovery ends as an exhausted budget.
    /// </summary>
    /// <returns>False when the poll ended the recovery: a local stop, an exhausted budget or a failure, recorded on the turn.</returns>
    private async Task<bool> PollAsync(TurnContext ctx, TurnTokens tokens, string taskId, WorkIqTaskSnapshot current, TimeSpan remaining, TimeSpan budget)
    {
        using var bound = new CancellationTokenSource(remaining, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(tokens.Token, bound.Token);
        try
        {
            var reply = await _client.GetTaskAsync(taskId, _options.HistoryLength, linked.Token, ctx.Call()).ConfigureAwait(false);
            ctx.RecoveryRequests++;
            ctx.AddCapture(reply.Capture);
            _observer.ApplyTask(ctx, reply.Value, WorkIqSnapshotSource.GetTask, reply.Capture, reply.ReceivedAt);
            return true;
        }
        catch (Exception ex) when (bound.IsCancellationRequested && !tokens.Token.IsCancellationRequested)
        {
            ctx.RecoveryRequests++;
            ctx.AddCapture((ex as WorkIqProtocolException)?.Failure.Capture);
            ctx.Trace(
                WorkIqTraceStage.GetTask,
                "recovery.poll_cut",
                $"GetTask poll #{ctx.RecoveryRequests} was stopped when the recovery budget ({budget}) ran out ({ex.GetType().Name}); the input was not resent",
                WorkIqTraceLevel.Warning,
                taskId: taskId,
                contextId: ctx.ContextId,
                a2aState: current.RawState);
            await ExhaustBudgetAsync(ctx, taskId, current, budget).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            ctx.RecoveryRequests++;
            _failures.RecordLocalStop(ctx, tokens, WorkIqOperation.GetTask, "during a GetTask poll");
            await TryCancelRemoteOnTimeoutAsync(ctx).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            ctx.RecoveryRequests++;
            RecordPollFailure(ctx, ex, tokens, taskId, current);
            return false;
        }
    }

    private void RecordPollFailure(TurnContext ctx, Exception ex, TurnTokens tokens, string taskId, WorkIqTaskSnapshot current)
    {
        var failure = _failures.Classify(ex, WorkIqOperation.GetTask, taskId, ctx.ContextId, tokens.Token) with
        {
            Certainty = OutcomeCertainty.Indeterminate,
            RemoteTaskState = current.RawState,
            RpcRequestId = ctx.Identity.RpcRequestId,
        };
        if (failure.Category == WorkIqFailureCategory.NotFound)
        {
            failure = failure with { SafeMessage = "GetTask: the task was not found during recovery (TaskUnavailable: inaccessible, expired or purged — not proof it never existed); the input was not resent" };
        }
        else if (failure.Category == WorkIqFailureCategory.Canceled)
        {
            failure = _failures.RefineLocalStop(ctx, failure, tokens, WorkIqOperation.GetTask, "during a GetTask poll");
        }

        ctx.AddCapture(failure.Capture);
        ctx.Fail(failure, TransportOutcome.Interrupted);
        ctx.Trace(
            WorkIqTraceStage.GetTask,
            "recovery.failed",
            $"recovery stopped after {ctx.RecoveryRequests} poll(s): {failure.Category} ({failure.Certainty}): {failure.SafeMessage}",
            WorkIqTraceLevel.Error,
            taskId: taskId,
            contextId: ctx.ContextId,
            requestId: failure.RequestId,
            httpStatus: failure.HttpStatus,
            rpcErrorCode: failure.RpcErrorCode,
            a2aState: current.RawState);
    }
}
