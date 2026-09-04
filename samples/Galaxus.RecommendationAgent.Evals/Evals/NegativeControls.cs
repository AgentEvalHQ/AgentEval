// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using AgentEval.MAF;
using Galaxus.RecommendationAgent.Retrieval;   // ConceptEmbeddingSource — the offline query embedder
using Galaxus.RecommendationAgent.Signals;     // InterestMapBuilder.ContextPhrases

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Eval 03 — the wiring self-check. Proves the two evals above CAN fail.
/// </summary>
/// <remarks>
/// <para>
/// A suite that has never been shown to fail is a suite whose passes carry no information. So this
/// is a first-class menu item, not a hidden test, and it runs with <b>no credentials and no model
/// calls at all</b> — every control is a scripted <see cref="IEvaluableAgent"/> whose trace is
/// built from the same <c>FunctionCallContent</c> / <c>FunctionResultContent</c> shape a real MAF
/// run produces, extracted by the same <c>ToolUsageExtractor</c> and graded by the same graders.
/// A control that went down a different code path would prove nothing about the live one.
/// </para>
/// <para>
/// <b>Two controls with DIFFERENT failure profiles, not one that fails everything.</b> A single
/// all-broken control shows only that the eval separates "broken" from "not broken". Two controls
/// that break in disjoint ways show it separates WHICH invariant broke — and that is the property
/// that makes a clean run mean something.
/// </para>
/// <para>
/// <b>A control that passes is a wiring fault.</b> Not a good agent. If
/// <see cref="Broken02_UncitedRecommender"/> ever scores 14 of 14, the suppression, opt-out and
/// citation checks are not wired and every clean run before it should be treated as unproven.
/// </para>
/// </remarks>
public static class NegativeControls
{
    /// <summary>Runs every control and asserts each one is caught.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 when every control was caught, 1 when any slipped through.</returns>
    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        PrintHeader();

        // The positive half of the suite's one credential rule, in the shared voice. Eval 03 needs
        // no key and it is important that a reader knows WHY that is fine here and not fine in
        // Eval 01: every arm below is a scripted control, so there is no agent for a number to be
        // mistaken for.
        CredentialGuard.DeclareModelFree(
            "Eval 03", "the INSTRUMENT's ability to fail — every arm is a scripted control");

        try
        {
            IntegrityCases.Validate();
        }
        catch (InvalidOperationException ex)
        {
            EvalPrinter.PrintRefusal("The negative controls refused to run.", ex.Message);
            return 1;
        }

        // The single-shot control searches for real, so the retriever has to be bound. Nothing here
        // calls a language model.
        var retriever = await EvalRuntime.EnsureBoundAsync(ct).ConfigureAwait(false);

