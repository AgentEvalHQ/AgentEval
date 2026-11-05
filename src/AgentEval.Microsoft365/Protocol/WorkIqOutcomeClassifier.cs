// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using A2A;

namespace AgentEval.Microsoft365.Protocol;

/// <summary>
/// The deterministic outcome rules of one turn (M14): the raw A2A state string or SDK enum → <see cref="WorkIqTaskState"/>
/// (strict wire names; anything else is <see cref="WorkIqTaskState.Unknown"/>, which never implies completion);
/// task state + direct message + transport + failure → (<see cref="WorkIqTurnOutcome"/>, <see cref="OutcomeCertainty"/>);
/// the answer completeness given the outcome and the answer selection; and the telemetry completeness given the
/// diagnostic profile. Precedence in <see cref="Classify"/>: an observed terminal or suspended state is authoritative,
/// then a direct message, then the transport and the structured failure; a non-terminal or unknown state with
/// nothing more is <see cref="WorkIqTurnOutcome.Indeterminate"/>. A local stop is never
/// <see cref="WorkIqTurnOutcome.Canceled"/> (only the service's <c>TASK_STATE_CANCELED</c> is). Owned by lane B.
/// </summary>
internal static class WorkIqOutcomeClassifier
{
    /// <summary>The prefix of every A2A 1.0 wire state name.</summary>
    public const string RawStatePrefix = "TASK_STATE_";

    /// <summary>
    /// Maps a raw <c>status.state</c> string to the provider's state: exactly the A2A 1.0 wire names
    /// (<c>TASK_STATE_SUBMITTED</c>…); <c>TASK_STATE_UNSPECIFIED</c>, null, a 0.3-style lower-case name or anything
    /// else is <see cref="WorkIqTaskState.Unknown"/> so a shape deviation surfaces instead of being normalised away.
    /// </summary>
    /// <param name="rawState">The raw state string.</param>
    public static WorkIqTaskState ToTaskState(string? rawState) => rawState switch
    {
        "TASK_STATE_SUBMITTED" => WorkIqTaskState.Submitted,
        "TASK_STATE_WORKING" => WorkIqTaskState.Working,
        "TASK_STATE_COMPLETED" => WorkIqTaskState.Completed,
        "TASK_STATE_FAILED" => WorkIqTaskState.Failed,
        "TASK_STATE_CANCELED" => WorkIqTaskState.Canceled,
        "TASK_STATE_REJECTED" => WorkIqTaskState.Rejected,
        "TASK_STATE_INPUT_REQUIRED" => WorkIqTaskState.InputRequired,
        "TASK_STATE_AUTH_REQUIRED" => WorkIqTaskState.AuthRequired,
        _ => WorkIqTaskState.Unknown,
    };

    /// <summary>Maps the SDK enum to the provider's state; <see cref="TaskState.Unspecified"/> (and any future value) is <see cref="WorkIqTaskState.Unknown"/>.</summary>
    /// <param name="state">The SDK state.</param>
    public static WorkIqTaskState ToTaskState(TaskState state) => state switch
    {
        TaskState.Submitted => WorkIqTaskState.Submitted,
        TaskState.Working => WorkIqTaskState.Working,
        TaskState.Completed => WorkIqTaskState.Completed,
        TaskState.Failed => WorkIqTaskState.Failed,
        TaskState.Canceled => WorkIqTaskState.Canceled,
        TaskState.Rejected => WorkIqTaskState.Rejected,
        TaskState.InputRequired => WorkIqTaskState.InputRequired,
        TaskState.AuthRequired => WorkIqTaskState.AuthRequired,
        _ => WorkIqTaskState.Unknown,
    };

    /// <summary>The A2A 1.0 wire name of a provider state; null for <see cref="WorkIqTaskState.Unknown"/> (there is no honest raw name for it).</summary>
    /// <param name="state">The provider state.</param>
    public static string? ToRawState(WorkIqTaskState state) => state switch
    {
        WorkIqTaskState.Submitted => "TASK_STATE_SUBMITTED",
        WorkIqTaskState.Working => "TASK_STATE_WORKING",
        WorkIqTaskState.Completed => "TASK_STATE_COMPLETED",
        WorkIqTaskState.Failed => "TASK_STATE_FAILED",
        WorkIqTaskState.Canceled => "TASK_STATE_CANCELED",
        WorkIqTaskState.Rejected => "TASK_STATE_REJECTED",
        WorkIqTaskState.InputRequired => "TASK_STATE_INPUT_REQUIRED",
        WorkIqTaskState.AuthRequired => "TASK_STATE_AUTH_REQUIRED",
        _ => null,
    };

