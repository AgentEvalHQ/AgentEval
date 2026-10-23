// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Assertions;
using AgentEval.MAF;
using AgentEval.TravelDemo.Agents;
using Microsoft.Extensions.AI;

namespace AgentEval.TravelDemo.Evals;

/// <summary>
/// Eval 04 — Stochastic TravelAgent (single agent, 5 runs)
///
/// Runs the single all-in-one TravelAgent 5 times against the same input,
/// evaluating each run with the top-3 most discriminating criteria:
///   1. Both flight legs booked
///   2. Hotel completeness (both cities + confirmation codes)
///   3. Date coherence (no hotel overlap, ~7 days)
///
/// Shows min/mean/max score, pass rate, and per-run strip so you can see
/// how much the output VARIES across runs — the key measure of determinism.
///
/// A highly variable score band (e.g. 33–100) confirms the hypothesis.
/// A tight band (e.g. 90–100) would disprove it.
///
/// A run whose model call or judge failed is NOT a score of 0: it is counted as not measured,
/// kept out of the statistics, and rules out a clean PASS (<see cref="StochasticTally"/>).
///
/// ⏱️ Runtime: ~2–4 minutes (5 × ~30 s agent runs + 5 LLM judge calls)
/// 💰 Cost: 5 × agent calls + 5 × small judge calls (~$0.05–$0.20)
/// </summary>
public static class Eval04_StochasticAgent
{
    private const int Runs      = 5;
    private const double Threshold = 0.60;  // 60% — we expect the single agent to sometimes fail
    private const int PassingScore = 60;

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

        Console.WriteLine("  Creating TravelAgent + AgentEval stochastic runner...\n");

        // Judge on the same provider + model as the agent.
        var evaluatorClient = Config.CreateChatClient();
        var harness         = new MAFEvaluationHarness(evaluatorClient, verbose: false);

        var evalOptions = new EvaluationOptions
        {
            TrackTools       = true,
            EvaluateResponse = true,
            ModelName        = Config.Model
        };
        // The options reach every run: without them the runner used its defaults and the model never reached the results.
        var runner = new StochasticRunner(harness, evaluationOptions: evalOptions);

        var testCase = new TestCase
        {
            Name  = "TravelAgent — Stochastic (top-3 criteria)",
            Input = TravelDemoPrompt.Canonical,
            EvaluationCriteria = [..TravelEvalCriteria.Stochastic],
            PassingScore = PassingScore
        };

        var options = new StochasticOptions(
            Runs:                   Runs,
            SuccessRateThreshold:   Threshold,
            OnProgress:             p => PrintProgress(p));

        Console.WriteLine($"  Running {Runs} evaluations of the SINGLE AGENT...\n");
        Console.WriteLine($"  Criteria ({TravelEvalCriteria.Stochastic.Length} top-3 discriminating):");
        for (int i = 0; i < TravelEvalCriteria.Stochastic.Length; i++)
            Console.WriteLine($"    {i + 1}. {TravelEvalCriteria.Stochastic[i][..Math.Min(TravelEvalCriteria.Stochastic[i].Length, 70)]}...");
        Console.WriteLine();
        Console.WriteLine("  ⏳ Each run ~30–60s — total ~3–5 minutes...\n");
        Console.WriteLine("  " + new string('─', 70));

        // Create a fresh agent for each run via factory pattern
        var agentFactory = new TravelAgentEvalFactory();

