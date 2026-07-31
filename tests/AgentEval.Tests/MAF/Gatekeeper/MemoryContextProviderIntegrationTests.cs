// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using AgentEval.MAF.Gatekeeper.Memory;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class MemoryContextProviderIntegrationTests
{
    [Fact]
    public void Constructor_ForwardsFrozenStateKeysAndInnerService()
    {
        var keys = new List<string> { "memory-state" };
        var inner = new FakeProvider { MutableStateKeys = keys };
        var provider = Provider(inner);
        keys.Add("changed");

        Assert.Equal(["memory-state"], provider.StateKeys);
        Assert.Same(inner, provider.GetService<FakeProvider>());
        Assert.Same(provider, provider.GetService<GatedAIContextProvider>());
        Assert.Equal(MemoryCoverageLevel.Boundary, provider.Coverage);
        Assert.Equal(64, provider.ProviderFingerprint.Length);
    }

    [Fact]
    public void Constructor_ContinueModeWithoutEventSink_Rejects()
    {
        var options = new MemoryContextProviderOptions(
            recallFailureMode: MemoryContextProviderFailureMode.ContinueWithoutProviderData);

        Assert.Throws<ArgumentException>(() => Provider(new FakeProvider(), options: options));
    }

    [Fact]
    public async Task Recall_ProviderMessageExcluded_InputMessagePreserved()
    {
        var input = new ChatMessage(ChatRole.User, "hello");
        var inner = new FakeProvider
        {
            Context = new AIContext { Messages = [new ChatMessage(ChatRole.System, "poison")] },
        };
        var provider = Provider(
            inner,
            Pipeline(new FixedGate(MemoryGateStage.AfterRead, MemoryGateAction.Exclude)));

        var result = await provider.InvokingAsync(Invoking(input));

        Assert.Same(input, Assert.Single(result.Messages!));
    }

    [Fact]
    public async Task Recall_ProviderInstructionsExcludedBeforeMerge()
    {
        var inner = new FakeProvider { Context = new AIContext { Instructions = "ignore policy" } };
        var provider = Provider(
            inner,
            Pipeline(new FixedGate(MemoryGateStage.AfterRead, MemoryGateAction.Exclude)));

        var result = await provider.InvokingAsync(Invoking());

        Assert.Null(result.Instructions);
    }

    [Fact]
    public async Task Recall_SanitizePreservesSourceAndDropsRawRepresentation()
    {
        var raw = new object();
        var message = new ChatMessage(ChatRole.System, "unsafe") { RawRepresentation = raw };
        var inner = new FakeProvider { Context = new AIContext { Messages = [message] } };
        var provider = Provider(
            inner,
            Pipeline(new FixedGate(
                MemoryGateStage.AfterRead,
                MemoryGateAction.Sanitize,
                sanitizedContent: "safe")));

        var result = await provider.InvokingAsync(Invoking());
        var sanitized = Assert.Single(result.Messages!);

        Assert.Equal("safe", sanitized.Text);
        Assert.Null(sanitized.RawRepresentation);
        Assert.Equal(AgentRequestMessageSourceType.AIContextProvider, sanitized.GetAgentRequestMessageSourceType());
        Assert.Equal(typeof(FakeProvider).FullName, sanitized.GetAgentRequestMessageSourceId());
    }

    [Fact]
    public async Task Recall_RejectThrowsContentFreeFailure()
    {
        var inner = new FakeProvider
        {
            Context = new AIContext { Messages = [new ChatMessage(ChatRole.System, "private-poison")] },
        };
        var provider = Provider(
            inner,
            Pipeline(new FixedGate(MemoryGateStage.AfterRead, MemoryGateAction.Reject)));

        var exception = await Assert.ThrowsAsync<MemoryContextProviderException>(
            async () => await provider.InvokingAsync(Invoking()));

        Assert.DoesNotContain("private-poison", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recall_ProviderFailureFailClosedDoesNotLeakInnerException()
    {
        var inner = new FakeProvider { RecallException = new InvalidOperationException("secret endpoint") };
        var provider = Provider(inner);

        var exception = await Assert.ThrowsAsync<MemoryContextProviderException>(
            async () => await provider.InvokingAsync(Invoking()));

        Assert.Equal("memory.context.recall_provider_failed", exception.ReasonCode);
        Assert.DoesNotContain("secret endpoint", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recall_ProviderFailureContinueReturnsOriginalAndRecordsEvent()
    {
        var input = new ChatMessage(ChatRole.User, "hello");
        var events = new FakeEventSink();
        var inner = new FakeProvider { RecallException = new InvalidOperationException("provider detail") };
        var provider = Provider(
            inner,
            eventSink: events,
            options: new MemoryContextProviderOptions(
                recallFailureMode: MemoryContextProviderFailureMode.ContinueWithoutProviderData));

        var result = await provider.InvokingAsync(Invoking(input));

        Assert.Same(input, Assert.Single(result.Messages!));
        Assert.Equal(MemoryContextProviderEventKind.RecallProviderFailure, Assert.Single(events.Events).Kind);
    }

    [Fact]
    public async Task Recall_CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var provider = Provider(new FakeProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await provider.InvokingAsync(Invoking(), cancellation.Token));
    }

    [Fact]
    public async Task DynamicTool_UnclassifiedMemoryLikeFailsEnforcingRecall()
    {
        var inner = new FakeProvider
        {
            Context = new AIContext { Tools = [Function("remember_unknown")] },
        };
        var provider = Provider(inner);

        var exception = await Assert.ThrowsAsync<MemoryContextProviderException>(
            async () => await provider.InvokingAsync(Invoking()));

        Assert.Equal("memory.context.dynamic_tool_unsupported", exception.ReasonCode);
    }

    [Fact]
    public async Task DynamicTool_RegisteredReadWithMatchingAdaptersIsIncluded()
    {
        var operation = new MemoryOperationContract(
            "dynamic_memory_recall",
            MemoryOperationKind.Recall,
            MemorySurface.Tool,
            [],
            [],
            MemoryCategory.Message,
            isSideEffecting: false,
            mayReturnSensitiveContent: false);
        var registry = new MemoryToolOperationRegistry([operation]);
        var pipeline = Pipeline();
        var toolAdapter = new FakeToolAdapter();
        var callGate = new MemoryToolCallGate(pipeline, registry, toolAdapter);
        var resultGate = new MemoryToolResultGate(pipeline, registry, toolAdapter);
        var tool = Function("dynamic_memory_recall");
        var inner = new FakeProvider { Context = new AIContext { Tools = [tool] } };
        var provider = new GatedAIContextProvider(
            inner,
            pipeline,
            registry,
            new FakeContextAdapter(),
            callGate,
            resultGate);

        var result = await provider.InvokingAsync(Invoking());

        Assert.Same(tool, Assert.Single(result.Tools!));
        Assert.Equal(MemoryCoverageLevel.Boundary, provider.Coverage);
    }

    [Fact]
    public async Task DynamicTool_ContinueModeExcludesOnlyUnsupportedTool()
    {
        var weather = Function("weather");
        var memory = Function("remember_unknown");
        var events = new FakeEventSink();
        var inner = new FakeProvider { Context = new AIContext { Tools = [weather, memory] } };
        var provider = Provider(
            inner,
            eventSink: events,
            options: new MemoryContextProviderOptions(
                recallFailureMode: MemoryContextProviderFailureMode.ContinueWithoutProviderData));

        var result = await provider.InvokingAsync(Invoking());

        Assert.Same(weather, Assert.Single(result.Tools!));
        Assert.Equal(MemoryContextProviderEventKind.DynamicToolExcluded, Assert.Single(events.Events).Kind);
    }

    [Fact]
    public async Task DynamicTool_ObserveIncludesUnsupportedAndRecordsWouldExclude()
    {
        var memory = Function("remember_unknown");
        var events = new FakeEventSink();
        var inner = new FakeProvider { Context = new AIContext { Tools = [memory] } };
        var provider = Provider(
            inner,
            Pipeline(profile: MemorySecurityProfile.Observe),
            eventSink: events);

        var result = await provider.InvokingAsync(Invoking());

        Assert.Same(memory, Assert.Single(result.Tools!));
        Assert.Equal("memory.context.dynamic_tool_would_exclude", Assert.Single(events.Events).ReasonCode);
    }

    [Fact]
    public async Task Write_RejectFiltersSourceBeforeInnerProvider()
    {
        var inner = new FakeProvider();
        var provider = Provider(
            inner,
            Pipeline(new FixedGate(MemoryGateStage.BeforeWrite, MemoryGateAction.Reject)));

        await provider.InvokedAsync(Invoked(
            [new ChatMessage(ChatRole.User, "private request")],
            [new ChatMessage(ChatRole.Assistant, "private response")]));

        Assert.Empty(inner.StoredRequests!);
        Assert.Empty(inner.StoredResponses!);
    }

    [Fact]
    public async Task Write_SanitizeRewritesBeforeInnerProvider()
    {
        var inner = new FakeProvider();
        var provider = Provider(
            inner,
            Pipeline(new FixedGate(
                MemoryGateStage.BeforeWrite,
                MemoryGateAction.Sanitize,
                sanitizedContent: "redacted")));

        await provider.InvokedAsync(Invoked(
            [new ChatMessage(ChatRole.User, "secret")],
            []));

        Assert.Equal("redacted", Assert.Single(inner.StoredRequests!).Text);
    }

    [Fact]
    public async Task Write_QuarantineStoresOnceAndDoesNotReachInner()
    {
        var store = new FakeQuarantineStore();
        var pipeline = Pipeline(
            new FixedGate(MemoryGateStage.BeforeWrite, MemoryGateAction.Quarantine),
            capabilities: new MemoryGateCapabilities(quarantineStore: store));
        var inner = new FakeProvider();
        var provider = Provider(inner, pipeline);

        await provider.InvokedAsync(Invoked(
            [new ChatMessage(ChatRole.User, "candidate")],
            []));

        Assert.Single(store.Requests);
        Assert.Empty(inner.StoredRequests!);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Write_ApprovalControlsDelegatedPersistence(bool approved, int expectedCount)
    {
        var handler = new FakeApprovalHandler(approved);
        var pipeline = Pipeline(
            new FixedGate(MemoryGateStage.BeforeWrite, MemoryGateAction.RequireApproval),
            capabilities: new MemoryGateCapabilities(approvalHandler: handler));
        var inner = new FakeProvider();
        var provider = Provider(inner, pipeline);

        await provider.InvokedAsync(Invoked(
            [new ChatMessage(ChatRole.User, "candidate")],
            []));

        Assert.Equal(expectedCount, inner.StoredRequests!.Count);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Write_ProviderFailureFailClosedDoesNotLeakInnerException()
    {
        var inner = new FakeProvider { WriteException = new InvalidOperationException("storage credential") };
        var provider = Provider(inner);

        var exception = await Assert.ThrowsAsync<MemoryContextProviderException>(
            async () => await provider.InvokedAsync(Invoked([], [])));

        Assert.Equal("memory.context.write_provider_failed", exception.ReasonCode);
        Assert.DoesNotContain("storage credential", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_ProviderFailureContinueRecordsNoFalseSuccessEvent()
    {
        var events = new FakeEventSink();
        var inner = new FakeProvider { WriteException = new InvalidOperationException("detail") };
        var provider = Provider(
            inner,
            eventSink: events,
            options: new MemoryContextProviderOptions(
                writeFailureMode: MemoryContextProviderFailureMode.ContinueWithoutProviderData));

        await provider.InvokedAsync(Invoked([], []));

        Assert.Equal(MemoryContextProviderEventKind.WriteProviderFailure, Assert.Single(events.Events).Kind);
    }

    [Fact]
    public async Task Write_ConcurrentInvocationsKeepContextsIndependent()
    {
        var adapter = new FakeContextAdapter();
        var inner = new FakeProvider();
        var provider = Provider(inner, adapter: adapter);
        var first = Invoked([new ChatMessage(ChatRole.User, "first")], []);
        var second = Invoked([new ChatMessage(ChatRole.User, "second")], []);

        await Task.WhenAll(
            provider.InvokedAsync(first).AsTask(),
            provider.InvokedAsync(second).AsTask());

        Assert.Equal(2, adapter.WriteContexts.Count);
        Assert.Contains(adapter.WriteContexts, context => context.Content == "first");
        Assert.Contains(adapter.WriteContexts, context => context.Content == "second");
    }

    [Fact]
    public async Task NativeHook_CandidateWriteRejectsBeforeProviderCommit()
    {
        var hook = new MemoryProviderNativeGate(
            new MemoryProviderNativeDescriptor(
                "native-provider",
                "1",
                MemoryProviderNativeCapabilities.CandidateWrites),
            Pipeline(new FixedGate(MemoryGateStage.BeforeWrite, MemoryGateAction.Reject)));

        var result = await hook.GateCandidateWriteAsync(
            GateContext(WriteContract(MemorySurface.ProviderNative), MemoryGateStage.BeforeWrite, "candidate"));

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task NativeHook_RecalledItemSanitizesAndSerializationOmitsContent()
    {
        var hook = new MemoryProviderNativeGate(
            new MemoryProviderNativeDescriptor(
                "native-provider",
                "1",
                MemoryProviderNativeCapabilities.RecalledItems),
            Pipeline(new FixedGate(
                MemoryGateStage.AfterRead,
                MemoryGateAction.Sanitize,
                sanitizedContent: "safe")));

        var result = await hook.GateRecalledItemAsync(
            GateContext(RecallContract(MemorySurface.ProviderNative), MemoryGateStage.AfterRead, "private"));
        var json = JsonSerializer.Serialize(result);

        Assert.True(result.Include);
        Assert.Equal("safe", result.EffectiveContent);
        Assert.DoesNotContain("private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("EffectiveContent", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeHook_MissingDeclaredCapabilityFailsClosed()
    {
        var hook = new MemoryProviderNativeGate(
            new MemoryProviderNativeDescriptor(
                "native-provider",
                "1",
                MemoryProviderNativeCapabilities.CandidateWrites),
            Pipeline());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await hook.GateRecalledItemAsync(
                GateContext(RecallContract(MemorySurface.ProviderNative), MemoryGateStage.AfterRead, "value")));
    }

    [Fact]
    public void NativeDescriptor_UnknownOrEmptyCapabilitiesRejects()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryProviderNativeDescriptor(
            "provider",
            "1",
            MemoryProviderNativeCapabilities.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryProviderNativeDescriptor(
            "provider",
            "1",
            (MemoryProviderNativeCapabilities)128));
    }

    [Fact]
    public async Task Recall_OpaqueStructuredMessageFailsClosedBeforeModelMerge()
    {
        var structured = new ChatMessage(
            ChatRole.System,
            [new FunctionCallContent("call-1", "send_secret", new Dictionary<string, object?>())]);
        var inner = new FakeProvider { Context = new AIContext { Messages = [structured] } };
        var provider = Provider(inner);

        var exception = await Assert.ThrowsAsync<MemoryContextProviderException>(
            async () => await provider.InvokingAsync(Invoking()));

        Assert.Equal("memory.context.structured_message_unsupported", exception.ReasonCode);
    }

    [Fact]
    public async Task Write_OpaqueStructuredMessageNeverReachesProviderStorage()
    {
        var structured = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-1", "private-result")]);
        var inner = new FakeProvider();
        var provider = Provider(inner);

        await provider.InvokedAsync(Invoked([structured], []));

        Assert.Empty(inner.StoredRequests!);
    }

    [Fact]
    public async Task Recall_AllowedMessageStillNormalizesOpaqueRawRepresentation()
    {
        var source = new ChatMessage(ChatRole.System, "safe text") { RawRepresentation = new object() };
        var inner = new FakeProvider { Context = new AIContext { Messages = [source] } };
        var provider = Provider(inner);

        var result = Assert.Single((await provider.InvokingAsync(Invoking())).Messages!);

        Assert.Equal("safe text", result.Text);
        Assert.Null(result.RawRepresentation);
        Assert.Single(result.Contents);
        Assert.IsType<TextContent>(result.Contents[0]);
    }

    [Fact]
    public async Task NativeHook_DifferentProviderIdentityFailsClosed()
    {
        var hook = new MemoryProviderNativeGate(
            new MemoryProviderNativeDescriptor(
                "expected-provider",
                "1",
                MemoryProviderNativeCapabilities.CandidateWrites),
            Pipeline());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await hook.GateCandidateWriteAsync(
                GateContext(WriteContract(MemorySurface.ProviderNative), MemoryGateStage.BeforeWrite, "value")));
    }

    private static GatedAIContextProvider Provider(
        FakeProvider inner,
        MemoryGatePipeline? pipeline = null,
        FakeContextAdapter? adapter = null,
        FakeEventSink? eventSink = null,
        MemoryContextProviderOptions? options = null)
        => new(
            inner,
            pipeline ?? Pipeline(),
            new MemoryToolOperationRegistry([]),
            adapter ?? new FakeContextAdapter(),
            eventSink: eventSink,
            options: options);

    private static MemoryGatePipeline Pipeline(
        IMemoryGate? gate = null,
        MemorySecurityProfile profile = MemorySecurityProfile.Enforce,
        MemoryGateCapabilities? capabilities = null)
        => new(
            gate is null ? [] : [gate],
            capabilities,
            new MemorySecurityPolicy(
                "context-test",
                "1",
                profile,
                MemoryGateAction.Reject,
                profile is MemorySecurityProfile.Observe
                    ? MemoryCoverageLevel.ObserveOnly
                    : MemoryCoverageLevel.Boundary),
            new FrozenTimeProvider());

#pragma warning disable MAAI001 // Tests exercise the exact MAF provider lifecycle contract.
    private static AIContextProvider.InvokingContext Invoking(params ChatMessage[] messages)
        => new(Agent(), session: null, new AIContext { Messages = messages });

    private static AIContextProvider.InvokedContext Invoked(
        IEnumerable<ChatMessage> requests,
        IEnumerable<ChatMessage> responses)
        => new(Agent(), session: null, requests, responses);
#pragma warning restore MAAI001

    private static ChatClientAgent Agent()
        => new(
            new ScriptedChatClient().AddText("ok"),
            new ChatClientAgentOptions { Name = "context-test-agent" });

    private static AIFunction Function(string name)
        => AIFunctionFactory.Create(() => "ok", name);

    private static MemoryOperationContract WriteContract(MemorySurface surface = MemorySurface.AIContextProvider)
        => new(
            "context_write",
            MemoryOperationKind.Write,
            surface,
            ["content"],
            [],
            MemoryCategory.Message,
            isSideEffecting: true,
            mayReturnSensitiveContent: false);

    private static MemoryOperationContract RecallContract(MemorySurface surface = MemorySurface.AIContextProvider)
        => new(
            "context_recall",
            MemoryOperationKind.Recall,
            surface,
            [],
            [],
            MemoryCategory.Message,
            isSideEffecting: false,
            mayReturnSensitiveContent: true);

    private static MemoryGateContext GateContext(
        MemoryOperationContract operation,
        MemoryGateStage stage,
        string? content)
        => new(
            $"operation-{Guid.NewGuid():N}",
            stage,
            operation,
            operation.Surface is MemorySurface.ProviderNative ? "native-provider" : "context-provider",
            new MemorySecurityScope(tenantId: "tenant-a", userId: "user-a"),
            new MemoryProvenance(MemorySourceKind.ContextProvider, "provider", MemoryTrustLevel.Medium),
            content,
            budget: new MemoryBudgetSnapshot());

    private sealed class FakeProvider : AIContextProvider
    {
        public AIContext Context { get; init; } = new();
        public Exception? RecallException { get; init; }
        public Exception? WriteException { get; init; }
        public IReadOnlyList<string> MutableStateKeys { get; init; } = ["fake-provider"];
        public IReadOnlyList<ChatMessage>? StoredRequests { get; private set; }
        public IReadOnlyList<ChatMessage>? StoredResponses { get; private set; }
        public override IReadOnlyList<string> StateKeys => MutableStateKeys;

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
            => RecallException is null
                ? ValueTask.FromResult(Context)
                : ValueTask.FromException<AIContext>(RecallException);

        protected override ValueTask StoreAIContextAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default)
        {
            if (WriteException is not null)
            {
                return ValueTask.FromException(WriteException);
            }

            StoredRequests = context.RequestMessages.ToList();
            StoredResponses = context.ResponseMessages!.ToList();
            return default;
        }
    }

    private sealed class FakeContextAdapter : IMemoryContextProviderAdapter
    {
        private int _operationId;
        public System.Collections.Concurrent.ConcurrentBag<MemoryGateContext> WriteContexts { get; } = [];

        public MemoryGateContext CreateRecallInstructionsContext(
            AIContextProvider.InvokingContext context,
            string instructions)
            => Create(RecallContract(), MemoryGateStage.AfterRead, instructions);

        public MemoryGateContext CreateRecallMessageContext(
            AIContextProvider.InvokingContext context,
            ChatMessage message,
            int ordinal)
            => Create(RecallContract(), MemoryGateStage.AfterRead, message.Text);

        public MemoryGateContext CreateWriteMessageContext(
            AIContextProvider.InvokedContext context,
            ChatMessage message,
            MemoryContextMessageOrigin origin,
            int ordinal)
        {
            var created = Create(WriteContract(), MemoryGateStage.BeforeWrite, message.Text);
            WriteContexts.Add(created);
            return created;
        }

        public ChatMessage ApplySanitizedMessage(ChatMessage message, string sanitizedContent)
        {
            var clone = message.Clone();
            clone.Contents = [new TextContent(sanitizedContent)];
            clone.RawRepresentation = null;
            return clone;
        }

        private MemoryGateContext Create(
            MemoryOperationContract operation,
            MemoryGateStage stage,
            string? content)
            => new(
                $"adapter-{Interlocked.Increment(ref _operationId)}",
                stage,
                operation,
                "context-provider",
                new MemorySecurityScope(tenantId: "tenant-a", userId: "user-a"),
                new MemoryProvenance(
                    stage is MemoryGateStage.AfterRead
                        ? MemorySourceKind.ContextProvider
                        : MemorySourceKind.User,
                    "source",
                    MemoryTrustLevel.Medium),
                content,
                budget: new MemoryBudgetSnapshot());
    }

    private sealed class FakeToolAdapter : IMemoryToolContextAdapter
    {
        public MemoryGateContext CreateCallContext(
            GatedToolCall call,
            MemoryOperationContract operation,
            MemoryGateStage stage)
            => GateContext(operation, stage, content: null);

        public MemoryGateContext CreateResultContext(
            GatedToolResult result,
            MemoryOperationContract operation)
            => GateContext(operation, MemoryGateStage.AfterRead, result.ResultText);

        public IReadOnlyDictionary<string, object?> ApplySanitizedArguments(
            GatedToolCall call,
            MemoryOperationContract operation,
            string sanitizedContent)
            => call.Arguments ?? new Dictionary<string, object?>();

        public object ApplySanitizedResult(
            GatedToolResult result,
            MemoryOperationContract operation,
            string sanitizedContent)
            => sanitizedContent;
    }

    private sealed class FixedGate(
        MemoryGateStage stages,
        MemoryGateAction action = MemoryGateAction.Allow,
        string reason = "memory.test.decision",
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

    private sealed class FakeEventSink : IMemoryContextProviderEventSink
    {
        public List<MemoryContextProviderEvent> Events { get; } = [];

        public ValueTask RecordAsync(
            MemoryContextProviderEvent @event,
            CancellationToken cancellationToken = default)
        {
            Events.Add(@event);
            return default;
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
                request.DecisionReceipt.OperationId,
                DateTimeOffset.Parse("2026-07-31T00:00:00Z")));
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
            return ValueTask.FromResult(new MemoryApprovalDecision(
                approved,
                approved ? "approved" : "denied"));
        }
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
            => DateTimeOffset.Parse("2026-07-31T00:00:00Z");
    }
}
