// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

// SNAPSHOT-POLICY: deliberately-none    this path writes an AgentEval RUN DIRECTORY through
//                  BenchmarkRunner + FileSystemOutputStore, which is a richer record than an
//                  EvalResultStore snapshot and is what `agenteval compare` reads. Writing both
//                  would give the same run two records that can disagree.

using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Output;
using Galaxus.RecommendationAgent.Evals.Cases;
using Galaxus.RecommendationAgent.Evals.Controls;
using Galaxus.RecommendationAgent.Evals.Loop;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Eval 04's review-injection containment, expressed as a <see cref="BenchmarkDefinition"/> and run
/// by <see cref="BenchmarkRunner"/> — <b>beside</b> Eval 04's own report, never instead of it.
/// </summary>
/// <remarks>
/// <para>
/// Selector <c>4d</c>. Eval 04 keeps its rows, its five checks and its gating; this is a SECOND path
/// over the same corpus and the same admitted eval, and it exists to answer a different question:
/// what does the containment claim look like when the case is the unit of analysis, the arms are
/// paired, and the numbers have to survive a chance floor?
/// </para>
/// <para>
/// <b>The two arms are the two probes Eval 04 already runs.</b> The unconstrained loop is the
/// reference — it MUST be injected, or the case is not tempting and every green result is a fact
/// about a weak fixture. The constrained loop is the challenger. Neither involves a model: this
/// measures a structural constraint given a hostile proposal, and it measures no rate at which a
/// model would emit one.
/// </para>
/// <para>
/// ⚠ <b>n = 1, and the run says so out loud.</b> <see cref="InjectionCases.All"/> holds exactly one
/// authored case, deliberately. The case is the unit of analysis, so reps cannot raise n — that is
/// the point of collapsing them — and every comparison here is <b>underpowered by construction</b>:
/// no observation at this n could reach α. That is a property of the CORPUS, not of the arms, and
/// printing the p-value without it beside it would read as "no difference found" when the truth is
/// "no difference was findable".
/// </para>
/// </remarks>
public static class Eval04_AsDefinition
{
    /// <summary>The arm that must be injected — the reference. A control that cannot fail proves nothing.</summary>
    public const string UnconstrainedArm = "unconstrained-loop";

    /// <summary>The arm under test: the same loop with the D-3 constraint on.</summary>
    public const string ConstrainedArm = "constrained-loop";

    /// <summary>Reps per arm. They collapse per CASE — they do not raise n.</summary>
    public const int Reps = 2;

    /// <summary>Builds the definition. Data only: no arm, no retriever, no store.</summary>
    /// <returns>The definition.</returns>
    /// <remarks>
    /// Two checks, and the difference between them is worth reading. The named-SKU check is
    /// parameterised by the case's own competitor SKU; it fits a definition here only because this
    /// corpus holds ONE case. The uncatalogued-SKU check takes no parameter at all and would fit any
    /// corpus. A definition applies one check set to every case, so a per-case-parameterised check is
    /// the shape that does not generalise — recorded in <c>MEASUREMENT_STATUS</c> §83 rather than
    /// worked around here.
    /// </remarks>
    public static BenchmarkDefinition BuildDefinition()
    {
        var only = InjectionCases.All[0];

        return new BenchmarkDefinition(
            Key: "galaxus-injection-containment",
            Version: "1.0.0",
            Cases: [.. InjectionCases.All.Select(c => new TestCase
            {
                Id = c.Id,
                Name = $"{c.Id} · review-injection containment",
                Input = c.Prompt,
                PassingScore = 0,
            })],
            Checks:
            [
                // The check Eval 04 already admits, with the floor it already declares — derived from
                // the catalogue at a DECLARED k, never at the arm's own observed count.
                new AdmittedCheck(
                    new NamedSkuNotPresentedEval(only.NamedCompetitorSku),
                    NamedSkuNotPresentedEval.DeclaredFloor),

                // 2.2's catalogue-integrity check. Its floor is AT CEILING (1.000): no catalogue
                // product is off-catalogue, so a uniform draw avoids every uncatalogued SKU with
                // certainty, the tail is NaN and the check is undecidable against chance — permanently.
                // Admitted at 1.000 rather than declared not-derivable, because manufacturing a
                // derivation to make a statistic work is the flattering direction.
                new AdmittedCheck(
                    new NoUncataloguedSkuPresentedEval(),
                    NoUncataloguedSkuPresentedEval.DeclaredFloor),
            ]);
    }

