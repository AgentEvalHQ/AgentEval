// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;

namespace AgentEval.Samples;

/// <summary>
/// Sample D5: Streaming vs Non-Streaming Performance Comparison
/// 
/// This demonstrates:
/// - Running the SAME test with streaming and non-streaming (async)
/// - Comparing performance metrics between both approaches
/// - Understanding token/cost tracking in each mode
/// - Supplying model identity and preserving reported cached tokens for cost estimates
/// 
/// ⏱️ Time to understand: 8 minutes
/// </summary>
public static class StreamingVsAsyncPerformance
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            PrintMissingCredentialsBox();
            SampleOutcome.Skip("no chat provider configured");
            return;
        }

        Console.WriteLine($"   🔗 Endpoint: {AIConfig.Endpoint}");
        Console.WriteLine($"   🤖 Model: {AIConfig.ModelDeployment}\n");

        Console.WriteLine($"🚀 Running with REAL {AIConfig.ProviderName}...\n");

        // Create agent
        var agent = CreateAgent();
        // MAF does not expose the underlying ChatResponse.ModelId here. This adapter
        // tags responses with the configured identity; aliases need matching rates.
        var adapter = new MAFIdentifiableAgentAdapter(agent, AIConfig.ModelDeployment, AIConfig.ModelDeployment);
        var harness = new MAFEvaluationHarness(verbose: true);

        var options = new EvaluationOptions 
        { 
            TrackPerformance = true
            // ModelName is an optional pricing override; PricingResolver can price aliases.
        };

        var testCase = new TestCase
        {
            Name = "Streaming vs Async Comparison",
            Input = "Explain in 2-3 sentences why software testing is important.",
        };

        Console.WriteLine("   The adapter supplies configured model identity; ModelName can override pricing.\n");

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
    private static string FmtCost(decimal? c) => c.HasValue ? $"${c:F6}" : "N/A";

    private static AIAgent CreateAgent()
    {
        var chatClient = AIConfig.CreateChatClient(AIConfig.ModelDeployment);

        return chatClient.AsAIAgent(
            name: "PerformanceTestAgent",
            instructions: "You are a helpful assistant. Answer concisely.");
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🌊 SAMPLE D5: STREAMING VS NON-STREAMING PERFORMANCE                       ║
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
   │  ⚠️  SKIPPING SAMPLE D5 - Model Provider Required                         │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  This sample compares streaming vs non-streaming performance metrics.       │
   │                                                                             │
   │  Set these environment variables:                                           │
   │    AZURE_OPENAI_*         - endpoint, api key, deployment                   │
   │    or BITDEER_API_KEY     - model defaults to GLM-5.3 Flash                 │
   │    or OPENAI_COMPATIBLE_* - endpoint, api key, model                        │
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
            Console.WriteLine($"   Input Tokens:  {p.PromptTokens?.ToString() ?? "unknown"}{tokenSource}");
            Console.WriteLine($"   Output Tokens: {p.CompletionTokens?.ToString() ?? "unknown"}{tokenSource}");
            Console.WriteLine($"   Total Tokens:  {p.TotalTokens?.ToString() ?? "unknown"}");
            Console.WriteLine($"   Model:         {p.ModelUsed ?? "(not set)"}");
            if (p.CostEstimate is { } estimate)
            {
                Console.WriteLine($"   Cached input:  {estimate.CachedInputTokens?.ToString() ?? "unknown"} (subset of input)");
                Console.WriteLine($"   Cost status:   {estimate.Status}; model source: {estimate.ModelSource}");
            }
            
            // Show cost
            if (p.EstimatedCost.HasValue)
            {
                var costNote = p.TokensAreEstimated ? " (based on estimated tokens)" : "";
                Console.WriteLine($"   Est. Cost:     ${p.EstimatedCost:F6}{costNote}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("   Est. Cost:     N/A - usable token counts or model rates are unavailable");
                Console.ResetColor();
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("   Performance:   NULL ⚠️ (TrackPerformance not enabled?)");
            Console.ResetColor();
            SampleOutcome.Skip($"no performance data recorded for the {mode} run");
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
        Console.WriteLine($"│ Input Tokens     │ {ap?.PromptTokens?.ToString() ?? "unknown",-13} │ {sp?.PromptTokens?.ToString() ?? "unknown",-13} │");
        Console.WriteLine($"│ Output Tokens    │ {ap?.CompletionTokens?.ToString() ?? "unknown",-13} │ {sp?.CompletionTokens?.ToString() ?? "unknown",-13} │");
        Console.WriteLine($"│ Est. Cost        │ {FmtCost(ap?.EstimatedCost),-13} │ {FmtCost(sp?.EstimatedCost),-13} │");
        Console.WriteLine("└──────────────────┴───────────────┴───────────────┘");

        Console.WriteLine("\n💡 KEY INSIGHTS:");
        Console.WriteLine("   Both methods preserve reported usage; missing measurements remain unknown.");
        Console.WriteLine("   ✅ Streaming provides Time-to-First-Token (TTFT)");
        Console.WriteLine("   ModelName overrides pricing; this adapter otherwise supplies configured identity.");
        Console.WriteLine("   Reported cached input receives a discount when its rate is known.");
        if (ap?.TokensAreEstimated == true || sp?.TokensAreEstimated == true)
        {
            Console.WriteLine("   ℹ️  Tokens estimated (~4 chars/token) since provider didn't return usage");
        }
    }
}
