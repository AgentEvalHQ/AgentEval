// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

namespace AgentEval.TravelDemo.Evals;

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

        // ── Provenance: architecture must be the ONLY difference ─────────────────
        // Each eval picks its provider and model from the environment at run time, and grades with that same model.
        // Two snapshots from different models (or one that does not say) compare hosts and judges as much as
        // architectures, so the hypothesis verdict is withheld. Warned either way; the models are named only with
        // --show-model. So is a side whose structure / tool / policy checks FAILED (or were not recorded): its quality
        // score says nothing about a run that did not do the job, and comparing scores would hide that.
        var noVerdict = agentSnapshot is not null && workflowSnapshot is not null
            ? ForSingleRuns(agentSnapshot, workflowSnapshot, Config.ShowModel)
            : null;
        PrintProvenanceWarning(noVerdict);

        // ── Main comparison panel ────────────────────────────────────────────────
        EvalPrinter.PrintComparison(
            agentSnapshot,
            workflowSnapshot,
            label: "Eval 03 — Hypothesis: Single Agent vs Workflow",
            noVerdictReason: noVerdict?.Short);

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

            var stochNoVerdict = agentStoch is not null && workflowStoch is not null
                ? ForStochastic(agentStoch, workflowStoch, Config.ShowModel)
                : null;
            PrintProvenanceWarning(stochNoVerdict);

            EvalPrinter.PrintStochasticComparison(
                agentStoch,
                workflowStoch,
                label: "Eval 03 — Stochastic Spread: Agent vs Workflow",
                noVerdictReason: stochNoVerdict?.Short);
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

    /// <summary>Why a pair of snapshots gets no hypothesis verdict: a short form for the panel, a full one above it.</summary>
    internal sealed record NoVerdict(string Short, string Long);

    /// <summary>Eval 1 vs Eval 2: the same model, and both sides' structure / tool / policy checks recorded as passed.</summary>
    internal static NoVerdict? ForSingleRuns(EvalSnapshot agent, EvalSnapshot workflow, bool showModel) =>
        NoVerdictReason("Eval 1", agent.ModelIdentity, "Eval 2", workflow.ModelIdentity, erroredRuns: 0, showModel,
                        leftChecksPassed: agent.StructurePassed, rightChecksPassed: workflow.StructurePassed);

    /// <summary>
    /// Eval 4 vs Eval 5: the same model, every run measured, and every measured run scored — a spread over fewer runs
    /// than were attempted is not the spread it claims (a workflow run with no output used to drop out of it).
    /// </summary>
    internal static NoVerdict? ForStochastic(StochasticSnapshot agent, StochasticSnapshot workflow, bool showModel) =>
        NoVerdictReason("Eval 4", agent.ModelIdentity, "Eval 5", workflow.ModelIdentity,
                        erroredRuns: agent.ErroredRuns + workflow.ErroredRuns, showModel,
                        unscoredRuns: agent.UnscoredRuns is { } a && workflow.UnscoredRuns is { } w ? a + w : null);

    /// <summary>
    /// Null when architecture is the only recorded difference between the two snapshots and both are sound; otherwise
    /// why not. Pure, so the self-test can check it, and it names the models only when <paramref name="showModel"/>.
    /// </summary>
    /// <param name="leftEval">The left snapshot's eval, for the message ("Eval 1").</param>
    /// <param name="leftIdentity">Its <c>model@provider</c>, or null when not recorded.</param>
    /// <param name="rightEval">The right snapshot's eval.</param>
    /// <param name="rightIdentity">Its <c>model@provider</c>, or null when not recorded.</param>
    /// <param name="erroredRuns">Runs not measured across both snapshots.</param>
    /// <param name="showModel">Whether the message may name the models.</param>
    /// <param name="leftChecksPassed">The left run's structure / tool / policy checks; false = failed, null = not recorded.</param>
    /// <param name="rightChecksPassed">The same for the right run.</param>
    /// <param name="unscoredRuns">Measured runs with no judge score across both snapshots; null = not recorded.</param>
    internal static NoVerdict? NoVerdictReason(string leftEval, string? leftIdentity, string rightEval, string? rightIdentity,
                                               int erroredRuns, bool showModel,
                                               bool? leftChecksPassed = true, bool? rightChecksPassed = true, int? unscoredRuns = 0)
    {
        switch (Provenance.Compare(leftIdentity, rightIdentity))
        {
            case ProvenanceMatch.Different:
                return new NoVerdict(
                    "the two snapshots come from different models",
                    showModel
                        ? $"{leftEval} ran on {leftIdentity} and {rightEval} on {rightIdentity}: the comparison is between models " +
                          "and judges as much as architectures. Re-run both on one model for a verdict."
                        : $"{leftEval} and {rightEval} ran on different models (--show-model names them): the comparison is between " +
                          "models and judges as much as architectures. Re-run both on one model for a verdict.");
            case ProvenanceMatch.Unknown:
                var unrecorded = (leftIdentity, rightIdentity) switch
                {
                    (null, null) => $"The {leftEval} and {rightEval} snapshots were",
                    (null, _)    => $"The {leftEval} snapshot was",
                    _            => $"The {rightEval} snapshot was",
                };
                return new NoVerdict(
                    "a snapshot does not record its model",
                    $"{unrecorded} saved before snapshots recorded their model, so nobody knows whether only the " +
                    "architecture differs. Re-run for a verdict.");
        }
        // A side that FAILED its deterministic checks did not do the job: no score comparison may stand in for that.
        var failed = new[] { (leftEval, leftChecksPassed), (rightEval, rightChecksPassed) }.Where(s => s.Item2 == false).Select(s => s.Item1).ToList();
        if (failed.Count > 0)
        {
            var who = string.Join(" and ", failed);
            return new NoVerdict(
                $"{who} FAILED structure / tool / policy checks",
                $"{who} FAILED {(failed.Count > 1 ? "their" : "its")} structure / tool / policy checks (RESULT: FAIL): " +
                "comparing quality scores would hide that. Fix the failure and re-run for a verdict.");
        }
        var unrecordedChecks = new[] { (leftEval, leftChecksPassed), (rightEval, rightChecksPassed) }.Where(s => s.Item2 is null).Select(s => s.Item1).ToList();
        if (unrecordedChecks.Count > 0)
        {
            var who = string.Join(" and ", unrecordedChecks);
            return new NoVerdict(
                "a snapshot does not record its structure checks",
                $"The {who} snapshot{(unrecordedChecks.Count > 1 ? "s were" : " was")} saved before snapshots recorded the structure / tool / " +
                "policy checks, so nobody knows whether the run passed them. Re-run for a verdict.");
        }
        if (erroredRuns > 0)
        {
            return new NoVerdict(
                "some runs were not measured",
                $"{erroredRuns} run(s) across {leftEval} and {rightEval} failed on the model call or the judge and were not " +
                "measured: the spreads cover fewer runs than they claim. Re-run for a verdict.");
        }
        if (unscoredRuns is null)
        {
            return new NoVerdict(
                "a snapshot does not record its unscored runs",
                $"The {leftEval} or {rightEval} snapshot was saved before snapshots recorded runs with no judge score, so " +
                "nobody knows whether the spreads cover every run. Re-run for a verdict.");
        }
        if (unscoredRuns > 0)
        {
            return new NoVerdict(
                "some runs have no judge score",
                $"{unscoredRuns} run(s) across {leftEval} and {rightEval} produced no judge score (no output, or a failed " +
                "structure the judge gave no verdict on). They count as FAILED in the pass rates, but the spreads cover " +
                "fewer runs than were attempted. Re-run for a verdict.");
        }
        return null;
    }

    private static void PrintProvenanceWarning(NoVerdict? noVerdict)
    {
        if (noVerdict is null) return;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  ⚠️  No hypothesis verdict: {noVerdict.Long}\n");
        Console.ResetColor();
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
