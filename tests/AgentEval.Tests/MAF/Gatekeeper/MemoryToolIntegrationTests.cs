// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using AgentEval.MAF.Gatekeeper.Memory;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class MemoryToolIntegrationTests
{
    [Fact]
    public void Registry_MutableSourceChanged_RemainsFrozenAndFingerprinted()
    {
        var contracts = new List<MemoryOperationContract> { WriteContract() };
        var registry = new MemoryToolOperationRegistry(contracts);
        var fingerprint = registry.ConfigurationFingerprint;

        contracts.Clear();

        Assert.True(registry.TryGet("memory_write", out _));
        Assert.Equal(fingerprint, registry.ConfigurationFingerprint);
    }

    [Fact]
    public void Registry_DuplicateOrNonToolContract_Rejects()
    {
        Assert.Throws<ArgumentException>(() => new MemoryToolOperationRegistry(
            [WriteContract(), WriteContract()]));
        Assert.Throws<ArgumentException>(() => new MemoryToolOperationRegistry(
            [Contract("provider", MemoryOperationKind.Write, MemorySurface.ProviderNative)]));
    }

    [Fact]
    public void Classifier_OnlyRegistryAssignsMemorySemantics()
    {
        var registry = Registry(WriteContract());
        var registered = Function("memory_write");
        var suspicious = Function("remember_everything");
        var ordinary = Function("weather");

        Assert.Equal(MemoryToolClassification.RegisteredMemory, MemoryToolClassifier.Classify(registered, registry));
        Assert.Equal(MemoryToolClassification.UnclassifiedMemoryLike, MemoryToolClassifier.Classify(suspicious, registry));
        Assert.Equal(MemoryToolClassification.NonMemory, MemoryToolClassifier.Classify(ordinary, registry));
        Assert.False(registry.TryGet(suspicious.Name, out _));
    }

    [Fact]
    public async Task CallGate_UnregisteredTool_AllowsWithoutAdapterInvocation()
    {
        var adapter = new FakeAdapter();
        var gate = CallGate(Pipeline(), Registry(WriteContract()), adapter);

        var verdict = await gate.InspectAsync(Call("weather"));

        Assert.Equal(ToolGateAction.Allow, verdict.Action);
        Assert.Equal(0, adapter.CallContextCount);
    }

    [Fact]
    public async Task CallGate_RegisteredWrite_UsesBeforeWriteAndAllows()
    {
        var adapter = new FakeAdapter();
        var gate = CallGate(Pipeline(new FixedGate(MemoryGateStage.BeforeWrite)), Registry(WriteContract()), adapter);

        var verdict = await gate.InspectAsync(Call("memory_write"));

        Assert.Equal(ToolGateAction.Allow, verdict.Action);
        Assert.Equal([MemoryGateStage.BeforeWrite], adapter.CallStages);
    }

    [Fact]
    public async Task CallGate_EnforcingReject_MapsToToolBlockWithSafeReason()
    {
        var gate = CallGate(
            Pipeline(new FixedGate(MemoryGateStage.BeforeWrite, MemoryGateAction.Reject, "memory.test.reject")),
            Registry(WriteContract()),
            new FakeAdapter());

        var verdict = await gate.InspectAsync(Call("memory_write"));

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Equal("memory.test.reject", verdict.Reason);
    }

    [Fact]
    public async Task CallGate_EnforcingSanitize_MutatesExplicitContentArgument()
    {
        var adapter = new FakeAdapter();
        var gate = CallGate(
            Pipeline(new FixedGate(
                MemoryGateStage.BeforeWrite,
                MemoryGateAction.Sanitize,
                "memory.test.sanitize",
                "[clean]")),
            Registry(WriteContract()),
            adapter);

        var verdict = await gate.InspectAsync(Call("memory_write", "unsafe"));

        Assert.Equal(ToolGateAction.Mutate, verdict.Action);
        Assert.Equal("[clean]", verdict.NewArguments!["content"]);
        Assert.Equal(1, adapter.ArgumentRewriteCount);
    }

    [Fact]
    public async Task CallGate_ObserveReject_RecordsDecisionButDoesNotBlock()
    {
        var sink = new FakeDecisionSink();
        var pipeline = Pipeline(
            new FixedGate(MemoryGateStage.BeforeWrite, MemoryGateAction.Reject, "memory.test.reject"),
            profile: MemorySecurityProfile.Observe);
        var gate = new MemoryToolCallGate(pipeline, Registry(WriteContract()), new FakeAdapter(), sink);

        var verdict = await gate.InspectAsync(Call("memory_write"));

        Assert.Equal(ToolGateAction.Allow, verdict.Action);
        Assert.Single(sink.Decisions);
        Assert.Equal(MemoryGateAction.Reject, sink.Decisions[0].Action);
    }

    [Fact]
    public async Task CallGate_Quarantine_StoresOnceAndBlocksActiveWrite()
    {
        var store = new FakeQuarantineStore();
        var pipeline = Pipeline(
            new FixedGate(MemoryGateStage.BeforeWrite, MemoryGateAction.Quarantine, "memory.test.quarantine"),
            capabilities: new MemoryGateCapabilities(quarantineStore: store));
        var gate = CallGate(pipeline, Registry(WriteContract()), new FakeAdapter());

        var verdict = await gate.InspectAsync(Call("memory_write"));

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Single(store.Requests);
        Assert.Equal(MemoryGateAction.Quarantine, store.Requests[0].DecisionReceipt.Action);
    }

    [Theory]
    [InlineData(true, ToolGateAction.Allow)]
    [InlineData(false, ToolGateAction.Block)]
    public async Task CallGate_Approval_AppliesDecision(bool approved, ToolGateAction expected)
    {
        var handler = new FakeApprovalHandler(approved);
        var pipeline = Pipeline(
            new FixedGate(MemoryGateStage.BeforeWrite, MemoryGateAction.RequireApproval, "memory.test.approval"),
            capabilities: new MemoryGateCapabilities(approvalHandler: handler));
        var gate = CallGate(pipeline, Registry(WriteContract()), new FakeAdapter());

        var verdict = await gate.InspectAsync(Call("memory_write"));

        Assert.Equal(expected, verdict.Action);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CallGate_Promotion_EvaluatesWriteThenPromotionStages()
    {
        var adapter = new FakeAdapter();
        var pipeline = Pipeline(new FixedGate(
            MemoryGateStage.BeforeWrite | MemoryGateStage.BeforePromotion));
        var gate = CallGate(pipeline, Registry(PromoteContract()), adapter);

        var verdict = await gate.InspectAsync(Call("memory_promote"));

        Assert.Equal(ToolGateAction.Allow, verdict.Action);
        Assert.Equal(
            [MemoryGateStage.BeforeWrite, MemoryGateStage.BeforePromotion],
            adapter.CallStages);
    }

    [Fact]
    public async Task CallGate_AdapterReturnsWrongStage_ThrowsFailClosed()
    {
        var adapter = new FakeAdapter { WrongStage = true };
        var gate = CallGate(Pipeline(), Registry(WriteContract()), adapter);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await gate.InspectAsync(Call("memory_write")));
    }

    [Fact]
    public async Task CallGate_CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var gate = CallGate(Pipeline(), Registry(WriteContract()), new FakeAdapter());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await gate.InspectAsync(Call("memory_write"), cancellation.Token));
    }

    [Fact]
    public async Task ResultGate_UnregisteredOrWriteResult_AllowsWithoutAdapter()
    {
        var adapter = new FakeAdapter();
        var registry = Registry(WriteContract());
        var gate = ResultGate(Pipeline(), registry, adapter);

        var unregistered = await gate.InspectAsync(Result("weather"));
        var write = await gate.InspectAsync(Result("memory_write"));

        Assert.Equal(ToolResultAction.Allow, unregistered.Action);
        Assert.Equal(ToolResultAction.Allow, write.Action);
        Assert.Equal(0, adapter.ResultContextCount);
    }

    [Fact]
    public async Task ResultGate_RecallSanitize_RedactsBeforeModel()
    {
        var adapter = new FakeAdapter();
        var gate = ResultGate(
            Pipeline(new FixedGate(
                MemoryGateStage.AfterRead,
                MemoryGateAction.Sanitize,
                "memory.test.delimit",
                "<memory>safe</memory>")),
            Registry(RecallContract()),
            adapter);

        var verdict = await gate.InspectAsync(Result("memory_recall", "raw"));

        Assert.Equal(ToolResultAction.Redact, verdict.Action);
        Assert.Equal("<memory>safe</memory>", verdict.RedactedResult);
        Assert.Equal(1, adapter.ResultRewriteCount);
    }

    [Fact]
    public async Task ResultGate_RecallExclude_BlocksRawResult()
    {
        var gate = ResultGate(
            Pipeline(new FixedGate(
                MemoryGateStage.AfterRead,
                MemoryGateAction.Exclude,
                "memory.test.exclude")),
            Registry(RecallContract()),
            new FakeAdapter());

        var verdict = await gate.InspectAsync(Result("memory_recall", "poison"));

        Assert.Equal(ToolResultAction.Block, verdict.Action);
        Assert.Equal("memory.test.exclude", verdict.Reason);
    }

    [Fact]
    public async Task ResultGate_ObserveExclude_AllowsAndRecordsWouldDecision()
    {
        var sink = new FakeDecisionSink();
        var gate = new MemoryToolResultGate(
            Pipeline(
                new FixedGate(MemoryGateStage.AfterRead, MemoryGateAction.Exclude, "memory.test.exclude"),
                profile: MemorySecurityProfile.Observe),
            Registry(RecallContract()),
            new FakeAdapter(),
            sink);

        var verdict = await gate.InspectAsync(Result("memory_recall", "raw"));

        Assert.Equal(ToolResultAction.Allow, verdict.Action);
        Assert.Equal(MemoryGateAction.Exclude, Assert.Single(sink.Decisions).Action);
    }

    [Fact]
    public async Task DecisionExecutor_ObserveNeverCallsDispositionServices()
    {
        var store = new FakeQuarantineStore();
        var executor = new MemoryGateDecisionExecutor(new MemoryGateCapabilities(quarantineStore: store));
        var context = Context(WriteContract(), MemoryGateStage.BeforeWrite, "candidate");
        var decision = await Pipeline(
            new FixedGate(MemoryGateStage.BeforeWrite, MemoryGateAction.Quarantine, "memory.test.quarantine"),
            profile: MemorySecurityProfile.Observe,
            capabilities: new MemoryGateCapabilities(quarantineStore: store)).EvaluateAsync(context);

        var applied = await executor.ExecuteAsync(context, decision);

        Assert.True(applied.Allowed);
        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task InfluenceGate_TaintedMemoryValueToSensitiveSink_BlocksWithoutEcho()
    {
        var gate = new MemoryInfluenceGate(Registry(RecallContract()), ["send_email"]);
        var messages = new[]
        {
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent("c1", "memory_recall", new Dictionary<string, object?>())]),
            new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent("c1", "private-value-123456")]),
        };
        var call = new GatedToolCall(
            "send_email",
            new Dictionary<string, object?> { ["body"] = "send private-value-123456" },
            "agent",
            0,
            0,
            1,
            false,
            messages);

        var verdict = await gate.InspectAsync(call);

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.DoesNotContain("private-value-123456", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InfluenceGate_UnrelatedSensitiveCall_Allows()
    {
        var gate = new MemoryInfluenceGate(Registry(RecallContract()), ["send_email"]);

        var verdict = await gate.InspectAsync(Call("send_email", "ordinary body"));

        Assert.Equal(ToolGateAction.Allow, verdict.Action);
    }

    [Fact]
    public void Coverage_UnclassifiedMemoryLikeTool_IsUnsupportedAndEnforcementThrows()
    {
        var registry = Registry(WriteContract());
        var tool = Function("remember_unknown");

        var report = MemoryToolCoverageAnalyzer.Analyze([tool], registry);

        Assert.Equal(MemoryCoverageLevel.Unsupported, Assert.Single(report.Entries).Coverage);
        Assert.Throws<MemoryToolCoverageException>(
            () => MemoryToolCoverageAnalyzer.AnalyzeOrThrow([tool], registry));
    }

    [Fact]
    public void Coverage_LocalWriteWithMatchingCallGate_IsFullLifecycle()
    {
        var registry = Registry(WriteContract());
        var callGate = CallGate(Pipeline(), registry, new FakeAdapter());

        var report = MemoryToolCoverageAnalyzer.AnalyzeOrThrow(
            [Function("memory_write")],
            registry,
            callGate);

        Assert.Equal(MemoryCoverageLevel.FullLifecycle, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void Coverage_ReadMissingResultGate_IsUnsupported()
    {
        var registry = Registry(RecallContract());
        var callGate = CallGate(Pipeline(), registry, new FakeAdapter());

        var report = MemoryToolCoverageAnalyzer.Analyze(
            [Function("memory_recall")],
            registry,
            callGate);

        Assert.Equal(MemoryCoverageLevel.Unsupported, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void Coverage_SensitiveReadWithoutInfluence_IsBoundary()
    {
        var registry = Registry(RecallContract());
        var adapter = new FakeAdapter();

        var report = MemoryToolCoverageAnalyzer.AnalyzeOrThrow(
            [Function("memory_recall")],
            registry,
            CallGate(Pipeline(), registry, adapter),
            ResultGate(Pipeline(), registry, adapter));

        Assert.Equal(MemoryCoverageLevel.Boundary, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void Coverage_SensitiveReadWithInfluence_IsFullLifecycle()
    {
        var registry = Registry(RecallContract());
        var adapter = new FakeAdapter();
        var influence = new MemoryInfluenceGate(registry, ["send_email"]);

        var report = MemoryToolCoverageAnalyzer.AnalyzeOrThrow(
            [Function("memory_recall")],
            registry,
            CallGate(Pipeline(), registry, adapter),
            ResultGate(Pipeline(), registry, adapter),
            influence);

        Assert.Equal(MemoryCoverageLevel.FullLifecycle, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void Coverage_LocalMcpClientNeverClaimsFullLifecycle()
    {
        var contract = Contract("memory_mcp", MemoryOperationKind.Write, MemorySurface.LocalMcp);
        var registry = Registry(contract);

        var report = MemoryToolCoverageAnalyzer.AnalyzeOrThrow(
            [Function("memory_mcp")],
            registry,
            CallGate(Pipeline(), registry, new FakeAdapter()));

        Assert.Equal(MemoryCoverageLevel.Boundary, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void Coverage_MismatchedGateRegistry_IsUnsupported()
    {
        var registry = Registry(WriteContract());
        var other = Registry(Contract("other_write", MemoryOperationKind.Write));
        var callGate = CallGate(Pipeline(), other, new FakeAdapter());

        var report = MemoryToolCoverageAnalyzer.Analyze(
            [Function("memory_write")],
            registry,
            callGate);

        Assert.Equal(MemoryCoverageLevel.Unsupported, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void Coverage_ObserveOnlyAdapters_CannotMeetEnforcementThreshold()
    {
        var registry = Registry(WriteContract());
        var callGate = CallGate(
            Pipeline(profile: MemorySecurityProfile.Observe),
            registry,
            new FakeAdapter());

        var report = MemoryToolCoverageAnalyzer.Analyze(
            [Function("memory_write")],
            registry,
            callGate);
        var exception = Assert.Throws<MemoryToolCoverageException>(
            () => MemoryToolCoverageAnalyzer.AnalyzeOrThrow(
                [Function("memory_write")],
                registry,
                callGate));

        Assert.Equal(MemoryCoverageLevel.ObserveOnly, Assert.Single(report.Entries).Coverage);
        Assert.Contains("memory_write", exception.Message, StringComparison.Ordinal);
        Assert.Equal(MemoryCoverageLevel.Boundary, exception.MinimumCoverage);
    }

    [Fact]
    public void Coverage_CallAndResultFromDifferentPipelines_IsUnsupported()
    {
        var registry = Registry(RecallContract());
        var adapter = new FakeAdapter();
        var callPipeline = Pipeline();
        var resultPipeline = Pipeline(new FixedGate(MemoryGateStage.AfterRead));

        var report = MemoryToolCoverageAnalyzer.Analyze(
            [Function("memory_recall")],
            registry,
            CallGate(callPipeline, registry, adapter),
            ResultGate(resultPipeline, registry, adapter));

        Assert.Equal(MemoryCoverageLevel.Unsupported, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void Coverage_InfluenceFromDifferentRegistry_DoesNotUpgradeSensitiveRead()
    {
        var registry = Registry(RecallContract());
        var adapter = new FakeAdapter();
        var other = Registry(Contract("other_recall", MemoryOperationKind.Recall));

        var report = MemoryToolCoverageAnalyzer.AnalyzeOrThrow(
            [Function("memory_recall")],
            registry,
            CallGate(Pipeline(), registry, adapter),
            ResultGate(Pipeline(), registry, adapter),
            new MemoryInfluenceGate(other, ["send_email"]));

        Assert.Equal(MemoryCoverageLevel.Boundary, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void Coverage_FullLifecycleThreshold_RejectsBoundaryCoverage()
    {
        var registry = Registry(RecallContract());
        var adapter = new FakeAdapter();

        var exception = Assert.Throws<MemoryToolCoverageException>(
            () => MemoryToolCoverageAnalyzer.AnalyzeOrThrow(
                [Function("memory_recall")],
                registry,
                CallGate(Pipeline(), registry, adapter),
                ResultGate(Pipeline(), registry, adapter),
                minimumCoverage: MemoryCoverageLevel.FullLifecycle));

        Assert.Equal(MemoryCoverageLevel.FullLifecycle, exception.MinimumCoverage);
        Assert.Contains("memory_recall", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallGate_NullSanitizedArguments_FailsClosed()
    {
        var adapter = new FakeAdapter { ReturnNullArguments = true };
        var gate = CallGate(
            Pipeline(new FixedGate(
                MemoryGateStage.BeforeWrite,
                MemoryGateAction.Sanitize,
                "memory.test.sanitize",
                "[clean]")),
            Registry(WriteContract()),
            adapter);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await gate.InspectAsync(Call("memory_write", "unsafe")));
    }

    [Fact]
    public async Task ResultGate_NullSanitizedResult_FailsClosed()
    {
        var adapter = new FakeAdapter { ReturnNullResult = true };
        var gate = ResultGate(
            Pipeline(new FixedGate(
                MemoryGateStage.AfterRead,
                MemoryGateAction.Sanitize,
                "memory.test.sanitize",
                "[clean]")),
            Registry(RecallContract()),
            adapter);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await gate.InspectAsync(Result("memory_recall", "unsafe")));
    }

    [Fact]
    public async Task InfluenceGate_CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var gate = new MemoryInfluenceGate(Registry(RecallContract()), ["send_email"]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await gate.InspectAsync(Call("send_email"), cancellation.Token));
    }

    [Fact]
    public async Task EnforcementResult_SerializationDoesNotExposeEffectiveContent()
    {
        var context = Context(WriteContract(), MemoryGateStage.BeforeWrite, "private-value");
        var decision = await Pipeline().EvaluateAsync(context);
        var result = await new MemoryGateDecisionExecutor(new MemoryGateCapabilities())
            .ExecuteAsync(context, decision);

        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("private-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("EffectiveContent", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAdmission_DeleteWithoutContent_AllowsContentIndependentDelete()
    {
        var contract = Contract("memory_delete", MemoryOperationKind.Delete);
        var context = Context(contract, MemoryGateStage.BeforeWrite, content: null);

        var verdict = await new MemoryWriteAdmissionGate().InspectAsync(context);

        Assert.Equal(MemoryGateAction.Allow, verdict.Action);
        Assert.Equal("memory.write.content_not_applicable", verdict.ReasonCode);
    }

    private static MemoryToolCallGate CallGate(
        MemoryGatePipeline pipeline,
        MemoryToolOperationRegistry registry,
        IMemoryToolContextAdapter adapter)
        => new(pipeline, registry, adapter);

    private static MemoryToolResultGate ResultGate(
        MemoryGatePipeline pipeline,
        MemoryToolOperationRegistry registry,
        IMemoryToolContextAdapter adapter)
        => new(pipeline, registry, adapter);

    private static MemoryGatePipeline Pipeline(
        IMemoryGate? gate = null,
        MemorySecurityProfile profile = MemorySecurityProfile.Enforce,
        MemoryGateCapabilities? capabilities = null)
        => new(
            gate is null ? [] : [gate],
            capabilities,
            new MemorySecurityPolicy(
                "tool-test",
                "1",
                profile,
                MemoryGateAction.Reject,
                MemoryCoverageLevel.Boundary),
            new FrozenTimeProvider());

    private static MemoryToolOperationRegistry Registry(params MemoryOperationContract[] contracts)
        => new(contracts);

    private static MemoryOperationContract WriteContract()
        => Contract("memory_write", MemoryOperationKind.Write);

    private static MemoryOperationContract PromoteContract()
        => Contract("memory_promote", MemoryOperationKind.Promote);

    private static MemoryOperationContract RecallContract()
        => Contract(
            "memory_recall",
            MemoryOperationKind.Recall,
            mayReturnSensitiveContent: true);

    private static MemoryOperationContract Contract(
        string name,
        MemoryOperationKind kind,
        MemorySurface surface = MemorySurface.Tool,
        bool mayReturnSensitiveContent = false)
        => new(
            name,
            kind,
            surface,
            ["content"],
            ["tenantId", "userId"],
            MemoryCategory.Fact,
            isSideEffecting: kind is not (MemoryOperationKind.Search or MemoryOperationKind.Recall),
            mayReturnSensitiveContent);

    private static AIFunction Function(string name)
        => AIFunctionFactory.Create((string content) => content, name);

    private static GatedToolCall Call(string name, string content = "candidate")
        => new(
            name,
            new Dictionary<string, object?> { ["content"] = content },
            "agent",
            0,
            0,
            1,
            false,
            Messages: null);

    private static GatedToolResult Result(string name, object? value = null)
        => new(
            name,
            new Dictionary<string, object?>(),
            value ?? "result",
            "agent",
            0,
            0,
            1,
            false,
            Messages: null);

    private static MemoryGateContext Context(
        MemoryOperationContract operation,
        MemoryGateStage stage,
        string? content)
        => new(
            "operation-1",
            stage,
            operation,
            "tool-provider",
            new MemorySecurityScope(tenantId: "tenant-a", userId: "user-a"),
            new MemoryProvenance(MemorySourceKind.User, "source", MemoryTrustLevel.Medium),
            content,
            budget: new MemoryBudgetSnapshot(),
            recordMetadata: stage is MemoryGateStage.AfterRead
                ? new MemoryRecordMetadata(
                    "memory-1",
                    new MemorySecurityScope(tenantId: "tenant-a", userId: "user-a"))
                : null);

    private sealed class FakeAdapter : IMemoryToolContextAdapter
    {
        public List<MemoryGateStage> CallStages { get; } = [];
        public int CallContextCount { get; private set; }
        public int ResultContextCount { get; private set; }
        public int ArgumentRewriteCount { get; private set; }
        public int ResultRewriteCount { get; private set; }
        public bool WrongStage { get; init; }
        public bool ReturnNullArguments { get; init; }
        public bool ReturnNullResult { get; init; }

        public MemoryGateContext CreateCallContext(
            GatedToolCall call,
            MemoryOperationContract operation,
            MemoryGateStage stage)
        {
            CallContextCount++;
            CallStages.Add(stage);
            object? content = null;
            call.Arguments?.TryGetValue("content", out content);
            return Context(
                operation,
                WrongStage ? MemoryGateStage.AfterDecision : stage,
                content?.ToString());
        }

        public MemoryGateContext CreateResultContext(
            GatedToolResult result,
            MemoryOperationContract operation)
        {
            ResultContextCount++;
            return Context(operation, MemoryGateStage.AfterRead, result.ResultText);
        }

        public IReadOnlyDictionary<string, object?> ApplySanitizedArguments(
            GatedToolCall call,
            MemoryOperationContract operation,
            string sanitizedContent)
        {
            ArgumentRewriteCount++;
            var copy = call.Arguments is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(call.Arguments, StringComparer.Ordinal);
            copy["content"] = sanitizedContent;
            return ReturnNullArguments ? null! : copy;
        }

        public object ApplySanitizedResult(
            GatedToolResult result,
            MemoryOperationContract operation,
            string sanitizedContent)
        {
            ResultRewriteCount++;
            return ReturnNullResult ? null! : sanitizedContent;
        }
    }

    private sealed class FixedGate(
        MemoryGateStage stages,
        MemoryGateAction action = MemoryGateAction.Allow,
        string reason = "memory.test.allow",
        string? sanitizedContent = null) : IMemoryGate
    {
        public string PolicyName => "memory.test.fixed";
        public GateCost Cost => GateCost.PureCode;
        public MemoryGateStage Stages => stages;
        public MemoryGateRequirements Requirements => MemoryGateRequirements.None;

        public ValueTask<MemoryGateVerdict> InspectAsync(
            MemoryGateContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(action switch
            {
                MemoryGateAction.Allow => MemoryGateVerdict.Allow(PolicyName, reason),
                MemoryGateAction.Sanitize => MemoryGateVerdict.Sanitize(
                    PolicyName,
                    sanitizedContent ?? throw new InvalidOperationException(),
                    reason),
                MemoryGateAction.Exclude => MemoryGateVerdict.Exclude(PolicyName, reason),
                MemoryGateAction.Quarantine => MemoryGateVerdict.Quarantine(PolicyName, reason),
                MemoryGateAction.RequireApproval => MemoryGateVerdict.RequireApproval(PolicyName, reason),
                _ => MemoryGateVerdict.Reject(PolicyName, reason),
            });
    }

    private sealed class FakeDecisionSink : IMemoryGateDecisionSink
    {
        public List<MemoryGateDecision> Decisions { get; } = [];

        public ValueTask RecordAsync(
            MemoryGateContext context,
            MemoryGateDecision decision,
            CancellationToken cancellationToken = default)
        {
            Decisions.Add(decision);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeQuarantineStore : IMemoryQuarantineStore
    {
        public List<MemoryQuarantineRequest> Requests { get; } = [];

        public ValueTask<MemoryQuarantineReceipt> StoreAsync(
            MemoryQuarantineRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new MemoryQuarantineReceipt(
                "quarantine-1",
                request.Context.OperationId,
                DateTimeOffset.UnixEpoch));
        }
    }

    private sealed class FakeApprovalHandler(bool approved) : IMemoryApprovalHandler
    {
        public List<MemoryApprovalRequest> Requests { get; } = [];

        public ValueTask<MemoryApprovalDecision> RequestApprovalAsync(
            MemoryApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new MemoryApprovalDecision(approved, "approval-1"));
        }
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
