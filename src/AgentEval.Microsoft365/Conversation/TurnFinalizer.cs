// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// Builds a turn's result once its calls are over: the outcome, the selected answer, the pending interaction, the
/// evidence (normalised exactly once) and the timing. It records the turn on the conversation state, traces the
/// outcome, and registers the one issue of a turn that did not complete.
/// </summary>
internal sealed class TurnFinalizer
{
    private readonly TimeProvider _clock;
    private readonly DiagnosticProfile _profile;
    private readonly ConversationState _state;
    private readonly PendingInteractionBuilder _interactions;

    public TurnFinalizer(TimeProvider clock, DiagnosticProfile profile, ConversationState state, PendingInteractionBuilder interactions)
    {
        _clock = clock;
        _profile = profile;
        _state = state;
        _interactions = interactions;
    }

    /// <summary>Builds the turn's result, records it on the conversation state, and traces it.</summary>
    /// <param name="ctx">The turn.</param>
    public WorkIqTurnResult BuildResult(TurnContext ctx)
    {
        var endedAt = _clock.GetUtcNow();
        var snapshot = ctx.Snapshot;
        var direct = ctx.DirectMessage;
        if (snapshot is not null && ctx.Failure is null)
        {
            ctx.SetTerminalFailure(DescribeTerminalState(ctx, snapshot));
        }

        var (outcome, _) = WorkIqOutcomeClassifier.Classify(snapshot?.State, direct is not null, ctx.Transport, ctx.Failure, taskIdKnown: ctx.Handle is not null);
        var captureId = ctx.CaptureId;
        var (completeness, answerText) = SelectAnswer(ctx, outcome, captureId);
        var diagnosticsObserved = snapshot is not null && snapshot.Artifacts.Any(WorkIqAnswerSelector.IsDiagnosticArtifact);
        var telemetry = WorkIqOutcomeClassifier.TelemetryFor(_profile, diagnosticsObserved);
        var pending = outcome is WorkIqTurnOutcome.InputRequired or WorkIqTurnOutcome.AuthRequired && snapshot is not null
            ? _interactions.Build(snapshot, captureId)
            : null;
        var identity = ctx.Identity with
        {
            ProviderMessageId = direct?.MessageId ?? snapshot?.StatusMessage?.MessageId,
            ProviderTaskId = snapshot?.Handle.TaskId,
            ProviderContextId = ctx.ContextId,
        };
        var evidence = Normalize(ctx, identity, outcome);
        var timing = new WorkIqTurnTiming(ctx.StartedAt, ctx.FirstEventAt, ctx.FirstAnswerAt, ctx.TerminalAt, endedAt, ctx.RecoveryRequests);
        var result = new WorkIqTurnResult(
            outcome,
            ctx.Transport,
            completeness,
            telemetry,
            snapshot?.Handle,
            ctx.ContextId,
            answerText,
            evidence,
            pending,
            outcome == WorkIqTurnOutcome.Completed ? null : ctx.Failure,
            timing,
            identity);

        _state.RecordTurn(ctx, result);
        TraceOutcome(ctx, result);
        return result;
    }

    /// <summary>The failure a final task state implies: none for a completed or paused task.</summary>
    private static WorkIqFailure? DescribeTerminalState(TurnContext ctx, WorkIqTaskSnapshot snapshot)
    {
        var operation = snapshot.Source switch
        {
            WorkIqSnapshotSource.GetTask => WorkIqOperation.GetTask,
            WorkIqSnapshotSource.Stream => WorkIqOperation.Stream,
            WorkIqSnapshotSource.Subscribe => WorkIqOperation.Subscribe,
            _ => WorkIqOperation.Send,
        };
        var raw = snapshot.RawState ?? "(absent)";
        return snapshot.State switch
        {
            WorkIqTaskState.Completed or WorkIqTaskState.InputRequired or WorkIqTaskState.AuthRequired => null,
            WorkIqTaskState.Failed => Failure(WorkIqFailureCategory.RemoteError, $"{operation}: the task ended {raw}; partial artifacts are retained but are not an answer", OutcomeCertainty.Known),
            WorkIqTaskState.Rejected => Failure(WorkIqFailureCategory.RemoteError, $"{operation}: the service rejected the task ({raw})", OutcomeCertainty.Known),
            WorkIqTaskState.Canceled => Failure(WorkIqFailureCategory.RemoteError, $"{operation}: the service confirmed cancellation ({raw})", OutcomeCertainty.Known),
            WorkIqTaskState.Submitted or WorkIqTaskState.Working => Failure(
                WorkIqFailureCategory.Timeout,
                $"the task was still {raw} when the local operation ended; the input was not resent",
                OutcomeCertainty.Indeterminate),
            _ => Failure(WorkIqFailureCategory.Malformed, $"unrecognised task state '{raw}'; it never implies completion", OutcomeCertainty.Known),
        };

        WorkIqFailure Failure(WorkIqFailureCategory category, string message, OutcomeCertainty certainty) => new(
            category,
            message,
            certainty,
            operation,
            RemoteTaskState: snapshot.RawState,
            RpcRequestId: ctx.Identity.RpcRequestId,
            TaskId: snapshot.Handle.TaskId,
            ContextId: snapshot.Handle.ContextId,
            Capture: snapshot.Capture);
    }

