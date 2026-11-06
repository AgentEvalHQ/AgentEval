// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// The stage of the Work IQ pipeline a trace event or issue belongs to. An external tester reads the stage first:
/// it says <i>where</i> a run stopped before the code says <i>why</i>.
/// </summary>
public enum WorkIqTraceStage
{
    /// <summary>Config loading, validation, option resolution and the diagnostics setup itself.</summary>
    Config,

    /// <summary>Token acquisition (silent, device code) and the <c>Authorization</c> decision of the auth handler.</summary>
    Auth,

    /// <summary>Agent-card discovery, interface selection and the trust policy.</summary>
    Discovery,

    /// <summary>Blocking <c>SendMessage</c>.</summary>
    Send,

    /// <summary><c>SendStreamingMessage</c> and the frames of its stream.</summary>
    Stream,

    /// <summary><c>GetTask</c> (reconciliation and polling).</summary>
    GetTask,

    /// <summary><c>CancelTask</c>.</summary>
    Cancel,

    /// <summary><c>SubscribeToTask</c>.</summary>
    Subscribe,

    /// <summary>Local task/stream reduction (artifact assembly, state tracking).</summary>
    Reduce,

    /// <summary>Evidence normalisation and decoding.</summary>
    Normalize,

    /// <summary>The turn outcome decision (completion gate, exceptions raised).</summary>
    Outcome,

    /// <summary>Turn budget and deadline accounting.</summary>
    Budget,

    /// <summary>Raw capture (teeing, bounds, file output).</summary>
    Capture,

    /// <summary>The <c>agenteval m365 doctor</c> readiness stages.</summary>
    Doctor,
}

/// <summary>The severity of a trace event.</summary>
public enum WorkIqTraceLevel
{
    /// <summary>Normal progress.</summary>
    Info,

    /// <summary>Something unexpected that did not stop the operation (also registered as an issue by the emitter).</summary>
    Warning,

    /// <summary>The operation failed or produced no usable result (also registered as an issue by the emitter).</summary>
    Error,
}

/// <summary>The category of a registered issue, chosen to point the tester at the person or setting that can fix it.</summary>
public enum WorkIqIssueCategory
{
    /// <summary>The config file or the options (missing field, bad value, unwritable path).</summary>
    Config,

    /// <summary>Sign-in and tokens (MSAL failure, AADSTS code, HTTP 401).</summary>
    Authentication,

    /// <summary>Consent, billing or enablement (HTTP 403: missing admin consent, the user not in a usage-based billing plan, Work IQ not enabled).</summary>
    Permission,

    /// <summary>DNS, TLS, socket, timeout or connection drop.</summary>
    Network,

    /// <summary>The wire contract (A2A 0.3 shape, version not supported, malformed payload, one-of violation).</summary>
    Protocol,

    /// <summary>The service answered with an error (JSON-RPC error, 404, 429, 5xx, a failed/rejected task).</summary>
    Service,

    /// <summary>Raw capture could not be retained as configured (bounds exceeded, file output failed).</summary>
    Capture,

    /// <summary>A local budget or deadline was exceeded (turn cap, recovery budget, turn deadline).</summary>
    Budget,

    /// <summary>Not classified; the message says what is known.</summary>
    Unknown,

    /// <summary>The evaluation case: the agent paused for a confirmation or input that a run never gives. Rephrase the case.</summary>
    Case,
}
