// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Phase 1, P0-2) — the single, explicit enforcement axis for <c>UseGatekeeper</c>. Required at
/// every call site (no default) — this is the fix for the review's sharpest finding: "Gatekeeper" as a name
/// strongly implies enforcement, but the previous default (<see cref="ToolGatePolicy.WarnOnly"/> /
/// <see cref="EvalGatePolicy.WarnOnly"/>) meant a gate's <c>Block</c> verdict was, by default, only logged
/// while the call still ran. There is no default value here — the caller must pick.
/// <para>Internally maps to both <see cref="ToolGatePolicy"/> (tool gates) and <see cref="EvalGatePolicy"/>
/// (run-pre/run-post chat gates) so one enum drives every mechanism <c>UseGatekeeper</c> composes:
/// <see cref="Observe"/> → WarnOnly, <see cref="ReplaceResult"/> → ReplaceResult/Redact,
/// <see cref="Terminate"/> → Terminate/ThrowOnFail.</para>
/// </summary>
public enum GatekeeperEnforcement
{
    /// <summary>
    /// Every gate's finding is recorded into the trace as evidence; nothing is ever blocked. Safe to add to an
    /// existing agent with zero behavior change — the recommended mode for a first rollout, or for a purely
    /// advisory deployment. <c>UseGatekeeper</c> prints a startup banner in this mode so nobody mistakes it for
    /// enforcement (the review's "false assurance" concern).
    /// <para><b>Exception: gates with a <see cref="IToolGate.MinimumPolicy"/> floor above WarnOnly</b> (a
    /// canary/honeypot gate — e.g. <c>CanaryToolGate</c> — where running under WarnOnly would silently defeat
    /// the trap) cannot be composed under Observe at all. <c>UseGatekeeper</c> refuses construction rather than
    /// silently downgrade them — the "zero behavior change, safe to add anywhere" guarantee does not extend to
    /// these gates; register them separately once you're ready to enforce.</para>
    /// </summary>
    Observe,

    /// <summary>
    /// A blocked tool call is replaced with a refusal (the tool never executes) but the run continues; a
    /// blocked run is redacted. Real enforcement, softer than <see cref="Terminate"/> — the model gets a
    /// chance to try something else in the same turn.
    /// </summary>
    ReplaceResult,

    /// <summary>
    /// A blocked tool call is replaced AND stops the function-calling loop; a blocked run throws
    /// (<see cref="EvalGatePolicy.ThrowOnFail"/>). The strongest enforcement level — use for the highest-risk
    /// gates (canaries, forbidden-tool deny-lists) where continuing the run at all is unacceptable.
    /// </summary>
    Terminate,
}
