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
}
