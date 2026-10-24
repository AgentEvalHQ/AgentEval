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
/// Sample D4: Combined Stochastic + Model Comparison
/// 
/// This demonstrates:
/// - Running stochastic tests across multiple models
/// - Comparing models with statistical rigor (5 runs each)
/// - Full metrics: performance, tokens, cost, tool usage
/// - Side-by-side comparison of all aggregated stats
/// 
/// ⏱️ Time to understand: 10 minutes
/// 
/// Why combine stochastic + comparison:
/// - Single runs can be misleading due to LLM non-determinism
/// - Multiple runs per model give statistical confidence
/// - Compare models fairly with variance-aware metrics
/// </summary>
public static class CombinedStochasticComparison
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

        // The primary against the third model (on Azure: gpt-4.1 by default), or the second when no distinct third is set.
        var models = ComparisonModels.Distinct(
            AIConfig.ModelDeployment, AIConfig.TertiaryModelDeployment, AIConfig.SecondaryModelDeployment).Take(2).ToList();

        // Print endpoint for verification
        Console.WriteLine($"   🔗 Endpoint: {AIConfig.Endpoint}");
        for (var i = 0; i < models.Count; i++)
            Console.WriteLine($"   🤖 Model {i + 1}: {models[i]}");
        Console.WriteLine();

        if (!ComparisonModels.Require("D4", models))
            return;

        Console.WriteLine("📝 Step 1: Creating agent factories for 2 models...\n");

        var factories = CreateFactories(models);
        foreach (var factory in factories)
        {
            Console.WriteLine($"   ✓ {factory.ModelName} ({factory.ModelId})");
        }
        Console.WriteLine();
        
        Console.WriteLine("📝 Step 2: Setting up test infrastructure...\n");
        
        var harness = new MAFEvaluationHarness(verbose: false);
        var stochasticRunner = new StochasticRunner(harness, statisticsCalculator: null, new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            // No ModelName: each agent names its own model (MAFIdentifiableAgentAdapter below), so each model's runs are
            // priced at that model's own rate in ModelPricing.
        });
        Console.WriteLine("   ✓ Stochastic runner ready\n");
        
        Console.WriteLine("📝 Step 3: Defining test case...\n");
        
        var testCase = new TestCase
        {
            Name = "Calculator Math Test",
            Input = "What is 123 plus 456? Use the calculator tool to compute this.",
            ExpectedOutputContains = "579",  // 123 + 456 = 579
            ExpectedTools = ["CalculatorTool"]
        };
        Console.WriteLine($"   ✓ Test: {testCase.Name}");
        Console.WriteLine($"   ✓ Expected result: 579\n");
        
        Console.WriteLine("📝 Step 4: Configuring stochastic options (3 runs per model)...\n");
        
        var stochasticOptions = new StochasticOptions(
            Runs: 3,                         // 3 runs per model for demo speed
            SuccessRateThreshold: 0.8,       // 80% must pass
            EnableStatisticalAnalysis: true,
            MaxParallelism: 1,               // Sequential to avoid rate limiting
            DelayBetweenRuns: TimeSpan.FromMilliseconds(1000)  // Increased for rate limits
        );
        Console.WriteLine($"   ✓ {stochasticOptions.Runs} runs per model\n");
        
        Console.WriteLine("📝 Step 5: Running stochastic tests for each model...\n");
        
        var modelResults = new List<(string ModelName, StochasticResult Result)>();
        
        foreach (var factory in factories)
        {
            Console.WriteLine($"   🔄 Testing {factory.ModelName}...");
            
            try
            {
                // Create options with progress callback for this model
                var optionsWithProgress = new StochasticOptions(
                    Runs: stochasticOptions.Runs,
                    SuccessRateThreshold: stochasticOptions.SuccessRateThreshold,
                    EnableStatisticalAnalysis: stochasticOptions.EnableStatisticalAnalysis,
                    MaxParallelism: stochasticOptions.MaxParallelism,
                    DelayBetweenRuns: stochasticOptions.DelayBetweenRuns,
                    OnProgress: progress =>
                    {
                        var status = progress.LastResult?.Passed == true ? "✅" : "❌";
                        Console.WriteLine($"      {status} Run {progress.CurrentRun}/{progress.TotalRuns} - Score: {progress.LastResult?.Score ?? 0}/100");
                    }
                );
                
                var result = await stochasticRunner.RunStochasticTestAsync(
                    factory, 
                    testCase, 
                    optionsWithProgress);
                
                modelResults.Add((factory.ModelName, result));
                Console.WriteLine($"      ✓ Completed: {result.PassedCount}/{result.IndividualResults.Count} passed\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      ❌ Error: {ex.Message}\n");
                // A model whose runs threw is an error, not a skip; the comparison below covers the rest.
                SampleOutcome.Error($"{factory.ModelName}: its runs threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine("📝 Step 6: Comparing models side-by-side...\n");

        if (modelResults.Count == 0)
        {
            Console.WriteLine("   ⚠️ No models completed successfully\n");
            SampleOutcome.Skip("no model completed its runs: nothing was compared");
            return;
        }

        if (modelResults.Count < factories.Count)
        {
            var missing = factories.Select(f => f.ModelName).Except(modelResults.Select(m => m.ModelName));
            SampleOutcome.Skip($"not every model completed its runs ({string.Join(", ", missing)} did not): " +
                               "the comparison covers only the rest");
        }
        
        // Use extension method for comparison table
        modelResults.PrintComparisonTable();
        
        Console.WriteLine("📝 Step 7: Detailed metrics per model...\n");
        
        foreach (var (modelName, result) in modelResults)
        {
            Console.WriteLine($"   ═══════════════════════════════════════════════════════════");
            Console.WriteLine($"   📊 {modelName}");
            Console.WriteLine($"   ═══════════════════════════════════════════════════════════\n");
            
            // Performance metrics table now includes summary
            result.PrintTable(modelName)
                  .PrintToolSummary("CalculatorTool");
            Console.WriteLine();
        }
        
        Console.WriteLine("📝 Step 8: Determining winner...\n");

        if (modelResults.Count < 2)
        {
            Console.WriteLine("   No winner: only one model completed, so there is nothing to compare it with.\n");
            return;
        }

        // A run that errored or got no judge verdict is not measured: with none measured there is nothing to rank, and
        // that is not a model that answered wrongly.
        if (modelResults.All(m => m.Result.MeasuredCount == 0))
        {
            Console.WriteLine("   No winner: no model's run was measured.\n");
            SampleOutcome.Error("no model's run was measured (every run errored or got no judge verdict): there is nothing to rank");
            return;
        }

        var winners = DetermineWinners(modelResults);

        // Each model's pass rate is the comparison's data; what must not read as a clean result is a winner that missed
        // the 80% threshold, or a comparison in which no model answered correctly at all.
        if (modelResults.All(m => m.Result.PassedCount == 0))
        {
            if (modelResults.All(m => m.Result.Verdict == StochasticVerdict.Failed))
                SampleOutcome.Fail("no model passed a single run of the test case (123 + 456 = 579 via CalculatorTool): " +
                                   "there is no correct answer to rank");
            else
                SampleOutcome.Error("no model passed a measured run, and runs that were not measured leave the comparison open");
        }
        else
        {
            foreach (var (name, result) in modelResults.Where(m => winners.Contains(m.ModelName) && !m.Result.Passed))
            {
                if (result.Verdict == StochasticVerdict.Failed)
                    SampleOutcome.Warn($"the winner {name} passed {result.PassedCount}/{result.IndividualResults.Count} " +
                                       $"runs, below the {stochasticOptions.SuccessRateThreshold:P0} threshold");
                else
                    // Undecided: its unmeasured runs could still put it either side of the threshold.
                    SampleOutcome.Error($"the winner {name} passed {result.PassedCount}/{result.IndividualResults.Count} runs and " +
                                        $"{result.NotMeasuredCount} were not measured: whether it meets the " +
                                        $"{stochasticOptions.SuccessRateThreshold:P0} threshold is undecided");
            }
        }

        PrintFooter(winners);
    }
    
    private static List<IAgentFactory> CreateFactories(IReadOnlyList<string> models) =>
        models.Select(model => (IAgentFactory)new DelegateAgentFactory(model, model, () => CreateAgentWithTool(model)))
              .ToList();
    
    private static IEvaluableAgent CreateAgentWithTool(string deployment)
    {
        var chatClient = AIConfig.CreateChatClient(deployment);
        
        var agent = chatClient.AsAIAgent(
            name: $"Calculator Agent ({deployment})",
            instructions: "You are a math assistant. Always use the CalculatorTool for calculations.",
            tools: [AIFunctionFactory.Create(CalculatorTool)]);

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
    
    private static IReadOnlyList<string> DetermineWinners(
        List<(string ModelName, StochasticResult Result)> modelResults)
    {
        // Simple scoring: pass rate * mean score; on a tie, the cheaper model — but only when every model has a cost.
        // An unpriced model read as $0 used to win every tie as the "cheapest". A tie cost cannot break stays a tie:
        // the first model listed is not the winner. The pass rate is over every run (a run that crashed delivered
        // nothing), the mean over the measured runs (Statistics); a model none of whose runs was measured is not ranked
        // rather than ranked last at a placeholder 0.
        var measured = modelResults.Where(m => m.Result.MeasuredCount > 0).ToList();
        foreach (var (name, _) in modelResults.Where(m => m.Result.MeasuredCount == 0))
            Console.WriteLine($"   {name}: not ranked, none of its runs was measured\n");
        var costKnown = measured.All(m => m.Result.CostStats is not null);
        var scored = measured
            .Select(m => new
            {
                Model = m.ModelName,
                Score = (double)m.Result.PassedCount / m.Result.IndividualResults.Count * m.Result.Statistics!.MeanScore,
                PassRate = (double)m.Result.PassedCount / m.Result.IndividualResults.Count,
                Cost = costKnown ? m.Result.CostStats!.Mean : 0
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Cost)
            .ToList();

        Console.WriteLine("   🏆 Model Ranking (by quality score):\n");
        if (!costKnown)
            Console.WriteLine("   (cost not measured for every model: a tie is not broken on cost)\n");

        for (int i = 0; i < scored.Count; i++)
        {
            // Tied models share a place.
            var place = scored.FindIndex(x => Math.Abs(x.Score - scored[i].Score) < 1e-9 && x.Cost == scored[i].Cost);
            var rank = place == 0 ? "🥇" : place == 1 ? "🥈" : "🥉";
            Console.WriteLine($"   {rank} {scored[i].Model}");
            Console.WriteLine($"      Score: {scored[i].Score:F1}  Pass Rate: {scored[i].PassRate:P0}");
            Console.WriteLine();
        }

        var top = scored[0];
        return scored.Where(x => Math.Abs(x.Score - top.Score) < 1e-9 && x.Cost == top.Cost).Select(x => x.Model).ToList();
    }
    
    private static void PrintHeader()
    {
        Console.WriteLine("""

        ╔═══════════════════════════════════════════════════════════════╗
        ║  Sample D4: Combined Stochastic + Model Comparison            ║
        ║  Compare models with statistical rigor (3 runs each)          ║
        ╚═══════════════════════════════════════════════════════════════╝

        """);
    }
    
    private static void PrintFooter(IReadOnlyList<string> winners)
    {
        var headline = winners.Count == 1
            ? $"🏆 Winner: {winners[0],-49}"
            : $"🤝 Tie: {string.Join(" = ", winners),-52}";
        Console.WriteLine($"""
              ╔═══════════════════════════════════════════════════════════════╗
              ║  {headline} ║
              ║  Based on pass rate and quality score; cost breaks a tie      ║
              ╚═══════════════════════════════════════════════════════════════╝
              """);
    }
}
