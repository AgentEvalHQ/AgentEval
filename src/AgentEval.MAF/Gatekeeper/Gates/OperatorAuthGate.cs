// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Guardrails;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Fail-closed authorization gate: blocks the run unless the session carries an operator identity (under
/// <see cref="OperatorMetadataKey"/> in the session <c>StateBag</c>, set by the caller) that is on the
/// allow-list. Missing identity ⇒ Block (MAF surfaces no framework identity, so the caller must supply one).
/// </summary>
public sealed class OperatorAuthGate : SessionContextGate
{
    /// <summary>The session <c>StateBag</c> key the caller sets to the operator id/hash (never a raw secret).</summary>
    public const string OperatorMetadataKey = "gatekeeper.operator";

    private readonly HashSet<string> _allowed;

    /// <inheritdoc/>
    public override string PolicyName => "OperatorAuthGate";

    /// <summary>Creates the gate over an allow-list of operator identities.</summary>
    public OperatorAuthGate(params string[] allowedOperators)
    {
        ArgumentNullException.ThrowIfNull(allowedOperators);
        _allowed = new HashSet<string>(allowedOperators, StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    protected override ValueTask<GateVerdict> CheckSessionAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (!session.StateBag.TryGetValue<string>(OperatorMetadataKey, out var op, JsonSerializerOptions.Default)
            || string.IsNullOrEmpty(op))
        {
            return new ValueTask<GateVerdict>(
                GateVerdict.Block(PolicyName, "no operator identity in the session — failing closed"));
        }

        return new ValueTask<GateVerdict>(_allowed.Contains(op)
            ? GateVerdict.Allow(PolicyName)
            : GateVerdict.Block(PolicyName, $"operator '{op}' is not authorized"));
    }
}