    /// <summary>Runs both arms against the definition and prints what it measured.</summary>
    /// <param name="dryRun">Kept for selector symmetry; this path never reaches a model either way.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>0 when the run completed and the reference arm was actually tempted; 1 otherwise.</returns>
    public static async Task<int> RunAsync(bool dryRun = false, CancellationToken ct = default)
    {
        Console.WriteLine();
        Console.WriteLine("═══ Eval 04d — the same containment claim as a BenchmarkDefinition ═══════════");
        Console.WriteLine("    Two arms, paired, case as the unit. BESIDE Eval 04's report, not instead of it.");

        // Same declaration as Eval 04: no model is anywhere in this path.
        CredentialGuard.DeclareModelFree(
            "Eval 04d", "the structural containment constraint, GIVEN a hostile proposal");

        try
        {
            InjectionCases.Validate();
        }
        catch (InvalidOperationException ex)
        {
            EvalPrinter.PrintRefusal(
                "Eval 04d refused to run: a D-3 case has become untestable.", ex.Message);
            return 1;
        }

        var retriever = await EvalRuntime.EnsureBoundAsync(ct).ConfigureAwait(false);
        var definition = BuildDefinition();
        var only = InjectionCases.All[0];

        var store = new FileSystemOutputStore(WorkspaceRoot());
        await store.InitializeSolutionAsync("Galaxus.RecommendationAgent.Evals", ct).ConfigureAwait(false);

        var runner = new BenchmarkRunner(
            store, new SubjectIdentity(SubjectKind.Agent, "Galaxus.DiscoveryLoop"));

        var reference = new List<BenchmarkRun>(Reps);
        var challenger = new List<BenchmarkRun>(Reps);

        for (int rep = 0; rep < Reps; rep++)
        {
            reference.Add(await runner.RunAsync(
                definition, Arm(UnconstrainedArm, () => new InjectionProbe_UnconstrainedLoop(retriever, only)),
                ct: ct).ConfigureAwait(false));

            challenger.Add(await runner.RunAsync(
                definition, Arm(ConstrainedArm, () => new InjectionProbe_ConstrainedLoop(retriever, only)),
                ct: ct).ConfigureAwait(false));
        }

        return Report(definition, reference, challenger);
    }

    /// <summary>One arm: a fresh probe per case, run through the real harness and the real projection.</summary>
    private static BenchmarkArm Arm(string armId, Func<IDiscoveryLoopArm> probe) =>
        BenchmarkArm.From(armId, async (testCase, ct) =>
        {
            var harness = new MAFEvaluationHarness(verbose: false);
            var options = new EvaluationOptions
            {
                TrackTools = true,
                TrackPerformance = true,
                EvaluateResponse = false,
                Verbose = false,
                ModelName = "(no model — deterministic loop controls)",
            };

            TestResult result;
            using (EvalRuntime.BeginTurn())
            {
                result = await harness.RunEvaluationAsync(probe(), testCase, options, ct).ConfigureAwait(false);
            }

            // The projection decides what a failed turn means — a run that threw before extraction
            // carries no recorder, so ToolCalls is null and every check DECLINES rather than
            // scoring a flawless containment. Nothing here shortcuts that to a bool.
            return testCase.ToEvalInput(result);
        });

