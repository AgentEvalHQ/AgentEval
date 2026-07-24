// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Gatekeeper Phase 1, P0-1 — the tool coverage analyzer.</summary>
public class GatekeeperCoverageAnalyzerTests
{
    private static ChatClientAgent BuildAgent(params AITool[] tools)
        => new(new ScriptedChatClient().AddText("hi"), new ChatClientAgentOptions { Name = "T", ChatOptions = new ChatOptions { Tools = tools } });

    [Fact]
    public void LocalFunction_NoGateRegistered_IsUnprotected()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "lookup_order");
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent(tool));

        Assert.True(report.ToolInventoryAvailable);
        var entry = Assert.Single(report.Tools);
        Assert.Equal(ToolExecutionModel.InterceptedLocalFunction, entry.ExecutionModel);
        Assert.False(entry.IsGateProtected);
        Assert.Equal(0, report.ProtectedCount);
        Assert.Equal(0.0, report.EnforcementCoveragePercent);
    }

    [Fact]
    public void LocalFunction_WithToolGateRegistered_IsProtected()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "lookup_order");
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent(tool), [new ForbiddenToolGate("delete_account")]);

        var entry = Assert.Single(report.Tools);
        Assert.True(entry.IsGateProtected);
        Assert.Equal(100.0, report.EnforcementCoveragePercent);
        Assert.Contains("ForbiddenToolGate", report.RegisteredToolGateNames);
    }

    [Fact]
    public void HighRiskKeyword_InName_IsClassifiedHighRisk()
    {
        var tool = AIFunctionFactory.Create((string id) => "ok", "delete_account");
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent(tool));

        var entry = Assert.Single(report.Tools);
        Assert.Equal(ToolRiskLevel.HighRisk, entry.RiskLevel);
        Assert.True(entry.IsUnprotectedHighRisk);
        Assert.True(report.HasUnprotectedHighRiskTools);
        Assert.Equal(1, report.HighRiskUnprotectedCount);
    }

    [Fact]
    public void StandardKeyword_NoHighRiskMatch_IsClassifiedStandard()
    {
        var tool = AIFunctionFactory.Create((string id) => "ok", "lookup_order");
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent(tool));

        var entry = Assert.Single(report.Tools);
        Assert.Equal(ToolRiskLevel.Standard, entry.RiskLevel);
        Assert.False(report.HasUnprotectedHighRiskTools);
    }

    [Fact]
    public void CredentialAccessKeyword_ReadSecrets_IsClassifiedHighRisk()
    {
        // read_secrets is the canonical example tool name used throughout this framework's own Gatekeeper
        // tests (e.g. the SequenceGate composite test) — a read/data-exposure risk, not a destructive-write or
        // financial one, so it needs its own keyword category rather than the mutation/destructive/financial
        // ones already present.
        var tool = AIFunctionFactory.Create(() => "ok", "read_secrets");
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent(tool));

        var entry = Assert.Single(report.Tools);
        Assert.Equal(ToolRiskLevel.HighRisk, entry.RiskLevel);
        Assert.True(report.HasUnprotectedHighRiskTools);
    }

    [Fact]
    public void HostedTool_IsProviderHostedOpaque_NeverProtected_EvenWithGatesRegistered()
    {
        var hosted = new HostedWebSearchTool();
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent(hosted), [new ForbiddenToolGate("x")]);

        var entry = Assert.Single(report.Tools);
        Assert.Equal(ToolExecutionModel.ProviderHostedOpaque, entry.ExecutionModel);
        Assert.False(entry.IsGateProtected);
        Assert.Contains("provider-executed", entry.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedToolSet_ComputesPartialCoverage()
    {
        var local1 = AIFunctionFactory.Create((string x) => x, "lookup_order");
        var local2 = AIFunctionFactory.Create((string x) => x, "process_refund");
        var hosted = new HostedWebSearchTool();
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent(local1, local2, hosted), [new ForbiddenToolGate("x")]);

        Assert.Equal(3, report.Tools.Count);
        Assert.Equal(2, report.InterceptedLocalFunctionCount);
        Assert.Equal(1, report.ProviderHostedOpaqueCount);
        Assert.Equal(2, report.ProtectedCount);   // both local functions protected; hosted tool never is
        Assert.True(report.EnforcementCoveragePercent is > 66.0 and < 67.0);
    }

    [Fact]
    public void AnalyzeOrThrow_UnprotectedHighRiskTool_Throws()
    {
        var tool = AIFunctionFactory.Create((string id) => "ok", "delete_account");
        var ex = Assert.Throws<UnprotectedHighRiskToolException>(() => GatekeeperCoverageAnalyzer.AnalyzeOrThrow(BuildAgent(tool)));
        Assert.Contains("delete_account", ex.Message, StringComparison.Ordinal);
        Assert.Same(ex.Report, ex.Report);   // Report is populated and accessible
        Assert.True(ex.Report.HasUnprotectedHighRiskTools);
    }

    [Fact]
    public void AnalyzeOrThrow_ProtectedHighRiskTool_DoesNotThrow()
    {
        var tool = AIFunctionFactory.Create((string id) => "ok", "delete_account");
        var report = GatekeeperCoverageAnalyzer.AnalyzeOrThrow(BuildAgent(tool), [new ForbiddenToolGate("delete_account")]);
        Assert.False(report.HasUnprotectedHighRiskTools);
    }

    [Fact]
    public void AnalyzeOrThrow_UnprotectedHostedCodeInterpreter_Throws_ByDefault()
    {
        // Fable 5 fix: a hosted code interpreter is uninterceptable AND arbitrary-capability, so it must be
        // treated as unprotected-high-risk by default — the old behavior classified it Standard and admitted it.
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent(new HostedCodeInterpreterTool()));
        Assert.True(report.HasUnprotectedHighRiskTools);
        Assert.Throws<UnprotectedHighRiskToolException>(
            () => GatekeeperCoverageAnalyzer.AnalyzeOrThrow(BuildAgent(new HostedCodeInterpreterTool())));
    }

    [Fact]
    public void HostedCodeInterpreter_OptOut_NotFlaggedHighRisk()
    {
        var options = new AnalyzeOptions { TreatArbitraryCapabilityOpaqueToolsAsHighRisk = false };
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent(new HostedCodeInterpreterTool()), options: options);
        Assert.False(report.HasUnprotectedHighRiskTools);   // explicit opt-out restores the lenient behavior
    }

    [Fact]
    public void HostedWebSearch_NotAutoHighRisk_NarrowingPreserved()
    {
        // Narrowing: only arbitrary-capability opaque tools auto-escalate. A hosted web search (far narrower)
        // stays on the keyword heuristic and does NOT trip AnalyzeOrThrow.
        var report = GatekeeperCoverageAnalyzer.AnalyzeOrThrow(BuildAgent(new HostedWebSearchTool()));
        Assert.False(report.HasUnprotectedHighRiskTools);
    }

    [Fact]
    public void AcknowledgeProviderHostedTools_ExemptsNamedOpaqueTool()
    {
        // P1-3: the sanctioned escape hatch — an explicitly acknowledged code-interpreter is admitted.
        var tool = new HostedCodeInterpreterTool();
        var options = new AnalyzeOptions { AcknowledgeProviderHostedTools = new HashSet<string> { tool.Name } };
        var report = GatekeeperCoverageAnalyzer.AnalyzeOrThrow(BuildAgent(tool), options: options);
        Assert.False(report.HasUnprotectedHighRiskTools);
    }

    [Fact]
    public void UnacknowledgedOpaqueTool_StillThrows()
    {
        var options = new AnalyzeOptions { AcknowledgeProviderHostedTools = new HashSet<string> { "a-different-tool" } };
        Assert.Throws<UnprotectedHighRiskToolException>(
            () => GatekeeperCoverageAnalyzer.AnalyzeOrThrow(BuildAgent(new HostedCodeInterpreterTool()), options: options));
    }

    [Fact]
    public void DynamicToolProvider_EmptyStaticTools_FailsClosed_NotVacuous100()
    {
        // P1-12 (§1): an agent whose tools come only from an AIContextProvider (declared here) must NOT be
        // certified as vacuously 100%-covered — AnalyzeOrThrow refuses rather than green-light an unverified agent.
        var options = new AnalyzeOptions { HasDynamicToolProvider = true };
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent(), options: options);   // no static tools
        Assert.False(report.ToolInventoryAvailable);
        Assert.Throws<ToolInventoryUnavailableException>(() => GatekeeperCoverageAnalyzer.AnalyzeOrThrow(BuildAgent(), options: options));
    }

    [Fact]
    public void DynamicToolProvider_WithStaticTools_StillAnalyzedNormally()
    {
        // The flag only fails closed when the static list is EMPTY; a declared provider alongside static tools does
        // not disable ordinary analysis of those static tools.
        var options = new AnalyzeOptions { HasDynamicToolProvider = true };
        var tool = AIFunctionFactory.Create((string x) => x, "lookup");
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent(tool), [new ForbiddenToolGate("x")], options);
        Assert.True(report.ToolInventoryAvailable);
        Assert.Single(report.Tools);
    }

    [Fact]
    public void CustomRiskHeuristic_OverridesDefault()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "lookup_order");   // Standard by default
        var options = new AnalyzeOptions { IsHighRisk = _ => true };            // force everything HighRisk
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent(tool), options: options);

        Assert.Equal(ToolRiskLevel.HighRisk, Assert.Single(report.Tools).RiskLevel);
    }

    [Fact]
    public void EmptyToolList_Is100PercentCoverage_Vacuously()
    {
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent());
        Assert.Empty(report.Tools);
        Assert.Equal(100.0, report.EnforcementCoveragePercent);
        Assert.False(report.HasUnprotectedHighRiskTools);
    }

    [Fact]
    public void ToolListOverload_MatchesAgentOverload()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "delete_account");
        var report = GatekeeperCoverageAnalyzer.Analyze([tool], [new ForbiddenToolGate("delete_account")]);

        Assert.True(report.ToolInventoryAvailable);
        Assert.True(Assert.Single(report.Tools).IsGateProtected);
    }

    [Fact]
    public void Render_ProducesNonEmptyHumanReadableReport()
    {
        var tool = AIFunctionFactory.Create((string id) => "ok", "delete_account");
        var report = GatekeeperCoverageAnalyzer.Analyze(BuildAgent(tool));
        var rendered = report.Render();

        Assert.Contains("delete_account", rendered, StringComparison.Ordinal);
        Assert.Contains("enforcement coverage", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolGatesWrappedThroughBuilderMiddleware_StillResolveViaGetService()
    {
        // Regression guard for the GetService-forwarding assumption the analyzer relies on: DelegatingAIAgent
        // (what AIAgentBuilder.Use(...) produces) must forward GetService(typeof(ChatOptions)) to the inner
        // ChatClientAgent so the analyzer works on a gate-wrapped agent, not just the raw base agent.
        var tool = AIFunctionFactory.Create((string id) => "ok", "delete_account");
        var baseAgent = BuildAgent(tool);
        var wrapped = baseAgent.AsBuilder().UseAgentEvalToolGate([new ForbiddenToolGate("delete_account")], ToolGatePolicy.Terminate).Build();

        var report = GatekeeperCoverageAnalyzer.Analyze(wrapped, [new ForbiddenToolGate("delete_account")]);

        Assert.True(report.ToolInventoryAvailable);
        Assert.True(Assert.Single(report.Tools).IsGateProtected);
    }

    // ── AnalyzeOrThrow must fail closed when the inventory itself is unreadable, not just when it's read and bad ──

    [Fact]
    public void AnalyzeOrThrow_ToolInventoryUnavailable_ThrowsToolInventoryUnavailableException()
    {
        // A custom AIAgent that doesn't forward GetService(typeof(ChatOptions)) — Analyze(agent) reports
        // ToolInventoryAvailable=false and an empty Tools list, which would otherwise make
        // HasUnprotectedHighRiskTools vacuously false (a silent "all clear" for an agent that was never
        // actually checked). AnalyzeOrThrow must refuse to construct rather than assume that's safe.
        var agent = new OpaqueAgent();

        var ex = Assert.Throws<ToolInventoryUnavailableException>(() => GatekeeperCoverageAnalyzer.AnalyzeOrThrow(agent));

        Assert.False(ex.Report.ToolInventoryAvailable);
        Assert.Contains("ToolInventoryAvailable=false", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ToolInventoryUnavailable_DoesNotThrow_OnlyAnalyzeOrThrowDoes()
    {
        // The non-throwing Analyze(agent) overload must still just report the fact (callers who want the raw
        // report, e.g. to render a diagnostic, should not be forced through an exception).
        var agent = new OpaqueAgent();

        var report = GatekeeperCoverageAnalyzer.Analyze(agent);

        Assert.False(report.ToolInventoryAvailable);
        Assert.Empty(report.Tools);
    }

    // A minimal AIAgent that does NOT override GetService — the base implementation returns null for any
    // requested service type, including ChatOptions, exactly like a real custom (non-ChatClientAgent) agent
    // that doesn't expose it.
    private sealed class OpaqueAgent : AIAgent
    {
        public override string? Name => "Opaque";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => new(new OpaqueSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Not exercised — this test only calls GetService via the coverage analyzer.");

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Not exercised — this test only calls GetService via the coverage analyzer.");

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            AgentSession session, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => new(System.Text.Json.JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement serializedState, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => new(new OpaqueSession());
    }

    private sealed class OpaqueSession : AgentSession
    {
    }
}
