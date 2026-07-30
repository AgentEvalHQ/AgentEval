// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Run-pre session gate that refuses admission when the host's bounded identity/session target set contains an
/// Active or Indeterminate target.
/// </summary>
public sealed class ContainedIdentityGate : SessionContextGate
{
    private readonly IContainmentStore _store;
    private readonly Func<AgentSession, IReadOnlyList<ContainmentTarget>> _sessionTargets;

    /// <summary>Creates the gate over an exact caller-owned target resolver and containment store.</summary>
    public ContainedIdentityGate(
        IContainmentStore store,
        Func<AgentSession, IReadOnlyList<ContainmentTarget>> sessionTargets)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessionTargets = sessionTargets ?? throw new ArgumentNullException(nameof(sessionTargets));
    }

    /// <inheritdoc/>
    public override string PolicyName => "ContainedIdentityGate";

    /// <inheritdoc/>
    protected override ValueTask<GateVerdict> CheckSessionAsync(
        AgentSession session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ContainmentGateEvaluator.TryResolve(
            _sessionTargets,
            session,
            requireAtLeastOne: true,
            cancellationToken,
            out var targets))
        {
            return Block("target_resolution_failed");
        }

        var decision = ContainmentGateEvaluator.Evaluate(_store, targets, cancellationToken);
        return decision.MustBlock
            ? Block(decision.ReasonCode!)
            : new ValueTask<GateVerdict>(GateVerdict.Allow(PolicyName));
    }

    private ValueTask<GateVerdict> Block(string reasonCode)
        => new(GateVerdict.Block(
            PolicyName,
            $"contained_identity:{reasonCode}"));
}
