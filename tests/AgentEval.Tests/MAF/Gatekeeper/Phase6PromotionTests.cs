// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 6.R aggregate promotion evidence across ingestion, compute, and containment enforcement.</summary>
public sealed class Phase6PromotionTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 18, 0, 0, TimeSpan.Zero);
    private static readonly byte[] SessionKey =
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly SecurityGraphNode Agent =
        new(SecurityGraphNodeKind.Agent, "agent-a");
    private static readonly SecurityGraphNode Server =
        new(SecurityGraphNodeKind.McpServer, "server-a");
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "agenteval-phase6-promotion-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DurableIngestion_CompleteGraphDecision_BlocksPhase3Gates()
    {
        const string rawSession = "raw-session-must-not-persist";
        using var graphStore = CreateGraphStore("complete.json");
        await using var pump = new SecurityGraphIngestionPump(graphStore);
        Assert.True(
            pump.TryEnqueue(
                new SecurityGraphObservationRequest(
                    "event-1",
                    Agent,
                    Server,
                    SecurityGraphSignalKind.CallBlocked,
                    rawSession,
                    "evidence:graph-1")));

        Assert.True(await pump.CompleteAndDrainAsync());
        var report = AgenticSecurityGraph.Compute(
            graphStore.Read(TimeSpan.FromHours(1)),
            [Agent, Server],
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));

        Assert.Equal(SecurityGraphCoverageState.Complete, report.Coverage);
        Assert.Equal(1, report.TotalCallCount);
        Assert.Equal(1, report.TotalBlockedCallCount);
        Assert.Equal(1d, report.FleetBlockRate);
        Assert.Empty(report.NeverObservedNodes);
        Assert.DoesNotContain(
            rawSession,
            File.ReadAllText(Path.Combine(_directory, "complete.json")),
            StringComparison.Ordinal);

        using var containmentStore = new PromotionContainmentStore();
        var decision = SecurityGraphContainmentDecision.ForTenant(
            report,
            "cross_session_pattern",
            "evidence:graph-1");
        var applied = await new SecurityGraphContainmentBridge(
            "tenant-a",
            containmentStore).ApplyAsync(decision);
        Assert.Equal(
            ContainmentMutationDisposition.Applied,
            applied.Disposition);

        var exactSession =
            new ContainmentTarget.Session("tenant-a", "session-a");
        var identityVerdict = await Inspect(
            new ContainedIdentityGate(
                containmentStore,
                _ => [exactSession]));
        var toolVerdict = await Inspect(
            new ContainmentOverrideGate(
                containmentStore,
                _ => [exactSession]));

        Assert.Equal(GateAction.Block, identityVerdict.Action);
        Assert.Equal(ToolGateAction.Block, toolVerdict.Action);
        Assert.Equal(
            "contained_identity:active",
            identityVerdict.Reason);
        Assert.Equal(
            "containment_override:active",
            toolVerdict.Reason);
    }

    [Fact]
    public async Task DurableCoverageGap_WithholdsRateAndCannotCreateContainmentDecision()
    {
        using var graphStore = CreateGraphStore("incomplete.json");
        var appended = await graphStore.AppendAsync(
            new SecurityGraphObservationRequest(
                "event-1",
                Agent,
                Server,
                SecurityGraphSignalKind.CallBlocked,
                "session-a"));
        var gap = await graphStore.MarkCoverageGapAsync(
            SecurityGraphCoverageGap.Accepted(
                Now,
                "ingestion_failed"));

        Assert.Equal(
            SecurityGraphMutationDisposition.Applied,
            appended.Disposition);
        Assert.Equal(
            SecurityGraphMutationDisposition.Applied,
            gap.Disposition);

        var report = AgenticSecurityGraph.Compute(
            graphStore.Read(TimeSpan.FromHours(1)),
            [Agent, Server],
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));

        Assert.Equal(SecurityGraphCoverageState.Incomplete, report.Coverage);
        Assert.Null(report.FleetBlockRate);
        Assert.Throws<ArgumentException>(
            () => SecurityGraphContainmentDecision.ForTenant(
                report,
                "cross_session_pattern",
                "evidence:graph-2"));
    }

    private JsonFileSecurityGraphStore CreateGraphStore(string fileName)
        => new(
            Path.Combine(_directory, fileName),
            "tenant-a",
            "key-a",
            SessionKey,
            new JsonFileSecurityGraphStoreOptions
            {
                BootstrapIfMissing = true,
                Retention = TimeSpan.FromDays(30),
            },
            new FixedClock(Now));

    private static async Task<GateVerdict> Inspect(
        ContainedIdentityGate gate)
    {
        using var scope = AgentRunScope.Begin(
            new BagSession(),
            "agent-a",
            trace: null);
        return await gate.InspectAsync("input");
    }

    private static async Task<ToolGateVerdict> Inspect(
        ContainmentOverrideGate gate)
    {
        using var scope = AgentRunScope.Begin(
            new BagSession(),
            "agent-a",
            trace: null);
        return await gate.InspectAsync(
            new GatedToolCall(
                "tool-a",
                new Dictionary<string, object?>(),
                "agent-a",
                Iteration: 0,
                FunctionCallIndex: 0,
                FunctionCount: 1,
                IsStreaming: false,
                Messages: null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class BagSession : AgentSession;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PromotionContainmentStore : IContainmentStore
    {
        private readonly Dictionary<
            ContainmentTarget,
            ContainmentSnapshot> _snapshots = [];

        public ContainmentSnapshot GetCurrent(ContainmentTarget target)
            => _snapshots.TryGetValue(target, out var snapshot)
                ? snapshot
                : ContainmentSnapshot.NotContained(target);

        public ValueTask<ContainmentMutationResult> ContainAsync(
            ContainmentRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_snapshots.TryGetValue(
                    request.Target,
                    out var existing) &&
                existing.State == ContainmentSnapshotState.Active)
            {
                return ValueTask.FromResult(
                    ContainmentMutationResult.Unchanged(existing));
            }

            var snapshot = ContainmentSnapshot.FromRecord(
                new ContainmentRecord(
                    request.Target,
                    ContainmentStatus.Active,
                    Now,
                    releasedAtUtc: null,
                    request.ReasonCode,
                    request.EvidenceReference,
                    request.Issuer,
                    reviewer: null,
                    version: 1,
                    "etag-1"));
            _snapshots[request.Target] = snapshot;
            return ValueTask.FromResult(
                ContainmentMutationResult.Applied(snapshot));
        }

        public ValueTask<ContainmentMutationResult> ReleaseAsync(
            ContainmentReleaseAuthorization authorization,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
