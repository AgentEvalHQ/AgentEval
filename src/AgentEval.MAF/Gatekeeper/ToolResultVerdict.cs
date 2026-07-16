// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// A single tool RESULT gate's finding for one already-executed call. <see cref="Action"/> is the finding;
/// <see cref="RedactedResult"/> is populated only for <see cref="ToolResultAction.Redact"/> — the sanitized
/// replacement value the model sees instead of the raw result.
/// </summary>
/// <param name="Action">What the gate found.</param>
/// <param name="PolicyName">Stable policy name recorded into the trace.</param>
/// <param name="Reason">Human-readable reason (for Block / Redact) — audit-visible only, never returned to the model (mirrors #12's refusal-message design).</param>
/// <param name="RedactedResult">Replacement result (Redact only).</param>
public sealed record ToolResultVerdict(
    ToolResultAction Action,
    string PolicyName,
    string? Reason = null,
    object? RedactedResult = null)
{
    /// <summary>Allow verdict for <paramref name="policy"/>.</summary>
    public static ToolResultVerdict Allow(string policy) => new(ToolResultAction.Allow, policy);

    /// <summary>Block verdict for <paramref name="policy"/> with an audit-visible reason (never model-visible).</summary>
    public static ToolResultVerdict Block(string policy, string reason) => new(ToolResultAction.Block, policy, reason);

    /// <summary>Redact verdict: replace the result with <paramref name="redactedResult"/> and let it proceed.</summary>
    public static ToolResultVerdict Redact(string policy, object redactedResult, string reason)
        => new(ToolResultAction.Redact, policy, reason, redactedResult);
}
