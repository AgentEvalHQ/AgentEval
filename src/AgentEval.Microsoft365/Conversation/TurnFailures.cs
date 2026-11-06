// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// Classifies how a turn's call failed or why its local wait stopped, records the failure on the turn and traces it.
/// A local stop is attributed to its source: the caller's token, an abandonment, or the turn deadline (a timeout).
/// The input is never resent.
/// </summary>
internal sealed class TurnFailures
{
    private readonly Microsoft365Config _config;
    private readonly WorkIqOptions _options;

    public TurnFailures(Microsoft365Config config, WorkIqOptions options)
    {
        _config = config;
        _options = options;
    }

    /// <summary>Classifies a transport exception into a safe <see cref="WorkIqFailure"/>, with the configured scope for sign-in failures.</summary>
    /// <param name="ex">The exception.</param>
    /// <param name="operation">The operation that failed.</param>
    /// <param name="taskId">The task id, when known.</param>
    /// <param name="contextId">The context id, when known.</param>
    /// <param name="token">The token the operation ran under.</param>
    public WorkIqFailure Classify(Exception ex, WorkIqOperation operation, string? taskId, string? contextId, CancellationToken token) =>
        WorkIqA2AClient.Classify(ex, operation, requestId: null, taskId, contextId, token, _config.Scope);

    /// <summary>
    /// Records a discovery failure that keeps the turn from starting. A stop of the discovery wait (the caller's token
    /// or an abandonment) is attributed to its source, so an abandoned turn ends as abandoned with nothing sent.
    /// </summary>
    /// <param name="ctx">The turn.</param>
    /// <param name="ex">The exception.</param>
    /// <param name="callerToken">The caller's token.</param>
    public void RecordDiscoveryFailure(TurnContext ctx, Exception ex, CancellationToken callerToken)
    {
        var failure = Classify(ex, WorkIqOperation.Discover, taskId: null, contextId: null, callerToken) with { RpcRequestId = ctx.Identity.RpcRequestId };
        ctx.CanceledByCaller = callerToken.IsCancellationRequested;
        var localStop = ex is OperationCanceledException
            || failure.Category == WorkIqFailureCategory.Canceled
            || WorkIqIssueClassifier.IsSignInNotCompleted(failure);
        if (!ctx.CanceledByCaller && localStop && ctx.AbandonToken.IsCancellationRequested)
        {
            ctx.Abandoned = true;
            if (!WorkIqIssueClassifier.IsSignInNotCompleted(failure))
            {
                failure = failure with
                {
                    Category = WorkIqFailureCategory.Canceled,
                    Certainty = OutcomeCertainty.Indeterminate,
                    SafeMessage = "Discover: abandoned locally (AbandonAndResetAsync) while the agent card was fetched; nothing was sent",
                };
            }
        }

        ctx.Fail(failure, TransportOutcome.Failed);
        ctx.AddCapture(failure.Capture);
        ctx.Trace(
            WorkIqTraceStage.Discovery,
            "discovery.turn_blocked",
            $"the turn cannot start: {failure.Category} ({failure.Certainty}): {failure.SafeMessage}",
            WorkIqTraceLevel.Error,
            httpStatus: failure.HttpStatus,
            rpcErrorCode: failure.RpcErrorCode);
    }

    /// <summary>
    /// Records a turn abandoned (<c>AbandonAndResetAsync</c>) while its card was fetched, when the fetch completed anyway
    /// (a sign-in finished after the abandonment). The turn sends nothing and ends as abandoned.
    /// </summary>
    /// <param name="ctx">The turn.</param>
    public void RecordAbandonedBeforeSend(TurnContext ctx)
    {
        ctx.Abandoned = true;
        var failure = new WorkIqFailure(
            WorkIqFailureCategory.Canceled,
            $"{ctx.Operation}: abandoned locally (AbandonAndResetAsync) before the input was sent; nothing was sent",
            OutcomeCertainty.Indeterminate,
            ctx.Operation,
            RpcRequestId: ctx.Identity.RpcRequestId);
        ctx.Fail(failure, TransportOutcome.Failed);
        ctx.Trace(
            WorkIqIssueClassifier.StageFor(ctx.Operation),
            "turn.stopped_locally",
            $"{failure.Category}: {failure.SafeMessage}",
            WorkIqTraceLevel.Warning);
    }

