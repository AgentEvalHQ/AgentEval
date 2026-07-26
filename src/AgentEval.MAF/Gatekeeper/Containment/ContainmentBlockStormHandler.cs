// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Maps a root run's stable session target to one idempotent durable block-storm containment.</summary>
internal sealed class ContainmentBlockStormHandler
{
    private readonly IContainmentStore _store;
    private readonly Func<AgentSession, IReadOnlyList<ContainmentTarget>> _sessionTargets;

    internal ContainmentBlockStormHandler(
        IContainmentStore store,
        Func<AgentSession, IReadOnlyList<ContainmentTarget>> sessionTargets)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessionTargets = sessionTargets ?? throw new ArgumentNullException(nameof(sessionTargets));
    }

    internal async ValueTask HandleAsync(
        BlockStormIncident incident,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        cancellationToken.ThrowIfCancellationRequested();

        var rootSession = AgentRunScope.Current?.Root.Session;
        if (rootSession is null
            || !ContainmentGateEvaluator.TryResolve(
                _sessionTargets,
                rootSession,
                requireAtLeastOne: true,
                cancellationToken,
                out var targets)
            || targets[0] is not ContainmentTarget.Session target)
        {
            throw new InvalidOperationException("The block-storm containment target could not be resolved.");
        }

        var result = await _store.ContainAsync(
            new ContainmentRequest(
                target,
                reasonCode: "block_storm",
                evidenceReference: incident.EvidenceReference,
                issuer: "gatekeeper"),
            cancellationToken).ConfigureAwait(false);

        if (result is null
            || result.Disposition is not (
                ContainmentMutationDisposition.Applied
                or ContainmentMutationDisposition.Unchanged)
            || result.Snapshot.State != ContainmentSnapshotState.Active
            || result.Snapshot.Target != target)
        {
            throw new InvalidOperationException("The block-storm containment outcome was not durably active.");
        }
    }
}
