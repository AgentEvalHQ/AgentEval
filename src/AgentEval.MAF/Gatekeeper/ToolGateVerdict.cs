// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// A single tool gate's finding for one call. <see cref="Action"/> is the finding; <see cref="NewArguments"/>
/// is populated only for <see cref="ToolGateAction.Mutate"/>.
/// </summary>
/// <param name="Action">What the gate found.</param>
/// <param name="PolicyName">Stable policy name recorded into the trace.</param>
/// <param name="Reason">Human-readable reason (for Block / Mutate).</param>
/// <param name="NewArguments">Replacement arguments (Mutate only).</param>
public sealed record ToolGateVerdict(
    ToolGateAction Action,
    string PolicyName,
    string? Reason = null,
    IReadOnlyDictionary<string, object?>? NewArguments = null)
{
    /// <summary>Allow verdict for <paramref name="policy"/>.</summary>
    public static ToolGateVerdict Allow(string policy) => new(ToolGateAction.Allow, policy);

    /// <summary>Block verdict for <paramref name="policy"/> with a reason surfaced to the model.</summary>
    public static ToolGateVerdict Block(string policy, string reason) => new(ToolGateAction.Block, policy, reason);

    /// <summary>Mutate verdict: rewrite the call's arguments to <paramref name="newArguments"/> and proceed.</summary>
    public static ToolGateVerdict Mutate(string policy, IReadOnlyDictionary<string, object?> newArguments, string reason)
        => new(ToolGateAction.Mutate, policy, reason, newArguments);
}
