// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.MAF.Gatekeeper;
using AgentEval.MAF.Gatekeeper.Memory;
using AgentEval.MAF.Gatekeeper.MemorySecurity;
using AgentEval.RedTeam.MemorySecurity;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using GatekeeperMode = AgentEval.MAF.Gatekeeper.GatekeeperEnforcement;

namespace AgentEval.Samples;

public static class MemorySecurityReleaseValidation
{
    public static async Task RunAsync()
    {
        LocalTool();
        LocalMcp();
        GenericProvider();
        NativeProvider();
        await CrossSessionAsync();
        ObserveRollout();
        QuarantineRollback();
        HostedRefusal();
        Console.WriteLine("Memory-security release samples: 8/8 passed (offline).");
    }

    private static void LocalTool()
    {
        var tool = Function("memory_write");
        var pipeline = new MemoryGatePipeline(
            [new MemoryScopeIntegrityGate()],
            new MemoryGateCapabilities(scopeResolver: new HostScopeResolver()),
            Policy("sample-local", MemoryCoverageLevel.Boundary));
        var report = Compose([tool], new MemoryProtectionOptions(
            pipeline,
            new MemoryToolOperationRegistry([Operation("memory_write", MemoryOperationKind.Write, MemorySurface.Tool)]),
            new ToolAdapter()));
        Require(One(report.ToolCoverage.Entries).Coverage is MemoryCoverageLevel.FullLifecycle, "local tool");
        Pass("1 local tool with host-resolved scope", report);
    }

    private static void LocalMcp()
    {
        var tool = Function("memory_write");
        var schema = MemoryMcpSchema.ComputeFingerprint(tool.JsonSchema.GetRawText());
        var client = new MemoryMcpClientOperationRegistry([
            new MemoryMcpClientOperationContract(
                "owned-memory", "1", MemoryMcpTransport.InProcess, schema,
                Operation("memory_write", MemoryOperationKind.Write, MemorySurface.LocalMcp)),
        ]);
        var serverRegistry = new MemoryMcpServerOperationRegistry(
            "owned-memory", "1", MemoryMcpTransport.InProcess,
            [new MemoryMcpServerOperationContract(
                schema, Operation("memory_write", MemoryOperationKind.Write, MemorySurface.McpServer))]);
        var pipeline = new MemoryGatePipeline(
            [new StageGate(MemoryGateStage.BeforeWrite)],
            policy: Policy("sample-mcp", MemoryCoverageLevel.FullLifecycle));
        var server = new MemoryMcpServerGate<string, string>(pipeline, serverRegistry, new ServerAdapter());
        var report = Compose([tool], new MemoryProtectionOptions(pipeline, client.OperationRegistry, new ToolAdapter())
        {
            LocalMcpRegistry = client,
            LocalMcpBindings = [new MemoryMcpClientToolBinding(
                tool, "owned-memory", "1", MemoryMcpTransport.InProcess, schema)],
            OwnedMcpServer = server.CoverageEvidence,
        });
        Require(One(report.McpCoverage.Entries).Coverage is MemoryCoverageLevel.FullLifecycle, "local MCP");
        Pass("2 local MCP plus owned server", report);
    }

    private static void GenericProvider()
    {
        var pipeline = new MemoryGatePipeline([], policy: Policy("sample-context", MemoryCoverageLevel.Boundary));
        var inner = new MockMemoryAIContextProvider(
            new MockMemorySqlStore(), new MockMemoryScope("tenant-a", "user-a"), "preference");
        var gated = new GatedAIContextProvider(
            inner, pipeline, new MemoryToolOperationRegistry([]), new ContextAdapter());
        var report = Compose([], new MemoryProtectionOptions(
            pipeline, new MemoryToolOperationRegistry([]), new ToolAdapter())
        {
            ContextProviders = [gated],
        });
        Require(One(report.ProviderCoverage).Coverage is MemoryCoverageLevel.Boundary, "generic provider");
        Pass("3 generic context provider (Boundary)", report);
    }

