// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Exporters;
using AgentEval.MAF;
using AgentEval.Models;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Sample 27: Cross-Framework Evaluation — Universal IChatClient Adapter
///
/// Demonstrates the <c>IChatClient.AsEvaluableAgent()</c> one-liner that makes
/// **any LLM provider** evaluable by AgentEval — zero boilerplate:
///   - Azure OpenAI, OpenAI, Ollama, LM Studio, vLLM, Groq, Together.ai, etc.
///   - Batch evaluation across multiple test cases
///   - Export to Markdown via <c>TestSummary.ToEvaluationReport()</c>
///
/// For MAF tool-calling agents, see Samples 2–3, 9–10, 12.
/// For Semantic Kernel integration, see the AgentEval.NuGetConsumer project.
///
/// 🤖 Requires Azure OpenAI credentials (AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY)
/// ⏱️ Time to understand: 3 minutes
/// </summary>
public static class CrossFrameworkEvaluation
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("❌ This sample requires Azure OpenAI credentials.");
            Console.WriteLine("   Set AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY environment variables.");
            Console.ResetColor();
            return;
        }

        // ─── Step 1: Create any IChatClient ──────────────────────────
        Console.WriteLine("📝 Step 1: Create an IChatClient");
        Console.WriteLine("   Any provider implementing IChatClient works:");
        Console.WriteLine("   • Azure OpenAI    → new AzureOpenAIClient(...)");
        Console.WriteLine("   • Ollama          → new OllamaChatClient(...)");
        Console.WriteLine("   • Semantic Kernel → kernel.GetRequiredService<IChatCompletionService>()");
        Console.WriteLine("   • LM Studio       → new OpenAIChatClient(...)");
        Console.WriteLine();

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        IChatClient chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();
        Console.WriteLine($"   ✅ Using: Azure OpenAI ({AIConfig.ModelDeployment})\n");

        // ─── Step 2: One-liner adapter ───────────────────────────────
        Console.WriteLine("📝 Step 2: Convert to evaluable agent with AsEvaluableAgent()");
        var agent = chatClient.AsEvaluableAgent(
            name: AIConfig.ModelDeployment,
            systemPrompt: "You are a helpful assistant. Answer questions concisely.");
        Console.WriteLine($"   ✅ Agent '{agent.Name}' ready for evaluation\n");

        // ─── Step 3: Define test cases ───────────────────────────────
        Console.WriteLine("📝 Step 3: Define test cases");
        var testCases = new List<TestCase>
        {
            new() { Name = "Capital City", Input = "What is the capital of France?",
                     ExpectedOutputContains = "Paris", PassingScore = 70 },
            new() { Name = "Simple Math", Input = "What is 7 * 8?",
                     ExpectedOutputContains = "56", PassingScore = 70 },
            new() { Name = "Color of Sky", Input = "What color is the sky on a clear day?",
                     ExpectedOutputContains = "blue", PassingScore = 70 }
        };
        foreach (var tc in testCases)
            Console.WriteLine($"   • {tc.Name}: \"{tc.Input}\" → expects \"{tc.ExpectedOutputContains}\"");
        Console.WriteLine();

        // ─── Step 4: Batch evaluation ────────────────────────────────
        Console.WriteLine("📝 Step 4: Run batch evaluation");
        var harness = new MAFEvaluationHarness(verbose: true);
        var results = new List<TestResult>();
        foreach (var testCase in testCases)
            results.Add(await harness.RunEvaluationAsync(agent, testCase));

        var summary = new TestSummary("Cross-Framework Evaluation", results);
        Console.WriteLine($"\n   ✅ {summary.TotalCount} tests completed: " +
                          $"{summary.PassedCount} passed, {summary.FailedCount} failed\n");

        // ─── Step 5: Export to Markdown ──────────────────────────────
        Console.WriteLine("📝 Step 5: Export results to Markdown");
        var report = summary.ToEvaluationReport(
            agentName: agent.Name, modelName: AIConfig.ModelDeployment,
            endpoint: AIConfig.Endpoint.ToString());

        var exporter = ResultExporterFactory.Create(ExportFormat.Markdown);
        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var markdown = await reader.ReadToEndAsync();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("   ┌── Markdown Export ──────────────────────────────────────┐");
        foreach (var line in markdown.Split('\n').Take(20))
            Console.WriteLine($"   │ {line.TrimEnd()}");
        if (markdown.Split('\n').Length > 20)
            Console.WriteLine("   │ ... (truncated)");
        Console.WriteLine("   └────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.WriteLine();

        // ─── Key Takeaways ───────────────────────────────────────────
        Console.WriteLine("💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • IChatClient.AsEvaluableAgent() — one line to make any provider evaluable");
        Console.WriteLine("   • Works with ANY provider: Azure OpenAI, Ollama, Semantic Kernel, etc.");
        Console.WriteLine("   • TestSummary.ToEvaluationReport() → Markdown, JUnit, JSON, CSV, TRX");
        Console.WriteLine("   • Same evaluation harness, metrics, and export for every framework");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("🔗 SEE ALSO:");
        Console.WriteLine("   • Samples 2–3: MAF tool-calling agents with fluent assertions");
        Console.WriteLine("   • Samples 9–10: Workflow evaluation with tool tracking");
        Console.WriteLine("   • NuGetConsumer project: Real Semantic Kernel + AgentEval integration");
        Console.ResetColor();
        Console.WriteLine();
    }

    // ─── Header ──────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🌐 SAMPLE 27: CROSS-FRAMEWORK EVALUATION                                    ║
║   Universal IChatClient Adapter · Any Provider · Export                        ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}
