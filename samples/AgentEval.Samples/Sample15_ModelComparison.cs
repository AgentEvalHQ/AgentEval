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
        Console.WriteLine($"   🤖 Model 1: {AIConfig.ModelDeployment}");
        Console.WriteLine($"   🤖 Model 2: {AIConfig.SecondaryModelDeployment}");
        Console.WriteLine($"   🤖 Model 3: {AIConfig.TertiaryModelDeployment}\n");
        
        Console.WriteLine("📝 Step 1: Creating agent factories...\n");
        
        var factories = CreateFactories();
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
            ModelName = AIConfig.ModelDeployment
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
            return;
        }
        
        Console.WriteLine($"   ✓ Compared {result.ModelResults.Count} models\n");
        
        Console.WriteLine("📝 Step 6: Rankings (by composite score)\n");
        
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("   Rank │ Model       │ Composite │ Quality │ Speed │ Cost │ Reliability");
        Console.WriteLine("   ─────┼─────────────┼───────────┼─────────┼───────┼──────┼────────────");
        Console.ResetColor();
        
        foreach (var ranking in result.Ranking)
        {
            var medal = ranking.Rank switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"#{ranking.Rank}" };
            Console.WriteLine($"   {medal,-4} │ {ranking.ModelName,-11} │ {ranking.CompositeScore,9:F1} │ {ranking.QualityScore,7:F1} │ {ranking.SpeedScore,5:F1} │ {ranking.CostScore,4:F1} │ {ranking.ReliabilityScore,8:F1}");
        }
        Console.WriteLine();
        
        Console.WriteLine("📝 Step 7: Cost Analysis\n");
        
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("   Model       │ Avg Cost/Call │ Total Cost │ Pass Rate │ Avg Latency");
        Console.WriteLine("   ────────────┼───────────────┼────────────┼───────────┼────────────");
        Console.ResetColor();
        
        foreach (var modelResult in result.ModelResults.OrderBy(m => m.AverageCost ?? decimal.MaxValue))
        {
            var avgCost = modelResult.AverageCost.HasValue ? $"${modelResult.AverageCost:F4}" : "N/A";
            var totalCost = modelResult.TotalCost.HasValue ? $"${modelResult.TotalCost:F4}" : "N/A";
            Console.WriteLine($"   {modelResult.ModelName,-11} │ {avgCost,13} │ {totalCost,10} │ {modelResult.PassRate,9:P0} │ {modelResult.AverageLatency.TotalMilliseconds,8:F0}ms");
        }
        Console.WriteLine();
        
        Console.WriteLine("📝 Step 8: Winner Recommendation\n");
        
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"   🏆 Recommended Model: {result.Winner.ModelName}");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"   Composite Score: {result.Winner.CompositeScore:F1} / 100");
        Console.WriteLine($"   ├─ Quality:     {result.Winner.QualityScore:F1} (weight: 60%)");
        Console.WriteLine($"   ├─ Speed:       {result.Winner.SpeedScore:F1} (weight: 15%)");
        Console.WriteLine($"   ├─ Cost:        {result.Winner.CostScore:F1} (weight: 10%)");
        Console.WriteLine($"   └─ Reliability: {result.Winner.ReliabilityScore:F1} (weight: 15%)");
        Console.WriteLine();
        
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
    
    private static List<IAgentFactory> CreateFactories()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        
        var factories = new List<IAgentFactory>
        {
            new DelegateAgentFactory(
                AIConfig.ModelDeployment,
                AIConfig.ModelDeployment,
                () => CreateAgentWithTool(azureClient, AIConfig.ModelDeployment))
        };
        
        if (!string.IsNullOrEmpty(AIConfig.SecondaryModelDeployment) && 
            AIConfig.SecondaryModelDeployment != AIConfig.ModelDeployment)
        {
            factories.Add(new DelegateAgentFactory(
                AIConfig.SecondaryModelDeployment,
                AIConfig.SecondaryModelDeployment,
                () => CreateAgentWithTool(azureClient, AIConfig.SecondaryModelDeployment)));
        }
        
        if (!string.IsNullOrEmpty(AIConfig.TertiaryModelDeployment) && 
            AIConfig.TertiaryModelDeployment != AIConfig.ModelDeployment)
        {
            factories.Add(new DelegateAgentFactory(
                AIConfig.TertiaryModelDeployment,
                AIConfig.TertiaryModelDeployment,
                () => CreateAgentWithTool(azureClient, AIConfig.TertiaryModelDeployment)));
        }
        
        return factories;
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
