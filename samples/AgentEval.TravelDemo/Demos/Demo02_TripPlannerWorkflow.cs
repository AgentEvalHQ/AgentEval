// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using System.Text;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using AgentEval.TravelDemo.Workflows;

namespace AgentEval.TravelDemo.Demos;

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

        Config.PrintTarget();
        Console.WriteLine();

        // ── 1. Build workflow ─────────────────────────────────────────────────
        Console.WriteLine("  Building TripPlanner workflow...\n");
        var (workflow, executorIds) = TripPlannerWorkflow.Create();

        Console.WriteLine($"  Name      : {workflow.Name}");
        Console.WriteLine($"  Pipeline  : {string.Join(" → ", executorIds)}");
        if (Config.ShowModel)
            Console.WriteLine($"  Model     : {Config.Model}");
        Console.WriteLine();

        // ── 2. Define the request ─────────────────────────────────────────────
        var request = TravelDemoPrompt.Canonical;

        Console.WriteLine($"  Request: \"{request}\"\n");
        Console.WriteLine("  ⏳ Executing workflow — this may take a minute...\n");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();

        // ── 3. Run workflow via MAF InProcessExecution ────────────────────────
        // The last agent's turn IS the itinerary. MAF names an agent executor "<agent name>_<id>".
        var outputExecutor = executorIds[^1];
        try
        {
            // MAF ChatProtocol requires a ChatMessage input + explicit TurnToken
            await using var run = await InProcessExecution
                .RunStreamingAsync(workflow, new ChatMessage(ChatRole.User, request));

            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            // Stream all workflow events in real-time as they arrive
            bool textStreamed = false;
            string? turnExecutor = null;                       // whose turn is running
            var turnText = new StringBuilder();                // that turn's streamed text
            string? lastTurnText = null;                       // the most recent finished turn's text
            string? itinerary = null;                          // the output executor's finished turn
            string? nonStreamedOutput = null;                  // a plain WorkflowOutputEvent, if one ever arrives
            int progressChars = 0;
            bool agentFailed = false;

            await foreach (WorkflowEvent evt in run.WatchStreamAsync())
            {
                switch (evt)
                {
                    case WorkflowStartedEvent:
                        Console.ForegroundColor = ConsoleColor.DarkCyan;
                        Console.WriteLine("  ▶ Workflow started\n");
                        Console.ResetColor();
                        break;

                    // MAF delivers 2–3 messages to each agent executor (its input or the forwarded history, the
                    // previous agent's reply, then a TurnToken) and raises ExecutorInvokedEvent for every one of
                    // them; only the TurnToken runs the agent. So each agent's turn is shown once.
                    case ExecutorInvokedEvent invoke when invoke.Data is TurnToken:
                        if (textStreamed) { Console.WriteLine("\n"); textStreamed = false; }
                        turnExecutor = invoke.ExecutorId;
                        turnText.Clear();
                        progressChars = 0;
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine(IsExecutor(invoke.ExecutorId, outputExecutor)
                            ? $"  🤖 [{ShortId(invoke.ExecutorId)}] writing the itinerary..."
                            : $"  🤖 [{ShortId(invoke.ExecutorId)}] starting...");
                        Console.ResetColor();
                        break;

                    case ExecutorInvokedEvent:
                        break;  // a message delivery that only queues input for the agent's turn

                    // MAF 1.24: AgentResponseUpdateEvent derives from WorkflowOutputEvent, so it must be matched
                    // BEFORE the WorkflowOutputEvent case — the old order made that case (the itinerary) unreachable.
                    case AgentResponseUpdateEvent agentUpdate:
                        if (!string.IsNullOrEmpty(agentUpdate.Update.Text))
                        {
                            turnText.Append(agentUpdate.Update.Text);
                            if (IsExecutor(turnExecutor, outputExecutor))
                            {
                                // The itinerary prints once, in full, after the run — a dot per chunk shows progress.
                                progressChars += agentUpdate.Update.Text.Length;
                                for (; progressChars >= 80; progressChars -= 80) Console.Write('.');
                            }
                            else
                            {
                                Console.Write(agentUpdate.Update.Text);
                            }
                            textStreamed = true;
                        }
                        break;

                    case ExecutorCompletedEvent complete when complete.ExecutorId == turnExecutor:
                        if (textStreamed) { Console.WriteLine("\n"); textStreamed = false; }
                        lastTurnText = turnText.ToString();
                        if (IsExecutor(complete.ExecutorId, outputExecutor)) itinerary = lastTurnText;
                        turnExecutor = null;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  ✓ [{ShortId(complete.ExecutorId)}] completed");
                        Console.ResetColor();
                        Console.WriteLine();
                        break;

                    case ExecutorCompletedEvent:
                        break;  // the end of a message delivery, not of a turn

                    case ExecutorFailedEvent failure:
                        agentFailed = true;
                        if (textStreamed) { Console.WriteLine("\n"); textStreamed = false; }
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ❌ [{ShortId(failure.ExecutorId)}] failed: {Config.Redact(failure.Data as Exception)}");
                        Console.ResetColor();
                        break;

                    case WorkflowOutputEvent output:
                        // A non-streamed output (an agent bound with EmitAgentResponseEvents). Not raised by this
                        // workflow on MAF 1.24, which streams; kept so the itinerary still prints if that changes.
                        nonStreamedOutput = output.Data?.ToString();
                        break;

                    case WorkflowErrorEvent error:
                        if (textStreamed) { Console.WriteLine(); textStreamed = false; }
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n  ❌ Workflow error: {Config.Redact(error.Exception)}");
                        Console.ResetColor();
                        return;

                    case WorkflowWarningEvent warning:
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"\n  ⚠ Warning: {Config.Redact(warning.Data?.ToString())}");
                        Console.ResetColor();
                        break;
                }
            }

            if (textStreamed) Console.WriteLine();
            if (agentFailed)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n  ❌ No itinerary — an agent failed (see above).");
                Console.ResetColor();
                return;
            }
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("\n  ─────────────────────────────────────────────────────────────────────────");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  ✈  Final itinerary:\n");
            var finalText = nonStreamedOutput ?? itinerary ?? lastTurnText;
            Console.WriteLine(string.IsNullOrWhiteSpace(finalText) ? "(no output)" : finalText);
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  ❌ Workflow failed: {Config.Redact(ex)}");
            Console.ResetColor();
            Console.WriteLine(Config.ShowModel
                ? $"\n  💡 Ensure the configured model ({Config.Model} on {Config.ProviderName}) supports tool calling (function calling)."
                : "\n  💡 Ensure the configured model supports tool calling (function calling).");
            return;
        }
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    /// <summary>True when a MAF executor id (<c>"Presenter_3f2a…"</c>) belongs to the agent named <paramref name="name"/>.</summary>
    private static bool IsExecutor(string? executorId, string name) =>
        executorId is not null
        && (executorId == name || executorId.StartsWith(name + "_", StringComparison.Ordinal));

    /// <summary>The agent name without MAF's per-instance suffix, for the console.</summary>
    private static string ShortId(string executorId) =>
        executorId.IndexOf('_') is var i and > 0 ? executorId[..i] : executorId;

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

    private static void PrintMissingCredentials() => Config.PrintMissingProvider("Demo 02");
}
