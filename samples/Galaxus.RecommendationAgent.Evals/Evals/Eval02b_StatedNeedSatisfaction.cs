// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using AgentEval.MAF;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Evals.Adapters;
using Microsoft.Agents.AI;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Eval 02b — Stated-Need Satisfaction. The first eval in this suite the tag-join oracle cannot
/// answer, and the first with a PRECISION channel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists — three findings about Eval 02, stated here because this eval is the
/// response to them.</b>
/// </para>
/// <list type="number">
///   <item><description><b>The headline was confounded by k.</b> Latent coverage is recall and
///   rises with k; the live agent presented 3.1 items on average, every scripted control exactly
///   5, the loop 7-12, and the sign test paired them anyway. Per item presented the live agent was
///   AHEAD. This eval scores <i>precision</i> — the share of presented items that satisfy every
///   stated constraint — which a uniform draw of ANY size scores at <c>|S| / N</c>, so arms of
///   different k sit on one scale. k is still printed beside every cell.</description></item>
///   <item><description><b>The suite never measured the task the pitch says the model is
///   for.</b> Twelve personas, one history-driven sentence each — the task a
///   <c>WHERE tags &amp;&amp; shared_tags</c> wins by construction, and did, at 1.000. Here each
///   persona states a real need with at least three constraints that are NOT tags: a budget in
///   CHF, a thing they own the answer must fit, a category, an exclusion, a deadline. The gold is
///   those constraints checked in code (<see cref="StatedNeedCase"/>), and nothing in it derives
///   from the field the retrieval index embeds.</description></item>
///   <item><description><b>Silence was scored on behaviour the agent was instructed to have.</b>
///   Jonas's Eval 02 turn is k = 0 by the prompt's own rule (one department, no stated need). A
///   STATED need voids that rule's precondition, so on this eval k = 0 on an applicable case is
///   a fail — scored 0 and flagged, never NaN, never a pass.</description></item>
/// </list>
/// <para>
/// <b>Arms.</b> The live agent; Demo 2's loop on its deterministic path, once with the customer's
/// words and once with them replaced by Eval 02's generic question (the pair whose difference is
/// exactly "reading the need"); the ORACLE — a constraint filter handed the gold, the ceiling;
/// Eval 02's single-shot control and its tag-join oracle, both utterance-blind, as reference rows
/// showing what history alone scores here; and <see cref="Broken06_ConstraintBlindRecommender"/>,
/// a uniform draw that is the chance floor EXECUTED fifty times per case and checked against the
/// closed form.
/// </para>
/// <para>
/// <b>Credentials.</b> Every arm but the live one runs with no key. Without a key the offline arms
/// are measured and printed under their own labels, the live column reads
/// <c>NOT MEASURED — no credentials</c>, and the exit code is 3. Nothing is substituted for the
/// agent. With <c>--dry-run</c> the live column is a deliberately implausible stub and the run
/// asserts only that the plumbing held.
/// </para>
/// <para>
/// ⏱️ Runtime: about 3-8 minutes live at 3 reps (36 agent turns), a third of that with
/// <c>--quick</c>. The offline arms take seconds.
/// </para>
/// </remarks>
public static class Eval02b_StatedNeedSatisfaction
{
    /// <summary>Repetitions per case for the live arm.</summary>
    public const int Reps = 3;

    /// <summary>Repetitions per case for the live arm under <c>--quick</c>.</summary>
    public const int QuickReps = 1;

    /// <summary>How many uniform draws per case the executed floor averages over.</summary>
    public const int FloorDraws = 50;

    /// <summary>Width of the band the executed floor must land in, in standard deviations of its own mean.</summary>
    public const double FloorBandSigmas = 3.0;

    /// <summary>The live agent's label. Same key as Eval 02.</summary>
    public const string ArmLive = CoverageArms.Live;

    /// <summary>Demo 2's loop, asked the customer's own question.</summary>
    public const string ArmLoop = DiscoveryLoopAdapter.ArmLabel;

    /// <summary>Demo 2's loop, asked Eval 02's generic question instead. Same customer, need removed.</summary>
    public const string ArmLoopBlind = "Discovery Workflow (Demo 2) — utterance-blind";

    /// <summary>The oracle: a constraint filter handed the gold.</summary>
    public const string ArmOracle = "Oracle — constraint filter";

    /// <summary>Eval 02's primary control. Utterance-blind by construction.</summary>
    public const string ArmSingleShot = CoverageArms.SingleShot;

    /// <summary>Eval 02's oracle. Utterance-blind by construction — the point of this eval.</summary>
    public const string ArmTagJoin = CoverageArms.TagJoin;

    /// <summary>The executed floor.</summary>
    public const string ArmFloor = "Broken06 — constraint-blind draw";

    private enum ArmRole { Live, Loop, Reference, Oracle, Floor }

    private sealed record Arm(
        string Label,
        string Short,
        ArmRole Role,
        Func<CoverageArmContext, int, IEvaluableAgent>? Factory,
        string Note);

    private sealed record WiringRow(string Name, string Expectation, string Observed, bool Ok);

    /// <summary>The key a one-case probe writes to. NEVER the full-cohort key.</summary>
    public const string ProbeSnapshotKey = OfflineSnapshotStore.StatedNeedKey + "_probe";

