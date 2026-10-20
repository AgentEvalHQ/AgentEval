// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
extern alias azureidentity;   // disambiguate DefaultAzureCredential (also in Azure.Core via the Foundry beta)

using System.ComponentModel;
using System.Text.Json;
using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Core.Evals.Rendering;
using AgentEval.Evals;
using AgentEval.MAF.Evaluators;
using AgentEval.Models;
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
/// <para>
/// Requires a model provider (see AIConfig) and an Azure AI Foundry project endpoint for the Foundry leaves. Set
/// <c>AZURE_FOUNDRY_ENDPOINT</c> to <c>https://&lt;hub&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</c> (copy
/// from the Foundry portal). The Foundry leaves use Azure AD (<c>DefaultAzureCredential</c>), so run <c>az login</c>
/// first. Without the endpoint only the AgentEval sub-composite runs (weight=1.0), and the run records NOT MEASURED:
/// the Foundry leaves are what this sample demonstrates. NOTE: a composite evaluates once per input, so each Foundry
/// leaf makes one cloud call per scenario — fine for a benchmark; for large runs prefer the batched path (the
/// MafEvalFoundryAlongsideLocal sample).
/// </para>
/// <para>
/// The travel advisor has two local tools with fixed data (a fixture, not a live service), and the calls it makes go
/// into the <see cref="EvalInput"/>. Until 2026-10 the agent had no tools and the input carried none, so the Tool Call
/// Accuracy composite graded a run with nothing to grade and printed "0% (warn)": a wiring fault reported as a score.
/// </para>
/// </remarks>
public static class FoundryHierarchyBenchmarkSample
{
    private const string WeatherTool = "get_weather_forecast";
    private const string ActivitiesTool = "find_family_activities";

