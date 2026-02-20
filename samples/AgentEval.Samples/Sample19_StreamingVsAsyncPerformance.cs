// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;

namespace AgentEval.Samples;

/// <summary>
/// Sample 19: Streaming vs Non-Streaming Performance Comparison
/// 
/// This demonstrates:
/// - Running the SAME test with streaming and non-streaming (async)
/// - Comparing performance metrics between both approaches
/// - Understanding token/cost tracking in each mode
/// - Using EvaluationOptions.ModelName for accurate cost calculation
/// 
/// ⏱️ Time to understand: 8 minutes
/// </summary>
public static class Sample19_StreamingVsAsyncPerformance
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            PrintMissingCredentialsBox();
            return;
        }

        Console.WriteLine($"   🔗 Endpoint: {AIConfig.Endpoint}");
        Console.WriteLine($"   🤖 Model: {AIConfig.ModelDeployment}\n");

        Console.WriteLine("🚀 Running with REAL Azure OpenAI...\n");

        // Create agent
        var agent = CreateAgent();
        var adapter = new MAFAgentAdapter(agent);
        var harness = new MAFEvaluationHarness(verbose: true);

        var options = new EvaluationOptions 
        { 
            TrackPerformance = true,
            ModelName = AIConfig.ModelDeployment
        };

        var testCase = new TestCase
        {
            Name = "Streaming vs Async Comparison",
            Input = "Explain in 2-3 sentences why software testing is important.",
        };

        Console.WriteLine($"   📋 EvaluationOptions.ModelName = '{options.ModelName}'\n");

        Console.WriteLine("   ⏳ PART 1: NON-STREAMING (Async) Mode\n");

        var asyncResult = await harness.RunEvaluationAsync(adapter, testCase, options);

        PrintResults("ASYNC", asyncResult);

        Console.WriteLine("   🌊 PART 2: STREAMING Mode\n");

        Console.Write("   Response: ");
        Console.ForegroundColor = ConsoleColor.DarkGray;

        var streamingResult = await harness.RunEvaluationStreamingAsync(adapter, testCase,
            streamingOptions: new StreamingOptions
            {
                OnTextChunk = chunk => Console.Write(chunk),
                OnFirstToken = _ => Console.ForegroundColor = ConsoleColor.White
            },
            options: options);

        Console.ResetColor();
        Console.WriteLine("\n");

        PrintResults("STREAMING", streamingResult);

        PrintComparison(asyncResult, streamingResult);
    }

    private static string Fmt(TimeSpan? t) => t.HasValue ? $"{t.Value.TotalMilliseconds:F0}ms" : "N/A";
    private static string FmtCost(decimal? c) => c > 0 ? $"${c:F6}" : "N/A";

    private static AIAgent CreateAgent()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "PerformanceTestAgent",
            ChatOptions = new() { Instructions = "You are a helpful assistant. Answer concisely." }
        });
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🌊 SAMPLE 19: STREAMING VS NON-STREAMING PERFORMANCE                       ║
║   Compare token/cost capture between streaming and async modes                ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMissingCredentialsBox()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
   ┌─────────────────────────────────────────────────────────────────────────────┐
   │  ⚠️  SKIPPING SAMPLE 19 - Azure OpenAI Credentials Required               │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  This sample compares streaming vs non-streaming performance metrics.       │
   │                                                                             │
   │  Set these environment variables:                                           │
   │    AZURE_OPENAI_ENDPOINT     - Your Azure OpenAI endpoint                   │
   │    AZURE_OPENAI_API_KEY      - Your API key                                 │
   │    AZURE_OPENAI_DEPLOYMENT   - Chat model (e.g., gpt-4o)                    │
   └─────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }

    private static void PrintResults(string mode, TestResult result)
    {
        Console.WriteLine($"   📊 {mode} RESULTS:");
        Console.WriteLine(new string('─', 50));

        if (result.Performance != null)
        {
            var p = result.Performance;
            Console.WriteLine($"   Duration:      {p.TotalDuration.TotalMilliseconds:F0}ms");

            if (p.TimeToFirstToken.HasValue)
                Console.WriteLine($"   TTFT:          {p.TimeToFirstToken.Value.TotalMilliseconds:F0}ms");
            else
                Console.WriteLine($"   TTFT:          N/A");

            // Show token values with estimation indicator
            var tokenSource = p.TokensAreEstimated ? " (estimated)" : " (actual)";
            Console.WriteLine($"   Input Tokens:  {p.PromptTokens ?? 0}{tokenSource}");
            Console.WriteLine($"   Output Tokens: {p.CompletionTokens ?? 0}{tokenSource}");
            Console.WriteLine($"   Total Tokens:  {p.TotalTokens}");
            Console.WriteLine($"   Model:         {p.ModelUsed ?? "(not set)"}");
            
            // Show cost
            if (p.EstimatedCost.HasValue && p.EstimatedCost > 0)
            {
                var costNote = p.TokensAreEstimated ? " (based on estimated tokens)" : "";
                Console.WriteLine($"   Est. Cost:     ${p.EstimatedCost:F6}{costNote}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"   Est. Cost:     N/A - Model '{p.ModelUsed}' not in pricing database");
                Console.ResetColor();
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("   Performance:   NULL ⚠️ (TrackPerformance not enabled?)");
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    private static void PrintComparison(TestResult asyncResult, TestResult streamingResult)
    {
        Console.WriteLine("   📊 COMPARISON TABLE\n");

        var ap = asyncResult.Performance;
        var sp = streamingResult.Performance;

        Console.WriteLine("┌──────────────────┬───────────────┬───────────────┐");
        Console.WriteLine("│ Metric           │ Non-Streaming │ Streaming     │");
        Console.WriteLine("├──────────────────┼───────────────┼───────────────┤");
        Console.WriteLine($"│ Duration         │ {Fmt(ap?.TotalDuration),-13} │ {Fmt(sp?.TotalDuration),-13} │");
        Console.WriteLine($"│ TTFT             │ {"N/A",-13} │ {Fmt(sp?.TimeToFirstToken),-13} │");
        Console.WriteLine($"│ Input Tokens     │ {ap?.PromptTokens ?? 0,-13} │ {sp?.PromptTokens ?? 0,-13} │");
        Console.WriteLine($"│ Output Tokens    │ {ap?.CompletionTokens ?? 0,-13} │ {sp?.CompletionTokens ?? 0,-13} │");
        Console.WriteLine($"│ Est. Cost        │ {FmtCost(ap?.EstimatedCost),-13} │ {FmtCost(sp?.EstimatedCost),-13} │");
        Console.WriteLine("└──────────────────┴───────────────┴───────────────┘");

        // Show if tokens are estimated
        var asyncEst = ap?.TokensAreEstimated == true ? " (est)" : "";
        var streamEst = sp?.TokensAreEstimated == true ? " (est)" : "";
        
        Console.WriteLine("\n💡 KEY INSIGHTS:");
        Console.WriteLine("   ✅ Both methods now capture token usage and costs!");
        Console.WriteLine("   ✅ Streaming provides Time-to-First-Token (TTFT)");
        Console.WriteLine("   ✅ EvaluationOptions.ModelName enables cost calculation");
        if (ap?.TokensAreEstimated == true || sp?.TokensAreEstimated == true)
        {
            Console.WriteLine("   ℹ️  Tokens estimated (~4 chars/token) since provider didn't return usage");
        }
    }
}
