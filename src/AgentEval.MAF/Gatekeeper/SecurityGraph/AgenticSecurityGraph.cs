// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Computes an honest cross-session graph view from a tenant snapshot.</summary>
public static class AgenticSecurityGraph
{
    private const int MaximumExpectedNodes = 4096;

    /// <summary>
    /// Computes node, edge, and fleet call-block facts without assigning health to unobserved nodes.
    /// </summary>
    public static SecurityGraphReport Compute(
        SecurityGraphTenantSnapshot snapshot,
        IReadOnlyCollection<SecurityGraphNode> expectedNodes,
        TimeSpan staleAfter,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(expectedNodes);
        if (expectedNodes.Count is < 1 or > MaximumExpectedNodes)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedNodes));
        }

        if (staleAfter <= TimeSpan.Zero || staleAfter > snapshot.Window)
        {
            throw new ArgumentOutOfRangeException(nameof(staleAfter));
        }

        var expected = new HashSet<SecurityGraphNode>();
        foreach (var node in expectedNodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            if (!expected.Add(node))
            {
                throw new ArgumentException(
                    "Expected graph nodes must be unique.",
                    nameof(expectedNodes));
            }
        }

        var nodes = new Dictionary<SecurityGraphNode, NodeAccumulator>();
        foreach (var node in expected)
        {
            nodes[node] = new NodeAccumulator(node);
        }

        var edges = new Dictionary<EdgeKey, EdgeAccumulator>();
        var totalCalls = 0;
        var totalBlocks = 0;
        foreach (var observation in snapshot.Observations)
        {
            ArgumentNullException.ThrowIfNull(observation);
            var destination = GetNode(nodes, observation.Destination);
            destination.Observe(observation);

            if (observation.Source is { } sourceNode &&
                sourceNode != observation.Destination)
            {
                GetNode(nodes, sourceNode).Observe(observation);
            }

            if (!IsCall(observation.Signal))
            {
                continue;
            }

            totalCalls++;
            if (observation.Signal == SecurityGraphSignalKind.CallBlocked)
            {
                totalBlocks++;
            }

            var source = observation.Source!;
            var edgeKey = new EdgeKey(source, observation.Destination);
            if (!edges.TryGetValue(edgeKey, out var edge))
            {
                edge = new EdgeAccumulator(source, observation.Destination);
                edges.Add(edgeKey, edge);
            }

            edge.Observe(observation);
        }

        var now = ContainmentValidation.Utc(
            (clock ?? TimeProvider.System).GetUtcNow(),
            nameof(clock));
        if (now < snapshot.CapturedAtUtc)
        {
            throw new ArgumentException(
                "The computation clock cannot precede the snapshot clock.",
                nameof(clock));
        }
        var nodeReports = nodes.Values
            .Select(node => node.Report(now, staleAfter))
            .OrderBy(report => report.Node.Kind)
            .ThenBy(report => report.Node.Identifier, StringComparer.Ordinal)
            .ToArray();
        var edgeReports = edges.Values
            .Select(edge => edge.Report())
            .OrderBy(report => report.Source.Kind)
            .ThenBy(report => report.Source.Identifier, StringComparer.Ordinal)
            .ThenBy(report => report.Destination.Kind)
            .ThenBy(report => report.Destination.Identifier, StringComparer.Ordinal)
            .ToArray();
        var neverObserved = nodeReports
            .Where(report => report.NeverObserved)
            .Select(report => report.Node)
            .ToArray();
        var staleNodes = nodeReports
            .Where(report => report.Stale)
            .Select(report => report.Node)
            .ToArray();

        double? fleetBlockRate =
            snapshot.Coverage == SecurityGraphCoverageState.Complete &&
            totalCalls > 0
                ? (double)totalBlocks / totalCalls
                : null;
        var explanation = snapshot.Coverage switch
        {
            SecurityGraphCoverageState.Complete when totalCalls == 0 =>
                $"Complete window; 0 accepted calls; {neverObserved.Length} expected node(s) never observed; no fleet block rate.",
            SecurityGraphCoverageState.Complete =>
                $"Complete window; {totalCalls} accepted call(s), {totalBlocks} blocked; {neverObserved.Length} expected node(s) never observed.",
            SecurityGraphCoverageState.Incomplete =>
                $"Incomplete window; observed facts are lower bounds; {snapshot.CoverageGaps.Count} coverage gap(s); no fleet block rate.",
            _ =>
                "Indeterminate window; storage validity is unproven; no fleet block rate.",
        };

        return new SecurityGraphReport(
            snapshot.Tenant,
            snapshot.Window,
            snapshot.CapturedAtUtc,
            snapshot.Coverage,
            Array.AsReadOnly(nodeReports),
            Array.AsReadOnly(edgeReports),
            Array.AsReadOnly(neverObserved),
            Array.AsReadOnly(staleNodes),
            totalCalls,
            totalBlocks,
            fleetBlockRate,
            explanation);
    }

    private static NodeAccumulator GetNode(
        Dictionary<SecurityGraphNode, NodeAccumulator> nodes,
        SecurityGraphNode key)
    {
        if (!nodes.TryGetValue(key, out var node))
        {
            node = new NodeAccumulator(key);
            nodes.Add(key, node);
        }

        return node;
    }

    private static bool IsCall(SecurityGraphSignalKind signal)
        => signal is SecurityGraphSignalKind.CallObserved or
            SecurityGraphSignalKind.CallBlocked;

    private sealed class NodeAccumulator(SecurityGraphNode node)
    {
        private readonly HashSet<string> _sessions =
            new(StringComparer.Ordinal);
        private int _calls;
        private int _blocks;
        private DateTimeOffset? _lastObserved;
        private DateTimeOffset? _lastContained;
        private DateTimeOffset? _lastReleased;

        public void Observe(SecurityGraphObservation observation)
        {
            _sessions.Add(observation.SessionDigest);
            if (_lastObserved is null ||
                observation.AcceptedAtUtc > _lastObserved.Value)
            {
                _lastObserved = observation.AcceptedAtUtc;
            }

            switch (observation.Signal)
            {
                case SecurityGraphSignalKind.CallObserved:
                    _calls++;
                    break;
                case SecurityGraphSignalKind.CallBlocked:
                    _calls++;
                    _blocks++;
                    break;
                case SecurityGraphSignalKind.ContainmentApplied:
                    _lastContained = Later(
                        _lastContained,
                        observation.AcceptedAtUtc);
                    break;
                case SecurityGraphSignalKind.ContainmentReleased:
                    _lastReleased = Later(
                        _lastReleased,
                        observation.AcceptedAtUtc);
                    break;
            }
        }

        public SecurityGraphNodeReport Report(
            DateTimeOffset now,
            TimeSpan staleAfter)
        {
            var neverObserved = _lastObserved is null;
            return new SecurityGraphNodeReport(
                node,
                neverObserved ? null : _calls,
                neverObserved ? null : _blocks,
                _calls > 0 ? (double)_blocks / _calls : null,
                neverObserved ? null : _sessions.Count,
                _lastObserved,
                _lastContained,
                _lastReleased,
                neverObserved,
                _lastObserved is { } last && now - last > staleAfter);
        }

        private static DateTimeOffset Later(
            DateTimeOffset? current,
            DateTimeOffset candidate)
            => current is null || candidate > current.Value
                ? candidate
                : current.Value;
    }

    private sealed class EdgeAccumulator(
        SecurityGraphNode source,
        SecurityGraphNode destination)
    {
        private readonly HashSet<string> _sessions =
            new(StringComparer.Ordinal);
        private int _calls;
        private int _blocks;
        private DateTimeOffset _lastObserved;

        public void Observe(SecurityGraphObservation observation)
        {
            _calls++;
            if (observation.Signal == SecurityGraphSignalKind.CallBlocked)
            {
                _blocks++;
            }

            _sessions.Add(observation.SessionDigest);
            if (observation.AcceptedAtUtc > _lastObserved)
            {
                _lastObserved = observation.AcceptedAtUtc;
            }
        }

        public SecurityGraphEdgeReport Report()
            => new(
                source,
                destination,
                _calls,
                _blocks,
                (double)_blocks / _calls,
                _sessions.Count,
                _lastObserved);
    }

    private sealed record EdgeKey(
        SecurityGraphNode Source,
        SecurityGraphNode Destination);
}