    public static async Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks H12: Foundry evals inside a composite hierarchy",
            "One weighted benchmark tree: AgentEval sub-composite ⊕ Foundry evals as weighted leaves.");

        if (!AIConfig.IsConfigured)
        {
            BenchmarkSampleHelpers.PrintMissingCredentialsBox("BENCHMARKS H12 — FOUNDRY HIERARCHY");
            SampleOutcome.Skip("no chat provider configured");
            return;
        }

        IChatClient judgeChat = AIConfig.CreateChatClient(AIConfig.ModelDeployment);
        var judge = new ChatClientEvaluator(judgeChat);

        // Foundry leaves come from AZURE_FOUNDRY_ENDPOINT (see AIConfig.FoundryEndpoint).
        // Format: https://<hub>.services.ai.azure.com/api/projects/<project>
        // AIProjectClient uses DefaultAzureCredential (Azure AD) — run `az login` first.
        var foundryEndpoint = AIConfig.FoundryEndpoint;
        var foundryModel = AIConfig.ModelDeployment;
        bool foundryOn = foundryEndpoint is not null;

        AIProjectClient? projectClient = foundryOn
            ? new AIProjectClient(foundryEndpoint!, new azureidentity::Azure.Identity.DefaultAzureCredential())
            : null;

        // ── Build the benchmark hierarchy: an AgentEval sub-composite + (optional) Foundry leaves ──────
        // ToolCallAccuracy wraps a 5-dimension composite (selection, input accuracy, output utilization, success,
        // efficiency), so this benchmark is genuinely multi-level. It grades the tool calls in the EvalInput, which is
        // why the agent below has tools and its calls are passed in.
        // One FoundryEvals per evaluator → each becomes its own weighted leaf in the tree.
        var components = new List<EvalComponent>
        {
            new(AgenticBenchmark.ToolCallAccuracy(judge, foundryModel), foundryOn ? 0.5 : 1.0),
        };

        if (projectClient is not null)
        {
            components.Add(new(
                new TracingAgentEvaluator(
                    new FoundryEvals(projectClient, foundryModel, splitter: null, pollIntervalSeconds: 5, timeoutSeconds: 120, FoundryEvals.Relevance))
                    .AsEvalLeaf("foundry.relevance", "Foundry Relevance", judgeModel: foundryModel),
                0.25));
            components.Add(new(
                new TracingAgentEvaluator(
                    new FoundryEvals(projectClient, foundryModel, splitter: null, pollIntervalSeconds: 5, timeoutSeconds: 120, FoundryEvals.TaskAdherence))
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
        Console.WriteLine(foundryOn ? $"   Foundry endpoint: {foundryEndpoint}" : "   (set AZURE_FOUNDRY_ENDPOINT to weave Foundry evals into the hierarchy)");

        // ── Run the SUT agent once, with its tools, then score its answer AND its tool calls ─────────────
        AITool[] tools =
        [
            AIFunctionFactory.Create(GetWeatherForecast, WeatherTool),
            AIFunctionFactory.Create(FindFamilyActivities, ActivitiesTool),
        ];
        AIAgent agent = judgeChat.AsAIAgent(
            name: "TravelAdvisor",
            instructions: "You are a helpful travel advisor. Check the forecast and the family activities with your tools before you plan.",
            tools: tools);
        const string query = "Plan a 3-day trip to Kyoto for a family with two kids.";
        var runResponse = await agent.RunAsync(query);

        // The calls the agent made, as the run recorded them: an empty list when it called nothing, which the tool
        // evaluators grade as "no tool was called", not as "no tool data".
        var testCase = new TestCase { Name = "kyoto-family-trip", Input = query, ExpectedTools = [WeatherTool, ActivitiesTool] };
        var run = new TestResult
        {
            TestName = testCase.Name,
            ActualOutput = runResponse.Text,
            ToolUsage = ToolUsageExtractor.Extract(runResponse.Messages.Cast<object>().ToList()),
        };
        var input = testCase.ToEvalInput(run) with
        {
            ToolDefinitions = tools.OfType<AIFunction>().Select(ToDefinition).ToList(),
        };
        var calls = input.ToolCalls is { } recorded
            ? recorded.Count == 0 ? "none" : string.Join(", ", recorded.Select(c => c.Name))
            : "not recorded";
        Console.WriteLine($"   Tool calls the agent made: {calls}  (offered: {WeatherTool}, {ActivitiesTool}; fixed fixture data)");

        EvalResult result = await benchmark.EvaluateAsync(input);

        // ── Per-component console summary ───────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"   Benchmark score: {result.Score.Value:P0} ({result.Score.Label}) across "
                          + $"{result.Details.SubResults?.Count ?? 0} top-level component(s).");
        Console.WriteLine();
        foreach (var sub in result.Details.SubResults ?? [])
        {
            var isFoundry = sub.Metric.Key.StartsWith("foundry.", StringComparison.OrdinalIgnoreCase);
            var icon = isFoundry ? "☁ " : "⚙ ";
            if (!sub.Score.CountsTowardAggregate())
            {
                var isIntentional = sub.Score.Label == "skipped";
                Console.ForegroundColor = isIntentional ? ConsoleColor.Yellow : ConsoleColor.Red;
                var verb = isIntentional ? "SKIPPED" : "NOT MEASURED";
                var reason = sub.Details.Evidence?.FirstOrDefault()?.Message ?? "no result";
                Console.WriteLine($"   {icon} [{sub.Metric.Name}]  {verb} ({sub.Score.Label}) — {reason}");
                Console.ResetColor();
                SampleOutcome.Skip($"{sub.Metric.Name}: not measured ({sub.Score.Label}) — {reason}");
                continue;
            }

            Console.ForegroundColor = sub.Score.Passed ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"   {icon} [{sub.Metric.Name}]  {sub.Score.Value:P0} ({sub.Score.Label})");
            Console.ResetColor();
            if (sub.Details.Evidence is { } evs)
                foreach (var ev in evs.Where(e => string.Equals(e.Reference, "report_url", StringComparison.Ordinal)))
                    Console.WriteLine($"               🔗 {ev.Message}");
            if (!isFoundry)
            {
                foreach (var leaf in AtomicLeaves(sub))
                {
                    var score = leaf.Score.CountsTowardAggregate() ? $"{leaf.Score.Value:P0}" : "not measured";
                    Console.WriteLine($"               • {leaf.Metric.Name}: {score} ({leaf.Score.Label})");
                }
            }
        }
        Console.WriteLine();

        RecordVerdict(result, foundryOn);

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
        Console.WriteLine("   - A tool-accuracy eval needs the run's tool calls in its input: no calls in, nothing graded.");
        Console.WriteLine("   - For a large item count, prefer the BATCHED path (MafEvalFoundryAlongsideLocal) so Foundry runs once, not per item.");
    }

    /// <summary>
    /// The verdict: no Foundry leaf is the sample's point not shown; the tree's root verdict is recorded as every report
    /// reads it (FAIL fails, WARN warns, a root that measured nothing is not measured).
    /// </summary>
    private static void RecordVerdict(EvalResult result, bool foundryOn)
    {
        if (!foundryOn)
        {
            SampleOutcome.Skip(
                "no Foundry leaf in the tree: AZURE_FOUNDRY_ENDPOINT is not set (an Azure AI Foundry project and `az login`); " +
                "only the AgentEval sub-composite ran");
        }

        SampleOutcome.RecordVerdict("Trip-Planner Hybrid Benchmark", result.Score);
    }

    private static IEnumerable<EvalResult> AtomicLeaves(EvalResult node) =>
        node.Details.SubResults is { Count: > 0 } children ? children.SelectMany(AtomicLeaves) : [node];

    /// <summary>A tool as the agent was offered it: name, description and parameter schema.</summary>
    private static ToolDefinition ToDefinition(AIFunction function) => new(
        function.Name,
        function.Description,
        JsonSerializer.Deserialize<Dictionary<string, object>>(function.JsonSchema.GetRawText()));

    [Description("Gets the daily weather forecast for a city.")]
    private static string GetWeatherForecast(
        [Description("The city, e.g. Kyoto")] string city,
        [Description("How many days, 1 to 7")] int days = 3)
    {
        if (!city.Contains("Kyoto", StringComparison.OrdinalIgnoreCase))
            return $"No forecast available for {city}.";

        string[] forecast =
        [
            "Day 1: sunny, 24°C, light breeze",
            "Day 2: showers in the afternoon, 21°C",
            "Day 3: cloudy, 22°C",
        ];
        return string.Join("\n", forecast.Take(Math.Clamp(days, 1, forecast.Length)));
    }

    [Description("Finds family-friendly activities in a city, suited to children.")]
    private static string FindFamilyActivities([Description("The city, e.g. Kyoto")] string city)
    {
        if (!city.Contains("Kyoto", StringComparison.OrdinalIgnoreCase))
            return $"No activities found for {city}.";

        return string.Join("\n",
            "Kyoto Railway Museum — hands-on trains, indoor (good for a rainy afternoon)",
            "Arashiyama Monkey Park — short uphill walk, best in the morning",
            "Kyoto Aquarium — indoor, next to the Railway Museum",
            "Fushimi Inari Shrine — the lower gates are an easy walk with children",
            "Nishiki Market — food stalls, crowded at midday");
    }
}
