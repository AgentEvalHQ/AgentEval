// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using AgentEval.MAF.Gatekeeper.Memory;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class MemoryGatekeeperIntegrationTests
{
    [Fact]
    public void UseGatekeeper_ProtectMemory_ComposesAndPublishesAuthoritativeReport()
    {
        var tool = Function("memory_write");
        var captured = new GatekeeperOptions();

        Agent(tool).AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            captured = options;
            options.KnownTools = [tool];
            options.ProtectMemory(Protection(Policy(MemoryCoverageLevel.Boundary), WriteContract()));
        });

        var report = Assert.IsType<MemoryProtectionReport>(captured.MemoryProtectionReport);
        Assert.Equal(MemoryProtectionReport.SchemaVersion, MemoryProtectionReport.SchemaVersion);
        Assert.Equal("phase7-test", report.PolicyId);
        Assert.Equal(MemoryCoverageLevel.FullLifecycle, Assert.Single(report.ToolCoverage.Entries).Coverage);
        Assert.False(report.HasCoverageBelowMinimum);
        Assert.NotEmpty(report.PolicyFingerprint);
        Assert.NotEmpty(report.ConfigurationFingerprint);
    }

    [Fact]
    public void UseGatekeeper_ProfileMismatch_RefusesBeforeComposition()
    {
        var tool = Function("memory_write");
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Agent(tool).AsBuilder().UseGatekeeper(GatekeeperEnforcement.Observe, options =>
            {
                options.Trace = new AgentTrace();
                options.KnownTools = [tool];
                options.ProtectMemory(Protection(Policy(MemoryCoverageLevel.Boundary), WriteContract()));
            }));

        Assert.Contains("incompatible", exception.Message, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void UseGatekeeper_ObserveMemoryWithSensitiveSinks_RemainsObserveOnly()
    {
        var tool = Function("memory_recall");
        var pipeline = new MemoryGatePipeline(
            [],
            policy: new MemorySecurityPolicy(
                "phase7-observe",
                "1",
                MemorySecurityProfile.Observe,
                MemoryGateAction.Quarantine,
                MemoryCoverageLevel.ObserveOnly));
        var protection = new MemoryProtectionOptions(
            pipeline,
            new MemoryToolOperationRegistry([RecallContract()]),
            new FakeAdapter())
        {
            SensitiveSinkTools = ["send_email"],
        };
        var captured = new GatekeeperOptions();

        Agent(tool).AsBuilder().UseGatekeeper(GatekeeperEnforcement.Observe, options =>
        {
            captured = options;
            options.Trace = new AgentTrace();
            options.BannerWriter = null;
            options.KnownTools = [tool];
            options.ProtectMemory(protection);
        });

        Assert.Equal(MemoryCoverageLevel.ObserveOnly,
            Assert.Single(captured.MemoryProtectionReport!.ToolCoverage.Entries).Coverage);
    }

    [Fact]
    public void UseGatekeeper_MemoryContractsWithoutKnownTools_Refuses()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Agent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
                options.ProtectMemory(Protection(Policy(MemoryCoverageLevel.Boundary), WriteContract()))));

        Assert.Contains("KnownTools", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseGatekeeper_UnclassifiedMemoryLikeTool_RefusesEnforcement()
    {
        var suspicious = Function("remember_everything");

        var exception = Assert.Throws<MemoryProtectionCoverageException>(() =>
            Agent(suspicious).AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.KnownTools = [suspicious];
                options.ProtectMemory(Protection(Policy(MemoryCoverageLevel.Boundary)));
            }));

        Assert.Contains("remember_everything", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseGatekeeper_ManualMemoryAdapterPlusComposite_Refuses()
    {
        var tool = Function("memory_write");
        var protection = Protection(Policy(MemoryCoverageLevel.Boundary), WriteContract());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Agent(tool).AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.KnownTools = [tool];
                options.Add(new MemoryToolCallGate(
                    protection.Pipeline,
                    protection.ToolRegistry,
                    protection.ToolContextAdapter));
                options.ProtectMemory(protection);
            }));

        Assert.Contains("single composite", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UseGatekeeper_CallerCollectionsMutatedAfterRegistration_ReportRemainsFrozen()
    {
        var tool = Function("memory_recall");
        var sinks = new List<string> { "send_email" };
        var protection = new MemoryProtectionOptions(
            Policy(MemoryCoverageLevel.Boundary),
            new MemoryToolOperationRegistry([RecallContract()]),
            new FakeAdapter())
        {
            SensitiveSinkTools = sinks,
        };
        var captured = new GatekeeperOptions();

        Agent(tool).AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            captured = options;
            options.KnownTools = [tool];
            options.ProtectMemory(protection);
        });
        var fingerprint = captured.MemoryProtectionReport!.ConfigurationFingerprint;
        var withoutInfluence = new GatekeeperOptions();
        Agent(tool).AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            withoutInfluence = options;
            options.KnownTools = [tool];
            options.ProtectMemory(Protection(Policy(MemoryCoverageLevel.Boundary), RecallContract()));
        });

        sinks.Clear();

        Assert.Equal(fingerprint, captured.MemoryProtectionReport.ConfigurationFingerprint);
        Assert.NotEqual(
            captured.MemoryProtectionReport.AdapterFingerprint,
            withoutInfluence.MemoryProtectionReport!.AdapterFingerprint);
        Assert.Equal(MemoryCoverageLevel.FullLifecycle, Assert.Single(captured.MemoryProtectionReport.ToolCoverage.Entries).Coverage);
    }

    [Fact]
    public void UseGatekeeper_HostedMcpWithoutHooks_RefusesFullLifecyclePolicy()
    {
        var hosted = new MemoryHostedMcpOperationContract(
            "hosted-memory",
            "1",
            new string('a', 64),
            Contract("hosted_write", MemoryOperationKind.Write, MemorySurface.HostedMcp),
            MemoryHostedMcpApprovalMode.Never,
            MemoryHostedMcpCallbackCapabilities.None);
        var protection = new MemoryProtectionOptions(
            Policy(MemoryCoverageLevel.FullLifecycle),
            new MemoryToolOperationRegistry([]),
            new FakeAdapter())
        {
            HostedMcpContracts = [hosted],
        };

        var exception = Assert.Throws<MemoryProtectionCoverageException>(() =>
            Agent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.KnownTools = [];
                options.ProtectMemory(protection);
            }));

        Assert.Contains("hosted-memory/hosted_write", exception.Message, StringComparison.Ordinal);
    }


    [Fact]
    public void UseGatekeeper_LocalMcpWithMatchingOwnedServer_ProvesFullLifecycle()
    {
        var tool = Function("memory_write");
        var schema = MemoryMcpSchema.ComputeFingerprint(tool.JsonSchema.GetRawText());
        var clientRegistry = new MemoryMcpClientOperationRegistry([
            new MemoryMcpClientOperationContract(
                "owned-memory", "1", MemoryMcpTransport.InProcess, schema,
                Contract("memory_write", MemoryOperationKind.Write, MemorySurface.LocalMcp)),
        ]);
        var serverRegistry = new MemoryMcpServerOperationRegistry(
            "owned-memory",
            "1",
            MemoryMcpTransport.InProcess,
            [new MemoryMcpServerOperationContract(
                schema,
                Contract("memory_write", MemoryOperationKind.Write, MemorySurface.McpServer))]);
        var pipeline = new MemoryGatePipeline(
            [new StageGate(MemoryGateStage.BeforeWrite)],
            policy: new MemorySecurityPolicy(
                "phase7-mcp",
                "1",
                MemorySecurityProfile.Enforce,
                MemoryGateAction.Reject,
                MemoryCoverageLevel.FullLifecycle));
        var server = new MemoryMcpServerGate<string, string>(
            pipeline, serverRegistry, new FakeServerAdapter());
        var protection = new MemoryProtectionOptions(
            pipeline, clientRegistry.OperationRegistry, new FakeAdapter())
        {
            LocalMcpRegistry = clientRegistry,
            LocalMcpBindings = [new MemoryMcpClientToolBinding(
                tool, "owned-memory", "1", MemoryMcpTransport.InProcess, schema)],
            OwnedMcpServer = server.CoverageEvidence,
        };
        var captured = new GatekeeperOptions();

        Agent(tool).AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            captured = options;
            options.KnownTools = [tool];
            options.ProtectMemory(protection);
        });

        Assert.Equal(MemoryCoverageLevel.Boundary,
            Assert.Single(captured.MemoryProtectionReport!.ToolCoverage.Entries).Coverage);
        Assert.Equal(MemoryCoverageLevel.FullLifecycle,
            Assert.Single(captured.MemoryProtectionReport.McpCoverage.Entries).Coverage);
        Assert.False(captured.MemoryProtectionReport.HasCoverageBelowMinimum);
    }

    [Fact]
    public void UseGatekeeper_ProviderNativeCompleteHooks_ProveFullLifecycle()
    {
        var pipeline = Policy(MemoryCoverageLevel.FullLifecycle);
        var native = new MemoryProviderNativeGate(
            new MemoryProviderNativeDescriptor(
                "native-provider",
                "1",
                MemoryProviderNativeCapabilities.CandidateWrites |
                MemoryProviderNativeCapabilities.RecalledItems),
            pipeline);
        var protection = new MemoryProtectionOptions(
            pipeline, new MemoryToolOperationRegistry([]), new FakeAdapter())
        {
            ProviderNativeGates = [native],
        };
        var captured = new GatekeeperOptions();

        Agent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            captured = options;
            options.KnownTools = [];
            options.ProtectMemory(protection);
        });

        Assert.Equal(MemoryCoverageLevel.FullLifecycle,
            Assert.Single(captured.MemoryProtectionReport!.ProviderCoverage).Coverage);
    }

    private static ChatClientAgent Agent(params AITool[] tools)
        => new(
            new ScriptedChatClient(),
            new ChatClientAgentOptions
            {
                Name = "phase7",
                ChatOptions = new ChatOptions { Tools = tools },
            });

    private static AIFunction Function(string name)
        => AIFunctionFactory.Create((string content) => content, name);

    private static MemoryProtectionOptions Protection(
        MemoryGatePipeline pipeline,
        params MemoryOperationContract[] contracts)
        => new(pipeline, new MemoryToolOperationRegistry(contracts), new FakeAdapter());

    private static MemoryGatePipeline Policy(MemoryCoverageLevel minimum)
        => new(
            [],
            policy: new MemorySecurityPolicy(
                "phase7-test",
                "1",
                MemorySecurityProfile.Enforce,
                MemoryGateAction.Reject,
                minimum));

    private static MemoryOperationContract WriteContract()
        => Contract("memory_write", MemoryOperationKind.Write, MemorySurface.Tool);

    private static MemoryOperationContract RecallContract()
        => new(
            "memory_recall",
            MemoryOperationKind.Recall,
            MemorySurface.Tool,
            ["content"],
            ["tenantId"],
            MemoryCategory.Fact,
            isSideEffecting: false,
            mayReturnSensitiveContent: true);

    private static MemoryOperationContract Contract(
        string name,
        MemoryOperationKind kind,
        MemorySurface surface)
        => new(
            name,
            kind,
            surface,
            ["content"],
            ["tenantId"],
            MemoryCategory.Fact,
            isSideEffecting: true,
            mayReturnSensitiveContent: false);

    private sealed class FakeAdapter : IMemoryToolContextAdapter
    {
        public MemoryGateContext CreateCallContext(
            GatedToolCall call,
            MemoryOperationContract operation,
            MemoryGateStage stage)
            => throw new NotSupportedException();

        public MemoryGateContext CreateResultContext(
            GatedToolResult result,
            MemoryOperationContract operation)
            => throw new NotSupportedException();

        public IReadOnlyDictionary<string, object?> ApplySanitizedArguments(
            GatedToolCall call,
            MemoryOperationContract operation,
            string sanitizedContent)
            => throw new NotSupportedException();

        public object ApplySanitizedResult(
            GatedToolResult result,
            MemoryOperationContract operation,
            string sanitizedContent)
            => throw new NotSupportedException();
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

    private sealed class FakeServerAdapter :
        IMemoryMcpServerContextAdapter<string, string>,
        IMemoryMcpConfigurationFingerprintContributor
    {
        public string ConfigurationFingerprint => new('b', 64);
        public MemoryGateContext CreateRequestContext(
            string request, MemoryMcpServerOperationContract operation, MemoryGateStage stage)
            => throw new NotSupportedException();
        public string ApplySanitizedRequest(
            string request, MemoryMcpServerOperationContract operation, string sanitizedContent)
            => throw new NotSupportedException();
        public MemoryGateContext CreateResultContext(
            string request, string result, MemoryMcpServerOperationContract operation)
            => throw new NotSupportedException();
        public string ApplySanitizedResult(
            string result, MemoryMcpServerOperationContract operation, string sanitizedContent)
            => throw new NotSupportedException();
    }

}
