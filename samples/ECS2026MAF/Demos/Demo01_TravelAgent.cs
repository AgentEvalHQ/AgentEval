// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ECS2026MAF.Agents;

namespace ECS2026MAF.Demos;

/// <summary>
/// Demo 01 — TravelAgent (single all-in-one agent)
///
/// Shows a single MAF <see cref="ChatClientAgent"/> handling an entire trip:
///   GetInfoAbout → SearchFlights → BookFlight
///                → SearchHotel  → BookHotel → SendConfirmation
///
/// Same task as Demo 02 — but handled by ONE agent instead of a pipeline.
/// Compare results to see how a workflow adds determinism.
///
/// ⏱️ Runtime: ~20–40 seconds (multiple LLM + tool round-trips)
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

        Console.WriteLine("  Creating TravelAgent...\n");
        var agent = TravelAgentFactory.Create();

        Console.WriteLine("  ⏳ Running full-service agent — this may take ~30 seconds...\n");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();

        var request = "Plan a 7-day trip visiting both Tokyo and Cologne. " +
                      "I need city information, flights between them, " +
                      "and hotel bookings for each city. " +
                      "Please send a trip summary to traveller@example.com.";

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
║   Demo 01 — TravelAgent (single agent)                                       ║
║   Research → Flights → Hotels → Confirmation  (same task as Demo 02)        ║
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