/// <summary>One honestly observed or explicitly never-observed graph node.</summary>
public sealed record SecurityGraphNodeReport(
    SecurityGraphNode Node,
    int? CallCount,
    int? BlockedCallCount,
    double? BlockRate,
    int? DistinctSessionCount,
    DateTimeOffset? LastObservedAtUtc,
    DateTimeOffset? LastContainedAtUtc,
    DateTimeOffset? LastReleasedAtUtc,
    bool NeverObserved,
    bool Stale);

/// <summary>Aggregated observed call edge.</summary>
public sealed record SecurityGraphEdgeReport(
    SecurityGraphNode Source,
    SecurityGraphNode Destination,
    int CallCount,
    int BlockedCallCount,
    double BlockRate,
    int DistinctSessionCount,
    DateTimeOffset LastObservedAtUtc);

/// <summary>Honest tenant graph computation; missing and incomplete inputs are explicit.</summary>
public sealed record SecurityGraphReport(
    string Tenant,
    TimeSpan Window,
    DateTimeOffset CapturedAtUtc,
    SecurityGraphCoverageState Coverage,
    IReadOnlyList<SecurityGraphNodeReport> Nodes,
    IReadOnlyList<SecurityGraphEdgeReport> Edges,
    IReadOnlyList<SecurityGraphNode> NeverObservedNodes,
    IReadOnlyList<SecurityGraphNode> StaleNodes,
    int TotalCallCount,
    int TotalBlockedCallCount,
    double? FleetBlockRate,
    string Explanation);
