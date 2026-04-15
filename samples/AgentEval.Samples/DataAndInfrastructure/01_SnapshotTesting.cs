// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Core;
using AgentEval.Snapshots;
using System.ComponentModel;
using System.Text.Json;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace AgentEval.Samples;

/// <summary>
/// Sample F1: Snapshot Testing - Detecting regressions in agent behavior
/// 
/// This demonstrates:
/// - Capturing a real agent response as a baseline snapshot
/// - Re-running the same prompt and comparing against the baseline
/// - Built-in scrubbing of timestamps, GUIDs, and dynamic values
/// - Semantic comparison for fuzzy matching of LLM outputs
/// - Snapshotting tool-call data for agentic regression detection
/// - SnapshotStore management (save, load, list, count, delete)
/// 
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class SnapshotTesting
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
        await RunSemanticComparison(harness, adapter, store);
        await RunToolCallSnapshot(harness, adapter, store, comparer);
        RunStoreManagement(store);
        CleanUp(snapshotDir);
        PrintKeyTakeaways();
    }

    private static async Task RunBaselineCapture(MAFEvaluationHarness harness, MAFAgentAdapter adapter, SnapshotStore store)
    {
        Console.WriteLine("📸 STEP 1: Capturing baseline snapshot from real agent...\n");

        var testCase = new TestCase
        {
            Name = "Capital Query",
            Input = "What is the capital of France? Answer in one sentence."
        };

        var result = await harness.RunEvaluationAsync(adapter, testCase);
        var baselineResponse = result.ActualOutput ?? "(no response)";

        Console.WriteLine($"   Query:    \"{testCase.Input}\"");
        Console.WriteLine($"   Response: \"{Truncate(baselineResponse, 80)}\"");

        var snapshot = new
        {
            query = testCase.Input,
            response = baselineResponse,
            timestamp = DateTime.UtcNow.ToString("o"),
            model = AIConfig.ModelDeployment
        };
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

        var baseline = await store.LoadAsync<JsonElement>("capital-query");
        if (baseline.ValueKind != JsonValueKind.Undefined)
        {
            var baselineJson = baseline.GetRawText();
            var currentSnapshot = JsonSerializer.Serialize(new
            {
                query = testCase.Input,
                response = currentResponse,
                timestamp = DateTime.UtcNow.ToString("o"),
                model = AIConfig.ModelDeployment
            });

            // Default comparer ignores "timestamp" and "id" fields automatically
            var comparison = comparer.Compare(baselineJson, currentSnapshot);
            PrintComparisonResult(comparison, "Exact comparison (scrubbing applied)");
        }
    }

    private static async Task RunSemanticComparison(MAFEvaluationHarness harness, MAFAgentAdapter adapter, SnapshotStore store)
    {
        Console.WriteLine("\n🧠 STEP 3: Semantic comparison — tolerating LLM rephrasing...\n");

        // Use semantic comparison: same meaning, different wording should still pass
        var semanticOptions = new SnapshotOptions
        {
            UseSemanticComparison = true,
            SemanticThreshold = 0.5,
            SemanticFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "response", "output", "content", "answer"
            }
        };
        var semanticComparer = new SnapshotComparer(semanticOptions);

        var testCase = new TestCase
        {
            Name = "Science Query",
            Input = "What is photosynthesis? Answer in one sentence."
        };

        // Run twice — LLM will likely rephrase the answer
        var result1 = await harness.RunEvaluationAsync(adapter, testCase);
        var response1 = result1.ActualOutput ?? "(no response)";
        await store.SaveAsync("science-query", new { response = response1 });

        var result2 = await harness.RunEvaluationAsync(adapter, testCase);
        var response2 = result2.ActualOutput ?? "(no response)";

        Console.WriteLine($"   Run 1: \"{Truncate(response1, 70)}\"");
        Console.WriteLine($"   Run 2: \"{Truncate(response2, 70)}\"\n");

        var baseline = await store.LoadAsync<JsonElement>("science-query");
        var currentJson = JsonSerializer.Serialize(new { response = response2 });
        var comparison = semanticComparer.Compare(baseline.GetRawText(), currentJson);

        PrintComparisonResult(comparison, "Semantic comparison (Jaccard similarity)");

        if (comparison.SemanticResults.Count > 0)
        {
            Console.WriteLine("\n   📊 Semantic similarity scores:");
            foreach (var sr in comparison.SemanticResults)
            {
                var icon = sr.Passed ? "✅" : "❌";
                Console.WriteLine($"      {icon} [{sr.Path}] similarity = {sr.Similarity:P1} (threshold = {semanticOptions.SemanticThreshold:P1})");
            }
        }
    }

    private static async Task RunToolCallSnapshot(MAFEvaluationHarness harness, MAFAgentAdapter adapter, SnapshotStore store, SnapshotComparer comparer)
    {
        Console.WriteLine("\n🔧 STEP 4: Snapshotting tool-call data for agentic regression...\n");

        var testCase = new TestCase
        {
            Name = "Weather + Math",
            Input = "What is the weather in Tokyo? Also calculate 25 * 4."
        };

        var result = await harness.RunEvaluationAsync(adapter, testCase,
            new EvaluationOptions { TrackPerformance = true });
        var response = result.ActualOutput ?? "(no response)";
        var toolsCalled = result.ToolUsage?.Calls?.Select(t => t.Name).ToList() ?? new List<string>();

        Console.WriteLine($"   Query:    \"{testCase.Input}\"");
        Console.WriteLine($"   Response: \"{Truncate(response, 70)}\"");
        Console.WriteLine($"   Tools:    [{string.Join(", ", toolsCalled)}]\n");

        // Snapshot the full result including tool usage
        var toolSnapshot = new
        {
            query = testCase.Input,
            response,
            tools = toolsCalled,
            toolCount = toolsCalled.Count,
            timestamp = DateTime.UtcNow.ToString("o")
        };

        if (store.Exists("tool-query"))
        {
            // Compare against previous run
            var baseline = await store.LoadAsync<JsonElement>("tool-query");
            var currentJson = JsonSerializer.Serialize(toolSnapshot);
            var comparison = comparer.Compare(baseline.GetRawText(), currentJson);

            PrintComparisonResult(comparison, "Tool-call regression check");
        }
        else
        {
            Console.WriteLine("   (First run — saving baseline)");
        }

        await store.SaveAsync("tool-query", toolSnapshot);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"   ✅ Tool snapshot saved: {store.GetSnapshotPath("tool-query")}");
        Console.ResetColor();
    }

    private static void RunStoreManagement(SnapshotStore store)
    {
        Console.WriteLine("\n📂 STEP 5: Snapshot store management...\n");

        Console.WriteLine($"   Total snapshots: {store.Count}");
        Console.WriteLine($"   Snapshot names:  [{string.Join(", ", store.ListSnapshots())}]");
    }

    private static void CleanUp(string snapshotDir)
    {
        // Clean up temp directory
        if (Directory.Exists(snapshotDir))
        {
            Directory.Delete(snapshotDir, recursive: true);
            Console.WriteLine($"\n   🗑️  Cleaned up temp snapshots: {snapshotDir}");
        }
    }

    private static void PrintComparisonResult(SnapshotComparisonResult result, string label)
    {
        Console.Write($"\n   {label}: ");
        if (result.IsMatch)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✅ MATCH — no regression detected");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠️ {result.Differences.Count} difference(s) detected (expected with LLM variance)");
            Console.ResetColor();
            foreach (var diff in result.Differences)
            {
                Console.WriteLine($"      • [{diff.Path}] {diff.Message}");
                Console.WriteLine($"        Expected: {Truncate(diff.Expected, 60)}");
                Console.WriteLine($"        Actual:   {Truncate(diff.Actual, 60)}");
            }
        }
        Console.ResetColor();

        if (result.IgnoredFields.Count > 0)
            Console.WriteLine($"   Ignored fields: {string.Join(", ", result.IgnoredFields)}");
    }

    private static AIAgent CreateAgent()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "SnapshotAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a helpful assistant. Give concise, factual answers.
                    Use the available tools when appropriate.
                    """,
                Tools = [AIFunctionFactory.Create(GetWeather), AIFunctionFactory.Create(Calculate)]
            }
        });
    }

    [Description("Get the current weather for a city")]
    private static string GetWeather([Description("City name")] string city)
    {
        return $"The weather in {city} is 22°C and sunny.";
    }

    [Description("Calculate a math expression and return the result")]
    private static string Calculate([Description("Math expression to evaluate")] string expression)
    {
        return $"Result: {expression} = (calculated)";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   📸 SAMPLE F1: SNAPSHOT TESTING                                              ║
║   Detect regressions with real agent responses, tool calls, and semantics     ║
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
   │  ⚠️  SKIPPING SAMPLE F1 - Azure OpenAI Credentials Required               │
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
        Console.WriteLine("   • SnapshotStore persists agent responses to disk for regression detection");
        Console.WriteLine("   • SnapshotComparer provides JSON-aware field-level diffs with auto-scrubbing");
        Console.WriteLine("   • Semantic comparison tolerates LLM rephrasing via Jaccard similarity");
        Console.WriteLine("   • Snapshot tool-call data to catch agentic regressions (wrong tools, missing calls)");
        Console.WriteLine("   • Store management: list, count, delete snapshots for CI/CD workflows");
        Console.WriteLine("\n🔗 NEXT: Run Sample C1 to see conversation evaluation!\n");
    }
}

