// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

namespace ECS2026MAF.Evals;

/// <summary>
/// Eval 03 — Hypothesis Comparison (zero LLM calls, instant)
///
/// Loads the saved snapshots from Eval 01 (single agent) and Eval 02 (workflow)
/// and prints a side-by-side comparison table to visually prove or disprove the
/// hypothesis:
///
///   "Is a structured workflow more deterministic than a single all-in-one agent?"
///
/// Run Eval 1 and Eval 2 first to populate the snapshots, then run this eval
/// to get an instant, cost-free comparison without re-running either agent.
///
/// ⏱️ Runtime: &lt;1 second (reads from temp files — no LLM calls)
/// </summary>
public static class Eval03_HypothesisComparison
{
    private const string AgentKey    = "eval01_agent";
    private const string WorkflowKey = "eval02_workflow";

    public static Task RunAsync()
    {
        PrintHeader();

        var agentSnapshot    = EvalResultStore.Load(AgentKey);
        var workflowSnapshot = EvalResultStore.Load(WorkflowKey);

        // ── Age warnings ────────────────────────────────────────────────────────
        if (agentSnapshot is null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ⚠️  No agent snapshot found. Run Eval 1 first.\n");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Agent snapshot   : {EvalResultStore.GetAge(AgentKey)}  →  {agentSnapshot.Label}");
            Console.ResetColor();
        }

        if (workflowSnapshot is null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ⚠️  No workflow snapshot found. Run Eval 2 first.\n");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Workflow snapshot : {EvalResultStore.GetAge(WorkflowKey)}  →  {workflowSnapshot.Label}");
            Console.ResetColor();
        }

        Console.WriteLine();

        // ── Main comparison panel ────────────────────────────────────────────────
        EvalPrinter.PrintComparison(
            agentSnapshot,
            workflowSnapshot,
            label: "Eval 03 — Hypothesis: Single Agent vs Workflow");

        // ── Per-criterion diff (only when both snapshots are present) ────────────
        if (agentSnapshot is { CriteriaDetails.Count: > 0 }
         && workflowSnapshot is { CriteriaDetails.Count: > 0 })
        {
            PrintCriteriaDiff(agentSnapshot.CriteriaDetails, workflowSnapshot.CriteriaDetails);
        }

        // ── Stochastic comparison (Eval04 + Eval05 snapshots) ────────────────────
        var agentStoch    = EvalResultStore.LoadStochastic("eval04_stochastic_agent");
        var workflowStoch = EvalResultStore.LoadStochastic("eval05_stochastic_workflow");

        if (agentStoch is not null || workflowStoch is not null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            if (agentStoch    is not null)
                Console.WriteLine($"  Stochastic agent    : {EvalResultStore.GetAge("eval04_stochastic_agent")}  →  {agentStoch.Label}");
            if (workflowStoch is not null)
                Console.WriteLine($"  Stochastic workflow : {EvalResultStore.GetAge("eval05_stochastic_workflow")}  →  {workflowStoch.Label}");
            Console.ResetColor();
            Console.WriteLine();

            EvalPrinter.PrintStochasticComparison(
                agentStoch,
                workflowStoch,
                label: "Eval 03 — Stochastic Spread: Agent vs Workflow");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  ℹ️  No stochastic snapshots yet.");
            Console.WriteLine("     Run Eval 4 (single agent) and Eval 5 (workflow) to add spread comparison here.");
            Console.ResetColor();
        }

        // ── Storage info ─────────────────────────────────────────────────────────
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n  📁 Snapshots stored in: {EvalResultStore.StorageLocation}");
        Console.ResetColor();

        return Task.CompletedTask;
    }

    private static void PrintCriteriaDiff(
        IReadOnlyList<CriterionSnapshot> agentCriteria,
        IReadOnlyList<CriterionSnapshot> workflowCriteria)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║ CRITERION-LEVEL DIFF  (Agent criteria vs Workflow criteria)                     ║");
        Console.WriteLine("╠══════════════════════════════════════════════════════════════════════════════════╣");
        Console.ResetColor();

        // Map by criterion text (first 60 chars as key)
        var agentMap    = agentCriteria.ToDictionary(
            c => c.Name[..Math.Min(c.Name.Length, 60)].Trim(),
            c => c.Met);
        var workflowMap = workflowCriteria.ToDictionary(
            c => c.Name[..Math.Min(c.Name.Length, 60)].Trim(),
            c => c.Met);

        // Print agent-specific criteria
        foreach (var cr in agentCriteria)
        {
            var key  = cr.Name[..Math.Min(cr.Name.Length, 60)].Trim();
            bool met = cr.Met;
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write("║ ");
            Console.ForegroundColor = met ? ConsoleColor.Green : ConsoleColor.Red;
            var line = $"  {(met ? "✅" : "❌")} Agent    │ {key,-60}";
            Console.Write(line[..Math.Min(line.Length, 80)].PadRight(80));
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine(" ║");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("╠══════════════════════════════════════════════════════════════════════════════════╣");
        Console.ResetColor();

        // Print workflow-specific criteria
        foreach (var cr in workflowCriteria)
        {
            var key  = cr.Name[..Math.Min(cr.Name.Length, 60)].Trim();
            bool met = cr.Met;
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write("║ ");
            Console.ForegroundColor = met ? ConsoleColor.Green : ConsoleColor.Red;
            var line = $"  {(met ? "✅" : "❌")} Workflow │ {key,-60}";
            Console.Write(line[..Math.Min(line.Length, 80)].PadRight(80));
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine(" ║");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 03 — Hypothesis Comparison  (no LLM calls — instant)                 ║
║   Reads saved snapshots from Eval 1 + Eval 2 and compares side-by-side      ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}
