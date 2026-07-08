// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
extern alias azureidentity;   // disambiguate DefaultAzureCredential (also in Azure.Core via the Foundry beta)

using Azure.AI.OpenAI;
using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Core.Evals.Rendering;
using AgentEval.Evals;
using AgentEval.MAF.Evaluators;
using AgentEval.Output;

using FoundryEvals = Microsoft.Agents.AI.Foundry.FoundryEvals;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks H11: <b>Foundry ⊕ AgentEval hybrid</b> — puts an Azure AI Foundry eval <i>inside</i> a
/// composite eval (<see cref="CompositeAgentEvaluator"/>), scores one MAF agent run with both the
/// AgentEval Composite Eval and the Foundry eval, and renders both in one source-tagged HTML report.
/// </summary>
/// <remarks>
/// Requires Azure OpenAI credentials (the judge + fallback agent) and optionally an Azure AI Foundry
/// project endpoint for the Foundry branch. Set <c>AZURE_FOUNDRY_ENDPOINT</c> to
/// <c>https://&lt;hub&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</c> (copy from the Foundry portal).
/// Without it the sample runs AgentEval-local-only. The Foundry branch uses Azure AD
/// (<c>DefaultAzureCredential</c>), so run <c>az login</c> first.
/// </remarks>
public static class FoundryHybridBenchmarkSample
{
    public static async Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks H11: Foundry ⊕ AgentEval (hybrid)",
            "A Foundry eval INSIDE a composite eval — one agent run, one source-tagged report.");

        if (!AIConfig.IsConfigured)
        {
            BenchmarkSampleHelpers.PrintMissingCredentialsBox("BENCHMARKS H11 — FOUNDRY HYBRID");
            return;
        }

        // ── The AgentEval judge + a composite (local, deterministic, multi-dimension) ──────────────
        var azure = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        IChatClient judgeChat = azure.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();
        var chatConfig = new ChatConfiguration(judgeChat);

        var composite = AgenticBenchmark
            .AgenticExecution(new ChatClientEvaluator(judgeChat), judgeModel: AIConfig.ModelDeployment)
            .AsMeaiEvaluator();
        IAgentEvaluator local = composite.AsAgentEvaluator(chatConfig);

        // ── The Foundry eval — only when AZURE_FOUNDRY_ENDPOINT is set ────────────────────────
        // Endpoint comes from AIConfig.FoundryEndpoint (AZURE_FOUNDRY_ENDPOINT).
        // Format: https://<hub>.services.ai.azure.com/api/projects/<project>
        // AIProjectClient uses DefaultAzureCredential (Azure AD) — run `az login` first.
        var foundryEndpoint = AIConfig.FoundryEndpoint;
        var foundryModel = AIConfig.ModelDeployment;
        bool foundryOn = foundryEndpoint is not null;

        AIProjectClient? projectClient = foundryOn
            ? new AIProjectClient(foundryEndpoint!, new azureidentity::Azure.Identity.DefaultAzureCredential())
            : null;

        // System-under-test agent: Foundry-hosted when available, else Azure OpenAI fallback.
        AIAgent agent = projectClient is not null
            ? projectClient.AsAIAgent(model: foundryModel, instructions: "You are a helpful travel advisor.", name: "TravelAdvisor")
            : judgeChat.AsAIAgent(name: "TravelAdvisor", instructions: "You are a helpful travel advisor.");

        // ── Put the Foundry eval INTO the composite, alongside the AgentEval-local one ─────────────
        var inners = new List<(string Source, IAgentEvaluator Evaluator, TimeSpan? Timeout)>
        {
            ("agenteval-local", local, null),
        };
        if (projectClient is not null)
        {
            inners.Add(("foundry",
                new TracingAgentEvaluator(
                    new FoundryEvals(projectClient, foundryModel, splitter: null, pollIntervalSeconds: 5, timeoutSeconds: 120,
                                     FoundryEvals.TaskAdherence, FoundryEvals.Relevance)),
                TimeSpan.FromSeconds(150)));
        }

        var hybrid = new CompositeAgentEvaluator(inners, name: "Foundry ⊕ AgentEval");

        Console.WriteLine($"   Sources: {string.Join(", ", inners.Select(i => i.Source))}"
                          + (foundryOn ? $"  (Foundry: {foundryEndpoint})" : "   (set AZURE_FOUNDRY_ENDPOINT to add the Foundry branch)"));

        string[] queries = { "Plan a 3-day trip to Kyoto for a family with two kids." };

        // One call: the agent runs once; the composite fans out to both evaluators concurrently.
        AgentEvaluationResults results = await agent.EvaluateAsync(queries, hybrid);

        // ── Per-source console summary ────────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"   Merged verdict: {results.Passed}/{results.Total} passed across {hybrid.CapturedPerSource.Count} source(s).");
        Console.WriteLine();
        foreach (var (src, r) in hybrid.CapturedPerSource)
        {
            var isFoundry = src.Contains("foundry", StringComparison.OrdinalIgnoreCase);
            var icon = isFoundry ? "☁ " : "⚙ ";
            if (!string.IsNullOrEmpty(r.Error) || r.Total == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"   {icon} [{src}]  SKIPPED — {r.Error ?? "no results returned"}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = r.Passed == r.Total ? ConsoleColor.Green : ConsoleColor.Red;
                Console.WriteLine($"   {icon} [{src}]  {r.Passed}/{r.Total} passed" +
                    (r.Status is not null ? $"  status={r.Status}" : ""));
                Console.ResetColor();
                if (r.ReportUrl is not null)
                    Console.WriteLine($"               🔗 {r.ReportUrl}");
                foreach (var meai in r.Items)
                {
                    foreach (var (metricName, metric) in meai.Metrics)
                    {
                        var score = metric is Microsoft.Extensions.AI.Evaluation.NumericMetric nm
                            ? nm.Value?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "N/A"
                            : metric?.ToString() ?? "N/A";
                        Console.WriteLine($"               • {metricName}: {score}");
                    }
                }
            }
        }
        Console.WriteLine();

        // One source-tagged report (a branch per source; the local branch keeps its full hierarchy).
        EvalResult tree = UnifiedEvalReport.Build(hybrid.CapturedPerSource, composite);
        var subject = new SubjectIdentity(SubjectKind.Agent, agent.Name ?? "agent", ModelId: foundryModel, Framework: "MAF");
        var html = await new HtmlEvalResultRenderer().RenderAsync(tree, new EvalResultRenderOptions(
            Subject: subject, Title: "Foundry ⊕ AgentEval — hybrid eval"));

        var dir = BenchmarkSampleHelpers.EnsureRunDirectory("foundry-hybrid");
        var htmlPath = System.IO.Path.Combine(dir, "report.html");
        await System.IO.File.WriteAllBytesAsync(htmlPath, html);

        Console.WriteLine();
        Console.WriteLine($"   Report: {htmlPath}");
        Console.WriteLine();
        Console.WriteLine("   KEY TAKEAWAY:");
        Console.WriteLine("   - A Foundry eval sits INSIDE a CompositeAgentEvaluator, next to the AgentEval composite.");
        Console.WriteLine("   - One agent run, both evaluators (concurrent + isolated), one source-tagged report.");
        Console.WriteLine("   - A Foundry failure/timeout becomes a visible 'skipped' branch — the local results survive.");
    }
}
