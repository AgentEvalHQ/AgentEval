// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Core;
using AgentEval.Snapshots;
using System.Text.Json;

namespace AgentEval.Samples;

/// <summary>
/// Sample 07: Snapshot Testing - Detecting regressions in agent behavior
/// 
/// This demonstrates:
/// - Using SnapshotStore to save/load agent response snapshots
/// - Using SnapshotComparer to detect regressions
/// - Built-in scrubbing of timestamps, GUIDs, and dynamic values
/// - JSON-aware comparison with field-level diff reporting
/// 
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Sample07_SnapshotTesting
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

        var snapshotDir = Path.Combine(Path.GetTempPath(), "agenteval-snapshots");
        var store = new SnapshotStore(snapshotDir);
        var comparer = new SnapshotComparer();

        var agent = CreateAgent();
        var harness = new MAFEvaluationHarness(verbose: false);
        var adapter = new MAFAgentAdapter(agent);

        await RunBaselineCapture(harness, adapter, store);
        await RunRegressionDetection(harness, adapter, store, comparer);
        DemonstrateScrubbing(comparer);
        DemonstrateJsonComparison(comparer);

        Console.WriteLine($"\n   📁 Snapshots saved to: {snapshotDir}");
        PrintKeyTakeaways();
    }

    private static async Task RunBaselineCapture(MAFEvaluationHarness harness, MAFAgentAdapter adapter, SnapshotStore store)
    {
        Console.WriteLine("📸 STEP 1: Capturing baseline snapshot...\n");

        var testCase = new TestCase
        {
            Name = "Capital Query",
            Input = "What is the capital of France? Answer in one sentence."
        };

        var result = await harness.RunEvaluationAsync(adapter, testCase);
        var baselineResponse = result.ActualOutput ?? "(no response)";

        Console.WriteLine($"   Query:    \"{testCase.Input}\"");
        Console.WriteLine($"   Response: \"{Truncate(baselineResponse, 80)}\"");

        // Save baseline to store
        var snapshot = new { query = testCase.Input, response = baselineResponse };
        await store.SaveAsync("capital-query", snapshot);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n   ✅ Baseline saved: {store.GetSnapshotPath("capital-query")}");
        Console.ResetColor();
    }

    private static async Task RunRegressionDetection(MAFEvaluationHarness harness, MAFAgentAdapter adapter, SnapshotStore store, SnapshotComparer comparer)
    {
        Console.WriteLine("\n🔍 STEP 2: Re-running agent and comparing against baseline...\n");

        var testCase = new TestCase
        {
            Name = "Capital Query",
            Input = "What is the capital of France? Answer in one sentence."
        };

        var result = await harness.RunEvaluationAsync(adapter, testCase);
        var currentResponse = result.ActualOutput ?? "(no response)";

        Console.WriteLine($"   Current response: \"{Truncate(currentResponse, 80)}\"");

        // Load baseline and compare
        var baseline = await store.LoadAsync<JsonElement>("capital-query");
        if (baseline.ValueKind != JsonValueKind.Undefined)
        {
            var baselineJson = baseline.GetRawText();
            var currentSnapshot = JsonSerializer.Serialize(new { query = testCase.Input, response = currentResponse });
            var comparison = comparer.Compare(baselineJson, currentSnapshot);

            PrintComparisonResult(comparison);
        }
    }

    private static void DemonstrateScrubbing(SnapshotComparer comparer)
    {
        Console.WriteLine("\n🧹 STEP 3: Built-in scrubbing for dynamic values...\n");

        var baseline = """{"response": "Paris is the capital. Retrieved at 2025-01-10T08:00:00Z. ID: chatcmpl-abc123"}""";
        var current  = """{"response": "Paris is the capital. Retrieved at 2025-02-14T15:30:00Z. ID: chatcmpl-xyz789"}""";

        Console.WriteLine($"   Baseline: {Truncate(baseline, 80)}");
        Console.WriteLine($"   Current:  {Truncate(current, 80)}\n");

        var result = comparer.Compare(baseline, current);
        Console.Write("   Result: ");
        if (result.IsMatch)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✅ MATCH (timestamps and IDs scrubbed automatically)");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ {result.Differences.Count} difference(s)");
        }
        Console.ResetColor();

        if (result.IgnoredFields.Count > 0)
            Console.WriteLine($"   Ignored fields: {string.Join(", ", result.IgnoredFields)}");
    }

    private static void DemonstrateJsonComparison(SnapshotComparer comparer)
    {
        Console.WriteLine("\n📋 STEP 4: JSON-aware field-level comparison...\n");

        var baseline = """{"response": "Paris is the capital of France.", "tools": ["lookup"], "tokens": 42}""";
        var current  = """{"response": "Berlin is the capital of Germany.", "tools": ["lookup", "verify"], "tokens": 55}""";

        Console.WriteLine($"   Baseline: {baseline}");
        Console.WriteLine($"   Current:  {current}\n");

        var result = comparer.Compare(baseline, current);
        Console.ForegroundColor = result.IsMatch ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"   Match: {(result.IsMatch ? "✅" : "❌")} ({result.Differences.Count} difference(s))");
        Console.ResetColor();

        foreach (var diff in result.Differences)
        {
            Console.WriteLine($"      • [{diff.Path}] {diff.Message}");
            Console.WriteLine($"        Expected: {Truncate(diff.Expected, 50)}");
            Console.WriteLine($"        Actual:   {Truncate(diff.Actual, 50)}");
        }
    }

    private static void PrintComparisonResult(SnapshotComparisonResult result)
    {
        Console.Write("\n   Comparison: ");
        if (result.IsMatch)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✅ MATCHES baseline — no regression detected");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠️ {result.Differences.Count} difference(s) detected (may be expected LLM variation)");
            Console.ResetColor();
            foreach (var diff in result.Differences)
            {
                Console.WriteLine($"      • [{diff.Path}] {diff.Message}");
            }
        }
        Console.ResetColor();
    }

    private static AIAgent CreateAgent()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "SnapshotAgent",
            ChatOptions = new() { Instructions = "You are a helpful assistant. Give concise, factual answers." }
        });
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   📸 SAMPLE 07: SNAPSHOT TESTING                                              ║
║   Detect regressions with SnapshotStore + SnapshotComparer                    ║
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
   │  ⚠️  SKIPPING SAMPLE 07 - Azure OpenAI Credentials Required               │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  This sample captures real agent responses and compares them as snapshots.  │
   │                                                                             │
   │  Set these environment variables:                                           │
   │    AZURE_OPENAI_ENDPOINT     - Your Azure OpenAI endpoint                   │
   │    AZURE_OPENAI_API_KEY      - Your API key                                 │
   │    AZURE_OPENAI_DEPLOYMENT   - Chat model (e.g., gpt-4o)                    │
   └─────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine("\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • SnapshotStore saves/loads JSON snapshots to disk");
        Console.WriteLine("   • SnapshotComparer provides JSON-aware field-level diffs");
        Console.WriteLine("   • Built-in scrubbing handles timestamps, GUIDs, request IDs");
        Console.WriteLine("   • Use snapshots to detect regressions in CI/CD pipelines");
        Console.WriteLine("\n🔗 NEXT: Run Sample 08 to see conversation evaluation!\n");
    }
}

