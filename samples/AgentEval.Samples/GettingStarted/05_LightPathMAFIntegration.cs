// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.MAF.Evaluators;
using AgentEval.Metrics.Agentic;
using System.ComponentModel;
using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;
using MEAIEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;

namespace AgentEval.Samples;

/// <summary>
/// Sample 05: Light Path — AgentEval metrics as MEAI IEvaluator
///
/// This demonstrates the "Light Path" integration with Microsoft Agent Framework:
/// - AgentEval metrics implementing MEAI's IEvaluator interface
/// - Preset bundles: Quality(), Safety(), Agentic(), RAG(), Advanced()
/// - Individual metrics as standalone IEvaluator instances
/// - Mixing AgentEval + MEAI evaluators side by side
/// - Custom composition with AgentEvalEvaluators.Custom()
///
/// The Light Path enables AgentEval metrics to plug directly into MAF's
/// agent.EvaluateAsync() orchestration — once MAF ships that API.
/// This sample demonstrates the evaluator side that's ready today.
///
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class LightPathMAFIntegration
{
    public static async Task RunAsync()
    {
        PrintHeader();

        // ════════════════════════════════════════════════════════════
        // PART 1: AgentEval evaluators are real MEAI IEvaluator instances
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("━━━ PART 1: AgentEval as MEAI IEvaluator ━━━━━━━━━━━━━━━━━━━━━\n");

        // Create a conversation to evaluate (simulating what MAF's orchestration would produce)
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Search for flights from Seattle to Paris for next Friday"),
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "SearchFlights",
                    new Dictionary<string, object?>
                    {
                        ["origin"] = "Seattle",
                        ["destination"] = "Paris",
                        ["date"] = "next Friday"
                    })
            ]),
            new(ChatRole.Tool,
            [
                new FunctionResultContent("call-1",
                    "Found 3 flights: AA101 ($450), DL205 ($520), UA309 ($480)")
            ]),
            new(ChatRole.Assistant,
                "I found 3 flights from Seattle to Paris for next Friday:\n" +
                "1. AA101 — $450\n2. DL205 — $520\n3. UA309 — $480\n" +
                "The cheapest option is AA101 at $450. Would you like to book it?"),
        };
        var response = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant,
                "I found 3 flights from Seattle to Paris for next Friday:\n" +
                "1. AA101 — $450\n2. DL205 — $520\n3. UA309 — $480\n" +
                "The cheapest option is AA101 at $450. Would you like to book it?")
        ]);

        Console.WriteLine("📨 User: \"Search for flights from Seattle to Paris for next Friday\"");
        Console.WriteLine("🔧 Tool: SearchFlights(origin: Seattle, destination: Paris)");
        Console.WriteLine("📤 Agent: Found 3 flights, cheapest is AA101 at $450\n");

        // ── Demo 1: Individual metric as IEvaluator ────────────────

        Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("│  Demo 1: Individual AgentEval metric as MEAI IEvaluator │");
        Console.WriteLine("└─────────────────────────────────────────────────────────┘\n");

        // This is an MEAI IEvaluator — it can plug into agent.EvaluateAsync()
        MEAIIEvaluator toolSuccessEvaluator = AgentEvalEvaluators.ToolSuccess();

        Console.WriteLine($"   Type: {toolSuccessEvaluator.GetType().Name}");
        Console.WriteLine($"   Implements: MEAI IEvaluator ✅");
        Console.WriteLine($"   Metric names: [{string.Join(", ", toolSuccessEvaluator.EvaluationMetricNames)}]");

        var result1 = await toolSuccessEvaluator.EvaluateAsync(messages, response);
        PrintMEAIResult("ToolSuccess (individual)", result1);

        // ── Demo 2: Agentic bundle with expected tools ─────────────

        Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("│  Demo 2: Agentic bundle — tool success + selection      │");
        Console.WriteLine("└─────────────────────────────────────────────────────────┘\n");

        var agenticEvaluator = AgentEvalEvaluators.Agentic(
            expectedTools: ["SearchFlights"]);

        Console.WriteLine($"   Type: {agenticEvaluator.GetType().Name}");
        Console.WriteLine($"   Metrics: {agenticEvaluator.MetricCount}");
        Console.WriteLine($"   Names: [{string.Join(", ", agenticEvaluator.MetricNames)}]");

        var result2 = await agenticEvaluator.EvaluateAsync(messages, response);
        PrintMEAIResult("Agentic (bundle)", result2);

        // ════════════════════════════════════════════════════════════
        // PART 2: Mixing AgentEval with MEAI built-in evaluators
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("\n━━━ PART 2: Mix AgentEval + MEAI evaluators ━━━━━━━━━━━━━━━━━\n");

        Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("│  Demo 3: Side-by-side — MEAI + AgentEval evaluators     │");
        Console.WriteLine("└─────────────────────────────────────────────────────────┘\n");

        // MEAI's built-in evaluator (from Microsoft.Extensions.AI.Evaluation.Quality)
        // and AgentEval's evaluator — both implement the same IEvaluator interface
        MEAIIEvaluator meaiEvaluator = new RelevanceEvaluator();
        MEAIIEvaluator agentEvalEvaluator = AgentEvalEvaluators.ToolSuccess();

        Console.WriteLine($"   MEAI evaluator type:      {meaiEvaluator.GetType().Name}");
        Console.WriteLine($"   AgentEval evaluator type:  {agentEvalEvaluator.GetType().Name}");
        Console.WriteLine($"   Both implement:            MEAI IEvaluator ✅");
        Console.WriteLine();

        // Both can be stored in the same collection — they're the same interface
        var evaluators = new List<MEAIIEvaluator> { meaiEvaluator, agentEvalEvaluator };
        Console.WriteLine($"   Combined in IEvaluator list: {evaluators.Count} evaluators");
        Console.WriteLine("   → This is what agent.EvaluateAsync(queries, evaluators) expects\n");

        // Run the AgentEval one (MEAI's RelevanceEvaluator needs a real ChatConfiguration)
        var agentEvalResult = await agentEvalEvaluator.EvaluateAsync(messages, response);
        PrintMEAIResult("AgentEval (in mixed list)", agentEvalResult);

        // ════════════════════════════════════════════════════════════
        // PART 3: All preset bundles
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("\n━━━ PART 3: Available preset bundles ━━━━━━━━━━━━━━━━━━━━━━━━━\n");

        Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("│  Demo 4: All AgentEvalEvaluators factory methods        │");
        Console.WriteLine("└─────────────────────────────────────────────────────────┘\n");

        // Code-based bundles (no LLM needed — can demo without credentials)
        var agentic = AgentEvalEvaluators.Agentic();
        var agenticWithTools = AgentEvalEvaluators.Agentic(["SearchFlights", "BookHotel"]);

        PrintBundle("Agentic()", agentic);
        PrintBundle("Agentic([SearchFlights, BookHotel])", agenticWithTools);

        // LLM-based bundles (show factory but only run if credentials available)
        Console.WriteLine("   LLM-based bundles (require Azure OpenAI credentials):\n");

        if (AIConfig.IsConfigured)
        {
            var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
            var judgeClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

            var quality = AgentEvalEvaluators.Quality(judgeClient);
            var safety = AgentEvalEvaluators.Safety(judgeClient);
            var advanced = AgentEvalEvaluators.Advanced(judgeClient);

            PrintBundle("Quality(judgeClient)", quality);
            PrintBundle("Safety(judgeClient)", safety);
            PrintBundle("Advanced(judgeClient)", advanced);

            // Run quality evaluation live
            Console.WriteLine("   🔄 Running Quality evaluation live...\n");
            var qualityResult = await quality.EvaluateAsync(messages, response);
            PrintMEAIResult("Quality (live)", qualityResult);
        }
        else
        {
            Console.WriteLine("   AgentEvalEvaluators.Quality(judgeClient)   → 4 metrics: faithfulness, relevance, coherence, fluency");
            Console.WriteLine("   AgentEvalEvaluators.RAG(judgeClient)       → 5 metrics: + context precision/recall, answer correctness");
            Console.WriteLine("   AgentEvalEvaluators.Safety(judgeClient)    → 3 metrics: toxicity, bias, misinformation");
            Console.WriteLine("   AgentEvalEvaluators.Advanced(judgeClient)  → 10 metrics: all of the above combined");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("   ⚠️  Set AZURE_OPENAI_* env vars to run LLM-based evaluations live");
            Console.ResetColor();
        }

        // ════════════════════════════════════════════════════════════
        // PART 4: Custom composition
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("\n━━━ PART 4: Custom composition ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");

        Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("│  Demo 5: Build your own evaluator from any IMetric      │");
        Console.WriteLine("└─────────────────────────────────────────────────────────┘\n");

        var custom = AgentEvalEvaluators.Custom(
            new ToolSuccessMetric(),
            new ToolSelectionMetric(["SearchFlights"]));

        Console.WriteLine($"   AgentEvalEvaluators.Custom(ToolSuccess, ToolSelection)");
        Console.WriteLine($"   Metrics: {custom.MetricCount} — [{string.Join(", ", custom.MetricNames)}]");

        var customResult = await custom.EvaluateAsync(messages, response);
        PrintMEAIResult("Custom composite", customResult);

        // ════════════════════════════════════════════════════════════
        // PART 5: What it looks like in MAF (preview)
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("\n━━━ PART 5: How it will look in MAF ━━━━━━━━━━━━━━━━━━━━━━━━━\n");

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("   Once MAF ships agent.EvaluateAsync() from ADR-0020:");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("   // One line — quality evaluation with AgentEval");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("   var results = await agent.EvaluateAsync(queries,");
        Console.WriteLine("       AgentEvalEvaluators.Quality(judgeClient));");
        Console.WriteLine("   results.AssertAllPassed();");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("   // Mix evaluators from three providers");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("   var results = await agent.EvaluateAsync(queries, [");
        Console.WriteLine("       new RelevanceEvaluator(),               // MEAI");
        Console.WriteLine("       AgentEvalEvaluators.Safety(judgeClient), // AgentEval");
        Console.WriteLine("       new FoundryEvals(client, \"gpt-4o\"),     // Foundry");
        Console.WriteLine("   ]);");
        Console.ResetColor();

        PrintKeyTakeaways();
    }

    // ════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════

    private static void PrintMEAIResult(string label, MEAIEvaluationResult result)
    {
        Console.WriteLine($"   📊 Result: {label}");
        foreach (var (name, metric) in result.Metrics)
        {
            if (metric is NumericMetric num)
            {
                var passed = num.Interpretation?.Failed == false;
                var icon = passed ? "✅" : "❌";
                var score = num.Value.HasValue ? $"{num.Value:F1}/5.0" : "N/A";
                var reason = num.Interpretation?.Reason ?? "";
                // Extract the original AgentEval score from the reason string
                var endIdx = reason.IndexOf(')');
                var agentEvalScore = reason.Contains("AgentEval score:") && endIdx >= 0
                    ? reason.Substring(0, endIdx + 1)
                    : "";
                Console.WriteLine($"      {icon} {name}: {score} — {agentEvalScore}");
            }
            else
            {
                Console.WriteLine($"      📏 {name}: {metric}");
            }
        }
        Console.WriteLine();
    }

    private static void PrintBundle(string name, AgentEvalEvaluator evaluator)
    {
        Console.WriteLine($"   AgentEvalEvaluators.{name}");
        Console.WriteLine($"      Metrics: {evaluator.MetricCount} — [{string.Join(", ", evaluator.MetricNames)}]");
        Console.WriteLine($"      Implements: MEAI IEvaluator ✅");
        Console.WriteLine();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine("\n\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • AgentEval metrics implement MEAI's IEvaluator — same interface as MEAI and Foundry");
        Console.WriteLine("   • AgentEvalEvaluators provides preset bundles: Quality, RAG, Safety, Agentic, Advanced");
        Console.WriteLine("   • Individual metrics can be mixed with MEAI/Foundry evaluators in the same list");
        Console.WriteLine("   • Code-based metrics (ToolSuccess, ToolSelection) run locally — no LLM needed");
        Console.WriteLine("   • LLM-based metrics (Quality, Safety) use IChatClient for LLM-as-judge evaluation");
        Console.WriteLine("   • Scores convert: AgentEval 0-100 → MEAI 1-5, with original score preserved");
        Console.WriteLine("   • This is the 'Light Path' — for the deep path with streaming, tool timelines,");
        Console.WriteLine("     and workflow graphs, see Samples C (Workflows & Conversations)");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("   \"The evaluator awakens.\"");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   ⚡ SAMPLE 05: LIGHT PATH — AgentEval as MEAI IEvaluator                    ║
║   AgentEval metrics plug directly into MAF's evaluation pipeline              ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}