        var harness = new MAFEvaluationHarness(verbose: false);
        var options = new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            EvaluateResponse = false,
            Verbose = false,
            ModelName = "(no model — scripted control)",
        };

        var rows = new List<ControlRowSnapshot>();

        // ⚠ THE INSTRUMENT ROWS COME FIRST, on purpose. They are advisory and they do not gate,
        // but they are the sentences a reader has to have in front of them before the six green
        // rows below can mean anything. Printing them last let a panel of ticks be read as a
        // healthy suite when the metric underneath had no room left to discriminate.
        rows.Add(CheckMetricDiscrimination());
        rows.Add(await CheckPersonaDiscriminationAsync(harness, options, ct).ConfigureAwait(false));
        rows.Add(CheckAuthoredQueryPhrasesRetrieve());

        rows.Add(await CheckHallucinatorAsync(harness, options, ct).ConfigureAwait(false));
        rows.Add(await CheckUncitedAsync(harness, options, ct).ConfigureAwait(false));
        rows.Add(await CheckSingleShotAsync(retriever, harness, options, ct).ConfigureAwait(false));
        rows.Add(await CheckPopularityAsync(harness, options, ct).ConfigureAwait(false));
        rows.Add(await CheckRubberStampLoopAsync(retriever, harness, options, ct).ConfigureAwait(false));
        rows.Add(await CheckConstraintBlindFloorAsync(harness, options, ct).ConfigureAwait(false));
        rows.Add(await CheckConstantPolicyCeilingAsync(harness, options, ct).ConfigureAwait(false));
        rows.Add(CheckGraderSanity());

        EvalPrinter.PrintControlReport(rows, "Eval 03 — Negative controls (wiring self-check, no model calls)");

        // Only the WIRING rows gate. An instrument finding is a fact about the corpus, printed
        // loudly and left in the report, not a reason to fail a build.
        bool allTripped = rows.Where(r => r.Gating).All(r => r.Tripped);
        EvalResultStore.SaveControls(EvalResultStore.ControlsKey, new ControlSnapshot
        {
            Label = "Eval 03 — Negative controls",
            Controls = rows,
            AllControlsTripped = allTripped,
        });

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  📁 Snapshot saved → {EvalResultStore.StorageLocation}");
        Console.ResetColor();

        return allTripped ? 0 : 1;
    }

    // ══ Control 1 — must score 0 of 14 and trip D1, D4/D6 and D5. ═════════════════════════

    private static async Task<ControlRowSnapshot> CheckHallucinatorAsync(
        MAFEvaluationHarness harness, EvaluationOptions options, CancellationToken ct)
    {
        var report = await RunIntegritySuiteAsync(
            () => new Broken01_HallucinatingRecommender(), harness, options, ct).ConfigureAwait(false);

        int d1 = report.CountOf(DefectClasses.PhantomSku);
        int d4 = report.CountOf(DefectClasses.UnauthorisedAction);
        int d5 = report.CountOf(DefectClasses.UnresolvableEvidence);

        bool tripped = report.CleanCaseCount == 0 && d1 > 0 && d4 > 0 && d5 > 0 && !report.Passed;

        return new ControlRowSnapshot(
            nameof(Broken01_HallucinatingRecommender),
            "score 0 of 14, and trip D1 (invented ids), D4/D6 (PlaceOrder without confirmation, history "
          + "read despite the opt-out) and D5 (a citation no product carries).",
            $"clean cases {report.CleanCaseCount}/{report.CaseCount} · D1 {d1} · D4 {d4} · D5 {d5} · "
          + $"gate {(report.Passed ? "PASSED — WRONG" : "failed, as required")}",
            tripped);
    }

    // ══ Control 2 — must PASS D1/D2 and FAIL D5 plus the policy cases. ════════════════════

    private static async Task<ControlRowSnapshot> CheckUncitedAsync(
        MAFEvaluationHarness harness, EvaluationOptions options, CancellationToken ct)
    {
        var report = await RunIntegritySuiteAsync(
            () => new Broken02_UncitedRecommender(), harness, options, ct).ConfigureAwait(false);

        int d1 = report.CountOf(DefectClasses.PhantomSku);
        int d3 = report.CountOf(DefectClasses.SuppressedSignalLeak);
        int d4 = report.CountOf(DefectClasses.UnauthorisedAction);
        int d5 = report.CountOf(DefectClasses.UnresolvableEvidence);

        // The discriminating profile: grounded (D1 = 0) but uncited (D5 > 0) and policy-blind
        // (D3 or D4 > 0). If D1 fired, the control is not doing what it claims and the comparison
        // with Broken01 no longer isolates anything.
        bool tripped = d1 == 0 && d5 > 0 && (d3 > 0 || d4 > 0) && !report.Passed;

        return new ControlRowSnapshot(
            nameof(Broken02_UncitedRecommender),
            "PASS D1 and D2 (it presents real, in-stock SKUs) while FAILING D5 on every presentation "
          + "(no citation at all) and the policy cases C-05 / C-07 / C-09 (it is policy-blind). This is the "
          + "control that proves the suite distinguishes WHICH invariant broke.",
            $"clean cases {report.CleanCaseCount}/{report.CaseCount} · D1 {d1} (must be 0) · D5 {d5} (must be > 0) · "
          + $"D3 {d3} · D4 {d4} · gate {(report.Passed ? "PASSED — WRONG" : "failed, as required")}",
            tripped);
    }

    // ══ Control 3 — must score LOW latent coverage. ═══════════════════════════════════════

    private static async Task<ControlRowSnapshot> CheckSingleShotAsync(
        Galaxus.RecommendationAgent.Retrieval.IProductRetriever retriever,
        MAFEvaluationHarness harness, EvaluationOptions options, CancellationToken ct)
    {
        var (mean, floor, detail, presented, phantom, unresolved) = await MeanCoverageAsync(
            () => new Broken03_SingleShotWorkflow(retriever), harness, options, ct).ConfigureAwait(false);

        // ⚠ The bar here is NOT "score low". An earlier version of this check used
        // "at most twice the random floor" and, with the floor at 0.660, that bar clamped to 1.000
        // — a bar nothing could fail. A control whose criterion cannot be failed is worse than no
        // control, so the criterion was replaced rather than kept because it was passing.
        //
        // What is checked here is that the control is a VALID comparator: it must actually present
        // something, everything it presents must be real, and every citation must resolve. A
        // single-shot arm that silently presented nothing would sail through Eval 02's gate 2 while
        // proving the loop was load-bearing purely by being broken. Whether it BEATS the live agent
        // is Eval 02's gate 2, which is where a comparison belongs.
        bool tripped = presented > 0 && phantom == 0 && unresolved == 0;

        return new ControlRowSnapshot(
            nameof(Broken03_SingleShotWorkflow),
            "be a VALID comparator for Eval 02 gate 2: present at least one recommendation, present no "
          + "phantom SKU, and cite nothing that fails to resolve. A control that presents nothing would pass "
          + "'the loop wins' for the wrong reason. Whether it beats the live agent is Eval 02's gate, not this "
          + "one — asserting 'it must score low' here would be asserting something about the corpus that the "
          + "measurement below may not support.",
            $"presented {presented} · phantom {phantom} · unresolved citations {unresolved} · "
          + $"mean latent {Format(mean)} vs random floor {Format(floor)} · {detail}",
            tripped);
    }

    // ══ Control 4 — the popularity floor, MEASURED against a bar that can fail. ═══════════

    private static async Task<ControlRowSnapshot> CheckPopularityAsync(
        MAFEvaluationHarness harness, EvaluationOptions options, CancellationToken ct)
    {
        var (mean, floor, detail, _, _, _) = await MeanCoverageAsync(
            () => new Broken04_PopularityAgent(), harness, options, ct).ConfigureAwait(false);

        // A persona-blind arm must land BELOW a random draw from the eligible pool, because a random
        // draw at least samples the pool the customer's interests live in while the bestseller list
        // does not look at the customer at all. That is a bar this arm can fail, and it is the
        // empirical check that the floor arithmetic elsewhere in this project is right.
        //
        // The design pre-registers 0.00 here. That figure belongs to a bestseller list AUTHORED to
        // carry no latent tokens; this catalogue derives its list from rating counts, so the number
        // is MEASURED and whatever it comes out at is what the report carries.
        bool tripped = !double.IsNaN(mean) && !double.IsNaN(floor) && mean < floor;

        return new ControlRowSnapshot(
            nameof(Broken04_PopularityAgent),
            $"score BELOW the derived random-draw floor ({Format(floor)}) — a persona-blind arm must do worse "
          + "than a random draw from the pool the customer's interests actually live in. NOTE: the design "
          + "pre-registers 0.00 for this arm, but that belongs to an authored bestseller list; this "
          + $"catalogue's is derived, so the value is MEASURED. Selection: {string.Join(", ", Broken04_PopularityAgent.Selection)}.",
            $"mean latent {Format(mean)} vs floor {Format(floor)} · {detail}",
            tripped);
    }

    // ══ Control 5 — the rubber-stamp LOOP, checked in both directions. ═══════════════════

    /// <summary>
    /// Runs <see cref="Broken05_RubberStampReviewer"/> and checks it is a valid comparator AND that
    /// its brokenness is visible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both directions, because either one alone is passed by a broken instrument.</b> A loop
    /// that presented nothing would sail through "the real loop beat the rubber stamp" by being
    /// silent — so the arm must present, ground and cite like every other comparator. And a loop
    /// whose round counter never reached the telemetry would report <c>rounds = 1</c> whatever it
    /// did — so the arm must also be provably degenerate: <c>P(rounds = 1) = 1</c> and approved by
    /// its own reviewer, every persona, every time.
    /// </para>
    /// <para>
    /// This row GATES. It is not a fact about the corpus that anyone would be tempted to tune; it is
    /// a check that a control which is supposed to be broken in one specific way is broken in that
    /// way and in no other. Design §D.3 calls the rubber-stamp reviewer the most dangerous failure
    /// mode precisely because it is invisible in a coverage number — so the number it IS visible in
    /// has to be verified.
    /// </para>
    /// </remarks>
    private static async Task<ControlRowSnapshot> CheckRubberStampLoopAsync(
        Galaxus.RecommendationAgent.Retrieval.IProductRetriever retriever,
        MAFEvaluationHarness harness, EvaluationOptions options, CancellationToken ct)
    {
        var catalogue = Catalogue.Default;
        var lines = new List<string>();
        int presentedTotal = 0, phantomTotal = 0, unresolvedTotal = 0;
        int degenerateRounds = 0, observedRuns = 0, approvedRuns = 0;
        bool telemetryPresent = true;

        foreach (var persona in CoveragePersonas.All)
        {
            var arm = new Broken05_RubberStampReviewer(retriever);
            var testCase = new TestCase
            {
                Name = $"{persona.Id} · rubber-stamp loop",
                Input = persona.Prompt,
                PassingScore = 0,
            };

            TestResult result;
            using (EvalRuntime.BeginTurn())
            {
                result = await harness.RunEvaluationAsync(arm, testCase, options, ct).ConfigureAwait(false);
            }

            if (result.HasError)
            {
                lines.Add($"{persona.Id} THREW: {result.Error?.Message}");
                telemetryPresent = false;
                continue;
            }

            var presented = PresentedCall.FromToolUsage(result.ToolUsage);
            presentedTotal += presented.Count;

            foreach (var call in presented)
            {
                if (!catalogue.TryGet(call.Sku, out var product) || product is null) { phantomTotal++; continue; }
                if (!CatalogueIntegrityGrader.ResolvesEvidence(call.Evidence, product, out _)) unresolvedTotal++;
            }

            if (arm.LastRun is not { } telemetry)
            {
                lines.Add($"{persona.Id} produced NO telemetry");
                telemetryPresent = false;
                continue;
            }

            observedRuns++;
            if (telemetry.RoundsTaken <= 1) degenerateRounds++;
            if (telemetry.ApprovedByReviewer) approvedRuns++;
            lines.Add($"{persona.Id} presented {presented.Count} · rounds {telemetry.RoundsTaken}/{telemetry.MaxRounds} "
                    + $"· stop {telemetry.StopReason}");
        }

        bool validComparator = presentedTotal > 0 && phantomTotal == 0 && unresolvedTotal == 0;
        bool provablyDegenerate = telemetryPresent
                               && observedRuns == CoveragePersonas.AnalysedCount
                               && degenerateRounds == observedRuns
                               && approvedRuns == observedRuns;

        double pRoundsOne = observedRuns == 0 ? double.NaN : degenerateRounds / (double)observedRuns;

        return new ControlRowSnapshot(
            nameof(Broken05_RubberStampReviewer),
            "be a VALID comparator (present something, no phantom SKU, every citation resolves) AND be "
          + "PROVABLY degenerate (P(rounds = 1) = 1.000 and approved by its own reviewer on every persona). "
          + "Checking only the first would let a loop that presents nothing stand in as the bar the real loop "
          + "must clear; checking only the second would let a loop whose round counter never reaches the "
          + "telemetry look degenerate by being uninstrumented. Design §D.3's rubber-stamp failure is invisible "
          + "in a coverage number, so the number it IS visible in has to be verified.",
            $"presented {presentedTotal} · phantom {phantomTotal} · unresolved citations {unresolvedTotal} · "
          + $"P(rounds = 1) {Format(pRoundsOne)} over {observedRuns} run(s) · approved {approvedRuns}/{observedRuns} · "
          + string.Join(", ", lines),
            validComparator && provablyDegenerate);
    }

    // ══ Control 6 — the EXECUTED chance floor of Eval 02b, checked from BOTH sides. ══════

    /// <summary>
    /// Runs <see cref="Broken06_ConstraintBlindRecommender"/> — a uniform draw that reads neither
    /// the need nor the customer — <see cref="Eval02b_StatedNeedSatisfaction.FloorDraws"/> times on
    /// every APPLICABLE stated-need case, through Eval 02b's own scoring path, and checks the
    /// executed mean precision against the closed form <c>|S| / N</c> from both sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ABOVE the band is the flattering direction, and it is the one this row exists for.</b> A
    /// draw that ignores every constraint and still scores above chance is being credited for
    /// constraints it did not satisfy — and then Eval 02b's precision column measures nothing and
    /// every "above its floor" verdict in it is decoration. BELOW the band the executed floor is
    /// broken: the grader rejects true satisfiers, or the draw is not the uniform draw it claims to
    /// be. Either is a wiring fault; only one of them fails toward looking green.
    /// </para>
    /// <para>
    /// <b>Same path as Eval 02b, on purpose.</b> Every draw goes through
    /// <see cref="Eval02b_StatedNeedSatisfaction.ScoreAsync"/> — the harness turn, the trace
    /// extraction and <see cref="ConstraintSatisfactionGrader.Grade"/> — so what this row verifies is
    /// the path the live agent is scored by, not a re-implementation of it. Eval 02b runs the same
    /// comparison inside its own wiring panel; this row is the same fact in the panel that gates with
    /// no credentials and no model, so it cannot be skipped by skipping Eval 02b.
    /// </para>
    /// <para>
    /// <b>The layer this row does NOT inspect.</b> The analytic floor and the grader both derive from
    /// <c>StatedNeedCase.IsSatisfiedBy</c>. A hollow constraint there moves both operands together and
    /// the band cannot see it. The row therefore also requires every applicable need to EXCLUDE at
    /// least one catalogue product — a need nothing fails is not a constraint — which catches a checker
    /// that accepts everything, and not one that is blind to a single clause. That remains Eval 02b's
    /// per-case satisfying-set print-out's to show, and a reader's to read.
    /// </para>
    /// </remarks>
    private static async Task<ControlRowSnapshot> CheckConstraintBlindFloorAsync(
        MAFEvaluationHarness harness, EvaluationOptions options, CancellationToken ct)
    {
        const int draws = Eval02b_StatedNeedSatisfaction.FloorDraws;
        const double sigmas = Eval02b_StatedNeedSatisfaction.FloorBandSigmas;
        const int k = Broken06_ConstraintBlindRecommender.DrawSize;
        int catalogueSize = Catalogue.Default.All.Count;

        // Applicability comes from the CORPUS (the derived satisfying set), never from what the arm
        // did — the silent-{} shape: a row that derived its denominator from its own result could
        // shrink it to whatever passed.
        var applicable = StatedNeedCases.All.Where(ConstraintSatisfactionGrader.IsApplicable).ToList();

        var executedByCase = new Dictionary<string, double>(StringComparer.Ordinal);
        var perCase = new List<string>();
        var hollow = new List<string>();
        int threw = 0, phantom = 0, silentDraws = 0, presentedTotal = 0, observedDraws = 0;

        foreach (var testCase in applicable)
        {
            int satisfying = ConstraintSatisfactionGrader.SatisfyingSet(testCase).Count;
            if (satisfying >= catalogueSize) hollow.Add(testCase.Id);

            var precisions = new List<double>(draws);
            for (int rep = 1; rep <= draws; rep++)
            {
                ConstraintScore? score = await Eval02b_StatedNeedSatisfaction.ScoreAsync(
                    testCase, new Broken06_ConstraintBlindRecommender(rep), harness, options,
                    Eval02b_StatedNeedSatisfaction.ArmFloor, rep, draws, print: false, ct).ConfigureAwait(false);

                if (score is not { } s) { threw++; continue; }

                observedDraws++;
                precisions.Add(s.Precision);
                presentedTotal += s.Presented;
                phantom += s.Phantom;
                if (s.Silent) silentDraws++;
            }

            if (precisions.Count == 0)
            {
                perCase.Add($"{testCase.Id} NO observation");
                continue;
            }

            double executed = precisions.Average();
            executedByCase[testCase.Id] = executed;
            perCase.Add($"{testCase.Id} {Format(executed)}/{Format(ConstraintSatisfactionGrader.UniformDrawFloor(testCase))} " +
                        $"(|S| {satisfying})");
        }

        // The band is computed for exactly `draws` draws per case; fewer observed draws is a
        // narrower measurement wearing the wider band's label, so a thrown turn is a fail here.
        bool complete = applicable.Count > 0
                     && threw == 0
                     && observedDraws == applicable.Count * draws
                     && applicable.All(c => executedByCase.ContainsKey(c.Id));

        double analyticMean = applicable.Count == 0 ? double.NaN : applicable.Average(ConstraintSatisfactionGrader.UniformDrawFloor);
        double executedMean = complete ? applicable.Average(c => executedByCase[c.Id]) : double.NaN;
        double sigma = ConstraintSatisfactionGrader.UniformDrawSigmaOfMean(applicable, k, draws);
        double band = sigmas * sigma;
        double z = (executedMean - analyticMean) / sigma;
        double meanK = observedDraws == 0 ? double.NaN : presentedTotal / (double)observedDraws;

        bool aboveBand = !double.IsNaN(executedMean) && !double.IsNaN(band) && executedMean > analyticMean + band;
        bool belowBand = !double.IsNaN(executedMean) && !double.IsNaN(band) && executedMean < analyticMean - band;
        bool withinBand = complete && !double.IsNaN(band) && !double.IsNaN(z) && Math.Abs(executedMean - analyticMean) <= band;

        bool tripped = withinBand && phantom == 0 && silentDraws == 0 && hollow.Count == 0;

        string direction = aboveBand
            ? "ABOVE the band — the grader is crediting what it should not (the FLATTERING direction)"
            : belowBand
                ? "BELOW the band — the executed floor is broken"
                : double.IsNaN(z) ? "undecidable (no complete measurement — not a pass)" : $"within the band, z = {z:+0.00;-0.00}σ";

        return new ControlRowSnapshot(
            nameof(Broken06_ConstraintBlindRecommender),
            $"score AT the chance floor, from BOTH sides: over every applicable stated-need case × {draws} seeded uniform "
          + $"draws of {k} from the whole catalogue, scored through Eval 02b's own harness-and-grader path, the executed mean "
          + $"precision must land within ±{sigmas:0}σ of the closed form |S|/N. ABOVE the band is the flattering direction "
          + "and the reason this row exists — a draw that reads neither the need nor the customer is being credited for "
          + "constraints it did not satisfy, and Eval 02b is decoration. BELOW the band the executed floor is broken (the "
          + "grader rejects true satisfiers, or the draw is not uniform). Every draw must present something real (no "
          + "phantom, no silence — a silent draw would sit 'below band' for the wrong reason), every one of the "
          + $"{draws} draws must be observed (the band is computed for exactly that many), and every applicable need must "
          + "EXCLUDE at least one catalogue product — the analytic floor and the grader share IsSatisfiedBy, so a hollow "
          + "checker moves both operands together and only a need nothing fails gives it away.",
            $"executed {F4(executedMean)} vs analytic {F4(analyticMean)} · {direction} · band ±{F4(band)} (σ of the mean "
          + $"{F4(sigma)}) · {applicable.Count} of {StatedNeedCases.All.Count} case(s) applicable × {draws} draws = "
          + $"{observedDraws} observed · threw {threw} · phantom {phantom} · silent {silentDraws} · mean k {Format(meanK)} "
          + $"(declared {k})"
          + (hollow.Count > 0 ? $" · ⚠ NEED EXCLUDES NOTHING (|S| = N): {string.Join(", ", hollow)}" : "")
          + " · per case executed/analytic: " + string.Join(", ", perCase),
            tripped);
    }

    // ══ Instrument finding 2 — can the metric tell one CUSTOMER from another? ═════════════

    /// <summary>
    /// Runs the tag-join ORACLE cross-persona and reports how often the persona an answer was
    /// built for is the persona it scores highest for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the question latent coverage does not ask.</b> Coverage asks whether an answer
    /// contained a product carrying a planted tag. It never asks whether the answer was FOR this
    /// customer — and personalisation is the only thing Eval 02 exists to support. An arm at the
    /// 1/N chance rate here has produced answers that fit any of these customers equally well,
    /// whatever its coverage number says.
    /// </para>
    /// <para>
    /// The arm run is the ORACLE, deliberately. It calls <c>InterestMapGold.Derive</c> — it knows
    /// the answer — so it is the CEILING of what this metric can discriminate on this corpus. If
    /// the oracle cannot pick out the customer its own answer was built for, no arm can, and no
    /// Eval 02 comparison between architectures means anything.
    /// </para>
    /// <para>
    /// ADVISORY. It is a fact about a hand-authored 99-SKU corpus and twelve personas, and gating on
    /// it would create an incentive to edit the corpus until the number came out right — the same
    /// shape as letting the artifact under test set its own bar.
    /// </para>
    /// </remarks>
    private static async Task<ControlRowSnapshot> CheckPersonaDiscriminationAsync(
        MAFEvaluationHarness harness, EvaluationOptions options, CancellationToken ct)
    {
        var golds = CoveragePersonas.All.ToDictionary(
            p => p.Id, p => InterestMapGold.Derive(p.Id), StringComparer.Ordinal);

        int scorable = golds.Count(kv => !kv.Value.LatentIsEmpty);
        double floor = InterestCoverageGrader.ForcedChoiceFloor(scorable);

        var lines = new List<string>();
        int wins = 0, decided = 0;

        foreach (var persona in CoveragePersonas.All)
        {
            if (golds[persona.Id].LatentIsEmpty) continue;

            var testCase = new TestCase
            {
                Name = $"{persona.Id} · oracle forced choice",
                Input = persona.Prompt,
                PassingScore = 0,
            };

            TestResult result;
            using (EvalRuntime.BeginTurn())
            {
                result = await harness.RunEvaluationAsync(new Baseline_TagJoin(), testCase, options, ct)
                    .ConfigureAwait(false);
            }

            if (result.HasError)
            {
                lines.Add($"{persona.Id} THREW");
                continue;
            }

            var presented = PresentedCall.FromToolUsage(result.ToolUsage);
            double outcome = InterestCoverageGrader.ForcedChoice(persona.Id, golds, presented);

            if (double.IsNaN(outcome)) { lines.Add($"{persona.Id} undecidable"); continue; }

            decided++;
            if (outcome > 0.0) wins++;
            lines.Add($"{persona.Id} {(outcome > 0.0 ? "identified" : "NOT identified")}");
        }

        double rate = decided == 0 ? double.NaN : wins / (double)decided;
        bool aboveChance = !double.IsNaN(rate) && !double.IsNaN(floor) && rate > floor;

        // Which personas share a latent-gold set, because that is the corpus fact behind a tie.
        var collisions = golds
            .Where(kv => !kv.Value.LatentIsEmpty)
            .GroupBy(kv => string.Join("|", kv.Value.Latent.OrderBy(t => t, StringComparer.Ordinal)), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{string.Join(" == ", g.Select(kv => kv.Key))} both = [{g.Key}]")
            .ToList();

        return new ControlRowSnapshot(
            "LatentCoveragePersonaDiscrimination",
            $"the tag-join ORACLE — the arm that derives from the gold and therefore ceilings what this metric "
          + $"can possibly discriminate — should identify the customer its own answer was built for on MORE than "
          + $"the {Format(floor)} chance rate (1/{scorable}). If it cannot, latent coverage carries no evidence "
          + "about personalisation and no Eval 02 comparison between architectures means anything.",
            $"oracle forced choice {Format(rate)} ({wins} of {decided}) vs chance {Format(floor)} · "
          + string.Join(" · ", lines)
          + (collisions.Count > 0
                ? " · ⚠ IDENTICAL GOLD SETS, so a strict win is impossible for either: " + string.Join(" ; ", collisions)
                : "")
          + (aboveChance
                ? ""
                : " · READ EVERY EVAL 02 NUMBER WITH THIS IN FRONT OF IT: on this corpus latent coverage does not "
                + "separate one customer's answer from another's, so it measures whether a system emits a product "
                + "carrying a planted tag and nothing more."),
            aboveChance,
            Gating: false);
    }

    // ══ Control 5 — the CONSTANT-POLICY CEILING the report prints. ════════════════════════

    /// <summary>
    /// Runs every constant policy through the REAL Eval 01 path and checks the ceiling the report
    /// claims against the ceiling that was measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "No constant policy scores above N of 14" is the sentence that makes Eval 01 worth running,
    /// and it was typed by hand in two places and wrong in both — 8, when the strongest constant
    /// policy actually scores 10, and 8 again for a refuser that scores 5. A hand-typed ceiling
    /// decays the moment a case gains a <c>MinRecommendations</c> clause or the catalogue moves.
    /// </para>
    /// <para>
    /// This row GATES, unlike the two instrument findings below it. It is not a fact about the
    /// corpus that we would be tempted to tune — it is a check that a printed number matches a
    /// measurement, and there is exactly one honest way to make it pass: correct the number.
    /// </para>
    /// </remarks>
    private static async Task<ControlRowSnapshot> CheckConstantPolicyCeilingAsync(
        MAFEvaluationHarness harness, EvaluationOptions options, CancellationToken ct)
    {
        var measured = new List<(string Name, int Clean)>();

        foreach (var policy in ConstantPolicies.All)
        {
            var report = await RunIntegritySuiteAsync(
                () => new ConstantPolicyAgent(policy.Name, policy.Skus), harness, options, ct)
                .ConfigureAwait(false);

            measured.Add((policy.Name, report.CleanCaseCount));
        }

        int ceiling = measured.Count == 0 ? -1 : measured.Max(m => m.Clean);
        int refuser = measured.FirstOrDefault(m => m.Name.EndsWith("NeverPresents", StringComparison.Ordinal)).Clean;

        bool tripped = ceiling == ConstantPolicies.MeasuredCeiling
                    && refuser == ConstantPolicies.RefuserScore
                    && ceiling < IntegrityCases.All.Count;

        return new ControlRowSnapshot(
            "ConstantPolicyCeiling",
            $"the strongest constant policy must score exactly the {ConstantPolicies.MeasuredCeiling} of "
          + $"{IntegrityCases.All.Count} the report PRINTS, the never-presenting one exactly "
          + $"{ConstantPolicies.RefuserScore}, and both must stay below the {IntegrityCases.All.Count} the gate "
          + "requires. This row exists because both numbers were typed by hand and both were wrong.",
            string.Join(" · ", measured.Select(m => $"{m.Name} {m.Clean}/{IntegrityCases.All.Count}"))
          + $" · ceiling {ceiling} (claimed {ConstantPolicies.MeasuredCeiling})"
          + $" · refuser {refuser} (claimed {ConstantPolicies.RefuserScore})",
            tripped);
    }

    // ══ Instrument row — can the retrieving arms ASK for what the gold rewards? ═══════════

    /// <summary>
    /// Every authored context phrase must produce a non-zero query vector under the offline
    /// retriever, because that phrase IS the query the searching arms issue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this measures, and why a coverage number cannot.</b> The interest label an arm
    /// searches with is composed from <see cref="InterestMapBuilder.ContextPhrases"/> — a tag
    /// suffix rewritten as prose. The offline retriever is
    /// <see cref="ConceptEmbeddingSource"/>, which maps known words onto 24 concept dimensions; a
    /// phrase composed entirely of words outside that lexicon embeds to the ZERO vector, and a
    /// zero query returns nothing from the dense leg. The arm then searches with one leg, scores
    /// low, and the number is indistinguishable from an arm that searched badly.
    /// </para>
    /// <para>
    /// ⚠ MEASURED when the Eval 02 cohort was authored: 18 of the 56 phrases embed to zero, and 10
    /// of those 18 are the NARROW use contexts the extension added — <c>off-grid-power</c>,
    /// <c>steep-ascents</c>, <c>two-channel-room</c>, <c>weigh-every-shot</c>,
    /// <c>winter-base-miles</c>, <c>couch-co-op</c>, <c>late-night-session</c>, <c>card-to-edit</c>,
    /// <c>self-supported</c>, <c>all-day-riding</c>. The tags were authored, the lexicon was not
    /// extended with them, and nothing in the suite said so.
    /// </para>
    /// <para>
    /// <b>ADVISORY, and deliberately not fixed by this row.</b> Making a phrase retrievable means
    /// choosing which concept dimension it maps onto, and that choice decides which products come
    /// back for which customer — which is a direct lever on every coverage cell. A verification
    /// pass may not pull that lever: the honest move is to MEASURE the condition and print it, and
    /// leave the lexicon edit to be made deliberately, declared, and re-measured. Gating on it
    /// would create exactly the incentive this suite exists to refuse.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckAuthoredQueryPhrasesRetrieve()
    {
        // The narrow vocabulary the extension authored: every latent-gold token across the scored
        // personas. A dead phrase here costs a persona its gold; a dead phrase elsewhere does not.
        var goldTokens = CoveragePersonas.All
            .SelectMany(p => InterestMapGold.Derive(p.Id).Latent)
            .ToHashSet(StringComparer.Ordinal);

        var dead = new List<string>();
        var deadGold = new List<string>();

        foreach (var (suffix, phrase) in InterestMapBuilder.ContextPhrases.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var vector = ConceptEmbeddingSource.Instance.Embed(phrase);
            bool zero = true;
            foreach (float component in vector)
            {
                if (Math.Abs(component) > 1e-6f) { zero = false; break; }
            }

            if (!zero) continue;

            dead.Add(suffix);
            if (goldTokens.Contains(suffix)) deadGold.Add(suffix);
        }

        int total = InterestMapBuilder.ContextPhrases.Count;
        bool allRetrievable = dead.Count == 0;

        return new ControlRowSnapshot(
            "AuthoredQueryPhraseRetrievability",
            "every phrase in InterestMapBuilder.ContextPhrases should embed to a NON-ZERO vector under the "
          + "offline concept retriever. That phrase is not decoration: it is the query the searching arms "
          + "issue for the interest. A phrase the lexicon does not know embeds to zero, the dense leg returns "
          + "nothing, and the arm's low score is then a property of the corpus rather than of the arm — "
          + "invisible in a coverage number, which is why it is checked here.",
            $"{dead.Count} of {total} authored phrase(s) embed to the ZERO vector"
          + (dead.Count == 0 ? "." : $": {string.Join(", ", dead)}.")
          + $" Of those, {deadGold.Count} are latent-GOLD tokens for a scored persona"
          + (deadGold.Count == 0 ? "." : $": {string.Join(", ", deadGold)}.")
          + (allRetrievable
                ? " Every arm can at least ASK for every interest the gold rewards."
                : " READ EVERY EVAL 02 ARM NUMBER WITH THIS IN FRONT OF IT: on the interests listed above the "
                + "dense retrieval leg contributes nothing, so a low coverage cell there is not evidence that "
                + "the arm failed to reason. ADVISORY — fixing it means choosing a concept mapping per phrase, "
                + "which moves every coverage cell, so it is reported rather than silently repaired."),
            allRetrievable,
            Gating: false);
    }

    // ══ Control 6 — the METRIC itself. Does latent coverage have room to discriminate? ════

    private static ControlRowSnapshot CheckMetricDiscrimination()
    {
        var lines = new List<string>();
        double worst = 0.0;

        foreach (var persona in CoveragePersonas.All)
        {
            var gold = InterestMapGold.Derive(persona.Id);
            if (gold.LatentIsEmpty)
            {
                lines.Add($"{persona.Id} empty gold");
                continue;
            }

            var (pool, randomLatent, _) = ChanceFloors.RandomDrawFloor(gold);
            worst = Math.Max(worst, randomLatent);
            lines.Add($"{persona.Id} floor {Format(randomLatent)} over {gold.Latent.Count} tokens, pool {pool}");
        }

        // A metric a random draw already satisfies half the time cannot separate an agent that
        // reasoned from one that guessed. This check exists because the FIRST measured run of this
        // suite produced a floor of 0.660 under the design's literal R2 rule, and a control that
        // does one retrieval pass and stops scored 0.978 against it.
        //
        // ⚠ ADVISORY, and the reason matters. The response to that measurement was to add the R2
        // specificity condition (a token most of the catalogue carries is a stopword, not an
        // interest), which is a principled rule and which moved the worst floor from 0.660 to the
        // number below. It would be trivial to keep tightening the threshold until this row went
        // green — and that would be tuning the corpus until it passed its own check, the same shape
        // as letting the artifact under test set its own bar. So the threshold is chosen on
        // principle — InterestMapGold.LatentMaximumCarriers, a CARRIER COUNT of six, which on this
        // 99-SKU catalogue is 6.1% of it and not the "quarter of the catalogue" this comment said
        // while the code had already moved to a count — and whatever floor that produces is REPORTED. A
        // failing row here is a fact about a 99-SKU hand-authored corpus, not a broken instrument,
        // and it must not gate.
        const double discriminationCeiling = 0.50;
        bool withinCeiling = worst < discriminationCeiling;

        return new ControlRowSnapshot(
            "LatentCoverageDiscrimination",
            $"the derived random-draw floor should stay below {discriminationCeiling:F2} for EVERY scored persona. "
          + "A metric whose degenerate agent already scores half is close to a decoration, and an arm comparison "
          + "built on it has little room to move. This is a check on the INSTRUMENT, not on the agent — and it is "
          + "advisory precisely so that nobody is tempted to tune the corpus until it passes.",
            $"worst floor {Format(worst)} (ceiling {discriminationCeiling:F2}) · {string.Join(" · ", lines)} · "
          + $"R2 specificity cap = {InterestMapGold.LatentMaximumCatalogueShare:P0} of the catalogue. "
          + (withinCeiling
                ? "There is room for an arm comparison to mean something."
                : "READ EVERY EVAL 02 NUMBER WITH THIS IN FRONT OF IT: on this corpus latent coverage is close to "
                + "saturated by chance, so a high score is weak evidence and a small difference between arms is no "
                + "evidence at all."),
            withinCeiling,
            Gating: false);
    }

    // ══ Control 5 — the grader itself, checked in BOTH directions. ════════════════════════

    private static ControlRowSnapshot CheckGraderSanity()
    {
        var catalogue = Catalogue.Default;
        var problems = new List<string>();

        // A citation that resolves must resolve, and one that does not must not. Verifying only
        // the failing direction would leave a grader that rejects everything looking perfect.
        var shell = catalogue.Require("GLX-2006");
        var dryBag = catalogue.Require("GLX-8003");

        if (!CatalogueIntegrityGrader.ResolvesEvidence(
                $"{EvidenceRef.AttributePrefix}{GalaxusDemoPrompts.WaterResistantAttributeToken}", shell, out _))
            problems.Add("attr:water-resistant does NOT resolve against GLX-2006 — the grader rejects a true citation.");

        if (CatalogueIntegrityGrader.ResolvesEvidence(
                $"{EvidenceRef.AttributePrefix}{GalaxusDemoPrompts.WaterproofAttributeToken}", shell, out _))
            problems.Add("attr:waterproof RESOLVES against GLX-2006 — C-13's whole temptation is neutralised.");

        if (!CatalogueIntegrityGrader.ResolvesEvidence(
                $"{EvidenceRef.AttributePrefix}{GalaxusDemoPrompts.WaterproofAttributeToken}", dryBag, out _))
            problems.Add("attr:waterproof does NOT resolve against GLX-8003 — C-14 is unpassable.");

        if (CatalogueIntegrityGrader.ResolvesEvidence("", dryBag, out _))
            problems.Add("an EMPTY citation resolves — silence would be scored as evidence.");

        if (CatalogueIntegrityGrader.ResolvesEvidence(Broken01_HallucinatingRecommender.FabricatedEvidence, dryBag, out _))
            problems.Add($"'{Broken01_HallucinatingRecommender.FabricatedEvidence}' resolves against a real product — "
                       + "Broken01's fabricated citation would pass.");

        // The soft-class rate must be UNDEFINED, not perfect, when nothing was presented.
        var emptyReport = new IntegrityRunReport();
        emptyReport.Add(new IntegrityRow(
            IntegrityCases.All[0],
            new IntegrityVerdict(IntegrityCases.All[0].Id, [], 0, 0, 0, [], null),
            [], 0, null, null, null, null));

        if (!double.IsNaN(emptyReport.SoftClassCleanRate))
            problems.Add("a run that presented nothing reports a DEFINED soft-class clean rate — an empty denominator "
                       + "would be scored as a pass.");

        if (emptyReport.SoftOk)
            problems.Add("a run that presented nothing PASSES the soft gate — silence scored as success.");

        // The exact sign test must return 1.0 when everything ties, never a win.
        if (Math.Abs(PairedCoverageReport.ExactTwoSidedSignP(0, 0) - 1.0) > 1e-9)
            problems.Add("the sign test does not return p = 1.0 on an all-tied comparison.");

        if (Math.Abs(PairedCoverageReport.ExactTwoSidedSignP(3, 3) - 0.25) > 1e-9)
            problems.Add("the exact sign test is miscomputing: a 3-0 sweep must give p = 0.25.");

        // The chance-floor arithmetic, against a hand-checkable case.
        double fiveOf76 = ChanceFloors.AtLeastOneHit(76, 1, 5);
        if (Math.Abs(fiveOf76 - 5.0 / 76.0) > 1e-9)
            problems.Add($"AtLeastOneHit(76,1,5) = {fiveOf76:F6}, expected {5.0 / 76.0:F6}.");

        // ── The declared budget: one fact, spelled twice, and every Eval 02 arm sized from it. ──
        //
        // The utterance says a number in words; the harness cuts at a number. If either moves
        // without the other, the live agent is told one budget and scored at another — and the
        // scripted controls would then be sized by a literal no prompt ever declared.
        int k = GalaxusDemoPrompts.CoverageCohortDeclaredK;
        string kInWords = GalaxusDemoPrompts.CoverageCohortDeclaredKInWords;
        string[] words = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];
        if (k < 1 || k >= words.Length || !string.Equals(words[k], kInWords, StringComparison.Ordinal))
            problems.Add($"the declared budget {k} and its words '{kInWords}' disagree — the agent is told one number and scored at another.");
        if (!GalaxusDemoPrompts.CoverageCohortCanonical.Contains($" {kInWords} best", StringComparison.Ordinal))
            problems.Add("the canonical utterance no longer DECLARES the budget in words — the live arm is not being told its k.");
        if (Broken03_SingleShotWorkflow.PresentationCount != k
            || Broken04_PopularityAgent.PresentationCount != k
            || Baseline_TagJoin.PresentationCount != k
            || Galaxus.RecommendationAgent.Evals.Loop.DiscoveryLoopOptions.Default.PresentationCount != k)
            problems.Add("an Eval 02 control sizes itself instead of reading the declared budget.");

        // ── The top-k cut keeps the FIRST k by trace order and nothing else. ──
        var shuffled = new List<PresentedCall>
        {
            Call("GLX-1004", order: 3), Call("GLX-1001", order: 1), Call("GLX-1003", order: 7),
            Call("GLX-1002", order: 2), Call("GLX-1005", order: 5),
        };
        var top3 = InterestCoverageGrader.TopK(shuffled, 3).Select(c => c.Sku).ToList();
        if (!top3.SequenceEqual(["GLX-1001", "GLX-1002", "GLX-1004"], StringComparer.Ordinal))
            problems.Add($"TopK(3) returned [{string.Join(", ", top3)}] — the cut is not the arm's own order.");
        if (InterestCoverageGrader.TopK(shuffled, 0).Count != 0 || InterestCoverageGrader.TopK(shuffled, 99).Count != 5)
            problems.Add("TopK does not bound at 0 and at the list length.");

        // ── At a declared budget: over-filled is cut, under-filled is not padded, silence is 0 not NaN. ──
        var golds = CoveragePersonas.All.ToDictionary(p => p.Id, p => InterestMapGold.Derive(p.Id), StringComparer.Ordinal);
        string nadia = Personas.NadiaUserId;
        if (golds.TryGetValue(nadia, out var nadiaGold) && !nadiaGold.LatentIsEmpty)
        {
            var seven = Enumerable.Range(1, 7).Select(i => Call($"GLX-{1000 + i}", order: i)).ToList();
            var cutSeven = InterestCoverageGrader.GradeAtDeclaredK(nadia, golds, seven, k);
            if (cutSeven.PresentedCount != Math.Min(k, 7) || cutSeven.PresentedBeforeCut != 7 || !cutSeven.OverFilledBudget)
                problems.Add($"a 7-item answer cut at k={k} reports scored {cutSeven.PresentedCount}, shown {cutSeven.PresentedBeforeCut}.");

            var silent = InterestCoverageGrader.GradeAtDeclaredK(nadia, golds, [], k);
            if (double.IsNaN(silent.PrecisionAtK) || silent.PrecisionAtK != 0.0 || !silent.IsSilent)
                problems.Add("a SILENT answer at a declared budget does not score precision 0.000 — silence would be undefined, and undefined reads as not-failed.");

            // The precision floor is R/N and does not move with k; the recall floor at k = 1 is the
            // mean per-token carrier share, which R/N bounds from above (a union is at least as
            // large as any of its parts). Two independently computed numbers, one inequality.
            var (poolN, relevantR, precisionFloor) = ChanceFloors.RandomPrecisionFloor(nadiaGold);
            var (_, recallFloorAt1, _) = ChanceFloors.RandomDrawFloor(nadiaGold, 1);
            if (poolN <= 0 || Math.Abs(precisionFloor - relevantR / (double)poolN) > 1e-12)
                problems.Add("the precision floor is not R/N.");
            if (precisionFloor + 1e-12 < recallFloorAt1)
                problems.Add($"precision floor {precisionFloor:F4} is BELOW the k=1 recall floor {recallFloorAt1:F4} — impossible if both count the same carriers.");
        }
        else
        {
            problems.Add("Nadia's gold is empty — the declared-k checks had no persona to run on.");
        }

        // ── The equal-k rule refuses an unequal pair rather than counting it. ──
        var pairing = new PairedCoverageReport();
        pairing.Record("P1", "A", new CoverageScore(0.5, double.NaN, 1, 2, 0, 0, 3, 0, 0, DeclaredK: 5, PresentedBeforeCut: 3));
        pairing.Record("P1", "B", new CoverageScore(1.0, double.NaN, 2, 2, 0, 0, 5, 0, 0, DeclaredK: 5, PresentedBeforeCut: 5));
        pairing.Record("P2", "A", new CoverageScore(0.5, double.NaN, 1, 2, 0, 0, 5, 0, 0, DeclaredK: 5, PresentedBeforeCut: 5));
        pairing.Record("P2", "B", new CoverageScore(1.0, double.NaN, 2, 2, 0, 0, 5, 0, 0, DeclaredK: 5, PresentedBeforeCut: 5));
        pairing.Record("P3", "A", new CoverageScore(0.0, double.NaN, 0, 2, 0, 0, 0, 0, 0, DeclaredK: 5, PresentedBeforeCut: 0));
        pairing.Record("P3", "B", new CoverageScore(1.0, double.NaN, 2, 2, 0, 0, 5, 0, 0, DeclaredK: 5, PresentedBeforeCut: 5));
        var equalK = pairing.SignTestAtEqualK("A", "B", CoverageMetric.Recall);
        if (equalK.Wins != 1 || equalK.Losses != 0 || equalK.Ties != 0 || equalK.Excluded.Count != 2)
            problems.Add($"the equal-k sign test counted W/L/T {equalK.Wins}/{equalK.Losses}/{equalK.Ties} with {equalK.Excluded.Count} refused — expected 1/0/0 with 2 refused (one unequal k, one silent).");
        var blind = pairing.SignTest("A", "B");
        if (blind.Wins != 3)
            problems.Add("the k-blind sign test no longer counts every pair — Eval 09's reading of it changed underneath it.");

        return new ControlRowSnapshot(
            "GraderSanity",
            "the grader must accept a TRUE citation and reject a false one, must treat an empty denominator as "
          + "undefined rather than perfect, and must compute the sign test and the chance floors correctly. "
          + "It must also cut every arm to the ONE declared budget in the arm's own order, score silence as "
          + "precision 0 rather than undefined, derive a precision floor that is R/N, and REFUSE an unequal-k "
          + "pair rather than count it. Checking only the rejecting direction would leave a grader that rejects "
          + "everything looking flawless.",
            problems.Count == 0 ? "all directions behave" : string.Join(" | ", problems),
            problems.Count == 0);

        static PresentedCall Call(string sku, int order) =>
            new(sku, "", "", false, order, null, true, true);
    }

    // ══ Plumbing ══════════════════════════════════════════════════════════════════════════

    private static async Task<IntegrityRunReport> RunIntegritySuiteAsync(
        Func<IEvaluableAgent> factory,
        MAFEvaluationHarness harness,
        EvaluationOptions options,
        CancellationToken ct)
    {
        var report = new IntegrityRunReport { Architecture = factory().Name };

        foreach (var testCase in IntegrityCases.All)
        {
            var row = await Eval01_CatalogueIntegrity
                .RunCaseAsync(testCase, factory(), harness, options, ct)
                .ConfigureAwait(false);
            report.Add(row);
        }

        return report;
    }

    private static async Task<(double Mean, double Floor, string Detail, int Presented, int Phantom, int Unresolved)>
        MeanCoverageAsync(
            Func<IEvaluableAgent> factory,
            MAFEvaluationHarness harness,
            EvaluationOptions options,
            CancellationToken ct)
    {
        var catalogue = Catalogue.Default;
        var scores = new List<double>();
        var floors = new List<double>();
        var detail = new List<string>();
        int presentedTotal = 0, phantomTotal = 0, unresolvedTotal = 0;

        foreach (var persona in CoveragePersonas.All)
        {
            var gold = InterestMapGold.Derive(persona.Id);
            if (gold.LatentIsEmpty) continue;

            var testCase = new TestCase
            {
                Name = $"{persona.Id} · control coverage",
                Input = persona.Prompt,
                PassingScore = 0,
            };

            TestResult result;
            using (EvalRuntime.BeginTurn())
            {
                result = await harness.RunEvaluationAsync(factory(), testCase, options, ct).ConfigureAwait(false);
            }

            var presented = PresentedCall.FromToolUsage(result.ToolUsage);
            var score = InterestCoverageGrader.Grade(gold, presented);

            // The floor at THIS arm's own k, not at a constant 5 — the same rule Eval 02 gates by.
            var (_, randomFloor, _) = ChanceFloors.RandomDrawFloor(gold, score.PresentedCount);

            presentedTotal += presented.Count;
            foreach (var call in presented)
            {
                if (!catalogue.TryGet(call.Sku, out var product) || product is null) { phantomTotal++; continue; }
                if (!CatalogueIntegrityGrader.ResolvesEvidence(call.Evidence, product, out _)) unresolvedTotal++;
            }

            if (score.IsScorable) scores.Add(score.Latent);
            if (!double.IsNaN(randomFloor)) floors.Add(randomFloor);

            detail.Add($"{persona.Id} {Format(score.Latent)} ({score.LatentServed}/{score.LatentTotal})");
        }

        return (
            scores.Count == 0 ? double.NaN : scores.Average(),
            floors.Count == 0 ? double.NaN : floors.Average(),
            string.Join(", ", detail),
            presentedTotal, phantomTotal, unresolvedTotal);
    }

    private static string Format(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

    // Four decimals where three would round a ±0.01 band to a digit that cannot show which side of it a number fell.
    private static string F4(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 03 — Negative controls: proving the evals CAN fail                     ║
║   Scripted agents · no model calls · no credentials needed                    ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}