    private static CaptureState ComputeCaptureState(ImmutableArray<RawCaptureRef> captures, TurnContext ctx)
    {
        if (captures.Any(c => c.State == CaptureState.Truncated))
        {
            return CaptureState.Truncated;
        }

        if (captures.Any(c => c.State == CaptureState.Interrupted) || ctx.Transport == TransportOutcome.Interrupted)
        {
            return CaptureState.Interrupted;
        }

        if (captures.IsEmpty || captures.All(c => c.State == CaptureState.NotRetained))
        {
            return CaptureState.NotRetained;
        }

        return CaptureState.Complete;
    }

    /// <summary>The answer's completeness and its text: the text only for a completed turn whose answer is complete or empty.</summary>
    private static (OutputCompleteness Completeness, string? AnswerText) SelectAnswer(TurnContext ctx, WorkIqTurnOutcome outcome, string captureId)
    {
        var snapshot = ctx.Snapshot;
        var direct = ctx.DirectMessage;
        var selection = direct is not null
            ? WorkIqAnswerSelector.SelectFromMessage(direct, captureId)
            : snapshot is not null
                ? WorkIqAnswerSelector.SelectFromTask(snapshot, captureId)
                : new WorkIqAnswerSelection(null, OutputCompleteness.None, ImmutableArray<string>.Empty, ImmutableArray<SourceRef>.Empty);

        var completeness = WorkIqOutcomeClassifier.AnswerCompletenessFor(outcome, selection);
        var answerText = outcome == WorkIqTurnOutcome.Completed && completeness is OutputCompleteness.Complete or OutputCompleteness.Empty
            ? selection.RawText
            : null;

        if (completeness == OutputCompleteness.Ambiguous)
        {
            // Traced here, where the selection happened. The issue itself is registered once, by the normaliser, which
            // records the same ambiguity in the evidence document.
            ctx.Trace(
                WorkIqTraceStage.Reduce,
                "answer.ambiguous",
                $"{selection.CandidateArtifactIds.Length} answer candidates; none was chosen and all are retained",
                WorkIqTraceLevel.Warning,
                taskId: snapshot?.Handle.TaskId,
                contextId: ctx.ContextId);
        }

        return (completeness, answerText);
    }

    /// <summary>
    /// Normalises the turn's evidence on the conversation's diagnostics. The normaliser traces its own measured scope
    /// (<c>normalize.start</c>, <c>.end</c>, <c>.failed</c>) and its <c>normalize.*</c> summaries. It registers its
    /// evidence issues (<c>approval.*</c>, <c>diagnostics.*</c>, <c>citations.*</c>, <c>answer.ambiguous</c>), so they
    /// reach the run's trace and issue files. A normaliser that throws registers <c>normalize.failed</c> and the
    /// exception propagates.
    /// </summary>
    private Microsoft365TurnEvidence Normalize(TurnContext ctx, TurnIdentity identity, WorkIqTurnOutcome outcome)
    {
        var snapshot = ctx.Snapshot;
        var captures = ctx.Captures.ToImmutableArray();
        var captureState = ComputeCaptureState(captures, ctx);
        try
        {
            return new WorkIqEvidenceNormalizer(ctx.Diagnostics).Normalize(snapshot, ctx.DirectMessage, captures, captureState, _profile, identity);
        }
        catch (Exception ex)
        {
            ctx.Diagnostics.Issue(
                WorkIqIssue.Create(
                    WorkIqTraceStage.Normalize,
                    WorkIqIssueCategory.Unknown,
                    "normalize.failed",
                    $"The evidence normaliser threw {ex.GetType().Name}; the turn result could not be built (outcome would have been {outcome}).",
                    "A normaliser defect or an unexpected payload shape; the trace carries the task state and ids, and the raw capture shows what arrived.") with
                {
                    CaseId = ctx.Request.CaseId,
                    TurnId = ctx.Request.TurnId,
                    TaskId = snapshot?.Handle.TaskId,
                    ContextId = ctx.ContextId,
                    A2AState = snapshot?.RawState,
                });
            throw;
        }
    }

