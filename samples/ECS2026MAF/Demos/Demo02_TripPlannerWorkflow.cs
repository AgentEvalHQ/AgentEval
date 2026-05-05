// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using ECS2026MAF.Workflows;

namespace ECS2026MAF.Demos;

/// <summary>
/// Demo 02 — TripPlanner Workflow
///
/// Shows a sequential MAF workflow with four specialised agents:
///   TripPlanner → FlightReservation → HotelReservation → Presenter
///
/// Each agent uses tools; the output of one feeds directly into the next.
///
/// ⏱️ Runtime: ~60–120 seconds (4 sequential LLM calls with tool round-trips)
/// </summary>
public static class Demo02_TripPlannerWorkflow
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!Config.IsConfigured)
        {
            PrintMissingCredentials();
            return;
        }

        // ── 1. Build workflow ─────────────────────────────────────────────────
        Console.WriteLine("  Building TripPlanner workflow...\n");
        var (workflow, executorIds) = TripPlannerWorkflow.Create();

        Console.WriteLine($"  Name      : {workflow.Name}");
        Console.WriteLine($"  Pipeline  : {string.Join(" → ", executorIds)}");
        Console.WriteLine($"  Model     : {Config.Model}\n");

        // ── 2. Define the request ─────────────────────────────────────────────
        var request = "Plan a 7-day trip visiting both Tokyo and Cologne. " +
                      "I need city information, flights between them, " +
                      "and hotel bookings for each city.";

        Console.WriteLine($"  Request: \"{request}\"\n");
        Console.WriteLine("  ⏳ Executing workflow — this may take a minute...\n");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();

        // ── 3. Run workflow via MAF InProcessExecution ────────────────────────
        string? finalOutput = null;
        try
        {
            // MAF ChatProtocol requires a ChatMessage input + explicit TurnToken
            var run = await InProcessExecution
                .RunStreamingAsync(workflow, new ChatMessage(ChatRole.User, request));

            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            // Collect the final output from the workflow output event
            await foreach (var mafEvent in run.WatchStreamAsync())
            {
                if (mafEvent is WorkflowOutputEvent outputEvent)
                    finalOutput = outputEvent.Data?.ToString();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  ❌ Workflow failed: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine("\n  💡 Ensure your Azure OpenAI deployment supports tool calling (function calling).");
            return;
        }

        // ── 4. Print final itinerary ──────────────────────────────────────────
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  Final itinerary (from Presenter agent):\n");
        Console.WriteLine(finalOutput ?? "(no output)");
        Console.ResetColor();
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Demo 02 — TripPlanner Workflow                                             ║
║   TripPlanner → FlightReservation → HotelReservation → Presenter            ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
  ⚠️  Skipping Demo 02 — Azure OpenAI credentials required.

     Set the following environment variables and try again:
       AZURE_OPENAI_ENDPOINT
       AZURE_OPENAI_API_KEY
       AZURE_OPENAI_DEPLOYMENT
");
        Console.ResetColor();
    }
}
