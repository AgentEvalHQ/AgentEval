// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Testing;

namespace AgentEval.Samples;

/// <summary>
/// AgentSession Lifecycle — MAF session management with evaluation
/// 
/// This sample demonstrates:
/// - MAF's session lifecycle: CreateSessionAsync → multi-turn → ResetSessionAsync
/// - How MAFAgentAdapter maps ResetSessionAsync to new session creation
/// - Multi-turn conversations with session state (context retention)
/// - Session isolation: facts from session 1 do not leak into session 2
/// - ConversationRunner with session resets between test cases
/// 
/// Key insight: MAFAgentAdapter.ResetSessionAsync() calls agent.CreateSessionAsync()
/// internally, which gives you a fresh session. This is what ISessionResettableAgent
/// provides — and it's how CrossSessionEvaluator and ConversationRunner work.
/// 
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// 
/// ⏱️ Time to understand: 5 minutes
/// ⏱️ Time to run: ~10–20 seconds (real LLM calls)
/// </summary>
public static class AgentSessionLifecycle
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

        // Step 1: Create MAF ChatClientAgent with history provider
        Console.WriteLine("📝 Step 1: Creating MAF agent with session management...\n");

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        var mafAgent = chatClient.AsAIAgent(
            name: "SessionDemo",
            instructions: """
                You are a helpful assistant. Remember all facts the user tells you within 
                a conversation. When asked to recall facts, include the specific details.
                Keep responses concise (1-2 sentences).
                """);

        // Wrap with MAFAgentAdapter — provides ISessionResettableAgent
        var adapter = new MAFAgentAdapter(mafAgent);

        Console.WriteLine($"   Agent    : {adapter.Name}");
        Console.WriteLine($"   Interfaces: IEvaluableAgent, IStreamableAgent, ISessionResettableAgent");
        Console.WriteLine($"   Session  : Managed by MAFAgentAdapter (CreateSessionAsync on reset)\n");

        // Step 2: Multi-turn conversation in Session 1
        Console.WriteLine("📝 Step 2: Session 1 — plant facts and verify context retention...\n");

        var response1 = await adapter.InvokeAsync("My name is Alice and I work at Contoso.");
        Console.WriteLine($"   👤 User: My name is Alice and I work at Contoso.");
        Console.WriteLine($"   🤖 Bot : {Truncate(response1.Text, 120)}\n");

        var response2 = await adapter.InvokeAsync("What is my name?");
        Console.WriteLine($"   👤 User: What is my name?");
        Console.WriteLine($"   🤖 Bot : {Truncate(response2.Text, 120)}");

        var containsAlice = response2.Text?.Contains("Alice", StringComparison.OrdinalIgnoreCase) == true;
        Console.ForegroundColor = containsAlice ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"   ✅ Recalled 'Alice': {containsAlice}\n");
        Console.ResetColor();

        // Step 3: Reset session — creates a new AgentSession
        Console.WriteLine("📝 Step 3: Resetting session (adapter.ResetSessionAsync())...\n");
        Console.WriteLine("   This calls agent.CreateSessionAsync() internally,");
        Console.WriteLine("   creating a fresh session with empty conversation history.\n");

        await adapter.ResetSessionAsync();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("   🔄 Session reset complete — new session created\n");
        Console.ResetColor();

        // Step 4: Session 2 — verify isolation
        Console.WriteLine("📝 Step 4: Session 2 — verify session isolation...\n");

        var response3 = await adapter.InvokeAsync("What is my name?");
        Console.WriteLine($"   👤 User: What is my name?");
        Console.WriteLine($"   🤖 Bot : {Truncate(response3.Text, 120)}");

        var noLongerKnows = response3.Text?.Contains("Alice", StringComparison.OrdinalIgnoreCase) != true;
        Console.ForegroundColor = noLongerKnows ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"   ✅ Session isolated (forgot 'Alice'): {noLongerKnows}\n");
        Console.ResetColor();

        // Step 5: Use ConversationRunner with automatic session management
        Console.WriteLine("📝 Step 5: ConversationRunner with session-managed multi-turn...\n");

        await adapter.ResetSessionAsync();
        var runner = new ConversationRunner(adapter);

        var testCase = ConversationalTestCase.Create("Session Context Retention")
            .WithDescription("Verify agent retains facts within a single session")
            .AddUserTurn("Remember that project Phoenix launches on March 15th.")
            .AddUserTurn("When does project Phoenix launch?")
            .WithMaxDuration(TimeSpan.FromSeconds(30))
            .Build();

        Console.WriteLine($"   Test: {testCase.Name}");
        Console.WriteLine($"   Turns: {testCase.Turns.Count} user messages\n");

        var conversationResult = await runner.RunAsync(testCase);

        Console.WriteLine($"   Result: {(conversationResult.Success ? "✅ PASSED" : "❌ FAILED")}");
        Console.WriteLine($"   Duration: {conversationResult.Duration.TotalMilliseconds:F0}ms");
        Console.WriteLine($"   Turns completed: {conversationResult.ActualTurns.Count}\n");

        var turnIndex = 0;
        foreach (var turn in conversationResult.ActualTurns)
        {
            turnIndex++;
            var icon = turn.Role == "user" ? "👤" : "🤖";
            Console.WriteLine($"   [{turnIndex}] {icon} {Truncate(turn.Content, 120)}\n");
        }

        PrintKeyTakeaways();
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🔄 SAMPLE 06: AgentSession Lifecycle                                        ║
║   MAF Session Management with Evaluation                                      ║
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
   │  ⚠️  SKIPPING SAMPLE 06 - Azure OpenAI Credentials Required               │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  This sample demonstrates AgentSession lifecycle with real LLM calls.       │
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
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              🎯 KEY TAKEAWAYS                                   │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                 │
│  1. MAFAgentAdapter manages AgentSession internally:                            │
│     var adapter = new MAFAgentAdapter(mafAgent);                               │
│     // Automatically creates sessions for each invocation                      │
│                                                                                 │
│  2. ResetSessionAsync() maps to CreateSessionAsync():                           │
│     await adapter.ResetSessionAsync();                                         │
│     // Internally: _session = await _agent.CreateSessionAsync()                │
│     // Fresh session = empty conversation history                               │
│                                                                                 │
│  3. Session isolation is guaranteed:                                            │
│     Session 1: ""My name is Alice"" → agent remembers                           │
│     Reset → new session created                                                │
│     Session 2: ""What is my name?"" → agent doesn't know                        │
│                                                                                 │
│  4. ConversationRunner handles multi-turn within a session:                     │
│     All turns in a test case share one session = context retention              │
│     Reset between test cases = isolation                                        │
│                                                                                 │
│  5. This is how CrossSessionEvaluator works under the hood:                    │
│     Plant facts → reset → ask questions → measure retention                    │
│     The evaluator calls the same ResetSessionAsync() you see here              │
│                                                                                 │
│  6. For persistent memory across resets, use AIContextProvider:                 │
│     See Sample 33 (AIContextProvider Memory) for the native MAF pattern        │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }
}