    /// <summary>Records a send, stream or subscribe call that failed before a task id was known.</summary>
    /// <param name="ctx">The turn.</param>
    /// <param name="ex">The exception.</param>
    /// <param name="tokens">The turn's tokens.</param>
    /// <param name="operation">The operation.</param>
    public void RecordSendFailure(TurnContext ctx, Exception ex, TurnTokens tokens, WorkIqOperation operation)
    {
        var failure = Classify(ex, operation, taskId: null, ctx.SentContextId, tokens.Token) with { RpcRequestId = ctx.Identity.RpcRequestId };
        if (failure.Category == WorkIqFailureCategory.Canceled)
        {
            failure = RefineLocalStop(ctx, failure, tokens, operation, "before a reply");
        }
        else if (WorkIqIssueClassifier.IsSignInNotCompleted(failure))
        {
            // The token wait before the call was stopped, so nothing was sent. The failure keeps its "sign-in not
            // completed" text (never a timeout that may have reached the service). Only the stop's source is recorded,
            // so a caller's cancellation still surfaces as WorkIqTurnCanceledException.
            ctx.CanceledByCaller = tokens.Caller.IsCancellationRequested;
            ctx.Abandoned = !ctx.CanceledByCaller && tokens.Abandon.IsCancellationRequested;
        }

        ctx.AddCapture(failure.Capture);
        ctx.Fail(failure, TransportOutcome.Failed);
        ctx.Trace(
            WorkIqIssueClassifier.StageFor(operation),
            "send.failed",
            $"{operation} failed before a task id was known: {failure.Category} ({failure.Certainty}): {failure.SafeMessage}; the input was NOT resent",
            WorkIqTraceLevel.Error,
            contextId: ctx.SentContextId,
            requestId: failure.RequestId,
            httpStatus: failure.HttpStatus,
            rpcErrorCode: failure.RpcErrorCode);
    }

    /// <summary>
    /// Refines a <see cref="WorkIqFailureCategory.Canceled"/> classification by the local source that fired: the
    /// caller's token, an abandonment, or the turn deadline (which makes it a timeout).
    /// </summary>
    /// <param name="ctx">The turn, which records the stop's source.</param>
    /// <param name="failure">The classified failure.</param>
    /// <param name="tokens">The turn's tokens.</param>
    /// <param name="operation">The operation that stopped.</param>
    /// <param name="phase">When it stopped, for the message.</param>
    public WorkIqFailure RefineLocalStop(TurnContext ctx, WorkIqFailure failure, TurnTokens tokens, WorkIqOperation operation, string phase)
    {
        if (tokens.Caller.IsCancellationRequested)
        {
            ctx.CanceledByCaller = true;
            return failure with { SafeMessage = $"{operation}: the caller's token stopped the local wait {phase}; the remote state was not reconciled and the input was not resent" };
        }

        if (tokens.Abandon.IsCancellationRequested)
        {
            ctx.Abandoned = true;
            return failure with { SafeMessage = $"{operation}: abandoned locally (AbandonAndResetAsync) {phase}; the remote state was not reconciled and the input was not resent" };
        }

        if (tokens.DeadlineFired)
        {
            ctx.DeadlineFired = true;
            return failure with
            {
                Category = WorkIqFailureCategory.Timeout,
                LocalBudget = WorkIqLocalBudget.TurnDeadline,
                SafeMessage = $"{operation}: the turn deadline ({_options.TurnDeadline}) elapsed {phase}; the request may have reached the service and the input was not resent",
            };
        }

        return failure;
    }

    /// <summary>Records a local wait that stopped (the caller, an abandonment or the turn deadline) while the remote state is unknown.</summary>
    /// <param name="ctx">The turn.</param>
    /// <param name="tokens">The turn's tokens.</param>
    /// <param name="operation">The operation that stopped.</param>
    /// <param name="phase">When it stopped, for the message.</param>
    public void RecordLocalStop(TurnContext ctx, TurnTokens tokens, WorkIqOperation operation, string phase)
    {
        var baseline = new WorkIqFailure(
            WorkIqFailureCategory.Canceled,
            $"{operation}: the local wait stopped {phase}",
            OutcomeCertainty.Indeterminate,
            operation,
            RemoteTaskState: ctx.Snapshot?.RawState,
            RpcRequestId: ctx.Identity.RpcRequestId,
            TaskId: ctx.Handle?.TaskId,
            ContextId: ctx.ContextId,
            Capture: ctx.LastCapture);
        var failure = RefineLocalStop(ctx, baseline, tokens, operation, phase);
        ctx.Fail(failure, TransportOutcome.Interrupted);
        ctx.Trace(
            WorkIqIssueClassifier.StageFor(operation),
            "turn.stopped_locally",
            $"{failure.Category}: {failure.SafeMessage}",
            WorkIqTraceLevel.Warning,
            taskId: ctx.Handle?.TaskId,
            contextId: ctx.ContextId,
            a2aState: ctx.Snapshot?.RawState);
    }
}
