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
/// Sample 14: Stochastic Evaluation - Run tests multiple times for reliability
/// 
/// This demonstrates:
/// - Running the same test N times to measure consistency
/// - Performance metrics in a clean table format
/// - Tool usage tracking across runs
/// - Statistical analysis with min/max/mean display
/// 
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Sample14_StochasticEvaluation
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
        Console.WriteLine($"   🤖 Model: {AIConfig.ModelDeployment}\n");
        
        Console.WriteLine("📝 Step 1: Creating agent with calculator tool...\n");
        
        var agent = CreateAgentWithCalculator();
        Console.WriteLine($"   ✓ Agent created with CalculatorTool\n");
        
        Console.WriteLine("📝 Step 2: Setting up stochastic runner...\n");
        
        var harness = new MAFEvaluationHarness(verbose: false);
        var stochasticRunner = new StochasticRunner(harness, statisticsCalculator: null, new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            ModelName = AIConfig.ModelDeployment
        });
        Console.WriteLine("   ✓ Stochastic runner ready\n");
        
        Console.WriteLine("📝 Step 3: Running stochastic test (3 runs)...\n");
        
        var testCase = new TestCase
        {
            Name = "Calculator Test",
            Input = "What is 42 multiplied by 17? Use the calculator tool.",
            ExpectedOutputContains = "714",
            ExpectedTools = ["CalculatorTool"]
        };
        
        var options = new StochasticOptions(
            Runs: 3,
            SuccessRateThreshold: 0.8,
            EnableStatisticalAnalysis: true,
            MaxParallelism: 1,
            OnProgress: progress =>
            {
                var status = progress.LastResult?.Passed == true ? "✅" : "❌";
                var remaining = progress.EstimatedRemaining?.TotalSeconds ?? 0;
                Console.WriteLine($"   {status} Run {progress.CurrentRun}/{progress.TotalRuns} - " +
                    $"Score: {progress.LastResult?.Score ?? 0}/100 " +
                    $"({progress.Elapsed.TotalSeconds:F1}s elapsed, ~{remaining:F0}s remaining)");
            }
        );
        
        Console.WriteLine($"   Test: {testCase.Name}");
        Console.WriteLine($"   Runs: {options.Runs}, Threshold: {options.SuccessRateThreshold:P0}\n");
        
        var result = await stochasticRunner.RunStochasticTestAsync(agent, testCase, options);
        Console.WriteLine(); // Extra line after progress
        
        Console.WriteLine("📝 Step 4: Results\n");
        Console.WriteLine($"   {result.Summary}\n");
        
        // Main metrics table (now includes performance summary)
        result.PrintTable("Performance Metrics");
        
        // Tool usage summary
        result.PrintToolSummary("CalculatorTool");
        
        Console.WriteLine("\n📝 Step 5: Assertions\n");
        
        try
        {
            result.Should()
                .HavePassRateAtLeast(0.7)
                .HaveMeanScoreAtLeast(60)
                .HaveStandardDeviationAtMost(25);
            
            Console.WriteLine("   ✅ All assertions passed!\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Assertion failed: {ex.Message}\n");
        }
        
        PrintFooter(result.Passed);
    }
    
    private static IEvaluableAgent CreateAgentWithCalculator()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();
        
        var agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "Calculator Agent",
                ChatOptions = new ChatOptions
                {
                    Instructions = "You are a math assistant. Always use the CalculatorTool for calculations.",
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
        ║  Sample 14: Stochastic Evaluation                             ║
        ║  Run tests multiple times for reliability measurement         ║
        ╚═══════════════════════════════════════════════════════════════╝

        """);
    }
    
    private static void PrintFooter(bool passed)
    {
        Console.WriteLine(passed
            ? """
              ╔═══════════════════════════════════════════════════════════════╗
              ║  ✅ Stochastic Evaluation PASSED                              ║
              ╚═══════════════════════════════════════════════════════════════╝
              """
            : """
              ╔═══════════════════════════════════════════════════════════════╗
              ║  ⚠️  Stochastic Evaluation FAILED                             ║
              ╚═══════════════════════════════════════════════════════════════╝
              """);
    }
}
