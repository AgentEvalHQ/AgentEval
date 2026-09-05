// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using AgentEval.MAF;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Evals.Adapters;
using Microsoft.Agents.AI;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Eval 02c — Held-Out Next Purchase. Leave-one-out hit-rate@k over the seeded order lines: the
/// one offline gold a recommender-systems engineer expects, and the one this suite did not have.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is.</b> For every customer with at least three order lines, the most recent
/// first-time purchase for their own use is HIDDEN; every arm is run on the remaining history
/// with Eval 02's canonical question; a hit is the hidden SKU — or, more coarsely, a product in
/// its leaf category — appearing in the first <see cref="K"/> items presented. Nothing in the
/// gold was authored for this eval: the target is a line that already existed in
/// <c>Personas.cs</c>, chosen by one stated rule (<see cref="HeldOutTargets"/>). That is what
/// makes it non-circular where Eval 02's planted-tag gold is not.
/// </para>
/// <para>
/// <b>k is the DECLARED budget, never the arm's.</b> Hit-rate@k rises with k, and the arms in
/// this suite present anywhere from 0 to 12 items. Every list is cut to <see cref="K"/> in
/// presentation order before the hit is read, and the floor — <c>k / pool</c> for the SKU, the
/// at-least-one-hit probability for the leaf — is derived at that same k and EXECUTED by a
/// uniform draw from the same pool. The arm's own-k hit is printed alongside, labelled
/// k-confounded, so it cannot be mistaken for the comparison.
/// </para>
/// <para>
/// <b>What a 79-line synthetic corpus lets this number mean, stated plainly.</b> Thirteen
/// targets, one per customer; a single hit moves an arm's rate by 0.077, and the 95% interval on
/// any rate here spans most of the unit interval. The histories were authored to plant three
/// reachable latent interests each, not sampled from a log, so "the next thing this customer
/// bought" is whatever the author wrote last — for five customers that was a replacement or a
/// replenishment repeat, and the first-time rule targets an earlier line for them, printed
/// beside the alternative. One target is out of stock and no stock-gated arm can hit it. This
/// eval can tell an arm that reads history from one that does not; it cannot rank two working
/// architectures, and its verdict is therefore the WIRING's, never the winner's.
/// </para>
/// <para>
/// <b>The hold-out seam is verified, not trusted.</b> <c>UserProfiles.BeginOverride</c> is an
/// <c>AsyncLocal</c>; three checks confirm the hidden line never reached an arm: a probe across an
/// awaited task, the loop's own <c>OwnedProductIds</c> (which must not contain the hidden SKU),
/// and the uniform-draw control's pool size (which must equal catalogue minus visible history).
/// </para>
/// <para>
/// <b>Credentials.</b> As Eval 02b: the offline arms run and print without a key, the live column
/// reads <c>NOT MEASURED — no credentials</c>, exit 3. Nothing is substituted for the agent.
/// </para>
/// </remarks>
public static class Eval02c_HeldOutNextPurchase
{
    /// <summary>The declared presentation budget every arm is cut to. The suite's degenerate draw size.</summary>
    public const int K = ChanceFloors.DegenerateDrawSize;

    /// <summary>Repetitions per target for the live arm.</summary>
    public const int Reps = 3;

    /// <summary>Repetitions per target for the live arm under <c>--quick</c>.</summary>
    public const int QuickReps = 1;

    /// <summary>How many uniform draws per target the executed floor averages over.</summary>
    public const int FloorDraws = 50;

    /// <summary>Width of the band the executed floor must land in, in standard deviations of its own mean.</summary>
    public const double FloorBandSigmas = 3.0;

    /// <summary>The live agent's label. Same key as Eval 02.</summary>
    public const string ArmLive = CoverageArms.Live;

    /// <summary>Demo 2's loop on its deterministic path.</summary>
    public const string ArmLoop = DiscoveryLoopAdapter.ArmLabel;

    /// <summary>Eval 02's primary control.</summary>
    public const string ArmSingleShot = CoverageArms.SingleShot;

    /// <summary>Eval 02's oracle. Here it has no gold to read — the hidden line is not a tag.</summary>
    public const string ArmTagJoin = CoverageArms.TagJoin;

    /// <summary>The bestseller list.</summary>
    public const string ArmPopularity = CoverageArms.Popularity;

    /// <summary>The executed floor: a uniform draw from the pool.</summary>
    public const string ArmFloor = "Uniform draw from the pool";

    /// <summary>The key a one-case probe writes to. NEVER the full-cohort key.</summary>
    public const string ProbeSnapshotKey = OfflineSnapshotStore.HeldOutKey + "_probe";

    private enum ArmRole { Live, Loop, Reference, Floor }

    private sealed record Arm(
        string Label,
        string Short,
        ArmRole Role,
        Func<CoverageArmContext, int, IEvaluableAgent>? Factory,
        string Note);