    private static void NativeProvider()
    {
        var pipeline = new MemoryGatePipeline([], policy: Policy("sample-native", MemoryCoverageLevel.FullLifecycle));
        var native = new MemoryProviderNativeGate(
            new MemoryProviderNativeDescriptor(
                "sample-native", "1",
                MemoryProviderNativeCapabilities.CandidateWrites | MemoryProviderNativeCapabilities.RecalledItems),
            pipeline);
        var report = Compose([], new MemoryProtectionOptions(
            pipeline, new MemoryToolOperationRegistry([]), new ToolAdapter())
        {
            ProviderNativeGates = [native],
        });
        Require(One(report.ProviderCoverage).Coverage is MemoryCoverageLevel.FullLifecycle, "native provider");
        Pass("4 provider-native full lifecycle", report);
    }

    private static async Task CrossSessionAsync()
    {
        var store = new MockMemorySqlStore();
        var a = new MockMemoryScope("tenant-a", "user-a");
        var b = new MockMemoryScope("tenant-a", "user-b");
        var browser = new MockMemoryInjectionSource(
            MemoryAttackDeliverySurface.BrowserDocument, "browser-doc", "untrusted preference");
        var email = new MockMemoryInjectionSource(
            MemoryAttackDeliverySurface.Email, "email-message", "separate untrusted claim");
        store.Write(a, await browser.FetchAsync(), browser.SourceId);
        _ = await email.FetchAsync();
        var restarted = store.Restart();
        Require(restarted.Recall(a, "preference").Count == 1, "persistence");
        Require(restarted.Recall(b, "preference").Count == 0, "isolation");
        Console.WriteLine("  ✅ 5 cross-session mocked browser/email benchmark");
    }

    private static void ObserveRollout()
    {
        var tool = Function("memory_write");
        var pipeline = new MemoryGatePipeline(
            [],
            policy: new MemorySecurityPolicy(
                "sample-rollout", "observe", MemorySecurityProfile.Observe,
                MemoryGateAction.Quarantine, MemoryCoverageLevel.ObserveOnly));
        var captured = new GatekeeperOptions();
        Agent([tool]).AsBuilder().UseGatekeeper(GatekeeperMode.Observe, options =>
        {
            captured = options;
            options.Trace = new AgentTrace();
            options.BannerWriter = null;
            options.KnownTools = [tool];
            options.ProtectMemory(new MemoryProtectionOptions(
                pipeline,
                new MemoryToolOperationRegistry([
                    Operation("memory_write", MemoryOperationKind.Write, MemorySurface.Tool),
                ]),
                new ToolAdapter()));
        });
        Require(captured.MemoryProtectionReport!.Profile is MemorySecurityProfile.Observe, "observe rollout");
        Pass("6 observe-to-enforce calibration starting point", captured.MemoryProtectionReport);
    }

    private static void QuarantineRollback()
    {
        var scope = new MockMemoryScope("tenant-a", "user-a");
        var record = new MockMemorySqlStore().Write(
            scope, "candidate under review", "browser-doc", quarantined: true);
        var quarantine = new MockMemoryQuarantineStore();
        quarantine.Quarantine(record);
        Require(quarantine.Records.Count == 1, "quarantine");
        Require(quarantine.Rollback(record.RecordId), "rollback");
        Console.WriteLine("  ✅ 7 quarantine review and rollback");
    }

    private static void HostedRefusal()
    {
        var pipeline = new MemoryGatePipeline([], policy: Policy("sample-hosted", MemoryCoverageLevel.Boundary));
        var hosted = new MemoryHostedMcpOperationContract(
            "opaque-hosted", "1", new string('a', 64),
            Operation("memory_write", MemoryOperationKind.Write, MemorySurface.HostedMcp),
            MemoryHostedMcpApprovalMode.Never, MemoryHostedMcpCallbackCapabilities.None);
        try
        {
            _ = Compose([], new MemoryProtectionOptions(
                pipeline, new MemoryToolOperationRegistry([]), new ToolAdapter())
            {
                HostedMcpContracts = [hosted],
            });
            throw new InvalidOperationException("Opaque hosted MCP unexpectedly passed.");
        }
        catch (MemoryProtectionCoverageException)
        {
            Console.WriteLine("  ✅ 8 opaque hosted MCP intentionally refused");
        }
    }

    private static MemoryProtectionReport Compose(
        IReadOnlyList<AITool> tools,
        MemoryProtectionOptions protection)
    {
        var captured = new GatekeeperOptions();
        Agent(tools).AsBuilder().UseGatekeeper(GatekeeperMode.Terminate, options =>
        {
            captured = options;
            options.KnownTools = tools;
            options.ProtectMemory(protection);
        });
        return captured.MemoryProtectionReport!;
    }