        StochasticResult stochResult;
        try
        {
            stochResult = await runner.RunStochasticTestAsync(
                agentFactory,
                testCase,
                options);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  ❌ Stochastic run failed — NOT measured (no verdict, no snapshot): {Config.Redact(ex)}");
            SampleOutcome.Skip("Eval 04: stochastic run failed without a measured verdict.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("\n  " + new string('─', 70) + "\n");

        // ── Errored runs are NOT scores ──────────────────────────────────────────
        // The harness turns a failed model call (or a judge with no verdict) into a placeholder Score 0. The runner's
        // statistics leave such runs out (StochasticResult.NotMeasuredCount), and so does the tally, which also keeps
        // this demo's stricter rule: any unmeasured run rules out a clean PASS (the core rule passes when the measured
        // passes alone reach the threshold).
        var tally = new StochasticTally(
            [.. stochResult.IndividualResults.Select((r, i) => RunClassifier.FromAgentRun(i + 1, r, PassingScore, ex => Config.Redact(ex)))],
            Threshold);
        var scores = tally.Scores;

        EvalPrinter.PrintStochasticSummary(
            architecture: "Single Agent (TravelAgent)",
            tally:        tally,
            passingScore: PassingScore,
            label:        "Eval 04 — Stochastic TravelAgent (5 runs · top-3 criteria)");

        // ── StochasticAssertions ─────────────────────────────────────────────────
        if (tally.Errored == 0)
        {
            // Every run measured: the library's pass rate and the tally's are the same number.
            try
            {
                stochResult.Should()
                    .HavePassRateAtLeast(Threshold,
                        because: $"at least {Threshold * 100:F0}% of runs should meet the 3 key criteria");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✅ Stochastic assertion PASSED — pass rate {tally.PassRate * 100:F1}%");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    $"\n  ⚠️  HYPOTHESIS SIGNAL — single agent fell below {Threshold * 100:F0}% pass rate.\n" +
                    $"     {Config.Redact(ex)}\n" +
                    $"     Compare with Eval 05 (workflow) to see if workflow is more consistent.");
                Console.ResetColor();
            }
        }
        else
        {
            // This demo gives no PASS and no hypothesis call while a run was not measured, so the library's assertion is
            // not run (it would pass when the measured passes alone reach the threshold).
            Console.ForegroundColor = tally.Verdict == StochasticVerdict.Fail ? ConsoleColor.Red : ConsoleColor.Yellow;
            Console.WriteLine(tally.Verdict switch
            {
                StochasticVerdict.Fail =>
                    $"  ❌ FAILED — {tally.PassedCount}/{tally.Measured} measured runs passed; even if the {tally.Errored} unmeasured " +
                    $"run(s) had passed, the pass rate would miss {Threshold * 100:F0}%.",
                StochasticVerdict.NotMeasured =>
                    $"  ⚠️  NOT MEASURED — every run failed on the model call or the judge; no verdict.",
                _ =>
                    $"  ⚠️  INCOMPLETE — {tally.Errored} of {tally.Total} runs were not measured; no PASS and no hypothesis signal from this run.",
            });
            Console.WriteLine("     The stochastic assertion was not run: this demo gives no PASS while a run was not measured.");
            Console.ResetColor();
        }

        if (scores.Count > 0)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  💡 Score range {scores.Min()}–{scores.Max()} (spread = {scores.Max() - scores.Min()}, measured runs only)");
            Console.WriteLine($"     A large spread means high variance — typical of a single unconstrained agent.");
            Console.WriteLine($"     Run Eval 05 to see the workflow's spread for comparison.");
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
        EvalResultStore.SaveStochastic("eval04_stochastic_agent", new StochasticSnapshot
        {
            Architecture = "Single Agent (TravelAgent)",
            Label        = "Eval 04 — Stochastic TravelAgent (5 runs · top-3 criteria)",
            ProviderTag  = EvalResultStore.CurrentProviderTag,   // provenance: recorded even when the console hides it
            Model        = EvalResultStore.CurrentModel,
            Runs         = tally.Total,
            ErroredRuns  = tally.Errored,
            UnscoredRuns = tally.Unscored,   // 0 here: the judge grades every finished agent run, empty answers included
            PassRate     = tally.PassRate,
            Passed       = tally.Verdict == StochasticVerdict.Pass,
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
        Console.WriteLine($"     Run Eval 3 to compare spread with Eval 05 (workflow).");
        Console.ResetColor();
    }

    private static void PrintProgress(StochasticProgress p)
    {
        var elapsed = p.Elapsed.TotalSeconds;
        var eta     = p.EstimatedRemaining.HasValue
            ? $"  ~{p.EstimatedRemaining.Value.TotalSeconds:F0}s remaining"
            : "";
        if (p.LastResult is null) return;

        var outcome = RunClassifier.FromAgentRun(p.CurrentRun, p.LastResult, PassingScore, ex => Config.Redact(ex));
        if (!outcome.Measured)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠️  Run {p.CurrentRun}/{p.TotalRuns} — NOT MEASURED: {EvalPrinter.OneLine(outcome.NotMeasuredReason)}  " +
                              $"(elapsed: {elapsed:F0}s{eta})");
            Console.ResetColor();
            return;
        }
        Console.ForegroundColor = outcome.Passed ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {(outcome.Passed ? "✅" : "❌")} Run {p.CurrentRun}/{p.TotalRuns} — score: {outcome.Score,3}/100  " +
                          $"(elapsed: {elapsed:F0}s{eta})");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        SampleOutcome.Skip("Eval 04: no model configured.");
        Config.PrintMissingProvider("Eval 04");
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 04 — Stochastic TravelAgent  (5 runs · top-3 criteria)               ║
║   Measures variance: how much does the single agent vary across runs?        ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}

/// <summary>Factory that creates a fresh MAFAgentAdapter(TravelAgent) for each stochastic run.</summary>
internal sealed class TravelAgentEvalFactory : IAgentFactory
{
    public string ModelId   => Config.Model;
    public string ModelName => "TravelAgent";
    public ModelConfiguration? Configuration => null;
    public IEvaluableAgent CreateAgent() => new MAFAgentAdapter(TravelAgentFactory.Create());
}
