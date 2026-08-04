// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.MAF.Gatekeeper;
using AgentEval.MissionControl.GraphQL;
using AgentEval.MissionControl.Services;
using Microsoft.Agents.AI;

namespace AgentEval.Samples;

/// <summary>Offline end-to-end graph observation, containment, and enforced-refusal story.</summary>
public static class GatekeeperSecurityGraphIncident
{
    private const string Tenant = "sample-tenant";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        GatekeeperSampleContractRenderer.Print("22");
        Console.WriteLine("\n=== Gatekeeper — Security Graph Incident Response (offline) ===\n");

        var directory = Path.Combine(
            Path.GetTempPath(),
            "agenteval-gatekeeper-graph-" + Guid.NewGuid().ToString("N"));
        var graphPath = Path.Combine(directory, "graph.json");
        var containmentPath = Path.Combine(directory, "containment.json");
        var clock = new SampleClock(Now);
        var agent = new SecurityGraphNode(SecurityGraphNodeKind.Agent, "support-agent");
        var endpoint = new SecurityGraphNode(SecurityGraphNodeKind.AgentEndpoint, "partner-endpoint");

        try
        {
            using var graphStore = new JsonFileSecurityGraphStore(
                graphPath,
                Tenant,
                "sample-key",
                Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
                new JsonFileSecurityGraphStoreOptions
                {
                    BootstrapIfMissing = true,
                    Retention = TimeSpan.FromDays(1),
                    MaxObservations = 32,
                    MaxCoverageGaps = 8,
                },
                clock);

            await using (var pump = new SecurityGraphIngestionPump(
                graphStore,
                queueCapacity: 8,
                drainTimeout: TimeSpan.FromSeconds(5)))
            {
                Require(pump.TryEnqueue(Observation("event-1", agent, endpoint, SecurityGraphSignalKind.CallObserved, "session-a")),
                    "first graph observation must enter the bounded queue");
                Require(pump.TryEnqueue(Observation("event-2", agent, endpoint, SecurityGraphSignalKind.CallBlocked, "session-b")),
                    "second graph observation must enter the bounded queue");
                Require(await pump.CompleteAndDrainAsync(), "the bounded graph queue must drain");
                Require(pump.AppliedCount == 2 && pump.DroppedCount == 0,
                    "exactly two content-free observations must be durably applied without drops");
            }

            var report = AgenticSecurityGraph.Compute(
                graphStore.Read(TimeSpan.FromHours(1)),
                [agent, endpoint],
                staleAfter: TimeSpan.FromMinutes(30),
                clock);
            Require(report.Coverage == SecurityGraphCoverageState.Complete,
                "the initial graph window must be complete");
            Require(report.TotalCallCount == 2 && report.TotalBlockedCallCount == 1,
                "the graph must report measured call and block totals");
            Require(report.FleetBlockRate is 0.5,
                "the complete graph must compute the measured 50% block rate");

            var operationsSource = new SecurityGraphStoreReportSource(
                Tenant,
                graphStore,
                [agent, endpoint],
                TimeSpan.FromHours(1),
                TimeSpan.FromMinutes(30),
                clock);
            var operationsView = new Query().SecurityGraph(
                operationsSource,
                nodeLimit: 2,
                edgeLimit: 1);
            Require(
                operationsView is
                {
                    Coverage: SecurityGraphCoverageState.Complete,
                    TotalCallCount: 2,
                    TotalBlockedCallCount: 1,
                    Nodes.Count: 2,
                    Edges.Count: 1,
                    NodesTruncated: false,
                    EdgesTruncated: false,
                },
                "the real read-only operations projection must expose bounded content-free totals");

            var decision = SecurityGraphContainmentDecision.ForNode(
                report,
                endpoint,
                "campaign_detected",
                "sample-incident-22");
            using var containmentStore = new JsonFileContainmentStore(
                containmentPath,
                new SampleReleaseVerifier(),
                new JsonFileContainmentStoreOptions { BootstrapIfMissing = true },
                clock);
            var applied = await new SecurityGraphContainmentBridge(Tenant, containmentStore)
                .ApplyAsync(decision);
            Require(applied.Snapshot.State == ContainmentSnapshotState.Active,
                "the evidence-backed graph decision must activate containment");

            var sessionTarget = new ContainmentTarget.Session(Tenant, "session-c");
            var overrideGate = new ContainmentOverrideGate(
                containmentStore,
                _ => [sessionTarget],
                _ => [decision.Target]);
            ToolGateVerdict refusal;
            using (AgentRunScope.Begin(new SampleSession(), "security-graph-sample", trace: null))
            {
                refusal = await overrideGate.InspectAsync(Call("delegate_to_partner"));
            }

            Require(refusal.Action == ToolGateAction.Block,
                "future calls to the contained endpoint must be refused inline");

            await graphStore.MarkCoverageGapAsync(new SecurityGraphCoverageGap("sample_queue_gap"));
            var incomplete = AgenticSecurityGraph.Compute(
                graphStore.Read(TimeSpan.FromHours(1)),
                [agent, endpoint],
                staleAfter: TimeSpan.FromMinutes(30),
                clock);
            Require(incomplete.Coverage == SecurityGraphCoverageState.Incomplete && incomplete.FleetBlockRate is null,
                "a known ingestion gap must remove the aggregate fleet-rate claim");
            RequireThrows<ArgumentException>(
                () => SecurityGraphContainmentDecision.ForNode(
                    incomplete,
                    endpoint,
                    "campaign_detected",
                    "sample-incomplete"),
                "an incomplete graph must not mint a new containment decision");

            Console.WriteLine("   durable observations: 2 calls across 2 sessions (1 blocked)");
            Console.WriteLine("   complete graph:       decision admitted → endpoint containment active");
            Console.WriteLine("   read-only ops view:   2 bounded nodes, 1 edge, no content payloads");
            Console.WriteLine("   future endpoint call: BLOCK by ContainmentOverrideGate");
            Console.WriteLine("   incomplete control:   no fleet rate and no new containment decision");
            Console.WriteLine("   ✅ local finding → graph → containment → enforced refusal completed with content-free evidence.");
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(directory);
        }
    }

    private static SecurityGraphObservationRequest Observation(
        string eventId,
        SecurityGraphNode source,
        SecurityGraphNode destination,
        SecurityGraphSignalKind signal,
        string session) =>
        new(eventId, source, destination, signal, session, "sample-evidence");

    private static GatedToolCall Call(string name) =>
        new(name, Arguments: null, AgentName: "security-graph-sample", Iteration: 0,
            FunctionCallIndex: 0, FunctionCount: 1, IsStreaming: false, Messages: null);

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("Security graph sample failed: " + message + ".");
    }

    private static void DeleteOwnedTemporaryDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("agenteval-gatekeeper-graph-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete a directory not owned by this sample.");
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Security graph sample failed: " + message + ".");
        }
    }

    private sealed class SampleClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SampleReleaseVerifier : IContainmentReleaseAuthorizationVerifier
    {
        public bool Verify(
            ContainmentReleaseAuthorization authorization,
            ReadOnlyMemory<byte> canonicalPayload) => false;
    }

    private sealed class SampleSession : AgentSession;
}