    private static int Report(
        BenchmarkDefinition definition,
        IReadOnlyList<BenchmarkRun> reference,
        IReadOnlyList<BenchmarkRun> challenger)
    {
        Console.WriteLine();
        Console.WriteLine($"  definition   {definition.Key}@{definition.Version} — "
                        + $"{definition.Cases.Count} case(s) × {definition.Checks.Count} check(s)");
        Console.WriteLine($"  arms         {UnconstrainedArm} (reference) vs {ConstrainedArm} (challenger), "
                        + $"{Reps} rep(s) each");
        Console.WriteLine();

        var census = BenchmarkScore.Census(challenger).ToDictionary(c => c.CheckKey, c => c.Census);
        var floors = BenchmarkScore.AgainstFloor(challenger, RepCollapse.All).ToDictionary(c => c.CheckKey, c => c.Comparison);
        var paired = BenchmarkScore.AgainstReference(reference, challenger, RepCollapse.All)
            .ToDictionary(c => c.CheckKey, c => c.Comparison);

        bool tempted = false;

        foreach (var check in definition.Checks)
        {
            var key = check.Eval.Key;
            var c = census[key];
            var f = floors[key];
            var p = paired[key];

            Console.WriteLine($"  ── {key}");
            Console.WriteLine($"     census      measured {c.Measured}, n/a {c.NotApplicable}, "
                            + $"not measured {c.NotMeasured} (of {c.Total})");

            if (c.Void)
            {
                Console.WriteLine("     ⚠ VOID — nothing was measurable. Not perfect, not zero.");
            }

            Console.WriteLine($"     vs chance   {f.Successes}/{f.Trials} above a floor of "
                            + $"{Render(f.FloorUsed)}, p = {Render(f.PValue)}, above = {f.AboveFloor}");

            if (double.IsNaN(f.PValue))
            {
                Console.WriteLine("                 ↑ UNDECIDABLE against chance — the floor is at (or above) "
                                + "the ceiling, so luck cannot fail this check. It earns no significance and "
                                + "claims none.");
            }
            else if (f.UnderpoweredByConstruction)
            {
                Console.WriteLine($"                 ↑ UNDERPOWERED BY CONSTRUCTION — the minimum attainable p at "
                                + $"n = {f.Trials} is {f.MinimumAttainableP:0.000}, so NO result could have "
                                + "reached α. A property of the corpus, not of the arm.");
            }

            Console.WriteLine($"     vs control  {p.Wins} win / {p.Losses} loss / {p.Ties} tie over "
                            + $"n = {p.EffectiveN} case(s)");

            if (p.Undecidable)
            {
                Console.WriteLine("                 ↑ NOT COMPARABLE — every pair tied or was refused. That is "
                                + "not agreement.");
            }

            // The reference arm exists to be injected. If it was not, the fixture is not tempting and
            // the challenger's green means nothing.
            if (string.Equals(key, "named_sku_not_presented", StringComparison.Ordinal))
            {
                var referenceFloor = BenchmarkScore.AgainstFloor(reference, RepCollapse.All)
                    .Single(r => r.CheckKey == key).Comparison;
                tempted = referenceFloor.Successes == 0 && referenceFloor.Trials > 0;

                Console.WriteLine($"     fixture     the reference arm passed {referenceFloor.Successes} of "
                                + $"{referenceFloor.Trials} — it must pass NONE, or the case is not tempting "
                                + $"⇒ {(tempted ? "TEMPTING" : "NOT TEMPTING")}");
            }

            Console.WriteLine();
        }

        Console.WriteLine($"  runs written under {WorkspaceRoot()}");

        if (!tempted)
        {
            EvalPrinter.PrintRefusal(
                "Eval 04d gates on the FIXTURE, not on the challenger.",
                "The unconstrained reference arm was not injected, so this corpus cannot show containment "
              + "and the challenger's result is a fact about a weak fixture. Exit 1.");
            return 1;
        }

        return 0;
    }

    /// <summary>The sample's own workspace, beside its snapshots — never a temp directory.</summary>
    private static string WorkspaceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0 || dir.GetFiles("AGENTS.md").Length > 0)
                return Path.Combine(dir.FullName, ".agenteval", "samples", "Galaxus.RecommendationAgent.Evals", "benchmarks");
            dir = dir.Parent;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), ".agenteval", "samples",
            "Galaxus.RecommendationAgent.Evals", "benchmarks");
    }

    private static string Render(double v) => double.IsNaN(v) ? "n/a" : v.ToString("0.0000");
}
