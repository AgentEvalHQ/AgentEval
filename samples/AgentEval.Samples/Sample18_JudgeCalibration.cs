// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Calibration;
using AgentEval.Core;
using AgentEval.Metrics.RAG;
using AgentEval.Testing;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Sample 18: Judge Calibration - Multi-model consensus for reliable LLM-as-judge evaluations.
/// 
/// This demonstrates:
/// - Using CalibratedJudge for multi-model evaluation
/// - Different voting strategies (Median, Mean, Unanimous, Weighted)
/// - Agreement scores and confidence intervals
/// - Graceful degradation when judges fail
/// 
/// ⏱️ Time to understand: 8 minutes
/// </summary>
public static class Sample18_JudgeCalibration
{
    public static async Task RunAsync()
    {
        PrintHeader();
        
        Console.WriteLine("📝 Step 1: Why use CalibratedJudge?\n");
        
        Console.WriteLine(@"   LLM-as-judge evaluations have inherent variance:
   • A single LLM may give inconsistent scores across runs
   • Different models have different biases
   • Single judge errors can skew entire evaluations
   
   CalibratedJudge solves this by:
   ✓ Running the same evaluation with multiple LLM judges
   ✓ Aggregating scores using configurable voting strategies
   ✓ Calculating agreement percentages and confidence intervals
   ✓ Providing graceful degradation if individual judges fail
");

        // Judge calibration REQUIRES real LLM evaluation - mocking defeats the purpose
        if (!AIConfig.IsConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("   ⚠️  SKIPPING JUDGE CALIBRATION - No Azure credentials configured\n");
            Console.ResetColor();
            Console.WriteLine(@"
   ┌─────────────────────────────────────────────────────────────────────────────┐
   │  🔒 REAL EVALUATION REQUIRED                                                │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  Judge calibration cannot be meaningfully demonstrated with mocks!          │
   │  Mocking judge scores defeats the entire purpose of multi-model consensus.  │
   │                                                                             │
   │  With Azure credentials, this sample would demonstrate:                     │
   │                                                                             │
   │  • Running the same evaluation with 3 judge instances                       │
   │  • Real score variance from LLM non-determinism                             │
   │  • Median, Mean, Weighted, and Unanimous voting strategies                  │
   │  • Agreement scores and confidence intervals                                │
   │  • Why consensus matters for reliable AI evaluation                         │
   │                                                                             │
   │  Set these environment variables to enable:                                 │
   │    AZURE_OPENAI_ENDPOINT                                                    │
   │    AZURE_OPENAI_API_KEY                                                     │
   │    AZURE_OPENAI_DEPLOYMENT                                                  │
   └─────────────────────────────────────────────────────────────────────────────┘
");
            PrintKeyTakeaways();
            return;
        }

        Console.WriteLine("📝 Step 2: Creating real LLM judges using Azure OpenAI...\n");
        
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        
        // Use different models when available, same model otherwise
        var model1 = AIConfig.ModelDeployment;
        var model2 = !string.IsNullOrEmpty(AIConfig.SecondaryModelDeployment) && 
                     AIConfig.SecondaryModelDeployment != model1 
            ? AIConfig.SecondaryModelDeployment : model1;
        var model3 = !string.IsNullOrEmpty(AIConfig.TertiaryModelDeployment) && 
                     AIConfig.TertiaryModelDeployment != model1 
            ? AIConfig.TertiaryModelDeployment : model1;
        
        var judge1Client = azureClient.GetChatClient(model1).AsIChatClient();
        var judge2Client = azureClient.GetChatClient(model2).AsIChatClient();
        var judge3Client = azureClient.GetChatClient(model3).AsIChatClient();
        
        // Create named judge dictionary for the factory pattern
        var judges = new Dictionary<string, IChatClient>
        {
            ["Judge-A"] = judge1Client,
            ["Judge-B"] = judge2Client,
            ["Judge-C"] = judge3Client
        };
        
        Console.WriteLine($"   ✓ Judge-A ({model1})");
        Console.WriteLine($"   ✓ Judge-B ({model2})");
        Console.WriteLine($"   ✓ Judge-C ({model3})");
        if (model1 == model2 && model2 == model3)
            Console.WriteLine("   💡 Using same model 3x to demonstrate score variance from LLM non-determinism\n");
        else
            Console.WriteLine("   💡 Using different models for cross-model consensus\n");

        Console.WriteLine("📝 Step 3: Creating CalibratedJudge with Median voting...\n");
        
        var calibratedJudge = CalibratedJudge.Create(
            ("Judge-A", judge1Client),
            ("Judge-B", judge2Client),
            ("Judge-C", judge3Client));
        
        Console.WriteLine($"   Judges: {string.Join(", ", calibratedJudge.JudgeNames)}");
        Console.WriteLine($"   Strategy: {calibratedJudge.Options.Strategy}");
        Console.WriteLine($"   Max Parallel: {calibratedJudge.Options.MaxParallelJudges}");
        Console.WriteLine($"   Timeout: {calibratedJudge.Options.Timeout.TotalSeconds}s\n");

        Console.WriteLine("📝 Step 4: Running calibrated evaluation...\n");
        
        var context = new EvaluationContext
        {
            Input = "What are the main benefits of renewable energy?",
            Output = "Renewable energy reduces carbon emissions and provides sustainable power sources.",
            Context = "Renewable energy comes from natural sources that replenish themselves. Key benefits include: reduced greenhouse gas emissions, energy independence, lower long-term costs, and sustainable power generation.",
            GroundTruth = "Benefits include reduced emissions, sustainability, and energy independence."
        };
        
        // Use factory pattern - each judge gets its own metric with its own client
        var result = await calibratedJudge.EvaluateAsync(context, judgeName =>
        {
            return new FaithfulnessMetric(judges[judgeName]);
        });
        
        // Display results
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("   ┌──────────────────────────────────────────────┐");
        Console.WriteLine($"   │  Final Score: {result.Score,6:F1} (Median)              │");
        Console.WriteLine($"   │  Agreement:   {result.Agreement,6:F1}%                      │");
        Console.WriteLine($"   │  Std Dev:     {result.StandardDeviation,6:F2}                       │");
        Console.WriteLine($"   │  Consensus:   {(result.HasConsensus ? "Yes ✓" : "No ✗"),-6}                      │");
        Console.WriteLine("   └──────────────────────────────────────────────┘");
        Console.ResetColor();
        
        Console.WriteLine("\n   Individual Judge Scores:");
        foreach (var (judgeName, score) in result.JudgeScores)
        {
            Console.WriteLine($"   • {judgeName}: {score:F1}");
        }
        
        if (result.ConfidenceLower.HasValue && result.ConfidenceUpper.HasValue)
        {
            Console.WriteLine($"\n   95% CI: [{result.ConfidenceLower:F1}, {result.ConfidenceUpper:F1}]");
        }
        Console.WriteLine();

        Console.WriteLine("📝 Step 5: Comparing voting strategies...\n");
        
        // Mean strategy
        var meanJudge = new CalibratedJudge(
            [("Judge-A", judge1Client), ("Judge-B", judge2Client), ("Judge-C", judge3Client)],
            new CalibratedJudgeOptions { Strategy = VotingStrategy.Mean });
        
        var meanResult = await meanJudge.EvaluateAsync(context, jn => new FaithfulnessMetric(judges[jn]));
        
        // Unanimous strategy (requires consensus)
        var unanimousJudge = new CalibratedJudge(
            [("Judge-A", judge1Client), ("Judge-B", judge2Client), ("Judge-C", judge3Client)],
            new CalibratedJudgeOptions { Strategy = VotingStrategy.Unanimous, ConsensusTolerance = 10 });
        
        CalibratedResult? unanimousResult = null;
        try
        {
            unanimousResult = await unanimousJudge.EvaluateAsync(context, jn => new FaithfulnessMetric(judges[jn]));
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"   ⚠️ Unanimous strategy failed: {ex.Message}");
        }
        
        Console.WriteLine("   Strategy Comparison:");
        Console.WriteLine("   ┌─────────────┬─────────┬───────────┐");
        Console.WriteLine("   │ Strategy    │  Score  │ Agreement │");
        Console.WriteLine("   ├─────────────┼─────────┼───────────┤");
        Console.WriteLine($"   │ Median      │  {result.Score,5:F1}  │   {result.Agreement,5:F1}%  │");
        Console.WriteLine($"   │ Mean        │  {meanResult.Score,5:F1}  │   {meanResult.Agreement,5:F1}%  │");
        if (unanimousResult != null)
        {
            Console.WriteLine($"   │ Unanimous   │  {unanimousResult.Score,5:F1}  │   {unanimousResult.Agreement,5:F1}%  │");
        }
        else
        {
            Console.WriteLine("   │ Unanimous   │  N/A    │   N/A     │");
        }
        Console.WriteLine("   └─────────────┴─────────┴───────────┘\n");

        Console.WriteLine("📝 Step 6: Weighted voting (trust Judge-A more)...\n");
        
        var weightedJudge = new CalibratedJudge(
            [("Judge-A", judge1Client), ("Judge-B", judge2Client), ("Judge-C", judge3Client)],
            new CalibratedJudgeOptions
            {
                Strategy = VotingStrategy.Weighted,
                JudgeWeights = new Dictionary<string, double>
                {
                    ["Judge-A"] = 2.0,      // Trust Judge-A twice as much
                    ["Judge-B"] = 1.0,
                    ["Judge-C"] = 1.0
                }
            });
        
        var weightedResult = await weightedJudge.EvaluateAsync(context, jn => new FaithfulnessMetric(judges[jn]));
        
        Console.WriteLine($"   Weighted Score: {weightedResult.Score:F1}");
        Console.WriteLine("   Weights: Judge-A=2.0, Judge-B=1.0, Judge-C=1.0");
        Console.WriteLine($"   (Biased toward Judge-A's score of {result.JudgeScores["Judge-A"]:F1})\n");

        Console.WriteLine("📝 Step 7: Real-world usage pattern...\n");
        
        ShowCodeExample();

        PrintKeyTakeaways();
    }
    
    private static void ShowCodeExample()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"   // Production usage with real Azure OpenAI clients:
   
   var judge = CalibratedJudge.Create(
       (""GPT-4o"", azureClient.GetChatClient(""gpt-4o"").AsIChatClient()),
       (""Claude"", claudeClient),
       (""Gemini"", geminiClient));
   
   // Factory pattern ensures each judge uses its own client
   var result = await judge.EvaluateAsync(context, judgeName =>
   {
       return judgeName switch
       {
           ""GPT-4o"" => new FaithfulnessMetric(gpt4oClient),
           ""Claude"" => new FaithfulnessMetric(claudeClient),
           ""Gemini"" => new FaithfulnessMetric(geminiClient),
           _ => throw new ArgumentException($""Unknown judge: {judgeName}"")
       };
   });
   
   Console.WriteLine($""Score: {result.Score:F1}, Agreement: {result.Agreement:F0}%"");
   Console.WriteLine($""95% CI: [{result.ConfidenceLower:F1}, {result.ConfidenceUpper:F1}]"");
");
        Console.ResetColor();
    }
    
    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║            Sample 18: Judge Calibration (Multi-Model Consensus)               ║
║                                                                               ║
║   Learn how to:                                                               ║
║   • Use multiple LLM judges for reliable evaluations                          ║
║   • Apply different voting strategies (Median, Mean, Weighted)                ║
║   • Interpret agreement scores and confidence intervals                       ║
║   • Handle graceful degradation when judges fail                              ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
    
    private static void PrintKeyTakeaways()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              🎯 KEY TAKEAWAYS                                   │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                 │
│  1. USE FACTORY PATTERN for per-judge metric instantiation:                     │
│     judge.EvaluateAsync(ctx, judgeName => new Metric(clients[judgeName]))      │
│                                                                                 │
│  2. MEDIAN STRATEGY is robust to outliers and biased judges                     │
│                                                                                 │
│  3. AGREEMENT SCORE shows how much judges agree (100% = identical)              │
│                                                                                 │
│  4. CONFIDENCE INTERVALS quantify uncertainty in the score                      │
│                                                                                 │
│  5. GRACEFUL DEGRADATION continues if some judges fail                          │
│                                                                                 │
│  6. USE WEIGHTED voting when you trust certain models more                      │
│                                                                                 │
│  7. USE UNANIMOUS for high-stakes decisions requiring consensus                 │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }
}
