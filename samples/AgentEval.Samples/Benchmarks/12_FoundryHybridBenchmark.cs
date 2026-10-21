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
using AgentEval.Evals.Agentic.Quality;
using AgentEval.Evals.Agentic.Reasoning;
using AgentEval.Evals.Agentic.System;
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
/// <para>
/// Requires a model provider (the judge + fallback agent; see AIConfig) and an Azure AI Foundry project endpoint for
/// the Foundry branch. Set <c>AZURE_FOUNDRY_ENDPOINT</c> to
/// <c>https://&lt;hub&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</c> (copy from the Foundry portal).
/// The Foundry branch uses Azure AD (<c>DefaultAzureCredential</c>), so run <c>az login</c> first. Without the endpoint
/// the AgentEval branch still runs and the report is written, and the run records NOT MEASURED: the Foundry branch
/// is what this sample demonstrates.
/// </para>
/// <para>
/// The AgentEval branch grades what a reply alone can show. MAF's <c>EvaluateAsync</c> hands each evaluator the
/// conversation and the reply. Through <c>AgentEvalAgentEvaluator</c>, <see cref="AgentEvalCompositeEvaluator"/> is also
/// given the turn's tool calls, the agent's tools (<c>EvalItem.Tools</c>) and the expected tool calls; the agent's
/// instructions reach it only when <c>AgentEvalAgentEvaluator.AgentInstructions</c> is set. Until 2026-10 this branch
/// ran the Agentic Execution preset, whose Tool Call Accuracy (20%) and Task Navigation Efficiency (10%, 0 without
/// expected actions) graded a tool-less agent through a path that then carried no tool data.
/// </para>
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
            SampleOutcome.Skip("no chat provider configured");
            return;
        }

        // ── The AgentEval judge + a composite (local, multi-dimension, itself a tree) ──────────────
        // Fitted to what this path carries, the query and the reply: did the plan resolve the intent, is it a sound
        // plan, does it stay on the question, does it read well.
        IChatClient judgeChat = AIConfig.CreateChatClient(AIConfig.ModelDeployment);
        var chatConfig = new ChatConfiguration(judgeChat);
        var judge = new ChatClientEvaluator(judgeChat);
        var judgeModel = AIConfig.ModelDeployment;

        var composite = new CompositeEval(
            key: "hybrid.trip_answer",
            name: "Trip Answer Quality",
            category: "agentic",
            version: "1.0.0",
            components:
            [
                new(new IntentResolutionEval(judge, judgeModel),       0.35) { OnFailure = ComponentFailureEffect.Fail },
                new(new PlanFormulationQualityEval(judge, judgeModel), 0.35) { OnFailure = ComponentFailureEffect.Fail },
                new(new RelevanceEval(judge, judgeModel),              0.20) { OnFailure = ComponentFailureEffect.Fail },
                new(new CoherenceEval(judge, judgeModel),              0.10) { OnFailure = ComponentFailureEffect.Warn },
            ],
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.70)
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

        // System-under-test agent: Foundry-hosted when available, else the configured chat provider as fallback.
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
                var isIntentional = string.Equals(r.Status, "skipped", StringComparison.OrdinalIgnoreCase);
                Console.ForegroundColor = isIntentional ? ConsoleColor.Yellow : ConsoleColor.Red;
                var verb = isIntentional ? "SKIPPED" : "ERROR";
                var reason = !string.IsNullOrEmpty(r.Error) ? r.Error : "no results returned";
                Console.WriteLine($"   {icon} [{src}]  {verb} — {reason}");
                Console.ResetColor();
                SampleOutcome.Skip($"[{src}] not measured ({verb.ToLowerInvariant()}): {reason}");
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
                        Console.WriteLine($"               • {metricName}: {Describe(metric)}");
                }

                // The AgentEval branch has its tree: record the root's verdict as every report reads it. The Foundry
                // branch has only MAF's pass count.
                if (!isFoundry && composite.CapturedResults.Count > 0)
                    SampleOutcome.RecordVerdict($"[{src}] {composite.CapturedResults[^1].Metric.Name}", composite.CapturedResults[^1].Score);
                else if (r.Passed < r.Total)
                    SampleOutcome.Warn($"[{src}] {r.Passed}/{r.Total} item(s) passed");
            }
        }
        Console.WriteLine();

        if (!foundryOn)
        {
            SampleOutcome.Skip(
                "the Foundry branch did not run: AZURE_FOUNDRY_ENDPOINT is not set (an Azure AI Foundry project and `az login`); " +
                "only the AgentEval branch was measured");
        }

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
        Console.WriteLine("   - Through MAF's EvaluateAsync an evaluator sees the query and the reply: grade what they can show.");
    }

    /// <summary>
    /// A metric as a reader reads it: a number with its rating, a yes/no, or the text a string metric carries (the
    /// chance-floor declaration is one). A metric with no value was not measured, which is not a 0.
    /// </summary>
    private static string Describe(EvaluationMetric? metric)
    {
        var rating = metric?.Interpretation is { } interpretation ? $" ({interpretation.Rating})" : "";
        return metric switch
        {
            NumericMetric { Value: { } value } => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + rating,
            BooleanMetric { Value: { } flag } => (flag ? "yes" : "no") + rating,
            StringMetric { Value: { } text } => text,
            null => "not measured",
            _ => "not measured" + rating,
        };
    }
}
