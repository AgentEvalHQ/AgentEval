// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using Azure.AI.OpenAI;
using ECS2026MAF.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace ECS2026MAF.Demos;

/// <summary>
/// Demo 03 — Live Demo  (COMPLETED REFERENCE)
///
/// This is the fully finished version of what gets built live during Demo 03.
/// Tools are reused from TravelTools — no new code needed there.
/// </summary>
public static class Demo03_LiveDemoComplete
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!Config.IsConfigured)
        {
            PrintMissingCredentials();
            return;
        }

        // ════════════════════════════════════════════════════════════════════
        //  1  —  Connect to Azure OpenAI
        // ════════════════════════════════════════════════════════════════════

        var azure      = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);
        var chatClient = azure.GetChatClient(Config.Model).AsIChatClient();

        // ════════════════════════════════════════════════════════════════════
        //  2  —  Register travel tools  (static methods from TravelTools)
        // ════════════════════════════════════════════════════════════════════

        AITool[] tools =
        [
            AIFunctionFactory.Create(TravelTools.GetInfoAbout),
            AIFunctionFactory.Create(TravelTools.SearchFlights),
            AIFunctionFactory.Create(TravelTools.BookFlight),
            AIFunctionFactory.Create(TravelTools.SearchHotel),
            AIFunctionFactory.Create(TravelTools.BookHotel),
            AIFunctionFactory.Create(TravelTools.GetUserConfirmation),
            AIFunctionFactory.Create(TravelTools.SendConfirmation),
        ];

        // ════════════════════════════════════════════════════════════════════
        // SNIPPET 3  —  Create the ChatClientAgent
        // ════════════════════════════════════════════════════════════════════

        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "TravelAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are Alex, a friendly travel assistant.
                    Use the available tools to help the user research destinations,
                    search and book flights and hotels, and send confirmations.
                    Always call GetUserConfirmation before booking anything.
                    """,
                Tools = [..tools]
            }
        });

        // ════════════════════════════════════════════════════════════════════
        //  4  —  Interactive chat loop
        // ════════════════════════════════════════════════════════════════════

        var session = await agent.CreateSessionAsync();
        var history = new List<ChatMessage>();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ✅ TravelAgent ready!  Type a request — or 'exit' to quit.\n");
        Console.ResetColor();

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("  You: ");
            Console.ResetColor();

            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input) || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                break;

            history.Add(new ChatMessage(ChatRole.User, input));

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("\n  " + new string('─', 70));
            Console.ResetColor();

            var response = await agent.RunAsync(history, session);
            history.Add(new ChatMessage(ChatRole.Assistant, response.Text ?? ""));

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("\n  Alex: ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(response.Text);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("\n  " + new string('─', 70) + "\n");
            Console.ResetColor();
        }

        Console.WriteLine("\n  Goodbye! 👋\n");
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Demo 03 — Live Demo  (Completed Reference)                                 ║
║   ChatClientAgent · 7 tools · interactive chat loop  — built from scratch   ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
  ⚠️  Skipping Demo 03 — Azure OpenAI credentials required.

     Set the following environment variables and try again:
       AZURE_OPENAI_ENDPOINT
       AZURE_OPENAI_API_KEY
       AZURE_OPENAI_DEPLOYMENT
");
        Console.ResetColor();
    }
}
