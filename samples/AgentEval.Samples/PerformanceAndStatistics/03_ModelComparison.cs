// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.Output;
using System.ComponentModel;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace AgentEval.Samples;

/// <summary>
/// Sample D3: Model Comparison - Compare multiple models on the same task
/// 
/// This demonstrates:
/// - Using agent factories to swap models
/// - Comparing models on quality, speed, cost, and reliability
/// - Weighted scoring profiles (Quality, Speed, Cost, Reliability focused)
/// - Cost analysis and winner recommendation
/// - Export to Markdown, GitHub PR comments
/// - Table format output like ai-rag-chat-evaluator
/// 
/// ⏱️ Time to understand: 10 minutes
/// 
/// Related samples:
/// - Sample14_StochasticEvaluation: Foundation for statistical analysis
/// - Sample11_DatasetsAndExport: Export formats (JUnit, JSON)
/// </summary>
public static class ModelComparison
{
    public static async Task RunAsync()
    {
        PrintHeader();
        
        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("   ⚠️  This sample requires a model provider (Azure OpenAI, Bitdeer, or any OpenAI-compatible endpoint).\n");
            SampleOutcome.Skip("no chat provider configured");
            return;
        }

        var models = ComparisonModels.Distinct(
            AIConfig.ModelDeployment, AIConfig.SecondaryModelDeployment, AIConfig.TertiaryModelDeployment);

        // Print endpoint for verification
        Console.WriteLine($"   🔗 Endpoint: {AIConfig.Endpoint}");
        for (var i = 0; i < models.Count; i++)
            Console.WriteLine($"   🤖 Model {i + 1}: {models[i]}");
        Console.WriteLine();

        if (!ComparisonModels.Require("D3", models))
            return;

        Console.WriteLine("📝 Step 1: Creating agent factories...\n");

        var factories = CreateFactories(models);
        foreach (var factory in factories)
        {
            Console.WriteLine($"   ✓ {factory.ModelName} ({factory.ModelId})");
        }
        Console.WriteLine();
        
        Console.WriteLine("📝 Step 2: Scoring Weight Profiles\n");
        
        Console.WriteLine("   AgentEval supports different scoring profiles to optimize for your priority:");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("   Profile          │ Quality │ Speed │ Cost │ Reliability");
        Console.WriteLine("   ─────────────────┼─────────┼───────┼──────┼────────────");
        Console.WriteLine("   Default          │   40%   │  20%  │ 20%  │    20%");
        Console.WriteLine("   QualityFocused   │   60%   │  15%  │ 10%  │    15%");
        Console.WriteLine("   SpeedFocused     │   25%   │  50%  │ 10%  │    15%");
        Console.WriteLine("   CostFocused      │   25%   │  10%  │ 50%  │    15%");
        Console.WriteLine("   ReliabilityFocused│  30%   │  15%  │ 15%  │    40%");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("   💡 Using QualityFocused for this comparison\n");
        
        Console.WriteLine("📝 Step 3: Setting up ModelComparer...\n");
        
