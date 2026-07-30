// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Reflection;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using AgentEval.MissionControl;
using AgentEval.MissionControl.GraphQL;
using AgentEval.MissionControl.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentEval.Tests.MissionControl;

public sealed class SecurityGraphOpsSurfaceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private static readonly SecurityGraphNode Agent =
        new(SecurityGraphNodeKind.Agent, "agent-a");
    private static readonly SecurityGraphNode Server =
        new(SecurityGraphNodeKind.McpServer, "server-a");
    private static readonly SecurityGraphNode Endpoint =
        new(SecurityGraphNodeKind.AgentEndpoint, "endpoint-a");
    private static readonly SecurityGraphNode NeverObserved =
        new(SecurityGraphNodeKind.Tool, "tool-never");

    [Fact]
    public void ReaderSplit_PreservesOriginalStoreReadMethodToken()
    {
        Assert.Contains(
            typeof(ISecurityGraphReader),
            typeof(ISecurityGraphStore).GetInterfaces());
        Assert.NotNull(
            typeof(ISecurityGraphStore).GetMethod(
                nameof(ISecurityGraphStore.Read),
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void DefaultHost_UnconfiguredSourceReturnsNull()
    {
        var builder = WebApplication.CreateBuilder();
        McHost.ConfigureServices(builder);
        using var provider = builder.Services.BuildServiceProvider();
        var source = provider.GetRequiredService<
            ISecurityGraphReportSource>();

        var result = new Query().SecurityGraph(source);

        Assert.Null(result);
    }

    [Fact]
    public async Task GraphQl_InjectedSourceReturnsBoundedSecurityGraph()
    {
        using var factory = new WebApplicationFactory<Query>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<
                        ISecurityGraphReportSource>();
                    services.AddSingleton<
                        ISecurityGraphReportSource>(
                        new StaticSource(CompleteReport()));
                }));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/graphql",
            new
            {
                query = """
                    {
                      securityGraph(nodeLimit: 1, edgeLimit: 1) {
                        tenant
                        coverage
                        totalCallCount
                        totalNodeCount
                        totalEdgeCount
                        nodesTruncated
                        edgesTruncated
                        nodes { kind identifier callCount }
                        edges {
                          sourceKind
                          sourceIdentifier
                          destinationKind
                          destinationIdentifier
                          callCount
                        }
                      }
                    }
                    """,
            });
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("errors", out _));
        var graph = root.GetProperty("data")
            .GetProperty("securityGraph");
        Assert.Equal("tenant-a", graph.GetProperty("tenant").GetString());
        Assert.Equal("COMPLETE", graph.GetProperty("coverage").GetString());
        Assert.Equal(1, graph.GetProperty("totalCallCount").GetInt32());
        Assert.Equal(2, graph.GetProperty("totalNodeCount").GetInt32());
        Assert.Equal(1, graph.GetProperty("totalEdgeCount").GetInt32());
        Assert.True(graph.GetProperty("nodesTruncated").GetBoolean());
        Assert.False(graph.GetProperty("edgesTruncated").GetBoolean());
        Assert.Equal(1, graph.GetProperty("nodes").GetArrayLength());
        Assert.Equal(1, graph.GetProperty("edges").GetArrayLength());
    }

    [Fact]
    public void StoreSource_CompleteSnapshotComputesBoundedHonestProjection()
    {
        var expected = new List<SecurityGraphNode>
        {
            Agent,
            Server,
            Endpoint,
            NeverObserved,
        };
        var snapshot = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            Window,
            Now,
            [
                Observation(
                    "event-1",
                    Agent,
                    Server,
                    SecurityGraphSignalKind.CallBlocked),
                Observation(
                    "event-2",
                    Agent,
                    Endpoint,
                    SecurityGraphSignalKind.CallObserved),
            ]);
        var source = new SecurityGraphStoreReportSource(
            "tenant-a",
            new FixedReader(snapshot),
            expected,
            Window,
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));
        expected.Clear();
        expected.Add(
            new SecurityGraphNode(
                SecurityGraphNodeKind.Tool,
                "tool-added-later"));

        var result = new Query().SecurityGraph(
            source,
            nodeLimit: 2,
            edgeLimit: 1)!;

        Assert.Equal("tenant-a", result.Tenant);
        Assert.Equal(SecurityGraphCoverageState.Complete, result.Coverage);
        Assert.Equal(2, result.TotalCallCount);
        Assert.Equal(1, result.TotalBlockedCallCount);
        Assert.Equal(0.5, result.FleetBlockRate);
        Assert.Equal(4, result.TotalNodeCount);
        Assert.Equal(2, result.TotalEdgeCount);
        Assert.Equal(1, result.NeverObservedNodeCount);
        Assert.True(result.NodesTruncated);
        Assert.True(result.EdgesTruncated);
        Assert.Equal(2, result.Nodes.Count);
        Assert.Equal("agent-a", result.Nodes[0].Identifier);
        Assert.Equal("tool-never", result.Nodes[1].Identifier);
        Assert.Single(result.Edges);
        Assert.Equal("server-a", result.Edges[0].DestinationIdentifier);
        Assert.DoesNotContain(
            result.Nodes,
            node => node.Identifier == "tool-added-later");
    }

    [Fact]
    public void StoreSource_EmptyAndStaleFactsRemainExplicit()
    {
        var empty = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            Window,
            Now,
            observations: []);
        var stale = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            Window,
            Now,
            [new SecurityGraphObservation(
                "event-stale",
                Now.AddMinutes(-40),
                Agent,
                Server,
                SecurityGraphSignalKind.CallObserved,
                Digest("session-a"),
                "evidence:incident-1")]);

        var emptyView = new Query().SecurityGraph(Source(empty))!;
        var staleView = new Query().SecurityGraph(Source(stale))!;

        Assert.Equal(SecurityGraphCoverageState.Complete, emptyView.Coverage);
        Assert.Equal(0, emptyView.TotalCallCount);
        Assert.Null(emptyView.FleetBlockRate);
        Assert.Equal(2, emptyView.NeverObservedNodeCount);
        Assert.Equal(2, staleView.StaleNodeCount);
        Assert.All(staleView.Nodes, node => Assert.True(node.Stale));
    }

    [Fact]
    public void StoreSource_IncompleteAndIndeterminateCoverageRemainExplicit()
    {
        var incomplete = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            Window,
            Now,
            [Observation(
                "event-1",
                Agent,
                Server,
                SecurityGraphSignalKind.CallBlocked)],
            [SecurityGraphCoverageGap.Accepted(
                Now.AddMinutes(-1),
                "queue_full")]);
        var indeterminate = SecurityGraphTenantSnapshot.Indeterminate(
            "tenant-a",
            Window,
            Now,
            "store_unavailable");

        var incompleteView = new Query().SecurityGraph(
            Source(incomplete))!;
        var indeterminateView = new Query().SecurityGraph(
            Source(indeterminate))!;

        Assert.Equal(
            SecurityGraphCoverageState.Incomplete,
            incompleteView.Coverage);
        Assert.Null(incompleteView.FleetBlockRate);
        Assert.Equal(1, incompleteView.TotalCallCount);
        Assert.Equal(
            SecurityGraphCoverageState.Indeterminate,
            indeterminateView.Coverage);
        Assert.Null(indeterminateView.FleetBlockRate);
        Assert.Equal(0, indeterminateView.TotalCallCount);
        Assert.All(
            indeterminateView.Nodes,
            node => Assert.True(node.NeverObserved));
    }

    [Fact]
    public void StoreSource_TenantOrWindowMismatchFailsWithoutProjection()
    {
        var crossTenant = SecurityGraphTenantSnapshot.Determinate(
            "tenant-b",
            Window,
            Now,
            observations: []);
        var wrongWindow = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            TimeSpan.FromMinutes(30),
            Now,
            observations: []);

        Assert.Throws<InvalidOperationException>(
            () => Source(crossTenant).Read());
        Assert.Throws<InvalidOperationException>(
            () => Source(wrongWindow).Read());
    }

    [Fact]
    public void StoreSource_InvalidOrDuplicateConfigurationIsRejected()
    {
        var reader = new FixedReader(
            SecurityGraphTenantSnapshot.Determinate(
                "tenant-a",
                Window,
                Now,
                observations: []));

        Assert.Throws<ArgumentException>(
            () => new SecurityGraphStoreReportSource(
                "tenant-a",
                reader,
                [Server, Server],
                Window,
                TimeSpan.FromMinutes(30)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SecurityGraphStoreReportSource(
                "tenant-a",
                reader,
                [Server],
                Window,
                Window + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Query_UnconfiguredSourceStillValidatesLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Query().SecurityGraph(
                new NullSource(),
                nodeLimit: 0,
                edgeLimit: 1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(501, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1001)]
    public void Query_OutOfRangeProjectionLimitsAreRejected(
        int nodeLimit,
        int edgeLimit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Query().SecurityGraph(
                new StaticSource(CompleteReport()),
                nodeLimit,
                edgeLimit));
    }

    [Fact]
    public void ProjectionContract_ContainsNoRawGraphOrSecretFields()
    {
        var forbidden = new[]
        {
            "sessiondigest",
            "digest",
            "event",
            "evidence",
            "key",
            "observation",
            "gap",
        };
        var exposed = new[]
        {
            typeof(SecurityGraphOpsView),
            typeof(SecurityGraphOpsNode),
            typeof(SecurityGraphOpsEdge),
        };

        foreach (var property in exposed.SelectMany(type =>
                     type.GetProperties(
                         BindingFlags.Instance |
                         BindingFlags.Public)))
        {
            Assert.DoesNotContain(
                forbidden,
                token => property.Name.Contains(
                    token,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void MissionControlAssembly_HasNoGraphStoreWriteOrConcreteOwnership()
    {
        var assembly = typeof(Query).Assembly;
        var offenders = assembly.GetTypes()
            .Where(type => !type.IsNestedPrivate)
            .SelectMany(type =>
                type.GetConstructors(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    .Cast<MethodBase>()
                    .Concat(type.GetMethods(
                        BindingFlags.Instance |
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly)))
            .SelectMany(method => method.GetParameters())
            .Where(parameter =>
                parameter.ParameterType == typeof(ISecurityGraphStore) ||
                parameter.ParameterType ==
                    typeof(JsonFileSecurityGraphStore))
            .Select(parameter =>
                $"{parameter.Member.DeclaringType?.FullName}." +
                $"{parameter.Member.Name}:{parameter.Name}")
            .ToArray();

        Assert.Empty(offenders);
    }

    private static SecurityGraphStoreReportSource Source(
        SecurityGraphTenantSnapshot snapshot)
        => new(
            "tenant-a",
            new FixedReader(snapshot),
            [Agent, Server],
            Window,
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));

    private static SecurityGraphReport CompleteReport()
        => Source(
                SecurityGraphTenantSnapshot.Determinate(
                    "tenant-a",
                    Window,
                    Now,
                    [Observation(
                        "event-1",
                        Agent,
                        Server,
                        SecurityGraphSignalKind.CallObserved)]))
            .Read();

    private static SecurityGraphObservation Observation(
        string eventId,
        SecurityGraphNode source,
        SecurityGraphNode destination,
        SecurityGraphSignalKind signal)
        => new(
            eventId,
            Now.AddMinutes(-5),
            source,
            destination,
            signal,
            Digest("session-a"),
            "evidence:incident-1");

    private static string Digest(string value)
        => Convert.ToBase64String(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(value)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class FixedReader(
        SecurityGraphTenantSnapshot snapshot) :
        ISecurityGraphReader
    {
        public SecurityGraphTenantSnapshot Read(TimeSpan window)
            => snapshot;
    }

    private sealed class NullSource : ISecurityGraphReportSource
    {
        public SecurityGraphReport? Read() => null;
    }

    private sealed class StaticSource(
        SecurityGraphReport report) :
        ISecurityGraphReportSource
    {
        public SecurityGraphReport Read() => report;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

#endif