    private sealed record HitCell(double Sku, double Leaf, double SkuOwnK, double LeafOwnK, double PresentedRaw, bool Silent);

    private sealed record WiringRow(string Name, string Expectation, string Observed, bool Ok);

    /// <summary>Runs the eval.</summary>
    /// <param name="quick">One live repetition instead of three.</param>
    /// <param name="dryRun">Stub the live arm; spend nothing; assert the plumbing.</param>
    /// <param name="onlyCase">
    /// Restrict the run to one customer id (<c>USR-NB-01</c> … ) — the one-item real run that is
    /// stage two of the three-stage protocol. The snapshot then goes to
    /// <see cref="ProbeSnapshotKey"/> and never to the full-cohort key.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 the wiring held and the live arm was measured, 1 a wiring check failed, 3 the live arm was not measured.</returns>
    public static async Task<int> RunAsync(
        bool quick = false, bool dryRun = false, string? onlyCase = null, CancellationToken ct = default)
    {
        PrintHeader();

        IReadOnlyList<HeldOutTarget> targets;
        try
        {
            targets = HeldOutTargets.Derive();
        }
        catch (InvalidOperationException ex)
        {
            EvalPrinter.PrintRefusal("Eval 02c refused to run.", ex.Message);
            return 1;
        }

        if (targets.Count == 0)
        {
            EvalPrinter.PrintRefusal("Eval 02c refused to run.", "The hold-out rule derived no target from the seeded histories.");
            return 1;
        }

        IReadOnlyList<HeldOutTarget> allTargets = targets;
        if (onlyCase is not null)
        {
            targets = [.. allTargets.Where(t => string.Equals(t.PersonaId, onlyCase, StringComparison.OrdinalIgnoreCase))];
            if (targets.Count == 0)
            {
                EvalPrinter.PrintRefusal(
                    $"--only {onlyCase} matches no hold-out target.",
                    "Target customer ids: " + string.Join(", ", allTargets.Select(t => t.PersonaId)) + ".");
                return 2;
            }
        }

        bool liveMeasurable = dryRun || Config.IsConfigured;
        PrintMode(dryRun, liveMeasurable, targets.Count);
        PrintScope(targets);

        if (onlyCase is not null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  🔬 ONE-CASE PROBE — {targets[0].PersonaId} only. Stage two of the three-stage protocol.");
            Console.WriteLine($"     n = 1 of {allTargets.Count}: no hit-rate is reachable, the snapshot goes to '{ProbeSnapshotKey}',");
            Console.WriteLine("     never to the full-cohort key. What this probe can show is that the live arm ran and what it presented.");
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
        var runnable = arms.Where(a => a.Factory is not null).ToList();
        var cells = new Dictionary<(string PersonaId, string Arm), HitCell>();
        var notes = new List<string>();
        int threw = 0;
        int probesOk = 0, probes = 0;
        int loopSawHidden = 0, loopObserved = 0;
        int poolMismatches = 0, poolObserved = 0;

        foreach (Arm absent in arms.Where(a => a.Factory is null))
            notes.Add($"Arm '{absent.Label}' is DECLARED ABSENT and was not run. {absent.Note}");

        foreach (HeldOutTarget target in targets)
        {
            PrintTargetHeader(target);

            using (UserProfiles.BeginOverride(target.Visible))
            {
                // Probe 1: the override flows into an awaited child context — the shape every arm's
                // reads take. A seam that only worked on the calling thread would be a seam that
                // silently showed the loop's executors the full history.
                probes++;
                bool flowed = await Task.Run(
                    () => UserProfiles.Find(target.PersonaId)?.PurchaseCount == target.Visible.PurchaseCount, ct)
                    .ConfigureAwait(false);
                if (flowed) probesOk++;

                foreach (Arm arm in runnable)
                {
                    if (arm.Role == ArmRole.Live && !liveMeasurable) continue;

                    int armReps = arm.Role switch
                    {
                        ArmRole.Live => liveReps,
                        ArmRole.Floor => FloorDraws,
                        _ => 1,
                    };

                    var hits = new List<HitScore>(armReps);
                    for (int rep = 1; rep <= armReps; rep++)
                    {
                        IEvaluableAgent agent = arm.Factory!(context, rep);
                        HitScore? hit = await ScoreAsync(
                            target, agent, harness, options, arm.Label, rep, armReps,
                            print: arm.Role != ArmRole.Floor, ct,
                            ledger: arm.Role == ArmRole.Live && !dryRun ? ledger : null).ConfigureAwait(false);

                        if (hit is null)
                        {
                            threw++;
                            notes.Add($"{target.PersonaId} · {arm.Label} · rep {rep} THREW and was EXCLUDED.");
                            continue;
                        }

                        hits.Add(hit.Value);

                        // Probe 2: the loop's own owned set, read off its final state. The hidden SKU is
                        // a first-time purchase, so it may appear here ONLY if the loop saw the hidden line.
                        if (agent is RealDiscoveryLoopArm loop && loop.LastResult is { } run)
                        {
                            loopObserved++;
                            if (run.State.OwnedProductIds.Contains(target.Target.Id)) loopSawHidden++;
                        }

                        // Probe 3: the draw control's pool must be catalogue minus VISIBLE history.
                        if (agent is Broken06_ConstraintBlindRecommender draw)
                        {
                            poolObserved++;
                            if (draw.LastPoolSize != target.PoolSize) poolMismatches++;
                        }
                    }

                    if (hits.Count == 0)
                    {
                        notes.Add($"{target.PersonaId} · {arm.Label}: EVERY run threw; no observation for this cell.");
                        continue;
                    }

                    cells[(target.PersonaId, arm.Label)] = new HitCell(
                        Sku: hits.Average(h => h.SkuHitAtK ? 1.0 : 0.0),
                        Leaf: hits.Average(h => h.LeafHitAtK ? 1.0 : 0.0),
                        SkuOwnK: hits.Average(h => h.SkuHitOwnK ? 1.0 : 0.0),
                        LeafOwnK: hits.Average(h => h.LeafHitOwnK ? 1.0 : 0.0),
                        PresentedRaw: hits.Average(h => (double)h.PresentedRaw),
                        Silent: hits.All(h => h.Silent));

                    if (arm.Role == ArmRole.Floor)
                    {
                        var c = cells[(target.PersonaId, arm.Label)];
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"      {arm.Label,-46} {FloorDraws} draws  executed sku {F3(c.Sku)} leaf {F3(c.Leaf)}  " +
                                          $"analytic sku {F3(target.SkuFloor(K))} leaf {F3(target.LeafFloor(K))}");
                        Console.ResetColor();
                    }
                }
            }
        }

        PrintTable(runnable, targets, cells, liveMeasurable, dryRun);
        PrintFloors(targets, cells);
        ledger.Print(Config.Model, "Eval 02c");

        var wiring = CheckWiring(runnable, targets, cells, threw, probes, probesOk, loopObserved, loopSawHidden, poolObserved, poolMismatches, liveMeasurable);
        PrintWiring(wiring);

        AddNotes(runnable, targets, cells, notes, liveMeasurable);
        PrintNotes(notes);
        PrintWhatThisSupports(targets.Count);

        bool wiringHeld = wiring.All(w => w.Ok);

        if (!wiringHeld)
        {
            EvalPanel.Line("  ❌ EVAL 02c — a WIRING check failed (exit code 1). Treat every number above as unproven.", ConsoleColor.Red);
            return 1;
        }

        if (dryRun)
        {
            bool plumbing = DryRunPlumbingHeld(runnable, targets, cells);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  📁 Snapshot NOT written — a dry run must not leave a result behind.");
            Console.ResetColor();
            return plumbing ? 0 : 1;
        }

        if (!liveMeasurable)
        {
            PrintLiveNotMeasured();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  📁 Snapshot NOT written — the live arm was not measured.");
            Console.ResetColor();
            return CredentialGuard.NotMeasuredExitCode;
        }

        string snapshotKey = onlyCase is null ? OfflineSnapshotStore.HeldOutKey : ProbeSnapshotKey;
        string path = OfflineSnapshotStore.Save(snapshotKey, ToSnapshot(runnable, targets, cells, liveReps));
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  📁 Snapshot saved → {path}");
        if (onlyCase is not null)
            Console.WriteLine($"     (probe key '{ProbeSnapshotKey}' — the full-cohort record at '{OfflineSnapshotStore.HeldOutKey}' is untouched.)");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ✅ EVAL 02c — the wiring held and the live arm was measured. Its hit-rate is REPORTED, not gated:");
        Console.WriteLine($"     n = {targets.Count} target(s) on a hand-authored corpus cannot carry a verdict about the agent in either direction.");
        Console.ResetColor();
        return 0;
    }

