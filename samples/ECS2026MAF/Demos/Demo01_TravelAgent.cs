// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ECS2026MAF.Agents;

namespace ECS2026MAF.Demos;

/// <summary>
/// Demo 01 — TravelAgent
///
/// Shows a single MAF agent with tools that searches for flights, books them,
/// and sends a confirmation email — all via function calling.
///
/// ⏱️ Runtime: ~10–20 seconds (2–3 LLM calls with tool round-trips)
/// </summary>
public static class Demo01_TravelAgent
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!Config.IsConfigured)
        {
            PrintMissingCredentials();
            return;
        }

        Console.WriteLine("  Creating TravelBookingAgent...\n");
        var agent = TravelAgentFactory.Create();

        Console.WriteLine("  Sending request to agent...\n");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();

        var request = "Search for flights from Madrid to Tokyo for 2026-09-15, " +
                      "book the cheapest one for 1 passenger, " +
                      "and send a confirmation to traveller@example.com.";

        Console.WriteLine($"  Request: \"{request}\"\n");

        var session  = await agent.CreateSessionAsync();
        var messages = new[] { new ChatMessage(ChatRole.User, request) };
        var response = await agent.RunAsync(messages, session);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  Agent response:\n");
        Console.WriteLine(response.Text);
        Console.ResetColor();
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Demo 01 — TravelAgent                                                      ║
║   Single agent · SearchFlights → BookFlight → SendConfirmation              ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
  ⚠️  Skipping Demo 01 — Azure OpenAI credentials required.

     Set the following environment variables and try again:
       AZURE_OPENAI_ENDPOINT
       AZURE_OPENAI_API_KEY
       AZURE_OPENAI_DEPLOYMENT
");
        Console.ResetColor();
    }
}