    /// <summary>Runs the eval.</summary>
    /// <param name="quick">One live repetition instead of three.</param>
    /// <param name="dryRun">Stub the live arm; spend nothing; assert the plumbing.</param>
    /// <param name="onlyCase">
    /// Restrict the run to one case id (<c>SN-01</c> … ) — the one-item real run that is stage two
    /// of the three-stage protocol. The snapshot then goes to <see cref="ProbeSnapshotKey"/>, so a
    /// single-case probe can never overwrite the full-cohort record, and the GATE is reported as a
    /// probe result: n = 1 decides nothing about the cohort in either direction.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 passed, 1 a gate or a wiring check failed, 3 the live arm was not measured.</returns>
    public static async Task<int> RunAsync(
        bool quick = false, bool dryRun = false, string? onlyCase = null, CancellationToken ct = default)
    {
        PrintHeader();

        try
        {
            StatedNeedCases.Validate();
        }
        catch (InvalidOperationException ex)
        {
            EvalPrinter.PrintRefusal("Eval 02b refused to run.", ex.Message);
            return 1;
        }

        IReadOnlyList<StatedNeedCase> selected = onlyCase is null
            ? StatedNeedCases.All
            : [.. StatedNeedCases.All.Where(c => string.Equals(c.Id, onlyCase, StringComparison.OrdinalIgnoreCase))];

        if (selected.Count == 0)
        {
            EvalPrinter.PrintRefusal(
                $"--only {onlyCase} matches no stated-need case.",
                "Case ids: " + string.Join(", ", StatedNeedCases.All.Select(c => c.Id)) + ".");
            return 2;
        }

        bool liveMeasurable = dryRun || Config.IsConfigured;
        PrintMode(dryRun, liveMeasurable);

        if (onlyCase is not null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  🔬 ONE-CASE PROBE — {selected[0].Id} only. Stage two of the three-stage protocol.");
            Console.WriteLine($"     n = 1: the cohort mean is not reachable, the snapshot goes to '{ProbeSnapshotKey}'");
            Console.WriteLine("     and never to the full-cohort key, and the gate below is this ONE case's, not the suite's.");
            Console.ResetColor();
            Console.WriteLine();
        }

        var retriever = await EvalRuntime.EnsureBoundAsync(ct).ConfigureAwait(false);

        int liveReps = dryRun ? 1 : quick ? QuickReps : Reps;
        var harness = new MAFEvaluationHarness(verbose: false);
        var options = new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            EvaluateResponse = false,
            Verbose = false,
            ModelName = dryRun ? "(stub — dry run)" : liveMeasurable ? Config.Model : "(live arm not measured)",
        };

        ChatClientAgent? liveAgent = dryRun
            ? RecommendationAgentFactory.Create(StubChatClient.PresentingAgent())
            : liveMeasurable ? RecommendationAgentFactory.Create() : null;

        var context = new CoverageArmContext(
            retriever,
            LiveAgentFactory: () => liveAgent is null
                ? throw new InvalidOperationException("The live arm was asked to run without a model.")
                : new ApprovalAwareAgentAdapter(liveAgent),
            DryRun: dryRun);

        var arms = BuildArms();
        var ledger = new SpendLedger();
        var cells = new Dictionary<(string CaseId, string Arm), ConstraintScore>();
        var floors = new Dictionary<string, double>(StringComparer.Ordinal);
        var executedFloor = new Dictionary<string, double>(StringComparer.Ordinal);
        var kByArm = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var applicable = new List<StatedNeedCase>();
        var inapplicable = new List<StatedNeedCase>();
        var notes = new List<string>();
        int threw = 0;

        foreach (Arm absent in arms.Where(a => a.Factory is null))
            notes.Add($"Arm '{absent.Label}' is DECLARED ABSENT and was not run. {absent.Note}");