    // ══ Arms ══════════════════════════════════════════════════════════════════════════════

    private static IReadOnlyList<Arm> BuildArms()
    {
        bool loopBound = DiscoveryLoopAdapter.IsBound;

        return
        [
            new Arm(ArmLive, "live", ArmRole.Live,
                (ctx, _) => ctx.LiveAgentFactory(),
                "The shipped single agent on the visible history. Repeated; stochastic."),

            new Arm(ArmLoop, "loop", ArmRole.Loop,
                loopBound ? (ctx, _) => DiscoveryLoopAdapter.Create(ctx)! : null,
                loopBound ? "Demo 2's loop on its deterministic path. Zero model calls." : DiscoveryLoopAdapter.AbsenceReason),

            new Arm(ArmSingleShot, "1-shot", ArmRole.Reference,
                (ctx, _) => new Broken03_SingleShotWorkflow(ctx.Retriever),
                "One retrieval pass from the dominant department of the visible history."),

            new Arm(ArmTagJoin, "tag-join", ArmRole.Reference,
                (_, _) => new Baseline_TagJoin(),
                "Eval 02's oracle. It joins on the visible history's tags; the hidden line is not a tag, so here it is an entrant."),

            new Arm(ArmPopularity, "popular", ArmRole.Reference,
                (_, _) => new Broken04_PopularityAgent(),
                "The bestseller list, ignoring the customer. Note it does NOT exclude owned items."),

            new Arm(ArmFloor, "uniform", ArmRole.Floor,
                (_, rep) => new Broken06_ConstraintBlindRecommender(rep, excludeOwned: true),
                $"A uniform draw of {K} from catalogue minus the visible history, {FloorDraws}× per target. The floor, executed."),
        ];
    }

