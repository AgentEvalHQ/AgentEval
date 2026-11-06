// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365;

/// <summary>The classified cause of a failed or indeterminate operation.</summary>
public enum WorkIqFailureCategory
{
    /// <summary>The response has the A2A 0.3 shape (<c>kind</c> discriminators) or the service rejected version 1.0 (<c>-32009</c>); never silently downgraded.</summary>
    ProtocolVersionMismatch,

    /// <summary>HTTP 401: the token was missing, expired or rejected; the hint is a device-code sign-in, never a resend.</summary>
    Unauthorized,

    /// <summary>HTTP 403: the principal lacks the delegated permission or admin consent.</summary>
    Forbidden,

    /// <summary>HTTP 429 (or 503 with <c>Retry-After</c>): backed off only for safe reads, never for a send.</summary>
    RateLimited,

    /// <summary>HTTP 404 or A2A task-not-found; a task "not found" can mean inaccessible, expired or purged, not nonexistent.</summary>
    NotFound,

    /// <summary>The card or an advertised interface points at a host outside the trust policy; refused before any token use.</summary>
    UntrustedHost,

    /// <summary>A JSON-RPC error object or a terminal remote failure (<c>failed</c>/<c>rejected</c>) reported by the service.</summary>
    RemoteError,

    /// <summary>A connection, DNS, TLS or socket failure.</summary>
    Transport,

    /// <summary>A local timeout (send timeout, turn deadline or recovery budget).</summary>
    Timeout,

    /// <summary>The payload could not be parsed or violated the one-of/identity rules.</summary>
    Malformed,

    /// <summary>The caller's token was cancelled while waiting locally.</summary>
    Canceled,

    /// <summary>Anything else; the safe message names what is known.</summary>
    Unknown,
}

/// <summary>Whether the remote side's state is known for an operation that did not complete locally.</summary>
public enum OutcomeCertainty
{
    /// <summary>The remote state is known (a reply, a snapshot or a confirmed terminal state was observed).</summary>
    Known,

    /// <summary>The remote state is not known (for example a timeout before a task id arrived); the input must not be resent automatically.</summary>
    Indeterminate,
}

/// <summary>The protocol operation (or local phase) in which a failure occurred.</summary>
public enum WorkIqOperation
{
    /// <summary>Not attributable to one operation.</summary>
    Unknown,

    /// <summary>Agent-card discovery.</summary>
    Discover,

    /// <summary><c>SendMessage</c>.</summary>
    Send,

    /// <summary><c>SendStreamingMessage</c>.</summary>
    Stream,

    /// <summary><c>GetTask</c>.</summary>
    GetTask,

    /// <summary><c>CancelTask</c>.</summary>
    CancelTask,

    /// <summary><c>SubscribeToTask</c>.</summary>
    Subscribe,

    /// <summary>Local reduction, normalisation or policy (no request involved).</summary>
    Local,
}

/// <summary>Why no bearer token was available for a request, so nothing was sent (<see cref="WorkIqFailure.TokenFailure"/>).</summary>
public enum WorkIqTokenFailure
{
    /// <summary>The wait for the token (a device-code sign-in included) was stopped before a token arrived: by the caller, an abandonment or the turn deadline.</summary>
    SignInNotCompleted,

    /// <summary>The token provider failed (an MSAL error, a refused consent, an expired device code, or another exception).</summary>
    ProviderFailed,

    /// <summary>The token provider returned an empty token.</summary>
    EmptyToken,
}

/// <summary>
/// A structured, safe description of why an operation failed or ended indeterminate. The message never contains
/// tenant content, prompts, answers, tokens or auth URLs — only categories, codes and correlation ids; detailed
/// content lives in the raw capture under the content policy.
/// </summary>
/// <param name="Category">The classified cause.</param>
/// <param name="SafeMessage">A human-readable summary without tenant content.</param>
/// <param name="Certainty">Whether the remote state is known.</param>
/// <param name="Operation">The operation or local phase that failed.</param>
/// <param name="HttpStatus">The HTTP status code, when a response was received.</param>
/// <param name="RpcErrorCode">The JSON-RPC error code, when an error object was received.</param>
/// <param name="RemoteTaskState">The raw remote task state, when known.</param>
/// <param name="RequestId">The service's <c>request-id</c> response header, when received.</param>
/// <param name="RpcRequestId">The JSON-RPC id of the failed request, when one was sent.</param>
/// <param name="TaskId">The task id, when known.</param>
/// <param name="ContextId">The context id, when known.</param>
/// <param name="Capture">The raw capture of the failed exchange, when retained.</param>
public sealed record WorkIqFailure(
    WorkIqFailureCategory Category,
    string SafeMessage,
    OutcomeCertainty Certainty,
    WorkIqOperation Operation = WorkIqOperation.Unknown,
    int? HttpStatus = null,
    int? RpcErrorCode = null,
    string? RemoteTaskState = null,
    string? RequestId = null,
    string? RpcRequestId = null,
    string? TaskId = null,
    string? ContextId = null,
    RawCaptureRef? Capture = null)
{
    /// <summary>
    /// Set when no bearer token was available, so nothing was sent: why. Null for every other failure. The auth layer
    /// sets it and registers the issue for such a failure itself, because only it holds the sign-in or MSAL error.
    /// </summary>
    public WorkIqTokenFailure? TokenFailure { get; init; }

    /// <summary>
    /// Set on a <see cref="WorkIqFailureCategory.Timeout"/> that a local budget of the turn ended (the turn deadline or
    /// the recovery budget), not the transport: which one. Null for every other failure.
    /// </summary>
    internal WorkIqLocalBudget? LocalBudget { get; init; }
}

/// <summary>The local budget that ended a turn (<see cref="WorkIqFailure.LocalBudget"/>).</summary>
internal enum WorkIqLocalBudget
{
    /// <summary><see cref="WorkIqOptions.TurnDeadline"/> elapsed.</summary>
    TurnDeadline,

    /// <summary><see cref="WorkIqOptions.RecoveryBudget"/> ran out while the task was still submitted or working.</summary>
    RecoveryBudget,
}
