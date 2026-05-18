// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Benchmarks;
using AgentEval.Output;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks B3: Agentic — invokes a real Azure OpenAI–backed agent with a
/// representative query, captures the live response, and grades it with the
/// agentic preset selected by <see cref="BenchmarkSampleHelpers.ResolvePreset"/>.
/// Renders the resulting composite tree to JSON + HTML + PDF using the v0.10.1
/// generic renderers.
///
/// Preset mapping:
/// <list type="bullet">
///   <item><see cref="SamplePreset.Smoke"/> → <see cref="AgenticBenchmark.ToolCallAccuracy"/></item>
///   <item><see cref="SamplePreset.Standard"/> → <see cref="AgenticBenchmark.AgenticExecution"/></item>
///   <item><see cref="SamplePreset.AuditGrade"/> → <see cref="AgenticBenchmark.AgenticExecution"/> (same 6-sub-eval composite; audit teams typically pair this with a stronger judge model)</item>
/// </list>
/// </summary>
/// <remarks>
/// Requires Azure OpenAI credentials. Skips gracefully when missing.
/// For multi-prompt JSONL-driven runs see <c>samples/DataAndInfrastructure/04_BenchmarkSystem.cs</c>.
/// </remarks>
public static class AgenticBenchmarkSample
{
    public static async Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks B3: Agentic",
            "Real agent invocation + real judge. JSON + HTML + PDF outputs.");

        if (!AIConfig.IsConfigured)
        {
            BenchmarkSampleHelpers.PrintMissingCredentialsBox("BENCHMARKS B3 — AGENTIC");
            return;
        }

        var preset = BenchmarkSampleHelpers.ResolvePreset();
        BenchmarkSampleHelpers.PrintPreset(preset);

        var judge = BenchmarkSampleHelpers.CreateAzureJudge();
        var (composite, presetLabel) = preset switch
        {
            SamplePreset.Standard => (
                AgenticBenchmark.AgenticExecution(judge, judgeModel: AIConfig.ModelDeployment),
                "Agentic-Execution (6 sub-evals)"),
            SamplePreset.AuditGrade => (
                AgenticBenchmark.AgenticExecution(judge, judgeModel: AIConfig.ModelDeployment),
                "Agentic-Execution (audit-grade; pair with a stronger judge model)"),
            _ => (
                AgenticBenchmark.ToolCallAccuracy(judge, judgeModel: AIConfig.ModelDeployment),
                "Tool-Call-Accuracy (5 sub-evals)"),
        };

        var agent = BenchmarkSampleHelpers.CreateAzureAgent(
            name: "AgenticSampleAgent",
            systemPrompt: "You are a helpful assistant that uses tools when needed. Keep replies tight.");

        const string query = "What's the weather in Paris right now? If you can't look it up, say so plainly.";
        Console.WriteLine($"Invoking real agent: \"{query}\"");
        var agentResponse = await agent.InvokeAsync(query);
        var responseText = agentResponse.Text ?? string.Empty;
        Console.WriteLine($"Agent replied ({responseText.Length} chars).");
        Console.WriteLine($"Grading with preset: {presetLabel}");

        var input = new EvalInput(Query: query, Response: responseText);
        var result = await composite.EvaluateAsync(input);

        var subject = new SubjectIdentity(
            Kind: SubjectKind.Agent,
            Name: agent.Name,
            ModelId: AIConfig.ModelDeployment,
            Framework: "MAF");

        var paths = await BenchmarkSampleHelpers.WriteReportsViaStoreAsync(
            result, subject,
            benchmarkName: "agentic",
            regulationOrBenchmark: $"AgentEval Agentic — {presetLabel}",
            includePdf: true,
            regulationCodeForEvidence: null,
            presetLabel: preset.ToString().ToLowerInvariant(),
            judgeModel: AIConfig.ModelDeployment);

        BenchmarkSampleHelpers.PrintReportPaths(result, paths);
        BenchmarkSampleHelpers.OfferToOpenReports(paths);

        Console.WriteLine();
        Console.WriteLine("   KEY TAKEAWAYS:");
        Console.WriteLine("   - The agent really ran — the EvalResult grades the live response, not a stub.");
        Console.WriteLine("   - Smoke → Tool-Call-Accuracy preset; Standard / Audit-Grade → Agentic-Execution.");
        Console.WriteLine("   - For multi-prompt JSONL-driven runs see DataAndInfrastructure/04_BenchmarkSystem.cs.");
    }
}
