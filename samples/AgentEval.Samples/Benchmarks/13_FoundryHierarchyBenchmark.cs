// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
extern alias azureidentity;   // disambiguate DefaultAzureCredential (also in Azure.Core via the Foundry beta)

using Azure.AI.OpenAI;
using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Core.Evals.Rendering;
using AgentEval.Evals;
using AgentEval.MAF.Evaluators;
using AgentEval.Output;

using FoundryEvals = Microsoft.Agents.AI.Foundry.FoundryEvals;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks H12: <b>Foundry evals INSIDE a composite hierarchy</b>. Builds one weighted AgentEval
/// <see cref="CompositeEval"/> whose components are a mix — an AgentEval sub-composite (multi-dimension,
/// itself a tree) plus Foundry evals turned into weighted leaves via <c>IAgentEvaluator.AsEvalLeaf()</c>.
/// The rendered report is a single benchmark hierarchy where <i>some of the leaves are Foundry evals</i>.
/// </summary>
/// <remarks>
/// Requires Azure OpenAI (the judge + the SUT agent). The Foundry leaves are added only when
/// <c>FOUNDRY_PROJECT_ENDPOINT</c> is set. NOTE: a composite evaluates once per input, so each Foundry leaf
/// makes one cloud call per scenario — fine for a benchmark; for large runs prefer the batched
/// "Foundry alongside local" path (Benchmarks H11 / the MafEvalFoundryAlongsideLocal sample).
/// </remarks>
public static class FoundryHierarchyBenchmarkSample
{
    public static async Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks H12: Foundry evals inside a composite hierarchy",
            "One weighted benchmark tree: AgentEval sub-composite ⊕ Foundry evals as weighted leaves.");

        if (!AIConfig.IsConfigured)
        {
            BenchmarkSampleHelpers.PrintMissingCredentialsBox("BENCHMARKS H12 — FOUNDRY HIERARCHY");
            return;
        }

        var azure = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        IChatClient judgeChat = azure.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();
        var judge = new ChatClientEvaluator(judgeChat);

        var foundryEndpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
        var foundryModel = Environment.GetEnvironmentVariable("FOUNDRY_MODEL") ?? AIConfig.ModelDeployment;
        bool foundryOn = !string.IsNullOrWhiteSpace(foundryEndpoint);

        AIProjectClient? projectClient = foundryOn
            ? new AIProjectClient(new Uri(foundryEndpoint!), new azureidentity::Azure.Identity.DefaultAzureCredential())
            : null;

        // ── Build the benchmark hierarchy: an AgentEval sub-composite + (optional) Foundry leaves ──────
        // ToolCallAccuracy is itself a 5-dimension composite — so this benchmark is genuinely multi-level.
        var components = new List<EvalComponent>
        {
            new(AgenticBenchmark.ToolCallAccuracy(judge, foundryModel), foundryOn ? 0.5 : 1.0),
        };

        if (projectClient is not null)
        {
            // One FoundryEvals per evaluator → each becomes its own weighted leaf in the tree.
            components.Add(new(
                new FoundryEvals(projectClient, foundryModel, splitter: null, pollIntervalSeconds: 5, timeoutSeconds: 120, FoundryEvals.Relevance)
                    .AsEvalLeaf("foundry.relevance", "Foundry Relevance", judgeModel: foundryModel),
                0.25));
            components.Add(new(
                new FoundryEvals(projectClient, foundryModel, splitter: null, pollIntervalSeconds: 5, timeoutSeconds: 120, FoundryEvals.TaskAdherence)
                    .AsEvalLeaf("foundry.task_adherence", "Foundry Task Adherence", judgeModel: foundryModel),
                0.25));
        }

        var benchmark = new CompositeEval(
            key: "hybrid.trip_planner",
            name: "Trip-Planner Hybrid Benchmark",
            category: "hybrid",
            version: "1.0.0",
            components: components,
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.75);

        Console.WriteLine($"   Components: {string.Join(", ", components.Select(c => $"{c.Eval.Name} (w={c.Weight})"))}");
        Console.WriteLine(foundryOn ? "" : "   (set FOUNDRY_PROJECT_ENDPOINT to weave Foundry evals into the hierarchy)");

        // ── Run the SUT agent once, then score its answer with the whole benchmark tree ───────────────
        AIAgent agent = judgeChat.AsAIAgent(name: "TravelAdvisor", instructions: "You are a helpful travel advisor.");
        const string query = "Plan a 3-day trip to Kyoto for a family with two kids.";
        var runResponse = await agent.RunAsync(query);

        EvalResult result = await benchmark.EvaluateAsync(new EvalInput(query, runResponse.Text));

        Console.WriteLine($"   Benchmark score: {result.Score.Value:P0} ({result.Score.Label}) across "
                          + $"{result.Details.SubResults?.Count ?? 0} top-level component(s).");

        var subject = new SubjectIdentity(SubjectKind.Agent, agent.Name ?? "agent", ModelId: foundryModel, Framework: "MAF");
        var html = await new HtmlEvalResultRenderer().RenderAsync(result, new EvalResultRenderOptions(
            Subject: subject, Title: "Trip-Planner Hybrid Benchmark — AgentEval ⊕ Foundry leaves"));

        var dir = BenchmarkSampleHelpers.EnsureRunDirectory("foundry-hierarchy");
        var htmlPath = System.IO.Path.Combine(dir, "report.html");
        await System.IO.File.WriteAllBytesAsync(htmlPath, html);

        Console.WriteLine();
        Console.WriteLine($"   Report: {htmlPath}");
        Console.WriteLine();
        Console.WriteLine("   KEY TAKEAWAY:");
        Console.WriteLine("   - Foundry evals are WEIGHTED LEAVES inside one AgentEval CompositeEval, next to a");
        Console.WriteLine("     multi-dimension AgentEval sub-composite — a single hierarchical benchmark tree.");
        Console.WriteLine("   - Same weighting / thresholding / aggregation applies to Foundry and AgentEval leaves alike.");
        Console.WriteLine("   - For a large item count, prefer the BATCHED path (H12) so Foundry runs once, not per item.");
    }
}
