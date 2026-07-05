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

    /// <summary>Inspect one tool call and return a verdict. Deterministic gates should complete synchronously.</summary>
    ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default);
}
