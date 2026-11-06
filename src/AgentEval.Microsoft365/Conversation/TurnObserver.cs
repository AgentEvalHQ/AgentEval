// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using A2A;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// Records what the service returned into the turn: a task, through the turn's reducer, or a direct message. It sets
/// the timing marks, traces each change of task state once and traces each new schema warning once. It tells the
/// conversation state which task the turn works on as soon as a snapshot names it.
/// </summary>
internal sealed class TurnObserver
{
    private readonly TaskHandleFactory _handles;
    private readonly ConversationState _state;

    public TurnObserver(TaskHandleFactory handles, ConversationState state)
    {
        _handles = handles;
        _state = state;
    }

    /// <summary>The trace stage of a snapshot source.</summary>
    /// <param name="source">The source.</param>
    public static WorkIqTraceStage StageOf(WorkIqSnapshotSource source) => source switch
    {
        WorkIqSnapshotSource.GetTask => WorkIqTraceStage.GetTask,
        WorkIqSnapshotSource.Stream => WorkIqTraceStage.Stream,
        WorkIqSnapshotSource.Subscribe => WorkIqTraceStage.Subscribe,
        _ => WorkIqTraceStage.Send,
    };

    /// <summary>Loads a whole task (a send reply, a <c>GetTask</c> or a <c>CancelTask</c> reply) and adopts its snapshot.</summary>
    /// <param name="ctx">The turn.</param>
    /// <param name="task">The task.</param>
    /// <param name="source">Where it came from.</param>
    /// <param name="capture">The reply's capture, when any.</param>
    /// <param name="at">When it was received.</param>
    public void ApplyTask(TurnContext ctx, AgentTask task, WorkIqSnapshotSource source, RawCaptureRef? capture, DateTimeOffset at)
    {
        var reducer = ctx.Reducer ??= _handles.NewReducer(ctx);
        reducer.Load(task, source, null, capture);
        var snapshot = reducer.Snapshot() ?? throw new InvalidOperationException("The task reducer produced no snapshot after loading a task.");
        var handle = _handles.Build(ctx.Card!, task.Id, task.ContextId);
        Adopt(ctx, snapshot with { Handle = handle, Capture = capture ?? snapshot.Capture }, at, source);
    }

    /// <summary>Adopts the reducer's current snapshot after a stream frame; null while no task snapshot exists yet.</summary>
    /// <param name="ctx">The turn.</param>
    /// <param name="source">Where the frame came from.</param>
    /// <param name="at">When the frame was received.</param>
    public WorkIqTaskSnapshot? Refresh(TurnContext ctx, WorkIqSnapshotSource source, DateTimeOffset at)
    {
        var snapshot = ctx.Reducer?.Snapshot();
        if (snapshot is null)
        {
            return null;
        }

        var handle = _handles.Build(ctx.Card!, snapshot.Handle.TaskId, snapshot.Handle.ContextId ?? ctx.ContextId);
        var adopted = snapshot with { Handle = handle };
        Adopt(ctx, adopted, at, source);
        return adopted;
    }

    /// <summary>Records a direct message reply (no task). It ends the turn's remote work.</summary>
    /// <param name="ctx">The turn.</param>
    /// <param name="message">The message.</param>
    /// <param name="at">When it was received.</param>
    public static void ApplyDirectMessage(TurnContext ctx, WorkIqMessage message, DateTimeOffset at)
    {
        ctx.DirectMessage = message;
        if (!string.IsNullOrWhiteSpace(message.ContextId))
        {
            ctx.ContextId = message.ContextId;
        }

        ctx.TerminalAt ??= at;
        if (ctx.FirstAnswerAt is null && message.Parts.Any(p => p.Kind == WorkIqPartKind.Text && !string.IsNullOrEmpty(p.Text)))
        {
            ctx.FirstAnswerAt = at;
        }

        ctx.Trace(
            ctx.Operation == WorkIqOperation.Stream ? WorkIqTraceStage.Stream : WorkIqTraceStage.Send,
            "task.state",
            $"direct message reply (no task) with {message.Parts.Length} part(s)",
            contextId: message.ContextId);
    }

    private void Adopt(TurnContext ctx, WorkIqTaskSnapshot snapshot, DateTimeOffset at, WorkIqSnapshotSource source)
    {
        var previous = ctx.Snapshot;
        ctx.Snapshot = snapshot;
        ctx.Handle = snapshot.Handle;
        _state.NoteRunningTask(ctx.Epoch, snapshot.Handle);
        if (!string.IsNullOrWhiteSpace(snapshot.Handle.ContextId))
        {
            ctx.ContextId = snapshot.Handle.ContextId;
        }

        if (ctx.FirstAnswerAt is null && HasAnswerText(snapshot))
        {
            ctx.FirstAnswerAt = at;
        }

        if (snapshot.IsTerminal || snapshot.IsSuspended)
        {
            ctx.TerminalAt ??= at;
        }

        if (previous is null || previous.State != snapshot.State || !string.Equals(previous.RawState, snapshot.RawState, StringComparison.Ordinal))
        {
            ctx.Trace(
                StageOf(source),
                "task.state",
                $"task state {snapshot.RawState ?? "(absent)"} via {source} ({snapshot.Artifacts.Length} artifact(s){(previous is null ? "" : $", was {previous.RawState ?? "(absent)"}")})",
                snapshot.State == WorkIqTaskState.Unknown ? WorkIqTraceLevel.Warning : WorkIqTraceLevel.Info,
                taskId: snapshot.Handle.TaskId,
                contextId: snapshot.Handle.ContextId,
                a2aState: snapshot.RawState);
        }

        for (var i = ctx.SchemaWarningsTraced; i < snapshot.SchemaWarnings.Length; i++)
        {
            ctx.Trace(WorkIqTraceStage.Reduce, "reduce.schema_warning", snapshot.SchemaWarnings[i], WorkIqTraceLevel.Warning, taskId: snapshot.Handle.TaskId, contextId: snapshot.Handle.ContextId);
        }

        ctx.SchemaWarningsTraced = Math.Max(ctx.SchemaWarningsTraced, snapshot.SchemaWarnings.Length);
    }

    private static bool HasAnswerText(WorkIqTaskSnapshot snapshot)
    {
        foreach (var artifact in snapshot.Artifacts)
        {
            if (WorkIqAnswerSelector.IsDiagnosticArtifact(artifact))
            {
                continue;
            }

            if (artifact.Parts.Any(p => p.Kind == WorkIqPartKind.Text && !string.IsNullOrEmpty(p.Text)))
            {
                return true;
            }
        }

        return false;
    }
}
