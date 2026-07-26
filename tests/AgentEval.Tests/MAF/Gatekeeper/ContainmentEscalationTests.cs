// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 3, Task 3.4 — one-shot block-storm escalation into durable containment.</summary>
public sealed class ContainmentEscalationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AutomaticEscalation_ThresholdCrossingContainsStableSessionAndBlocks()
    {
        var currentTarget = Session("stable-session");
        var priorTarget = Session("prior-session");
        var store = new FakeStore();
        var gate = Build(store, _ => [currentTarget, priorTarget], threshold: 2);
        using var scope = AgentRunScope.Begin(new BagSession(), "agent", trace: null);
        RecordTreeDenials(2);

        var verdict = await gate.InspectAsync(Call());

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Equal("BlockStormSentinel", verdict.PolicyName);
        var request = Assert.Single(store.Requests);
        Assert.Equal(currentTarget, request.Target);
        Assert.Equal("block_storm", request.ReasonCode);
        Assert.Equal("gatekeeper", request.Issuer);
        Assert.Contains($"incidentRef={request.EvidenceReference}", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("callback=completed", verdict.Reason, StringComparison.Ordinal);
        Assert.Equal(ContainmentSnapshotState.Active, store.GetCurrent(currentTarget).State);

        var containedVerdict = await gate.InspectAsync(Call());
        Assert.Equal("ContainmentOverrideGate", containedVerdict.PolicyName);
        Assert.Single(store.Requests);
    }

    [Fact]
    public async Task AutomaticEscalation_BelowThresholdAllowsWithoutMutation()
    {
        var store = new FakeStore();
        var gate = Build(store, _ => [Session()], threshold: 3);
        using var scope = AgentRunScope.Begin(new BagSession(), "agent", trace: null);
        RecordTreeDenials(2);

        var verdict = await gate.InspectAsync(Call());

        Assert.Equal(ToolGateAction.Allow, verdict.Action);
        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task AutomaticEscalation_NestedRunUsesRootStableTargetNeverRunId()
    {
        var rootSession = new BagSession();
        var childSession = new BagSession();
        var rootTarget = Session("root-stable-session");
        var childTarget = Session("child-stable-session");
        var store = new FakeStore();
        var gate = Build(
            store,
            session => ReferenceEquals(session, rootSession) ? [rootTarget] : [childTarget],
            threshold: 1);
        using var root = AgentRunScope.Begin(rootSession, "root-agent", trace: null);
        RecordTreeDenials(1);
        using var child = AgentRunScope.Begin(childSession, "child-agent", trace: null);

        var verdict = await gate.InspectAsync(Call());

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        var request = Assert.Single(store.Requests);
        Assert.Equal(rootTarget, request.Target);
        Assert.NotEqual(root.RunId, request.Target.Identifier);
        Assert.NotEqual(child.RunId, request.Target.Identifier);
        Assert.NotEqual(root.RunId, request.EvidenceReference);
    }

    [Fact]
    public async Task AutomaticEscalation_StoreThrowStillBlocksWithOpaqueIncidentReference()
    {
        var store = new FakeStore
        {
            OnContain = (_, _) => throw new InvalidOperationException("secret persistence path"),
        };
        var gate = Build(store, _ => [Session()], threshold: 1);
        using var scope = AgentRunScope.Begin(new BagSession(), "agent", trace: null);
        RecordTreeDenials(1);

        var verdict = await gate.InspectAsync(Call());

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Equal("BlockStormSentinel", verdict.PolicyName);
        Assert.Contains("callback=failed", verdict.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", verdict.Reason, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"incidentRef=[A-Za-z0-9._:-]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            verdict.Reason);
        Assert.Single(store.Requests);
    }

    [Fact]
    public async Task AutomaticEscalation_IndeterminateOutcomeStillBlocks()
    {
        var target = Session();
        var store = new FakeStore
        {
            OnContain = (request, _) => new ValueTask<ContainmentMutationResult>(
                ContainmentMutationResult.Indeterminate(
                    ContainmentSnapshot.Indeterminate(request.Target, "persistence_failed"))),
        };
        var gate = Build(store, _ => [target], threshold: 1);
        using var scope = AgentRunScope.Begin(new BagSession(), "agent", trace: null);
        RecordTreeDenials(1);

        var verdict = await gate.InspectAsync(Call());

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Contains("callback=failed", verdict.Reason, StringComparison.Ordinal);
        Assert.Single(store.Requests);
    }

    [Fact]
    public async Task AutomaticEscalation_InvalidRootTargetBlocksWithoutStoreMutation()
    {
        var store = new FakeStore();
        var gate = Build(
            store,
            _ => [new ContainmentTarget.McpServer("tenant-a", "server-a")],
            threshold: 1);
        using var scope = AgentRunScope.Begin(new BagSession(), "agent", trace: null);
        RecordTreeDenials(1);

        var verdict = await gate.InspectAsync(Call());

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Contains("callback=failed", verdict.Reason, StringComparison.Ordinal);
        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task AutomaticEscalation_ConcurrentCallsInvokeContainOnce()
    {
        var store = new FakeStore();
        var gate = Build(store, _ => [Session()], threshold: 1);
        using var scope = AgentRunScope.Begin(new BagSession(), "agent", trace: null);
        RecordTreeDenials(1);

        var verdicts = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => gate.InspectAsync(Call()).AsTask()));

        Assert.All(verdicts, verdict => Assert.Equal(ToolGateAction.Block, verdict.Action));
        Assert.Single(store.Requests);
    }

    [Fact]
    public async Task AutomaticEscalation_PreCancelledCallDoesNotMutate()
    {
        var store = new FakeStore();
        var gate = Build(store, _ => [Session()], threshold: 1);
        using var scope = AgentRunScope.Begin(new BagSession(), "agent", trace: null);
        RecordTreeDenials(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => gate.InspectAsync(Call(), cancellation.Token).AsTask());

        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task LivePipeline_FirstEnforcedBlockFeedsContainmentBeforeRetry()
    {
        var target = Session();
        var store = new FakeStore();
        var executed = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref executed);
                return "executed";
            },
            "dangerous");
        var scripted = new ScriptedChatClient()
            .AddToolCall("call-1", "dangerous", new Dictionary<string, object?>())
            .AddToolCall("call-2", "dangerous", new Dictionary<string, object?>())
            .AddText("done");
        var agent = new ChatClientAgent(
            scripted,
            new ChatClientAgentOptions
            {
                Name = "agent",
                ChatOptions = new ChatOptions { Tools = [tool] },
            });
        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
            {
                options.ContainmentStore = store;
                options.ContainmentTargets = _ => [target];
                options.ContainmentRetryThreshold = 1;
                options.Add(new ForbiddenToolGate("dangerous"));
            })
            .Build();
        var session = await gated.CreateSessionAsync();

        await gated.RunAsync("go", session);

        Assert.Equal(0, executed);
        Assert.Single(store.Requests);
        Assert.Equal(ContainmentSnapshotState.Active, store.GetCurrent(target).State);
    }

    private static ContainmentOverrideGate Build(
        IContainmentStore store,
        Func<AgentSession, IReadOnlyList<ContainmentTarget>> targets,
        int threshold)
    {
        GatekeeperOptions? captured = null;
        NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
        {
            options.ContainmentStore = store;
            options.ContainmentTargets = targets;
            options.ContainmentRetryThreshold = threshold;
            captured = options;
        });

        return Assert.IsType<ContainmentOverrideGate>(captured!.ToolGates[0]);
    }

    private static void RecordTreeDenials(int count)
    {
        for (var index = 0; index < count; index++)
        {
            RunLedger.ForRootRun().RecordTreeDenial();
        }
    }

    private static GatedToolCall Call()
        => new(
            "tool",
            new Dictionary<string, object?>(),
            "agent",
            Iteration: 0,
            FunctionCallIndex: 0,
            FunctionCount: 1,
            IsStreaming: false,
            Messages: null);

    private static ContainmentTarget.Session Session(string identifier = "stable-session")
        => new("tenant-a", identifier);

    private static ChatClientAgent NewAgent()
        => new(
            new ScriptedChatClient().AddText("done"),
            new ChatClientAgentOptions { Name = "agent" });

    private sealed class BagSession : AgentSession;

    private sealed class FakeStore : IContainmentStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<ContainmentTarget, ContainmentSnapshot> _snapshots = [];
        private readonly List<ContainmentRequest> _requests = [];

        public Func<ContainmentRequest, CancellationToken, ValueTask<ContainmentMutationResult>>? OnContain { get; init; }

        public IReadOnlyList<ContainmentRequest> Requests
        {
            get { lock (_lock) { return [.. _requests]; } }
        }

        public ContainmentSnapshot GetCurrent(ContainmentTarget target)
        {
            lock (_lock)
            {
                return _snapshots.TryGetValue(target, out var snapshot)
                    ? snapshot
                    : ContainmentSnapshot.NotContained(target);
            }
        }

        public async ValueTask<ContainmentMutationResult> ContainAsync(
            ContainmentRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _requests.Add(request);
            }

            if (OnContain is not null)
            {
                return await OnContain(request, cancellationToken);
            }

            lock (_lock)
            {
                if (_snapshots.TryGetValue(request.Target, out var current)
                    && current.State == ContainmentSnapshotState.Active)
                {
                    return ContainmentMutationResult.Unchanged(current);
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
                        etag: "etag-1"));
                _snapshots[request.Target] = snapshot;
                return ContainmentMutationResult.Applied(snapshot);
            }
        }

        public ValueTask<ContainmentMutationResult> ReleaseAsync(
            ContainmentReleaseAuthorization authorization,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() { }
    }
}
