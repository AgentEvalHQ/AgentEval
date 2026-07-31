// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using AgentEval.MAF.Gatekeeper.Memory;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class MemoryMcpIntegrationTests
{
    private static readonly string Schema = MemoryMcpSchema.ComputeFingerprint(
        """{"type":"object","properties":{"content":{"type":"string"}}}""");

    [Fact]
    public void Schema_PropertyOrderChanged_HasStableFingerprint()
    {
        var first = MemoryMcpSchema.ComputeFingerprint(
            """{"type":"object","properties":{"b":{"type":"string"},"a":{"type":"integer"}}}""");
        var second = MemoryMcpSchema.ComputeFingerprint(
            """{"properties":{"a":{"type":"integer"},"b":{"type":"string"}},"type":"object"}""");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Schema_SemanticsChanged_ChangesFingerprint()
    {
        var first = MemoryMcpSchema.ComputeFingerprint("""{"type":"string"}""");
        var second = MemoryMcpSchema.ComputeFingerprint("""{"type":"integer"}""");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Schema_DuplicateProperty_RejectsAmbiguity()
        => Assert.Throws<ArgumentException>(
            () => MemoryMcpSchema.ComputeFingerprint("""{"type":"object","type":"string"}"""));

    [Fact]
    public void ClientBinding_FromFunction_UsesActualFunctionSchema()
    {
        var function = Function("memory_write");

        var binding = MemoryMcpClientToolBinding.FromFunction(
            function,
            "server-a",
            "1",
            MemoryMcpTransport.Stdio);

        Assert.Equal(
            MemoryMcpSchema.ComputeFingerprint(function.JsonSchema.GetRawText()),
            binding.SchemaFingerprint);
    }

    [Fact]
    public void ClientRegistry_MutableInputChanged_RemainsFrozen()
    {
        var source = new List<MemoryMcpClientOperationContract> { ClientContract(WriteOperation()) };
        var registry = new MemoryMcpClientOperationRegistry(source);
        var fingerprint = registry.ConfigurationFingerprint;

        source.Clear();

        Assert.True(registry.TryGet("memory_write", out _));
        Assert.Equal(fingerprint, registry.ConfigurationFingerprint);
    }

    [Fact]
    public void LocalCoverage_MatchingClientBoundary_IsBoundary()
    {
        var registry = ClientRegistry(WriteOperation());
        var pipeline = Pipeline();
        var adapter = new FakeToolAdapter();
        var report = MemoryMcpCoverageAnalyzer.AnalyzeLocal(
            [Binding(Function("memory_write"))],
            registry,
            new MemoryToolCallGate(pipeline, registry.OperationRegistry, adapter));

        Assert.Equal(MemoryCoverageLevel.Boundary, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void LocalCoverage_ChangedSchema_IsUnsupported()
    {
        var registry = ClientRegistry(WriteOperation());
        var changed = MemoryMcpSchema.ComputeFingerprint("""{"type":"object","required":["content"]}""");

        var report = MemoryMcpCoverageAnalyzer.AnalyzeLocal(
            [Binding(Function("memory_write"), schema: changed)],
            registry);

        Assert.Equal(MemoryCoverageLevel.Unsupported, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void LocalCoverage_MissingRuntimeTool_IsUnsupported()
    {
        var report = MemoryMcpCoverageAnalyzer.AnalyzeLocal([], ClientRegistry(WriteOperation()));

        Assert.Equal(MemoryCoverageLevel.Unsupported, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void LocalCoverage_UnknownBoundTool_IsUnsupported()
    {
        var report = MemoryMcpCoverageAnalyzer.AnalyzeLocal(
            [Binding(Function("unknown_memory"))],
            ClientRegistry(WriteOperation()));

        Assert.Equal(2, report.Entries.Count);
        Assert.All(report.Entries, entry => Assert.Equal(MemoryCoverageLevel.Unsupported, entry.Coverage));
    }

    [Fact]
    public void LocalCoverage_MatchingOwnedServer_UpgradesWriteToFullLifecycle()
    {
        var client = ClientRegistry(WriteOperation());
        var pipeline = Pipeline();
        var adapter = new FakeToolAdapter();
        var server = ServerGate(
            ServerOperation("memory_write", MemoryOperationKind.Write),
            Pipeline(new FixedGate(MemoryGateStage.BeforeWrite)));

        var report = MemoryMcpCoverageAnalyzer.AnalyzeLocal(
            [Binding(Function("memory_write"))],
            client,
            new MemoryToolCallGate(pipeline, client.OperationRegistry, adapter),
            ownedServer: server.CoverageEvidence);

        Assert.Equal(MemoryCoverageLevel.FullLifecycle, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void LocalCoverage_SensitiveReadWithoutInfluence_RemainsBoundary()
    {
        var client = ClientRegistry(RecallOperation());
        var pipeline = Pipeline();
        var adapter = new FakeToolAdapter();
        var server = ServerGate(
            ServerOperation("memory_recall", MemoryOperationKind.Recall, true),
            Pipeline(new FixedGate(
                MemoryGateStage.BeforeRead | MemoryGateStage.AfterRead)));

        var report = MemoryMcpCoverageAnalyzer.AnalyzeLocal(
            [Binding(Function("memory_recall"))],
            client,
            new MemoryToolCallGate(pipeline, client.OperationRegistry, adapter),
            new MemoryToolResultGate(pipeline, client.OperationRegistry, adapter),
            ownedServer: server.CoverageEvidence);

        Assert.Equal(MemoryCoverageLevel.Boundary, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void LocalCoverage_SensitiveReadWithInfluenceAndServer_IsFullLifecycle()
    {
        var client = ClientRegistry(RecallOperation());
        var pipeline = Pipeline();
        var adapter = new FakeToolAdapter();
        var server = ServerGate(
            ServerOperation("memory_recall", MemoryOperationKind.Recall, true),
            Pipeline(new FixedGate(
                MemoryGateStage.BeforeRead | MemoryGateStage.AfterRead)));

        var report = MemoryMcpCoverageAnalyzer.AnalyzeLocal(
            [Binding(Function("memory_recall"))],
            client,
            new MemoryToolCallGate(pipeline, client.OperationRegistry, adapter),
            new MemoryToolResultGate(pipeline, client.OperationRegistry, adapter),
            new MemoryInfluenceGate(client.OperationRegistry, ["send_email"]),
            server.CoverageEvidence);

        Assert.Equal(MemoryCoverageLevel.FullLifecycle, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void LocalCoverage_EmptyOwnedServerPipeline_DoesNotUpgradeCoverage()
    {
        var client = ClientRegistry(WriteOperation());
        var clientPipeline = Pipeline();
        var server = ServerGate(ServerOperation("memory_write", MemoryOperationKind.Write), Pipeline());

        var report = MemoryMcpCoverageAnalyzer.AnalyzeLocal(
            [Binding(Function("memory_write"))],
            client,
            new MemoryToolCallGate(clientPipeline, client.OperationRegistry, new FakeToolAdapter()),
            ownedServer: server.CoverageEvidence);

        Assert.Equal(MemoryCoverageLevel.Boundary, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void LocalCoverage_UnfingerprintedOwnedServerAdapter_DoesNotUpgradeCoverage()
    {
        var client = ClientRegistry(WriteOperation());
        var clientPipeline = Pipeline();
        var server = ServerGate(
            ServerOperation("memory_write", MemoryOperationKind.Write),
            Pipeline(new FixedGate(MemoryGateStage.BeforeWrite)),
            new UnfingerprintedServerAdapter());

        var report = MemoryMcpCoverageAnalyzer.AnalyzeLocal(
            [Binding(Function("memory_write"))],
            client,
            new MemoryToolCallGate(clientPipeline, client.OperationRegistry, new FakeToolAdapter()),
            ownedServer: server.CoverageEvidence);

        Assert.Equal(MemoryCoverageLevel.Boundary, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void ServerGate_AdapterConfiguration_ChangesFingerprint()
    {
        var operation = ServerOperation("memory_write", MemoryOperationKind.Write);
        var pipeline = Pipeline(new FixedGate(MemoryGateStage.BeforeWrite));
        var first = ServerGate(operation, pipeline);
        var second = ServerGate(
            operation,
            pipeline,
            new FakeServerAdapter
            {
                ConfigurationFingerprint = MemoryMcpSchema.ComputeFingerprint(
                    """{"type":"object","required":["trustedScope"]}"""),
            });

        Assert.NotEqual(first.ConfigurationFingerprint, second.ConfigurationFingerprint);
        Assert.NotEqual(
            first.CoverageEvidence.ConfigurationFingerprint,
            second.CoverageEvidence.ConfigurationFingerprint);
    }

    [Fact]
    public void LocalCoverage_MismatchedServerSemantics_DoesNotUpgradeCoverage()
    {
        var client = ClientRegistry(WriteOperation());
        var clientPipeline = Pipeline();
        var serverOperation = Operation(
            "memory_write",
            MemoryOperationKind.Write,
            MemorySurface.McpServer,
            sensitive: true);
        var server = ServerGate(
            serverOperation,
            Pipeline(new FixedGate(MemoryGateStage.BeforeWrite)));

        var report = MemoryMcpCoverageAnalyzer.AnalyzeLocal(
            [Binding(Function("memory_write"))],
            client,
            new MemoryToolCallGate(clientPipeline, client.OperationRegistry, new FakeToolAdapter()),
            ownedServer: server.CoverageEvidence);

        Assert.Equal(MemoryCoverageLevel.Boundary, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void LocalCoverage_UnregisteredBinding_DoesNotInventOperationKind()
    {
        var report = MemoryMcpCoverageAnalyzer.AnalyzeLocal(
            [Binding(Function("unknown_memory"))],
            new MemoryMcpClientOperationRegistry([]));

        Assert.Null(Assert.Single(report.Entries).OperationKind);
    }

    [Fact]
    public void Registry_ExcessiveOperationInventory_IsRejectedBeforeEnumerationContinues()
    {
        var contract = ClientContract(WriteOperation());

        Assert.Throws<ArgumentException>(
            () => new MemoryMcpClientOperationRegistry(
                Enumerable.Repeat(contract, 257)));
    }

    [Fact]
    public void Schema_ExcessiveSource_IsRejected()
        => Assert.Throws<ArgumentException>(
            () => MemoryMcpSchema.ComputeFingerprint(
                "{" + new string('a', MemoryMcpSchema.MaximumSchemaCharacters) + "}"));

    [Fact]
    public async Task ServerGate_RequestRejected_BackendIsNeverCalled()
    {
        var gate = ServerGate(
            ServerOperation("memory_write", MemoryOperationKind.Write),
            Pipeline(new FixedGate(MemoryGateStage.BeforeWrite, MemoryGateAction.Reject)));
        var called = false;

        var exception = await Assert.ThrowsAsync<MemoryMcpSafeException>(
            async () => await gate.ExecuteAsync(
                "memory_write",
                "correlation-1",
                "poison",
                (request, cancellationToken) =>
                {
                    called = true;
                    return ValueTask.FromResult("stored");
                }));

        Assert.False(called);
        Assert.Equal("memory.mcp.request_denied", exception.ReasonCode);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task ServerGate_RequestSanitized_BackendReceivesSanitizedContent()
    {
        var gate = ServerGate(
            ServerOperation("memory_write", MemoryOperationKind.Write),
            Pipeline(new FixedGate(
                MemoryGateStage.BeforeWrite,
                MemoryGateAction.Sanitize,
                sanitizedContent: "clean")));
        string? observed = null;

        var result = await gate.ExecuteAsync(
            "memory_write",
            "correlation-2",
            "poison",
            (request, cancellationToken) =>
            {
                observed = request;
                return ValueTask.FromResult("stored");
            });

        Assert.Equal("clean", observed);
        Assert.Equal("stored", result);
    }

    [Fact]
    public async Task ServerGate_ReadResultRejected_DoesNotReturnProviderContent()
    {
        var gate = ServerGate(
            ServerOperation("memory_recall", MemoryOperationKind.Recall, true),
            Pipeline(new FixedGate(MemoryGateStage.AfterRead, MemoryGateAction.Exclude)));

        var exception = await Assert.ThrowsAsync<MemoryMcpSafeException>(
            async () => await gate.ExecuteAsync(
                "memory_recall",
                "correlation-3",
                "query",
                (request, cancellationToken) => ValueTask.FromResult("provider-secret")));

        Assert.Equal("memory.mcp.result_denied", exception.ReasonCode);
        Assert.DoesNotContain("provider-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerGate_ReadResultSanitized_ReturnsOnlySanitizedContent()
    {
        var gate = ServerGate(
            ServerOperation("memory_recall", MemoryOperationKind.Recall, true),
            Pipeline(new FixedGate(
                MemoryGateStage.AfterRead,
                MemoryGateAction.Sanitize,
                sanitizedContent: "[safe]")));

        var result = await gate.ExecuteAsync(
            "memory_recall",
            "correlation-4",
            "query",
            (request, cancellationToken) => ValueTask.FromResult("provider-secret"));

        Assert.Equal("[safe]", result);
    }

    [Fact]
    public async Task ServerGate_BackendFailure_MapsToContentFreeError()
    {
        var gate = ServerGate(
            ServerOperation("memory_write", MemoryOperationKind.Write),
            Pipeline());

        var exception = await Assert.ThrowsAsync<MemoryMcpSafeException>(
            async () => await gate.ExecuteAsync(
                "memory_write",
                "correlation-5",
                "candidate",
                (request, cancellationToken) =>
                    ValueTask.FromException<string>(
                        new InvalidOperationException("Authorization: Bearer provider-secret"))));

        Assert.Equal("memory.mcp.backend_failure", exception.ReasonCode);
        Assert.DoesNotContain("provider-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task ServerGate_UnknownOperation_FailsBeforeBackend()
    {
        var gate = ServerGate(
            ServerOperation("memory_write", MemoryOperationKind.Write),
            Pipeline());
        var called = false;

        var exception = await Assert.ThrowsAsync<MemoryMcpSafeException>(
            async () => await gate.ExecuteAsync(
                "other_operation",
                "correlation-6",
                "candidate",
                (request, cancellationToken) =>
                {
                    called = true;
                    return ValueTask.FromResult("stored");
                }));

        Assert.False(called);
        Assert.Equal("memory.mcp.contract_mismatch", exception.ReasonCode);
    }

    [Fact]
    public async Task ServerGate_WrongProviderFromAdapter_FailsBeforeBackend()
    {
        var gate = ServerGate(
            ServerOperation("memory_write", MemoryOperationKind.Write),
            Pipeline(),
            new FakeServerAdapter { ProviderId = "other-server" });
        var called = false;

        var exception = await Assert.ThrowsAsync<MemoryMcpSafeException>(
            async () => await gate.ExecuteAsync(
                "memory_write",
                "correlation-7",
                "candidate",
                (request, cancellationToken) =>
                {
                    called = true;
                    return ValueTask.FromResult("stored");
                }));

        Assert.False(called);
        Assert.Equal("memory.mcp.adapter_failure", exception.ReasonCode);
    }

    [Fact]
    public async Task ServerGate_CallerCancellation_Propagates()
    {
        var gate = ServerGate(
            ServerOperation("memory_write", MemoryOperationKind.Write),
            Pipeline());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await gate.ExecuteAsync(
                "memory_write",
                "correlation-8",
                "candidate",
                (request, cancellationToken) => ValueTask.FromResult("stored"),
                cancellation.Token));
    }

    [Fact]
    public async Task ServerGate_Quarantine_StoresOnceAndSkipsBackend()
    {
        var quarantine = new FakeQuarantineStore();
        var pipeline = Pipeline(
            new FixedGate(MemoryGateStage.BeforeWrite, MemoryGateAction.Quarantine),
            capabilities: new MemoryGateCapabilities(quarantineStore: quarantine));
        var gate = ServerGate(ServerOperation("memory_write", MemoryOperationKind.Write), pipeline);
        var called = false;

        await Assert.ThrowsAsync<MemoryMcpSafeException>(
            async () => await gate.ExecuteAsync(
                "memory_write",
                "correlation-9",
                "candidate",
                (request, cancellationToken) =>
                {
                    called = true;
                    return ValueTask.FromResult("stored");
                }));

        Assert.False(called);
        Assert.Single(quarantine.Requests);
    }

    [Fact]
    public async Task ServerGate_ObserveMode_DoesNotApplyHypotheticalSanitization()
    {
        var gate = ServerGate(
            ServerOperation("memory_write", MemoryOperationKind.Write),
            Pipeline(
                new FixedGate(
                    MemoryGateStage.BeforeWrite,
                    MemoryGateAction.Sanitize,
                    sanitizedContent: "hypothetical"),
                MemorySecurityProfile.Observe));
        string? observed = null;

        await gate.ExecuteAsync(
            "memory_write",
            "correlation-10",
            "original",
            (request, cancellationToken) =>
            {
                observed = request;
                return ValueTask.FromResult("stored");
            });

        Assert.Equal("original", observed);
    }

    [Fact]
    public async Task SafeException_SerializationAndMessage_ContainNoProviderDetails()
    {
        var gate = ServerGate(
            ServerOperation("memory_write", MemoryOperationKind.Write),
            Pipeline());

        var exception = await Assert.ThrowsAsync<MemoryMcpSafeException>(
            async () => await gate.ExecuteAsync(
                "memory_write",
                "correlation-safe",
                "candidate",
                (request, cancellationToken) =>
                    ValueTask.FromException<string>(new Exception("password=provider-secret"))));
        var json = JsonSerializer.Serialize(new
        {
            exception.ReasonCode,
            exception.CorrelationId,
        });

        Assert.DoesNotContain("provider-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate", json, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedCoverage_NoCallbacksOrApproval_IsUnsupported()
    {
        var report = MemoryMcpCoverageAnalyzer.AnalyzeHosted(
            [Hosted(WriteOperation(MemorySurface.HostedMcp))],
            MemorySecurityProfile.Enforce);

        Assert.Equal(MemoryCoverageLevel.Unsupported, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void HostedCoverage_MandatoryApproval_IsActionOnly()
    {
        var report = MemoryMcpCoverageAnalyzer.AnalyzeHosted(
            [Hosted(
                WriteOperation(MemorySurface.HostedMcp),
                MemoryHostedMcpApprovalMode.Always)],
            MemorySecurityProfile.Enforce);

        Assert.Equal(MemoryCoverageLevel.ActionOnly, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void HostedCoverage_CompleteCallbacks_WriteIsFullLifecycle()
    {
        var report = MemoryMcpCoverageAnalyzer.AnalyzeHosted(
            [Hosted(
                WriteOperation(MemorySurface.HostedMcp),
                capabilities: CompleteCallbacks)],
            MemorySecurityProfile.Enforce);

        Assert.Equal(MemoryCoverageLevel.FullLifecycle, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void HostedCoverage_SensitiveReadWithoutDerivedActions_IsUnsupported()
    {
        var report = MemoryMcpCoverageAnalyzer.AnalyzeHosted(
            [Hosted(
                RecallOperation(MemorySurface.HostedMcp),
                capabilities: CompleteCallbacks)],
            MemorySecurityProfile.Enforce);

        Assert.Equal(MemoryCoverageLevel.Unsupported, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void HostedCoverage_SensitiveReadWithCompleteCallbacks_IsFullLifecycle()
    {
        var report = MemoryMcpCoverageAnalyzer.AnalyzeHosted(
            [Hosted(
                RecallOperation(MemorySurface.HostedMcp),
                capabilities: CompleteCallbacks |
                    MemoryHostedMcpCallbackCapabilities.DerivedActions)],
            MemorySecurityProfile.Enforce);

        Assert.Equal(MemoryCoverageLevel.FullLifecycle, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void HostedCoverage_ObserveProfile_NeverClaimsEnforcement()
    {
        var report = MemoryMcpCoverageAnalyzer.AnalyzeHosted(
            [Hosted(
                WriteOperation(MemorySurface.HostedMcp),
                MemoryHostedMcpApprovalMode.Always,
                CompleteCallbacks)],
            MemorySecurityProfile.Observe);

        Assert.Equal(MemoryCoverageLevel.ObserveOnly, Assert.Single(report.Entries).Coverage);
    }

    [Fact]
    public void HostedCoverage_ActionOnly_FailsBoundaryPreflight()
    {
        var report = MemoryMcpCoverageAnalyzer.AnalyzeHosted(
            [Hosted(
                WriteOperation(MemorySurface.HostedMcp),
                MemoryHostedMcpApprovalMode.Always)],
            MemorySecurityProfile.Enforce);

        var exception = Assert.Throws<MemoryMcpCoverageException>(
            () => MemoryMcpCoverageAnalyzer.RequireCoverage(report, MemoryCoverageLevel.Boundary));

        Assert.Equal(MemoryCoverageLevel.Boundary, exception.MinimumCoverage);
        Assert.Contains("server-a/memory_write", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedContract_UnknownCallbackFlag_IsRejected()
        => Assert.Throws<ArgumentException>(
            () => Hosted(
                WriteOperation(MemorySurface.HostedMcp),
                capabilities: (MemoryHostedMcpCallbackCapabilities)64));

    [Fact]
    public void LocalContract_ProviderHostedTransport_IsRejected()
        => Assert.Throws<ArgumentException>(
            () => new MemoryMcpClientOperationContract(
                "server-a",
                "1",
                MemoryMcpTransport.ProviderHosted,
                Schema,
                WriteOperation()));

    [Fact]
    public void CoverageReport_SerializationContainsNoRawMemoryContent()
    {
        var report = MemoryMcpCoverageAnalyzer.AnalyzeHosted(
            [Hosted(WriteOperation(MemorySurface.HostedMcp))],
            MemorySecurityProfile.Enforce);

        var json = JsonSerializer.Serialize(report);

        Assert.DoesNotContain("candidate", json, StringComparison.Ordinal);
        Assert.Contains(Schema, json, StringComparison.Ordinal);
    }

    private const MemoryHostedMcpCallbackCapabilities CompleteCallbacks =
        MemoryHostedMcpCallbackCapabilities.CallArguments |
        MemoryHostedMcpCallbackCapabilities.ResultContent |
        MemoryHostedMcpCallbackCapabilities.TrustedScope |
        MemoryHostedMcpCallbackCapabilities.PreExecutionApproval;

    private static MemoryMcpClientOperationRegistry ClientRegistry(MemoryOperationContract operation)
        => new([ClientContract(operation)]);

    private static MemoryMcpClientOperationContract ClientContract(MemoryOperationContract operation)
        => new("server-a", "1", MemoryMcpTransport.Stdio, Schema, operation);

    private static MemoryMcpClientToolBinding Binding(AITool tool, string? schema = null)
        => new(tool, "server-a", "1", MemoryMcpTransport.Stdio, schema ?? Schema);

    private static MemoryMcpServerGate<string, string> ServerGate(
        MemoryOperationContract operation,
        MemoryGatePipeline pipeline,
        IMemoryMcpServerContextAdapter<string, string>? adapter = null)
        => new(
            pipeline,
            new MemoryMcpServerOperationRegistry(
                "server-a",
                "1",
                MemoryMcpTransport.Stdio,
                [new MemoryMcpServerOperationContract(Schema, operation)]),
            adapter ?? new FakeServerAdapter());

    private static MemoryHostedMcpOperationContract Hosted(
        MemoryOperationContract operation,
        MemoryHostedMcpApprovalMode approval = MemoryHostedMcpApprovalMode.Never,
        MemoryHostedMcpCallbackCapabilities capabilities = MemoryHostedMcpCallbackCapabilities.None)
        => new("server-a", "1", Schema, operation, approval, capabilities);

    private static MemoryOperationContract WriteOperation(
        MemorySurface surface = MemorySurface.LocalMcp)
        => Operation("memory_write", MemoryOperationKind.Write, surface);

    private static MemoryOperationContract RecallOperation(
        MemorySurface surface = MemorySurface.LocalMcp)
        => Operation("memory_recall", MemoryOperationKind.Recall, surface, true);

    private static MemoryOperationContract ServerOperation(
        string name,
        MemoryOperationKind kind,
        bool sensitive = false)
        => Operation(name, kind, MemorySurface.McpServer, sensitive);

    private static MemoryOperationContract Operation(
        string name,
        MemoryOperationKind kind,
        MemorySurface surface,
        bool sensitive = false)
        => new(
            name,
            kind,
            surface,
            ["content"],
            ["tenantId"],
            MemoryCategory.Fact,
            isSideEffecting: kind is not (MemoryOperationKind.Search or MemoryOperationKind.Recall),
            mayReturnSensitiveContent: sensitive);

    private static MemoryGatePipeline Pipeline(
        IMemoryGate? gate = null,
        MemorySecurityProfile profile = MemorySecurityProfile.Enforce,
        MemoryGateCapabilities? capabilities = null)
        => new(
            gate is null ? [] : [gate],
            capabilities,
            new MemorySecurityPolicy(
                "mcp-test",
                "1",
                profile,
                MemoryGateAction.Reject,
                MemoryCoverageLevel.Boundary),
            new FrozenTimeProvider());

    private static AIFunction Function(string name)
        => AIFunctionFactory.Create((string content) => content, name);

    private static MemoryGateContext Context(
        MemoryOperationContract operation,
        MemoryGateStage stage,
        string providerId,
        string? content)
        => new(
            $"operation-{Guid.NewGuid():N}",
            stage,
            operation,
            providerId,
            new MemorySecurityScope(tenantId: "tenant-a", userId: "user-a"),
            new MemoryProvenance(MemorySourceKind.Mcp, "server-a", MemoryTrustLevel.Medium),
            content,
            budget: new MemoryBudgetSnapshot(),
            recordMetadata: stage is MemoryGateStage.AfterRead
                ? new MemoryRecordMetadata(
                    "memory-1",
                    new MemorySecurityScope(tenantId: "tenant-a", userId: "user-a"))
                : null);

    private sealed class FakeServerAdapter :
        IMemoryMcpServerContextAdapter<string, string>,
        IMemoryMcpConfigurationFingerprintContributor
    {
        public string ProviderId { get; init; } = "server-a";
        public string ConfigurationFingerprint { get; init; } = Schema;

        public MemoryGateContext CreateRequestContext(
            string request,
            MemoryMcpServerOperationContract operation,
            MemoryGateStage stage)
            => Context(operation.Operation, stage, ProviderId, request);

        public string ApplySanitizedRequest(
            string request,
            MemoryMcpServerOperationContract operation,
            string sanitizedContent)
            => sanitizedContent;

        public MemoryGateContext CreateResultContext(
            string request,
            string result,
            MemoryMcpServerOperationContract operation)
            => Context(operation.Operation, MemoryGateStage.AfterRead, ProviderId, result);

        public string ApplySanitizedResult(
            string result,
            MemoryMcpServerOperationContract operation,
            string sanitizedContent)
            => sanitizedContent;
    }

    private sealed class UnfingerprintedServerAdapter :
        IMemoryMcpServerContextAdapter<string, string>
    {
        private readonly FakeServerAdapter _inner = new();

        public MemoryGateContext CreateRequestContext(
            string request,
            MemoryMcpServerOperationContract operation,
            MemoryGateStage stage)
            => _inner.CreateRequestContext(request, operation, stage);

        public string ApplySanitizedRequest(
            string request,
            MemoryMcpServerOperationContract operation,
            string sanitizedContent)
            => _inner.ApplySanitizedRequest(request, operation, sanitizedContent);

        public MemoryGateContext CreateResultContext(
            string request,
            string result,
            MemoryMcpServerOperationContract operation)
            => _inner.CreateResultContext(request, result, operation);

        public string ApplySanitizedResult(
            string result,
            MemoryMcpServerOperationContract operation,
            string sanitizedContent)
            => _inner.ApplySanitizedResult(result, operation, sanitizedContent);
    }

    private sealed class FakeToolAdapter : IMemoryToolContextAdapter
    {
        public MemoryGateContext CreateCallContext(
            GatedToolCall call,
            MemoryOperationContract operation,
            MemoryGateStage stage)
            => Context(operation, stage, "server-a", content: null);

        public MemoryGateContext CreateResultContext(
            GatedToolResult result,
            MemoryOperationContract operation)
            => Context(operation, MemoryGateStage.AfterRead, "server-a", result.ResultText);

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

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
