// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>Bounded operator projection of an honest tenant security graph report.</summary>
public sealed record SecurityGraphOpsView(
    string Tenant,
    double WindowSeconds,
    DateTimeOffset CapturedAtUtc,
    SecurityGraphCoverageState Coverage,
    int TotalCallCount,
    int TotalBlockedCallCount,
    double? FleetBlockRate,
    int TotalNodeCount,
    int TotalEdgeCount,
    int NeverObservedNodeCount,
    int StaleNodeCount,
    bool NodesTruncated,
    bool EdgesTruncated,
    IReadOnlyList<SecurityGraphOpsNode> Nodes,
    IReadOnlyList<SecurityGraphOpsEdge> Edges);

/// <summary>Privacy-minimized graph-node facts; no sessions, events, or evidence are exposed.</summary>
public sealed record SecurityGraphOpsNode(
    SecurityGraphNodeKind Kind,
    string Identifier,
    int? CallCount,
    int? BlockedCallCount,
    double? BlockRate,
    int? DistinctSessionCount,
    DateTimeOffset? LastObservedAtUtc,
    DateTimeOffset? LastContainedAtUtc,
    DateTimeOffset? LastReleasedAtUtc,
    bool NeverObserved,
    bool Stale);

/// <summary>Privacy-minimized graph-edge facts; no sessions, events, or evidence are exposed.</summary>
public sealed record SecurityGraphOpsEdge(
    SecurityGraphNodeKind SourceKind,
    string SourceIdentifier,
    SecurityGraphNodeKind DestinationKind,
    string DestinationIdentifier,
    int CallCount,
    int BlockedCallCount,
    double BlockRate,
    int DistinctSessionCount,
    DateTimeOffset LastObservedAtUtc);

internal static class SecurityGraphOpsProjector
{
    internal const int DefaultNodeLimit = 100;
    internal const int DefaultEdgeLimit = 100;
    internal const int MaximumNodeLimit = 500;
    internal const int MaximumEdgeLimit = 1000;

    internal static SecurityGraphOpsView Project(
        SecurityGraphReport report,
        int nodeLimit,
        int edgeLimit)
    {
        ArgumentNullException.ThrowIfNull(report);
        ValidateLimits(nodeLimit, edgeLimit);

        var nodes = report.Nodes
            .Take(nodeLimit)
            .Select(node => new SecurityGraphOpsNode(
                node.Node.Kind,
                node.Node.Identifier,
                node.CallCount,
                node.BlockedCallCount,
                node.BlockRate,
                node.DistinctSessionCount,
                node.LastObservedAtUtc,
                node.LastContainedAtUtc,
                node.LastReleasedAtUtc,
                node.NeverObserved,
                node.Stale))
            .ToArray();
        var edges = report.Edges
            .Take(edgeLimit)
            .Select(edge => new SecurityGraphOpsEdge(
                edge.Source.Kind,
                edge.Source.Identifier,
                edge.Destination.Kind,
                edge.Destination.Identifier,
                edge.CallCount,
                edge.BlockedCallCount,
                edge.BlockRate,
                edge.DistinctSessionCount,
                edge.LastObservedAtUtc))
            .ToArray();

        return new SecurityGraphOpsView(
            report.Tenant,
            report.Window.TotalSeconds,
            report.CapturedAtUtc,
            report.Coverage,
            report.TotalCallCount,
            report.TotalBlockedCallCount,
            report.FleetBlockRate,
            report.Nodes.Count,
            report.Edges.Count,
            report.NeverObservedNodes.Count,
            report.StaleNodes.Count,
            nodes.Length < report.Nodes.Count,
            edges.Length < report.Edges.Count,
            Array.AsReadOnly(nodes),
            Array.AsReadOnly(edges));
    }

    internal static void ValidateLimits(
        int nodeLimit,
        int edgeLimit)
    {
        if (nodeLimit is < 1 or > MaximumNodeLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeLimit));
        }

        if (edgeLimit is < 1 or > MaximumEdgeLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(edgeLimit));
        }
    }
}
