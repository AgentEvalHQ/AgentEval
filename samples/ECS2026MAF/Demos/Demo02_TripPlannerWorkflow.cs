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
        try
        {
            // MAF ChatProtocol requires a ChatMessage input + explicit TurnToken
            var run = await InProcessExecution
                .RunStreamingAsync(workflow, new ChatMessage(ChatRole.User, request));

            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            // Stream all workflow events in real-time as they arrive
            bool textStreamed = false;

            await foreach (WorkflowEvent evt in run.WatchStreamAsync())
            {
                switch (evt)
                {
                    case WorkflowStartedEvent:
                        Console.ForegroundColor = ConsoleColor.DarkCyan;
                        Console.WriteLine("  ▶ Workflow started\n");
                        Console.ResetColor();
                        break;

                    case ExecutorInvokedEvent invoke:
                        if (textStreamed) { Console.WriteLine("\n"); textStreamed = false; }
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"  🤖 [{invoke.ExecutorId}] starting...");
                        Console.ResetColor();
                        break;

                    case AgentResponseUpdateEvent agentUpdate:
                        if (!string.IsNullOrEmpty(agentUpdate.Update.Text))
                        {
                            Console.Write(agentUpdate.Update.Text);
                            textStreamed = true;
                        }
                        break;

                    case ExecutorCompletedEvent complete:
                        if (textStreamed) { Console.WriteLine("\n"); textStreamed = false; }
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  ✓ [{complete.ExecutorId}] completed");
                        Console.ResetColor();
                        Console.WriteLine();
                        break;

                    case WorkflowOutputEvent output:
                        if (textStreamed) { Console.WriteLine(); textStreamed = false; }
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine("\n  ─────────────────────────────────────────────────────────────────────────");
                        Console.ResetColor();
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.WriteLine("  ✈  Final itinerary:\n");
                        Console.WriteLine(output.Data?.ToString() ?? "(no output)");
                        Console.ResetColor();
                        return;

                    case WorkflowErrorEvent error:
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n  ❌ Workflow error: {error.Exception?.Message}");
                        Console.ResetColor();
                        return;

                    case WorkflowWarningEvent warning:
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"\n  ⚠ Warning: {warning.Data}");
                        Console.ResetColor();
                        break;
                }
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
