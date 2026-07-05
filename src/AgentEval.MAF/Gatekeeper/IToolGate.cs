// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Stage 2, M0) — a runtime policy over a single live tool call at the MAF function-invocation
/// boundary. A gate only <em>finds</em> (Allow / Block); how a block is <em>enforced</em> is decided by the
/// <see cref="ToolGatePolicy"/> passed to <c>UseAgentEvalToolGate</c>, mirroring the shipped chat-gate split.
/// </summary>
public interface IToolGate
{
    /// <summary>Stable policy name, recorded into <c>gate.tool.*</c> trace evidence.</summary>
    string PolicyName { get; }

    /// <summary>
    /// How expensive this gate is. Only <see cref="GateCost.PureCode"/> / <see cref="GateCost.Bounded"/> gates
    /// may run inline; <c>UseAgentEvalToolGate</c> rejects <see cref="GateCost.Network"/> / <see cref="GateCost.Llm"/>
    /// at construction (those belong in shadow mode).
    /// </summary>
    GateCost Cost { get; }

    /// <summary>Inspect one tool call and return a verdict. Deterministic gates should complete synchronously.</summary>
    ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default);

    /// <summary>
    /// The weakest <see cref="ToolGatePolicy"/> under which this gate is safe to run. Defaults to
    /// <see cref="ToolGatePolicy.WarnOnly"/> (observe-only is fine). A gate whose whole purpose is to STOP an
    /// action — e.g. a honeypot canary gate — overrides this to at least <see cref="ToolGatePolicy.ReplaceResult"/>
    /// so <c>UseAgentEvalToolGate</c> refuses to silently downgrade it to observe-only (which would let the
    /// forbidden action run). Enforcement strength: WarnOnly &lt; ReplaceResult &lt; Terminate.
    /// </summary>
    ToolGatePolicy MinimumPolicy => ToolGatePolicy.WarnOnly;
}
