// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.MAF;
using Azure.AI.OpenAI;
using ECS2026MAF.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace ECS2026MAF.Evals;

/// <summary>
/// Eval 05 — Stochastic TripPlanner Workflow (5 runs)
///
/// Runs the 4-agent TripPlanner Workflow 5 times, evaluating each run with
/// the same top-3 discriminating criteria used in Eval 04.
///
/// Because <see cref="StochasticRunner"/> only supports <see cref="IEvaluableAgent"/>,
/// the N-run loop is implemented manually here and statistics are computed locally.
/// This is intentional — the workflow harness API is different and we want to keep
/// the comparison apples-to-apples (same criteria, same judge prompt).
///
/// Compare the score spread with Eval 04 to test the hypothesis:
///   Workflow spread should be NARROWER than single-agent spread.
///
/// ⏱️ Runtime: ~8–15 minutes (5 × ~90–180s workflow runs + 5 judge calls)
/// 💰 Cost: 5 × 4-agent workflow calls + 5 × small judge calls (~$0.15–$0.50)
/// </summary>
public static class Eval05_StochasticWorkflow
{
    private const int    Runs      = 5;
    private const double Threshold = 0.80;  // 80% — workflow should be more consistent
    private const int    PassingScore = 60;

    public static async Task RunAsync()
    {
        PrintHeader();

        if (!Config.IsConfigured)
        {
            PrintMissingCredentials();
            return;
        }

        Console.WriteLine("  Building TripPlanner Workflow + ChatClientEvaluator...\n");

        var azureClient     = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);
        var evaluatorClient = azureClient.GetChatClient(Config.Model).AsIChatClient();
        var evaluator       = new ChatClientEvaluator(evaluatorClient);
        var harness         = new WorkflowEvaluationHarness(verbose: false);

        var input = "Plan a 7-day trip visiting both Tokyo and Cologne. " +
                    "I need city information, flights between them, " +
                    "and hotel bookings for each city.";

        Console.WriteLine($"  Criteria ({TravelEvalCriteria.Stochastic.Length} top-3 discriminating):");
        for (int i = 0; i < TravelEvalCriteria.Stochastic.Length; i++)
            Console.WriteLine($"    {i + 1}. {TravelEvalCriteria.Stochastic[i][..Math.Min(TravelEvalCriteria.Stochastic[i].Length, 70)]}...");
        Console.WriteLine();
        Console.WriteLine($"  Running {Runs} workflow evaluations...\n");
        Console.WriteLine("  ⏳ Each run ~90–180s — total ~8–15 minutes...\n");
        Console.WriteLine("  " + new string('─', 70));

        var scores     = new List<int>();
        int passedCount = 0;

        for (int run = 1; run <= Runs; run++)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\n  ── Run {run}/{Runs} ──────────────────────────────────────");
            Console.ResetColor();

            var runStopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // ── Rebuild workflow for each run ──────────────────────────────
                var (workflow, executorIds) = TripPlannerWorkflow.Create();

                var workflowAdapter = MAFWorkflowAdapter.FromMAFWorkflow(
                    workflow,
                    name: "TripPlanner",
                    executorIds: executorIds,
                    workflowType: "PromptChaining");

                var testCase = new WorkflowTestCase
                {
                    Name  = $"TripPlanner — Stochastic run {run}",
                    Input = input,
                    ExpectedExecutors   = ["TripPlanner", "FlightReservation", "HotelReservation", "Presenter"],
                    StrictExecutorOrder = true,
                    MaxDuration         = TimeSpan.FromMinutes(5),
                    ExpectedTools       = ["GetInfoAbout", "SearchFlights", "BookFlight",
                                           "SearchHotel", "BookHotel"]
                };

                var testResult = await harness.RunWorkflowTestAsync(
                    workflowAdapter, testCase,
                    new WorkflowTestOptions { Timeout = TimeSpan.FromMinutes(5) });

