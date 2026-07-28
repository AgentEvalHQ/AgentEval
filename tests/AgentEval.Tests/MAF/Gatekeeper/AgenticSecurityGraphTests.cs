// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class AgenticSecurityGraphTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private static readonly SecurityGraphNode Agent =
        new(SecurityGraphNodeKind.Agent, "agent-a");
    private static readonly SecurityGraphNode Tool =
        new(SecurityGraphNodeKind.Tool, "tool-a");
    private static readonly SecurityGraphNode Missing =
        new(SecurityGraphNodeKind.McpServer, "never-seen");

    [Fact]
    public void Compute_CompleteWindow_AggregatesObservedFactsWithoutFabricatingMissingNode()
    {
        var observations = new[]
        {
            Observation(
                "event-1",
                Now.AddMinutes(-10),
                Agent,
                Tool,
                SecurityGraphSignalKind.CallObserved,
                Digest('A')),
            Observation(
                "event-2",
                Now.AddMinutes(-5),
                Agent,
                Tool,
                SecurityGraphSignalKind.CallBlocked,
                Digest('B')),
            Observation(
                "event-3",
                Now.AddMinutes(-4),
                source: null,
                Tool,
                SecurityGraphSignalKind.ContainmentApplied,
                Digest('B')),
        };
        var snapshot = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            Window,
            Now,
            observations);

        var report = AgenticSecurityGraph.Compute(
            snapshot,
            [Agent, Tool, Missing],
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));

        Assert.Equal(SecurityGraphCoverageState.Complete, report.Coverage);
        Assert.Equal(2, report.TotalCallCount);
        Assert.Equal(1, report.TotalBlockedCallCount);
        Assert.Equal(0.5, report.FleetBlockRate);

        var agent = Assert.Single(report.Nodes, node => node.Node == Agent);
        Assert.Equal(2, agent.CallCount);
        Assert.Equal(1, agent.BlockedCallCount);
        Assert.Equal(2, agent.DistinctSessionCount);
        Assert.False(agent.NeverObserved);

        var tool = Assert.Single(report.Nodes, node => node.Node == Tool);
        Assert.Equal(2, tool.CallCount);
        Assert.Equal(1, tool.BlockedCallCount);
        Assert.Equal(Now.AddMinutes(-4), tool.LastContainedAtUtc);

        var missing = Assert.Single(report.Nodes, node => node.Node == Missing);
        Assert.True(missing.NeverObserved);
        Assert.Null(missing.CallCount);
        Assert.Null(missing.BlockedCallCount);
        Assert.Null(missing.DistinctSessionCount);
        Assert.Contains(Missing, report.NeverObservedNodes);

        var edge = Assert.Single(report.Edges);
        Assert.Equal(Agent, edge.Source);
        Assert.Equal(Tool, edge.Destination);
        Assert.Equal(2, edge.CallCount);
        Assert.Equal(1, edge.BlockedCallCount);
        Assert.Equal(0.5, edge.BlockRate);
        Assert.Equal(2, edge.DistinctSessionCount);
    }

    [Fact]
    public void Compute_IncompleteWindow_ReturnsFactsButWithholdsFleetRate()
    {
        var observation = Observation(
            "event-1",
            Now.AddMinutes(-5),
            Agent,
            Tool,
            SecurityGraphSignalKind.CallBlocked,
            Digest('A'));
        var gap = SecurityGraphCoverageGap.Accepted(
            Now.AddMinutes(-3),
            "queue_full",
            count: 2);
        var snapshot = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            Window,
            Now,
            [observation],
            [gap]);

        var report = AgenticSecurityGraph.Compute(
            snapshot,
            [Agent, Tool],
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));

        Assert.Equal(SecurityGraphCoverageState.Incomplete, report.Coverage);
        Assert.Equal(1, report.TotalCallCount);
        Assert.Equal(1, report.TotalBlockedCallCount);
        Assert.Null(report.FleetBlockRate);
        Assert.Contains("lower bounds", report.Explanation);
        Assert.DoesNotContain("agent-a", report.Explanation);
        Assert.DoesNotContain("tool-a", report.Explanation);
        Assert.DoesNotContain("queue_full", report.Explanation);
    }

    [Fact]
    public void Compute_IndeterminateWindow_ReturnsNoInventedRate()
    {
        var snapshot = SecurityGraphTenantSnapshot.Indeterminate(
            "tenant-a",
            Window,
            Now,
            "store_unavailable");

        var report = AgenticSecurityGraph.Compute(
            snapshot,
            [Agent],
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));

        Assert.Equal(SecurityGraphCoverageState.Indeterminate, report.Coverage);
        Assert.Null(report.FleetBlockRate);
        Assert.Equal(0, report.TotalCallCount);
        var node = Assert.Single(report.Nodes);
        Assert.True(node.NeverObserved);
        Assert.Null(node.CallCount);
        Assert.DoesNotContain("store_unavailable", report.Explanation);
    }

    [Fact]
    public void Compute_EmptyCompleteWindow_ListsEveryExpectedNodeAsNeverObserved()
    {
        var snapshot = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            Window,
            Now,
            observations: []);

        var report = AgenticSecurityGraph.Compute(
            snapshot,
            [Agent, Tool],
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));

        Assert.Null(report.FleetBlockRate);
        Assert.Equal(2, report.NeverObservedNodes.Count);
        Assert.All(report.Nodes, node =>
        {
            Assert.True(node.NeverObserved);
            Assert.Null(node.CallCount);
        });
    }

    [Fact]
    public void Compute_ObservedUnexpectedNodeIsIncludedAndStalenessIsExplicit()
    {
        var unexpected = new SecurityGraphNode(
            SecurityGraphNodeKind.AgentEndpoint,
            "remote-a");
        var observation = Observation(
            "event-1",
            Now.AddMinutes(-40),
            Agent,
            unexpected,
            SecurityGraphSignalKind.CallObserved,
            Digest('A'));
        var snapshot = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            Window,
            Now,
            [observation]);

        var report = AgenticSecurityGraph.Compute(
            snapshot,
            [Agent],
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));

        Assert.Equal(2, report.Nodes.Count);
        Assert.Contains(
            report.StaleNodes,
            node => node == unexpected);
        Assert.Contains(
            report.Nodes,
            node => node.Node == unexpected && node.Stale);
    }

    [Fact]
    public void Compute_SelfEdgeCountsNodeOnce()
    {
        var observation = Observation(
            "event-1",
            Now.AddMinutes(-1),
            Agent,
            Agent,
            SecurityGraphSignalKind.CallBlocked,
            Digest('A'));
        var snapshot = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            Window,
            Now,
            [observation]);

        var report = AgenticSecurityGraph.Compute(
            snapshot,
            [Agent],
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));

        var node = Assert.Single(report.Nodes);
        Assert.Equal(1, node.CallCount);
        Assert.Equal(1, node.BlockedCallCount);
        Assert.Single(report.Edges);
    }

    [Fact]
    public void Compute_InvalidExpectedSetStalenessOrClock_Fails()
    {
        var snapshot = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            Window,
            Now,
            observations: []);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AgenticSecurityGraph.Compute(
                snapshot,
                [],
                TimeSpan.FromMinutes(30)));
        Assert.Throws<ArgumentException>(
            () => AgenticSecurityGraph.Compute(
                snapshot,
                [Agent, new SecurityGraphNode(
                    SecurityGraphNodeKind.Agent,
                    Agent.Identifier)],
                TimeSpan.FromMinutes(30)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AgenticSecurityGraph.Compute(
                snapshot,
                [Agent],
                Window.Add(TimeSpan.FromSeconds(1))));
        Assert.Throws<ArgumentException>(
            () => AgenticSecurityGraph.Compute(
                snapshot,
                [Agent],
                TimeSpan.FromMinutes(30),
                new FixedClock(Now.AddSeconds(-1))));
    }

    [Fact]
    public void Models_EnforceCallAndContainmentShapesWithoutEchoingRejectedValues()
    {
        Assert.Throws<ArgumentException>(
            () => new SecurityGraphObservationRequest(
                "event-1",
                source: null,
                Tool,
                SecurityGraphSignalKind.CallBlocked,
                "session-a"));
        Assert.Throws<ArgumentException>(
            () => new SecurityGraphObservationRequest(
                "event-1",
                Agent,
                Tool,
                SecurityGraphSignalKind.ContainmentApplied,
                "session-a"));

        const string secretIdentifier = "secret\ridentifier";
        var exception = Assert.Throws<ArgumentException>(
            () => new SecurityGraphNode(
                SecurityGraphNodeKind.Tool,
                secretIdentifier));
        Assert.DoesNotContain(secretIdentifier, exception.Message);
        Assert.Throws<ArgumentException>(
            () => new SecurityGraphNode(
                SecurityGraphNodeKind.Tool,
                " padded"));
        Assert.Throws<ArgumentException>(
            () => new SecurityGraphObservation(
                "event-2",
                Now,
                Agent,
                Tool,
                SecurityGraphSignalKind.CallObserved,
                new string(':', 43)));
        Assert.Throws<ArgumentException>(
            () => new SecurityGraphObservation(
                "event-3",
                Now,
                Agent,
                Tool,
                SecurityGraphSignalKind.CallObserved,
                new string('A', 42) + "B"));
    }

    [Fact]
    public void Snapshot_RejectsEntriesOutsideWindowAndDoesNotExposeMutableArrays()
    {
        var tooOld = Observation(
            "event-1",
            Now.Subtract(Window).AddTicks(-1),
            Agent,
            Tool,
            SecurityGraphSignalKind.CallObserved,
            Digest('A'));

        Assert.Throws<ArgumentException>(
            () => SecurityGraphTenantSnapshot.Determinate(
                "tenant-a",
                Window,
                Now,
                [tooOld]));

        var valid = Observation(
            "event-2",
            Now,
            Agent,
            Tool,
            SecurityGraphSignalKind.CallObserved,
            Digest('A'));
        var snapshot = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            Window,
            Now,
            [valid]);

        Assert.False(
            snapshot.Observations is SecurityGraphObservation[]);
        var list = Assert.IsAssignableFrom<IList<SecurityGraphObservation>>(
            snapshot.Observations);
        Assert.Throws<NotSupportedException>(
            () => list[0] = tooOld);
    }

    [Fact]
    public void ReportCollections_AreReadOnlyAndExplanationOmitsSensitiveFields()
    {
        var source = new SecurityGraphNode(
            SecurityGraphNodeKind.Agent,
            "sensitive-agent-id");
        var destination = new SecurityGraphNode(
            SecurityGraphNodeKind.Tool,
            "sensitive-tool-id");
        var observation = new SecurityGraphObservation(
            "event-1",
            Now,
            source,
            destination,
            SecurityGraphSignalKind.CallBlocked,
            Digest('Z'),
            "evidence-secret-ref");
        var snapshot = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            Window,
            Now,
            [observation]);

        var report = AgenticSecurityGraph.Compute(
            snapshot,
            [source, destination],
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));

        Assert.False(report.Nodes is SecurityGraphNodeReport[]);
        Assert.DoesNotContain(source.Identifier, report.Explanation);
        Assert.DoesNotContain(destination.Identifier, report.Explanation);
        Assert.DoesNotContain(observation.SessionDigest, report.Explanation);
        Assert.DoesNotContain(
            observation.EvidenceReference!,
            report.Explanation);
    }

    private static SecurityGraphObservation Observation(
        string eventId,
        DateTimeOffset acceptedAtUtc,
        SecurityGraphNode? source,
        SecurityGraphNode destination,
        SecurityGraphSignalKind signal,
        string sessionDigest)
        => new(
            eventId,
            acceptedAtUtc,
            source,
            destination,
            signal,
            sessionDigest);

    private static string Digest(char value)
        => Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData([(byte)value]))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