        var harness = new MAFEvaluationHarness(verbose: false);
        var stochasticRunner = new StochasticRunner(harness, statisticsCalculator: null, new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            // No ModelName: each agent names its own model (MAFIdentifiableAgentAdapter below), so each model's runs are
            // priced at that model's own rate in ModelPricing. A ModelName here would price every model at one rate.
        });
        var comparer = new ModelComparer(stochasticRunner);
        
        var comparisonOptions = new ModelComparisonOptions(
            RunsPerModel: 3,
            ScoringWeights: ScoringWeights.QualityFocused,
            EnableCostAnalysis: true,
            EnableStatistics: true,
            MaxParallelism: 1
        );
        Console.WriteLine($"   ✓ Runs per model: {comparisonOptions.RunsPerModel} (reduced for demo speed)");
        Console.WriteLine($"   ✓ Scoring weights: QualityFocused");
        Console.WriteLine($"   ✓ Cost analysis: Enabled\n");
        
        Console.WriteLine("📝 Step 4: Defining test case...\n");
        
        var testCase = new TestCase
        {
            Name = "Calculator Test",
            Input = "What is 42 multiplied by 17? Use the calculator tool.",
            ExpectedOutputContains = "714",
            ExpectedTools = ["CalculatorTool"]
        };
        Console.WriteLine($"   Test: {testCase.Name}");
        Console.WriteLine($"   Expected: Contains '714', uses CalculatorTool\n");
        
        Console.WriteLine("📝 Step 5: Running model comparison (3 runs per model)...\n");
        
        ModelComparisonResult result;
        try
        {
            result = await comparer.CompareModelsAsync(factories, testCase, comparisonOptions);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   ❌ Comparison failed: {ex.Message}");
            Console.ResetColor();
            // A comparison that threw is an error (a provider call or the comparer failed), not a dimension left unmeasured.
            SampleOutcome.Error($"the comparison threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        Console.WriteLine($"   ✓ Compared {result.ModelResults.Count} models\n");

        Console.WriteLine("📝 Step 6: Rankings (by composite score)\n");

        var nameWidth = Math.Max(11, result.Ranking.Max(r => r.ModelName.Length));
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"   Rank │ {"Model".PadRight(nameWidth)} │ Composite │ Quality │ Speed │  Cost │ Reliability");
        Console.WriteLine($"   ─────┼─{new string('─', nameWidth)}─┼───────────┼─────────┼───────┼───────┼────────────");
        Console.ResetColor();

        foreach (var ranking in result.Ranking)
        {
            var medal = ranking.Rank switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"#{ranking.Rank}" };
            Console.WriteLine($"   {medal,-4} │ {ranking.ModelName.PadRight(nameWidth)} │ {ranking.CompositeScore,9:F1} │ {ranking.QualityScore,7:F1} │ {FormatScore(ranking.SpeedScore),5} │ {FormatScore(ranking.CostScore),5} │ {ranking.ReliabilityScore,8:F1}");
        }
        Console.WriteLine();

        // Not measured is not 100 or 0: before, a model none of whose runs returned scored Speed 100 (the "fastest"),
        // and an unpriced model Cost 100 (the "cheapest"). An unmeasured dimension is never a clean result.
        if (result.SpeedNotMeasuredReason is { } speedNotMeasured)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   ⚠️  Speed n/a — not measured: {speedNotMeasured}\n");
            Console.ResetColor();
            SampleOutcome.Skip($"speed not measured: {speedNotMeasured}");
        }

        if (result.CostNotMeasuredReason is { } costNotMeasured)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   ⚠️  Cost n/a — not measured: {costNotMeasured}");
            Console.ResetColor();
            // Each model is priced from ModelPricing's table at its own id; a model the table does not know is unpriced.
            var unpriced = models.Where(m => ModelPricing.GetPricing(m) is null).ToList();
            Console.WriteLine(unpriced.Count > 0
                ? $"      To rank on cost, give {string.Join(", ", unpriced)} a price: ModelPricing.SetPricing(model, inputPer1K, outputPer1K).\n"
                : "      Every model has a price, so a model's runs returned no token usage to price.\n");
            SampleOutcome.Skip($"cost not measured: {costNotMeasured}");
        }
        else
        {
            Console.WriteLine($"   (Cost: each model's tokens at its own price in ModelPricing, table {ModelPricing.TableName}.)\n");
        }

        Console.WriteLine("📝 Step 7: Cost Analysis\n");
        
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"   {"Model".PadRight(nameWidth)} │ Avg Cost/Call │ Total Cost │ Pass Rate │ Avg Latency");
        Console.WriteLine($"   {new string('─', nameWidth)}─┼───────────────┼────────────┼───────────┼────────────");
        Console.ResetColor();

        foreach (var modelResult in result.ModelResults.OrderBy(m => m.AverageCost ?? decimal.MaxValue))
        {
            var avgCost = modelResult.AverageCost.HasValue ? $"${modelResult.AverageCost:F4}" : "N/A";
            var totalCost = modelResult.TotalCost.HasValue ? $"${modelResult.TotalCost:F4}" : "not priced";
            var latency = modelResult.AverageLatency is { } l ? $"{l.TotalMilliseconds:F0}ms" : "N/A";
            Console.WriteLine($"   {modelResult.ModelName.PadRight(nameWidth)} │ {avgCost,13} │ {totalCost,10} │ {modelResult.PassRate,9:P0} │ {latency,10}");
        }
        foreach (var model in result.ModelResults)
        {
            if (model.TotalCostNotPricedReason is { } reason)
                Console.WriteLine($"   {model.ModelName}: {reason}");
            if (model.AverageCost.HasValue && model.PricedRunCount < model.StochasticResult.IndividualResults.Count)
                Console.WriteLine($"   Average cost covers {model.PricedRunCount} of {model.StochasticResult.IndividualResults.Count} priced runs.");
        }
        Console.WriteLine();

        Console.WriteLine("📝 Step 8: Winner Recommendation\n");

        var weights = comparisonOptions.EffectiveScoringWeights;
        var speedMeasured = result.SpeedNotMeasuredReason is null;
        var costMeasured = result.CostNotMeasuredReason is null;
        // The weights the composite actually used: a dimension not measured is left out and the rest scaled up to the full weight.
        var rescale = weights.TotalWeight / (weights.Quality + weights.Reliability +
                                             (speedMeasured ? weights.Speed : 0) + (costMeasured ? weights.Cost : 0));
        // ModelComparer names the first-listed model Winner on an exact tie (documented): say it is a tie, as D4 and G7 do.
        var tied = result.Ranking.Where(r => Math.Abs(r.CompositeScore - result.Winner.CompositeScore) < 1e-9).ToList();
        if (tied.Count > 1)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   🤝 Tie: {string.Join(" = ", tied.Select(r => r.ModelName))} (composite {result.Winner.CompositeScore:F1}); " +
                              "no model is ahead, the first listed is shown below.");
            Console.ResetColor();
        }
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"   🏆 Recommended Model: {result.Winner.ModelName}");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"   Composite Score: {result.Winner.CompositeScore:F1} / 100");
        Console.WriteLine($"   ├─ Quality:     {result.Winner.QualityScore:F1} (weight: {weights.Quality * rescale:P0})");
        Console.WriteLine(speedMeasured
            ? $"   ├─ Speed:       {result.Winner.SpeedScore:F1} (weight: {weights.Speed * rescale:P0})"
            : $"   ├─ Speed:       n/a  (not measured: left out of the composite)");
        Console.WriteLine(costMeasured
            ? $"   ├─ Cost:        {result.Winner.CostScore:F1} (weight: {weights.Cost * rescale:P0})"
            : $"   ├─ Cost:        n/a  (not measured: left out of the composite)");
        Console.WriteLine($"   └─ Reliability: {result.Winner.ReliabilityScore:F1} (weight: {weights.Reliability * rescale:P0})");
        Console.WriteLine();

        RecordChecks(result);
        
        Console.WriteLine("📝 Step 9: Export to Markdown\n");
        
        // Show GitHub PR comment format
        Console.WriteLine("   📋 GitHub PR Comment (collapsible):\n");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(result.ToGitHubComment());
        Console.ResetColor();
        Console.WriteLine();
        
        // Save full report
        var reportPath = Path.Combine(Path.GetTempPath(), "model-comparison-report.md");
        await result.SaveToMarkdownAsync(reportPath);
        Console.WriteLine($"   ✅ Full report saved to: {reportPath}");
        Console.WriteLine();
        
        Console.WriteLine("   💡 Export methods available:");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("      • result.ToMarkdown()             - Full report with all sections");
        Console.WriteLine("      • result.ToRankingsTable()        - Compact rankings table");
        Console.WriteLine("      • result.ToDetailedMetricsTable() - Pass rate, latency, cost");
        Console.WriteLine("      • result.ToStatisticsTable()      - Mean, median, percentiles");
        Console.WriteLine("      • result.ToGitHubComment()        - Collapsible PR comment");
        Console.WriteLine("      • result.SaveToMarkdownAsync()    - Save to file");
        Console.ResetColor();
        Console.WriteLine();
        
        PrintFooter(result.Winner.ModelName);
    }
    
    private static List<IAgentFactory> CreateFactories(IReadOnlyList<string> models) =>
        models.Select(model => (IAgentFactory)new DelegateAgentFactory(model, model, () => CreateAgentWithTool(model)))
              .ToList();

    // Each model's pass/fail on the test case is the comparison's data, printed above; a weak model losing is the
    // sample working. What the sample itself must not report cleanly: a recommendation resting on a model that failed
    // the check, or a comparison in which no model answered correctly at all (a wiring fault until shown otherwise).
    private static void RecordChecks(ModelComparisonResult result)
    {
        if (result.ModelResults.All(m => m.StochasticResult.PassedCount == 0))
        {
            SampleOutcome.Fail("no model passed a single run of the test case (contains '714', calls CalculatorTool): " +
                               "there is no correct answer to rank");
            return;
        }

        var winner = result.ModelResults.First(m => m.ModelId == result.Winner.ModelId);
        var runs = winner.StochasticResult.IndividualResults.Count;
        if (winner.StochasticResult.PassedCount < runs)
        {
            SampleOutcome.Warn($"the recommended model {winner.ModelName} passed {winner.StochasticResult.PassedCount}/{runs} " +
                               "runs of the test case");
        }
    }

    private static string FormatScore(double? score) => score is { } value ? $"{value:F1}" : "n/a";
    
    private static IEvaluableAgent CreateAgentWithTool(string deployment)
    {
        var chatClient = AIConfig.CreateChatClient(deployment);
        
        var agent = chatClient.AsAIAgent(
            name: $"Calculator Agent ({deployment})",
            instructions: "You are a math assistant. Always use the CalculatorTool for calculations.",
            tools: [AIFunctionFactory.Create(CalculatorTool)]);

        // The identifiable adapter names the model, so the harness prices this agent's runs at its own rate.
        return new MAFIdentifiableAgentAdapter(agent, deployment, deployment);
    }

    [Description("Performs basic arithmetic operations")]
    private static string CalculatorTool(
        [Description("First operand")] double a,
        [Description("Operation: add, subtract, multiply, divide")] string operation,
        [Description("Second operand")] double b)
    {
        var result = operation.ToLowerInvariant() switch
        {
            "add" or "+" or "plus" => a + b,
            "subtract" or "-" or "minus" => a - b,
            "multiply" or "*" or "x" or "times" => a * b,
            "divide" or "/" => b != 0 ? a / b : double.NaN,
            _ => throw new ArgumentException($"Unknown operation: {operation}")
        };
        return $"{a} {operation} {b} = {result}";
    }
    
    private static void PrintHeader()
    {
        Console.WriteLine("""

        ╔═══════════════════════════════════════════════════════════════╗
        ║  Sample D3: Model Comparison                                  ║
        ║  Compare models on quality, speed, cost & tool usage          ║
        ╚═══════════════════════════════════════════════════════════════╝

        """);
    }
    
    private static void PrintFooter(string winner)
    {
        Console.WriteLine($"""
              ╔═══════════════════════════════════════════════════════════════╗
              ║  🏆 Recommended: {winner,-45} ║
              ╚═══════════════════════════════════════════════════════════════╝
              """);
    }
}
