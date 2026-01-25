// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

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
/// Sample 16: Combined Stochastic + Model Comparison
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
public static class Sample16_CombinedStochasticComparison
{
    public static async Task RunAsync()
    {
        PrintHeader();
        
        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("   ⚠️  This sample requires Azure OpenAI credentials.\n");
            return;
        }
        
        // Print endpoint for verification
        Console.WriteLine($"   🔗 Endpoint: {AIConfig.Endpoint}");
        Console.WriteLine($"   🤖 Model 1: {AIConfig.ModelDeployment} (GPT-5 Mini)");
        Console.WriteLine($"   🤖 Model 2: {AIConfig.TertiaryModelDeployment} (GPT-4.1)\n");
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 1: Create agent factories for 2 models
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 1: Creating agent factories for 2 models...\n");
        
        var factories = CreateFactories();
        foreach (var factory in factories)
        {
            Console.WriteLine($"   ✓ {factory.ModelName} ({factory.ModelId})");
        }
        Console.WriteLine();
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 2: Set up test infrastructure
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 2: Setting up test infrastructure...\n");
        
        var harness = new MAFEvaluationHarness(verbose: false);
        var stochasticRunner = new StochasticRunner(harness, new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            ModelName = AIConfig.ModelDeployment // Will be overridden by factory
        });
        Console.WriteLine("   ✓ Stochastic runner ready\n");
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 3: Define test case
        // ═══════════════════════════════════════════════════════════════
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
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 4: Configure stochastic options (5 runs per model)
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 4: Configuring stochastic options (5 runs per model)...\n");
        
        var stochasticOptions = new StochasticOptions(
            Runs: 5,                         // 5 runs per model for demo
            SuccessRateThreshold: 0.8,       // 80% must pass
            EnableStatisticalAnalysis: true,
            MaxParallelism: 1,               // Sequential to avoid rate limiting
            DelayBetweenRuns: TimeSpan.FromMilliseconds(1000)  // Increased for rate limits
        );
        Console.WriteLine($"   ✓ {stochasticOptions.Runs} runs per model\n");
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 5: Run stochastic tests for each model
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 5: Running stochastic tests for each model...\n");
        
        var modelResults = new List<(string ModelName, StochasticResult Result)>();
        
        foreach (var factory in factories)
        {
            Console.WriteLine($"   🔄 Testing {factory.ModelName}...");
            
            try
            {
                var result = await stochasticRunner.RunStochasticTestAsync(
                    factory, 
                    testCase, 
                    stochasticOptions);
                
                modelResults.Add((factory.ModelName, result));
                Console.WriteLine($"      ✓ Completed: {result.PassedCount}/{result.IndividualResults.Count} passed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      ❌ Error: {ex.Message}");
                // Skip this model if it fails completely
            }
        }
        Console.WriteLine();
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 6: Compare models side-by-side using table
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 6: Comparing models side-by-side...\n");
        
        if (modelResults.Count == 0)
        {
            Console.WriteLine("   ⚠️ No models completed successfully\n");
            return;
        }
        
        // Use extension method for comparison table
        modelResults.PrintComparisonTable();
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 7: Detailed metrics per model
        // ═══════════════════════════════════════════════════════════════
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
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 8: Determine winner
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 8: Determining winner...\n");
        
        var winner = DetermineWinner(modelResults);
        
        PrintFooter(winner);
    }
    
    private static List<IAgentFactory> CreateFactories()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        
        return new List<IAgentFactory>
        {
            // Factory 1: GPT-5 Mini
            new DelegateAgentFactory(
                AIConfig.ModelDeployment,
                "GPT-5 Mini",
                () => CreateAgentWithTool(azureClient, AIConfig.ModelDeployment, "GPT-5 Mini")),
            
            // Factory 2: GPT-4.1
            new DelegateAgentFactory(
                AIConfig.TertiaryModelDeployment,
                "GPT-4.1",
                () => CreateAgentWithTool(azureClient, AIConfig.TertiaryModelDeployment, "GPT-4.1"))
        };
    }
    
    private static IEvaluableAgent CreateAgentWithTool(
        AzureOpenAIClient client,
        string deployment,
        string modelName)
    {
        var chatClient = client
            .GetChatClient(deployment)
            .AsIChatClient();
        
        var agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = $"Calculator Agent ({modelName})",
                Instructions = "You are a math assistant. Always use the CalculatorTool for calculations.",
                ChatOptions = new ChatOptions
                {
                    Tools = [AIFunctionFactory.Create(CalculatorTool)]
                }
            });
        
        return new MAFAgentAdapter(agent);
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
    
    private static string DetermineWinner(
        List<(string ModelName, StochasticResult Result)> modelResults)
    {
        // Simple scoring: pass rate * mean score - (cost impact)
        var scored = modelResults
            .Select(m => new
            {
                Model = m.ModelName,
                Score = m.Result.Statistics.PassRate * m.Result.Statistics.MeanScore,
                PassRate = m.Result.Statistics.PassRate,
                Cost = m.Result.CostStats?.Mean ?? 0
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Cost)
            .ToList();
        
        Console.WriteLine("   🏆 Model Ranking (by quality score):\n");
        
        for (int i = 0; i < scored.Count; i++)
        {
            var rank = i == 0 ? "🥇" : i == 1 ? "🥈" : "🥉";
            Console.WriteLine($"   {rank} {scored[i].Model}");
            Console.WriteLine($"      Score: {scored[i].Score:F1}  Pass Rate: {scored[i].PassRate:P0}");
            Console.WriteLine();
        }
        
        return scored.Count > 0 ? scored[0].Model : "None";
    }
    
    private static void PrintHeader()
    {
        Console.WriteLine("""

        ╔═══════════════════════════════════════════════════════════════╗
        ║  Sample 16: Combined Stochastic + Model Comparison            ║
        ║  Compare models with statistical rigor (5 runs each)          ║
        ╚═══════════════════════════════════════════════════════════════╝

        """);
    }
    
    private static void PrintFooter(string winner)
    {
        Console.WriteLine($"""
              ╔═══════════════════════════════════════════════════════════════╗
              ║  🏆 Winner: {winner,-49} ║
              ║  Based on pass rate, quality score, and cost efficiency       ║
              ╚═══════════════════════════════════════════════════════════════╝
              """);
    }
}