                if (testResult.ExecutionResult is not { } execResult || string.IsNullOrWhiteSpace(execResult.FinalOutput))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ❌ Run {run} — workflow produced no output");
                    Console.ResetColor();
                    scores.Add(0);
                    continue;
                }

                // ── Judge with top-3 criteria ──────────────────────────────────
                var evalResult = await evaluator.EvaluateAsync(
                    input:    input,
                    output:   execResult.FinalOutput,
                    criteria: TravelEvalCriteria.Stochastic);

                int score = evalResult.OverallScore;
                bool passed = score >= PassingScore;
                if (passed) passedCount++;
                scores.Add(score);

                runStopwatch.Stop();
                var icon = passed ? "✅" : "❌";
                Console.ForegroundColor = passed ? ConsoleColor.Green : ConsoleColor.Red;
                Console.WriteLine($"  {icon} Run {run}/{Runs} — score: {score,3}/100  " +
                                  $"(elapsed: {runStopwatch.Elapsed.TotalSeconds:F0}s)");
                Console.ResetColor();

                // Show per-criterion summary for this run
                if (evalResult.CriteriaResults is { Count: > 0 } criteria)
                {
                    foreach (var cr in criteria)
                    {
                        Console.ForegroundColor = cr.Met ? ConsoleColor.DarkGreen : ConsoleColor.DarkRed;
                        var shortCrit = cr.Criterion.Length > 60
                            ? cr.Criterion[..57] + "…"
                            : cr.Criterion;
                        Console.WriteLine($"       {(cr.Met ? "✅" : "❌")} {shortCrit}");
                    }
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                runStopwatch.Stop();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ❌ Run {run} failed: {ex.Message}");
                Console.ResetColor();
                scores.Add(0);
            }
        }

        Console.WriteLine("\n  " + new string('─', 70) + "\n");

        // ── Print stochastic summary ─────────────────────────────────────────────
        double passRate = Runs > 0 ? (double)passedCount / Runs : 0;

        EvalPrinter.PrintStochasticSummary(
            architecture:  "Workflow (TripPlanner — 4 agents)",
            runs:          Runs,
            scores:        scores,
            passRate:      passRate,
            overallPassed: passRate >= Threshold,
            label:         "Eval 05 — Stochastic Workflow (5 runs · top-3 criteria)");

        // ── Verdict ──────────────────────────────────────────────────────────────
        bool overallPassed = passRate >= Threshold;
        Console.ForegroundColor = overallPassed ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine(overallPassed
            ? $"  ✅ Workflow meets {Threshold * 100:F0}% consistency threshold"
              + $"  ({passedCount}/{Runs} runs passed)"
            : $"  ⚠️  Workflow below {Threshold * 100:F0}% threshold "
              + $"({passedCount}/{Runs} runs passed)");
        Console.ResetColor();

        if (scores.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\n  💡 Score range {scores.Min()}–{scores.Max()} (spread = {scores.Max() - scores.Min()})");
            Console.WriteLine($"     Compare with Eval 04 single-agent spread to evaluate the hypothesis.");
            Console.ResetColor();
        }
        // ── Persist stochastic snapshot for Eval03 comparison ──────────────────
        if (scores.Count > 0)
        {
            double mean = scores.Average();
            EvalResultStore.SaveStochastic("eval05_stochastic_workflow", new StochasticSnapshot
            {
                Architecture = "Workflow (TripPlanner — 4 agents)",
                Label        = "Eval 05 — Stochastic Workflow (5 runs · top-3 criteria)",
                Runs         = Runs,
                PassRate     = passRate,
                Passed       = overallPassed,
                PassedCount  = passedCount,
                MinScore     = scores.Min(),
                MaxScore     = scores.Max(),
                MeanScore    = mean,
                Scores       = scores
            });
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  📁 Stochastic snapshot saved → {EvalResultStore.StorageLocation}");
            Console.WriteLine($"     Run Eval 3 to compare spread with Eval 04 (single agent).");
            Console.ResetColor();
        }    }

    private static void PrintMissingCredentials()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
  ⚠️  Skipping Eval 05 — Azure OpenAI credentials required.
");
        Console.ResetColor();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 05 — Stochastic Workflow  (5 runs · top-3 criteria)                  ║
║   Measures variance: how consistently does the workflow perform?             ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}
