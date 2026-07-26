// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 3, Task 3.3 — absolute-first containment and identity admission gates.</summary>
public sealed class ContainmentGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OverrideGate_DeclaresBoundedRunScopedEnforcementFloor()
    {
        var gate = Override(new FakeStore());

        Assert.Equal("ContainmentOverrideGate", gate.PolicyName);
        Assert.Equal(GateCost.Bounded, gate.Cost);
        Assert.Equal(GateRequirements.RunScope, gate.Requirements);
        Assert.Equal(ToolGatePolicy.ReplaceResult, gate.MinimumPolicy);
    }

    [Fact]
    public async Task OverrideGate_AllowsOnlyWhenEveryTargetIsNotContainedOrReleased()
    {
        var sessionTarget = Session();
        var releasedTarget = Server();
        var store = new FakeStore(target => target == releasedTarget
            ? ContainmentSnapshot.FromRecord(Record(releasedTarget, ContainmentStatus.Released))
            : ContainmentSnapshot.NotContained(target));
        var gate = Override(
            store,
            _ => [sessionTarget],
            _ => [releasedTarget]);

        var verdict = await Inspect(gate);

        Assert.Equal(ToolGateAction.Allow, verdict.Action);
        Assert.Equal(2, store.Reads.Count);
        Assert.Contains(sessionTarget, store.Reads);
        Assert.Contains(releasedTarget, store.Reads);
    }

    [Fact]
    public async Task OverrideGate_BlocksActiveSessionBeforeReadingLaterCallTargets()
    {
        var sessionTarget = Session();
        var serverTarget = Server();
        var store = new FakeStore(target => target == sessionTarget
            ? ContainmentSnapshot.FromRecord(Record(target, ContainmentStatus.Active))
            : ContainmentSnapshot.NotContained(target));
        var gate = Override(store, _ => [sessionTarget], _ => [serverTarget]);

        var verdict = await Inspect(gate);

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Equal("containment_override:active", verdict.Reason);
        Assert.Equal([sessionTarget], store.Reads);
        Assert.DoesNotContain("tenant-a", verdict.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("session-a", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverrideGate_BlocksIndeterminateAdditionalTarget()
    {
        var serverTarget = Server();
        var store = new FakeStore(target => target == serverTarget
            ? ContainmentSnapshot.Indeterminate(target, "reload_failed")
            : ContainmentSnapshot.NotContained(target));
        var gate = Override(store, _ => [Session()], _ => [serverTarget]);

        var verdict = await Inspect(gate);

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Equal("containment_override:indeterminate", verdict.Reason);
        Assert.DoesNotContain("reload_failed", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverrideGate_DeduplicatesTargetsAcrossSessionAndCallResolvers()
    {
        var target = Session();
        var store = new FakeStore();
        var gate = Override(store, _ => [target], _ => [target]);

        var verdict = await Inspect(gate);

        Assert.Equal(ToolGateAction.Allow, verdict.Action);
        Assert.Equal([target], store.Reads);
    }

    [Fact]
    public async Task OverrideGate_MissingRunScopeOrSessionFailsClosed()
    {
        var gate = Override(new FakeStore());

        var withoutScope = await gate.InspectAsync(Call());
        using var scopeWithoutSession = AgentRunScope.Begin(session: null, agentName: "agent", trace: null);
        var withoutSession = await gate.InspectAsync(Call());

        Assert.Equal(ToolGateAction.Block, withoutScope.Action);
        Assert.Equal("containment_override:session_context_unavailable", withoutScope.Reason);
        Assert.Equal(ToolGateAction.Block, withoutSession.Action);
    }

    [Fact]
    public async Task OverrideGate_ResolverFailuresAndInvalidCollectionsFailClosed()
    {
        var target = Session();
        var cases = new Func<AgentSession, IReadOnlyList<ContainmentTarget>>[]
        {
            _ => throw new InvalidOperationException("secret resolver failure"),
            _ => null!,
            _ => [],
            _ => new ThrowingTargetList(),
            _ => Enumerable.Range(0, 17)
                .Select(index => (ContainmentTarget)new ContainmentTarget.Session("tenant", $"session-{index}"))
                .ToArray(),
            _ => [target, null!],
        };

        foreach (var resolver in cases)
        {
            var verdict = await Inspect(Override(new FakeStore(), resolver));
            Assert.Equal(ToolGateAction.Block, verdict.Action);
            Assert.Equal("containment_override:target_resolution_failed", verdict.Reason);
            Assert.DoesNotContain("secret", verdict.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OverrideGate_StoreFailureOrMismatchedSnapshotFailsClosed()
    {
        var target = Session();
        var throwing = new FakeStore(_ => throw new InvalidOperationException("secret store path"));
        var mismatched = new FakeStore(_ => ContainmentSnapshot.NotContained(Server()));

        var throwingVerdict = await Inspect(Override(throwing, _ => [target]));
        var mismatchedVerdict = await Inspect(Override(mismatched, _ => [target]));

        Assert.Equal("containment_override:store_read_failed", throwingVerdict.Reason);
        Assert.Equal("containment_override:store_snapshot_invalid", mismatchedVerdict.Reason);
        Assert.DoesNotContain("secret", throwingVerdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverrideGate_CancellationPropagatesWithoutStoreRead()
    {
        var store = new FakeStore();
        var gate = Override(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Inspect(gate, cancellation.Token));

        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task IdentityGate_BlocksWhenAnyIdentityLinkedExactTargetIsActive()
    {
        var current = Session(identifier: "new-session");
        var prior = Session(identifier: "prior-session");
        var store = new FakeStore(target => target == prior
            ? ContainmentSnapshot.FromRecord(Record(target, ContainmentStatus.Active))
            : ContainmentSnapshot.NotContained(target));
        var gate = new ContainedIdentityGate(store, _ => [current, prior]);

        var verdict = await Inspect(gate);

        Assert.Equal(GateAction.Block, verdict.Action);
        Assert.Equal("contained_identity:active", verdict.Reason);
        Assert.Null(verdict.RedactedText);
    }

    [Fact]
    public async Task IdentityGate_AllowsCleanAndReleasedIdentityTargets()
    {
        var current = Session(identifier: "new-session");
        var prior = Session(identifier: "prior-session");
        var store = new FakeStore(target => target == prior
            ? ContainmentSnapshot.FromRecord(Record(target, ContainmentStatus.Released))
            : ContainmentSnapshot.NotContained(target));
        var gate = new ContainedIdentityGate(store, _ => [current, prior]);

        var verdict = await Inspect(gate);

        Assert.Equal(GateAction.Allow, verdict.Action);
    }

    [Fact]
    public async Task IdentityGate_IndeterminateAndResolverFailureBlock()
    {
        var target = Session();
        var indeterminate = new ContainedIdentityGate(
            new FakeStore(value => ContainmentSnapshot.Indeterminate(value, "store_failed")),
            _ => [target]);
        var resolverFailure = new ContainedIdentityGate(
            new FakeStore(),
            _ => throw new InvalidOperationException("secret"));

        Assert.Equal(GateAction.Block, (await Inspect(indeterminate)).Action);
        var failure = await Inspect(resolverFailure);
        Assert.Equal(GateAction.Block, failure.Action);
        Assert.Equal("contained_identity:target_resolution_failed", failure.Reason);
    }

    [Fact]
    public void UseGatekeeper_HalfConfiguredContainmentFailsBeforeReservedGatesAreAdded()
    {
        GatekeeperOptions? storeOnly = null;
        var exception = Assert.Throws<InvalidOperationException>(() =>
            NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
            {
                storeOnly = options;
                options.ContainmentStore = new FakeStore();
            }));

        Assert.Contains("configured together", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(storeOnly!.ToolGates, gate => gate is ContainmentOverrideGate);
        Assert.DoesNotContain(storeOnly.PreGates, gate => gate is ContainedIdentityGate);

        Assert.Throws<InvalidOperationException>(() =>
            NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
                options.ContainmentTargets = _ => [Session()]));
        Assert.Throws<InvalidOperationException>(() =>
            NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
                options.AdditionalContainmentTargets = _ => [Server()]));
    }

    [Fact]
    public void UseGatekeeper_AutomaticWiringReservesAbsoluteSlots()
    {
        GatekeeperOptions? captured = null;
        NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
        {
            options.Add(new AllowToolGate());
            options.AddPreGate(new AllowChatGate());
            options.Contract("tool", contract => contract.DeniedKeywords("value", "forbidden"));
            options.ContainmentStore = new FakeStore();
            options.ContainmentTargets = _ => [Session()];
            options.AdditionalContainmentTargets = _ => [Server()];
            captured = options;
        });

        Assert.Collection(
            captured!.ToolGates,
            gate => Assert.IsType<ContainmentOverrideGate>(gate),
            gate => Assert.IsType<ToolUsageContractGate>(gate),
            gate => Assert.IsType<AllowToolGate>(gate));
        Assert.Collection(
            captured.PreGates,
            gate => Assert.IsType<ContainedIdentityGate>(gate),
            gate => Assert.IsType<AllowChatGate>(gate));
    }

    [Fact]
    public void UseGatekeeper_DirectGatesAreNormalizedToReservedSlots()
    {
        GatekeeperOptions? captured = null;
        var store = new FakeStore();
        var directOverride = Override(store);
        var directIdentity = new ContainedIdentityGate(store, _ => [Session()]);

        NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            options.Add(new AllowToolGate());
            options.Add(directOverride);
            options.AddPreGate(new AllowChatGate());
            options.AddPreGate(directIdentity);
            captured = options;
        });

        Assert.Same(directOverride, captured!.ToolGates[0]);
        Assert.Same(directIdentity, captured.PreGates[0]);
    }

    [Fact]
    public void UseGatekeeper_DuplicateMixedOrMisplacedContainmentGatesFail()
    {
        var store = new FakeStore();

        Assert.Throws<InvalidOperationException>(() =>
            NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.Add(Override(store));
                options.Add(Override(store));
            }));

        Assert.Throws<InvalidOperationException>(() =>
            NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.ContainmentStore = store;
                options.ContainmentTargets = _ => [Session()];
                options.Add(Override(store));
            }));

        Assert.Throws<InvalidOperationException>(() =>
            NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
                options.AddPostGate(new ContainedIdentityGate(store, _ => [Session()]))));
    }

    [Fact]
    public void UseGatekeeper_AutomaticContainmentUnderObserveFailsEnforcementFloor()
    {
        using var writer = new StringWriter();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Observe, options =>
            {
                options.ContainmentStore = new FakeStore();
                options.ContainmentTargets = _ => [Session()];
                options.Trace = new AgentTrace();
                options.BannerWriter = writer;
            }));

        Assert.Contains("ContainmentOverrideGate", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MinimumPolicy", exception.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public async Task LiveToolPipeline_OverrideShortCircuitsBeforeOrdinaryGateAndTool()
    {
        var target = Session();
        var store = new FakeStore(value => ContainmentSnapshot.FromRecord(Record(value, ContainmentStatus.Active)));
        var overrideGate = Override(store, _ => [target]);
        var ordinaryGate = new CountingToolGate();
        var executed = 0;
        var tool = AIFunctionFactory.Create(() =>
        {
            executed++;
            return "executed";
        }, "dangerous");
        var scripted = new ScriptedChatClient()
            .AddToolCall("call-1", "dangerous", new Dictionary<string, object?>())
            .AddText("done");
        var inner = new ChatClientAgent(
            scripted,
            new ChatClientAgentOptions
            {
                Name = "agent",
                ChatOptions = new ChatOptions { Tools = [tool] },
            });
        var gated = inner.AsBuilder()
            .UseAgentEvalGate()
            .UseAgentEvalToolGate(
                [overrideGate, ordinaryGate],
                ToolGatePolicy.ReplaceResult)
            .Build();
        var session = await gated.CreateSessionAsync();

        await gated.RunAsync("go", session);

        Assert.Equal(0, ordinaryGate.Calls);
        Assert.Equal(0, executed);
    }

    private static async Task<ToolGateVerdict> Inspect(
        ContainmentOverrideGate gate,
        CancellationToken cancellationToken = default)
    {
        using var scope = AgentRunScope.Begin(new BagSession(), "agent", trace: null);
        return await gate.InspectAsync(Call(), cancellationToken);
    }

    private static async Task<GateVerdict> Inspect(ContainedIdentityGate gate)
    {
        using var scope = AgentRunScope.Begin(new BagSession(), "agent", trace: null);
        return await gate.InspectAsync("input");
    }

    private static ContainmentOverrideGate Override(
        IContainmentStore store,
        Func<AgentSession, IReadOnlyList<ContainmentTarget>>? sessionTargets = null,
        Func<GatedToolCall, IReadOnlyList<ContainmentTarget>>? additionalTargets = null)
        => new(
            store,
            sessionTargets ?? (_ => [Session()]),
            additionalTargets);

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

    private static ContainmentTarget Session(
        string tenant = "tenant-a",
        string identifier = "session-a")
        => new ContainmentTarget.Session(tenant, identifier);

    private static ContainmentTarget Server()
        => new ContainmentTarget.McpServer("tenant-a", "server-a");

    private static ContainmentRecord Record(
        ContainmentTarget target,
        ContainmentStatus status)
        => new(
            target,
            status,
            Now,
            status == ContainmentStatus.Released ? Now.AddMinutes(1) : null,
            "block_storm",
            "evidence:incident-1",
            "gatekeeper",
            status == ContainmentStatus.Released ? "operator-a" : null,
            version: 1,
            "etag-1");

    private static ChatClientAgent NewAgent()
        => new(
            new ScriptedChatClient().AddText("done"),
            new ChatClientAgentOptions { Name = "agent" });

    private sealed class BagSession : AgentSession;

    private sealed class FakeStore : IContainmentStore
    {
        private readonly Func<ContainmentTarget, ContainmentSnapshot> _read;

        public FakeStore(Func<ContainmentTarget, ContainmentSnapshot>? read = null)
        {
            _read = read ?? ContainmentSnapshot.NotContained;
        }

        public List<ContainmentTarget> Reads { get; } = [];

        public ContainmentSnapshot GetCurrent(ContainmentTarget target)
        {
            Reads.Add(target);
            return _read(target);
        }

        public ValueTask<ContainmentMutationResult> ContainAsync(
            ContainmentRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<ContainmentMutationResult> ReleaseAsync(
            ContainmentReleaseAuthorization authorization,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() { }
    }

    private sealed class ThrowingTargetList : IReadOnlyList<ContainmentTarget>
    {
        public int Count => throw new InvalidOperationException("secret count failure");

        public ContainmentTarget this[int index] => throw new InvalidOperationException("secret index failure");

        public IEnumerator<ContainmentTarget> GetEnumerator() => throw new NotSupportedException();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class AllowToolGate : IToolGate
    {
        public string PolicyName => "AllowToolGate";

        public GateCost Cost => GateCost.PureCode;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
            => new(ToolGateVerdict.Allow(PolicyName));
    }

    private sealed class CountingToolGate : IToolGate
    {
        public int Calls { get; private set; }

        public string PolicyName => "CountingToolGate";

        public GateCost Cost => GateCost.PureCode;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
        }
    }

    private sealed class AllowChatGate : IChatGate
    {
        public string PolicyName => "AllowChatGate";

        public ValueTask<GateVerdict> InspectAsync(
            string text,
            CancellationToken cancellationToken = default)
            => new(GateVerdict.Allow(PolicyName));
    }
}
