// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using A2A;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365.Protocol;

/// <summary>
/// Reduces task snapshots, <c>statusUpdate</c> and <c>artifactUpdate</c> events into the current
/// <see cref="WorkIqTaskSnapshot"/>: artifacts keyed by <c>(taskId, artifactId)</c> with <c>append</c>
/// true/false/omitted, <c>lastChunk</c> closing one artifact only, non-text parts preserved, unknown fields
/// counted as schema warnings, identical chunks never de-duplicated. A subscription's first snapshot
/// <b>replaces</b> state; a terminal snapshot reconciles it. It never treats HTTP 200, EOF, <c>lastChunk</c> or
/// telemetry as completion: only an observed <see cref="WorkIqTaskState.Completed"/> state is. Owned by lane C (M13).
/// </summary>
/// <remarks>
/// <para>One reducer serves one turn (one task identity, or one direct message). It is built with a
/// <i>handle template</i> — the endpoint, protocol version, agent, routing tenant and principal the turn addresses —
/// whose task/context ids may be blank until the service assigns them; the first snapshot or update fills them.</para>
/// <para>Every conformance deviation the typed SDK objects can still show (one-of violations, id mismatches, a
/// status or artifact update before any snapshot, an append after <c>lastChunk</c>, a second direct message, an
/// artifact without an id…) is appended to <see cref="SchemaWarnings"/>, traced at <see cref="WorkIqTraceLevel.Warning"/>
/// in stage <see cref="WorkIqTraceStage.Reduce"/> and registered as a <see cref="WorkIqIssueCategory.Protocol"/> issue.
/// A frame-level failure (JSON-RPC error, malformed frame, transport drop) is kept in <see cref="Failures"/> and
/// marks the reduction <see cref="IsInterrupted"/>; later valid frames still apply, so a stream that fails and
/// then completes reads as Completed <i>and</i> interrupted — never as a clean pass.</para>
/// <para>End of stream (<see cref="MarkStreamEnded"/>): EOF is a normal end when the task has <i>settled</i> — a
/// terminal state (Completed, Failed, Canceled, Rejected) or a suspended one (InputRequired, AuthRequired: the service
/// closes the stream and waits for input on the same task; HLD D6, research "Streaming, snapshots and multiple turns"
/// item 9) — or when a direct message was the reply. EOF while the task is still Submitted/Working (or Unknown), or
/// before any task identity or message, is an interruption. Settled is not completed: a suspended or failed task never
/// yields an answer.</para>
/// <para>Not thread-safe: a turn applies frames sequentially.</para>
/// </remarks>
internal sealed class WorkIqTaskReducer
{
    private readonly WorkIqTaskHandle _template;
    private readonly TimeProvider _clock;
    private readonly WorkIqDiagnostics _diagnostics;
    private readonly string? _caseId;
    private readonly string? _turnId;
    private readonly List<ArtifactEntry> _artifacts = [];
    private readonly Dictionary<string, ArtifactEntry> _artifactIndex = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];
    private readonly List<WorkIqFailure> _failures = [];
    private readonly List<WorkIqMessage> _additionalDirectMessages = [];
    private WorkIqTaskHandle? _handle;
    private WorkIqTaskState _state;
    private string? _rawState;
    private DateTimeOffset? _statusTimestamp;
    private WorkIqMessage? _statusMessage;
    private ImmutableArray<WorkIqMessage> _history = ImmutableArray<WorkIqMessage>.Empty;
    private WorkIqSnapshotSource _source;
    private DateTimeOffset _observedAt;
    private RawCaptureRef? _capture;
    private WorkIqMessage? _directMessage;
    private bool _interrupted;
    private int _interruptions;
    private int _framesSeen;
    private int _framesApplied;

    /// <summary>Creates a reducer for one turn.</summary>
    /// <param name="handleTemplate">
    /// The handle the turn addresses: endpoint, protocol version, agent id, routing tenant and principal key are
    /// taken as given; <see cref="WorkIqTaskHandle.TaskId"/> may be blank (not yet assigned) and
    /// <see cref="WorkIqTaskHandle.ContextId"/> null — the first snapshot or update fills them.
    /// </param>
    /// <param name="clock">The clock for <see cref="WorkIqTaskSnapshot.ObservedAt"/> when a snapshot arrives without a frame; null uses <see cref="TimeProvider.System"/>.</param>
    /// <param name="diagnostics">Where <c>reduce.*</c> events and issues go; null means <see cref="WorkIqDiagnostics.None"/>.</param>
    /// <param name="caseId">The benchmark case id stamped on every event, when known.</param>
    /// <param name="turnId">The client turn id stamped on every event, when known.</param>
    public WorkIqTaskReducer(
        WorkIqTaskHandle handleTemplate,
        TimeProvider? clock = null,
        WorkIqDiagnostics? diagnostics = null,
        string? caseId = null,
        string? turnId = null)
    {
        _template = handleTemplate ?? throw new ArgumentNullException(nameof(handleTemplate));
        _clock = clock ?? TimeProvider.System;
        _diagnostics = diagnostics ?? WorkIqDiagnostics.None;
        _caseId = caseId;
        _turnId = turnId;
        _observedAt = _clock.GetUtcNow();
    }

    /// <summary>The handle of the task being reduced; null before any task identity was established.</summary>
    public WorkIqTaskHandle? Handle => _handle;

    /// <summary>Whether a task identity was established (a snapshot, status update or artifact update was applied).</summary>
    public bool HasTask => _handle is not null;

    /// <summary>The source of the last snapshot loaded (<see cref="WorkIqSnapshotSource.Stream"/> until a snapshot arrives).</summary>
    public WorkIqSnapshotSource Source => _source;

    /// <summary>
    /// Whether the transport was interrupted: EOF before the task settled (a terminal or suspended state) and
    /// before any direct message, a connection drop or local abort reported through <see cref="MarkInterrupted()"/>,
    /// or a frame that failed. EOF after <see cref="WorkIqTaskState.InputRequired"/> or
    /// <see cref="WorkIqTaskState.AuthRequired"/> is <b>not</b> an interruption: the service ended the stream to wait
    /// for input. Sticky: a later subscription snapshot replaces the <i>state</i> but the evidence gap happened; the
    /// conversation records the recovery separately. <see cref="Interruptions"/> counts them.
    /// </summary>
    public bool IsInterrupted => _interrupted;

    /// <summary>How many times <see cref="MarkInterrupted()"/> took effect.</summary>
    public int Interruptions => _interruptions;

    /// <summary>The most recent frame-level failure, when any.</summary>
    public WorkIqFailure? Failure => _failures.Count == 0 ? null : _failures[^1];

    /// <summary>Every frame-level failure in arrival order.</summary>
    public IReadOnlyList<WorkIqFailure> Failures => _failures;

    /// <summary>The conformance notes recorded so far (also carried by every <see cref="Snapshot"/>).</summary>
    public IReadOnlyList<string> SchemaWarnings => _warnings;

    /// <summary>How many direct messages arrived (a message-only stream must carry exactly one).</summary>
    public int DirectMessageCount => _directMessage is null ? 0 : 1 + _additionalDirectMessages.Count;

    /// <summary>Direct messages after the first, retained so nothing is lost; each one was a warning.</summary>
    public IReadOnlyList<WorkIqMessage> AdditionalDirectMessages => _additionalDirectMessages;

    /// <summary>How many frames were handed to <see cref="Apply(WorkIqStreamFrame)"/>.</summary>
    public int FramesSeen => _framesSeen;

    /// <summary>How many frames changed the state (failures and skipped frames excluded).</summary>
    public int FramesApplied => _framesApplied;

    /// <summary>Loads (or, on a subscription/recovery snapshot, replaces) the state from a full task.</summary>
    /// <param name="task">The SDK task.</param>
    /// <param name="source">Which operation produced it.</param>
    /// <param name="frame">The frame or reply it arrived in (for capture/sequence provenance).</param>
    /// <param name="capture">The raw capture of a blocking reply (<c>SendMessage</c>/<c>GetTask</c>), when the caller has it; null keeps the current one.</param>
    /// <remarks>
    /// Snapshot semantics: the task's artifacts replace the current set in wire order. An artifact already known
    /// keeps its revision when its content (name, description, metadata, parts) is identical and its closed flag;
    /// a changed one gets revision + 1 and is re-opened; a new one starts at revision 0; one absent from the
    /// snapshot is dropped. Status, history and the handle's ids are replaced. Completion is never inferred: the
    /// state is whatever the snapshot says.
    /// </remarks>
    public void Load(AgentTask task, WorkIqSnapshotSource source, WorkIqStreamFrame? frame, RawCaptureRef? capture = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        var warnings = new List<string>();
        var previousState = _state;
        var replaced = _handle is not null;
        if (_directMessage is not null)
        {
            warnings.Add("task.after_direct_message: a task snapshot arrived after a direct message; a stream carries one or the other");
        }

        var handle = WorkIqSdkMapping.ReconcileHandle(_handle ?? _template, task.Id, task.ContextId, warnings, "task");
        var status = task.Status ?? new A2A.TaskStatus();
        WorkIqSdkMapping.NoteState(status.State, warnings, "task");

        var added = 0;
        var changed = 0;
        var incoming = new List<ArtifactEntry>();
        var incomingIndex = new Dictionary<string, ArtifactEntry>(StringComparer.Ordinal);
        if (task.Artifacts is { Count: > 0 })
        {
            foreach (var artifact in task.Artifacts)
            {
                var mapped = WorkIqSdkMapping.ToArtifact(artifact, revision: 0, warnings);
                ArtifactEntry entry;
                if (incomingIndex.ContainsKey(mapped.ArtifactId))
                {
                    warnings.Add($"artifact.duplicate_id_in_snapshot: artifact id '{mapped.ArtifactId}' appears more than once in one task snapshot; every occurrence is kept, updates address the first");
                    entry = ArtifactEntry.FromSnapshot(mapped, revision: 0, isClosed: false);
                    added++;
                }
                else if (_artifactIndex.TryGetValue(mapped.ArtifactId, out var existing))
                {
                    var same = existing.ContentEquals(mapped);
                    entry = ArtifactEntry.FromSnapshot(mapped, same ? existing.Revision : existing.Revision + 1, same && existing.IsClosed);
                    if (!same)
                    {
                        changed++;
                    }
                }
                else
                {
                    entry = ArtifactEntry.FromSnapshot(mapped, revision: 0, isClosed: false);
                    added++;
                }

                incoming.Add(entry);
                incomingIndex.TryAdd(mapped.ArtifactId, entry);
            }
        }

        var dropped = _artifactIndex.Keys.Count(k => !incomingIndex.ContainsKey(k));
        var history = task.History is { Count: > 0 }
            ? task.History.Select(m => WorkIqSdkMapping.ToMessage(m, warnings)).ToImmutableArray()
            : ImmutableArray<WorkIqMessage>.Empty;
        var statusMessage = status.Message is null ? null : WorkIqSdkMapping.ToMessage(status.Message, warnings);
        if (status.Message is not null)
        {
            NoteStatusMessageIds(status.Message, handle, warnings, "task");
        }

        _artifacts.Clear();
        _artifacts.AddRange(incoming);
        _artifactIndex.Clear();
        foreach (var (id, entry) in incomingIndex)
        {
            _artifactIndex[id] = entry;
        }

        _handle = handle;
        _state = WorkIqSdkMapping.ToTaskState(status.State);
        _rawState = WorkIqSdkMapping.ToRawState(status.State);
        _statusTimestamp = status.Timestamp;
        _statusMessage = statusMessage;
        _history = history;
        _source = source;
        _observedAt = frame?.ReceivedAt ?? _clock.GetUtcNow();
        if (capture is not null)
        {
            _capture = capture;
        }

        _framesApplied++;
        RecordWarnings(warnings);
        Trace(
            "reduce.snapshot_loaded",
            replaced ? $"task snapshot replaced the state ({source})" : $"task snapshot loaded ({source})",
            WorkIqTraceLevel.Info,
            new SnapshotData(source.ToString(), replaced, incoming.Count, added, changed, dropped, history.Length, statusMessage is not null, frame?.ClientSequence, frame?.ConnectionEpoch));
        TraceStateChange(previousState);
    }

    /// <summary>Applies one validated stream frame (status update, artifact update, task snapshot or direct message).</summary>
    /// <param name="frame">The frame.</param>
    public void Apply(WorkIqStreamFrame frame) => Apply(frame, WorkIqSnapshotSource.Stream);

    /// <summary>
    /// Applies one stream frame; <paramref name="snapshotSource"/> says which operation delivered it so a task
    /// frame is loaded as <see cref="WorkIqSnapshotSource.Stream"/> or <see cref="WorkIqSnapshotSource.Subscribe"/>
    /// (a subscription's first frame replaces the state).
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <param name="snapshotSource">The stream operation that delivered the frame.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="snapshotSource"/> is not a stream source.</exception>
    public void Apply(WorkIqStreamFrame frame, WorkIqSnapshotSource snapshotSource)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (snapshotSource is not (WorkIqSnapshotSource.Stream or WorkIqSnapshotSource.Subscribe))
        {
            throw new ArgumentOutOfRangeException(nameof(snapshotSource), snapshotSource, "A frame comes from SendStreamingMessage or SubscribeToTask.");
        }

        _framesSeen++;
        if (frame.Failure is { } failure)
        {
            RecordFrameFailure(frame, failure, ownIssue: false);
            return;
        }

        var response = frame.Event;
        if (response is null)
        {
            RecordFrameFailure(frame, MalformedFrame(frame, "the frame carries neither an event nor a failure"), ownIssue: true);
            return;
        }

        var populated = (response.Task is null ? 0 : 1) + (response.Message is null ? 0 : 1) + (response.StatusUpdate is null ? 0 : 1) + (response.ArtifactUpdate is null ? 0 : 1);
        if (populated != 1)
        {
            RecordWarnings([$"frame.one_of_violation: frame {frame.ClientSequence} has {populated} of task|message|statusUpdate|artifactUpdate populated; exactly one is required"]);
            RecordFrameFailure(frame, MalformedFrame(frame, $"{populated} of task|message|statusUpdate|artifactUpdate populated; exactly one is required"), ownIssue: true);
            return;
        }

        switch (response.PayloadCase)
        {
            case StreamResponseCase.Task:
                Load(response.Task!, snapshotSource, frame);
                break;

            case StreamResponseCase.Message:
                ApplyDirectMessage(frame, response.Message!);
                break;

            case StreamResponseCase.StatusUpdate:
                ApplyStatusUpdate(frame, response.StatusUpdate!);
                break;

            case StreamResponseCase.ArtifactUpdate:
                ApplyArtifactUpdate(frame, response.ArtifactUpdate!);
                break;

            default:
                RecordFrameFailure(frame, MalformedFrame(frame, "the SDK reports no payload case"), ownIssue: true);
                break;
        }
    }

    /// <summary>
    /// Marks the transport interrupted (a connection drop, a read failure or a local abort — the caller classifies
    /// a plain EOF with <see cref="MarkStreamEnded"/> instead); the state itself is unchanged.
    /// </summary>
    public void MarkInterrupted() => MarkInterrupted("the stream dropped before the operation did");

    /// <summary>
    /// Tells the reducer the stream reached EOF and classifies the end. The stream ended <i>normally</i> when the
    /// task had settled — a terminal state (<see cref="WorkIqTaskState.Completed"/>, <see cref="WorkIqTaskState.Failed"/>,
    /// <see cref="WorkIqTaskState.Canceled"/>, <see cref="WorkIqTaskState.Rejected"/>) or a suspended one
    /// (<see cref="WorkIqTaskState.InputRequired"/>, <see cref="WorkIqTaskState.AuthRequired"/>: the service closes
    /// the stream and waits for input; the task stays resumable on the same id) — or when a direct message was the
    /// reply and no task was opened. EOF while the task is still <see cref="WorkIqTaskState.Submitted"/> or
    /// <see cref="WorkIqTaskState.Working"/> (or <see cref="WorkIqTaskState.Unknown"/>), or before any task identity
    /// or message, is an interruption: the transport ended, the operation did not. Traces <c>reduce.stream_ended</c>
    /// (Info when settled, Warning otherwise). Settled is never completion: only
    /// <see cref="WorkIqTaskState.Completed"/> yields an answer (HLD D6).
    /// </summary>
    /// <returns>Whether the reduction is interrupted after the call.</returns>
    public bool MarkStreamEnded()
    {
        var terminal = _handle is not null && IsTerminal(_state);
        var suspended = _handle is not null && IsSuspended(_state);
        var directReply = _directMessage is not null && _handle is null;
        var settled = terminal || suspended || directReply;
        var how = terminal
            ? $"a terminal state ({_rawState ?? _state.ToString()})"
            : suspended
                ? $"a suspended state ({_rawState ?? _state.ToString()}); the service waits for input on the same task"
                : "a direct message";
        Trace(
            "reduce.stream_ended",
            settled ? $"stream ended after {how}" : $"stream ended before the task settled (last state: {(_handle is null ? "none" : _rawState ?? _state.ToString())})",
            settled ? WorkIqTraceLevel.Info : WorkIqTraceLevel.Warning,
            new StreamEndData(_framesSeen, _framesApplied, _directMessage is not null, terminal, suspended, _artifacts.Count));
        if (!settled)
        {
            MarkInterrupted("the stream reached EOF before a terminal or suspended task state or a direct message");
        }

        return _interrupted;
    }

    /// <summary>Attaches the raw capture every later <see cref="Snapshot"/> carries (a stream's capture is known only when it ends).</summary>
    /// <param name="capture">The capture reference.</param>
    public void AttachCapture(RawCaptureRef capture)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    }

    /// <summary>The current reduced snapshot; null before any task identity was established.</summary>
    public WorkIqTaskSnapshot? Snapshot()
    {
        if (_handle is null)
        {
            return null;
        }

        return new WorkIqTaskSnapshot(
            _handle,
            _state,
            _rawState,
            _statusTimestamp,
            _statusMessage,
            _artifacts.Select(a => a.ToArtifact()).ToImmutableArray(),
            _history,
            _source,
            _observedAt,
            _warnings.ToImmutableArray(),
            _capture);
    }

    /// <summary>The direct message reply when the service answered without a task; null otherwise.</summary>
    public WorkIqMessage? DirectMessage() => _directMessage;

    private void ApplyDirectMessage(WorkIqStreamFrame frame, Message message)
    {
        var warnings = new List<string>();
        var mapped = WorkIqSdkMapping.ToMessage(message, warnings);
        if (_handle is not null)
        {
            warnings.Add("message.in_task_stream: a direct message arrived in a stream that already carries a task; it is kept apart from the task's artifacts");
            if (mapped.TaskId is not null && !string.Equals(mapped.TaskId, _handle.TaskId, StringComparison.Ordinal))
            {
                warnings.Add($"message.task_id_mismatch: expected task '{_handle.TaskId}' but the message carries '{mapped.TaskId}'");
            }

            if (mapped.ContextId is not null && _handle.ContextId is not null && !string.Equals(mapped.ContextId, _handle.ContextId, StringComparison.Ordinal))
            {
                warnings.Add($"message.context_id_mismatch: expected context '{_handle.ContextId}' but the message carries '{mapped.ContextId}'");
            }
        }

        if (_directMessage is null)
        {
            _directMessage = mapped;
        }
        else
        {
            _additionalDirectMessages.Add(mapped);
            warnings.Add($"message.duplicate_direct_message: a message-only stream must contain exactly one message; message #{DirectMessageCount} is retained separately");
        }

        _framesApplied++;
        _observedAt = frame.ReceivedAt;
        RecordWarnings(warnings);
        Trace(
            "reduce.direct_message",
            _additionalDirectMessages.Count == 0 ? "direct message received" : $"additional direct message #{DirectMessageCount} received",
            WorkIqTraceLevel.Info,
            new MessageData(mapped.Parts.Length, mapped.Metadata is not null, mapped.TaskId, mapped.ContextId, frame.ClientSequence, frame.ConnectionEpoch),
            messageId: mapped.MessageId);
    }

    private void ApplyStatusUpdate(WorkIqStreamFrame frame, TaskStatusUpdateEvent update)
    {
        var warnings = new List<string>();
        if (!EstablishOrCheckIdentity(update.TaskId, update.ContextId, warnings, "status"))
        {
            RecordWarnings(warnings);
            Trace("reduce.status_skipped", "status update for another task was not applied", WorkIqTraceLevel.Warning, new SkippedData(update.TaskId, update.ContextId, frame.ClientSequence, frame.ConnectionEpoch), taskIdOverride: update.TaskId);
            return;
        }

        var previousState = _state;
        var status = update.Status ?? new A2A.TaskStatus();
        WorkIqSdkMapping.NoteState(status.State, warnings, "status");
        var statusMessage = status.Message is null ? null : WorkIqSdkMapping.ToMessage(status.Message, warnings);
        if (status.Message is not null)
        {
            NoteStatusMessageIds(status.Message, _handle!, warnings, "status");
        }

        _state = WorkIqSdkMapping.ToTaskState(status.State);
        _rawState = WorkIqSdkMapping.ToRawState(status.State);
        _statusTimestamp = status.Timestamp;
        _statusMessage = statusMessage;
        _framesApplied++;
        _observedAt = frame.ReceivedAt;
        RecordWarnings(warnings);
        Trace(
            "reduce.status_applied",
            $"status update applied: {_rawState ?? "(no state)"}",
            WorkIqTraceLevel.Info,
            new StatusData(previousState.ToString(), _state.ToString(), statusMessage is not null, statusMessage?.Parts.Length ?? 0, statusMessage?.Metadata is not null, status.Timestamp, frame.ClientSequence, frame.ConnectionEpoch));
        TraceStateChange(previousState);
    }

    private void ApplyArtifactUpdate(WorkIqStreamFrame frame, TaskArtifactUpdateEvent update)
    {
        var warnings = new List<string>();
        if (!EstablishOrCheckIdentity(update.TaskId, update.ContextId, warnings, "artifact"))
        {
            RecordWarnings(warnings);
            Trace("reduce.artifact_skipped", "artifact update for another task was not applied", WorkIqTraceLevel.Warning, new SkippedData(update.TaskId, update.ContextId, frame.ClientSequence, frame.ConnectionEpoch), taskIdOverride: update.TaskId);
            return;
        }

        var mapped = WorkIqSdkMapping.ToArtifact(update.Artifact ?? new Artifact(), revision: 0, warnings);
        if (string.IsNullOrEmpty(mapped.ArtifactId))
        {
            warnings.Add("artifact.update_without_id: the artifact update cannot be keyed and was skipped; its bytes remain in the raw capture");
            RecordWarnings(warnings);
            Trace("reduce.artifact_skipped", "artifact update without an artifact id was not applied", WorkIqTraceLevel.Warning, new SkippedData(update.TaskId, update.ContextId, frame.ClientSequence, frame.ConnectionEpoch));
            return;
        }

        var created = false;
        if (_artifactIndex.TryGetValue(mapped.ArtifactId, out var entry))
        {
            if (update.Append)
            {
                if (entry.IsClosed)
                {
                    warnings.Add($"artifact.append_after_last_chunk: artifact '{mapped.ArtifactId}' received {mapped.Parts.Length} more part(s) after lastChunk; applied, never de-duplicated");
                }

                entry.Parts.AddRange(mapped.Parts);
                entry.IsClosed = entry.IsClosed || update.LastChunk;
            }
            else
            {
                entry.Parts.Clear();
                entry.Parts.AddRange(mapped.Parts);
                entry.IsClosed = update.LastChunk;
            }

            entry.Name = mapped.Name ?? entry.Name;
            entry.Description = mapped.Description ?? entry.Description;
            entry.Metadata = mapped.Metadata ?? entry.Metadata;
            entry.Revision++;
        }
        else
        {
            created = true;
            entry = ArtifactEntry.FromSnapshot(mapped, revision: 1, isClosed: update.LastChunk);
            _artifacts.Add(entry);
            _artifactIndex[mapped.ArtifactId] = entry;
        }

        _framesApplied++;
        _observedAt = frame.ReceivedAt;
        RecordWarnings(warnings);
        Trace(
            "reduce.artifact_applied",
            $"artifact update applied ({(update.Append ? "append" : "replace")}{(update.LastChunk ? ", lastChunk" : "")}{(created ? ", created" : "")})",
            WorkIqTraceLevel.Info,
            new ArtifactData(mapped.ArtifactId, entry.Name, update.Append, update.LastChunk, created, entry.Revision, mapped.Parts.Length, entry.Parts.Count, frame.ClientSequence, frame.ConnectionEpoch));
    }

    /// <summary>
    /// Establishes the task identity from an update when none exists yet (a conforming stream starts with a
    /// snapshot; identity from an update is recorded as a warning), or checks the update's ids against it. Returns
    /// false when the update belongs to another task (it must not mutate this task's state).
    /// </summary>
    private bool EstablishOrCheckIdentity(string? taskId, string? contextId, List<string> warnings, string scope)
    {
        if (_handle is null)
        {
            if (string.IsNullOrEmpty(taskId))
            {
                warnings.Add($"{scope}.before_task_snapshot: a {scope} update arrived before any task snapshot and carries no task id; it was not applied");
                return false;
            }

            warnings.Add($"{scope}.before_task_snapshot: a {scope} update arrived before any task snapshot; the task identity was taken from the update");
            _handle = WorkIqSdkMapping.ReconcileHandle(_template, taskId, contextId, warnings, scope);
            _state = WorkIqTaskState.Unknown;
            _rawState = null;
            return true;
        }

        if (string.IsNullOrEmpty(taskId))
        {
            warnings.Add($"{scope}.id_missing: the {scope} update carries no task id (required by A2A 1.0); it was applied to the current task");
            return true;
        }

        if (!string.Equals(taskId, _handle.TaskId, StringComparison.Ordinal))
        {
            warnings.Add($"{scope}.task_id_mismatch: expected task '{_handle.TaskId}' but the {scope} update carries '{taskId}'; not applied");
            return false;
        }

        if (!string.IsNullOrEmpty(contextId) && _handle.ContextId is not null && !string.Equals(contextId, _handle.ContextId, StringComparison.Ordinal))
        {
            warnings.Add($"{scope}.context_id_mismatch: expected context '{_handle.ContextId}' but the {scope} update carries '{contextId}'; applied by task id");
        }
        else if (!string.IsNullOrEmpty(contextId) && _handle.ContextId is null)
        {
            _handle = _handle with { ContextId = contextId };
        }

        return true;
    }

    private static void NoteStatusMessageIds(Message message, WorkIqTaskHandle handle, List<string> warnings, string scope)
    {
        if (!string.IsNullOrEmpty(message.TaskId) && !string.Equals(message.TaskId, handle.TaskId, StringComparison.Ordinal))
        {
            warnings.Add($"{scope}.message_task_id_mismatch: the status message carries task '{message.TaskId}' but the task is '{handle.TaskId}'");
        }

        if (!string.IsNullOrEmpty(message.ContextId) && handle.ContextId is not null && !string.Equals(message.ContextId, handle.ContextId, StringComparison.Ordinal))
        {
            warnings.Add($"{scope}.message_context_id_mismatch: the status message carries context '{message.ContextId}' but the task's is '{handle.ContextId}'");
        }
    }

    private void RecordFrameFailure(WorkIqStreamFrame frame, WorkIqFailure failure, bool ownIssue)
    {
        _failures.Add(failure);
        if (failure.Category == WorkIqFailureCategory.Malformed)
        {
            _warnings.Add($"frame.malformed: frame {frame.ClientSequence} (epoch {frame.ConnectionEpoch}) was not usable: {failure.SafeMessage}");
        }

        Trace(
            "reduce.frame_failure",
            $"frame {frame.ClientSequence} carried a {failure.Category} failure: {failure.SafeMessage}",
            WorkIqTraceLevel.Warning,
            new FailureData(failure.Category.ToString(), failure.Operation.ToString(), failure.Certainty.ToString(), frame.ClientSequence, frame.ConnectionEpoch),
            httpStatus: failure.HttpStatus,
            rpcErrorCode: failure.RpcErrorCode,
            requestId: failure.RequestId);
        if (ownIssue)
        {
            Issue(WorkIqIssueCategory.Protocol, "reduce.frame_malformed", $"frame {frame.ClientSequence} was malformed: {failure.SafeMessage}",
                "The service sent a stream frame that violates the A2A 1.0 one-of rule (task | message | statusUpdate | artifactUpdate); the raw capture holds the exact bytes. The frame was not applied and the run is marked interrupted so it cannot pass silently.");
        }

        MarkInterrupted($"frame {frame.ClientSequence} failed ({failure.Category})");
    }

    private void MarkInterrupted(string reason)
    {
        if (_interrupted)
        {
            Trace("reduce.interrupted", $"interruption noted again: {reason}", WorkIqTraceLevel.Info, null);
            return;
        }

        _interrupted = true;
        _interruptions++;
        var terminal = _handle is not null && IsTerminal(_state);
        var suspended = _handle is not null && IsSuspended(_state);
        var observedState = _handle is null ? "none" : _rawState ?? _state.ToString();
        Trace("reduce.interrupted", $"reduction marked interrupted: {reason}", WorkIqTraceLevel.Warning, new InterruptedData(reason, _state.ToString(), terminal, suspended, _directMessage is not null, _framesSeen, _framesApplied));
        Issue(
            WorkIqIssueCategory.Network,
            "reduce.interrupted",
            terminal
                ? $"the stream was interrupted ({reason}) although a terminal state ({observedState}) was observed; telemetry may be incomplete"
                : suspended
                    ? $"the stream was interrupted ({reason}) after a suspended state ({observedState}) was observed; the pause is known, telemetry may be incomplete"
                    : $"the stream was interrupted ({reason}) before a terminal or suspended task state (last state: {observedState})",
            terminal
                ? "A frame failed or the connection dropped during the stream, but the task reached a terminal state from the valid frames. The answer is usable only if its state is completed; intermediate events may be missing from the evidence."
                : suspended
                    ? "A frame failed or the connection dropped after the task paused for input or authorization. The pause and its payload come from the valid frames and the task stays resumable on the same id; intermediate events may be missing from the evidence. A paused task is not an answer either way."
                    : "The connection dropped or the stream ended before the task finished. When a task id is known the turn reconciles with GetTask (and SubscribeToTask when the card allows streaming) under the recovery budget; otherwise the outcome is indeterminate and the prompt is not resent.");
    }

    private WorkIqFailure MalformedFrame(WorkIqStreamFrame frame, string detail) =>
        new(
            WorkIqFailureCategory.Malformed,
            $"stream frame {frame.ClientSequence} is malformed: {detail}",
            OutcomeCertainty.Indeterminate,
            WorkIqOperation.Local,
            TaskId: _handle?.TaskId,
            ContextId: _handle?.ContextId);

    private void RecordWarnings(List<string> warnings)
    {
        foreach (var warning in warnings)
        {
            _warnings.Add(warning);
            var separator = warning.IndexOf(':', StringComparison.Ordinal);
            var code = separator > 0 ? warning[..separator] : warning;
            Trace("reduce." + code, warning, WorkIqTraceLevel.Warning, null);
            Issue(WorkIqIssueCategory.Protocol, "reduce." + code, warning, SuggestedCause(code));
        }
    }

    private static string SuggestedCause(string code)
    {
        if (code.EndsWith(".task_id_mismatch", StringComparison.Ordinal) || code.EndsWith(".id_mismatch", StringComparison.Ordinal) || code.EndsWith(".context_id_mismatch", StringComparison.Ordinal))
        {
            return "A frame carried a task or context id different from the task this turn is tracking; the service may be multiplexing, the subscription may target another task, or the reply belongs to a previous turn. Check the ids in the raw capture.";
        }

        if (code.EndsWith(".before_task_snapshot", StringComparison.Ordinal))
        {
            return "A status or artifact update arrived before any task snapshot. A conforming stream starts with the task; identity was taken from the update, so earlier state (if any) may be missing.";
        }

        if (code.StartsWith("artifact.append_after_last_chunk", StringComparison.Ordinal))
        {
            return "The service kept sending chunks for an artifact after lastChunk: true. The chunks were applied in order and never de-duplicated; the artifact content may be longer than the service intended.";
        }

        if (code.StartsWith("artifact.", StringComparison.Ordinal))
        {
            return "An artifact in the stream deviates from A2A 1.0 (missing or duplicated artifactId). Nothing was merged by guess; the raw capture holds the exact frames.";
        }

        if (code.StartsWith("message.", StringComparison.Ordinal))
        {
            return "A direct message arrived where exactly one (or none, in a task stream) was expected, or it carries an unexpected role or id. The first message is the reply; later ones are retained separately and never merged into it.";
        }

        if (code.StartsWith("part.", StringComparison.Ordinal))
        {
            return "A part did not follow the one-of text | raw | url | data rule. Content was mapped by the SDK's content case; the raw capture holds the original part.";
        }

        if (code.StartsWith("task.", StringComparison.Ordinal) || code.StartsWith("status.", StringComparison.Ordinal))
        {
            return "The task or status deviates from the expected identity or state vocabulary (unspecified state, missing or changed id). An unknown state never counts as completed.";
        }

        if (code.StartsWith("frame.", StringComparison.Ordinal))
        {
            return "A stream frame violated the A2A 1.0 shape and was not applied; the run is marked interrupted so it cannot pass silently. The raw capture holds the exact bytes.";
        }

        return "The stream deviates from A2A 1.0 in a way this provider records but does not repair; the raw capture holds the exact bytes.";
    }

    private void TraceStateChange(WorkIqTaskState previous)
    {
        if (previous == _state)
        {
            return;
        }

        Trace("reduce.state_changed", $"task state {previous} → {_state} ({_rawState ?? "no raw state"})", WorkIqTraceLevel.Info, new StateChangeData(previous.ToString(), _state.ToString(), IsTerminal(_state)));
    }

    private void Trace(
        string code,
        string message,
        WorkIqTraceLevel level,
        object? data,
        string? taskIdOverride = null,
        string? messageId = null,
        int? httpStatus = null,
        int? rpcErrorCode = null,
        string? requestId = null)
    {
        if (_diagnostics.IsNone)
        {
            return;
        }

        _diagnostics.Trace(new WorkIqTraceEvent(default, WorkIqTraceStage.Reduce, level, code, message)
        {
            CaseId = _caseId,
            TurnId = _turnId,
            TaskId = taskIdOverride ?? _handle?.TaskId,
            ContextId = _handle?.ContextId,
            MessageId = messageId,
            A2AState = _handle is null ? null : _rawState,
            HttpStatus = httpStatus,
            RpcErrorCode = rpcErrorCode,
            RequestId = requestId,
            Data = data is null ? null : JsonSerializer.SerializeToElement(data, data.GetType(), WorkIqDiagnosticsJson.Options),
        });
    }

    private void Issue(WorkIqIssueCategory category, string code, string message, string suggestedCause)
    {
        if (_diagnostics.IsNone)
        {
            return;
        }

        _diagnostics.Issue(
            new WorkIqIssue(default, WorkIqTraceStage.Reduce, category, code, message, suggestedCause)
            {
                CaseId = _caseId,
                TurnId = _turnId,
                TaskId = _handle?.TaskId,
                ContextId = _handle?.ContextId,
                A2AState = _handle is null ? null : _rawState,
            },
            WorkIqTraceLevel.Warning);
    }

    private static bool IsTerminal(WorkIqTaskState state) =>
        state is WorkIqTaskState.Completed or WorkIqTaskState.Failed or WorkIqTaskState.Canceled or WorkIqTaskState.Rejected;

    /// <summary>The resumable states: the service ends the stream and waits for input on the same task (mirrors <see cref="WorkIqTaskSnapshot.IsSuspended"/>).</summary>
    private static bool IsSuspended(WorkIqTaskState state) =>
        state is WorkIqTaskState.InputRequired or WorkIqTaskState.AuthRequired;

    /// <summary>The mutable per-artifact accumulator; <see cref="ToArtifact"/> projects it into the immutable model.</summary>
    private sealed class ArtifactEntry
    {
        private ArtifactEntry(string artifactId)
        {
            ArtifactId = artifactId;
        }

        public string ArtifactId { get; }

        public string? Name { get; set; }

        public string? Description { get; set; }

        public JsonElement? Metadata { get; set; }

        public List<WorkIqPart> Parts { get; } = [];

        public int Revision { get; set; }

        public bool IsClosed { get; set; }

        public static ArtifactEntry FromSnapshot(WorkIqArtifact mapped, int revision, bool isClosed)
        {
            var entry = new ArtifactEntry(mapped.ArtifactId)
            {
                Name = mapped.Name,
                Description = mapped.Description,
                Metadata = mapped.Metadata,
                Revision = revision,
                IsClosed = isClosed,
            };
            entry.Parts.AddRange(mapped.Parts);
            return entry;
        }

        public WorkIqArtifact ToArtifact() =>
            new(ArtifactId, Name, Description, Parts.ToImmutableArray(), Revision, IsClosed, Metadata);

        /// <summary>Whether <paramref name="other"/> carries the same name, description, metadata and parts (by value).</summary>
        public bool ContentEquals(WorkIqArtifact other)
        {
            if (!string.Equals(Name, other.Name, StringComparison.Ordinal)
                || !string.Equals(Description, other.Description, StringComparison.Ordinal)
                || !JsonEquals(Metadata, other.Metadata)
                || Parts.Count != other.Parts.Length)
            {
                return false;
            }

            for (var i = 0; i < Parts.Count; i++)
            {
                if (!PartEquals(Parts[i], other.Parts[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool PartEquals(WorkIqPart a, WorkIqPart b) =>
            a.Kind == b.Kind
            && string.Equals(a.Text, b.Text, StringComparison.Ordinal)
            && string.Equals(a.MediaType, b.MediaType, StringComparison.Ordinal)
            && string.Equals(a.Filename, b.Filename, StringComparison.Ordinal)
            && string.Equals(a.Url, b.Url, StringComparison.Ordinal)
            && a.RawBytes.AsSpan().SequenceEqual(b.RawBytes.AsSpan())
            && JsonEquals(a.Data, b.Data)
            && JsonEquals(a.Metadata, b.Metadata);

        private static bool JsonEquals(JsonElement? a, JsonElement? b)
        {
            if (a is null || b is null)
            {
                return a is null && b is null;
            }

            return string.Equals(a.Value.GetRawText(), b.Value.GetRawText(), StringComparison.Ordinal);
        }
    }

    private sealed record SnapshotData(string Source, bool Replaced, int Artifacts, int Added, int Changed, int Dropped, int History, bool HasStatusMessage, long? Sequence, int? Epoch);

    private sealed record StatusData(string PreviousState, string State, bool HasMessage, int MessageParts, bool HasMetadata, DateTimeOffset? Timestamp, long Sequence, int Epoch);

    private sealed record ArtifactData(string ArtifactId, string? Name, bool Append, bool LastChunk, bool Created, int Revision, int PartsInUpdate, int PartsTotal, long Sequence, int Epoch);

    private sealed record MessageData(int Parts, bool HasMetadata, string? TaskId, string? ContextId, long Sequence, int Epoch);

    private sealed record SkippedData(string? TaskId, string? ContextId, long Sequence, int Epoch);

    private sealed record FailureData(string Category, string Operation, string Certainty, long Sequence, int Epoch);

    private sealed record InterruptedData(string Reason, string State, bool Terminal, bool Suspended, bool HasDirectMessage, int FramesSeen, int FramesApplied);

    private sealed record StreamEndData(int FramesSeen, int FramesApplied, bool HasDirectMessage, bool Terminal, bool Suspended, int Artifacts);

    private sealed record StateChangeData(string Previous, string State, bool Terminal);
}