    private static ChatClientAgent Agent(IReadOnlyList<AITool> tools)
        => new(
            new ScriptedChatClient(),
            new ChatClientAgentOptions
            {
                Name = "memory-security-release",
                ChatOptions = new ChatOptions { Tools = tools.ToList() },
            });

    private static AIFunction Function(string name)
        => AIFunctionFactory.Create((string content) => content, name);

    private static MemorySecurityPolicy Policy(string id, MemoryCoverageLevel minimum)
        => new(id, "1", MemorySecurityProfile.Enforce, MemoryGateAction.Reject, minimum);

    private static MemoryOperationContract Operation(
        string name,
        MemoryOperationKind kind,
        MemorySurface surface)
        => new(
            name, kind, surface, ["content"], ["tenantId", "userId"],
            MemoryCategory.Fact, isSideEffecting: true, mayReturnSensitiveContent: false);

    private static T One<T>(IReadOnlyList<T> values)
        => values.Count == 1 ? values[0] : throw new InvalidOperationException("Expected one entry.");

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Sample failed: " + label);
    }

    private static void Pass(string label, MemoryProtectionReport report)
        => Console.WriteLine("  ✅ " + label + " [" + report.ConfigurationFingerprint[..12] + "]");

    private sealed class HostScopeResolver : IMemoryScopeResolver
    {
        public MemorySecurityScope Resolve(AgentSession session, string? agentName)
            => new(tenantId: "tenant-a", userId: "user-a", agentId: agentName ?? "agent");
    }

    private sealed class StageGate(MemoryGateStage stages) : IMemoryGate
    {
        public string PolicyName => "sample.stage";
        public GateCost Cost => GateCost.PureCode;
        public MemoryGateStage Stages => stages;
        public MemoryGateRequirements Requirements => MemoryGateRequirements.None;
        public ValueTask<MemoryGateVerdict> InspectAsync(
            MemoryGateContext context,
            CancellationToken cancellationToken = default)
            => new(MemoryGateVerdict.Allow(PolicyName, "memory.sample.allow"));
    }

    private sealed class ToolAdapter : IMemoryToolContextAdapter
    {
        public MemoryGateContext CreateCallContext(GatedToolCall call, MemoryOperationContract operation, MemoryGateStage stage) => throw new NotSupportedException();
        public MemoryGateContext CreateResultContext(GatedToolResult result, MemoryOperationContract operation) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, object?> ApplySanitizedArguments(GatedToolCall call, MemoryOperationContract operation, string sanitizedContent) => throw new NotSupportedException();
        public object ApplySanitizedResult(GatedToolResult result, MemoryOperationContract operation, string sanitizedContent) => throw new NotSupportedException();
    }

    private sealed class ServerAdapter :
        IMemoryMcpServerContextAdapter<string, string>,
        IMemoryMcpConfigurationFingerprintContributor
    {
        public string ConfigurationFingerprint => new('b', 64);
        public MemoryGateContext CreateRequestContext(string request, MemoryMcpServerOperationContract operation, MemoryGateStage stage) => throw new NotSupportedException();
        public string ApplySanitizedRequest(string request, MemoryMcpServerOperationContract operation, string sanitizedContent) => throw new NotSupportedException();
        public MemoryGateContext CreateResultContext(string request, string result, MemoryMcpServerOperationContract operation) => throw new NotSupportedException();
        public string ApplySanitizedResult(string result, MemoryMcpServerOperationContract operation, string sanitizedContent) => throw new NotSupportedException();
    }

    private sealed class ContextAdapter : IMemoryContextProviderAdapter
    {
        public MemoryGateContext CreateRecallInstructionsContext(AIContextProvider.InvokingContext context, string instructions) => throw new NotSupportedException();
        public MemoryGateContext CreateRecallMessageContext(AIContextProvider.InvokingContext context, ChatMessage message, int ordinal) => throw new NotSupportedException();
        public MemoryGateContext CreateWriteMessageContext(AIContextProvider.InvokedContext context, ChatMessage message, MemoryContextMessageOrigin origin, int ordinal) => throw new NotSupportedException();
        public ChatMessage ApplySanitizedMessage(ChatMessage message, string sanitizedContent) => throw new NotSupportedException();
    }
}
