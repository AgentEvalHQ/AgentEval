// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class SecurityGraphContainmentBridgeTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 15, 0, 0, TimeSpan.Zero);
    private static readonly SecurityGraphNode Agent =
        new(SecurityGraphNodeKind.Agent, "agent-a");
    private static readonly SecurityGraphNode Tool =
        new(SecurityGraphNodeKind.Tool, "tool-a");
    private static readonly SecurityGraphNode Server =
        new(SecurityGraphNodeKind.McpServer, "server-a");
    private static readonly SecurityGraphNode Endpoint =
        new(SecurityGraphNodeKind.AgentEndpoint, "endpoint-a");
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "agenteval-global-containment-" +
        Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(
        _directory,
        "containment.json");

    [Fact]
    public void TenantScope_IsCanonicalAndIsolatedByTenantAndKind()
    {
        var normalized = new ContainmentTarget.TenantScope(" tenant-a ");
        var canonical = new ContainmentTarget.TenantScope("tenant-a");

        Assert.Equal(canonical, normalized);
        Assert.Equal(
            ContainmentTargetKind.TenantScope,
            canonical.Kind);
        Assert.Equal(
            ContainmentTarget.TenantScope.GlobalIdentifier,
            canonical.Identifier);
        Assert.NotEqual(
            canonical,
            new ContainmentTarget.TenantScope("tenant-b"));
        Assert.False(
            canonical.Equals(
                new ContainmentTarget.Session("tenant-a", "global")));
        Assert.DoesNotContain(
            "tenant-a",
            canonical.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TenantScope_JsonRoundTripsAndUsesSignedRelease()
    {
        var target = new ContainmentTarget.TenantScope("tenant-a");
        using (var store = CreateJsonStore(bootstrap: true))
        {
            var contained = await store.ContainAsync(
                new ContainmentRequest(
                    target,
                    "cross_session_pattern",
                    "evidence:global-1",
                    "security-graph"));
            Assert.Equal(
                ContainmentMutationDisposition.Applied,
                contained.Disposition);
        }

        using (var reopened = CreateJsonStore(bootstrap: false))
        {
            Assert.Equal(
                ContainmentSnapshotState.Active,
                reopened.GetCurrent(target).State);
            var released = await reopened.ReleaseAsync(
                Authorization(target));
            Assert.Equal(
                ContainmentSnapshotState.Released,
                released.Snapshot.State);
        }

        using var final = CreateJsonStore(bootstrap: false);
        Assert.Equal(
            ContainmentSnapshotState.Released,
            final.GetCurrent(target).State);
    }

    [Fact]
    public async Task TenantScope_NonCanonicalDurableIdentifierFailsClosed()
    {
        using (var store = CreateJsonStore(bootstrap: true))
        {
            await store.ContainAsync(
                new ContainmentRequest(
                    new ContainmentTarget.TenantScope("tenant-a"),
                    "cross_session_pattern",
                    "evidence:global-1",
                    "security-graph"));
        }

        var json = File.ReadAllText(StorePath);
        var modified = json.Replace(
            "\"identifier\":\"global\"",
            "\"identifier\":\"not-global\"",
            StringComparison.Ordinal);
        Assert.NotEqual(json, modified);
        File.WriteAllText(StorePath, modified);

        Assert.Throws<InvalidOperationException>(
            () => CreateJsonStore(bootstrap: false));
    }

    [Fact]
    public async Task CompleteTenantDecision_IsPersistedAndBlocksBothPhase3Gates()
    {
        using var store = new MemoryContainmentStore();
        var decision = SecurityGraphContainmentDecision.ForTenant(
            CompleteReport(Server),
            "cross_session_pattern",
            "evidence:global-1");
        var bridge = new SecurityGraphContainmentBridge(
            "tenant-a",
            store);

        var applied = await bridge.ApplyAsync(decision);
        var replay = await bridge.ApplyAsync(decision);

        Assert.Equal(
            ContainmentMutationDisposition.Applied,
            applied.Disposition);
        Assert.Equal(
            ContainmentMutationDisposition.Unchanged,
            replay.Disposition);
        Assert.Equal(
            "security-graph",
            applied.Snapshot.Record!.Issuer);
        Assert.IsType<ContainmentTarget.TenantScope>(
            applied.Snapshot.Target);

        var identity = new ContainedIdentityGate(
            store,
            _ => [Session()]);
        var identityVerdict = await Inspect(identity);
        Assert.Equal(GateAction.Block, identityVerdict.Action);
        Assert.Equal(
            "contained_identity:active",
            identityVerdict.Reason);

        var tool = new ContainmentOverrideGate(
            store,
            _ => [Session()]);
        var toolVerdict = await Inspect(tool);
        Assert.Equal(ToolGateAction.Block, toolVerdict.Action);
        Assert.Equal(
            "containment_override:active",
            toolVerdict.Reason);
    }

    [Theory]
    [InlineData(SecurityGraphNodeKind.McpServer)]
    [InlineData(SecurityGraphNodeKind.AgentEndpoint)]
    public async Task ObservedEnforceableNodeDecision_MapsToExactPhase3Target(
        SecurityGraphNodeKind kind)
    {
        var node = kind == SecurityGraphNodeKind.McpServer
            ? Server
            : Endpoint;
        using var store = new MemoryContainmentStore();
        var decision = SecurityGraphContainmentDecision.ForNode(
            CompleteReport(node),
            node,
            "cross_session_pattern",
            "evidence:node-1");
        var bridge = new SecurityGraphContainmentBridge(
            "tenant-a",
            store);

        var result = await bridge.ApplyAsync(decision);

        Assert.Equal(
            ContainmentSnapshotState.Active,
            result.Snapshot.State);
        Assert.Equal(node.Identifier, result.Snapshot.Target.Identifier);
        Assert.Equal(
            kind == SecurityGraphNodeKind.McpServer
                ? ContainmentTargetKind.McpServer
                : ContainmentTargetKind.AgentEndpoint,
            result.Snapshot.Target.Kind);
    }

    [Fact]
    public void DecisionFactories_RejectIncompleteEmptyMissingAndUnenforceableFacts()
    {
        var incomplete = AgenticSecurityGraph.Compute(
            SecurityGraphTenantSnapshot.Determinate(
                "tenant-a",
                TimeSpan.FromHours(1),
                Now,
                [Observation(Server)],
                [SecurityGraphCoverageGap.Accepted(
                    Now,
                    "queue_full")]),
            [Agent, Server],
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));
        Assert.Throws<ArgumentException>(
            () => SecurityGraphContainmentDecision.ForTenant(
                incomplete,
                "cross_session_pattern",
                "evidence:global-1"));

        var indeterminate = AgenticSecurityGraph.Compute(
            SecurityGraphTenantSnapshot.Indeterminate(
                "tenant-a",
                TimeSpan.FromHours(1),
                Now,
                "store_unavailable"),
            [Server],
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));
        Assert.Throws<ArgumentException>(
            () => SecurityGraphContainmentDecision.ForTenant(
                indeterminate,
                "cross_session_pattern",
                "evidence:global-1"));

        var empty = AgenticSecurityGraph.Compute(
            SecurityGraphTenantSnapshot.Determinate(
                "tenant-a",
                TimeSpan.FromHours(1),
                Now,
                observations: []),
            [Server],
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));
        Assert.Throws<ArgumentException>(
            () => SecurityGraphContainmentDecision.ForTenant(
                empty,
                "cross_session_pattern",
                "evidence:global-1"));
        Assert.Throws<ArgumentException>(
            () => SecurityGraphContainmentDecision.ForNode(
                empty,
                Server,
                "cross_session_pattern",
                "evidence:node-1"));

        Assert.Throws<ArgumentException>(
            () => SecurityGraphContainmentDecision.ForNode(
                CompleteReport(Tool),
                Tool,
                "cross_session_pattern",
                "evidence:node-1"));
        Assert.Throws<ArgumentException>(
            () => SecurityGraphContainmentDecision.ForNode(
                CompleteReport(Agent),
                Agent,
                "cross_session_pattern",
                "evidence:node-1"));
    }

    [Fact]
    public async Task Bridge_RejectsCrossTenantDecisionBeforeStoreMutation()
    {
        using var store = new MemoryContainmentStore();
        var bridge = new SecurityGraphContainmentBridge(
            "tenant-b",
            store);
        var decision = SecurityGraphContainmentDecision.ForTenant(
            CompleteReport(Server),
            "cross_session_pattern",
            "evidence:global-1");

        await Assert.ThrowsAsync<ArgumentException>(
            () => bridge.ApplyAsync(decision).AsTask());

        Assert.Equal(0, store.ContainCalls);
    }

    [Fact]
    public async Task Bridge_RejectsConflictThatDoesNotProveContainment()
    {
        using var store = new ConflictContainmentStore();
        var bridge = new SecurityGraphContainmentBridge(
            "tenant-a",
            store);
        var decision = SecurityGraphContainmentDecision.ForTenant(
            CompleteReport(Server),
            "cross_session_pattern",
            "evidence:global-1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bridge.ApplyAsync(decision).AsTask());
    }

    [Fact]
    public async Task Bridge_PreservesIndeterminateContainmentResult()
    {
        using var store = new IndeterminateContainmentStore();
        var bridge = new SecurityGraphContainmentBridge(
            "tenant-a",
            store);
        var decision = SecurityGraphContainmentDecision.ForTenant(
            CompleteReport(Server),
            "cross_session_pattern",
            "evidence:global-1");

        var result = await bridge.ApplyAsync(decision);

        Assert.Equal(
            ContainmentMutationDisposition.Indeterminate,
            result.Disposition);
        Assert.Equal(
            ContainmentSnapshotState.Indeterminate,
            result.Snapshot.State);
    }

    [Fact]
    public async Task ReleasedOrNotContainedTenantScope_AllowsBothPhase3Gates()
    {
        foreach (var released in new[] { false, true })
        {
            using var store = new MemoryContainmentStore();
            if (released)
            {
                store.Set(Record(
                    new ContainmentTarget.TenantScope("tenant-a"),
                    ContainmentStatus.Released));
            }

            var identity = new ContainedIdentityGate(
                store,
                _ => [Session()]);
            var tool = new ContainmentOverrideGate(
                store,
                _ => [Session()]);

            Assert.Equal(
                GateAction.Allow,
                (await Inspect(identity)).Action);
            Assert.Equal(
                ToolGateAction.Allow,
                (await Inspect(tool)).Action);
        }
    }

    [Fact]
    public async Task GateEvaluation_ChecksTenantScopeOnceAndIsolatesTenants()
    {
        var tenantAScope =
            new ContainmentTarget.TenantScope("tenant-a");
        using var store = new MemoryContainmentStore();
        store.Set(Record(
            tenantAScope,
            ContainmentStatus.Active));
        var gate = new ContainmentOverrideGate(
            store,
            _ => [Session("tenant-b")],
            _ => [
                new ContainmentTarget.McpServer(
                    "tenant-b",
                    "server-b"),
            ]);

        var verdict = await Inspect(gate);

        Assert.Equal(ToolGateAction.Allow, verdict.Action);
        Assert.Equal(
            1,
            store.Reads.Count(target =>
                target is ContainmentTarget.TenantScope &&
                target.Tenant == "tenant-b"));
        Assert.DoesNotContain(tenantAScope, store.Reads);
    }

    [Fact]
    public async Task IndeterminateTenantScope_BlocksBothPhase3Gates()
    {
        using var store = new IndeterminateTenantScopeStore();
        var identity = new ContainedIdentityGate(
            store,
            _ => [Session()]);
        var tool = new ContainmentOverrideGate(
            store,
            _ => [Session()]);

        var identityVerdict = await Inspect(identity);
        var toolVerdict = await Inspect(tool);

        Assert.Equal(GateAction.Block, identityVerdict.Action);
        Assert.Equal(
            "contained_identity:indeterminate",
            identityVerdict.Reason);
        Assert.Equal(ToolGateAction.Block, toolVerdict.Action);
        Assert.Equal(
            "containment_override:indeterminate",
            toolVerdict.Reason);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private JsonFileContainmentStore CreateJsonStore(bool bootstrap)
        => new(
            StorePath,
            new AcceptingVerifier(),
            new JsonFileContainmentStoreOptions
            {
                BootstrapIfMissing = bootstrap,
            },
            new FixedClock(Now));

    private static SecurityGraphReport CompleteReport(
        SecurityGraphNode destination)
    {
        var snapshot = SecurityGraphTenantSnapshot.Determinate(
            "tenant-a",
            TimeSpan.FromHours(1),
            Now,
            [Observation(destination)]);
        SecurityGraphNode[] expectedNodes = destination == Agent
            ? [Agent]
            : [Agent, destination];
        return AgenticSecurityGraph.Compute(
            snapshot,
            expectedNodes,
            TimeSpan.FromMinutes(30),
            new FixedClock(Now));
    }

    private static SecurityGraphObservation Observation(
        SecurityGraphNode destination)
        => new(
            "event-1",
            Now,
            Agent,
            destination,
            SecurityGraphSignalKind.CallBlocked,
            Digest("session-a"),
            "evidence:incident-1");

    private static string Digest(string value)
        => Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(value)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static ContainmentTarget Session(
        string tenant = "tenant-a")
        => new ContainmentTarget.Session(tenant, "session-a");

    private static ContainmentRecord Record(
        ContainmentTarget target,
        ContainmentStatus status)
        => new(
            target,
            status,
            Now,
            status == ContainmentStatus.Released
                ? Now.AddMinutes(1)
                : null,
            "cross_session_pattern",
            "evidence:global-1",
            "security-graph",
            status == ContainmentStatus.Released
                ? "operator-a"
                : null,
            version: 1,
            "etag-1");

    private static ContainmentReleaseAuthorization Authorization(
        ContainmentTarget target)
        => new(
            target,
            expectedVersion: 1,
            "operator-a",
            Now.AddMinutes(-1),
            Now.AddMinutes(5),
            "release-nonce-0001",
            "test",
            algorithmVersion: 1,
            "key-a",
            "valid-signature-0001");

    private static async Task<GateVerdict> Inspect(
        ContainedIdentityGate gate)
    {
        using var scope = AgentRunScope.Begin(
            new BagSession(),
            "agent",
            trace: null);
        return await gate.InspectAsync("input");
    }

    private static async Task<ToolGateVerdict> Inspect(
        ContainmentOverrideGate gate)
    {
        using var scope = AgentRunScope.Begin(
            new BagSession(),
            "agent",
            trace: null);
        return await gate.InspectAsync(
            new GatedToolCall(
                "tool",
                new Dictionary<string, object?>(),
                "agent",
                Iteration: 0,
                FunctionCallIndex: 0,
                FunctionCount: 1,
                IsStreaming: false,
                Messages: null));
    }

    private sealed class BagSession : AgentSession;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AcceptingVerifier :
        IContainmentReleaseAuthorizationVerifier
    {
        public bool Verify(
            ContainmentReleaseAuthorization authorization,
            ReadOnlyMemory<byte> canonicalPayload)
            => !canonicalPayload.IsEmpty;
    }

    private sealed class MemoryContainmentStore : IContainmentStore
    {
        private readonly Dictionary<
            ContainmentTarget,
            ContainmentSnapshot> _snapshots = [];

        public List<ContainmentTarget> Reads { get; } = [];

        public int ContainCalls { get; private set; }

        public ContainmentSnapshot GetCurrent(
            ContainmentTarget target)
        {
            Reads.Add(target);
            return _snapshots.TryGetValue(target, out var snapshot)
                ? snapshot
                : ContainmentSnapshot.NotContained(target);
        }

        public ValueTask<ContainmentMutationResult> ContainAsync(
            ContainmentRequest request,
            CancellationToken cancellationToken = default)
        {
            ContainCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (_snapshots.TryGetValue(
                    request.Target,
                    out var existing) &&
                existing.State == ContainmentSnapshotState.Active)
            {
                return ValueTask.FromResult(
                    ContainmentMutationResult.Unchanged(existing));
            }

            var record = new ContainmentRecord(
                request.Target,
                ContainmentStatus.Active,
                Now,
                releasedAtUtc: null,
                request.ReasonCode,
                request.EvidenceReference,
                request.Issuer,
                reviewer: null,
                version: 1,
                "etag-1");
            var snapshot = ContainmentSnapshot.FromRecord(record);
            _snapshots[request.Target] = snapshot;
            return ValueTask.FromResult(
                ContainmentMutationResult.Applied(snapshot));
        }

        public ValueTask<ContainmentMutationResult> ReleaseAsync(
            ContainmentReleaseAuthorization authorization,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Set(ContainmentRecord record)
            => _snapshots[record.Target] =
                ContainmentSnapshot.FromRecord(record);

        public void Dispose()
        {
        }
    }

    private sealed class ConflictContainmentStore : IContainmentStore
    {
        public ContainmentSnapshot GetCurrent(
            ContainmentTarget target)
            => ContainmentSnapshot.NotContained(target);

        public ValueTask<ContainmentMutationResult> ContainAsync(
            ContainmentRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                ContainmentMutationResult.Conflict(
                    ContainmentSnapshot.NotContained(request.Target)));

        public ValueTask<ContainmentMutationResult> ReleaseAsync(
            ContainmentReleaseAuthorization authorization,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class IndeterminateTenantScopeStore :
        IContainmentStore
    {
        public ContainmentSnapshot GetCurrent(
            ContainmentTarget target)
            => target is ContainmentTarget.TenantScope
                ? ContainmentSnapshot.Indeterminate(
                    target,
                    "store_unavailable")
                : ContainmentSnapshot.NotContained(target);

        public ValueTask<ContainmentMutationResult> ContainAsync(
            ContainmentRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<ContainmentMutationResult> ReleaseAsync(
            ContainmentReleaseAuthorization authorization,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class IndeterminateContainmentStore :
        IContainmentStore
    {
        public ContainmentSnapshot GetCurrent(
            ContainmentTarget target)
            => ContainmentSnapshot.Indeterminate(
                target,
                "store_unavailable");

        public ValueTask<ContainmentMutationResult> ContainAsync(
            ContainmentRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                ContainmentMutationResult.Indeterminate(
                    ContainmentSnapshot.Indeterminate(
                        request.Target,
                        "store_unavailable")));

        public ValueTask<ContainmentMutationResult> ReleaseAsync(
            ContainmentReleaseAuthorization authorization,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