    /// <summary>
    /// Decides the turn outcome and its certainty:
    /// <list type="bullet">
    /// <item><c>Completed</c> state, or a direct message reply → <see cref="WorkIqTurnOutcome.Completed"/>, <see cref="OutcomeCertainty.Known"/>.</item>
    /// <item><c>Failed</c> / <c>Canceled</c> / <c>Rejected</c> / <c>InputRequired</c> / <c>AuthRequired</c> → the matching outcome, <see cref="OutcomeCertainty.Known"/> (the state was observed; the transport may still have been recovered or interrupted after it).</item>
    /// <item>A failed transport before any task id with a failure whose certainty is <see cref="OutcomeCertainty.Known"/> (the service answered: 401, 403, 404, 429, a JSON-RPC error…) → <see cref="WorkIqTurnOutcome.Failed"/>, <see cref="OutcomeCertainty.Known"/>: no task was created.</item>
    /// <item>A failed or interrupted transport otherwise (timeout, drop, a failure after the task id with a non-terminal last state) → <see cref="WorkIqTurnOutcome.Indeterminate"/>, <see cref="OutcomeCertainty.Indeterminate"/>; the input must not be resent.</item>
    /// <item>A local cancellation (<see cref="WorkIqFailureCategory.Canceled"/>) → <see cref="WorkIqTurnOutcome.Indeterminate"/>, <see cref="OutcomeCertainty.Indeterminate"/>: a local stop is never a remote cancel.</item>
    /// <item><c>Submitted</c> / <c>Working</c> / <see cref="WorkIqTaskState.Unknown"/> / no state with nothing more → <see cref="WorkIqTurnOutcome.Indeterminate"/>, <see cref="OutcomeCertainty.Indeterminate"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="state">The last observed task state; null when no task was ever observed.</param>
    /// <param name="hasDirectMessage">Whether the service replied with a message instead of a task.</param>
    /// <param name="transport">What happened to the transport.</param>
    /// <param name="failure">The structured failure, when the operation did not complete locally.</param>
    /// <param name="taskIdKnown">Whether a task id was received before the transport ended.</param>
    public static (WorkIqTurnOutcome Outcome, OutcomeCertainty Certainty) Classify(
        WorkIqTaskState? state,
        bool hasDirectMessage,
        TransportOutcome transport,
        WorkIqFailure? failure,
        bool taskIdKnown)
    {
        switch (state)
        {
            case WorkIqTaskState.Completed:
                return (WorkIqTurnOutcome.Completed, OutcomeCertainty.Known);
            case WorkIqTaskState.Failed:
                return (WorkIqTurnOutcome.Failed, OutcomeCertainty.Known);
            case WorkIqTaskState.Canceled:
                return (WorkIqTurnOutcome.Canceled, OutcomeCertainty.Known);
            case WorkIqTaskState.Rejected:
                return (WorkIqTurnOutcome.Rejected, OutcomeCertainty.Known);
            case WorkIqTaskState.InputRequired:
                return (WorkIqTurnOutcome.InputRequired, OutcomeCertainty.Known);
            case WorkIqTaskState.AuthRequired:
                return (WorkIqTurnOutcome.AuthRequired, OutcomeCertainty.Known);
        }

        if (hasDirectMessage)
        {
            return (WorkIqTurnOutcome.Completed, OutcomeCertainty.Known);
        }

        // No terminal or suspended state and no direct message: Submitted, Working, Unknown or nothing at all.
        if (transport == TransportOutcome.Failed
            && !taskIdKnown
            && failure is { Certainty: OutcomeCertainty.Known }
            && failure.Category != WorkIqFailureCategory.Canceled)
        {
            // The service answered the send itself with an error: no task exists, the remote state is known.
            return (WorkIqTurnOutcome.Failed, OutcomeCertainty.Known);
        }

        return (WorkIqTurnOutcome.Indeterminate, OutcomeCertainty.Indeterminate);
    }

    /// <summary>
    /// The answer completeness of a turn: on <see cref="WorkIqTurnOutcome.Completed"/> the selection's own
    /// completeness (no selection → <see cref="OutputCompleteness.None"/>); on any other outcome text that was
    /// received is <see cref="OutputCompleteness.Partial"/> (never an answer) and an empty or absent selection is
    /// <see cref="OutputCompleteness.None"/>.
    /// </summary>
    /// <param name="outcome">The turn outcome.</param>
    /// <param name="selection">The answer selection, when one ran.</param>
    public static OutputCompleteness AnswerCompletenessFor(WorkIqTurnOutcome outcome, WorkIqAnswerSelection? selection)
    {
        if (outcome == WorkIqTurnOutcome.Completed)
        {
            return selection?.Completeness ?? OutputCompleteness.None;
        }

        return selection?.Completeness switch
        {
            OutputCompleteness.Complete or OutputCompleteness.Partial or OutputCompleteness.Ambiguous => OutputCompleteness.Partial,
            _ => OutputCompleteness.None,
        };
    }

    /// <summary>
    /// The telemetry completeness of a turn: <see cref="TelemetryCompleteness.NotRequested"/> under
    /// <see cref="DiagnosticProfile.PublicAnswerOnly"/>; otherwise <see cref="TelemetryCompleteness.Partial"/> when
    /// any diagnostic channel arrived and <see cref="TelemetryCompleteness.Unknown"/> when none did. Never
    /// <see cref="TelemetryCompleteness.Complete"/> in v1: the provider declares no completeness.
    /// </summary>
    /// <param name="profile">The diagnostic profile of the turn.</param>
    /// <param name="diagnosticsObserved">Whether at least one diagnostic channel was received.</param>
    public static TelemetryCompleteness TelemetryFor(DiagnosticProfile profile, bool diagnosticsObserved)
    {
        if (profile == DiagnosticProfile.PublicAnswerOnly)
        {
            return TelemetryCompleteness.NotRequested;
        }

        return diagnosticsObserved ? TelemetryCompleteness.Partial : TelemetryCompleteness.Unknown;
    }
}
