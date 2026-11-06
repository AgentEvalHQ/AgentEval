// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using A2A;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// Turns the frames of a stream or a subscription into <see cref="WorkIqStreamEvent"/>s. Each frame goes through the
/// turn's reducer, and the turn records the snapshots, the direct message or the failure. A failed or malformed frame
/// stops the turn; it is never skipped.
/// </summary>
internal sealed class StreamEventProjector
{
    private readonly TaskHandleFactory _handles;
    private readonly TurnObserver _observer;
    private readonly TurnFailures _failures;
    private readonly PendingInteractionBuilder _interactions;

    public StreamEventProjector(TaskHandleFactory handles, TurnObserver observer, TurnFailures failures, PendingInteractionBuilder interactions)
    {
        _handles = handles;
        _observer = observer;
        _failures = failures;
        _interactions = interactions;
    }

    /// <summary>
    /// Reads the frames until the stream ends, the turn fails, a direct message arrives, or the task reaches a final
    /// or paused state.
    /// </summary>
    /// <remarks>
    /// The enumeration is driven by <see cref="TurnTokens.Token"/> (the caller, the abandonment and the deadline,
    /// linked). The caller's own token is read from <see cref="TurnTokens.Caller"/> only to attribute a stop. So this
    /// iterator takes no <see cref="CancellationToken"/> parameter that could silently diverge from the linked token.
    /// </remarks>
    /// <param name="ctx">The turn.</param>
    /// <param name="frames">The frames.</param>
    /// <param name="source">Where the frames come from: a stream or a subscription.</param>
    /// <param name="tokens">The turn's tokens.</param>
    public async IAsyncEnumerable<WorkIqStreamEvent> ConsumeFramesAsync(TurnContext ctx, IAsyncEnumerable<WorkIqStreamFrame> frames, WorkIqSnapshotSource source, TurnTokens tokens)
    {
        var operation = OperationOf(source);
        var enumerator = frames.GetAsyncEnumerator(tokens.Token);
        try
        {
            while (true)
            {
                WorkIqStreamFrame? frame;
                try
                {
                    frame = await enumerator.MoveNextAsync().ConfigureAwait(false) ? enumerator.Current : null;
                }
                catch (OperationCanceledException)
                {
                    _failures.RecordLocalStop(ctx, tokens, operation, "while reading the stream");
                    yield break;
                }
                catch (Exception ex)
                {
                    RecordStreamFailure(ctx, ex, source, tokens);
                    yield break;
                }

                if (frame is null)
                {
                    ctx.Reducer?.MarkStreamEnded();
                    yield break;
                }

                ctx.FirstEventAt ??= frame.ReceivedAt;
                foreach (var evt in ReduceFrame(ctx, frame, source))
                {
                    yield return evt;
                }

                if (ctx.Failure is not null || ctx.DirectMessage is not null || ctx.Snapshot is { IsTerminal: true } or { IsSuspended: true })
                {
                    yield break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static WorkIqOperation OperationOf(WorkIqSnapshotSource source) =>
        source == WorkIqSnapshotSource.Subscribe ? WorkIqOperation.Subscribe : WorkIqOperation.Stream;

    /// <summary>A protocol warning of the turn, with the next sequence number.</summary>
    private static WorkIqStreamEvent.ProtocolWarning NewWarning(TurnContext ctx, DateTimeOffset at, string code, string message) =>
        new(ctx.Request.TurnId, ctx.NextSequence(), at, code, message, ImmutableArray<SourceRef>.Empty);

    private static JsonElement ParseOrWrap(string? dataJson)
    {
        if (!string.IsNullOrWhiteSpace(dataJson))
        {
            try
            {
                using var document = JsonDocument.Parse(dataJson);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Not JSON: keep the text as a string element so nothing is lost.
            }
        }

        return JsonSerializer.SerializeToElement(dataJson ?? "");
    }

    /// <summary>The stream itself failed (not a frame): the turn fails, and its transport is failed or interrupted.</summary>
    private void RecordStreamFailure(TurnContext ctx, Exception ex, WorkIqSnapshotSource source, TurnTokens tokens)
    {
        var operation = OperationOf(source);
        var failure = _failures.Classify(ex, operation, ctx.Handle?.TaskId, ctx.ContextId, tokens.Token) with
        {
            RpcRequestId = ctx.Identity.RpcRequestId,
            RemoteTaskState = ctx.Snapshot?.RawState,
        };
        if (failure.Category == WorkIqFailureCategory.Canceled)
        {
            failure = _failures.RefineLocalStop(ctx, failure, tokens, operation, "while reading the stream");
        }

        ctx.AddCapture(failure.Capture);
        ctx.Fail(failure, ctx.Snapshot is null && ctx.DirectMessage is null ? TransportOutcome.Failed : TransportOutcome.Interrupted);
        ctx.Reducer?.MarkInterrupted();
        ctx.Trace(
            TurnObserver.StageOf(source),
            "stream.failed",
            $"{failure.Category} ({failure.Certainty}): {failure.SafeMessage}",
            WorkIqTraceLevel.Error,
            taskId: ctx.Handle?.TaskId,
            contextId: ctx.ContextId,
            httpStatus: failure.HttpStatus,
            rpcErrorCode: failure.RpcErrorCode,
            a2aState: ctx.Snapshot?.RawState);
    }

    private List<WorkIqStreamEvent> ReduceFrame(TurnContext ctx, WorkIqStreamFrame frame, WorkIqSnapshotSource source)
    {
        var events = new List<WorkIqStreamEvent>();
        if (frame.Failure is { } frameFailure)
        {
            RecordFrameFailure(ctx, frame, frameFailure, source, events);
            return events;
        }

        if (frame.Event is not { } response)
        {
            events.Add(new WorkIqStreamEvent.UnknownProviderEvent(ctx.Request.TurnId, ctx.NextSequence(), frame.ReceivedAt, ParseOrWrap(frame.DataJson), frame.SseEvent, frame.SseId));
            ctx.Trace(
                TurnObserver.StageOf(source),
                "stream.unknown_frame",
                $"frame {frame.ClientSequence} carries no recognised event (sse event {(frame.SseEvent is null ? "absent" : "present")}); retained raw",
                WorkIqTraceLevel.Warning,
                taskId: ctx.Handle?.TaskId,
                contextId: ctx.ContextId);
            return events;
        }

        // The capture the frame's sources point at, as it stood before this frame changed the snapshot.
        var captureId = ctx.CaptureId;
        var reducer = ctx.Reducer ??= _handles.NewReducer(ctx);
        try
        {
            ProjectEvent(ctx, reducer, frame, response, source, captureId, events);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordReduceFailure(ctx, frame, ex, OperationOf(source), events);
        }

        for (var i = ctx.SchemaWarningsEmitted; i < (ctx.Snapshot?.SchemaWarnings.Length ?? 0); i++)
        {
            events.Add(NewWarning(ctx, frame.ReceivedAt, "reduce.schema_warning", ctx.Snapshot!.SchemaWarnings[i]));
        }

        ctx.SchemaWarningsEmitted = Math.Max(ctx.SchemaWarningsEmitted, ctx.Snapshot?.SchemaWarnings.Length ?? 0);
        return events;
    }

    /// <summary>Applies one parsed frame to the reducer and adds the events it produces.</summary>
    private void ProjectEvent(
        TurnContext ctx,
        WorkIqTaskReducer reducer,
        WorkIqStreamFrame frame,
        StreamResponse response,
        WorkIqSnapshotSource source,
        string captureId,
        List<WorkIqStreamEvent> events)
    {
        switch (response.PayloadCase)
        {
            case StreamResponseCase.Task when response.Task is { } task:
                reducer.Load(task, source, frame);
                AddSnapshotEvents(ctx, frame, source, captureId, events);
                break;

            case StreamResponseCase.Message when response.Message is { } message:
                reducer.Apply(frame, source);
                if (ctx.Snapshot is not null)
                {
                    // One precedence rule: a stream that carries a task is never answered by a message (see the method).
                    RecordMessageInTaskStream(ctx, frame, source, events);
                    break;
                }

                TurnObserver.ApplyDirectMessage(ctx, reducer.DirectMessage() ?? WorkIqSdkMapping.ToMessage(message), frame.ReceivedAt);
                break;

            case StreamResponseCase.StatusUpdate when response.StatusUpdate is not null:
                reducer.Apply(frame, source);
                AddStatusEvents(ctx, frame, source, captureId, events);
                break;

            case StreamResponseCase.ArtifactUpdate when response.ArtifactUpdate is { } update:
                reducer.Apply(frame, source);
                AddArtifactEvent(ctx, frame, source, update, events);
                break;

            default:
                RecordOneOfViolation(ctx, frame, OperationOf(source), events);
                break;
        }
    }

    private void AddSnapshotEvents(TurnContext ctx, WorkIqStreamFrame frame, WorkIqSnapshotSource source, string captureId, List<WorkIqStreamEvent> events)
    {
        var at = frame.ReceivedAt;
        var snapshot = _observer.Refresh(ctx, source, at);
        if (snapshot is null)
        {
            return;
        }

        events.Add(new WorkIqStreamEvent.TaskSnapshotObserved(ctx.Request.TurnId, ctx.NextSequence(), at, snapshot));
        if (snapshot.IsSuspended)
        {
            events.Add(new WorkIqStreamEvent.InteractionRequired(ctx.Request.TurnId, ctx.NextSequence(), at, _interactions.Build(snapshot, captureId)));
        }
    }

    private void AddStatusEvents(TurnContext ctx, WorkIqStreamFrame frame, WorkIqSnapshotSource source, string captureId, List<WorkIqStreamEvent> events)
    {
        var turnId = ctx.Request.TurnId;
        var at = frame.ReceivedAt;
        var snapshot = _observer.Refresh(ctx, source, at);
        if (snapshot is null)
        {
            events.Add(NewWarning(ctx, at, "stream.status_without_task", "a statusUpdate arrived before any task snapshot established the task identity"));
            return;
        }

        events.Add(new WorkIqStreamEvent.TaskStatusObserved(turnId, ctx.NextSequence(), at, snapshot.Handle, snapshot.State, snapshot.RawState, snapshot.StatusTimestamp, snapshot.StatusMessage));
        if (!snapshot.IsTerminal && !snapshot.IsSuspended && snapshot.StatusMessage is { } progress)
        {
            events.Add(new WorkIqStreamEvent.ProgressObserved(turnId, ctx.NextSequence(), at, snapshot.Handle, progress));
        }

        if (snapshot.IsSuspended)
        {
            events.Add(new WorkIqStreamEvent.InteractionRequired(turnId, ctx.NextSequence(), at, _interactions.Build(snapshot, captureId)));
        }
    }

    private void AddArtifactEvent(TurnContext ctx, WorkIqStreamFrame frame, WorkIqSnapshotSource source, TaskArtifactUpdateEvent update, List<WorkIqStreamEvent> events)
    {
        var at = frame.ReceivedAt;
        var snapshot = _observer.Refresh(ctx, source, at);
        var artifact = snapshot?.Artifacts.FirstOrDefault(a => string.Equals(a.ArtifactId, update.Artifact?.ArtifactId, StringComparison.Ordinal));
        if (snapshot is null || artifact is null)
        {
            events.Add(NewWarning(ctx, at, "stream.artifact_not_reduced", "an artifactUpdate could not be matched to a reduced artifact of the current task"));
            return;
        }

        events.Add(new WorkIqStreamEvent.ArtifactRevisionObserved(ctx.Request.TurnId, ctx.NextSequence(), at, snapshot.Handle, artifact, update.Append, update.LastChunk));
    }

    /// <summary>The client could not use the frame (a JSON-RPC error, a malformed payload): the turn stops here.</summary>
    private static void RecordFrameFailure(TurnContext ctx, WorkIqStreamFrame frame, WorkIqFailure frameFailure, WorkIqSnapshotSource source, List<WorkIqStreamEvent> events)
    {
        var failure = frameFailure with
        {
            RpcRequestId = frameFailure.RpcRequestId ?? ctx.Identity.RpcRequestId,
            TaskId = frameFailure.TaskId ?? ctx.Handle?.TaskId,
            ContextId = frameFailure.ContextId ?? ctx.ContextId,
        };
        ctx.Fail(failure, TransportOutcome.Failed);
        events.Add(NewWarning(ctx, frame.ReceivedAt, "stream.frame_failed", $"{frameFailure.Category}: {frameFailure.SafeMessage}"));
        ctx.Trace(
            TurnObserver.StageOf(source),
            "stream.frame_failed",
            $"frame {frame.ClientSequence} (epoch {frame.ConnectionEpoch}): {frameFailure.Category}: {frameFailure.SafeMessage}; the turn stops here (a malformed frame is never skipped)",
            WorkIqTraceLevel.Error,
            taskId: ctx.Handle?.TaskId,
            contextId: ctx.ContextId,
            httpStatus: frameFailure.HttpStatus,
            rpcErrorCode: frameFailure.RpcErrorCode);
    }

    /// <summary>
    /// The frame carries none of the four payloads: a one-of violation that stops the turn. The service answered, so a
    /// streamed send may have created a task (<see cref="WorkIqA2AClient.UnusableReply"/>).
    /// </summary>
    private static void RecordOneOfViolation(TurnContext ctx, WorkIqStreamFrame frame, WorkIqOperation operation, List<WorkIqStreamEvent> events)
    {
        var failure = WorkIqA2AClient.UnusableReply(new WorkIqFailure(
            WorkIqFailureCategory.Malformed,
            $"{operation}: frame {frame.ClientSequence} carries none of task|message|statusUpdate|artifactUpdate (one-of violation)",
            OutcomeCertainty.Known,
            operation,
            RpcRequestId: ctx.Identity.RpcRequestId,
            TaskId: ctx.Handle?.TaskId,
            ContextId: ctx.ContextId));
        ctx.Fail(failure, TransportOutcome.Failed);
        events.Add(NewWarning(ctx, frame.ReceivedAt, "stream.one_of_violation", failure.SafeMessage));
    }

    /// <summary>
    /// A direct message arrived in a stream that already carries a task. A stream is either one message or one task
    /// with its updates, so this breaks the stream's shape. The turn stops here as malformed: the message is not taken
    /// as the answer, and no <c>GetTask</c> reconciliation follows. The task stays addressable through its handle.
    /// </summary>
    private static void RecordMessageInTaskStream(TurnContext ctx, WorkIqStreamFrame frame, WorkIqSnapshotSource source, List<WorkIqStreamEvent> events)
    {
        var operation = OperationOf(source);
        var failure = new WorkIqFailure(
            WorkIqFailureCategory.Malformed,
            $"{operation}: frame {frame.ClientSequence} carries a direct message in a stream that already carries task {ctx.Handle?.TaskId ?? "(unknown)"}; " +
            "a stream holds one message or one task, so neither is taken as the answer",
            OutcomeCertainty.Indeterminate,
            operation,
            RemoteTaskState: ctx.Snapshot?.RawState,
            RpcRequestId: ctx.Identity.RpcRequestId,
            TaskId: ctx.Handle?.TaskId,
            ContextId: ctx.ContextId);
        ctx.Fail(failure, TransportOutcome.Interrupted);
        events.Add(NewWarning(ctx, frame.ReceivedAt, "stream.message_in_task_stream", failure.SafeMessage));
        ctx.Trace(
            TurnObserver.StageOf(source),
            "stream.message_in_task_stream",
            failure.SafeMessage,
            WorkIqTraceLevel.Error,
            taskId: ctx.Handle?.TaskId,
            contextId: ctx.ContextId,
            a2aState: ctx.Snapshot?.RawState);
    }

    /// <summary>The reducer threw on the frame: the turn fails as malformed (or with the classified failure) and stops here.</summary>
    private static void RecordReduceFailure(TurnContext ctx, WorkIqStreamFrame frame, Exception ex, WorkIqOperation operation, List<WorkIqStreamEvent> events)
    {
        var failure = WorkIqA2AClient.Classify(ex, operation, requestId: null, ctx.Handle?.TaskId, ctx.ContextId) with
        {
            // The frame arrived, so a streamed send was accepted and a task may exist (WorkIqA2AClient.UnusableReply).
            Certainty = operation == WorkIqOperation.Stream ? OutcomeCertainty.Indeterminate : OutcomeCertainty.Known,
            RpcRequestId = ctx.Identity.RpcRequestId,
            RemoteTaskState = ctx.Snapshot?.RawState,
        };
        if (failure.Category is WorkIqFailureCategory.Unknown or WorkIqFailureCategory.Canceled or WorkIqFailureCategory.Timeout)
        {
            failure = failure with
            {
                Category = WorkIqFailureCategory.Malformed,
                SafeMessage = $"{operation}: frame {frame.ClientSequence} could not be reduced ({ex.GetType().Name}); the turn stops here",
            };
        }

        ctx.Fail(failure, TransportOutcome.Failed);
        events.Add(NewWarning(ctx, frame.ReceivedAt, "reduce.failed", failure.SafeMessage));
        ctx.Trace(WorkIqTraceStage.Reduce, "reduce.failed", failure.SafeMessage, WorkIqTraceLevel.Error, taskId: ctx.Handle?.TaskId, contextId: ctx.ContextId);
    }
}
