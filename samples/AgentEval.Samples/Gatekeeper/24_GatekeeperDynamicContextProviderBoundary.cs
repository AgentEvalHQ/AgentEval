// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.MAF.Gatekeeper;
using AgentEval.MAF.Gatekeeper.Memory;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>Offline dynamic-tool inventory and context-provider boundary demonstration.</summary>
public static class GatekeeperDynamicContextProviderBoundary
{
    public static async Task RunAsync()
    {
        GatekeeperSampleContractRenderer.Print("24");
        Console.WriteLine("\n=== Gatekeeper — Dynamic Context Provider Boundary (offline) ===\n");

        var weather = Function("weather");
        var unsupportedMemoryTool = Function("remember_unknown");
        var inner = new DynamicToolProvider(weather, unsupportedMemoryTool);
        var events = new EventSink();
        var gatedProvider = new GatedAIContextProvider(
            inner,
            Pipeline(),
            new MemoryToolOperationRegistry([]),
            new ContextAdapter(),
            eventSink: events,
            options: new MemoryContextProviderOptions(
                recallFailureMode: MemoryContextProviderFailureMode.ContinueWithoutProviderData));

        var agent = new ChatClientAgent(
            new ScriptedChatClient().AddText("ok"),
            new ChatClientAgentOptions
            {
                Name = "dynamic-provider-sample",
                ChatOptions = new ChatOptions { MaxOutputTokens = 64 },
                AIContextProviders = [gatedProvider],
            });

        var report = GatekeeperCoverageAnalyzer.Analyze(agent);
        Require(!report.ToolInventoryAvailable,
            "a dynamic-only provider must not receive vacuous 100% static coverage");
        RequireThrows<ToolInventoryUnavailableException>(
            () => GatekeeperCoverageAnalyzer.AnalyzeOrThrow(agent),
            "promotion must fail closed when the runtime tool inventory is unavailable");

#pragma warning disable MAAI001 // Sample exercises the current MAF AIContextProvider lifecycle deliberately.
        var context = await gatedProvider.InvokingAsync(
            new AIContextProvider.InvokingContext(agent, session: null, new AIContext()));
#pragma warning restore MAAI001

        var admitted = context.Tools?.Select(tool => tool.Name).ToArray() ?? [];
        Require(admitted.SequenceEqual(["weather"], StringComparer.Ordinal),
            "the real provider boundary must exclude only the unsupported dynamic memory tool");
        Require(events.Events is [{ Kind: MemoryContextProviderEventKind.DynamicToolExcluded }],
            "dynamic exclusion must emit one privacy-minimized event");
        Require(gatedProvider.Coverage == MemoryCoverageLevel.Boundary,
            "the provider must report boundary coverage, not fabricated static tool coverage");

        Console.WriteLine("   static analyzer:      inventory unavailable → promotion refused");
        Console.WriteLine("   provider boundary:    weather admitted; unsupported memory tool excluded");
        Console.WriteLine("   coverage claim:       boundary only, with one content-free exclusion event");
        Console.WriteLine("   ✅ runtime-contributed tools were handled at their real provider seam.");
    }

    private static MemoryGatePipeline Pipeline() => new(
        [],
        capabilities: null,
        new MemorySecurityPolicy(
            "dynamic-provider-sample",
            "1",
            MemorySecurityProfile.Enforce,
            MemoryGateAction.Reject,
            MemoryCoverageLevel.Boundary),
        TimeProvider.System);

    private static AIFunction Function(string name) => AIFunctionFactory.Create(() => "ok", name);

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

        throw new InvalidOperationException("Dynamic-provider sample failed: " + message + ".");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Dynamic-provider sample failed: " + message + ".");
        }
    }

    private sealed class DynamicToolProvider(params AITool[] tools) : AIContextProvider
    {
        private readonly AIContext _context = new() { Tools = tools };

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_context);

        protected override ValueTask StoreAIContextAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default) => default;
    }

    private sealed class ContextAdapter : IMemoryContextProviderAdapter
    {
        private int _operationId;

        public MemoryGateContext CreateRecallInstructionsContext(
            AIContextProvider.InvokingContext context,
            string instructions) => Create(MemoryGateStage.AfterRead, instructions);

        public MemoryGateContext CreateRecallMessageContext(
            AIContextProvider.InvokingContext context,
            ChatMessage message,
            int ordinal) => Create(MemoryGateStage.AfterRead, message.Text);

        public MemoryGateContext CreateWriteMessageContext(
            AIContextProvider.InvokedContext context,
            ChatMessage message,
            MemoryContextMessageOrigin origin,
            int ordinal) => Create(MemoryGateStage.BeforeWrite, message.Text);

        public ChatMessage ApplySanitizedMessage(ChatMessage message, string sanitizedContent)
        {
            var clone = message.Clone();
            clone.Contents = [new TextContent(sanitizedContent)];
            clone.RawRepresentation = null;
            return clone;
        }

        private MemoryGateContext Create(MemoryGateStage stage, string? content) => new(
            $"dynamic-provider-{Interlocked.Increment(ref _operationId)}",
            stage,
            new MemoryOperationContract(
                stage == MemoryGateStage.BeforeWrite ? "context_write" : "context_recall",
                stage == MemoryGateStage.BeforeWrite ? MemoryOperationKind.Write : MemoryOperationKind.Recall,
                MemorySurface.AIContextProvider,
                stage == MemoryGateStage.BeforeWrite ? ["content"] : [],
                [],
                MemoryCategory.Message,
                isSideEffecting: stage == MemoryGateStage.BeforeWrite,
                mayReturnSensitiveContent: stage != MemoryGateStage.BeforeWrite),
            "context-provider",
            new MemorySecurityScope("tenant-a", "user-a"),
            new MemoryProvenance(MemorySourceKind.ContextProvider, "provider", MemoryTrustLevel.Medium),
            content,
            budget: new MemoryBudgetSnapshot());
    }

    private sealed class EventSink : IMemoryContextProviderEventSink
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
}
