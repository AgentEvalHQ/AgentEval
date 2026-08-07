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

            Console.WriteLine("── An incident, end to end: local finding → durable graph → containment → refusal ──");

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
                Console.WriteLine($"   ▶ ingest   two content-free observations (a call, a blocked call) durably applied; dropped: {pump.DroppedCount}");
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
            Console.WriteLine($"   ▶ compute  coverage={report.Coverage}; {report.TotalCallCount} calls, {report.TotalBlockedCallCount} blocked → measured fleet block rate {report.FleetBlockRate!.Value:P0}");

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
            Console.WriteLine($"   ▶ project  Mission Control sees {operationsView!.Nodes.Count} nodes / {operationsView.Edges.Count} edge — read-only, bounded, content-free");

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
            Console.WriteLine("   ▶ contain  the evidence-backed decision activates durable containment of partner-endpoint");

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
            Console.WriteLine("   ▶ enforce  the next delegate_to_partner call → ⛔ BLOCK, inline, from the durable decision");

            Console.WriteLine("   ▶ gap test mark a known ingestion gap, then try to mint another containment decision…");
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
            Console.WriteLine("              └ coverage=Incomplete → fleet rate withdrawn → new decision REFUSED (incomplete evidence cannot mint containment)");

            Console.WriteLine();
            Console.WriteLine($"   {"Stage",-14} {"Measured evidence",-50} Security disposition");
            Console.WriteLine($"   {new string('─', 14)} {new string('─', 50)} {new string('─', 20)}");
            PrintStage("1  ingest", $"{report.TotalCallCount} calls / {report.TotalBlockedCallCount} blocked", "DURABLY APPLIED");
            PrintStage("2  compute", $"coverage={report.Coverage}; fleet block rate={report.FleetBlockRate!.Value:P0}", "DECISION ELIGIBLE");
            PrintStage("3  project", $"{operationsView!.Nodes.Count} nodes / {operationsView.Edges.Count} edge / content-free", "READ ONLY");
            PrintStage("4  enforce", "partner-endpoint containment active", refusal.Action.ToString().ToUpperInvariant());
            PrintStage("5  gap test", $"coverage={incomplete.Coverage}; fleet rate absent", "NO NEW DECISION");
            Console.WriteLine("   ✅ local finding → graph → containment → enforced refusal completed with content-free evidence.");
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(directory);
        }
    }

    private static void PrintStage(string stage, string evidence, string disposition) =>
        Console.WriteLine($"   {stage,-14} {evidence,-50} {disposition}");
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
