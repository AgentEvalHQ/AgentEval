// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Assertions;
using AgentEval.MAF;
using Azure.AI.OpenAI;
using ECS2026MAF.Agents;
using Microsoft.Extensions.AI;

namespace ECS2026MAF.Evals;

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
/// ⏱️ Runtime: ~2–4 minutes (5 × ~30 s agent runs + 5 LLM judge calls)
/// 💰 Cost: 5 × agent calls + 5 × small judge calls (~$0.05–$0.20)
/// </summary>
public static class Eval04_StochasticAgent
{
    private const int Runs      = 5;
    private const double Threshold = 0.60;  // 60% — we expect the single agent to sometimes fail

    public static async Task RunAsync()
    {
        PrintHeader();

        if (!Config.IsConfigured)
        {
            PrintMissingCredentials();
            return;
        }

        Console.WriteLine("  Creating TravelAgent + AgentEval stochastic runner...\n");

        var azureClient     = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);
        var evaluatorClient = azureClient.GetChatClient(Config.Model).AsIChatClient();
        var harness         = new MAFEvaluationHarness(evaluatorClient, verbose: false);
        var runner          = new StochasticRunner(harness);

        var testCase = new TestCase
        {
            Name  = "TravelAgent — Stochastic (top-3 criteria)",
            Input = "Plan a 7-day trip visiting both Tokyo and Cologne. " +
                    "I need city information, flights between them, " +
                    "and hotel bookings for each city. " +
                    "Please send a trip summary to traveller@example.com.",
            EvaluationCriteria = [..TravelEvalCriteria.Stochastic],
            PassingScore = 60
        };

        var options = new StochasticOptions(
            Runs:                   Runs,
            SuccessRateThreshold:   Threshold,
            OnProgress:             p => PrintProgress(p));

        var evalOptions = new EvaluationOptions
        {
            TrackTools       = true,
            EvaluateResponse = true,
            ModelName        = Config.Model
        };

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
            Console.WriteLine($"\n  ❌ Stochastic run failed: {ex.Message}");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("\n  " + new string('─', 70) + "\n");

        // Collect individual scores for EvalPrinter
        var scores = stochResult.IndividualResults.Select(r => r.Score).ToList();

        EvalPrinter.PrintStochasticSummary(
            architecture:  "Single Agent (TravelAgent)",
            runs:          Runs,
            scores:        scores,
            passRate:      stochResult.Statistics.PassRate,
            overallPassed: stochResult.Passed,
            label:         "Eval 04 — Stochastic TravelAgent (5 runs · top-3 criteria)");

        // ── StochasticAssertions ─────────────────────────────────────────────────
        try
        {
            stochResult.Should()
                .HavePassRateAtLeast(Threshold,
                    because: $"at least {Threshold * 100:F0}% of runs should meet the 3 key criteria");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ✅ Stochastic assertion PASSED — pass rate {stochResult.Statistics.PassRate * 100:F1}%");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"\n  ⚠️  HYPOTHESIS SIGNAL — single agent fell below {Threshold * 100:F0}% pass rate.\n" +
                $"     {ex.Message}\n" +
                $"     Compare with Eval 05 (workflow) to see if workflow is more consistent.");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  💡 Score range {scores.Min()}–{scores.Max()} (spread = {scores.Max() - scores.Min()})");
        Console.WriteLine($"     A large spread means high variance — typical of a single unconstrained agent.");
        Console.WriteLine($"     Run Eval 05 to see the workflow's spread for comparison.");
        Console.ResetColor();

        // ── Persist stochastic snapshot for Eval03 comparison ──────────────────
        double mean = scores.Count > 0 ? scores.Average() : 0;
        EvalResultStore.SaveStochastic("eval04_stochastic_agent", new StochasticSnapshot
        {
            Architecture = "Single Agent (TravelAgent)",
            Label        = "Eval 04 — Stochastic TravelAgent (5 runs · top-3 criteria)",
            Runs         = Runs,
            PassRate     = stochResult.Statistics.PassRate,
            Passed       = stochResult.Passed,
            PassedCount  = stochResult.PassedCount,
            MinScore     = scores.Min(),
            MaxScore     = scores.Max(),
            MeanScore    = mean,
            Scores       = scores
        });
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  📁 Stochastic snapshot saved → {EvalResultStore.StorageLocation}");
        Console.WriteLine($"     Run Eval 3 to compare spread with Eval 05 (workflow).");
        Console.ResetColor();
    }

    private static void PrintProgress(StochasticProgress p)
    {
        var icon    = p.LastResult?.Passed == true ? "✅" : "❌";
        var score   = p.LastResult?.Score ?? 0;
        var elapsed = p.Elapsed.TotalSeconds;
        var eta     = p.EstimatedRemaining.HasValue
            ? $"  ~{p.EstimatedRemaining.Value.TotalSeconds:F0}s remaining"
            : "";
        Console.ForegroundColor = p.LastResult?.Passed == true ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {icon} Run {p.CurrentRun}/{p.TotalRuns} — score: {score,3}/100  " +
                          $"(elapsed: {elapsed:F0}s{eta})");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
  ⚠️  Skipping Eval 04 — Azure OpenAI credentials required.
");
        Console.ResetColor();
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
