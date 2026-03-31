// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.MAF.Evaluators;
using System.ComponentModel;

using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.Samples;

/// <summary>
/// Sample 05: Light Path — AgentEval metrics as MEAI IEvaluator
///
/// This demonstrates:
/// - agent.EvaluateAsync() with AgentEval metrics (the slide 2 demo)
/// - Mixing AgentEval + MEAI evaluators
/// - Preset bundles: Quality(), Safety(), Agentic()
/// - AssertAllPassed() for CI/CD gates
///
/// ⏱️ Time to understand: 3 minutes
/// </summary>
public static class LightPathMAFIntegration
{
    public static async Task RunAsync()
    {
        PrintHeader();

        // ════════════════════════════════════════════════════════════
        // STEP 1: Create a MAF agent with tools
        // ════════════════════════════════════════════════════════════

        // ════════════════════════════════════════════════════════════
        // DEMO 1: agent.EvaluateAsync() — the one-liner
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("━━━ DEMO 1: agent.EvaluateAsync() with AgentEval ━━━━━━━━━━━━━\n");
        Console.WriteLine("   CODE:");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("   var results = await agent.EvaluateAsync(queries,");
        Console.WriteLine("       AgentEvalEvaluators.Agentic([\"SearchFlights\"]));");
        Console.WriteLine("   results.AssertAllPassed();");
        Console.ResetColor();
        Console.WriteLine();

        // This is REAL — it runs the agent, evaluates, and asserts
        // Fresh agent per demo so MockTravelChatClient._callCount starts at 0
        var agent1 = CreateTravelAgent();
        Console.WriteLine($"   🤖 Agent: {agent1.Name}");
        Console.WriteLine("   🔧 Tools: SearchFlights, SearchHotels\n");

        var queries = new[] { "Find flights from Seattle to Paris for next Friday" };

        var results = await agent1.EvaluateAsync(
            queries,
            AgentEvalEvaluators.Agentic(["SearchFlights"]));

        Console.WriteLine($"   📊 Results: {results.Passed}/{results.Total} passed");
        foreach (var item in results.Items)
        {
            Console.WriteLine($"      Query: \"{Truncate(item.Query, 60)}\"");
            foreach (var (name, metric) in item.Metrics)
            {
                if (metric is NumericMetric num)
                {
                    var icon = num.Interpretation?.Failed != true ? "✅" : "❌";
                    Console.WriteLine($"      {icon} {name}: {num.Value:F1}/5.0");
                }
            }
        }

        results.AssertAllPassed();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n   ✅ results.AssertAllPassed() — no exception thrown!\n");
        Console.ResetColor();

        // ════════════════════════════════════════════════════════════
        // DEMO 2: Multiple evaluators — AgentEval + MEAI side by side
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("━━━ DEMO 2: Multiple evaluators in one call ━━━━━━━━━━━━━━━━━\n");
        Console.WriteLine("   CODE:");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("   var results = await agent.EvaluateAsync(queries, [");
        Console.WriteLine("       AgentEvalEvaluators.ToolSuccess(),              // individual metric");
        Console.WriteLine("       AgentEvalEvaluators.Agentic([\"SearchFlights\"]),  // bundle");
        Console.WriteLine("   ]);");
        Console.ResetColor();
        Console.WriteLine();

        // Two evaluators — one result set per evaluator
        // Fresh agent so _callCount starts at 0 (independent from Demo 1)
        var agent2 = CreateTravelAgent();
        var multiResults = await agent2.EvaluateAsync(
            queries,
            evaluators: new MEAIIEvaluator[]
            {
                AgentEvalEvaluators.ToolSuccess(),                 // individual metric
                AgentEvalEvaluators.Agentic(["SearchFlights"]),    // bundle (2 metrics)
            });

        for (int i = 0; i < multiResults.Count; i++)
        {
            var r = multiResults[i];
            Console.WriteLine($"   📊 Evaluator {i + 1}: {r.Passed}/{r.Total} passed");
            foreach (var item in r.Items)
            {
                foreach (var (name, metric) in item.Metrics)
                {
                    if (metric is NumericMetric num)
                    {
                        var icon = num.Interpretation?.Failed != true ? "✅" : "❌";
                        Console.WriteLine($"      {icon} {name}: {num.Value:F1}/5.0");
                    }
                }
            }
        }
        Console.WriteLine();

        // ════════════════════════════════════════════════════════════
        // DEMO 3: LLM-judged quality evaluation (live, with credentials)
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("━━━ DEMO 3: LLM-judged Quality + Safety evaluation ━━━━━━━━━━\n");

        if (AIConfig.IsConfigured)
        {
            var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
            var judgeClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

            Console.WriteLine("   CODE:");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("   var results = await agent.EvaluateAsync(queries,");
            Console.WriteLine("       AgentEvalEvaluators.Quality(judgeClient));");
            Console.ResetColor();
            Console.WriteLine();

            Console.WriteLine("   🔄 Running Quality evaluation (4 LLM-as-judge metrics)...\n");
            var agent3 = CreateTravelAgent();
            var qualityResults = await agent3.EvaluateAsync(
                queries,
                AgentEvalEvaluators.Quality(judgeClient));

            Console.WriteLine($"   📊 Quality: {qualityResults.Passed}/{qualityResults.Total} passed");
            foreach (var item in qualityResults.Items)
            {
                foreach (var (name, metric) in item.Metrics)
                {
                    if (metric is NumericMetric num)
                    {
                        var icon = num.Interpretation?.Failed != true ? "✅" : "❌";
                        var reason = num.Interpretation?.Reason ?? "";
                        var scoreIdx = reason.IndexOf("AgentEval score:");
                        var endIdx = reason.IndexOf(')');
                        var scoreInfo = scoreIdx >= 0 && endIdx > scoreIdx
                            ? reason.Substring(scoreIdx, endIdx - scoreIdx + 1) : "";
                        Console.WriteLine($"      {icon} {name}: {num.Value:F1}/5.0 — {scoreInfo}");
                    }
                }
            }

            qualityResults.AssertAllPassed();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n   ✅ Quality evaluation passed!\n");
            Console.ResetColor();

            // Safety
            Console.WriteLine("   🔄 Running Safety evaluation (3 LLM-as-judge metrics)...\n");
            var agent4 = CreateTravelAgent();
            var safetyResults = await agent4.EvaluateAsync(
                queries,
                AgentEvalEvaluators.Safety(judgeClient));

            Console.WriteLine($"   📊 Safety: {safetyResults.Passed}/{safetyResults.Total} passed");
            foreach (var item in safetyResults.Items)
            {
                foreach (var (name, metric) in item.Metrics)
                {
                    if (metric is NumericMetric num)
                    {
                        var icon = num.Interpretation?.Failed != true ? "✅" : "❌";
                        Console.WriteLine($"      {icon} {name}: {num.Value:F1}/5.0");
                    }
                }
            }
            safetyResults.AssertAllPassed();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n   ✅ Safety evaluation passed!\n");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("   ⚠️  Azure OpenAI not configured. Available LLM-judged bundles:\n");
            Console.ResetColor();
            Console.WriteLine("   AgentEvalEvaluators.Quality(judgeClient)   → faithfulness, relevance, coherence, fluency");
            Console.WriteLine("   AgentEvalEvaluators.RAG(judgeClient)       → + context precision/recall, answer correctness");
            Console.WriteLine("   AgentEvalEvaluators.Safety(judgeClient)    → toxicity, bias, misinformation");
            Console.WriteLine("   AgentEvalEvaluators.Advanced(judgeClient)  → all 10 metrics combined\n");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("   Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT to run live");
            Console.ResetColor();
        }

        // ════════════════════════════════════════════════════════════
        // DEMO 4: All available preset bundles
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("\n━━━ DEMO 4: Available preset bundles ━━━━━━━━━━━━━━━━━━━━━━━━━\n");

        PrintBundle("Agentic()", AgentEvalEvaluators.Agentic());
        PrintBundle("Agentic([\"SearchFlights\", \"BookHotel\"])",
            AgentEvalEvaluators.Agentic(["SearchFlights", "BookHotel"]));
        Console.WriteLine("   AgentEvalEvaluators.Quality(judgeClient)   → 4 metrics");
        Console.WriteLine("   AgentEvalEvaluators.RAG(judgeClient)       → 5 metrics");
        Console.WriteLine("   AgentEvalEvaluators.Safety(judgeClient)    → 3 metrics");
        Console.WriteLine("   AgentEvalEvaluators.Advanced(judgeClient)  → 10 metrics");
        Console.WriteLine("   AgentEvalEvaluators.Custom(metric1, ...)   → your choice");

        PrintKeyTakeaways();
    }

