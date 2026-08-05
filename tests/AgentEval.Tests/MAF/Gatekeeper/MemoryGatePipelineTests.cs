// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using AgentEval.MAF.Gatekeeper.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Time.Testing;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class MemoryGatePipelineTests
{
    [Fact]
    public void Models_MutableInputsChangedAfterConstruction_RemainFrozen()
    {
        var contentArguments = new List<string> { "content" };
        var scopeArguments = new List<string> { "user_id" };
        var parents = new List<string> { "parent-1" };
        var modelScope = new Dictionary<string, string?> { ["user_id"] = "model-user" };
        var conflicts = new List<MemoryConflictCandidate>
        {
            new("memory-1", Digest("old"), MemoryTrustLevel.High, "root-1", MemoryCategory.Fact),
        };

        var operation = new MemoryOperationContract(
            "remember",
            MemoryOperationKind.Write,
            MemorySurface.Tool,
            contentArguments,
            scopeArguments,
            MemoryCategory.Fact,
            isSideEffecting: true,
            mayReturnSensitiveContent: false);
        var provenance = new MemoryProvenance(
            MemorySourceKind.User,
            "source-1",
            MemoryTrustLevel.Low,
            parentMemoryIds: parents);
        var context = CreateContext(
            operation: operation,
            provenance: provenance,
            modelScope: modelScope,
            conflicts: conflicts);

        contentArguments.Add("later");
        scopeArguments.Clear();
        parents.Add("parent-2");
        modelScope["user_id"] = "changed";
        conflicts.Clear();

        Assert.Equal(["content"], operation.ContentArguments);
        Assert.Equal(["user_id"], operation.ScopeArguments);
        Assert.Equal(["parent-1"], provenance.ParentMemoryIds);
        Assert.Equal("model-user", context.ModelSuppliedScope["user_id"]);
        Assert.Single(context.Conflicts);
    }

    [Fact]
    public void Context_ContentAndDigestMismatch_ThrowsWithoutEchoingContent()
    {
        var secret = "secret-value-that-must-not-appear";

        var exception = Assert.Throws<ArgumentException>(
            () => CreateContext(content: secret, contentDigest: Digest("different")));

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Context_Serialized_DoesNotContainRawContent()
    {
        var secret = "secret-value-that-must-not-appear";
        var context = CreateContext(content: secret);

        var json = JsonSerializer.Serialize(context);

        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.Contains(context.ContentDigest, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_IllegalOperationStage_Throws()
    {
        var operation = new MemoryOperationContract(
            "remember",
            MemoryOperationKind.Write,
            MemorySurface.Tool,
            ["content"],
            [],
            MemoryCategory.Fact,
            isSideEffecting: true,
            mayReturnSensitiveContent: false);

        Assert.Throws<ArgumentException>(
            () => CreateContext(operation: operation, stage: MemoryGateStage.AfterRead));
    }

    [Fact]
    public void Pipeline_SourceGateListChangedAfterConstruction_SnapshotAndFingerprintRemainStable()
    {
        var first = Gate("first", _ => MemoryGateVerdict.Allow("first"));
        var gates = new List<IMemoryGate> { first };
        var pipeline = new MemoryGatePipeline(gates);
        var fingerprint = pipeline.PolicyFingerprint;

        gates.Add(Gate("later", _ => MemoryGateVerdict.Reject("later", "memory.test.reject")));

        Assert.Single(pipeline.Gates);
        Assert.Equal(fingerprint, pipeline.PolicyFingerprint);
    }

    [Fact]
    public void Pipeline_GateOrderChanged_FingerprintChanges()
    {
        var first = Gate("first", _ => MemoryGateVerdict.Allow("first"));
        var second = Gate("second", _ => MemoryGateVerdict.Allow("second"));

        var one = new MemoryGatePipeline([first, second]);
        var two = new MemoryGatePipeline([second, first]);

        Assert.NotEqual(one.PolicyFingerprint, two.PolicyFingerprint);
    }

    [Fact]
    public async Task EvaluateAsync_SanitizeThenInspect_UsesSanitizedContentAndReturnsSanitize()
    {
        string? observed = null;
        var sanitize = Gate(
            "sanitize",
            _ => MemoryGateVerdict.Sanitize("sanitize", "safe", "memory.test.sanitized"));
        var inspect = Gate(
            "inspect",
            context =>
            {
                observed = context.Content;
                return MemoryGateVerdict.Allow("inspect");
            });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero));
        var pipeline = new MemoryGatePipeline([sanitize, inspect], policy: EnforcePolicy(), timeProvider: clock);

        var decision = await pipeline.EvaluateAsync(CreateContext(content: "unsafe"));

        Assert.Equal(MemoryGateAction.Sanitize, decision.Action);
        Assert.Equal("safe", decision.EffectiveContent);
        Assert.Equal("safe", observed);
        Assert.Equal("memory.test.sanitized", decision.ReasonCode);
        Assert.Equal(2, decision.Receipts.Count);
        Assert.All(decision.Receipts, receipt => Assert.Equal(clock.GetUtcNow(), receipt.EvaluatedAtUtc));
    }

    [Fact]
    public async Task EvaluateAsync_SecondSanitizer_RejectsBoundedReentry()
    {
        var first = Gate("first", _ => MemoryGateVerdict.Sanitize("first", "one", "memory.test.first"));
        var second = Gate("second", _ => MemoryGateVerdict.Sanitize("second", "two", "memory.test.second"));
        var pipeline = new MemoryGatePipeline([first, second]);

        var decision = await pipeline.EvaluateAsync(CreateContext());

        Assert.Equal(MemoryGateAction.Reject, decision.Action);
        Assert.Equal("memory.pipeline.sanitize_loop", decision.ReasonCode);
        Assert.Equal(3, decision.Receipts.Count);
    }

    [Fact]
    public async Task EvaluateAsync_QuarantineThenReject_RejectDominates()
    {
        var quarantine = Gate(
            "quarantine",
            _ => MemoryGateVerdict.Quarantine("quarantine", "memory.test.quarantine"),
            requirements: MemoryGateRequirements.QuarantineStore);
        var reject = Gate("reject", _ => MemoryGateVerdict.Reject("reject", "memory.test.reject"));
        var capabilities = new MemoryGateCapabilities(quarantineStore: new FakeQuarantineStore());
        var pipeline = new MemoryGatePipeline([quarantine, reject], capabilities);

        var decision = await pipeline.EvaluateAsync(CreateContext());

        Assert.Equal(MemoryGateAction.Reject, decision.Action);
        Assert.Equal("memory.test.reject", decision.ReasonCode);
        Assert.Equal(2, decision.Receipts.Count);
    }

    [Fact]
    public async Task EvaluateAsync_GateThrows_RejectsWithoutExceptionDisclosure()
    {
        var secret = "provider-secret-exception";
        var gate = new FakeGate(
            "throwing",
            GateCost.PureCode,
            MemoryGateStage.BeforeWrite,
            MemoryGateRequirements.None,
            (_, _) => throw new InvalidOperationException(secret));
        var pipeline = new MemoryGatePipeline([gate]);

        var decision = await pipeline.EvaluateAsync(CreateContext());
        var json = JsonSerializer.Serialize(decision);

        Assert.Equal(MemoryGateAction.Reject, decision.Action);
        Assert.Equal("memory.gate.failure", decision.ReasonCode);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_InvalidStageAction_Rejects()
    {
        var gate = Gate(
            "invalid",
            _ => MemoryGateVerdict.Exclude("invalid", "memory.test.exclude"),
            stages: MemoryGateStage.BeforeWrite);
        var pipeline = new MemoryGatePipeline([gate]);

        var decision = await pipeline.EvaluateAsync(CreateContext());

        Assert.Equal(MemoryGateAction.Reject, decision.Action);
        Assert.Equal("memory.gate.invalid_action", decision.ReasonCode);
    }

    [Fact]
    public async Task EvaluateAsync_QuarantineWithoutDeclaredCapability_RejectsAtRuntime()
    {
        var gate = Gate(
            "undeclared",
            _ => MemoryGateVerdict.Quarantine("undeclared", "memory.test.quarantine"));
        var pipeline = new MemoryGatePipeline([gate], policy: EnforcePolicy());

        var decision = await pipeline.EvaluateAsync(CreateContext());

        Assert.Equal(MemoryGateAction.Reject, decision.Action);
        Assert.Equal("memory.capability.quarantine_missing", decision.ReasonCode);
    }

    [Fact]
    public async Task EvaluateAsync_ApprovalUnavailableWithExplicitFallback_Quarantines()
    {
        var gate = Gate(
            "approval",
            _ => MemoryGateVerdict.RequireApproval("approval", "memory.test.approval"));
        var capabilities = new MemoryGateCapabilities(
            quarantineStore: new FakeQuarantineStore(),
            quarantineOnApprovalUnavailable: true);
        var pipeline = new MemoryGatePipeline([gate], capabilities, EnforcePolicy());

        var decision = await pipeline.EvaluateAsync(CreateContext());

        Assert.Equal(MemoryGateAction.Quarantine, decision.Action);
        Assert.Equal("memory.approval.fallback_quarantine", decision.ReasonCode);
    }

    [Fact]
    public async Task EvaluateAsync_CallerCancellation_Propagates()
    {
        var gate = new FakeGate(
            "cancel",
            GateCost.PureCode,
            MemoryGateStage.BeforeWrite,
            MemoryGateRequirements.None,
            (_, cancellationToken) => ValueTask.FromCanceled<MemoryGateVerdict>(cancellationToken));
        var pipeline = new MemoryGatePipeline([gate]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await pipeline.EvaluateAsync(CreateContext(), cancellation.Token));
    }

    [Fact]
    public async Task EvaluateAsync_ConcurrentCalls_DoNotShareContentOrReceipts()
    {
        var gate = Gate(
            "allow",
            context => MemoryGateVerdict.Allow("allow", $"memory.test.{context.OperationId}"));
        var pipeline = new MemoryGatePipeline([gate]);
        var contexts = Enumerable.Range(0, 100)
            .Select(index => CreateContext(operationId: $"operation-{index}", content: $"content-{index}"))
            .ToArray();

        var decisions = await Task.WhenAll(
            contexts.Select(async context => await pipeline.EvaluateAsync(context)));

        Assert.Equal(100, decisions.Length);
        for (var index = 0; index < decisions.Length; index++)
        {
            Assert.Equal($"content-{index}", decisions[index].EffectiveContent);
            Assert.Equal($"operation-{index}", Assert.Single(decisions[index].Receipts).OperationId);
        }
    }

    [Theory]
    [InlineData(GateCost.Network)]
    [InlineData(GateCost.Llm)]
    public void Constructor_NondeterministicInlineGate_Rejects(GateCost cost)
    {
        var gate = new FakeGate(
            "expensive",
            cost,
            MemoryGateStage.BeforeWrite,
            MemoryGateRequirements.None,
            (_, _) => ValueTask.FromResult(MemoryGateVerdict.Allow("expensive")));

        Assert.Throws<ArgumentException>(() => new MemoryGatePipeline([gate]));
    }

    [Fact]
    public void Constructor_DuplicatePolicyName_Rejects()
    {
        var first = Gate("same", _ => MemoryGateVerdict.Allow("same"));
        var second = Gate("same", _ => MemoryGateVerdict.Allow("same"));

        Assert.Throws<ArgumentException>(() => new MemoryGatePipeline([first, second]));
    }

    [Theory]
    [InlineData(MemoryGateRequirements.RunScope)]
    [InlineData(MemoryGateRequirements.AuthenticatedMemoryScope)]
    [InlineData(MemoryGateRequirements.QuarantineStore)]
    [InlineData(MemoryGateRequirements.ApprovalHandler)]
    [InlineData(MemoryGateRequirements.ProviderCandidateHook)]
    public void Constructor_MissingRequiredCapability_Rejects(MemoryGateRequirements requirement)
    {
        var gate = Gate(
            "requires",
            _ => MemoryGateVerdict.Allow("requires"),
            requirements: requirement);

        var exception = Assert.Throws<InvalidOperationException>(() => new MemoryGatePipeline([gate]));

        Assert.Contains(requirement.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_AllCapabilitiesPresent_AcceptsCombinedRequirements()
    {
        var requirements =
            MemoryGateRequirements.RunScope |
            MemoryGateRequirements.AuthenticatedMemoryScope |
            MemoryGateRequirements.QuarantineStore |
            MemoryGateRequirements.ApprovalHandler |
            MemoryGateRequirements.ProviderCandidateHook;
        var gate = Gate(
            "requires-all",
            _ => MemoryGateVerdict.Allow("requires-all"),
            requirements: requirements);
        var capabilities = new MemoryGateCapabilities(
            guaranteesRunScope: true,
            scopeResolver: new FakeScopeResolver(),
            quarantineStore: new FakeQuarantineStore(),
            approvalHandler: new FakeApprovalHandler(),
            providerCandidateHook: new FakeProviderHook());

        var pipeline = new MemoryGatePipeline([gate], capabilities);

        Assert.Equal(requirements, pipeline.Requirements);
    }

    [Fact]
    public void QuarantineAndApprovalRequests_WrongDisposition_Reject()
    {
        var context = CreateContext();
        var pipeline = new MemoryGatePipeline([]);
        var allowReceipt = new MemoryGateReceipt(
            context.OperationId,
            context.Stage,
            context.Surface,
            MemoryGateAction.Allow,
            "test",
            "memory.allow",
            pipeline.PolicyFingerprint,
            context.ContentDigest,
            context.RunId,
            null,
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() => new MemoryQuarantineRequest(context, allowReceipt));
        Assert.Throws<ArgumentException>(() => new MemoryApprovalRequest(allowReceipt));
    }

    [Theory]
    [InlineData(MemoryGateAction.Quarantine)]
    [InlineData(MemoryGateAction.RequireApproval)]
    public void Constructor_EnforcingPolicyMissingDispositionCapability_Rejects(MemoryGateAction action)
    {
        var policy = new MemorySecurityPolicy(
            "enforce",
            "1",
            MemorySecurityProfile.Enforce,
            action,
            MemoryCoverageLevel.Boundary);

        Assert.Throws<InvalidOperationException>(
            () => new MemoryGatePipeline([], policy: policy));
    }

    [Fact]
    public async Task EvaluateAsync_ObservePolicyNeedsNoDispositionServiceAndDoesNotEnforce()
    {
        var policy = new MemorySecurityPolicy(
            "observe",
            "1",
            MemorySecurityProfile.Observe,
            MemoryGateAction.Quarantine,
            MemoryCoverageLevel.ObserveOnly);
        var gate = Gate(
            "would-quarantine",
            _ => MemoryGateVerdict.Quarantine("would-quarantine", "memory.test.quarantine"));
        var pipeline = new MemoryGatePipeline([gate], policy: policy);

        var decision = await pipeline.EvaluateAsync(CreateContext());

        Assert.Same(policy, pipeline.Policy);
        Assert.Equal(MemoryGateAction.Quarantine, decision.Action);
        Assert.True(decision.IsAllowed);
        Assert.Equal("candidate", decision.EffectiveContent);
        Assert.False(decision.ShouldApplySanitizedContent);
    }

    [Fact]
    public void Pipeline_PolicyChanged_FingerprintChanges()
    {
        var one = new MemoryGatePipeline([], policy: EnforcePolicy("1"));
        var two = new MemoryGatePipeline([], policy: EnforcePolicy("2"));

        Assert.NotEqual(one.PolicyFingerprint, two.PolicyFingerprint);
    }

    [Fact]
    public void Models_UnknownEnumValues_Reject()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MemorySecurityPolicy(
                "policy",
                "1",
                (MemorySecurityProfile)99,
                MemoryGateAction.Reject,
                MemoryCoverageLevel.Boundary));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MemoryProvenance(
                (MemorySourceKind)99,
                "source",
                MemoryTrustLevel.Low));
    }

    private static MemorySecurityPolicy EnforcePolicy(string version = "1")
        => new(
            "test-enforce",
            version,
            MemorySecurityProfile.Enforce,
            MemoryGateAction.Reject,
            MemoryCoverageLevel.Boundary);
    private static MemoryGateContext CreateContext(
        string operationId = "operation-1",
        string? content = "candidate",
        string? contentDigest = null,
        MemoryGateStage stage = MemoryGateStage.BeforeWrite,
        MemoryOperationContract? operation = null,
        MemoryProvenance? provenance = null,
        IReadOnlyDictionary<string, string?>? modelScope = null,
        IEnumerable<MemoryConflictCandidate>? conflicts = null)
    {
        operation ??= new MemoryOperationContract(
            "remember",
            MemoryOperationKind.Write,
            MemorySurface.Tool,
            ["content"],
            ["user_id"],
            MemoryCategory.Fact,
            isSideEffecting: true,
            mayReturnSensitiveContent: false);
        provenance ??= new MemoryProvenance(
            MemorySourceKind.User,
            "source-1",
            MemoryTrustLevel.Low);

        return new MemoryGateContext(
            operationId,
            stage,
            operation,
            "provider-1",
            new MemorySecurityScope(tenantId: "tenant-1", userId: "user-1"),
            provenance,
            content,
            contentDigest,
            modelScope,
            conflicts,
            runId: "run-1",
            logicalSessionId: "session-1");
    }

    private static FakeGate Gate(
        string policyName,
        Func<MemoryGateContext, MemoryGateVerdict> inspect,
        MemoryGateStage stages = MemoryGateStage.BeforeWrite,
        MemoryGateRequirements requirements = MemoryGateRequirements.None)
        => new(
            policyName,
            GateCost.PureCode,
            stages,
            requirements,
            (context, _) => ValueTask.FromResult(inspect(context)));

    private static string Digest(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class FakeGate(
        string policyName,
        GateCost cost,
        MemoryGateStage stages,
        MemoryGateRequirements requirements,
        Func<MemoryGateContext, CancellationToken, ValueTask<MemoryGateVerdict>> inspect) : IMemoryGate
    {
        public string PolicyName { get; } = policyName;
        public GateCost Cost { get; } = cost;
        public MemoryGateStage Stages { get; } = stages;
        public MemoryGateRequirements Requirements { get; } = requirements;

        public ValueTask<MemoryGateVerdict> InspectAsync(
            MemoryGateContext context,
            CancellationToken cancellationToken = default)
            => inspect(context, cancellationToken);
    }

    private sealed class FakeQuarantineStore : IMemoryQuarantineStore
    {
        public ValueTask<MemoryQuarantineReceipt> StoreAsync(
            MemoryQuarantineRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                new MemoryQuarantineReceipt("quarantine-1", request.DecisionReceipt.OperationId, DateTimeOffset.UtcNow));
    }

    private sealed class FakeApprovalHandler : IMemoryApprovalHandler
    {
        public ValueTask<MemoryApprovalDecision> RequestApprovalAsync(
            MemoryApprovalRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new MemoryApprovalDecision(approved: true, "approval-1"));
    }

    private sealed class FakeProviderHook : IMemoryProviderCandidateHook
    {
        public string HookId => "fake-provider";
        public string Version => "1";
    }

    private sealed class FakeScopeResolver : IMemoryScopeResolver
    {
        public MemorySecurityScope Resolve(AgentSession session, string? agentName)
            => new(tenantId: "tenant-1", userId: "user-1");
    }
}