        foreach (StatedNeedCase testCase in selected)
        {
            var satisfying = ConstraintSatisfactionGrader.SatisfyingSet(testCase);
            double floor = ConstraintSatisfactionGrader.UniformDrawFloor(testCase);
            PrintCaseHeader(testCase, satisfying, floor);

            if (satisfying.Count == 0)
            {
                inapplicable.Add(testCase);
                notes.Add($"{testCase.Id} ({testCase.Name}) is NOT APPLICABLE: no catalogue product satisfies every stated " +
                          "constraint. Excluded from every mean — an impossible need is a fact about the corpus, not a fail.");
                continue;
            }

            applicable.Add(testCase);
            floors[testCase.Id] = floor;

            foreach (Arm arm in arms)
            {
                if (arm.Factory is null) continue;
                if (arm.Role == ArmRole.Live && !liveMeasurable) continue;

                int armReps = arm.Role switch
                {
                    ArmRole.Live => liveReps,
                    ArmRole.Floor => FloorDraws,
                    _ => 1,
                };

                var scores = new List<ConstraintScore>(armReps);
                for (int rep = 1; rep <= armReps; rep++)
                {
                    IEvaluableAgent agent = arm.Factory(context, rep);
                    ConstraintScore? score = await ScoreAsync(
                        testCase, agent, harness, options, arm.Label, rep, armReps,
                        print: arm.Role != ArmRole.Floor, ct,
                        ledger: arm.Role == ArmRole.Live && !dryRun ? ledger : null).ConfigureAwait(false);

                    if (score is null)
                    {
                        threw++;
                        notes.Add($"{testCase.Id} · {arm.Label} · rep {rep} THREW and was EXCLUDED. An errored turn is the " +
                                  "absence of a measurement, not a 0.000.");
                        continue;
                    }

                    scores.Add(score.Value);
                    if (!kByArm.TryGetValue(arm.Label, out var ks)) kByArm[arm.Label] = ks = [];
                    ks.Add(score.Value.Presented);
                }

                if (scores.Count == 0)
                {
                    notes.Add($"{testCase.Id} · {arm.Label}: EVERY run threw; no observation for this cell.");
                    continue;
                }

                var mean = ConstraintScore.Mean(scores);
                cells[(testCase.Id, arm.Label)] = mean;

                if (arm.Role == ArmRole.Floor)
                {
                    executedFloor[testCase.Id] = mean.Precision;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"      {arm.Label,-46} {FloorDraws} draws  executed floor {F3(mean.Precision)}  " +
                                      $"analytic {F3(floor)}");
                    Console.ResetColor();
                }
            }
        }

        var runnable = arms.Where(a => a.Factory is not null).ToList();
        PrintTable(runnable, applicable, cells, floors, liveMeasurable, dryRun);
        PrintFloors(applicable, floors, executedFloor, cells);
        ledger.Print(Config.Model, "Eval 02b");

        var wiring = CheckWiring(runnable, applicable, cells, floors, executedFloor, threw, liveMeasurable);
        PrintWiring(wiring);

        AddComparisonNotes(runnable, applicable, cells, floors, kByArm, notes, liveMeasurable);

        bool wiringHeld = wiring.All(w => w.Ok);
        bool liveAboveFloor = liveMeasurable && applicable.Count > 0 && applicable.All(c =>
            cells.TryGetValue((c.Id, ArmLive), out var s) && !s.Silent && s.Precision > floors[c.Id]);

        PrintVerdict(applicable, cells, floors, liveMeasurable, dryRun, liveAboveFloor, notes);

        if (!wiringHeld)
        {
            EvalPanel.Line("  ❌ EVAL 02b — a WIRING check failed (exit code 1). Treat every number above as unproven.", ConsoleColor.Red);
            return 1;
        }

        if (dryRun)
        {
            bool plumbing = DryRunPlumbingHeld(runnable, applicable, cells);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  📁 Snapshot NOT written — a dry run must not leave a result behind.");
            Console.ResetColor();
            return plumbing ? 0 : 1;
        }

        if (!liveMeasurable)
        {
            PrintLiveNotMeasured();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  📁 Snapshot NOT written — the live arm was not measured, and a snapshot without it");
            Console.WriteLine("     would be read later as a run that had one.");
            Console.ResetColor();
            return CredentialGuard.NotMeasuredExitCode;
        }

        string snapshotKey = onlyCase is null ? OfflineSnapshotStore.StatedNeedKey : ProbeSnapshotKey;
        string path = OfflineSnapshotStore.Save(snapshotKey, ToSnapshot(runnable, applicable, inapplicable, cells, floors, liveReps));
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  📁 Snapshot saved → {path}");
        if (onlyCase is not null)
            Console.WriteLine($"     (probe key '{ProbeSnapshotKey}' — the full-cohort record at '{OfflineSnapshotStore.StatedNeedKey}' is untouched.)");
        Console.ResetColor();

        return liveAboveFloor ? 0 : 1;
    }

    // ══ Arms ══════════════════════════════════════════════════════════════════════════════

    private static IReadOnlyList<Arm> BuildArms()
    {
        bool loopBound = DiscoveryLoopAdapter.IsBound;

        return
        [
            new Arm(ArmLive, "live", ArmRole.Live,
                (ctx, _) => ctx.LiveAgentFactory(),
                "The shipped single agent, asked the customer's own words. Repeated; stochastic."),

            new Arm(ArmLoop, "loop", ArmRole.Loop,
                loopBound ? (ctx, _) => DiscoveryLoopAdapter.Create(ctx)! : null,
                loopBound
                    ? "Demo 2's loop on its deterministic path, with the customer's words in its stated-need slot. Zero model calls."
                    : DiscoveryLoopAdapter.AbsenceReason),

            new Arm(ArmLoopBlind, "loop-blind", ArmRole.Reference,
                loopBound ? (ctx, _) => new UtteranceBlindArm(DiscoveryLoopAdapter.Create(ctx)!) : null,
                loopBound
                    ? "The SAME loop asked Eval 02's generic question. The difference to the column left of it is what reading the need bought."
                    : DiscoveryLoopAdapter.AbsenceReason),

            new Arm(ArmOracle, "oracle", ArmRole.Oracle,
                (_, _) => new Baseline_ConstraintFilter(),
                "A constraint filter HANDED the gold. The ceiling, and the grader's accepting direction: must be 1.000 everywhere."),

            new Arm(ArmSingleShot, "1-shot", ArmRole.Reference,
                (ctx, _) => new Broken03_SingleShotWorkflow(ctx.Retriever),
                "Eval 02's primary control. It never reads the utterance, so here it is a history-only reference."),

            new Arm(ArmTagJoin, "tag-join", ArmRole.Reference,
                (_, _) => new Baseline_TagJoin(),
                "Eval 02's ORACLE (1.000 there). It never reads the utterance. What it scores here is what a tag join is worth on a stated need."),

            new Arm(ArmFloor, "uniform", ArmRole.Floor,
                (_, rep) => new Broken06_ConstraintBlindRecommender(rep),
                $"A uniform draw of {Broken06_ConstraintBlindRecommender.DrawSize} from the whole catalogue, {FloorDraws}× per case. The floor, executed."),
        ];
    }

    // ══ One graded turn ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// One arm, one case, one harness turn, one grade. Internal — not private — because Eval 03's
    /// Broken06 row scores its draws through THIS method, so that what it verifies is the path the
    /// live agent is scored by and not a re-implementation of it.
    /// </summary>
    /// <returns>The score, or null when the turn threw (an absent measurement, never a 0.000).</returns>
    internal static async Task<ConstraintScore?> ScoreAsync(
        StatedNeedCase testCase,
        IEvaluableAgent agent,
        MAFEvaluationHarness harness,
        EvaluationOptions options,
        string armLabel,
        int rep,
        int reps,
        bool print,
        CancellationToken ct,
        SpendLedger? ledger = null)
    {
        var tc = new TestCase
        {
            Name = $"{testCase.Id} · {armLabel} · rep {rep}/{reps}",
            Input = testCase.Prompt,
            PassingScore = 0,
        };

        TestResult result;
        using (EvalRuntime.BeginTurn())
        {
            result = await harness.RunEvaluationAsync(agent, tc, options, ct).ConfigureAwait(false);
        }

        // Only ever non-null for the LIVE arm — see the note at the same seam in Eval 02c.
        ledger?.Record(result.Performance);

        if (result.HasError)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"      {armLabel,-46} rep {rep}/{reps} ❌ threw: {result.Error?.Message}");
            Console.ResetColor();
            return null;
        }

        var presented = PresentedCall.FromToolUsage(result.ToolUsage);
        var score = ConstraintSatisfactionGrader.Grade(testCase, presented);

        if (print)
        {
            double floor = ConstraintSatisfactionGrader.UniformDrawFloor(testCase);
            Console.ForegroundColor = score.Silent ? ConsoleColor.Red
                                    : score.Precision > floor ? ConsoleColor.Green : ConsoleColor.DarkGray;
            Console.WriteLine($"      {armLabel,-46} {(reps > 1 ? $"rep {rep}/{reps}" : "deterministic"),-14} " +
                              $"precision {F3(score.Precision)} ({score.Satisfied}/{score.Presented}) " +
                              $"floor {F3(floor)}  slots {score.SlotsCovered}/{score.SlotTotal}" +
                              (score.Silent ? "  ⚠ SILENT — presented nothing on a stated need" : "") +
                              (score.Phantom > 0 ? $"  ⚠ phantom {score.Phantom}" : ""));
            Console.ResetColor();
            if (score.Presented > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"        presented: {string.Join(", ", score.PresentedSkus.Select(s => score.SatisfiedSkus.Contains(s, StringComparer.OrdinalIgnoreCase) ? s + "✓" : s))}");
                Console.ResetColor();
            }
        }

        return score;
    }

    // ══ Wiring — the checks that make the numbers mean anything ═══════════════════════════

    private static IReadOnlyList<WiringRow> CheckWiring(
        IReadOnlyList<Arm> arms,
        IReadOnlyList<StatedNeedCase> applicable,
        IReadOnlyDictionary<(string, string), ConstraintScore> cells,
        IReadOnlyDictionary<string, double> floors,
        IReadOnlyDictionary<string, double> executedFloor,
        int threw,
        bool liveMeasurable)
    {
        var rows = new List<WiringRow>();

        rows.Add(new WiringRow("FixtureApplicable",
            "at least one of the twelve authored needs has a satisfying product in the catalogue.",
            $"{applicable.Count} of {StatedNeedCases.All.Count} applicable",
            applicable.Count > 0));

        // The ACCEPTING direction: the arm handed the gold must score 1.000 on every applicable case.
        var oracleShort = applicable
            .Where(c => !(cells.TryGetValue((c.Id, ArmOracle), out var s) && s.Presented > 0 && Math.Abs(s.Precision - 1.0) < 1e-9))
            .Select(c => c.Id)
            .ToList();
        rows.Add(new WiringRow("OracleAccepts",
            "the constraint-filter ORACLE, handed the gold, scores exactly 1.000 on every applicable case. A grader that " +
            "rejected true satisfiers would put the floor control 'at floor' for the wrong reason.",
            oracleShort.Count == 0
                ? $"1.000 on all {applicable.Count} applicable cases"
                : $"NOT 1.000 on {string.Join(", ", oracleShort)}",
            applicable.Count > 0 && oracleShort.Count == 0));

        // The REJECTING direction: the uniform draw's executed mean must land within a stated band of the closed form.
        double analyticMean = applicable.Count == 0 ? double.NaN : applicable.Average(c => floors[c.Id]);
        double executedMean = applicable.Count == 0 || applicable.Any(c => !executedFloor.ContainsKey(c.Id))
            ? double.NaN
            : applicable.Average(c => executedFloor[c.Id]);
        double sdOfMean = ConstraintSatisfactionGrader.UniformDrawSigmaOfMean(
            applicable, Broken06_ConstraintBlindRecommender.DrawSize, FloorDraws);
        double band = FloorBandSigmas * sdOfMean;
        bool atFloor = !double.IsNaN(executedMean) && !double.IsNaN(analyticMean) && Math.Abs(executedMean - analyticMean) <= band;
        rows.Add(new WiringRow("FloorControlAtFloor",
            $"Broken06 — a uniform draw that ignores every constraint — scores AT the floor: its mean over {FloorDraws} draws " +
            $"× {applicable.Count} cases within ±{FloorBandSigmas:0}σ of the closed form |S|/N. Above the band the grader " +
            "credits what it should not; below it the grader rejects true satisfiers.",
            $"executed {F3(executedMean)} vs analytic {F3(analyticMean)} · band ±{F3(band)} (σ of the mean {F3(sdOfMean)})",
            atFloor));

        var silentArms = arms
            .Where(a => a.Role is not ArmRole.Live and not ArmRole.Floor)
            .Where(a => !applicable.Any(c => cells.TryGetValue((c.Id, a.Label), out var s) && s.Presented > 0))
            .Select(a => a.Label)
            .ToList();
        rows.Add(new WiringRow("DeterministicArmsPresent",
            "every offline arm presented at least one item on at least one applicable case. An arm that is silent " +
            "everywhere sits 'at floor' by being broken, not by being constraint-blind.",
            silentArms.Count == 0 ? "every offline arm presented something" : $"SILENT everywhere: {string.Join(", ", silentArms)}",
            silentArms.Count == 0));

        rows.Add(new WiringRow("NoArmThrew",
            "no arm run threw.",
            threw == 0 ? "none threw" : $"{threw} run(s) threw",
            threw == 0));

        if (liveMeasurable)
        {
            bool liveHasCells = applicable.Any(c => cells.ContainsKey((c.Id, ArmLive)));
            rows.Add(new WiringRow("LiveArmObserved",
                "the live column has at least one observation — the persona loop, the adapter and the trace extraction ran.",
                liveHasCells ? "observed" : "NO observation in the live column",
                liveHasCells));
        }

        return rows;
    }

    private static bool DryRunPlumbingHeld(
        IReadOnlyList<Arm> arms,
        IReadOnlyList<StatedNeedCase> applicable,
        IReadOnlyDictionary<(string, string), ConstraintScore> cells)
    {
        bool stubObserved = applicable.Any(c => cells.ContainsKey((c.Id, ArmLive)));
        bool loopRan = arms.All(a => a.Role != ArmRole.Loop)
                    || applicable.Any(c => cells.TryGetValue((c.Id, ArmLoop), out var s) && s.Presented > 0);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ─── DRY-RUN PLUMBING CHECKS (a stub cannot prove anything else) ──────");
        Console.ResetColor();
        Line(applicable.Count > 0, $"{applicable.Count} case(s) derived a non-empty satisfying set.");
        Line(stubObserved, "the stub live arm produced an observation — the case loop, the adapter and the trace extraction ran.");
        Line(loopRan, "the deterministic loop presented at least one item with a stated need in its slot.");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  The stub presents the same two products for every need, so its precision column is NOT a");
        Console.WriteLine("  result. The oracle, the floor band and the offline arms above ARE real measurements of");
        Console.WriteLine("  those arms; only the 'Single Agent' column is a stub.");
        Console.ResetColor();

        return applicable.Count > 0 && stubObserved && loopRan;

        static void Line(bool ok, string text)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"  {(ok ? "✅" : "❌")} {text}");
            Console.ResetColor();
        }
    }

    // ══ Notes ═════════════════════════════════════════════════════════════════════════════

    private static void AddComparisonNotes(
        IReadOnlyList<Arm> arms,
        IReadOnlyList<StatedNeedCase> applicable,
        IReadOnlyDictionary<(string, string), ConstraintScore> cells,
        IReadOnlyDictionary<string, double> floors,
        IReadOnlyDictionary<string, List<int>> kByArm,
        List<string> notes,
        bool liveMeasurable)
    {
        if (applicable.Count == 0) return;

        notes.Add("PRECISION IS k-INVARIANT IN EXPECTATION. A uniform draw of any size scores |S|/N, so arms " +
                  "presenting 3 and arms presenting 12 sit on one scale. This is the channel Eval 02 lacks; it does not " +
                  "replace recall, and an arm can buy precision by presenting ONE item — which is why k is printed. " +
                  "Mean k per arm: " + string.Join("; ", arms
                      .Where(a => kByArm.ContainsKey(a.Label))
                      .Select(a => $"{a.Short} {kByArm[a.Label].Average():F1}")) + ".");

        double Mean(string arm) =>
            applicable.Where(c => cells.ContainsKey((c.Id, arm))).Select(c => cells[(c.Id, arm)].Precision)
                      .DefaultIfEmpty(double.NaN).Average();
        int N(string arm) => applicable.Count(c => cells.ContainsKey((c.Id, arm)));

        double floorMean = applicable.Average(c => floors[c.Id]);

        if (N(ArmLoop) > 0 && N(ArmLoopBlind) > 0)
        {
            int wins = 0, losses = 0, ties = 0;
            foreach (var c in applicable)
            {
                if (!cells.TryGetValue((c.Id, ArmLoop), out var with) || !cells.TryGetValue((c.Id, ArmLoopBlind), out var without)) continue;
                double d = with.Precision - without.Precision;
                if (d > 1e-9) wins++; else if (d < -1e-9) losses++; else ties++;
            }
            int n = wins + losses;
            double p = PairedCoverageReport.ExactTwoSidedSignP(wins, n);
            notes.Add($"THE ONE-OPERAND PAIR — the loop WITH the customer's words ({F3(Mean(ArmLoop))}) vs the SAME loop " +
                      $"asked Eval 02's generic question ({F3(Mean(ArmLoopBlind))}): W/L/T {wins}/{losses}/{ties}, exact two-sided " +
                      $"p = {p:F4} on n = {n}. Nothing varies between these two columns but the utterance, so this " +
                      "difference IS what reading the need bought the deterministic loop. Reported, never gated.");
        }

        notes.Add($"WHAT HISTORY ALONE IS WORTH ON A STATED NEED — Eval 02's oracle (tag join) {F3(Mean(ArmTagJoin))} " +
                  $"and its primary control (single shot) {F3(Mean(ArmSingleShot))} against a mean floor of {F3(floorMean)}. " +
                  "Both read the history and never the words. The tag join scored 1.000 on Eval 02; this is the eval it " +
                  "cannot answer, and the number is whatever it is.");

        notes.Add($"The ORACLE (constraint filter) scores {F3(Mean(ArmOracle))} and reaches the ceiling by construction: " +
                  "it is handed the filter. Every other arm has to BUILD the filter from a shopper's sentence — that " +
                  "is the whole measurement.");

        if (liveMeasurable && N(ArmLive) > 0)
        {
            var silent = applicable.Where(c => cells.TryGetValue((c.Id, ArmLive), out var s) && s.Silent).Select(c => c.Id).ToList();
            if (silent.Count > 0)
                notes.Add($"⚠ The live arm was SILENT on {string.Join(", ", silent)} — presented nothing against a STATED need " +
                          "with a satisfying product in the catalogue. Scored 0.000 and counted as below the floor. This is " +
                          "not the abstention rule: its precondition ('the customer has not described a need') is false here.");

            var slotMisses = applicable
                .Where(c => c.Slots.Count > 1 && cells.TryGetValue((c.Id, ArmLive), out var s) && s.SlotsCovered < s.SlotTotal)
                .Select(c => $"{c.Id} {cells[(c.Id, ArmLive)].SlotsCovered}/{c.Slots.Count}")
                .ToList();
            if (slotMisses.Count > 0)
                notes.Add($"Assembly cases where the live arm covered only some slots: {string.Join(", ", slotMisses)}. " +
                          "Precision does not see a missing slot; slot coverage does.");
        }

        notes.Add("NOT GATED, on purpose: whether the live arm beats the loop, the tag join or anything else. Gating on " +
                  "a win creates an incentive to tune the eval until it happens. The live gate is 'above ITS OWN floor on " +
                  "EVERY applicable case, and never silent' — a bar the artifact under test supplies no input to.");
    }

    // ══ Printing ══════════════════════════════════════════════════════════════════════════

    private static void PrintCaseHeader(StatedNeedCase testCase, IReadOnlyList<Product> satisfying, double floor)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine();
        Console.WriteLine($"  ─── {testCase.Id}  {testCase.PersonaId}  {testCase.Name} ──────────────────────────");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.White;
        foreach (string line in EvalPanel.Wrap("\"" + testCase.Utterance + "\"", 74))
            Console.WriteLine("      " + line);
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (string line in EvalPanel.Wrap(testCase.Note, 74))
            Console.WriteLine("      " + line);
        Console.ResetColor();

        foreach (var slot in testCase.Slots)
        {
            Console.WriteLine($"      slot: {slot.Label}");
            foreach (var constraint in slot.Constraints)
                foreach (string line in EvalPanel.Wrap("· " + constraint.Describe(), 70))
                    Console.WriteLine("        " + line);
        }

        Console.ForegroundColor = satisfying.Count == 0 ? ConsoleColor.Yellow : ConsoleColor.White;
        Console.WriteLine(satisfying.Count == 0
            ? "      satisfying set: EMPTY — NOT APPLICABLE, no arm is scored on this case."
            : $"      satisfying set ({satisfying.Count} of {Catalogue.Default.All.Count}): " +
              string.Join(", ", satisfying.Select(p => $"{p.Id} {Truncate(p.Name, 34)}")));
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"      floor: uniform-draw precision = |S|/N = {satisfying.Count}/{Catalogue.Default.All.Count} = {F3(floor)} " +
                          "(any k; executed below by Broken06)");
        Console.ResetColor();
    }

    private static void PrintTable(
        IReadOnlyList<Arm> arms,
        IReadOnlyList<StatedNeedCase> applicable,
        IReadOnlyDictionary<(string, string), ConstraintScore> cells,
        IReadOnlyDictionary<string, double> floors,
        bool liveMeasurable,
        bool dryRun)
    {
        EvalPanel.Open(dryRun
            ? $"Eval 02b — DRY RUN: the 'live' COLUMN IS A STUB, NOT A RESULT (n = {applicable.Count})"
            : $"Eval 02b — Stated-Need Satisfaction, precision per arm (n = {applicable.Count} applicable cases)");
        EvalPanel.Section("PRECISION = satisfying / presented  ·  ▲/▼ against the case's OWN floor  ·  sat/k");
        EvalPanel.Divider();

        int cell = 9;
        EvalPanel.Row($"  {"case",-7} {"floor",6} " + string.Join("", arms.Select(a => EvalPanel.Fit(a.Short, cell))));

        foreach (var c in applicable)
        {
            double floor = floors[c.Id];
            var line = $"  {c.Id,-7} {F3(floor),6} ";
            foreach (var arm in arms)
            {
                if (!cells.TryGetValue((c.Id, arm.Label), out var s))
                {
                    line += EvalPanel.Fit(arm.Role == ArmRole.Live && !liveMeasurable ? "n/m" : "—", cell);
                    continue;
                }

                string text = arm.Role == ArmRole.Floor
                    ? s.Precision.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    : s.Silent
                        ? "SILENT"
                        : $"{s.Precision:F2}{(s.Precision > floor ? "▲" : "▼")}{s.Satisfied}/{s.Presented}";
                line += EvalPanel.Fit(text, cell);
            }
            EvalPanel.Row(line);
        }

        EvalPanel.Divider();
        var meanLine = $"  {"MEAN",-7} {F3(applicable.Count == 0 ? double.NaN : applicable.Average(c => floors[c.Id])),6} ";
        var nLine = $"  {"n",-7} {"",6} ";
        foreach (var arm in arms)
        {
            var values = applicable.Where(c => cells.ContainsKey((c.Id, arm.Label))).Select(c => cells[(c.Id, arm.Label)].Precision).ToList();
            meanLine += EvalPanel.Fit(values.Count == 0 ? (arm.Role == ArmRole.Live && !liveMeasurable ? "n/m" : "—") : F3(values.Average()), cell);
            nLine += EvalPanel.Fit(values.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), cell);
        }
        EvalPanel.Row(meanLine);
        EvalPanel.Row(nLine);

        EvalPanel.Divider();
        EvalPanel.Section("ARM LEGEND");
        foreach (var arm in arms)
        {
            EvalPanel.Note($"  {EvalPanel.Fit(arm.Short, 11)} = {arm.Label}");
            EvalPanel.Note($"               {arm.Note}");
        }
        if (!liveMeasurable)
            EvalPanel.Row($"  n/m = {CredentialGuard.NotMeasuredBanner}. The live column is absent, not zero.", ConsoleColor.Yellow);
        EvalPanel.Close();
    }

    private static void PrintFloors(
        IReadOnlyList<StatedNeedCase> applicable,
        IReadOnlyDictionary<string, double> floors,
        IReadOnlyDictionary<string, double> executedFloor,
        IReadOnlyDictionary<(string, string), ConstraintScore> cells)
    {
        EvalPanel.Open("Chance floors — STATED (closed form) and EXECUTED (Broken06, uniform draw), per case");
        EvalPanel.Section("COMPUTED FROM THIS CORPUS AT RUN TIME — not quoted from a design document");
        EvalPanel.Divider();
        EvalPanel.Row($"  {"case",-7} {"|S|",4} {"analytic",9} {"executed",9} {"oracle",7}   satisfying SKUs");
        foreach (var c in applicable)
        {
            int s = ConstraintSatisfactionGrader.SatisfyingSet(c).Count;
            string oracle = cells.TryGetValue((c.Id, ArmOracle), out var o) ? F3(o.Precision) : "—";
            string executed = executedFloor.TryGetValue(c.Id, out var e) ? F3(e) : "—";
            EvalPanel.Row($"  {c.Id,-7} {s,4} {F3(floors[c.Id]),9} {executed,9} {oracle,7}   " +
                          string.Join(", ", ConstraintSatisfactionGrader.SatisfyingSet(c).Select(p => p.Id)));
        }
        EvalPanel.Divider();
        EvalPanel.Note("  analytic = |S|/N, the expected precision of a uniform draw of ANY size from the N-product");
        EvalPanel.Note($"  catalogue. executed = the mean of {FloorDraws} seeded draws of {Broken06_ConstraintBlindRecommender.DrawSize} through the real");
        EvalPanel.Note("  harness and grader. The two must agree within the band printed in the wiring panel.");
        EvalPanel.Close();
    }

    private static void PrintWiring(IReadOnlyList<WiringRow> rows)
    {
        EvalPanel.Open("Wiring — the checks that make the table above mean anything");
        EvalPanel.Section("A CONTROL THAT PASSES IS A WIRING FAULT, NOT A GOOD AGENT");
        EvalPanel.Divider();
        foreach (var row in rows)
        {
            EvalPanel.Row($"  {(row.Ok ? "✅ held" : "❌ FAILED")}  {row.Name}", row.Ok ? ConsoleColor.Green : ConsoleColor.Red);
            EvalPanel.Note("      expected: " + row.Expectation);
            EvalPanel.Row("      observed: " + row.Observed, row.Ok ? ConsoleColor.DarkGreen : ConsoleColor.Red);
        }
        EvalPanel.Divider();
        bool all = rows.All(r => r.Ok);
        EvalPanel.Row(all
            ? "  ✅ Every wiring check held. The grader accepts true satisfiers and rejects a blind draw."
            : "  ❌ A wiring check failed. Treat every number above as UNPROVEN.",
            all ? ConsoleColor.Green : ConsoleColor.Red);
        EvalPanel.Close();
    }

    private static void PrintVerdict(
        IReadOnlyList<StatedNeedCase> applicable,
        IReadOnlyDictionary<(string, string), ConstraintScore> cells,
        IReadOnlyDictionary<string, double> floors,
        bool liveMeasurable,
        bool dryRun,
        bool liveAboveFloor,
        IReadOnlyList<string> notes)
    {
        Console.WriteLine();
        if (liveMeasurable)
        {
            Console.ForegroundColor = liveAboveFloor ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"  {(liveAboveFloor ? "✅" : "❌")} GATE — the live arm is above ITS OWN floor on EVERY applicable case, and silent on none." +
                              (dryRun ? "  (a STUB — expected to fail; the exit code is the plumbing's)" : ""));
            Console.ResetColor();

            foreach (var c in applicable)
            {
                bool has = cells.TryGetValue((c.Id, ArmLive), out var s);
                bool ok = has && !s.Silent && s.Precision > floors[c.Id];
                Console.ForegroundColor = ok ? ConsoleColor.DarkGreen : ConsoleColor.Red;
                Console.WriteLine($"       {(ok ? "▲" : "▼")} {c.Id} {c.Name,-16} live {(has ? F3(s.Precision) : "—")} " +
                                  $"({(has ? $"{s.Satisfied}/{s.Presented}" : "no observation")}) vs floor {F3(floors[c.Id])}" +
                                  (has && s.Silent ? "  SILENT" : ""));
                Console.ResetColor();
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ⚠️  GATE — UNDECIDABLE: the live arm was not measured. An undecidable gate is not a pass.");
            Console.ResetColor();
        }

        foreach (string note in notes)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            foreach (string line in EvalPanel.Wrap("  · " + note, EvalPanel.BoxWidth))
                Console.WriteLine(line);
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    private static void PrintLiveNotMeasured()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  ⚠️  Eval 02b — {CredentialGuard.NotMeasuredBanner}.");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("     NOT MEASURED: the live agent's constraint-satisfaction precision.");
        Console.WriteLine("     The offline arms above were run and their numbers are real — about THOSE arms.");
        Console.WriteLine("     Nothing was substituted into the live column, and no verdict about the agent exists.");
        Console.WriteLine();
        Console.WriteLine("     Set AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY (AZURE_OPENAI_DEPLOYMENT is optional");
        Console.WriteLine($"     and defaults to {Config.PreferredDeployment}) — or add --dry-run to exercise the live path against a stub.");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"     Exit code {CredentialGuard.NotMeasuredExitCode}, never 0. An eval whose subject was not measured has not passed.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintMode(bool dryRun, bool liveMeasurable)
    {
        if (dryRun)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  🧪 DRY RUN — the live arm is a stub that presents the same two products for every need.");
            Console.WriteLine("     Nothing spent, no snapshot written. Its precision column is NOT a result. Every other");
            Console.WriteLine("     arm is offline and its numbers are real measurements of that arm.");
            Console.ResetColor();
        }
        else if (liveMeasurable)
        {
            Config.PrintAzureTarget();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠️  No credentials. The live column will read n/m — {CredentialGuard.NotMeasuredBanner} — and this");
            Console.WriteLine($"     run will exit {CredentialGuard.NotMeasuredExitCode}. The oracle, the loop, the references and the floor run anyway:");
            Console.WriteLine("     they need no model, and their numbers are facts about those arms, not about the agent.");
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 02b — Stated-Need Satisfaction (constraint precision, code-checked)    ║
║   Twelve multi-constraint needs · the eval a tag join cannot answer           ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static StatedNeedSnapshot ToSnapshot(
        IReadOnlyList<Arm> arms,
        IReadOnlyList<StatedNeedCase> applicable,
        IReadOnlyList<StatedNeedCase> inapplicable,
        IReadOnlyDictionary<(string, string), ConstraintScore> cells,
        IReadOnlyDictionary<string, double> floors,
        int liveReps) =>
        new()
        {
            Label = $"Eval 02b — Stated-Need Satisfaction (n = {applicable.Count}, {liveReps} rep(s) on the live arm)",
            Arms = [.. arms.Select(a => a.Label)],
            ApplicableCases = applicable.Count,
            InapplicableCases = [.. inapplicable.Select(c => c.Id)],
            MeanPrecisionByArm = arms.ToDictionary(
                a => a.Label,
                a => applicable.Where(c => cells.ContainsKey((c.Id, a.Label)))
                               .Select(c => cells[(c.Id, a.Label)].Precision).DefaultIfEmpty(double.NaN).Average(),
                StringComparer.Ordinal),
            MeanFloor = applicable.Count == 0 ? double.NaN : applicable.Average(c => floors[c.Id]),
            LiveArmMeasured = applicable.Any(c => cells.ContainsKey((c.Id, ArmLive))),
            Cells =
            [
                .. from c in applicable
                   from a in arms
                   where cells.ContainsKey((c.Id, a.Label))
                   let s = cells[(c.Id, a.Label)]
                   select new StatedNeedCellSnapshot(c.Id, c.PersonaId, a.Label, s.Precision, s.Presented, s.Satisfied, s.Silent, s.SlotsCovered, floors[c.Id])
            ],
        };

    private static string F3(double value) => EvalPanel.F3(value);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