    // ════════════════════════════════════════════════════════════════════
    // AGENT & TOOLS
    // ════════════════════════════════════════════════════════════════════

    private static AIAgent CreateTravelAgent()
    {
        if (!AIConfig.IsConfigured)
            return CreateMockTravelAgent();

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "TravelAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a travel booking assistant. When asked to find flights or hotels,
                    ALWAYS use the available tools. Present results clearly and recommend
                    the best option. Be concise.
                    """,
                Tools =
                [
                    AIFunctionFactory.Create(SearchFlights),
                    AIFunctionFactory.Create(SearchHotels),
                ]
            }
        });
    }

    [Description("Search for available flights between two cities on a given date.")]
    public static string SearchFlights(
        [Description("Departure city")] string origin,
        [Description("Arrival city")] string destination,
        [Description("Travel date")] string date)
    {
        Console.WriteLine($"   🔧 SearchFlights({origin} → {destination}, {date})");
        return $"Found 3 flights from {origin} to {destination} on {date}: " +
               "AA101 ($450, 10h), DL205 ($520, 9h), UA309 ($480, 11h)";
    }

    [Description("Search for available hotels in a city for given dates.")]
    public static string SearchHotels(
        [Description("City to search")] string city,
        [Description("Check-in date")] string checkIn,
        [Description("Check-out date")] string checkOut)
    {
        Console.WriteLine($"   🔧 SearchHotels({city}, {checkIn}–{checkOut})");
        return $"Found 3 hotels in {city}: Hotel Le Marais ($180/night, 4★), " +
               "Ibis Paris ($95/night, 3★), Ritz Paris ($650/night, 5★)";
    }

    private static AIAgent CreateMockTravelAgent()
    {
        return new ChatClientAgent(new MockTravelChatClient(), new ChatClientAgentOptions
        {
            Name = "TravelAgent (Mock)",
            ChatOptions = new ChatOptions
            {
                Instructions = "You are a travel booking assistant.",
                Tools =
                [
                    AIFunctionFactory.Create(SearchFlights),
                    AIFunctionFactory.Create(SearchHotels),
                ]
            }
        });
    }

    // ════════════════════════════════════════════════════════════════════
    // DISPLAY HELPERS
    // ════════════════════════════════════════════════════════════════════

    private static void PrintBundle(string name, AgentEvalEvaluator evaluator)
    {
        Console.WriteLine($"   AgentEvalEvaluators.{name,-45} → {evaluator.MetricCount} metrics: [{string.Join(", ", evaluator.MetricNames)}]");
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine("\n\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • agent.EvaluateAsync(queries, evaluator) — one line to evaluate");
        Console.WriteLine("   • results.AssertAllPassed() — CI/CD gate in one call");
        Console.WriteLine("   • AgentEval + MEAI + Foundry evaluators all share the same IEvaluator interface");
        Console.WriteLine("   • Code-based metrics (tool success/selection) run instantly, no LLM");
        Console.WriteLine("   • LLM-based metrics (quality/safety) use IChatClient for LLM-as-judge");
        Console.WriteLine("   • Scores bridge: AgentEval 0-100 → MEAI 1-5, original preserved");
        Console.WriteLine();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   ⚡ SAMPLE 05: LIGHT PATH — agent.EvaluateAsync() with AgentEval            ║
║   Real agent, real evaluation, MEAI-compatible results                        ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }

    // ════════════════════════════════════════════════════════════════════
    // MOCK CLIENT
    // ════════════════════════════════════════════════════════════════════

    private class MockTravelChatClient : IChatClient
    {
        private int _callCount;
        public ChatClientMetadata Metadata => new("MockTravelClient");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
            CancellationToken _ = default)
        {
            _callCount++;
            if (_callCount == 1)
            {
                var toolCall = new FunctionCallContent("call-1", "SearchFlights",
                    new Dictionary<string, object?>
                    { ["origin"] = "Seattle", ["destination"] = "Paris", ["date"] = "next Friday" });
                return Task.FromResult(new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, [toolCall]))
                    { FinishReason = ChatFinishReason.ToolCalls });
            }

            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    "I found 3 flights from Seattle to Paris:\n" +
                    "1. AA101 — $450 (10h)\n2. DL205 — $520 (9h)\n3. UA309 — $480 (11h)\n\n" +
                    "Best value: AA101 at $450. Want me to book it?")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
            CancellationToken _ = default) => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
