// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using ECS2026MAF.Agents;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace ECS2026MAF.Evals;

/// <summary>
/// Eval 01 — TravelAgent Full-Trip Assertions (Hypothesis Test)
///
/// Given the SAME request as Eval 02, we assert that the single all-in-one
/// TravelAgent calls every expected tool in the correct sequence:
///   GetInfoAbout → SearchFlights → BookFlight
///                → SearchHotel  → BookHotel → SendConfirmation
///
/// Hypothesis: a single agent is LESS deterministic than a structured workflow.
/// If these assertions ever fail on a given run, the hypothesis is confirmed.
///
/// Agents and tools are imported from ECS2026MAF — no duplication.
/// </summary>
public static class Eval01_TravelAgentEvals
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!Config.IsConfigured)
        {
            PrintMissingCredentials();
            return;
        }

        Console.WriteLine("  Creating TravelAgent + AgentEval harness...\n");

        // ── Agent from ECS2026MAF (no duplication) ────────────────────────────
        var rawAgent = TravelAgentFactory.Create();
        var agent    = new MAFAgentAdapter(rawAgent);

        // ── Evaluator LLM client for LLM-as-a-judge ──────────────────────────
        var azureClient    = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);
        var evaluatorClient = azureClient.GetChatClient(Config.Model).AsIChatClient();

        var harness = new MAFEvaluationHarness(evaluatorClient, verbose: false);

        // ── Test case (same input as Eval 02 / Demo 02) ———————————————————
        var testCase = new TestCase
        {
            Name  = "TravelAgent — Full trip (Tokyo + Cologne)",
            Input = "Plan a 7-day trip visiting both Tokyo and Cologne. " +
                    "I need city information, flights between them, " +
                    "and hotel bookings for each city. " +
                    "Please send a trip summary to traveller@example.com.",
            ExpectedTools          = ["GetInfoAbout", "SearchFlights", "SearchHotel",
                                      "BookFlight", "BookHotel", "SendConfirmation"],
            ExpectedOutputContains = "Tokyo",
            EvaluationCriteria = [..TravelEvalCriteria.Eval01],
            PassingScore = 70
        };

        Console.WriteLine($"  Running: \"{testCase.Name}\"\n");
        Console.WriteLine("  ⏳ This may take ~30 seconds (single agent, multiple tool calls)...\n");

        var result = await harness.RunEvaluationStreamingAsync(
            agent,
            testCase,
            options: new EvaluationOptions
            {
                TrackTools       = true,
                EvaluateResponse = true,
                ModelName        = Config.Model
            });

        // ── Print rich ASCII summary ─────────────────────────────────────────────
        EvalPrinter.PrintAgentResult(
            result,
            testCase.ExpectedTools ?? [],
            label: "Eval 01 — TravelAgent (single agent, hypothesis test)");

        // ── LLM-as-judge criterion results ────────────────────────────────────
        EvalPrinter.PrintLlmJudge(
            result.Score,
            result.CriteriaResults,
            result.Suggestions,
            label: "Eval 01 — LLM Quality Gate (9 criteria)");

        // ── Fluent tool assertions (order + completeness) ─────────────────────
        try
        {
            result.ToolUsage!.Should()
                // Research must come first
                .HaveCalledTool("GetInfoAbout",
                    because: "agent must research both cities before planning")
                    .BeforeTool("SearchFlights",
                        because: "destination research should precede flight search")
                .And()
                // Flights: search → (user confirm) → book
                .HaveCalledTool("SearchFlights",
                    because: "user asked for flights between Tokyo and Cologne")
                    .BeforeTool("BookFlight",
                        because: "must search available options before booking")
                .And()
                .HaveCalledTool("BookFlight",
                    because: "user explicitly requested flight bookings")
                .And()
                // Hotels: search → (user confirm) → book
                .HaveCalledTool("SearchHotel",
                    because: "user asked for hotel bookings in each city")
                    .BeforeTool("BookHotel",
                        because: "must search available options before booking")
                .And()
                .HaveCalledTool("BookHotel",
                    because: "user explicitly requested hotel bookings for each city")
                .And()
                // Email confirmation
                .HaveCalledTool("SendConfirmation",
                    because: "user asked for trip summary sent to traveller@example.com")
                .And()
                // Policy: no cancellations were requested
                .NeverCallTool("CancelFlightReservation",
                    because: "user did not request any cancellation")
                .NeverCallTool("CancelHotelBooking",
                    because: "user did not request any cancellation");

            PrintPass("Tool order & completeness assertions PASSED!");

            result.ActualOutput!.Should()
                .Contain("Tokyo",   because: "response must cover the first destination")
                .Contain("Cologne", because: "response must cover the second destination")
                .HaveLengthBetween(500, 50_000,
                    because: "a full 7-day trip plan with bookings should be substantial");

            PrintPass("Response content assertions PASSED!");

            // ── Count-based assertions (inline — help confirm hypothesis) ─────────
            var bookFlightCount   = result.ToolUsage?.Calls.Count(c => c.Name == "BookFlight")          ?? 0;
            var confirmCount      = result.ToolUsage?.Calls.Count(c => c.Name == "GetUserConfirmation") ?? 0;

            if (bookFlightCount < 2)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    $"\n  ⚠️  HYPOTHESIS SIGNAL — BookFlight called {bookFlightCount} time(s); " +
                    $"expected ≥ 2 for a 2-city trip.\n" +
                    $"     The workflow enforces this structurally — single agent can miss a leg.");
                Console.ResetColor();
            }
            else
            {
                PrintPass($"BookFlight called {bookFlightCount} time(s) — at least 2 legs booked ✓");
            }

            if (confirmCount < 4)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    $"\n  ⚠️  Policy gap — GetUserConfirmation called {confirmCount} time(s); " +
                    $"expected ≥ 4 (2 flights + 2 hotels).\n");
                Console.ResetColor();
            }
            else
            {
                PrintPass($"GetUserConfirmation called {confirmCount} time(s) — all bookings confirmed ✓");
            }
        }
        catch (ToolAssertionException ex)
        {
            // A ToolAssertionException here confirms the hypothesis:
            // the single all-in-one agent skipped or re-ordered steps that
            // the workflow enforces structurally.
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"\n  ⚠️  HYPOTHESIS CONFIRMED — single agent was non-deterministic.\n" +
                $"     Assertion: {ex.Message}\n");
            Console.ResetColor();
        }
        catch (ResponseAssertionException ex)
        {
            PrintFail($"Response assertion failed: {ex.Message}");
        }

        // ── Deterministic criteria score + snapshot (for Eval03 comparison) ──────
        int criteriaTotal = result.CriteriaResults?.Count ?? 0;
        int criteriaMetCount = result.CriteriaResults?.Count(c => c.Met) ?? 0;
        int criteriaScore = criteriaTotal > 0 ? criteriaMetCount * 100 / criteriaTotal : 0;
        int bookFlightCountFinal = result.ToolUsage?.Calls.Count(c => c.Name == "BookFlight") ?? 0;

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n  Criteria score (discrete): {criteriaScore}/100  " +
                          $"({criteriaMetCount}/{criteriaTotal} criteria met)");
        Console.ResetColor();

        EvalResultStore.Save("eval01_agent", new EvalSnapshot
        {
            Architecture    = "Single Agent",
            Label           = "Eval 01 — TravelAgent (Alex)",
            LlmScore        = result.Score,
            CriteriaScore   = criteriaScore,
            CriteriaMetCount = criteriaMetCount,
            CriteriaTotal   = criteriaTotal,
            ToolCallCount   = result.ToolCallCount,
            BookFlightCount = bookFlightCountFinal,
            Passed          = result.Passed,
            DurationMs      = (long)(result.Performance?.TotalDuration.TotalMilliseconds ?? 0),
            CriteriaDetails = result.CriteriaResults?
                .Select(c => new CriterionSnapshot(c.Criterion[..Math.Min(c.Criterion.Length, 80)], c.Met, c.Explanation))
                .ToList() ?? []
        });

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  📁 Snapshot saved — run Eval 3 to compare with workflow.");
        Console.ResetColor();
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 01 — TravelAgent (single agent, hypothesis test)                       ║
║   Same task as Eval 02 · All tools asserted · Order matters                  ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
  ⚠️  Skipping Eval 01 — Azure OpenAI credentials required.
");
        Console.ResetColor();
    }

    private static void PrintPass(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  ✅ {message}");
        Console.ResetColor();
    }

    private static void PrintFail(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  ❌ {message}");
        Console.ResetColor();
    }
}
