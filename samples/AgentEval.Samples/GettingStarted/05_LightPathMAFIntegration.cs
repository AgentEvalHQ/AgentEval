// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
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
/// This demonstrates:
/// - Creating a real MAF agent with tools
/// - Running the agent and capturing its conversation
/// - Evaluating the conversation with AgentEval metrics via MEAI IEvaluator
/// - Mixing AgentEval + MEAI evaluators side by side
/// - Preset bundles and custom composition
///
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class LightPathMAFIntegration
{
    public static async Task RunAsync()
    {
        PrintHeader();

        // ════════════════════════════════════════════════════════════
        // STEP 1: Create a real MAF agent with tools
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("━━━ STEP 1: Create a MAF agent with tools ━━━━━━━━━━━━━━━━━━━━\n");

        var agent = CreateTravelAgent();
        Console.WriteLine($"   🤖 Agent: {agent.Name}");
        Console.WriteLine("   🔧 Tools: SearchFlights, SearchHotels");
        Console.WriteLine();

        // ════════════════════════════════════════════════════════════
        // STEP 2: Run the agent — capture the conversation
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("━━━ STEP 2: Run the agent ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");

        var query = "Find me flights from Seattle to Paris for next Friday";
        Console.WriteLine($"   📨 User: \"{query}\"\n");

        // Run via MAFAgentAdapter to get the conversation in MEAI format
        var adapter = new MAFAgentAdapter(agent);
        var agentResponse = await adapter.InvokeAsync(query);

        Console.WriteLine($"   📤 Agent: {Truncate(agentResponse.Text, 150)}");

        // Build the MEAI conversation format (what MAF's orchestration produces)
        var messages = new List<ChatMessage> { new(ChatRole.User, query) };
        if (agentResponse.RawMessages != null)
        {
            foreach (var raw in agentResponse.RawMessages)
            {
                if (raw is ChatMessage chatMsg)
                    messages.Add(chatMsg);
            }
        }
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, agentResponse.Text)]);

        // Show what tool calls were captured
        var toolUsage = ConversationExtractor.ExtractToolUsage(messages, response);
        if (toolUsage != null)
        {
            Console.WriteLine($"   🔧 Tools called: {string.Join(", ", toolUsage.UniqueToolNames)}");
        }
        Console.WriteLine();

        // ════════════════════════════════════════════════════════════
        // STEP 3: Evaluate with AgentEval — the Light Path
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("━━━ STEP 3: Evaluate with AgentEval (Light Path) ━━━━━━━━━━━━━\n");

        // ── Demo 1: Individual metric ──────────────────────────────

        Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("│  Demo 1: ToolSuccess — did all tools execute cleanly?   │");
        Console.WriteLine("└─────────────────────────────────────────────────────────┘\n");

        MEAIIEvaluator toolSuccessEvaluator = AgentEvalEvaluators.ToolSuccess();
        Console.WriteLine($"   Type: {toolSuccessEvaluator.GetType().Name}");
        Console.WriteLine($"   Implements: MEAI IEvaluator ✅");
        Console.WriteLine($"   Metric: [{string.Join(", ", toolSuccessEvaluator.EvaluationMetricNames)}]\n");

        var result1 = await toolSuccessEvaluator.EvaluateAsync(messages, response);
        PrintMEAIResult("ToolSuccess", result1);

        // ── Demo 2: Agentic bundle ─────────────────────────────────

        Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("│  Demo 2: Agentic bundle — success + tool selection      │");
        Console.WriteLine("└─────────────────────────────────────────────────────────┘\n");

        var agenticEvaluator = AgentEvalEvaluators.Agentic(
            expectedTools: ["SearchFlights"]);
        Console.WriteLine($"   Metrics: {agenticEvaluator.MetricCount} — [{string.Join(", ", agenticEvaluator.MetricNames)}]\n");

        var result2 = await agenticEvaluator.EvaluateAsync(messages, response);
        PrintMEAIResult("Agentic bundle", result2);

        // ── Demo 3: Mix with MEAI ──────────────────────────────────

        Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("│  Demo 3: AgentEval + MEAI in the same IEvaluator list   │");
        Console.WriteLine("└─────────────────────────────────────────────────────────┘\n");

        MEAIIEvaluator meaiRelevance = new RelevanceEvaluator();
        MEAIIEvaluator agentEvalToolSuccess = AgentEvalEvaluators.ToolSuccess();

        // Both implement IEvaluator — they compose naturally
        var mixedEvaluators = new List<MEAIIEvaluator> { meaiRelevance, agentEvalToolSuccess };
        Console.WriteLine($"   {meaiRelevance.GetType().Name,-30} → MEAI built-in");
        Console.WriteLine($"   {agentEvalToolSuccess.GetType().Name,-30} → AgentEval");
        Console.WriteLine($"   Both in List<IEvaluator>: {mixedEvaluators.Count} evaluators ✅\n");

        // Run the AgentEval one (MEAI RelevanceEvaluator needs ChatConfiguration with a real client)
        var mixedResult = await agentEvalToolSuccess.EvaluateAsync(messages, response);
        PrintMEAIResult("AgentEval (from mixed list)", mixedResult);

        // ════════════════════════════════════════════════════════════
        // STEP 4: LLM-judged evaluation (with credentials)
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("━━━ STEP 4: LLM-judged evaluation ━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");

        if (AIConfig.IsConfigured)
        {
            var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
            var judgeClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

            // Quality bundle: faithfulness, relevance, coherence, fluency
            Console.WriteLine("   🔄 Running AgentEvalEvaluators.Quality(judgeClient)...\n");
            var quality = AgentEvalEvaluators.Quality(judgeClient);
            Console.WriteLine($"   Metrics: {quality.MetricCount} — [{string.Join(", ", quality.MetricNames)}]\n");

            var qualityResult = await quality.EvaluateAsync(messages, response);
            PrintMEAIResult("Quality (LLM-judged)", qualityResult);

            // Safety bundle: toxicity, bias, misinformation
            Console.WriteLine("   🔄 Running AgentEvalEvaluators.Safety(judgeClient)...\n");
            var safety = AgentEvalEvaluators.Safety(judgeClient);
            var safetyResult = await safety.EvaluateAsync(messages, response);
            PrintMEAIResult("Safety (LLM-judged)", safetyResult);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("   ⚠️  Azure OpenAI not configured — showing available bundles:\n");
            Console.ResetColor();
            Console.WriteLine("   AgentEvalEvaluators.Quality(judgeClient)   → 4 metrics: faithfulness, relevance, coherence, fluency");
            Console.WriteLine("   AgentEvalEvaluators.RAG(judgeClient)       → 5 metrics: + context precision/recall, answer correctness");
            Console.WriteLine("   AgentEvalEvaluators.Safety(judgeClient)    → 3 metrics: toxicity, bias, misinformation");
            Console.WriteLine("   AgentEvalEvaluators.Advanced(judgeClient)  → 10 metrics: all combined\n");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("   Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT to run live");
            Console.ResetColor();
        }

        // ════════════════════════════════════════════════════════════
        // STEP 5: What it looks like in MAF (preview)
        // ════════════════════════════════════════════════════════════

        Console.WriteLine("\n━━━ STEP 5: How it will look in MAF (ADR-0020) ━━━━━━━━━━━━━━\n");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("   // Today (what this sample does):");
        Console.ResetColor();
        Console.WriteLine("   var adapter = new MAFAgentAdapter(agent);");
        Console.WriteLine("   var response = await adapter.InvokeAsync(query);");
        Console.WriteLine("   var result = await evaluator.EvaluateAsync(messages, response);");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("   // Tomorrow (once MAF ships agent.EvaluateAsync from ADR-0020):");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("   var results = await agent.EvaluateAsync(queries,");
        Console.WriteLine("       AgentEvalEvaluators.Quality(judgeClient));");
        Console.WriteLine("   results.AssertAllPassed();");
        Console.ResetColor();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("   // Mix evaluators from three providers in one call:");
        Console.WriteLine("   var results = await agent.EvaluateAsync(queries, [");
        Console.WriteLine("       new RelevanceEvaluator(),                // MEAI");
        Console.WriteLine("       AgentEvalEvaluators.Safety(judgeClient), // AgentEval");
        Console.WriteLine("       new FoundryEvals(client, \"gpt-4o\"),     // Azure Foundry");
        Console.WriteLine("   ]);");
        Console.ResetColor();

        PrintKeyTakeaways();
    }

    // ════════════════════════════════════════════════════════════════════
    // AGENT SETUP
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
        return new ChatClientAgent(
            new MockTravelChatClient(),
            new ChatClientAgentOptions
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

    private static void PrintMEAIResult(string label, MEAIEvaluationResult result)
    {
        Console.WriteLine($"   📊 {label}:");
        foreach (var (name, metric) in result.Metrics)
        {
            if (metric is NumericMetric num)
            {
                var passed = num.Interpretation?.Failed == false;
                var icon = passed ? "✅" : "❌";
                var meaiScore = num.Value.HasValue ? $"{num.Value:F1}/5.0" : "N/A";
                var reason = num.Interpretation?.Reason ?? "";
                // Extract original AgentEval 0-100 score from reason
                var scoreStart = reason.IndexOf("AgentEval score: ");
                var scoreEnd = reason.IndexOf(')');
                var agentEvalInfo = scoreStart >= 0 && scoreEnd > scoreStart
                    ? reason.Substring(scoreStart, scoreEnd - scoreStart + 1)
                    : "";
                Console.WriteLine($"      {icon} {name}: {meaiScore} MEAI — {agentEvalInfo}");
            }
        }
        Console.WriteLine();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine("\n\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • AgentEval metrics implement MEAI's IEvaluator — plug into any MEAI-based pipeline");
        Console.WriteLine("   • AgentEvalEvaluators factory: .Quality(), .RAG(), .Safety(), .Agentic(), .Advanced()");
        Console.WriteLine("   • Mix freely with MEAI and Foundry evaluators in the same List<IEvaluator>");
        Console.WriteLine("   • Code-based metrics (tool success/selection) need no LLM — run instantly");
        Console.WriteLine("   • LLM-based metrics (quality/safety) use IChatClient for LLM-as-judge");
        Console.WriteLine("   • Score bridge: AgentEval 0-100 → MEAI 1-5, original score preserved in Interpretation");
        Console.WriteLine("   • This is the Light Path — for streaming, tool timelines, workflow graphs: see Samples C");
        Console.WriteLine();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   ⚡ SAMPLE 05: LIGHT PATH — AgentEval as MEAI IEvaluator                    ║
║   Real agent → real evaluation → MEAI-compatible results                      ║
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
    // MOCK CLIENT — for demo without Azure credentials
    // ════════════════════════════════════════════════════════════════════

    private class MockTravelChatClient : IChatClient
    {
        private int _callCount;

        public ChatClientMetadata Metadata => new("MockTravelClient");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _callCount++;

            if (_callCount == 1)
            {
                // First call: agent decides to use SearchFlights tool
                var toolCall = new FunctionCallContent("call-1", "SearchFlights",
                    new Dictionary<string, object?>
                    {
                        ["origin"] = "Seattle",
                        ["destination"] = "Paris",
                        ["date"] = "next Friday"
                    });
                return Task.FromResult(new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, [toolCall]))
                    { FinishReason = ChatFinishReason.ToolCalls });
            }

            // Second call: agent summarizes tool results
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    "I found 3 flights from Seattle to Paris for next Friday:\n" +
                    "1. AA101 — $450 (10h direct)\n" +
                    "2. DL205 — $520 (9h direct)\n" +
                    "3. UA309 — $480 (11h, 1 stop)\n\n" +
                    "The best value is AA101 at $450. Want me to book it?")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
