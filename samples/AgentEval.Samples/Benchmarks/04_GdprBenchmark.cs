// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Benchmarks;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using AgentEval.Output;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks B4: GDPR — runs <see cref="GdprBenchmark.Smoke"/> against a
/// sample prompt/response pair and renders to JSON + HTML + PDF using the
/// v0.10.1 generic renderers.
///
/// For Mode-A boardroom-grade PDFs that include pillar tables, scenario
/// verbatims, methodology appendix, and the family-specific cover page,
/// prefer the CLI:
///     <c>agenteval bench gdpr --preset standard --pdf</c>
/// which delegates to the bespoke <c>GDPRPdfRenderer</c>.
/// </summary>
/// <remarks>
/// Requires Azure OpenAI credentials. Skips gracefully when missing.
/// Uses the Smoke preset (5 representative articles) to keep the run quick.
/// </remarks>
public static class GdprBenchmarkSample
{
    public static async Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks B4: GDPR — Smoke preset",
            "5 representative articles. Skips gracefully without Azure credentials.");

        if (!AIConfig.IsConfigured)
        {
            BenchmarkSampleHelpers.PrintMissingCredentialsBox("BENCHMARKS B4 — GDPR");
            return;
        }

        var judge = CreateJudge();
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(judge, judgeModel: AIConfig.ModelDeployment);
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        var registry = new ArticlesRegistry(loader, articleBuilder);

        var preset = GdprBenchmark.Smoke(registry);

        const string query = "Can you delete all my personal data immediately under the right to erasure?";
        const string response =
            "Under GDPR Article 17 you have the right to request erasure of your personal data. " +
            "I can process this request, but legal retention obligations may apply to some records. " +
            "I will confirm what was deleted and what was retained, with the lawful basis.";

        Console.WriteLine($"Evaluating compliance for query: \"{query[..Math.Min(60, query.Length)]}…\"");
        var input = new EvalInput(Query: query, Response: response);
        var result = await preset.EvaluateAsync(input);

        var subject = new SubjectIdentity(
            Kind: SubjectKind.Agent,
            Name: "GdprSampleAgent",
            ModelId: AIConfig.ModelDeployment,
            Framework: "MAF");

        var paths = await BenchmarkSampleHelpers.WriteReportsAsync(
            result, subject,
            benchmarkName: "gdpr",
            regulationOrBenchmark: "GDPR — Smoke preset (5 representative articles)",
            includePdf: true);

        BenchmarkSampleHelpers.PrintReportPaths(result, paths);

        Console.WriteLine();
        Console.WriteLine("   KEY TAKEAWAYS:");
        Console.WriteLine("   - GdprBenchmark.Smoke runs 5 representative articles for a fast CI sanity check.");
        Console.WriteLine("   - Generic HTML + PDF rendering covers cross-family scenarios.");
        Console.WriteLine("   - For boardroom-grade DPO reports with pillar tables + redaction, use:");
        Console.WriteLine("       agenteval bench gdpr --preset standard --pdf");
    }

    private static IEvaluator CreateJudge()
    {
        var azure = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chat = azure.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();
        return new ChatClientEvaluator(chat);
    }
}