    // ══ One graded turn ═══════════════════════════════════════════════════════════════════

    private static async Task<HitScore?> ScoreAsync(
        HeldOutTarget target,
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
            Name = $"{target.PersonaId} · hold-out {target.Hidden.Id} · {armLabel} · rep {rep}/{reps}",
            Input = target.Prompt,
            PassingScore = 0,
        };

        TestResult result;
        using (EvalRuntime.BeginTurn())
        {
            result = await harness.RunEvaluationAsync(agent, tc, options, ct).ConfigureAwait(false);
        }

        // Only ever non-null for the LIVE arm: an offline arm costs nothing, and counting its turn
        // would make every per-turn figure in the ledger a different question's answer.
        ledger?.Record(result.Performance);

        if (result.HasError)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"      {armLabel,-46} rep {rep}/{reps} ❌ threw: {result.Error?.Message}");
            Console.ResetColor();
            return null;
        }

        var presented = PresentedCall.FromToolUsage(result.ToolUsage);
        var hit = HeldOutHitGrader.Grade(target, presented, K);

        if (print)
        {
            Console.ForegroundColor = hit.Silent ? ConsoleColor.Red
                                    : hit.SkuHitAtK ? ConsoleColor.Green
                                    : hit.LeafHitAtK ? ConsoleColor.DarkGreen : ConsoleColor.DarkGray;
            Console.WriteLine($"      {armLabel,-46} {(reps > 1 ? $"rep {rep}/{reps}" : "deterministic"),-14} " +
                              $"@{K}: sku {(hit.SkuHitAtK ? "HIT" : "miss")} leaf {(hit.LeafHitAtK ? "HIT" : "miss")}  " +
                              $"own-k={hit.PresentedRaw}: sku {(hit.SkuHitOwnK ? "hit" : "miss")} leaf {(hit.LeafHitOwnK ? "hit" : "miss")}" +
                              (hit.Silent ? "  ⚠ SILENT" : "") +
                              (hit.Phantom > 0 ? $"  ⚠ phantom {hit.Phantom}" : ""));
            Console.ResetColor();
            if (hit.TopK.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"        top-{K}: {string.Join(", ", hit.TopK)}");
                Console.ResetColor();
            }
        }

        return hit;
    }

    // ══ Wiring ════════════════════════════════════════════════════════════════════════════

    private static IReadOnlyList<WiringRow> CheckWiring(
        IReadOnlyList<Arm> arms,
        IReadOnlyList<HeldOutTarget> targets,
        IReadOnlyDictionary<(string, string), HitCell> cells,
        int threw,
        int probes, int probesOk,
        int loopObserved, int loopSawHidden,
        int poolObserved, int poolMismatches,
        bool liveMeasurable)
    {
        var rows = new List<WiringRow>();

        rows.Add(new WiringRow("HoldOutFlowsToChildren",
            "inside every hold-out scope, a lookup on an awaited child task sees the REDUCED history. The seam is an " +
            "AsyncLocal; if it did not flow, the loop's executors would read the full history and every hit would be a leak.",
            $"{probesOk} of {probes} probes saw the reduced history",
            probes > 0 && probesOk == probes));

        bool loopBound = arms.Any(a => a.Role == ArmRole.Loop);
        rows.Add(new WiringRow("HiddenLineInvisibleToLoop",
            "the loop's own OwnedProductIds, read off its final state, never contains the hidden SKU. The hidden line " +
            "is a first-time purchase, so its SKU can be 'owned' only if the loop saw the line it was not supposed to.",
            loopBound
                ? $"{loopSawHidden} of {loopObserved} loop runs had the hidden SKU in OwnedProductIds"
                : "loop not bound — no loop run to check (the row is a pass only because there was nothing to leak)",
            !loopBound || (loopObserved > 0 && loopSawHidden == 0)));

        rows.Add(new WiringRow("UniformDrawPoolIsVisibleOnly",
            "the uniform-draw control's pool equals catalogue minus the VISIBLE history on every target — the pool " +
            "the floor k/pool is stated over.",
            $"{poolMismatches} mismatch(es) in {poolObserved} draws",
            poolObserved > 0 && poolMismatches == 0));

        // The executed floor vs the closed form, SKU and leaf, within a binomial band of the mean.
        double analyticSku = targets.Average(t => t.SkuFloor(K));
        double analyticLeaf = targets.Average(t => t.LeafFloor(K));
        bool floorObserved = targets.All(t => cells.ContainsKey((t.PersonaId, ArmFloor)));
        double executedSku = floorObserved ? targets.Average(t => cells[(t.PersonaId, ArmFloor)].Sku) : double.NaN;
        double executedLeaf = floorObserved ? targets.Average(t => cells[(t.PersonaId, ArmFloor)].Leaf) : double.NaN;
        double sdSku = Math.Sqrt(targets.Sum(t => { double p = t.SkuFloor(K); return p * (1 - p) / FloorDraws; })) / targets.Count;
        double sdLeaf = Math.Sqrt(targets.Sum(t => { double p = t.LeafFloor(K); return p * (1 - p) / FloorDraws; })) / targets.Count;
        bool skuAtFloor = floorObserved && Math.Abs(executedSku - analyticSku) <= FloorBandSigmas * sdSku;
        bool leafAtFloor = floorObserved && Math.Abs(executedLeaf - analyticLeaf) <= FloorBandSigmas * sdLeaf;
        rows.Add(new WiringRow("FloorControlAtFloor",
            $"the uniform draw's executed hit-rate@{K} over {FloorDraws} draws × {targets.Count} targets lands within " +
            $"±{FloorBandSigmas:0}σ of the closed form, on the SKU (k/pool) and on the leaf (at-least-one-hit).",
            $"sku executed {F3(executedSku)} vs {F3(analyticSku)} (band ±{F3(FloorBandSigmas * sdSku)}) · " +
            $"leaf executed {F3(executedLeaf)} vs {F3(analyticLeaf)} (band ±{F3(FloorBandSigmas * sdLeaf)})",
            skuAtFloor && leafAtFloor));

        var silentArms = arms
            .Where(a => a.Role is not ArmRole.Live and not ArmRole.Floor)
            .Where(a => !targets.Any(t => cells.TryGetValue((t.PersonaId, a.Label), out var c) && c.PresentedRaw > 0))
            .Select(a => a.Label)
            .ToList();
        rows.Add(new WiringRow("DeterministicArmsPresent",
            "every offline arm presented at least one item on at least one target.",
            silentArms.Count == 0 ? "every offline arm presented something" : $"SILENT everywhere: {string.Join(", ", silentArms)}",
            silentArms.Count == 0));

        rows.Add(new WiringRow("NoArmThrew",
            "no arm run threw.",
            threw == 0 ? "none threw" : $"{threw} run(s) threw",
            threw == 0));

        if (liveMeasurable)
        {
            bool liveHasCells = targets.Any(t => cells.ContainsKey((t.PersonaId, ArmLive)));
            rows.Add(new WiringRow("LiveArmObserved",
                "the live column has at least one observation.",
                liveHasCells ? "observed" : "NO observation in the live column",
                liveHasCells));
        }

        return rows;
    }

    private static bool DryRunPlumbingHeld(
        IReadOnlyList<Arm> arms,
        IReadOnlyList<HeldOutTarget> targets,
        IReadOnlyDictionary<(string, string), HitCell> cells)
    {
        bool stubObserved = targets.Any(t => cells.ContainsKey((t.PersonaId, ArmLive)));
        bool loopRan = arms.All(a => a.Role != ArmRole.Loop)
                    || targets.Any(t => cells.TryGetValue((t.PersonaId, ArmLoop), out var c) && c.PresentedRaw > 0);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ─── DRY-RUN PLUMBING CHECKS (a stub cannot prove anything else) ──────");
        Console.ResetColor();
        Line(targets.Count > 0, $"{targets.Count} hold-out target(s) derived by rule.");
        Line(stubObserved, "the stub live arm produced an observation inside a hold-out scope.");
        Line(loopRan, "the deterministic loop presented at least one item on a reduced history.");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  The stub presents the same two products for every customer, so its hit column is NOT a");
        Console.WriteLine("  result. The floor band, the hold-out probes and the offline arms above ARE real.");
        Console.ResetColor();

        return targets.Count > 0 && stubObserved && loopRan;

        static void Line(bool ok, string text)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"  {(ok ? "✅" : "❌")} {text}");
            Console.ResetColor();
        }
    }

    // ══ Notes ═════════════════════════════════════════════════════════════════════════════

    private static void AddNotes(
        IReadOnlyList<Arm> arms,
        IReadOnlyList<HeldOutTarget> targets,
        IReadOnlyDictionary<(string, string), HitCell> cells,
        List<string> notes,
        bool liveMeasurable)
    {
        double Rate(string arm, Func<HitCell, double> pick) =>
            targets.Where(t => cells.ContainsKey((t.PersonaId, arm))).Select(t => pick(cells[(t.PersonaId, arm)]))
                   .DefaultIfEmpty(double.NaN).Average();

        notes.Add($"k = {K} for EVERY arm, cut in presentation order. Own-k hits are printed per turn and labelled " +
                  "k-confounded; they are not on the table and not in any comparison. Mean own-k per arm: " +
                  string.Join("; ", arms.Where(a => targets.Any(t => cells.ContainsKey((t.PersonaId, a.Label))))
                                        .Select(a => $"{a.Short} {Rate(a.Label, c => c.PresentedRaw):F1}")) + ".");

        var oos = targets.Where(t => !t.TargetInStock).ToList();
        if (oos.Count > 0)
            notes.Add($"Target OUT OF STOCK for {string.Join(", ", oos.Select(t => $"{t.PersonaId} ({t.Target.Id})"))}: every " +
                      "arm that gates on stock cannot hit the SKU, and the leaf only if another product shares it. Counted " +
                      "as a miss for every arm alike — it depresses all rates equally and is a fact about the corpus.");

        var alt = targets
            .Where(t => t.AlternativeMostRecent is not null)
            .Select(t => (Target: t, Recent: t.AlternativeMostRecent!))
            .ToList();
        if (alt.Count > 0)
            notes.Add($"For {alt.Count} customer(s) the most recent line of ANY intent is NOT the target: " +
                      string.Join("; ", alt.Select(x => $"{x.Target.PersonaId} most recent {x.Recent.Id} ({x.Recent.ProductId}), target {x.Target.Hidden.Id}")) +
                      ". Those are replacement or replenishment repeats of an owned SKU; a discovery arm excludes owned SKUs by " +
                      "construction, so targeting them would measure the exclusion rule.");

        notes.Add($"Popularity does not exclude owned items; the pool floor does. That is why the bestseller row can " +
                  "sit below the uniform draw without being 'worse than random' about anything but ownership.");

        if (liveMeasurable)
        {
            var silent = targets.Where(t => cells.TryGetValue((t.PersonaId, ArmLive), out var c) && c.Silent).Select(t => t.PersonaId).ToList();
            if (silent.Count > 0)
                notes.Add($"The live arm was SILENT on {string.Join(", ", silent)}. On the canonical history question the " +
                          "shipped prompt's abstention rule CAN legitimately fire (fewer than two independent signals); a " +
                          "silent turn is a miss here because a hit was possible, and it is flagged rather than excused.");
        }
    }

    // ══ Printing ══════════════════════════════════════════════════════════════════════════

    private static void PrintScope(IReadOnlyList<HeldOutTarget> targets)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  {targets.Count} hold-out targets from {Personas.Purchases.Count} order lines over {UserProfiles.All.Count} customers " +
                          $"(customers with fewer than {HeldOutTargets.MinimumPurchases} lines are skipped).");
        Console.WriteLine("  Rule: classify the FULL history; hide the most recent ForSelf line whose SKU appears nowhere earlier.");
        Console.WriteLine($"  Budget k = {K} for every arm; floor = k/pool on the SKU, at-least-one-hit on the leaf, pool = catalogue");
        Console.WriteLine("  minus the visible history. Every arm sees Eval 02's canonical question on the REDUCED history.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintTargetHeader(HeldOutTarget target)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine();
        Console.WriteLine($"  ─── {target.PersonaId}  {target.Name} ──────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"      hidden : {target.Hidden.Id} {target.Hidden.PurchasedOn:yyyy-MM-dd}  {target.Target.Id} {target.Target.Name}");
        Console.WriteLine($"      leaf   : {target.TargetLeaf}   in stock: {(target.TargetInStock ? "yes" : "NO — unreachable for stock-gated arms")}");
        Console.WriteLine($"      visible: {target.Visible.PurchaseCount} of {target.Visible.PurchaseCount + 1} lines");
        if (target.AlternativeMostRecent is { } alt)
            Console.WriteLine($"      note   : most recent line of any intent is {alt.Id} ({alt.ProductId}), a repeat; the rule targets {target.Hidden.Id}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"      floor  : pool {target.PoolSize} → sku {K}/{target.PoolSize} = {F3(target.SkuFloor(K))}, " +
                          $"leaf ({target.LeafCarriersInPool} carriers) {F3(target.LeafFloor(K))}");
        Console.ResetColor();
    }

    private static void PrintTable(
        IReadOnlyList<Arm> arms,
        IReadOnlyList<HeldOutTarget> targets,
        IReadOnlyDictionary<(string, string), HitCell> cells,
        bool liveMeasurable,
        bool dryRun)
    {
        EvalPanel.Open(dryRun
            ? $"Eval 02c — DRY RUN: the 'live' COLUMN IS A STUB, NOT A RESULT (n = {targets.Count})"
            : $"Eval 02c — Held-Out Next Purchase, hit-rate@{K} (n = {targets.Count} targets, one per customer)");
        EvalPanel.Section($"cell = sku/leaf hit within the first {K} presented  ·  1 = hit, · = miss  ·  reps averaged");
        EvalPanel.Divider();

        int cell = 9;
        EvalPanel.Row($"  {"customer",-10} {"floor",7} " + string.Join("", arms.Select(a => EvalPanel.Fit(a.Short, cell))));

        foreach (var t in targets)
        {
            var line = $"  {t.PersonaId,-10} {F3(t.SkuFloor(K)),7} ";
            foreach (var arm in arms)
            {
                if (!cells.TryGetValue((t.PersonaId, arm.Label), out var c))
                {
                    line += EvalPanel.Fit(arm.Role == ArmRole.Live && !liveMeasurable ? "n/m" : "—", cell);
                    continue;
                }

                string text = arm.Role == ArmRole.Floor
                    ? $"{c.Sku:F2}/{c.Leaf:F2}"
                    : c.Silent ? "SILENT" : $"{Mark(c.Sku)}/{Mark(c.Leaf)}";
                line += EvalPanel.Fit(text, cell);
            }
            EvalPanel.Row(line);
        }

        EvalPanel.Divider();
        var skuLine = $"  {"sku@" + K,-10} {F3(targets.Average(t => t.SkuFloor(K))),7} ";
        var leafLine = $"  {"leaf@" + K,-10} {F3(targets.Average(t => t.LeafFloor(K))),7} ";
        var nLine = $"  {"n",-10} {"",7} ";
        foreach (var arm in arms)
        {
            var have = targets.Where(t => cells.ContainsKey((t.PersonaId, arm.Label))).ToList();
            string missing = arm.Role == ArmRole.Live && !liveMeasurable ? "n/m" : "—";
            skuLine += EvalPanel.Fit(have.Count == 0 ? missing : F3(have.Average(t => cells[(t.PersonaId, arm.Label)].Sku)), cell);
            leafLine += EvalPanel.Fit(have.Count == 0 ? missing : F3(have.Average(t => cells[(t.PersonaId, arm.Label)].Leaf)), cell);
            nLine += EvalPanel.Fit(have.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), cell);
        }
        EvalPanel.Row(skuLine);
        EvalPanel.Row(leafLine);
        EvalPanel.Row(nLine);
        EvalPanel.Note("  the 'floor' column and the two rate rows' floor cells are the ANALYTIC floors; the 'uniform'");
        EvalPanel.Note("  column is the same floor EXECUTED. Read an arm against both.");

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

        static string Mark(double v) => v >= 0.999 ? "1" : v <= 0.001 ? "·" : v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void PrintFloors(IReadOnlyList<HeldOutTarget> targets, IReadOnlyDictionary<(string, string), HitCell> cells)
    {
        EvalPanel.Open($"Chance floors at k = {K} — STATED (closed form) and EXECUTED (uniform draw from the pool)");
        EvalPanel.Section("COMPUTED FROM THIS CORPUS AT RUN TIME — not quoted from a design document");
        EvalPanel.Divider();
        EvalPanel.Row($"  {"customer",-10} {"pool",5} {"sku an.",8} {"sku ex.",8} {"leaf",5} {"leaf an.",9} {"leaf ex.",9}  target");
        foreach (var t in targets)
        {
            bool has = cells.TryGetValue((t.PersonaId, ArmFloor), out var c);
            EvalPanel.Row($"  {t.PersonaId,-10} {t.PoolSize,5} {F3(t.SkuFloor(K)),8} {(has ? F3(c.Sku) : "—"),8} " +
                          $"{t.LeafCarriersInPool,5} {F3(t.LeafFloor(K)),9} {(has ? F3(c.Leaf) : "—"),9}  {t.Target.Id} {t.TargetLeaf}");
        }
        EvalPanel.Divider();
        EvalPanel.Note($"  sku floor = k/pool; leaf floor = 1 - C(pool - carriers, k)/C(pool, k). Executed = mean of {FloorDraws}");
        EvalPanel.Note("  seeded draws through the real harness and grader. They must agree within the wiring band.");
        EvalPanel.Close();
    }

    private static void PrintWiring(IReadOnlyList<WiringRow> rows)
    {
        EvalPanel.Open("Wiring — the checks that make the table above mean anything");
        EvalPanel.Section("A LEAK THAT LOOKS LIKE A HIT IS THE FLATTERING DIRECTION; IT IS CHECKED HARDEST");
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
            ? "  ✅ Every wiring check held. The hidden line stayed hidden and the floor is where the arithmetic says."
            : "  ❌ A wiring check failed. Treat every number above as UNPROVEN.",
            all ? ConsoleColor.Green : ConsoleColor.Red);
        EvalPanel.Close();
    }

    private static void PrintNotes(IReadOnlyList<string> notes)
    {
        foreach (string note in notes)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            foreach (string line in EvalPanel.Wrap("  · " + note, EvalPanel.BoxWidth))
                Console.WriteLine(line);
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    private static void PrintWhatThisSupports(int n)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  What this number can and cannot support:");
        Console.WriteLine($"    · CAN: catch an arm that does not read history at all — at floor ≈ {K}/94 a working arm should");
        Console.WriteLine($"      clear it, and {n} targets are enough to see a rate of 0.3 against a floor of 0.05.");
        Console.WriteLine("    · CANNOT: rank two working arms. One hit is 0.077 of rate; the 95% interval on any rate here");
        Console.WriteLine("      spans most of [0, 1]; the histories were authored to plant latent interests, not sampled");
        Console.WriteLine("      from a log, so 'the next purchase' is whatever the author wrote last.");
        Console.WriteLine("    · CANNOT: say anything about a customer with fewer than three lines, or about the five whose");
        Console.WriteLine("      latest line is a repeat — the rule targets an earlier line for them, and says so above.");
        Console.WriteLine("    · The verdict of this eval is the WIRING's. The live arm's rate is reported, never gated.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintLiveNotMeasured()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  ⚠️  Eval 02c — {CredentialGuard.NotMeasuredBanner}.");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("     NOT MEASURED: the live agent's held-out hit-rate.");
        Console.WriteLine("     The offline arms above were run and their numbers are real — about THOSE arms.");
        Console.WriteLine("     Nothing was substituted into the live column.");
        Console.WriteLine();
        Console.WriteLine("     Set AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY (AZURE_OPENAI_DEPLOYMENT is optional");
        Console.WriteLine($"     and defaults to {Config.PreferredDeployment}) — or add --dry-run to exercise the live path against a stub.");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"     Exit code {CredentialGuard.NotMeasuredExitCode}, never 0.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintMode(bool dryRun, bool liveMeasurable, int targets)
    {
        if (dryRun)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  🧪 DRY RUN — the live arm is a stub that presents the same two products for every customer.");
            Console.WriteLine("     Nothing spent, no snapshot written. Its hit column is NOT a result.");
            Console.ResetColor();
        }
        else if (liveMeasurable)
        {
            Config.PrintAzureTarget();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠ PAID: {targets} targets × live reps. Add --quick for one rep.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠️  No credentials. The live column will read n/m — {CredentialGuard.NotMeasuredBanner} — and this");
            Console.WriteLine($"     run will exit {CredentialGuard.NotMeasuredExitCode}. The offline arms and the floor run anyway.");
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 02c — Held-Out Next Purchase (leave-one-out hit-rate@k, k declared)    ║
║   The non-circular offline gold · floor k/pool, executed                      ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static HeldOutSnapshot ToSnapshot(
        IReadOnlyList<Arm> arms,
        IReadOnlyList<HeldOutTarget> targets,
        IReadOnlyDictionary<(string, string), HitCell> cells,
        int liveReps) =>
        new()
        {
            Label = $"Eval 02c — Held-Out Next Purchase (n = {targets.Count}, k = {K}, {liveReps} rep(s) on the live arm)",
            K = K,
            Arms = [.. arms.Select(a => a.Label)],
            TargetCount = targets.Count,
            SkuHitRateByArm = arms.ToDictionary(
                a => a.Label,
                a => targets.Where(t => cells.ContainsKey((t.PersonaId, a.Label))).Select(t => cells[(t.PersonaId, a.Label)].Sku).DefaultIfEmpty(double.NaN).Average(),
                StringComparer.Ordinal),
            LeafHitRateByArm = arms.ToDictionary(
                a => a.Label,
                a => targets.Where(t => cells.ContainsKey((t.PersonaId, a.Label))).Select(t => cells[(t.PersonaId, a.Label)].Leaf).DefaultIfEmpty(double.NaN).Average(),
                StringComparer.Ordinal),
            MeanSkuFloor = targets.Average(t => t.SkuFloor(K)),
            MeanLeafFloor = targets.Average(t => t.LeafFloor(K)),
            LiveArmMeasured = targets.Any(t => cells.ContainsKey((t.PersonaId, ArmLive))),
            Cells =
            [
                .. from t in targets
                   from a in arms
                   where cells.ContainsKey((t.PersonaId, a.Label))
                   let c = cells[(t.PersonaId, a.Label)]
                   select new HeldOutCellSnapshot(t.PersonaId, t.Hidden.Id, t.Target.Id, a.Label, c.Sku, c.Leaf,
                                                  (int)Math.Round(c.PresentedRaw), t.SkuFloor(K), t.LeafFloor(K))
            ],
        };

    private static string F3(double value) => EvalPanel.F3(value);
}
