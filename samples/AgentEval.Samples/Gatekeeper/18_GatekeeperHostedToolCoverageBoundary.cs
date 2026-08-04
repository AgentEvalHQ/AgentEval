// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#pragma warning disable MEAI001 // Hosted tools are inspected as coverage subjects; none is executed.

using AgentEval.MAF.Gatekeeper;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Gatekeeper — honest provider-hosted-tool coverage, fully offline.
///
/// A local function passes through MAF function-invocation middleware. A provider-hosted code interpreter does
/// not. Registering more local gates cannot change that execution model, and acknowledging the hosted tool is an
/// explicit risk acceptance rather than a claim that Gatekeeper intercepts it.
/// </summary>
public static class GatekeeperHostedToolCoverageBoundary
{
    public static Task RunAsync()
    {
        Console.WriteLine("\n=== Gatekeeper — Hosted Tool Coverage Boundary (offline) ===\n");

        var localTool = AIFunctionFactory.Create((string orderId) => $"fake order {orderId}", "lookup_order");
        var hostedTool = new HostedCodeInterpreterTool();
        IToolGate[] gates = [new ForbiddenToolGate("delete_account")];
        AITool[] tools = [localTool, hostedTool];

        var report = GatekeeperCoverageAnalyzer.Analyze(tools, gates);
        var local = report.Tools.Single(entry => entry.ToolName == localTool.Name);
        var hosted = report.Tools.Single(entry => entry.ToolName == hostedTool.Name);

        Require(local.ExecutionModel == ToolExecutionModel.InterceptedLocalFunction, "the local function must be interceptable");
        Require(local.IsGateProtected, "the registered local gate must be structurally reachable for the local function");
        Require(hosted.ExecutionModel == ToolExecutionModel.ProviderHostedOpaque, "the hosted interpreter must be classified opaque");
        Require(!hosted.IsGateProtected, "a local tool gate must never claim to protect provider-hosted execution");
        Require(hosted.IsUnprotectedHighRisk, "an unacknowledged hosted code interpreter must fail high-risk coverage");
        Require(report.EnforcementCoveragePercent == 50.0, "one of two tools should be structurally protected");

        UnprotectedHighRiskToolException? refused = null;
        try
        {
            GatekeeperCoverageAnalyzer.AnalyzeOrThrow(tools, gates);
        }
        catch (UnprotectedHighRiskToolException ex)
        {
            refused = ex;
        }

        Require(refused is not null, "promotion must refuse the unacknowledged high-risk hosted tool");
        var refusedReport = refused?.Report
            ?? throw new InvalidOperationException("Hosted-tool-coverage sample invariant failed: promotion must refuse the unacknowledged high-risk hosted tool.");
        Require(refusedReport.ProviderHostedOpaqueCount == 1, "the refusal must carry the honest coverage report");

        Console.WriteLine("① Unacknowledged hosted code interpreter — promotion refused");
        Console.WriteLine(Indent(report.Render(), "   "));

        var acknowledgedOptions = new AnalyzeOptions
        {
            AcknowledgeProviderHostedTools = new HashSet<string>(StringComparer.Ordinal) { hostedTool.Name },
        };
        var acknowledged = GatekeeperCoverageAnalyzer.AnalyzeOrThrow(tools, gates, acknowledgedOptions);
        var acknowledgedHosted = acknowledged.Tools.Single(entry => entry.ToolName == hostedTool.Name);

        Require(!acknowledged.HasUnprotectedHighRiskTools, "explicit acknowledgment should admit promotion");
        Require(!acknowledgedHosted.IsGateProtected, "acknowledgment must not fabricate local interception");
        Require(acknowledged.EnforcementCoveragePercent == 50.0, "acknowledgment must not inflate structural coverage");

        Console.WriteLine("② Explicit acknowledgment — admitted, but still visibly opaque");
        Console.WriteLine(Indent(acknowledged.Render(), "   "));
        Console.WriteLine("   ✅ acknowledgment records risk acceptance; it does not turn a provider-hosted tool into a gated local function");
        Console.WriteLine("   ✅ no provider, hosted tool, local tool, or model was invoked");
        Console.WriteLine("\n=== Hosted Tool Coverage Boundary Complete ===");

        return Task.CompletedTask;
    }

    private static string Indent(string value, string prefix)
        => prefix + value.Replace(Environment.NewLine, Environment.NewLine + prefix, StringComparison.Ordinal);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Hosted-tool-coverage sample invariant failed: " + message + ".");
        }
    }
}