    private static string DescribeOutcome(WorkIqTurnResult result) =>
        $"outcome {result.Outcome}; transport {result.Transport}; answer {result.AnswerCompleteness} ({result.AnswerText?.Length ?? 0} chars); telemetry {result.Telemetry}; " +
        $"{result.Timing.RecoveryRequests} recovery request(s); {result.Timing.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms" +
        (result.Failure is null ? "" : $"; failure {result.Failure.Category} ({result.Failure.Certainty}): {result.Failure.SafeMessage}") +
        (result.PendingInteraction is null ? "" : $"; pending {result.PendingInteraction.Kind} with {result.PendingInteraction.ProposedCalls.Length} proposed call(s)");

    /// <summary>
    /// Traces the outcome and registers the one issue of a turn that did not complete, classified by
    /// <see cref="WorkIqIssueClassifier.ForTurnOutcome"/>. A turn that got no bearer token registers nothing here: the
    /// auth layer registered that failure already.
    /// </summary>
    private static void TraceOutcome(TurnContext ctx, WorkIqTurnResult result)
    {
        var level = result.Outcome switch
        {
            WorkIqTurnOutcome.Completed => WorkIqTraceLevel.Info,
            WorkIqTurnOutcome.InputRequired or WorkIqTurnOutcome.AuthRequired => WorkIqTraceLevel.Warning,
            _ => WorkIqTraceLevel.Error,
        };
        var message = DescribeOutcome(result);
        ctx.Diagnostics.Trace(new WorkIqTraceEvent(default, WorkIqTraceStage.Outcome, level, "turn.outcome", message)
        {
            CaseId = ctx.Request.CaseId,
            TurnId = ctx.Request.TurnId,
            RpcId = ctx.Identity.RpcRequestId,
            MessageId = ctx.Identity.ClientMessageId,
            TaskId = result.Task?.TaskId ?? result.Failure?.TaskId,
            ContextId = result.ContextId,
            RequestId = result.Failure?.RequestId,
            HttpStatus = result.Failure?.HttpStatus,
            RpcErrorCode = result.Failure?.RpcErrorCode,
            A2AState = ctx.Snapshot?.RawState,
            ElapsedMs = result.Timing.Elapsed.TotalMilliseconds,
        });

        if (result.Outcome == WorkIqTurnOutcome.Completed)
        {
            return;
        }

        if (result.Failure?.TokenFailure is not null)
        {
            // No bearer token, nothing sent: the auth layer registered this failure's one issue already. It holds the
            // sign-in or MSAL error and is stamped with this turn's case and turn ids.
            return;
        }

        RegisterOutcomeIssue(ctx, result, message, level);
    }

    private static void RegisterOutcomeIssue(TurnContext ctx, WorkIqTurnResult result, string message, WorkIqTraceLevel level)
    {
        var (category, code, cause) = WorkIqIssueClassifier.ForTurnOutcome(result);
        ctx.Diagnostics.Issue(new WorkIqIssue(default, WorkIqTraceStage.Outcome, category, code, message, cause)
        {
            CaseId = ctx.Request.CaseId,
            TurnId = ctx.Request.TurnId,
            // A send reply that could not be used may still name its task (the failure carries the id).
            TaskId = result.Task?.TaskId ?? result.Failure?.TaskId,
            ContextId = result.ContextId,
            RequestId = result.Failure?.RequestId,
            HttpStatus = result.Failure?.HttpStatus,
            RpcErrorCode = result.Failure?.RpcErrorCode,
            A2AState = ctx.Snapshot?.RawState,
        }, level);
    }
}
