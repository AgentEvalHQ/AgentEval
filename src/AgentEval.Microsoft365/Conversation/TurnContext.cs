// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// Everything one turn (or subscription) accumulates before its result is built. It also binds the turn's case and
/// turn ids to every trace event and every client call the turn makes.
/// </summary>
internal sealed class TurnContext
{
    private const string UncapturedId = "uncaptured";

    private long _sequence;

    public TurnContext(
        WorkIqTurnRequest request,
        TurnIdentity identity,
        WorkIqOperation operation,
        DateTimeOffset startedAt,
        int epoch,
        CancellationToken abandonToken,
        WorkIqDiagnostics diagnostics)
    {
        Request = request;
        Identity = identity;
        Operation = operation;
        StartedAt = startedAt;
        Epoch = epoch;
        AbandonToken = abandonToken;
        Diagnostics = diagnostics;
    }

    public WorkIqTurnRequest Request { get; }

    public TurnIdentity Identity { get; }

    public WorkIqOperation Operation { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>The conversation's epoch when the turn started. A reset or an abandonment starts a new one.</summary>
    public int Epoch { get; }

    public CancellationToken AbandonToken { get; }

    /// <summary>The diagnostics the turn traces through: the conversation's.</summary>
    public WorkIqDiagnostics Diagnostics { get; }

    /// <summary>The <c>&lt;runId&gt;/&lt;turnId&gt;</c> subfolder this turn's captures are written under.</summary>
    public string? CaptureScope { get; init; }

    public RawAgentCard? Card { get; set; }

    public DateTimeOffset? FirstEventAt { get; set; }

    public DateTimeOffset? FirstAnswerAt { get; set; }

    public DateTimeOffset? TerminalAt { get; set; }

    public int RecoveryRequests { get; set; }

    public List<RawCaptureRef> Captures { get; } = [];

    public RawCaptureRef? LastCapture { get; private set; }

    /// <summary>The capture the turn's sources point at: the last capture, else the snapshot's, else <c>uncaptured</c>.</summary>
    public string CaptureId => LastCapture?.CaptureId ?? Snapshot?.Capture?.CaptureId ?? UncapturedId;

    public WorkIqTaskReducer? Reducer { get; set; }

    public WorkIqTaskSnapshot? Snapshot { get; set; }

    public WorkIqMessage? DirectMessage { get; set; }

    public WorkIqTaskHandle? Handle { get; set; }

    public string? ContextId { get; set; }

    public string? SentContextId { get; set; }

    /// <summary>Why the turn did not complete, when it did not. Set through <see cref="Fail"/> or <see cref="SetTerminalFailure"/>.</summary>
    public WorkIqFailure? Failure { get; private set; }

    /// <summary>How the transport ended. Set through <see cref="Fail"/> or <see cref="MarkRecovered"/>.</summary>
    public TransportOutcome Transport { get; private set; } = TransportOutcome.Completed;

    public bool CanceledByCaller { get; set; }

    public bool Abandoned { get; set; }

    public bool DeadlineFired { get; set; }

    public bool Stopped => CanceledByCaller || Abandoned || DeadlineFired;

    public int SchemaWarningsTraced { get; set; }

    public int SchemaWarningsEmitted { get; set; }

    /// <summary>The capture subfolder of a turn: the run id and the turn id, each made safe as a path segment.</summary>
    /// <param name="runId">The run id.</param>
    /// <param name="turnId">The turn id.</param>
    public static string CaptureScopeFor(string runId, string turnId) =>
        SanitizePathSegment(runId) + "/" + SanitizePathSegment(turnId);

    public long NextSequence() => _sequence++;

    /// <summary>Records the turn's failure and how its transport ended.</summary>
    /// <param name="failure">The failure.</param>
    /// <param name="transport">The transport outcome.</param>
    public void Fail(WorkIqFailure failure, TransportOutcome transport)
    {
        Failure = failure;
        Transport = transport;
    }

    /// <summary>Records the failure a final task state implies (none for a completed or paused task). The transport keeps its value.</summary>
    /// <param name="failure">The failure, or null.</param>
    public void SetTerminalFailure(WorkIqFailure? failure) => Failure = failure;

    /// <summary>Records that a read after a dropped or unfinished operation reached the task's final state.</summary>
    public void MarkRecovered() => Transport = TransportOutcome.Recovered;

    /// <summary>Adds a capture once (by reference or id) and makes it the last capture. A null capture is ignored.</summary>
    /// <param name="capture">The capture, or null.</param>
    public void AddCapture(RawCaptureRef? capture)
    {
        if (capture is null)
        {
            return;
        }

        if (!Captures.Any(c => ReferenceEquals(c, capture) || string.Equals(c.CaptureId, capture.CaptureId, StringComparison.Ordinal)))
        {
            Captures.Add(capture);
        }

        LastCapture = capture;
    }

    /// <summary>
    /// Attaches the capture of the stream that just ended to the turn and its reducer. The client hands it back on
    /// the call. A snapshot without a capture gets this one.
    /// </summary>
    /// <param name="call">The stream's call context.</param>
    public void AttachStreamCapture(WorkIqCallContext call)
    {
        if (call.StreamCapture is not { } capture)
        {
            return;
        }

        AddCapture(capture);
        Reducer?.AttachCapture(capture);
        if (Snapshot is { Capture: null } snapshot)
        {
            Snapshot = snapshot with { Capture = capture };
        }
    }

    /// <summary>
    /// The per-call context of one client call the turn makes: the turn's capture folder, case and turn ids. The turn
    /// registers the one issue of its outcome itself, so the client only traces a failure.
    /// </summary>
    /// <param name="rpcId">The JSON-RPC id the call sends; null lets the client mint one.</param>
    public WorkIqCallContext Call(string? rpcId = null) => new()
    {
        RpcId = rpcId,
        CaptureScope = CaptureScope,
        CaseId = Request.CaseId,
        TurnId = Request.TurnId,
        CallerRegistersIssues = true,
    };

    /// <summary>Traces an event of this turn. The case and turn ids are filled in; every other id is the caller's.</summary>
    /// <param name="stage">The stage.</param>
    /// <param name="code">The stable event code.</param>
    /// <param name="message">The message.</param>
    /// <param name="level">The level.</param>
    /// <param name="taskId">The task id, when any.</param>
    /// <param name="contextId">The context id, when any.</param>
    /// <param name="requestId">The service's request id, when any.</param>
    /// <param name="httpStatus">The HTTP status, when any.</param>
    /// <param name="rpcErrorCode">The JSON-RPC error code, when any.</param>
    /// <param name="a2aState">The raw A2A task state, when any.</param>
    public void Trace(
        WorkIqTraceStage stage,
        string code,
        string message,
        WorkIqTraceLevel level = WorkIqTraceLevel.Info,
        string? taskId = null,
        string? contextId = null,
        string? requestId = null,
        int? httpStatus = null,
        int? rpcErrorCode = null,
        string? a2aState = null) =>
        Diagnostics.Trace(stage, code, message, level, Request.CaseId, Request.TurnId, taskId, contextId, requestId, httpStatus, rpcErrorCode, a2aState);

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 || c == '/' || c == '\\' ? '_' : c);
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }
}
