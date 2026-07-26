// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Absolute-first tool gate that blocks every call when any exact session, MCP-server, or agent-endpoint target
/// applicable to the call is actively contained or indeterminate.
/// </summary>
public sealed class ContainmentOverrideGate : IToolGate
{
    private readonly IContainmentStore _store;
    private readonly Func<AgentSession, IReadOnlyList<ContainmentTarget>> _sessionTargets;
    private readonly Func<GatedToolCall, IReadOnlyList<ContainmentTarget>>? _additionalCallTargets;

    /// <summary>Creates a containment override over caller-owned bounded target resolvers.</summary>
    public ContainmentOverrideGate(
        IContainmentStore store,
        Func<AgentSession, IReadOnlyList<ContainmentTarget>> sessionTargets,
        Func<GatedToolCall, IReadOnlyList<ContainmentTarget>>? additionalCallTargets = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessionTargets = sessionTargets ?? throw new ArgumentNullException(nameof(sessionTargets));
        _additionalCallTargets = additionalCallTargets;
    }

    /// <inheritdoc/>
    public string PolicyName => "ContainmentOverrideGate";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.Bounded;

    /// <inheritdoc/>
    public ToolGatePolicy MinimumPolicy => ToolGatePolicy.ReplaceResult;

    /// <inheritdoc/>
    public GateRequirements Requirements => GateRequirements.RunScope;

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(
        GatedToolCall call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();

        var session = AgentRunScope.Current?.Session;
        if (session is null)
        {
            return Block("session_context_unavailable");
        }

        if (!ContainmentGateEvaluator.TryResolve(
            _sessionTargets,
            session,
            requireAtLeastOne: true,
            cancellationToken,
            out var sessionTargets))
        {
            return Block("target_resolution_failed");
        }

        ContainmentTarget[] additionalTargets = [];
        if (_additionalCallTargets is not null
            && !ContainmentGateEvaluator.TryResolve(
                _additionalCallTargets,
                call,
                requireAtLeastOne: false,
                cancellationToken,
                out additionalTargets))
        {
            return Block("target_resolution_failed");
        }

        if (!ContainmentGateEvaluator.TryCombine(sessionTargets, additionalTargets, out var targets))
        {
            return Block("target_resolution_failed");
        }

        var decision = ContainmentGateEvaluator.Evaluate(_store, targets, cancellationToken);
        return decision.MustBlock
            ? Block(decision.ReasonCode!)
            : new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
    }

    private ValueTask<ToolGateVerdict> Block(string reasonCode)
        => new(ToolGateVerdict.Block(
            PolicyName,
            $"containment_override:{reasonCode}"));
}
