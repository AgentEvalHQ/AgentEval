// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365;

/// <summary>
/// The operation outcome of one turn. Only <see cref="Completed"/> yields an <c>AgentResponse</c> through
/// <see cref="WorkIqEvaluableAgent"/>; every other value is surfaced as a typed exception carrying the result.
/// </summary>
public enum WorkIqTurnOutcome
{
    /// <summary><c>TASK_STATE_COMPLETED</c> with a valid assembled output, or a direct message reply.</summary>
    Completed,

    /// <summary>The task is suspended waiting for input or approval (<see cref="WorkIqTurnResult.PendingInteraction"/> describes it); nothing was approved.</summary>
    InputRequired,

    /// <summary>The task is suspended waiting for an external authorization workflow; no answer was fabricated.</summary>
    AuthRequired,

    /// <summary>The task ended <c>failed</c>, or the transport failed with the remote state known.</summary>
    Failed,

    /// <summary>The service rejected the task.</summary>
    Rejected,

    /// <summary>The service confirmed cancellation (a local stop alone is never <see cref="Canceled"/>).</summary>
    Canceled,

    /// <summary>The remote state is unknown (timeout or drop before a task id, or recovery exhausted); the input was not resent.</summary>
    Indeterminate,
}

/// <summary>What happened to the transport of the turn, independent of the remote task outcome.</summary>
public enum TransportOutcome
{
    /// <summary>The blocking reply or the stream ended normally.</summary>
    Completed,

    /// <summary>The connection dropped or the local wait stopped before a terminal state, and recovery did not re-establish the state.</summary>
    Interrupted,

    /// <summary>The connection dropped but <c>GetTask</c>/<c>Subscribe</c> re-established the final state (intermediate events may be missing).</summary>
    Recovered,

    /// <summary>The request itself failed (HTTP error, transport error, malformed payload).</summary>
    Failed,
}

/// <summary>Whether a final answer text was assembled, independent of the task outcome.</summary>
public enum OutputCompleteness
{
    /// <summary>One answer artifact (or direct message) was selected and assembled.</summary>
    Complete,

    /// <summary>Text was received but the task did not complete, or a capture bound truncated it.</summary>
    Partial,

    /// <summary>The selected answer is present and genuinely empty (a completed task with no text output; an output-contract matter, not a transport failure).</summary>
    Empty,

    /// <summary>Several candidate answer artifacts exist; none was chosen and all are retained.</summary>
    Ambiguous,

    /// <summary>No answer text exists (nothing selected, or the turn did not complete).</summary>
    None,
}

/// <summary>Whether diagnostic telemetry (tool/retrieval/skill channels) is complete, independent of the answer.</summary>
public enum TelemetryCompleteness
{
    /// <summary>No diagnostic channel was requested (<see cref="DiagnosticProfile.PublicAnswerOnly"/>).</summary>
    NotRequested,

    /// <summary>Requested, but whether all events arrived is unknown (for example a recovered stream).</summary>
    Unknown,

    /// <summary>Requested and some channels are known to be incomplete or malformed.</summary>
    Partial,

    /// <summary>Requested and every channel is complete for its declared scope.</summary>
    Complete,
}

/// <summary>
/// Client-side wall-clock observations of one turn. These are network/adapter timings, not model token
/// generation latency, and never a substitute for provider-reported tool times.
/// </summary>
/// <param name="StartedAt">When the send was dispatched.</param>
/// <param name="FirstEventAt">When the first reply byte/frame was received, when any.</param>
/// <param name="FirstAnswerAt">When the first answer-artifact content was received, when any.</param>
/// <param name="TerminalAt">When a terminal or suspended task state was observed, when any.</param>
/// <param name="EndedAt">When the local operation ended (result built or exception raised).</param>
/// <param name="RecoveryRequests">How many <c>GetTask</c>/<c>Subscribe</c> requests ran during the turn (0 for a single blocking reply).</param>
public sealed record WorkIqTurnTiming(
    DateTimeOffset StartedAt,
    DateTimeOffset? FirstEventAt,
    DateTimeOffset? FirstAnswerAt,
    DateTimeOffset? TerminalAt,
    DateTimeOffset EndedAt,
    int RecoveryRequests)
{
    /// <summary>Total local wall-clock duration of the turn.</summary>
    public TimeSpan Elapsed => EndedAt - StartedAt;
}

/// <summary>
/// The immutable rich result of one turn: four orthogonal outcome dimensions, the task handle, the selected
/// answer text, the evidence document, the pending interaction (when suspended) and the structured failure
/// (when not completed). Carried by <see cref="WorkIqTurnNotCompletedException"/> and
/// <see cref="WorkIqTurnCanceledException"/> so no partial text travels the normal answer path.
/// </summary>
/// <param name="Outcome">The operation outcome.</param>
/// <param name="Transport">What happened to the transport.</param>
/// <param name="AnswerCompleteness">Whether a final answer text was assembled.</param>
/// <param name="Telemetry">Whether diagnostic telemetry is complete.</param>
/// <param name="Task">The task handle; null for a direct-message reply or when no task id was ever received.</param>
/// <param name="ContextId">The server context id, when known.</param>
/// <param name="AnswerText">The selected raw answer text (markers kept); null unless <paramref name="AnswerCompleteness"/> is <see cref="OutputCompleteness.Complete"/> or <see cref="OutputCompleteness.Empty"/>.</param>
/// <param name="Evidence">The evidence document (always present, even for a failed or indeterminate turn).</param>
/// <param name="PendingInteraction">The pending pause when <paramref name="Outcome"/> is <see cref="WorkIqTurnOutcome.InputRequired"/> or <see cref="WorkIqTurnOutcome.AuthRequired"/>.</param>
/// <param name="Failure">The structured failure when the turn did not complete; null on <see cref="WorkIqTurnOutcome.Completed"/>.</param>
/// <param name="Timing">Client-side timings.</param>
/// <param name="Identity">The turn's identities.</param>
public sealed record WorkIqTurnResult(
    WorkIqTurnOutcome Outcome,
    TransportOutcome Transport,
    OutputCompleteness AnswerCompleteness,
    TelemetryCompleteness Telemetry,
    WorkIqTaskHandle? Task,
    string? ContextId,
    string? AnswerText,
    Microsoft365TurnEvidence Evidence,
    WorkIqPendingInteraction? PendingInteraction,
    WorkIqFailure? Failure,
    WorkIqTurnTiming Timing,
    TurnIdentity Identity)
{
    /// <summary><see langword="true"/> only when <see cref="Outcome"/> is <see cref="WorkIqTurnOutcome.Completed"/>.</summary>
    public bool IsCompleted => Outcome == WorkIqTurnOutcome.Completed;
}
