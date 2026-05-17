// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Benchmarks;
using AgentEval.Output;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks B3: Agentic — runs <see cref="AgenticBenchmark.ToolCallAccuracy"/>
/// against a single user prompt and renders the resulting composite tree to
/// JSON + HTML + PDF using the v0.10.1 generic renderers.
///
/// Mirrors the existing <c>DataAndInfrastructure/04_BenchmarkSystem.cs</c>
/// shape, but focuses on the unified rendering story rather than the
/// JSONL-dataset loading story (which is unchanged from v0.10.0).
/// </summary>
/// <remarks>
/// Requires Azure OpenAI credentials. Skips gracefully when missing.
/// </remarks>
public static class AgenticBenchmarkSample
{
    public static async Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks B3: Agentic — Tool-Call-Accuracy preset",
            "Single-prompt run with JSON + HTML + PDF outputs");

        if (!AIConfig.IsConfigured)
        {
            BenchmarkSampleHelpers.PrintMissingCredentialsBox("BENCHMARKS B3 — AGENTIC");
            return;
        }

        var judge = CreateJudge();
        var preset = AgenticBenchmark.ToolCallAccuracy(judge, judgeModel: AIConfig.ModelDeployment);

        const string query = "What's the weather in Paris?";
        const string response = "The current weather in Paris is 18°C, partly cloudy.";

        Console.WriteLine($"Evaluating: \"{query}\"");
        var input = new EvalInput(Query: query, Response: response);
        var result = await preset.EvaluateAsync(input);

        var subject = new SubjectIdentity(
            Kind: SubjectKind.Agent,
            Name: "AgenticSampleAgent",
            ModelId: AIConfig.ModelDeployment,
            Framework: "MAF");

        var paths = await BenchmarkSampleHelpers.WriteReportsAsync(
            result, subject,
            benchmarkName: "agentic",
            regulationOrBenchmark: "AgentEval Agentic — Tool-Call-Accuracy preset",
            includePdf: true);

        BenchmarkSampleHelpers.PrintReportPaths(result, paths);

        Console.WriteLine();
        Console.WriteLine("   KEY TAKEAWAYS:");
        Console.WriteLine("   - AgenticBenchmark.ToolCallAccuracy composes 5 sub-evaluators into a CompositeEval.");
        Console.WriteLine("   - The same EvalResult flows into HtmlEvalResultRenderer (Core) and");
        Console.WriteLine("     PdfEvalResultRenderer (Rendering.Pdf) for human + audit-grade artefacts.");
        Console.WriteLine("   - For multi-prompt JSONL-driven runs see samples/DataAndInfrastructure/04_BenchmarkSystem.cs.");
    }

    private static IEvaluator CreateJudge()
    {
        var azure = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chat = azure.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();
        return new ChatClientEvaluator(chat);
    }
}
