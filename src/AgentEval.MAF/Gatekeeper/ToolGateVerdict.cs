// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// A single tool gate's finding for one call. Reuses the shipped <see cref="GateAction"/> (Allow / Block) so
/// tool-gate evidence carries the exact same <c>action</c> token the Glass Box evidence reader already counts.
/// (M1 extends the action set with Mutate / Terminate.)
/// </summary>
/// <param name="Action">What the gate found.</param>
/// <param name="PolicyName">Stable policy name recorded into the trace.</param>
/// <param name="Reason">Human-readable reason (for <see cref="GateAction.Block"/>).</param>
public sealed record ToolGateVerdict(GateAction Action, string PolicyName, string? Reason = null)
{
    /// <summary>Allow verdict for <paramref name="policy"/>.</summary>
    public static ToolGateVerdict Allow(string policy) => new(GateAction.Allow, policy);

    /// <summary>Block verdict for <paramref name="policy"/> with a reason surfaced to the model.</summary>
    public static ToolGateVerdict Block(string policy, string reason) => new(GateAction.Block, policy, reason);
}
