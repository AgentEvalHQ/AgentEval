// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails;

/// <summary>
/// What a gate <em>found</em> about a chat turn (binary). How that finding is <em>enforced</em>
/// (warn / throw / redact) is decided by the <see cref="EvalGatePolicy"/> on the client, not the gate.
/// </summary>
public enum GateAction
{
    /// <summary>Content is acceptable.</summary>
    Allow,

    /// <summary>Content violates the policy. The client enforces this per its <see cref="EvalGatePolicy"/>.</summary>
    Block,
}

/// <summary>A single gate's verdict for one chat turn (Glass Box runtime policy gate).</summary>
/// <param name="Action">What the gate found (<see cref="GateAction.Allow"/> / <see cref="GateAction.Block"/>).</param>
/// <param name="PolicyName">Stable policy name recorded into the trace and exceptions.</param>
/// <param name="Reason">Human-readable reason (for <see cref="GateAction.Block"/>).</param>
/// <param name="RedactedText">A masked replacement the client may apply under <see cref="EvalGatePolicy.Redact"/>; null when the finding is not maskable.</param>
/// <param name="Matches">The offending matches found (e.g. PII categories / injection tokens), if any.</param>
public sealed record GateVerdict(
    GateAction Action,
    string PolicyName,
    string? Reason = null,
    string? RedactedText = null,
    IReadOnlyList<string>? Matches = null)
{
    /// <summary>Allow verdict for <paramref name="policy"/>.</summary>
    public static GateVerdict Allow(string policy) => new(GateAction.Allow, policy);

    /// <summary>
    /// Block verdict for <paramref name="policy"/>. Supply <paramref name="redactedText"/> when the finding
    /// is maskable so the client can redact under <see cref="EvalGatePolicy.Redact"/>.
    /// </summary>
    public static GateVerdict Block(string policy, string reason, IReadOnlyList<string>? matches = null, string? redactedText = null)
        => new(GateAction.Block, policy, reason, redactedText, matches);
}
