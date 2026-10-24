// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.MAF;
using AgentEval.TravelDemo.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AgentEval.TravelDemo.Evals;

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
/// A run passes only when the harness's structural checks (executor order, expected tools, duration,
/// no errors) AND the judge's score pass. A run whose agent call, workflow or judge failed is not
/// measured: kept out of the statistics, counted, and unknown in the shared verdict (<see cref="RunClassifier"/>).
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

        Config.PrintTarget();
        Console.WriteLine();

        Console.WriteLine("  Building TripPlanner Workflow + ChatClientEvaluator...\n");

        // Judge on the same provider + model as the workflow's agents.
        var evaluatorClient = Config.CreateChatClient();
        var evaluator       = new ChatClientEvaluator(evaluatorClient);
        var harness         = new WorkflowEvaluationHarness(verbose: false);

        var input = TravelDemoPrompt.Canonical;

        Console.WriteLine($"  Criteria ({TravelEvalCriteria.Stochastic.Length} top-3 discriminating):");
        for (int i = 0; i < TravelEvalCriteria.Stochastic.Length; i++)
            Console.WriteLine($"    {i + 1}. {TravelEvalCriteria.Stochastic[i][..Math.Min(TravelEvalCriteria.Stochastic[i].Length, 70)]}...");
        Console.WriteLine();
        Console.WriteLine($"  Running {Runs} workflow evaluations...\n");
        Console.WriteLine("  ⏳ Each run ~90–180s — total ~8–15 minutes...\n");
        Console.WriteLine("  " + new string('─', 70));

        var outcomes = new List<RunOutcome>();

        for (int run = 1; run <= Runs; run++)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\n  ── Run {run}/{Runs} ──────────────────────────────────────");
            Console.ResetColor();

            var runStopwatch = System.Diagnostics.Stopwatch.StartNew();
            WorkflowTestResult? testResult = null;

            try
            {
                // ── Rebuild workflow for each run ──────────────────────────────
                using var timing = new MAFWorkflowInstrumentation();
                var (workflow, executorIds) = TripPlannerWorkflow.Create(builder => timing.Build(builder));

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

                testResult = await harness.RunWorkflowTestAsync(
                    workflowAdapter, testCase,
                    new WorkflowTestOptions { Timeout = TimeSpan.FromMinutes(5) });

                // A run that errored (the adapter records a failed agent call instead of throwing) or produced no
                // output is settled here, without the judge.
                var settled = RunClassifier.BeforeJudging(run, testResult, ex => Config.Redact(ex), Config.Redact);
                if (settled is not null)
                {
                    runStopwatch.Stop();
                    outcomes.Add(settled);
                    PrintRun(settled, testResult, runStopwatch.Elapsed);
                    continue;
                }

                // ── Judge with top-3 criteria ──────────────────────────────────
                var evalResult = await evaluator.EvaluateAsync(
                    input:    input,
                    output:   testResult.ExecutionResult!.FinalOutput,
                    criteria: TravelEvalCriteria.Stochastic);

                // The run passes only when the harness's structural checks (executor order, expected tools, …)
                // AND the judge's score pass — the score alone used to decide, so a failed structure showed ✅.
                var outcome = RunClassifier.AfterJudging(run, testResult, evalResult, PassingScore);
                runStopwatch.Stop();
                outcomes.Add(outcome);
                PrintRun(outcome, testResult, runStopwatch.Elapsed);

                // Show per-criterion summary for this run
                if (outcome.Score is not null && evalResult.CriteriaResults is { Count: > 0 } criteria)
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
                // The workflow harness threw: not measured — not a score of 0. The JUDGE threw: the run is checked like
                // a judged one — a failed structure is a measured FAIL (unscored), a sound one is not measured.
                runStopwatch.Stop();
                var outcome = testResult is null
                    ? RunOutcome.NotMeasured(run, $"the workflow run failed: {Config.Redact(ex)}")
                    : RunClassifier.JudgeThrew(run, testResult, Config.Redact(ex));
                outcomes.Add(outcome);
                PrintRun(outcome, testResult, runStopwatch.Elapsed);
            }
        }

        Console.WriteLine("\n  " + new string('─', 70) + "\n");

        // ── Print stochastic summary ─────────────────────────────────────────────
        var tally  = new StochasticTally(outcomes, Threshold);
        var scores = tally.Scores;

        EvalPrinter.PrintStochasticSummary(
            architecture: "Workflow (TripPlanner — 4 agents)",
            tally:        tally,
            passingScore: PassingScore,
            label:        "Eval 05 — Stochastic Workflow (5 runs · top-3 criteria)");

        // ── Verdict ──────────────────────────────────────────────────────────────
        // A run passes only when structure AND score passed; the library decides whether missing runs can change the verdict.
        Console.ForegroundColor = tally.Verdict switch
        {
            StochasticVerdict.Passed => ConsoleColor.Green,
            StochasticVerdict.Failed => ConsoleColor.Red,
            _                      => ConsoleColor.Yellow,
        };
        Console.WriteLine(tally.Verdict switch
        {
            StochasticVerdict.Passed =>
                $"  ✅ Workflow meets {Threshold * 100:F0}% success-rate threshold  ({tally.PassedCount}/{tally.Total} runs passed structure + score)",
            StochasticVerdict.Failed =>
                $"  ❌ Workflow below {Threshold * 100:F0}% threshold ({tally.PassedCount}/{tally.Measured} measured runs passed structure + score)",
            StochasticVerdict.Undecided =>
                $"  ⚠️  INCOMPLETE — {tally.Errored} of {tally.Total} runs not measured; {tally.PassedCount}/{tally.Measured} measured runs passed. No PASS.",
            _ =>
                "  ⚠️  NOT MEASURED — no run produced a verdict.",
        });
        Console.ResetColor();
        int structuralFailures = outcomes.Count(o => o.Measured && o.StructureFailed);
        if (structuralFailures > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"     {structuralFailures} measured run(s) failed the harness's structural checks, whatever their score.");
            Console.ResetColor();
        }
        if (tally.Unscored > 0)
        {
            // A run with no output (or a failed structure the judge gave no verdict on) is a FAILED run — counted above —
            // with no score: the spread below covers the other runs only, so it cannot speak for consistency.
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"     {tally.Unscored} measured run(s) had no judge score: counted as FAILED in the pass rate; " +
                              $"the spread covers {scores.Count} of {tally.Total} runs, so Eval 3 gives no consistency verdict.");
            Console.ResetColor();
        }

        if (scores.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\n  💡 Score range {scores.Min()}–{scores.Max()} (spread = {scores.Max() - scores.Min()}, " +
                              $"over {scores.Count} of {tally.Total} runs — judged runs only)");
            Console.WriteLine($"     Compare with Eval 04 single-agent spread to evaluate the hypothesis.");
            Console.ResetColor();
        }
        // ── Persist stochastic snapshot for Eval03 comparison ──────────────────
        if (scores.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n  ⚠️  Snapshot NOT saved — no run produced a score; Eval 3 keeps the previous one.");
            Console.ResetColor();
            return;
        }
        EvalResultStore.SaveStochastic("eval05_stochastic_workflow", new StochasticSnapshot
        {
            Architecture = "Workflow (TripPlanner — 4 agents)",
            Label        = "Eval 05 — Stochastic Workflow (5 runs · top-3 criteria)",
            ProviderTag  = EvalResultStore.CurrentProviderTag,   // provenance: recorded even when the console hides it
            Model        = EvalResultStore.CurrentModel,
            Runs         = tally.Total,
            ErroredRuns  = tally.Errored,
            UnscoredRuns = tally.Unscored,   // failed runs with no score: Eval 3 withholds the consistency verdict
            PassRate     = tally.PassRate,
            Passed       = tally.Verdict == StochasticVerdict.Passed,
            PassedCount  = tally.PassedCount,
            MinScore     = scores.Min(),
            MaxScore     = scores.Max(),
            MeanScore    = scores.Average(),
            Scores       = [.. scores]
        });
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  📁 Stochastic snapshot saved → {EvalResultStore.StorageLocation}");
        if (tally.Errored > 0)
            Console.WriteLine($"     It records {tally.Errored} unmeasured run(s); Eval 3 gives no verdict on it.");
        Console.WriteLine($"     Run Eval 3 to compare spread with Eval 04 (single agent).");
        Console.ResetColor();
    }

    /// <summary>One run's line: ✅ only when structure AND score passed; the failed checks listed; never a 0 for an error.</summary>
    private static void PrintRun(RunOutcome outcome, WorkflowTestResult? testResult, TimeSpan elapsed)
    {
        if (!outcome.Measured)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠️  Run {outcome.Run}/{Runs} — NOT MEASURED: {EvalPrinter.OneLine(outcome.NotMeasuredReason)}  " +
                              $"(elapsed: {elapsed.TotalSeconds:F0}s)");
            Console.ResetColor();
            return;
        }

        var score = outcome.Score is { } s ? $"{s,3}/100" : "  —";
        Console.ForegroundColor = outcome.Passed ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {(outcome.Passed ? "✅" : "❌")} Run {outcome.Run}/{Runs} — score: {score}  " +
                          $"(elapsed: {elapsed.TotalSeconds:F0}s){(outcome.FailureReason is { } why ? $"  — {why}" : "")}");
        // Each failed harness check's own message (executor ids, tool names) — redacted like every printed message.
        foreach (var failed in testResult?.AssertionResults?.Where(a => !a.Passed) ?? [])
            Console.WriteLine($"       • {failed.AssertionName}: {EvalPrinter.OneLine(Config.Redact(failed.FailureMessage))}");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        SampleOutcome.Skip("Eval 05: no model configured.");
        Config.PrintMissingProvider("Eval 05");
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
