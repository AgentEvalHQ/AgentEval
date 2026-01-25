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
/// Sample 15: Model Comparison - Compare multiple models on the same task
/// 
/// This demonstrates:
/// - Using agent factories to swap models
/// - Comparing models on quality, speed, cost, and reliability
/// - Table format output like ai-rag-chat-evaluator
/// - Tool usage tracking in comparison
/// 
/// ⏱️ Time to understand: 8 minutes
/// </summary>
public static class Sample15_ModelComparison
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
        Console.WriteLine($"   🤖 Model 2: {AIConfig.SecondaryModelDeployment} (GPT-4o)");
        Console.WriteLine($"   🤖 Model 3: {AIConfig.TertiaryModelDeployment} (GPT-4.1)\n");
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 1: Create agent factories
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 1: Creating agent factories...\n");
        
        var factories = CreateFactories();
        foreach (var factory in factories)
        {
            Console.WriteLine($"   ✓ {factory.ModelName} ({factory.ModelId})");
        }
        Console.WriteLine();
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 2: Set up stochastic runner for each model
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 2: Setting up test infrastructure...\n");
        
        var harness = new MAFEvaluationHarness(verbose: false);
        var stochasticRunner = new StochasticRunner(harness, new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            ModelName = AIConfig.ModelDeployment // Will be overridden by factory
        });
        Console.WriteLine("   ✓ Test runner ready\n");
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 3: Define test case (same as Sample 14 for consistency)
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 3: Defining test case...\n");
        
        var testCase = new TestCase
        {
            Name = "Calculator Test",
            Input = "What is 42 multiplied by 17? Use the calculator tool.",
            ExpectedOutputContains = "714",
            ExpectedTools = ["CalculatorTool"]
        };
        Console.WriteLine($"   Test: {testCase.Name}");
        Console.WriteLine($"   Expected: Contains '714', uses CalculatorTool\n");
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 4: Run stochastic tests for each model
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 4: Running comparison (5 runs per model)...\n");
        
        var stochasticOptions = new StochasticOptions(
            Runs: 5,
            SuccessRateThreshold: 0.6,
            EnableStatisticalAnalysis: true,
            MaxParallelism: 1
        );
        
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
                Console.WriteLine($"      ✓ {result.PassedCount}/{result.IndividualResults.Count} passed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      ❌ Error: {ex.Message}");
            }
        }
        Console.WriteLine();
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 5: Display comparison table
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 5: Model Comparison Results\n");
        
        // Use extension method for fluent printing
        modelResults.PrintComparisonTable();
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 6: Determine winner
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 6: Winner\n");
        
        var winner = modelResults
            .Where(m => m.Result.Passed)
            .OrderByDescending(m => m.Result.Statistics.MeanScore)
            .ThenBy(m => m.Result.DurationStats.Mean)
            .FirstOrDefault();
        
        if (winner.ModelName != null)
        {
            Console.WriteLine($"   🏆 Best Model: {winner.ModelName}");
            Console.WriteLine($"      Pass Rate: {winner.Result.Statistics.PassRate:P0}");
            Console.WriteLine($"      Mean Score: {winner.Result.Statistics.MeanScore:F1}");
            Console.WriteLine($"      Avg Duration: {winner.Result.DurationStats.Mean:F0}ms\n");
        }
        else
        {
            Console.WriteLine("   ⚠️ No models passed the success threshold\n");
        }
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 7: Export to Markdown (NEW!)
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 7: Export comparison to Markdown...\n");
        
        // Show a preview of what ToMarkdown() produces
        Console.WriteLine("   📄 Markdown export preview (ToGitHubComment()):\n");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("   ### 🤖 Model Comparison Results");
        Console.WriteLine($"   **Test:** {testCase.Name}");
        if (winner.ModelName != null)
        {
            Console.WriteLine($"   **Winner:** 🏆 {winner.ModelName} (Score: {winner.Result.Statistics.MeanScore:F1})");
        }
        Console.WriteLine();
        Console.WriteLine("   | Model | Pass Rate | Mean Score | Avg Duration |");
        Console.WriteLine("   |-------|-----------|------------|--------------||");
        foreach (var (name, res) in modelResults.OrderByDescending(m => m.Result.Statistics.MeanScore))
        {
            Console.WriteLine($"   | {name} | {res.Statistics.PassRate:P0} | {res.Statistics.MeanScore:F1} | {res.DurationStats.Mean:F0}ms |");
        }
        Console.ResetColor();
        Console.WriteLine();
        
        Console.WriteLine("   💡 Full Markdown export methods available:");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("      • result.ToMarkdown()           - Full report with all sections");
        Console.WriteLine("      • result.ToRankingsTable()      - Compact rankings table");
        Console.WriteLine("      • result.ToDetailedMetricsTable() - Pass rate, latency, cost");
        Console.WriteLine("      • result.ToStatisticsTable()    - Mean, median, percentiles");
        Console.WriteLine("      • result.ToGitHubComment()      - Collapsible PR comment");
        Console.WriteLine("      • result.SaveToMarkdownAsync()  - Save to file");
        Console.ResetColor();
        Console.WriteLine();
        
        PrintFooter(winner.ModelName ?? "None");
    }
    
    private static List<IAgentFactory> CreateFactories()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        
        return new List<IAgentFactory>
        {
            // Model 1: gpt-5-mini
            new DelegateAgentFactory(
                AIConfig.ModelDeployment,
                "GPT-5 Mini",
                () => CreateAgentWithTool(azureClient, AIConfig.ModelDeployment)),
            
            // Model 2: gpt-4o
            new DelegateAgentFactory(
                AIConfig.SecondaryModelDeployment,
                "GPT-4o",
                () => CreateAgentWithTool(azureClient, AIConfig.SecondaryModelDeployment)),
            
            // Model 3: gpt-4.1
            new DelegateAgentFactory(
                AIConfig.TertiaryModelDeployment,
                "GPT-4.1",
                () => CreateAgentWithTool(azureClient, AIConfig.TertiaryModelDeployment))
        };
    }
    
    private static IEvaluableAgent CreateAgentWithTool(AzureOpenAIClient client, string deployment)
    {
        var chatClient = client
            .GetChatClient(deployment)
            .AsIChatClient();
        
        var agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = $"Calculator Agent ({deployment})",
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
    
    private static void PrintHeader()
    {
        Console.WriteLine("""

        ╔═══════════════════════════════════════════════════════════════╗
        ║  Sample 15: Model Comparison                                  ║
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
