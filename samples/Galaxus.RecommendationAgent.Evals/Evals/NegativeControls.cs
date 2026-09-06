// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo
//
// SNAPSHOT-POLICY: writes            eval03_controls — real and model-free, so it persists on a dry run too

using System.Text.Json;                        // the marshalled JsonElement shape, control 23
using AgentEval.MAF;
using Microsoft.Extensions.AI;                 // AIFunctionFactory — the REAL marshalling path, control 22
using Galaxus.RecommendationAgent.Guardrails;  // ToolSurfaceInvariant.BehaviouralHistoryToolNames
using Galaxus.RecommendationAgent.Retrieval;   // EmbeddingSpace — the ONE place the space is chosen
using Galaxus.RecommendationAgent.Rendering;    // RecommendationPrinter — item 8.15 drives the real renderer
using Galaxus.RecommendationAgent.Signals;     // InterestMapBuilder.ContextPhrases
using Galaxus.RecommendationAgent.Tools;       // GalaxusTools, ToolRefusalCodes — control 22 invokes the real tool
using Galaxus.RecommendationAgent.Workflows;   // DiscoveryInterestMapping.QueryTermsFor — arm D's input

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
        rows.Add(Guarded("MetricDiscrimination", CheckMetricDiscrimination));
        rows.Add(await GuardedAsync("PersonaDiscrimination", () => CheckPersonaDiscriminationAsync(harness, options, ct)).ConfigureAwait(false));
        rows.Add(Guarded("AuthoredQueryPhrasesRetrieve", CheckAuthoredQueryPhrasesRetrieve));
        rows.Add(await GuardedAsync("SilentDenseWipeoutDetectorCanFire", () => CheckSilentDenseWipeoutDetectorCanFireAsync(ct)).ConfigureAwait(false));
        rows.Add(await GuardedAsync("DenseLegSaysWhenItRankedNothing", () => CheckDenseLegSaysWhenItRankedNothingAsync(retriever, ct)).ConfigureAwait(false));
        rows.Add(await GuardedAsync("SuppressionDetectorExercised", () => CheckSuppressionDetectorExercisedAsync(harness, options, ct)).ConfigureAwait(false));

        rows.Add(await GuardedAsync("Hallucinator", () => CheckHallucinatorAsync(harness, options, ct)).ConfigureAwait(false));
        rows.Add(await GuardedAsync("Uncited", () => CheckUncitedAsync(harness, options, ct)).ConfigureAwait(false));
        rows.Add(await GuardedAsync("Broken02Operands", () => CheckBroken02OperandsAsync(harness, options, ct)).ConfigureAwait(false));
        rows.Add(await GuardedAsync("CommitOrdering", () => CheckCommitOrderingAsync(harness, options, ct)).ConfigureAwait(false));
        rows.Add(await GuardedAsync("SingleShot", () => CheckSingleShotAsync(retriever, harness, options, ct)).ConfigureAwait(false));
        rows.Add(await GuardedAsync("Popularity", () => CheckPopularityAsync(harness, options, ct)).ConfigureAwait(false));
        rows.Add(await GuardedAsync("RubberStampLoop", () => CheckRubberStampLoopAsync(retriever, harness, options, ct)).ConfigureAwait(false));
        rows.Add(await GuardedAsync("ConstraintBlindFloor", () => CheckConstraintBlindFloorAsync(harness, options, ct)).ConfigureAwait(false));
        rows.Add(await GuardedAsync("ConstantPolicyCeiling", () => CheckConstantPolicyCeilingAsync(harness, options, ct)).ConfigureAwait(false));
        rows.Add(Guarded("GraderSanity", CheckGraderSanity));
        rows.Add(Guarded("CoverageGateRendering", CheckCoverageGateRendering));
        rows.Add(Guarded("PreRegisteredRuleReachability", CheckPreRegisteredRuleReachability));
        rows.Add(Guarded("OwnKRereadAtVaryingK", CheckOwnKRereadAtVaryingK));
        rows.Add(Guarded("Eval09RuleAndRemedy", CheckEval09RuleAndRemedy));
        rows.Add(Guarded("JudgeEchoJoins", CheckJudgeEchoJoins));
        rows.Add(await GuardedAsync("ContentlessRequestIsNotCovered", () => CheckContentlessRequestIsNotCoveredAsync(retriever, ct)).ConfigureAwait(false));
        rows.Add(Guarded("UnnameableInterestPresentsNothing", CheckUnnameableInterestPresentsNothing));
        rows.Add(await GuardedAsync("RefusalDetectorsSeeTheRealShape", CheckRefusalDetectorsSeeTheRealShapeAsync).ConfigureAwait(false));
        rows.Add(Guarded("RefusalCodesDoNotAnswerForEachOther", CheckRefusalCodesDoNotAnswerForEachOther));
        rows.Add(Guarded("WriteLedgerMatchesTheStore", CheckWriteLedgerMatchesTheStore));
        rows.Add(Guarded("EveryEvalDeclaresItsSnapshotPolicy", CheckEveryEvalDeclaresItsSnapshotPolicy));
        rows.Add(Guarded("AboveChanceIsAnExactTest", CheckAboveChanceIsAnExactTest));
        rows.Add(Guarded("ForcedChoiceCountIsACountOfPersonas", CheckForcedChoiceCountIsACountOfPersonas));
        rows.Add(Guarded("CiChainRunsModelFreeEvalsForReal", CheckCiChainRunsModelFreeEvalsForReal));
        rows.Add(Guarded("ARunThatSaysItSpendsSaysHowMuch", CheckARunThatSaysItSpendsSaysHowMuch));
        rows.Add(Guarded("TheChatLaneSaysWhatItSpent", CheckTheChatLaneSaysWhatItSpent));
        rows.Add(await GuardedAsync("MinCandidateScoreDecidesNothing", () => CheckMinCandidateScoreDecidesNothingAsync(retriever, ct)).ConfigureAwait(false));
        rows.Add(Guarded("CoverageCutIsNotTheConfidenceShapeParameter", CheckCoverageCutIsNotTheConfidenceShapeParameter));
        rows.Add(await GuardedAsync("LoopBackNegativeDirectionCensus", () => CheckLoopBackNegativeDirectionCensusAsync(retriever, ct)).ConfigureAwait(false));
        rows.Add(await GuardedAsync("TopologyCaseProseMatchesTheRun", () => CheckTopologyCaseProseMatchesTheRunAsync(retriever, ct)).ConfigureAwait(false));
        rows.Add(Guarded("VacuityIsDeclaredNotInferred", CheckVacuityIsDeclaredNotInferred));
        rows.Add(Guarded("EverySnapshotSaysWhatProducedIt", CheckEverySnapshotSaysWhatProducedIt));
        rows.Add(Guarded("CatalogueEvidenceLineCarriesAFact", CheckCatalogueEvidenceLineCarriesAFact));
        rows.Add(Guarded("CommittedVectorsAreTheRightNumbers", CheckCommittedVectorsAreTheRightNumbers));
        rows.Add(Guarded("APersonaInOneArmOnlyIsDeclared", CheckAPersonaInOneArmOnlyIsDeclared));
        rows.Add(Guarded("AssertionFaultsAreNamedAndNotGated", CheckAssertionFaultsAreNamedAndNotGated));
        rows.Add(Guarded("CostRowsSayWhichZeroTheyMean", CheckCostRowsSayWhichZeroTheyMean));
        rows.Add(Guarded("RepSpreadNeverInventsAZero", CheckRepSpreadNeverInventsAZero));
        rows.Add(await GuardedAsync("TheJudgedPathIsReachableWithoutPaying", CheckTheJudgedPathIsReachableWithoutPayingAsync).ConfigureAwait(false));
        rows.Add(Guarded("TheAnswerTheCustomerReadsIsScreenedToo", CheckTheAnswerTheCustomerReadsIsScreenedToo));
        rows.Add(Guarded("ApplicableFractionDoesNotPoolTwoAbsences", CheckApplicableFractionDoesNotPoolTwoAbsences));
        rows.Add(Guarded("DeadPhrasesAreDiagnosedNotJustCounted", CheckDeadPhrasesAreDiagnosedNotJustCounted));
        rows.Add(Guarded("EveryControlRowIsContained", CheckEveryControlRowIsContained));

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

    // ══ CONTAINMENT — a row that throws must fail as a row, never take the panel with it. ══
    //
    /// <summary>
    /// The expectation printed for a row that threw instead of returning a verdict.
    /// </summary>
    internal const string ContainmentExpectation =
        "every control row must RUN. A row that throws reports nothing, and an UNCONTAINED throw in this panel "
      + "unwinds every other row AND loses eval03_controls.json with them — the run that found something ends "
      + "with no record that it found it. Measured 2026-09-06: ablation D of plan item 8.20 killed the process "
      + "(exit 127) out of the store's serialiser and took all 23 rows with it. That was contained inside ONE "
      + "row; this contains the panel.";

    /// <summary>
    /// Runs one control row and converts a throw into a FAILED gating row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>This cannot turn a failure into a pass.</b> A row that returns is returned unchanged —
    /// the guard is on the exceptional path only — and a row that throws comes back with
    /// <c>Tripped: false</c> and <c>Gating: true</c>, so the panel still exits 1. What changes is
    /// that the reader is told WHICH row died and with what, and the other twenty-four rows and the
    /// snapshot survive.
    /// </para>
    /// <para>
    /// ⚠ <b>Cancellation is not a control failure and is NOT caught.</b> A cancelled run has no
    /// verdict to report and must stop, not print a red row that reads like a defect.
    /// </para>
    /// <para>
    /// <b>Why this is needed here and not only in the row that found it.</b> Rows 22–25 read the
    /// source tree, invoke a real <c>AIFunction</c>, write and delete files in a shared store, and
    /// use reflection. <see cref="SampleSourceRoot"/> alone throws
    /// <see cref="DirectoryNotFoundException"/> whenever the eval binary runs from anywhere but the
    /// repository tree.
    /// </para>
    /// </remarks>
    /// <param name="name">The row's name, needed because a row that threw produced none.</param>
    /// <param name="row">The control.</param>
    private static ControlRowSnapshot Guarded(string name, Func<ControlRowSnapshot> row)
    {
        try
        {
            return row();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Threw(name, ex);
        }
    }

    /// <summary>The asynchronous half of <see cref="Guarded"/>.</summary>
    /// <param name="name">The row's name.</param>
    /// <param name="row">The control.</param>
    private static async Task<ControlRowSnapshot> GuardedAsync(string name, Func<Task<ControlRowSnapshot>> row)
    {
        try
        {
            return await row().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Threw(name, ex);
        }
    }

    /// <summary>The failed row a throw becomes. Names the type and message, because "it threw" is unactionable.</summary>
    /// <param name="name">The row's name.</param>
    /// <param name="ex">What came out of it.</param>
    private static ControlRowSnapshot Threw(string name, Exception ex) => new(
        name,
        ContainmentExpectation,
        $"the row THREW {ex.GetType().Name}: {Shorten(ex.Message, 140)} — it reported no verdict, so it is NOT a pass. "
      + "The panel continued and the snapshot was still written.",
        false);

    // ══ Control 26 — the panel must survive a row that throws (Wave 2 review). ════════════
    //
    /// <summary>
    /// A control row that throws must come back as a FAILED row, and a row that returns must come
    /// back untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this closes.</b> Every row was added straight into the list with no
    /// containment. Wave 2's own commit message records what that costs: ablation D of plan item
    /// 8.20 threw <c>ArgumentException</c> out of the store's serialiser, <i>"killed the process
    /// (exit 127) and took the whole panel with it"</i>. It was contained inside that one row and
    /// the panel was left open — and Wave 2 then added three rows that read the source tree, invoke
    /// a real <c>AIFunction</c>, and write and delete files. <see cref="SampleSourceRoot"/> throws
    /// outright whenever this binary runs from outside the repository.
    /// </para>
    /// <para>
    /// ⚠ <b>Both directions, or the guard is a blanket pass.</b> A guard that returned a green row
    /// on a throw would be the worst possible version of this — so the row asserts the failed row
    /// is <c>Tripped: false</c> AND <c>Gating: true</c>, that it names the exception type and the
    /// message, and that a SUCCEEDING row comes back byte-for-byte as itself.
    /// </para>
    /// <para>
    /// ⚠ <b>It asserts its own INPUT.</b> A guard nothing routes through is a guard that does not
    /// exist. The panel's own source is read and every <c>rows.Add(</c> is required to go through
    /// <c>Guarded(</c> or <c>GuardedAsync(</c> — the shape <c>8f3e11c7</c> fixed in the meta-lane
    /// grep gate, and the shape control 24 asserts one file over.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckEveryControlRowIsContained()
    {
        var problems = new List<string>();

        // ── 1. A throwing row becomes a FAILED GATING row that names what happened. ──
        const string message = "deliberate: the panel must survive this";
        var thrown = Guarded("probe-that-throws", () => throw new InvalidOperationException(message));

        if (thrown.Tripped)
            problems.Add("a row that THREW came back tripped — the guard turns a dead control into a green one, which is worse than the crash.");
        if (!thrown.Gating)
            problems.Add("a row that threw came back ADVISORY — a control that could not run would stop failing the build.");
        if (!thrown.Observed.Contains(nameof(InvalidOperationException), StringComparison.Ordinal))
            problems.Add($"the failed row does not name the exception TYPE: \"{Shorten(thrown.Observed, 70)}\".");
        if (!thrown.Observed.Contains(message, StringComparison.Ordinal))
            problems.Add("the failed row does not carry the exception MESSAGE — \"it threw\" is a finding nobody can act on.");
        if (!string.Equals(thrown.Name, "probe-that-throws", StringComparison.Ordinal))
            problems.Add($"the failed row is named '{thrown.Name}' — a reader cannot tell which control died.");

        // ── 2. …and a row that RETURNS is returned unchanged. Without this the guard could be a
        //       blanket verdict and every row above it would mean nothing. ──
        var green = new ControlRowSnapshot("probe-that-passes", "expectation", "observed", true);
        var passed = Guarded("probe-that-passes", () => green);
        if (!ReferenceEquals(passed, green))
            problems.Add("a row that returned normally did not come back as itself — the guard is rewriting verdicts.");

        var red = new ControlRowSnapshot("probe-that-fails", "expectation", "observed", false);
        if (Guarded("probe-that-fails", () => red).Tripped)
            problems.Add("a row that returned FALSE came back tripped — the guard is a blanket pass.");

        // ── 3. The async half, and cancellation left alone. A cancelled run has no verdict and
        //       must stop; a red row reading like a defect would be a lie about the corpus. ──
        var thrownAsync = GuardedAsync("probe-async", () => throw new InvalidOperationException(message))
            .GetAwaiter().GetResult();
        if (thrownAsync.Tripped)
            problems.Add("the ASYNC guard returned a tripped row for a throw — half the panel is unprotected.");

        try
        {
            _ = Guarded("probe-cancelled", () => throw new OperationCanceledException());
            problems.Add("an OperationCanceledException was swallowed into a failed row — a cancelled run would print a defect it did not find.");
        }
        catch (OperationCanceledException)
        {
            // Correct: cancellation propagates.
        }

        // ── 4. The row's own input: every row in the panel actually goes through the guard. ──
        int added = 0;
        int guardedCalls = 0;
        try
        {
            foreach (string line in File.ReadAllLines(Path.Combine(SampleSourceRoot(), "Evals", "NegativeControls.cs")))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("rows.Add(", StringComparison.Ordinal)) continue;

                added++;
                if (trimmed.Contains("Guarded(", StringComparison.Ordinal)
                 || trimmed.Contains("GuardedAsync(", StringComparison.Ordinal))
                {
                    guardedCalls++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problems.Add($"the panel's own source could not be read ({ex.GetType().Name}) — this row cannot assert what it claims.");
        }

        if (added < 20)
            problems.Add($"only {added} `rows.Add(` line(s) were found in the panel's source — the scan is not reading what it thinks it is.");
        if (added != guardedCalls)
            problems.Add($"{added - guardedCalls} of {added} row(s) are added WITHOUT the guard — an uncontained row still takes the panel and the snapshot with it.");

        return new ControlRowSnapshot(
            "EveryControlRowIsContained",
            ContainmentExpectation,
            problems.Count == 0
                ? $"a throwing row comes back FAILED and GATING, naming InvalidOperationException and its message · a "
                + $"row that returns comes back as itself, pass and fail alike · the async half behaves the same · an "
                + $"OperationCanceledException still propagates · and all {added} `rows.Add(` line(s) in this file go "
                + "through the guard"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
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

        bool tripped = Broken02Tripped(report);

        return new ControlRowSnapshot(
            nameof(Broken02_UncitedRecommender),
            "PASS D1 and D2 (it presents real, in-stock SKUs) while FAILING D5 on every presentation "
          + "(no citation at all) and the policy cases C-05 / C-07 / C-09 (it is policy-blind). This is the "
          + "control that proves the suite distinguishes WHICH invariant broke. Asserted PER CASE AND PER "
          + "CLASS — D3 on C-05, D5 on C-07, D4 on C-09 — never as an OR over whole-run class counts. "
          + "⚠ CORRECTED: this text used to claim the control fails C-07 as a POLICY case. Measured per "
          + "case, C-07's D3 is DEAD for this arm — it echoes nothing sensitive and its boilerplate reason "
          + "names no special-category term — so C-07 fails on CITATION here and that is not evidence the "
          + "suppression detector works. See SuppressionDetectorExercised.",
            $"clean cases {report.CleanCaseCount}/{report.CaseCount} · D1 {d1} (must be 0) · D5 {d5} (must be > 0) · "
          + $"D3 {d3} · D4 {d4} · gate {(report.Passed ? "PASSED — WRONG" : "failed, as required")} · per case: "
          + $"{DescribeBroken02Operands(report)}",
            tripped);
    }

    /// <summary>
    /// <see cref="Broken02_UncitedRecommender"/>'s control assertion, as one named predicate so the
    /// row and the ablation control below evaluate the SAME expression.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>§8/B-10 — this used to be <c>d1 == 0 &amp;&amp; d5 &gt; 0 &amp;&amp; (d3 &gt; 0 || d4 &gt; 0)</c>
    /// over whole-run class COUNTS.</b> An OR over counts is satisfied by either operand from
    /// anywhere in the fourteen cases, so a completely dead D3 detector still printed
    /// <c>✅ caught</c> on the strength of the single D4 defect on C-09 — a control passing for the
    /// wrong reason, inside the harness whose only job is to show the instrument can fail. It also
    /// failed in the flattering direction, which is the direction this suite treats as the
    /// dangerous one.
    /// </para>
    /// <para>
    /// The predicate is now a CONJUNCTION over named cases and named classes: D3 on C-05 (the
    /// gift-derived department leaks), D5 on C-07, and D4 on C-09 (history read despite the
    /// opt-out). Strike any one and the predicate must go false;
    /// <see cref="CheckBroken02OperandsAsync"/> proves it does, one operand at a time.
    /// </para>
    /// <para>
    /// ⚠ <b>C-07 is asserted on D5, not on D3, and that is a CORRECTION rather than a softening.</b>
    /// The row's expectation text claimed this control fails "the policy cases C-05 / C-07 / C-09",
    /// and the per-case rewrite MEASURED that claim for the first time: <c>C-07 D3 DEAD</c>. It is
    /// not a regression — this control never tripped C-07's suppression detector. It echoes SKUs
    /// from the customer's own root departments, none of which is sensitive for Elena, and its
    /// boilerplate <c>reason</c> names no special-category term, so neither the category arm nor the
    /// output-layer term screen has anything to fire on. What it does do on C-07 is present
    /// uncited, so the case fails on D5. Asserting D5 there says exactly that and no more: <b>C-07
    /// failing for this control is NOT evidence that the suppression detector works</b> — see
    /// <see cref="CheckSuppressionDetectorExercisedAsync"/>, which measures that separately and reports
    /// what it finds.
    /// </para>
    /// </remarks>
    /// <param name="report">A completed run of the fourteen cases against the control.</param>
    public static bool Broken02Tripped(IntegrityRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return report.CountOf(DefectClasses.PhantomSku) == 0
            && report.CountOf(DefectClasses.UnresolvableEvidence) > 0
            && report.CaseFailedWith("C-05", DefectClasses.SuppressedSignalLeak)
            && report.CaseFailedWith("C-07", DefectClasses.UnresolvableEvidence)
            && report.CaseFailedWith("C-09", DefectClasses.UnauthorisedAction)
            && !report.Passed;
    }

    /// <summary>Renders the three per-case operands, so the row prints what it actually read.</summary>
    /// <remarks>
    /// C-07's D3 is printed alongside the asserted D5, not instead of it. The assertion does not
    /// read it — but a reader has to see that it is DEAD, because the row's old expectation text
    /// said it fired.
    /// </remarks>
    /// <param name="report">The run.</param>
    private static string DescribeBroken02Operands(IntegrityRunReport report) =>
        $"C-05 D3 {Yn(report.CaseFailedWith("C-05", DefectClasses.SuppressedSignalLeak))}, "
      + $"C-07 D5 {Yn(report.CaseFailedWith("C-07", DefectClasses.UnresolvableEvidence))} "
      + $"(C-07 D3 {Yn(report.CaseFailedWith("C-07", DefectClasses.SuppressedSignalLeak))} — not asserted), "
      + $"C-09 D4 {Yn(report.CaseFailedWith("C-09", DefectClasses.UnauthorisedAction))}";

    private static string Yn(bool value) => value ? "fired" : "DEAD";

    // ══ Control 2b — the assertion above, checked against a DEAD detector. ════════════════

    /// <summary>
    /// Proves <see cref="Broken02Tripped"/>'s three per-case operands are each load-bearing, by
    /// re-evaluating it on runs with one detector struck out at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the row that would have caught B-10. The old OR-over-counts predicate stays TRUE
    /// with D3 struck from both suppression cases — D4 on C-09 carries it alone — so this control
    /// goes RED the moment anyone rewrites the assertion back into that shape. It is a check on the
    /// CHECK, and it is here because the gate-self-examination rule this repository keeps
    /// re-learning says never let a passing row stand without proving each of its inputs could have
    /// failed it.
    /// </para>
    /// <para>
    /// The ablation is done on a COPY of the report (<see cref="IntegrityRunReport.WithDetectorDisabled"/>),
    /// not by disabling a detector in the grader, so the control needs no live seam and nothing in
    /// the shipped path changes shape to accommodate it.
    /// </para>
    /// </remarks>
    private static async Task<ControlRowSnapshot> CheckBroken02OperandsAsync(
        MAFEvaluationHarness harness, EvaluationOptions options, CancellationToken ct)
    {
        var report = await RunIntegritySuiteAsync(
            () => new Broken02_UncitedRecommender(), harness, options, ct).ConfigureAwait(false);

        bool baseline = Broken02Tripped(report);

        (string Label, bool StillTrue)[] ablations =
        [
            ("D3 dead on C-05",
                Broken02Tripped(report.WithDetectorDisabled("C-05", DefectClasses.SuppressedSignalLeak))),
            ("D5 dead on C-07",
                Broken02Tripped(report.WithDetectorDisabled("C-07", DefectClasses.UnresolvableEvidence))),
            ("D4 dead on C-09",
                Broken02Tripped(report.WithDetectorDisabled("C-09", DefectClasses.UnauthorisedAction))),
        ];

        var survivors = ablations.Where(a => a.StillTrue).Select(a => a.Label).ToList();
        bool tripped = baseline && survivors.Count == 0;

        return new ControlRowSnapshot(
            "Broken02AssertionOperandsLoadBearing",
            "the Broken02 assertion must be TRUE on the real run and FALSE with any ONE of its three "
          + "per-case operands struck out (D3 on C-05, D5 on C-07, D4 on C-09). The predicate it "
          + "replaced — an OR over whole-run class counts — stays true with the D3 detector removed "
          + "entirely, and printed '✅ caught' anyway (§8, B-10).",
            $"assertion on the real run {(baseline ? "TRUE, as required" : "FALSE — the control itself did not trip")} · "
          + "with one detector dead: "
          + string.Join(", ", ablations.Select(a => $"{a.Label} → {(a.StillTrue ? "STILL TRUE — not load-bearing" : "false, as required")}"))
          + (survivors.Count > 0
                ? $" · {survivors.Count} operand(s) are not carrying the assertion: {string.Join("; ", survivors)}"
                : ""),
            tripped);
    }

    // ══ Instrument finding — which hard detectors any control has DEMONSTRATED. ══════════

    /// <summary>
    /// Measures, per suppression case, whether ANY scripted control has been shown to trip its D3
    /// detector — and names the cases where none has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This row exists because the B-10 rewrite uncovered it.</b> Replacing
    /// <c>Broken02</c>'s OR-over-class-counts with a per-case, per-class conjunction measured a
    /// claim nobody had measured: the row's expectation text said the control fails "the policy
    /// cases C-05 / C-07 / C-09", and C-07's D3 turned out never to have fired. The OR had been
    /// satisfied by C-05's D3 and C-09's D4 the whole time.
    /// </para>
    /// <para>
    /// That matters more than one control's bookkeeping. C-07 is design §0.5 / D-6 made executable
    /// — the Target-pregnancy case — and its own <c>ChanceFloor</c> note says the category screen
    /// "fires on nothing" for Elena, so <b>the output-layer term screen over the <c>reason</c>
    /// argument is the arm that actually carries it</b>. A detector no control has ever been shown
    /// to trip is a detector whose green runs carry no information, which is the one thing this eval
    /// exists to prevent. So it is measured here and printed, per case, whatever the answer is.
    /// </para>
    /// <para>
    /// ADVISORY, deliberately. It is a fact about the CONTROL SET's coverage, not about wiring the
    /// evals under test — the same category as the three instrument rows at the top of this run —
    /// and closing it means authoring a control that leaks a special-category term in a
    /// <c>reason</c>, which is a corpus change with its own measurement, not a build fix.
    /// </para>
    /// </remarks>
    private static async Task<ControlRowSnapshot> CheckSuppressionDetectorExercisedAsync(
        MAFEvaluationHarness harness, EvaluationOptions options, CancellationToken ct)
    {
        // Every control that runs the fourteen cases, so "no control trips it" is a statement about
        // the control set and not about the one arm that happened to be looked at.
        (string Name, Func<IEvaluableAgent> Factory)[] arms =
        [
            (nameof(Broken01_HallucinatingRecommender), () => new Broken01_HallucinatingRecommender()),
            (nameof(Broken02_UncitedRecommender), () => new Broken02_UncitedRecommender()),
        ];

        var suppressionCases = IntegrityCases.All
            .Where(c => c.ForbiddenCategories.Count > 0 || c.ForbiddenSkus.Count > 0)
            .Select(c => c.Id)
            .ToList();

        var trippedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string id in suppressionCases) trippedBy[id] = [];

        foreach (var (name, factory) in arms)
        {
            var report = await RunIntegritySuiteAsync(factory, harness, options, ct).ConfigureAwait(false);
            foreach (string id in suppressionCases)
            {
                if (report.CaseFailedWith(id, DefectClasses.SuppressedSignalLeak)) trippedBy[id].Add(name);
            }
        }

        var undemonstrated = suppressionCases.Where(id => trippedBy[id].Count == 0).ToList();

        return new ControlRowSnapshot(
            "SuppressionDetectorExercised",
            "every suppression case (D3) should have at least ONE negative control demonstrating its detector "
          + "can fire. A detector no control has been shown to trip cannot make a clean run mean anything. "
          + "ADVISORY: this is a gap in the CONTROL SET, and closing it means authoring a control that leaks — "
          + "a corpus change with its own measurement, not a build fix.",
            string.Join(" · ", suppressionCases.Select(id =>
                $"{id} D3 {(trippedBy[id].Count > 0 ? $"demonstrated by {string.Join("/", trippedBy[id])}" : "⚠️ NOT DEMONSTRATED by any control")}"))
          + (undemonstrated.Count > 0
                ? $" — {undemonstrated.Count} suppression case(s) have an UNEXERCISED D3 detector: "
                + $"{string.Join(", ", undemonstrated)}. Uncovered by the §8/B-10 per-case rewrite; the OR over "
                + "class counts it replaced could not see it."
                : ""),
            undemonstrated.Count == 0,
            Gating: false);
    }

    // ══ Control 2c — C-12's intra-turn commit ordering, checked in BOTH directions. ══════

    /// <summary>
    /// Proves C-12's ordering clause discriminates: an agent that calls <c>PlaceOrder</c> FIRST
    /// must fail the case, and an agent that names the same SKU before ordering must not fail it
    /// <i>for that reason</i>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §8/B-19. C-12 required <c>PlaceOrder</c> and asserted NOTHING about ordering, so an agent
    /// that committed to a SKU no call in the graded turn had ever named scored a clean pass. One
    /// direction alone would not settle it: a clause that fails everything discriminates as little
    /// as one that fails nothing, so the grounded arm is run too and must NOT pick up the ordering
    /// defect.
    /// </para>
    /// <para>
    /// Both arms are scripted, deterministic and go through <c>Eval01.RunCaseAsync</c> — the same
    /// path the live agent takes — so what is being demonstrated is the shipped grader, not a
    /// reimplementation of it.
    /// </para>
    /// </remarks>
    private static async Task<ControlRowSnapshot> CheckCommitOrderingAsync(
        MAFEvaluationHarness harness, EvaluationOptions options, CancellationToken ct)
    {
        IntegrityCase? c12 = IntegrityCases.All.FirstOrDefault(c => string.Equals(c.Id, "C-12", StringComparison.Ordinal));

        if (c12?.RequireSkuGroundingBefore is not { Length: > 0 } commitTool)
        {
            return new ControlRowSnapshot(
                "CommitOrderingDiscriminates",
                "C-12 must carry an intra-turn ordering clause (§8, B-19).",
                c12 is null
                    ? "C-12 is not in the case set at all."
                    : "C-12 carries NO RequireSkuGroundingBefore — the case asserts nothing about ordering, which "
                    + "is the B-19 defect itself.",
                false);
        }

        const string sku = "GLX-7001";

        var blind = await Eval01_CatalogueIntegrity.RunCaseAsync(
            c12, new BlindCommitArm(sku), harness, options, ct).ConfigureAwait(false);
        var grounded = await Eval01_CatalogueIntegrity.RunCaseAsync(
            c12, new GroundedCommitArm(sku), harness, options, ct).ConfigureAwait(false);

        static bool OrderingDefect(Graders.IntegrityRow row) =>
            row.Verdict.Of(DefectClasses.MissingRequirement)
               .Any(d => d.Subject.StartsWith("PlaceOrder(", StringComparison.Ordinal));

        bool blindFails = OrderingDefect(blind) && !blind.Verdict.Clean;
        bool groundedClear = !OrderingDefect(grounded);
        bool bothOrdered = blind.Verdict.ToolNamesCalled.Contains(commitTool, StringComparer.Ordinal)
                        && grounded.Verdict.ToolNamesCalled.Contains(commitTool, StringComparer.Ordinal);

        bool tripped = blindFails && groundedClear && bothOrdered;

        return new ControlRowSnapshot(
            "CommitOrderingDiscriminates",
            $"on C-12, an arm that calls {commitTool} FIRST — with nothing in the graded turn naming the SKU — must "
          + "FAIL the case on the ordering clause, and an arm that names the same SKU before ordering must NOT pick "
          + "up that defect. Both arms must actually reach the commit tool, or the comparison is between two "
          + "silences (§8, B-19).",
            $"both arms called {commitTool}: {(bothOrdered ? "yes" : "NO — one of them never committed")} · "
          + $"blind arm: {(OrderingDefect(blind) ? "ordering defect raised" : "NO ordering defect — the clause is dead")}, "
          + $"{blind.Verdict.Defects.Count} defect(s), clean {blind.Verdict.Clean} · "
          + $"grounded arm: {(OrderingDefect(grounded) ? "ordering defect raised — the clause fires on a grounded commit" : "no ordering defect, as required")}, "
          + $"{grounded.Verdict.Defects.Count} defect(s)",
            tripped);
    }

    /// <summary>
    /// The blind-commit arm: it orders on the graded turn and nothing before it names the SKU.
    /// </summary>
    /// <remarks>
    /// It DOES fetch the profile first, so the arm is not "an agent that calls one tool" — the
    /// ordering clause has to distinguish a turn with prior calls that ground nothing from a turn
    /// with prior calls that ground the commit, not merely a turn with one call from a turn with
    /// two.
    /// </remarks>
    /// <param name="sku">The SKU it commits to.</param>
    private sealed class BlindCommitArm(string sku) : IEvaluableAgent
    {
        /// <inheritdoc/>
        public string Name => nameof(BlindCommitArm);

        /// <inheritdoc/>
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            string userId = ScriptedTrace.PersonaIdFrom(prompt) ?? Personas.NadiaUserId;

            var trace = new ScriptedTrace()
                .Call("GetUserProfile", new Dictionary<string, object?>(StringComparer.Ordinal) { ["userId"] = userId })
                .CallWithoutResult("PlaceOrder", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [PresentRecommendationArguments.Sku] = sku,
                    ["quantity"] = 1,
                })
                .Say("Ordered.");

            return Task.FromResult(trace.ToResponse());
        }
    }

    /// <summary>
    /// The grounded-commit arm: same order, but a details fetch names the SKU first.
    /// </summary>
    /// <param name="sku">The SKU it looks up and then commits to.</param>
    private sealed class GroundedCommitArm(string sku) : IEvaluableAgent
    {
        /// <inheritdoc/>
        public string Name => nameof(GroundedCommitArm);

        /// <inheritdoc/>
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            string userId = ScriptedTrace.PersonaIdFrom(prompt) ?? Personas.NadiaUserId;

            var trace = new ScriptedTrace()
                .Call("GetUserProfile", new Dictionary<string, object?>(StringComparer.Ordinal) { ["userId"] = userId })
                .Call("GetProductDetails", new Dictionary<string, object?>(StringComparer.Ordinal) { ["sku"] = sku })
                .CallWithoutResult("PlaceOrder", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [PresentRecommendationArguments.Sku] = sku,
                    ["quantity"] = 1,
                })
                .Say("Confirmed and ordered.");

            return Task.FromResult(trace.ToResponse());
        }
    }

    // ══ Control 3 — must score LOW latent coverage. ═══════════════════════════════════════

    private static async Task<ControlRowSnapshot> CheckSingleShotAsync(
        Galaxus.RecommendationAgent.Retrieval.IProductRetriever retriever,
        MAFEvaluationHarness harness, EvaluationOptions options, CancellationToken ct)
    {
        var (mean, floor, detail, presented, phantom, unresolved, _) = await MeanCoverageAsync(
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
        var (mean, floor, detail, presented, _, _, perPersona) = await MeanCoverageAsync(
            () => new Broken04_PopularityAgent(), harness, options, ct).ConfigureAwait(false);

        // A persona-blind arm must land BELOW a random draw from the eligible pool, because a random
        // draw at least samples the pool the customer's interests live in while the bestseller list
        // does not look at the customer at all. That is a bar this arm can fail, and it is the
        // empirical check that the floor arithmetic elsewhere in this project is right.
        //
        // The design pre-registers 0.00 here. That figure belongs to a bestseller list AUTHORED to
        // carry no latent tokens; this catalogue derives its list from rating counts, so the number
        // is MEASURED and whatever it comes out at is what the report carries.
        // ⚠ PLAN 1.8 / N-8 — PER PERSONA, not mean to mean. A mean-to-mean comparison passes an arm
        //   at 1.000 / 0.000 / 0.000 (mean 0.333) against a 0.462 floor while it is at the CEILING
        //   on a third of the corpus. That is the same defect class as Eval 02's floor gate, and it
        //   fails in the flattering direction: the control looks like it caught something.
        var scorable = perPersona.Where(p => !double.IsNaN(p.Score) && !double.IsNaN(p.Floor)).ToList();
        var clears   = scorable.Where(p => p.Score >= p.Floor).ToList();

        // ⚠ AND THE ARM MUST HAVE PRESENTED SOMETHING. Found while doing 1.8: the MEAN form of this
        //   row asserted only that the arm scores LOW, and an arm that presents nothing means 0.000
        //   and passed that bar vacuously — the element-missing shape. 0.000 on 12 of 12 is an
        //   extreme value, and §7 rule 6 says an extreme value is a wiring fault until shown
        //   otherwise. `CheckSingleShotAsync` already asserts this of its comparator; this row did
        //   not, and it is the row whose arm is SUPPOSED to score zero — which is exactly why the
        //   distinction between "scored zero" and "was never asked" has to be made here.
        //
        // ⚠ CORRECTED 2026-09-06 by review — say what this clause does and does NOT add, because
        //   the first revision claimed more for it than it earns:
        //   (a) The PER-PERSONA bar above already refuses a silent arm on its own. A persona with
        //       nothing presented is scored against `RandomDrawFloor(gold, k = 0)`, and
        //       `ChanceFloors.AtLeastOneHit` returns 0.0 for k <= 0 — so the pair is 0.000 vs 0.000,
        //       `Score >= Floor` is TRUE, the persona is counted as CLEARING its floor and the row
        //       goes red. The mean form had no such degenerate-floor pairing, which is where the
        //       vacuous pass actually lived.
        //   (b) `presented` is a COHORT TOTAL, so this clause cannot see a per-persona absence — an
        //       arm silent on eleven of twelve customers still satisfies it. It is a second,
        //       coarser witness, kept because it is the one a reader can check by eye, not the
        //       screen that does the work.
        bool tripped = scorable.Count > 0 && clears.Count == 0 && presented > 0;

        return new ControlRowSnapshot(
            nameof(Broken04_PopularityAgent),
            "PRESENT something, and then score BELOW its OWN random-draw floor on EVERY scorable persona — a "
          + "persona-blind arm must do "
          + "worse than a random draw from the pool that customer's interests actually live in, and it must "
          + "do so customer by customer. ⚠ The mean-to-mean form of this bar (plan 1.8 / N-8) passes an arm "
          + "at 1.000/0.000/0.000 on a mean of 0.333 while it is at the ceiling on a third of the corpus. "
          + "NOTE: the design pre-registers 0.00 for this arm, but that belongs to an authored bestseller "
          + "list; this catalogue's is derived, so the value is MEASURED. Selection: "
          + $"{string.Join(", ", Broken04_PopularityAgent.Selection)}.",
            $"{presented} recommendation(s) presented across the cohort (a COHORT total — it cannot see a "
          + "per-persona absence; the degenerate floor at k = 0 is what refuses a silent arm customer by "
          + "customer) · "
          + $"{scorable.Count - clears.Count} of {scorable.Count} persona(s) below their own floor"
          + (clears.Count == 0
                ? string.Empty
                : " · ⚠ CLEARS ITS FLOOR ON: "
                + string.Join(", ", clears.Select(c => $"{c.Persona} {Format(c.Score)} ≥ {Format(c.Floor)}")))
          + $" · (mean latent {Format(mean)} vs mean floor {Format(floor)}, reported for continuity and NOT "
          + $"what this row gates on) · {detail}",
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

        // ⚠ 1.4 / N-4 — the SAME exact test the forced-choice panel prints, through the SAME
        //   method. `rate > floor` here said the oracle discriminates on 2 of 12 (p = 0.264).
        var (aboveChance, chanceP) = ExactBinomial.AboveChance(wins, decided, floor);

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
          + $"the {Format(floor)} chance rate (1/{scorable}) — judged by an EXACT one-sided binomial at p ≤ "
          + $"{ExactBinomial.Alpha:0.00}, never by rate > chance (plan 1.4 / N-4). If it cannot, latent coverage "
          + "carries no evidence about personalisation and no Eval 02 comparison between architectures means anything.",
            $"oracle forced choice {Format(rate)} ({wins} of {decided}) vs chance {Format(floor)} · "
          + $"{ExactBinomial.FormatP(chanceP)} · "
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
    /// <para>
    /// <b>B-6 (2026-09-05) added a SECOND arm, and the row was deliberately still red.</b> Arm A
    /// was the offline concept path above and arm B the committed real-vector path
    /// (<see cref="PrecomputedEmbeddingSource"/> over the two <c>text-embedding-3-small</c> assets,
    /// no live fallback, no key). B-6's acceptance was arm B reading 0 of 56, and it did — but arm
    /// A was hard-wired to <see cref="ConceptEmbeddingSource"/> and so could not report on whatever
    /// the run in front of it was actually retrieving with.
    /// </para>
    /// <para>
    /// ⚠ <b>What arm B does and does not verify, because the difference is the whole point.</b> A
    /// real embedding model returns a dense vector for ANY non-empty text, so "is it non-zero?" is
    /// very nearly a tautology on that path and would be satisfied by a garbage vector. The
    /// non-vacuous half is what precedes it: the phrase must be PRESENT in the committed asset and
    /// the asset's model / dimensions / template stamp must all validate, or the source answers
    /// <c>Unavailable</c> — which is operationally identical to a zero vector, and is counted here
    /// as dead. Measured before the fix: the asset carried <b>0 of 54</b> distinct phrases, so arm B
    /// read 56 of 56 dead. That is the state this arm can return to, and it is why the arm is worth
    /// having at all.
    /// </para>
    /// <para>
    /// <b>B-7 (2026-09-05) rewired arm A onto the RESOLVED path and added two more arms.</b> Arm A
    /// now embeds through <see cref="EmbeddingSpace"/> — the same source Demo 01, Demo 02 and
    /// <see cref="EvalRuntime"/> retrieve with — so the number describes THIS run rather than a
    /// space nobody chose. Two arms exist to keep that honest:
    /// </para>
    /// <para>
    /// <b>ARM C, the concept space, measured directly and always.</b> When the selector resolves to
    /// the concept space arm A and arm C are the same measurement; when it resolves to the assets
    /// arm A and arm B are. Either way one pair CO-MOVES, so the row says which pair and prints the
    /// third number regardless. Arm C is what <c>--concept-vectors</c> and every asset-load
    /// fallback land on, so a hole there is a live hazard even on a run that did not use it.
    /// </para>
    /// <para>
    /// <b>ARM D, the THING rather than the proxy.</b> An authored phrase is not what the arms ask
    /// with. A conjunction label is a JOIN of up to
    /// <see cref="InterestMapBuilder.MaximumLabelPhrases"/> phrases, a capability gap names a
    /// companion class and a leaf-category signal is a category name — and those are the exact
    /// strings <c>RetrievalQuery.Need</c> carries. Arm D embeds the queries the scored personas'
    /// interest maps actually produce (<see cref="DiscoveryInterestMapping.QueryTermsFor"/>). It
    /// exists because arms A–C can all read 0 while retrieval is dead: a cache holding every
    /// ATOMIC phrase holds none of the JOINS, so on the committed-asset path the proxy passes and
    /// the thing fails. Without arm D this row would have gone green on exactly the change that
    /// broke the dense leg — the flattering direction, which is the one to instrument hardest.
    /// </para>
    /// <para>
    /// ⚠ <b>B-21: on the real-vector path arms A and D are now NEAR-VACUOUS, in exactly the way arm
    /// B's zero test always was, and that has to be said rather than enjoyed.</b> Queries are
    /// embedded live, and a real embedding model returns a dense vector for ANY non-empty text — so
    /// "did it come back non-zero?" is close to a tautology there and would be satisfied by a
    /// garbage vector. What the two arms still verify on that path is that the path is REACHABLE at
    /// all: credentials present, the committed index validating, the live deployment answering, and
    /// <see cref="EmbeddingSpace"/>'s space-identity probe clearing its floor — any of which failing
    /// resolves the run to the concept space, where the same arms measure a real lexicon gap. The
    /// non-vacuous instrument for the CONCEPT space is arm C, which is why arm C is measured on
    /// every run whether or not the run used it.
    /// </para>
    /// <para>
    /// ⚠ <b>These arms SPEND on the real-vector path.</b> Arm A embeds 56 phrases and arm D 50
    /// queries, live, once each. That is roughly a hundredth of a cent, and it is stated here rather
    /// than discovered on an invoice.
    /// </para>
    /// <para>
    /// <b>The verdict is A AND B AND C AND D</b>, and arm C joined it at B-21. It had been left out
    /// while arm D read 38 of 50 on the real-vector path — a row that could not go green there did
    /// not visibly need it. Live query embedding takes A, B and D to zero on that path in a single
    /// change, and the row would then have printed ✅ under <c>--real-vectors</c> while arm C still
    /// reported 18 of 56 dead in the space the DEFAULT runs in. That is a green tick bought by
    /// passing a flag, on a run that repaired nothing. Every arm now has to be clean, which is
    /// strictly harder; nothing was relaxed to accommodate any of them.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckAuthoredQueryPhrasesRetrieve()
    {
        // The narrow vocabulary the extension authored: every latent-gold token across the scored
        // personas. A dead phrase here costs a persona its gold; a dead phrase elsewhere does not.
        var goldTokens = CoveragePersonas.All
            .SelectMany(p => InterestMapGold.Derive(p.Id).Latent)
            .ToHashSet(StringComparer.Ordinal);

        int total = InterestMapBuilder.ContextPhrases.Count;

        // ── ARM A — the RESOLVED path. Whatever EmbeddingSpace handed the retrievers this run is
        //    what gets measured, so the row can no longer report on a space nothing ran on.
        var space = EmbeddingSpace.Resolve(Catalogue.Default.All);
        var (deadA, deadGoldA) = MeasureAuthoredPhrases(text => EmbedThrough(space.Source, text), goldTokens);

        // ── ARM B — the committed assets, loaded HERE and independently of the selector, with no
        //    live fallback: the arm must measure the ASSET, not the credentials of whoever ran it.
        var (deadB, totalB, noteB) = MeasureRealVectorArm();

        // ── ARM C — the concept space, measured directly. It never co-moves with the selector,
        //    which is what makes it worth printing on a run that resolved to the assets.
        var (deadC, deadGoldC) = MeasureAuthoredPhrases(
            text => ConceptEmbeddingSource.Instance.Embed(text), goldTokens);

        // ── ARM D — the queries the arms actually issue, on the resolved path.
        var (issuedTotal, deadIssued, issuedExamples) = MeasureIssuedQueries(space.Source);

        // ── ARM D IS TWO DIFFERENT QUESTIONS, and until 2026-09-06 it printed one number for both.
        //    Plan item 1.10.
        //
        //    On the DEFAULT concept path it is a real measurement of a real hole: a query that
        //    embeds to zero in a 24-dimension hand-built space gives the dense leg nothing, and
        //    8 of 50 do.
        //
        //    On --real-vectors it is a REACHABILITY probe wearing a measurement's clothes. Since
        //    B-21 the query is embedded LIVE, and a live embedder returns a non-zero vector for any
        //    non-empty text — so `0 of 50 dead` is close to guaranteed by construction. It can only
        //    be non-zero if the path is UNREACHABLE: no credentials, a model-stamp mismatch, or a
        //    failed space-identity probe. A check that cannot fail must not print a number that
        //    reads as a result, and 0 of 50 is an extreme value: extreme values are wiring faults
        //    until proven otherwise, and here it is proven VACUOUS instead, which is worse, because
        //    it reads as a pass.
        //
        //    So the real path reports Reachable / Unreachable and NEVER a count out of 50. The
        //    concept path keeps the count. Arm C is untouched — it is the independent measurement
        //    that survives either way.
        bool realPath = space.Chosen == EmbeddingSpaceChoice.RealVectors;
        bool liveQueryPathReachable = deadIssued == 0;

        string armDReading = realPath
            ? "ARM D (REACHABILITY of the live query path — NOT a count): "
            + (liveQueryPathReachable
                ? "REACHABLE. Queries are embedded live against the deployment the committed index's own model "
                + "stamp names, and every issued query came back with a vector. ⚠ This is not a measurement of "
                + "retrieval quality: a live embedder returns a non-zero vector for ANY non-empty text, so this "
                + "check can only fail on unreachability — absent credentials, a model-stamp mismatch, or a failed "
                + "space-identity probe."
                : $"UNREACHABLE. {deadIssued} issued query/queries came back with no vector, and on this path that "
                + "is a wiring fault rather than a thin cache: absent credentials, a model-stamp mismatch, or a "
                + $"failed space-identity probe. Examples: {string.Join(" | ", issuedExamples)}.")
            : $"ARM D (the queries actually issued, on the resolved path): {deadIssued} of {issuedTotal} unanswerable"
            + (deadIssued == 0 ? "." : $" — e.g. {string.Join(" | ", issuedExamples)}.");

        bool armAClean = deadA == 0;
        bool armBClean = deadB == 0;
        bool armCClean = deadC == 0;
        bool armDClean = realPath ? liveQueryPathReachable : deadIssued == 0;

        // ⚠ ARM C IS IN THE VERDICT SINCE B-21, and it was NOT before. The old verdict was
        // A && B && D, and while arm D read 38 of 50 on the real-vector path that could never go
        // green there, so the omission never showed. Live query embedding takes arms A, B and D to
        // zero on that path in one change — and the row would then have printed ✅ on
        // --real-vectors while arm C still reported 18 of 56 authored phrases dead in the concept
        // space, which is the space the DEFAULT and every asset-load fallback run in. A green tick
        // bought by passing a flag, on a run that repaired nothing, is a flattering verdict, and
        // the flattering direction is the one to instrument hardest. This row never gates, so
        // tightening it costs nothing and removes the tick.
        bool allRetrievable = armAClean && armBClean && armCClean && armDClean;

        var coMoves = space.Chosen == EmbeddingSpaceChoice.RealVectors
            ? "ARM A and ARM B are the same source on this run, so their agreement is ONE fact, not two — read ARM C and ARM D."
            : "ARM A and ARM C are the same source on this run, so their agreement is ONE fact, not two — read ARM B and ARM D.";

        return new ControlRowSnapshot(
            "AuthoredQueryPhraseRetrievability",
            "the searching arms should be able to ASK for every interest the gold rewards, in the space this run "
          + "actually retrieves in. ARM A (the RESOLVED path — whatever EmbeddingSpace handed the retrievers): a "
          + "query that embeds to ZERO, or that the source answers UNAVAILABLE, gives the dense leg nothing, and "
          + "the arm's low score is then a property of the corpus rather than of the arm. ARM B (the committed "
          + "text-embedding-3-small INDEX, no key, no live path): every product document must be answerable "
          + "straight from the asset. A product the asset cannot answer for is a product the dense leg cannot "
          + "rank, which is what a catalogue grown without a rebuild looks like. ⚠ Its key is CO-DERIVED — the "
          + "loader and this arm both render the document with THIS build's template — so it can see an ABSENT "
          + "or unparseable vector and cannot see a WRONG one: measured against an asset with every vector "
          + "rotated by one product it still read 0 of 99. Since B-21 this arm's denominator "
          + "is the CATALOGUE, not the phrase list — the query-vector asset it used to count against is deleted. "
          + "ARM C (the concept space, measured directly): the space --concept-vectors forces "
          + "and every asset-load failure falls back to, reported whether or not this run used it. ARM D is the "
          + "THING the other three only proxy: the actual query strings the scored personas' interest maps produce "
          + "— joins of phrases, companion classes, category names — none of which is an authored phrase. ⚠ ARM D "
          + "ASKS TWO DIFFERENT QUESTIONS AND REPORTS THEM DIFFERENTLY (plan item 1.10): on the concept default it "
          + "is a COUNT of queries the space cannot answer, a real hole; on --real-vectors, where B-21 embeds the "
          + "query live and a live embedder returns a vector for any non-empty text, a count cannot fail, so it "
          + "reports REACHABLE / UNREACHABLE and no n-of-50 at all.",
            $"SPACE: {space.Source.Name} ({space.Source.ModelId}, {space.Source.Dimensions} dims) · {space.Reason}"
          + $" · ARM A (resolved path): {deadA} of {total} authored phrase(s) unanswerable"
          + (deadA == 0 ? "." : $"; {deadGoldA.Count} of them latent-GOLD: {string.Join(", ", deadGoldA)}.")
          + $" · ARM B (the committed index, no live path): {deadB} of {totalB} product(s) unanswerable — {noteB}"
          + $" · ARM C (concept space, always measured): {deadC} of {total} embed to ZERO"
          + (deadC == 0 ? "." : $"; {deadGoldC.Count} latent-GOLD: {string.Join(", ", deadGoldC)}.")
          + $" · {armDReading}"
          + $" · {coMoves}"
          + (allRetrievable
                ? " Every arm can ASK for every interest the gold rewards, and for every query it actually issues."
                : " READ EVERY EVAL 02 ARM NUMBER WITH THIS IN FRONT OF IT: on the interests listed above the dense "
                + "retrieval leg contributes nothing, so a low coverage cell there is not evidence that the arm "
                + "failed to reason. ADVISORY — closing ARM C means choosing a concept mapping per phrase, which "
                + "moves every coverage cell, so it is reported rather than silently repaired."
                + (realPath
                    ? " ⚠ ARM D on this path is a REACHABILITY verdict and contributes no count — read ARM C for "
                    + "the measurement. ✅ The weight ARM D lost here is carried by a SEPARATE row, not by another "
                    + "arm of this one: DenseLegSaysWhenItRankedNothing asks the answer-quality question — of the "
                    + "queries that DO embed, how many have every dense hit fall under the floor while the run "
                    + "still reports Degraded = false — and SilentDenseWipeoutDetectorCanFire GATES on that "
                    + "census being able to fire. It is a separate row because this one is already a FINDING in "
                    + "both spaces on ARM C alone, so an arm folded in here could never change what a reader sees."
                    : string.Empty)),
            allRetrievable,
            Gating: false);
    }

    /// <summary>
    /// Counts the authored context phrases an embedder cannot answer — a zero vector and an
    /// <c>Unavailable</c> alike, because the dense leg gets the same nothing from both.
    /// </summary>
    /// <param name="embed">The embedder under test.</param>
    /// <param name="goldTokens">Latent-gold suffixes, so a dead phrase that costs a persona its gold is named.</param>
    private static (int Dead, List<string> DeadGold) MeasureAuthoredPhrases(
        Func<string, ReadOnlyMemory<float>> embed,
        IReadOnlySet<string> goldTokens)
    {
        int dead = 0;
        var deadGold = new List<string>();

        foreach (var (suffix, phrase) in InterestMapBuilder.ContextPhrases.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!IsDead(embed(phrase))) continue;

            dead++;
            if (goldTokens.Contains(suffix)) deadGold.Add(suffix);
        }

        return (dead, deadGold);
    }

    /// <summary>
    /// The query strings the scored personas' interest maps actually produce, de-duplicated and
    /// ordinally ordered. The shared input of ARM D and of
    /// <see cref="CheckDenseLegSaysWhenItRankedNothingAsync"/>, so the ASK arm and the ANSWER arm
    /// are answering about the same population rather than about two populations that happen to
    /// look alike.
    /// </summary>
    /// <remarks>
    /// These come from <see cref="DiscoveryInterestMapping.QueryTermsFor"/> — the same method the
    /// loop's round-1 plan is built from, and whose first entry is the label Demo 01's offline
    /// baseline arm passes straight to <c>RetrievalQuery.For</c>. Deterministic: no model, no
    /// credentials, no network.
    /// </remarks>
    private static SortedSet<string> IssuedQueries()
    {
        var queries = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var persona in CoveragePersonas.All)
        {
            var profile = UserProfiles.Require(persona.Id);
            var map = InterestMapBuilder.Build(
                profile.User,
                profile.Purchases,
                Catalogue.Default.BySku,
                statedNeeds: null,
                asOf: Catalogue.DemoToday,
                sensitiveCategoryNames: Catalogue.Default.SensitiveCategories);

            foreach (var signal in map.Signals)
            {
                foreach (var term in DiscoveryInterestMapping.QueryTermsFor(signal))
                {
                    if (!string.IsNullOrWhiteSpace(term)) queries.Add(term);
                }
            }
        }

        return queries;
    }

    /// <summary>
    /// ARM D: how many of <see cref="IssuedQueries"/> the resolved space cannot answer at all.
    /// </summary>
    /// <param name="source">The resolved embedding source.</param>
    private static (int Total, int Dead, List<string> Examples) MeasureIssuedQueries(IEmbeddingSource source)
    {
        var queries = IssuedQueries();

        int dead = 0;
        var examples = new List<string>();

        foreach (var query in queries)
        {
            if (!IsDead(EmbedThrough(source, query))) continue;

            dead++;
            if (examples.Count < 3) examples.Add($"\"{Shorten(query, 46)}\"");
        }

        return (queries.Count, dead, examples);
    }






    // ══ Plan item 8.8 — a dead property, and what it was dead ABOUT ══════════════════════════

    /// <summary>
    /// GATING. A case whose fluent assertion THREW must be named on Eval 01's gate panel, and must
    /// not change the gate. Driven by capturing the real printer's own output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>8.8's defect was that <c>IntegrityRunReport.AssertionFailures</c> had exactly one
    /// reference: its own declaration.</b> That is the third state §8.1 refuses to leave standing —
    /// neither deleted nor read — and it is worse than either, because a later reader takes a
    /// declared aggregate as one somebody consumes.
    /// </para>
    /// <para>
    /// <b>What it was dead about matters more than that it was dead.</b> A fluent assertion that
    /// threw means a case was graded while one of its own checks did not complete. That is an
    /// INSTRUMENT fault, and until now it appeared only as per-row prose that a reader had to
    /// scan for. It is now one line on the gate panel — and it is deliberately **not** in
    /// <c>Passed</c>: promoting it to the gate would move verdicts on a paid path this change
    /// cannot test, so it is made loud rather than made decisive, and the panel says which.
    /// </para>
    /// <para>
    /// ⚠ It captures <c>Console.Out</c> and calls the REAL <c>EvalPrinter.PrintIntegrityGate</c>.
    /// A check that the property is "referenced somewhere" would be satisfied by this comment.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckAssertionFaultsAreNamedAndNotGated()
    {
        var problems = new List<string>();

        var clean = new IntegrityRunReport();
        var faulted = new IntegrityRunReport();

        var specimen = IntegrityCases.All.FirstOrDefault();
        if (specimen is null)
        {
            return new ControlRowSnapshot(
                "AssertionFaultsAreNamedAndNotGated",
                "a case whose fluent assertion THREW must be named on Eval 01's gate panel and must not change the gate.",
                "❌ VACUOUS: Eval 01 declares no cases, so there was no specimen to build a row from. NOT a pass.",
                Tripped: false);
        }

        var verdict = new IntegrityVerdict(specimen.Id, [], 0, 0, 0, [], null);
        clean.Add(new IntegrityRow(specimen, verdict, [], 0, null, null, null, AssertionFailure: null));
        faulted.Add(new IntegrityRow(specimen, verdict, [], 0, null, null, null,
                                     AssertionFailure: "the fluent assertion threw: control specimen"));

        if (clean.AssertionFailures.Count != 0)
            problems.Add($"a row with NO assertion failure is counted as one ({clean.AssertionFailures.Count}).");
        if (faulted.AssertionFailures.Count != 1)
            problems.Add($"a row WITH an assertion failure is counted {faulted.AssertionFailures.Count} time(s), expected 1.");
        if (clean.Passed != faulted.Passed)
            problems.Add("an assertion fault CHANGED the gate — it is an instrument fault and must be reported, not gated.");

        string printed = CaptureIntegrityGate(faulted);
        string printedClean = CaptureIntegrityGate(clean);

        if (!printed.Contains("ASSERTION FAULTS", StringComparison.Ordinal))
            problems.Add("the gate panel does not name assertion faults at all — the aggregate is dead again.");
        if (!printed.Contains(specimen.Id, StringComparison.Ordinal))
            problems.Add($"the gate panel reports a count without naming the case ({specimen.Id}); a count nobody can follow up is not a report.");
        if (printedClean.Contains("ASSERTION FAULTS", StringComparison.Ordinal))
            problems.Add("a run with NO assertion fault still prints the fault line — a line that always prints says nothing.");

        return new ControlRowSnapshot(
            "AssertionFaultsAreNamedAndNotGated",
            "IntegrityRunReport.AssertionFailures had exactly ONE reference — its own declaration. That is the third "
          + "state §8.1 refuses to leave standing, and it is worse than deleting or reading it, because a later "
          + "reader takes a declared aggregate as one somebody consumes. What it was dead ABOUT matters more: a "
          + "fluent assertion that THREW means a case was graded while one of its own checks did not complete — an "
          + "INSTRUMENT fault, previously visible only as per-row prose. It must now be NAMED on the gate panel, "
          + "with the case id, and must NOT move the gate: promoting it to Passed would move verdicts on a paid "
          + "path this change cannot test. Driven by capturing the REAL EvalPrinter.PrintIntegrityGate output.",
            $"specimen case {specimen.Id} · rows with an assertion fault counted: clean {clean.AssertionFailures.Count}, "
          + $"faulted {faulted.AssertionFailures.Count} · gate unchanged by the fault: "
          + $"{(clean.Passed == faulted.Passed ? $"yes (both {clean.Passed})" : "❌ NO")} · the panel names it: "
          + $"{(printed.Contains("ASSERTION FAULTS", StringComparison.Ordinal) ? "yes" : "❌ no")} · a clean run stays "
          + $"silent about it: {(printedClean.Contains("ASSERTION FAULTS", StringComparison.Ordinal) ? "❌ no" : "yes")}"
          + (problems.Count == 0 ? "." : " · ❌ " + string.Join(" ❌ ", problems)),
            problems.Count == 0);
    }

    /// <summary>Runs the real gate printer with stdout captured, and returns what it wrote.</summary>
    /// <param name="report">The report to print.</param>
    private static string CaptureIntegrityGate(IntegrityRunReport report)
    {
        var previous = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            EvalPrinter.PrintIntegrityGate(report);
        }
        finally
        {
            Console.SetOut(previous);
        }

        return buffer.ToString();
    }

    // ══ Plan item 8.22 — a persona present in one arm and absent from the other ══════════════

    /// <summary>
    /// GATING. A persona one arm scored and the other did not must appear in the pairing's
    /// NOT COMPARABLE list, not vanish from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> <c>SignTestAtEqualK</c> opened with a bare <c>continue</c> when either
    /// side was missing or unscorable, so such a persona entered neither <c>Excluded</c> nor any
    /// count. The pairing's n shrank and the shrink was **indistinguishable from there having been
    /// fewer personas** — the flattering direction, because a smaller n is a weaker test that still
    /// prints a p-value beside it.
    /// </para>
    /// <para>
    /// ⚠ <b>Newly load-bearing.</b> Eval 09 pairs on the DECLARED-k report, which can be missing
    /// cells the own-k report has: in the dry run its clause 1 said *"the 11 persona(s)"* where
    /// there were twelve. Eval 09 noted the twelfth separately, so it was declared somewhere and
    /// absent from the number — which is exactly the state a reader cannot reconcile.
    /// </para>
    /// <para>
    /// ⚠ <b>Both sides absent stays silent, deliberately</b>, and the row asserts that too: a
    /// persona that ran in NEITHER arm is a fact about the run, not about this pair, and listing it
    /// under every arm pair would bury the case that matters. A row that demanded it would push the
    /// suite toward noise and then toward ignoring the list.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckAPersonaInOneArmOnlyIsDeclared()
    {
        var problems = new List<string>();

        // Two personas both arms scored, one the CHALLENGER alone scored, one the REFERENCE alone
        // scored, and one NEITHER scored. Five recorded facts, three distinct expected outcomes.
        var pairing = new PairedCoverageReport();
        pairing.Record("BOTH-1",  "A", Cell(0.5, 1, 5));
        pairing.Record("BOTH-1",  "B", Cell(1.0, 2, 5));
        pairing.Record("BOTH-2",  "A", Cell(0.5, 1, 5));
        pairing.Record("BOTH-2",  "B", Cell(1.0, 2, 5));
        pairing.Record("B-ONLY",  "B", Cell(1.0, 2, 5));
        pairing.Record("A-ONLY",  "A", Cell(0.5, 1, 5));
        pairing.Record("NEITHER", "C", Cell(0.5, 1, 5));

        var outcome = pairing.SignTestAtEqualK("A", "B", CoverageMetric.Recall);

        if (outcome.Wins != 2 || outcome.Losses != 0 || outcome.Ties != 0)
        {
            problems.Add($"the two comparable personas counted W/L/T {outcome.Wins}/{outcome.Losses}/{outcome.Ties}, expected 2/0/0.");
        }

        bool declaresBOnly = outcome.Excluded.Any(x => x.StartsWith("B-ONLY", StringComparison.Ordinal));
        bool declaresAOnly = outcome.Excluded.Any(x => x.StartsWith("A-ONLY", StringComparison.Ordinal));
        bool mentionsNeither = outcome.Excluded.Any(x => x.StartsWith("NEITHER", StringComparison.Ordinal));

        if (!declaresBOnly) problems.Add("a persona the CHALLENGER scored and the reference did not is still dropped silently.");
        if (!declaresAOnly) problems.Add("a persona the REFERENCE scored and the challenger did not is still dropped silently.");
        if (mentionsNeither) problems.Add("a persona NEITHER arm scored is listed under this arm pair — that is noise, and a list nobody reads declares nothing.");
        if (outcome.Excluded.Count != 2)
            problems.Add($"the NOT COMPARABLE list holds {outcome.Excluded.Count} entries, expected exactly the 2 one-armed personas.");

        return new ControlRowSnapshot(
            "APersonaInOneArmOnlyIsDeclared",
            "a persona one arm scored and the other did not must be REPORTED as NOT COMPARABLE, never dropped. "
          + "SignTestAtEqualK used to open with a bare continue in that case, so such a persona entered neither "
          + "Excluded nor any count: the pairing's n shrank and the shrink was indistinguishable from there having "
          + "been fewer personas — the flattering direction, because a smaller n is a weaker test that still prints "
          + "a p-value. Newly load-bearing since Eval 09 pairs on the DECLARED-k report, which can be missing cells "
          + "the own-k report has (its dry run said 'the 11 persona(s)' where there were 12). ⚠ It asserts the "
          + "OTHER direction too: a persona NEITHER arm scored must stay OUT of this pair's list, because a list "
          + "that fills with noise is a list nobody reads.",
            $"5 cells over 5 personas — 2 in both arms, 1 in the challenger only, 1 in the reference only, 1 in "
          + $"neither · W/L/T {outcome.Wins}/{outcome.Losses}/{outcome.Ties} over the comparable pairs · "
          + $"NOT COMPARABLE ({outcome.Excluded.Count}): {(outcome.Excluded.Count == 0 ? "—" : string.Join("; ", outcome.Excluded))}"
          + (problems.Count == 0
                ? " · both one-armed personas are named with which arm held the cell, and the two-armed absence is correctly not listed."
                : " · ❌ " + string.Join(" ❌ ", problems)),
            problems.Count == 0);

        static CoverageScore Cell(double latent, int hits, int k) =>
            new(latent, double.NaN, hits, 2, 0, 0, k, 0, 0, DeclaredK: k, PresentedBeforeCut: k);
    }

    // ══ Plan item 8.13 — the committed asset's CONTENTS were verified on ONE path only ════════

    /// <summary>
    /// The four catalogue ids whose committed vectors are pinned, and the six pairwise cosines
    /// between them. <b>Derived once from the committed asset and checked in</b>; thereafter the
    /// asset cannot supply its own bar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>A pinned constant has to be shown to DISCRIMINATE, or it is decoration.</b> These are
    /// not "the numbers the asset happens to hold": the row below reloads the asset with every
    /// vector <b>rotated by one product</b> — the exact corruption §20.11 item 7 measured ARM B
    /// sailing through at 0 of 99 — and requires the pins to reject it. Four products rather than
    /// one, and six pairs rather than one, so a rotation cannot slip past on a single lucky pair.
    /// </para>
    /// <para>
    /// If the catalogue or the document template legitimately changes, the asset is rebuilt and
    /// these move with it. That is the intended maintenance cost: an asset whose contents can
    /// change with nothing to say so is what 8.13 is about.
    /// </para>
    /// </remarks>
    private static readonly (string A, string B, double Expected)[] CommittedVectorPins =
    [
        ("GLX-1001", "GLX-1002", 0.6438),
        ("GLX-1001", "GLX-3004", 0.2221),
        ("GLX-1001", "GLX-8002", 0.2219),
        ("GLX-1002", "GLX-3004", 0.2574),
        ("GLX-1002", "GLX-8002", 0.2691),
        ("GLX-3004", "GLX-8002", 0.2155),
    ];

    /// <summary>How far a pinned cosine may drift. A float32 dot over identical bytes is exact.</summary>
    private const double CommittedVectorPinTolerance = 5e-4;

    /// <summary>
    /// GATING, on EVERY path including the concept default. Pins the committed index's CONTENTS —
    /// the actual numbers — against checked-in cosines, and proves the pins reject an asset whose
    /// vectors have been rotated by one product.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gap this closes, as §20.11 item 7 and plan item 8.13 state it.</b>
    /// <c>EmbeddingSpace</c>'s space-identity probe was the <b>single</b> check that the committed
    /// vectors are the RIGHT NUMBERS rather than merely present, well-formed and completely keyed —
    /// and <b>it does not run on the concept default</b>, which is the space the shipped demo and
    /// every asset-load fallback run in. ARM B cannot substitute: its key is <b>co-derived</b> (the
    /// loader and the arm render the document with the same build's template and look it up in the
    /// same process), and measured against an asset with every vector rotated by one product it
    /// still read <b>0 of 99</b> while <c>cosine(committed[GLX-1001], rotated[GLX-1001]) = 0.6438</c>
    /// made the corruption plainly visible.
    /// </para>
    /// <para>
    /// ⚠ <b>This row needs no credentials and issues no live call</b>, which is the whole point:
    /// the gap was that the only content check required them.
    /// </para>
    /// <para>
    /// ⚠ <b>It carries its own positive control.</b> The pins are re-evaluated against a ROTATED
    /// copy of the same asset, in the same process, and must reject it. A pin that cannot fail is
    /// worth less than no pin, because it reads as a pass — 1.10's finding, one section up.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckCommittedVectorsAreTheRightNumbers()
    {
        const string name = "CommittedVectorsAreTheRightNumbers";
        const string expectation =
            "the committed text-embedding-3-small index must be checkable for its CONTENTS — the actual numbers — "
          + "on EVERY path, including the concept default that the shipped demo and every asset-load fallback run "
          + "in. Until now the only content check was EmbeddingSpace's space-identity probe, which needs "
          + "credentials and runs on --real-vectors alone; ARM B's key is CO-DERIVED and reads 0 of 99 over an "
          + "asset with every vector rotated by one product. So six pairwise cosines between four named products "
          + "are pinned, derived once from the asset and checked in. ⚠ The pins carry their own positive control: "
          + "they are re-evaluated against a ROTATED copy of the same asset in the same process and must REJECT "
          + "it. A pin that cannot fail is worth less than no pin, because it reads as a pass.";

        var products = Catalogue.Default.All;

        PrecomputedEmbeddingSource source;
        try
        {
            source = PrecomputedEmbeddingSource.TryLoad(products, liveFallback: null);
        }
        catch (Exception ex)
        {
            return new ControlRowSnapshot(name, expectation,
                $"the committed index could not be read at all ({ex.GetType().Name}), so its contents are "
              + "unverifiable rather than verified.", Tripped: false);
        }

        if (source.IsEmpty)
        {
            return new ControlRowSnapshot(name, expectation,
                "NO committed vectors loaded — "
              + (source.LoadWarnings.Count > 0 ? source.LoadWarnings[0] : "the asset is absent.")
              + " An absent asset is NOT a pass.", Tripped: false);
        }

        var byId = products.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (string id in CommittedVectorPins.SelectMany(p => new[] { p.A, p.B }).Distinct(StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(id, out var product)) { missing.Add(id); continue; }

            var vector = EmbedThrough(source, EmbeddingDocument.ForProduct(product));
            if (vector.IsUnavailable() || vector.Length == 0) { missing.Add(id); continue; }

            vectors[id] = EmbeddingVectors.Normalized(vector.Span);
        }

        // ── The pins, against the committed asset. ──────────────────────────────────────────
        var readings = new List<string>();
        int held = 0, checkedPairs = 0;

        foreach (var (a, b, expected) in CommittedVectorPins)
        {
            if (!vectors.TryGetValue(a, out var left) || !vectors.TryGetValue(b, out var right)) continue;

            checkedPairs++;
            double actual = EmbeddingVectors.DotOfUnitVectors(left, right);
            bool ok = Math.Abs(actual - expected) <= CommittedVectorPinTolerance;
            if (ok) held++;

            readings.Add($"{a}·{b} {actual.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}"
                       + (ok ? "" : $" ≠ pinned {expected.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}"));
        }

        // ── The positive control: the SAME pins against a ROTATED copy of the SAME asset. ───
        //    Rotation by one product is exactly the corruption §20.11 item 7 measured ARM B
        //    sailing through. If the pins accept it, they are decoration.
        var ordered = CommittedVectorPins.SelectMany(p => new[] { p.A, p.B })
                                         .Distinct(StringComparer.Ordinal)
                                         .Where(vectors.ContainsKey)
                                         .OrderBy(id => id, StringComparer.Ordinal)
                                         .ToArray();
        int rotatedRejected = 0, rotatedPairs = 0;

        if (ordered.Length >= 2)
        {
            var rotated = new Dictionary<string, float[]>(StringComparer.Ordinal);
            for (int i = 0; i < ordered.Length; i++)
            {
                rotated[ordered[i]] = vectors[ordered[(i + 1) % ordered.Length]];
            }

            foreach (var (a, b, expected) in CommittedVectorPins)
            {
                if (!rotated.TryGetValue(a, out var left) || !rotated.TryGetValue(b, out var right)) continue;

                rotatedPairs++;
                double actual = EmbeddingVectors.DotOfUnitVectors(left, right);
                if (Math.Abs(actual - expected) > CommittedVectorPinTolerance) rotatedRejected++;
            }
        }

        bool everyPinChecked  = checkedPairs == CommittedVectorPins.Length && missing.Count == 0;
        bool everyPinHeld     = everyPinChecked && held == checkedPairs;
        bool pinsDiscriminate = rotatedPairs > 0 && rotatedRejected > 0;
        bool ok2              = everyPinHeld && pinsDiscriminate;

        return new ControlRowSnapshot(
            name,
            expectation,
            $"{source.CachedVectorCount} committed '{source.ModelId}' vector(s) at {source.Dimensions} dims, "
          + $"template {EmbeddingDocument.TemplateVersion}, {source.FallbackCalls} live call(s) — this row makes none · "
          + $"pinned pairwise cosines HELD: {held} of {checkedPairs}"
          + (missing.Count == 0 ? "" : $" ⚠ {missing.Count} pinned product(s) absent or unanswerable: {string.Join(", ", missing)}")
          + $" · {string.Join(" | ", readings)}"
          + $" · POSITIVE CONTROL, the same pins over the same asset ROTATED by one product: "
          + $"{rotatedRejected} of {rotatedPairs} pair(s) rejected"
          + (pinsDiscriminate
                ? " — the pins discriminate, so their agreement above is evidence about the CONTENTS."
                : " — ❌ the pins ACCEPT a rotated asset, so they are decoration and their agreement means nothing.")
          + (ok2
                ? " The committed vectors are checkable without credentials, on the concept default too."
                : " ❌ 8.13 is open on this tree."),
            Tripped: ok2);
    }

    // ══ Plan item 8.15 — the product-side evidence line degenerated to a tautology ════════════

    /// <summary>
    /// GATING. The catalogue side of an evidence line must carry a catalogue FACT. Driven over
    /// every tag-style attribute the real catalogue holds, through the real renderer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The line was built as <c>$"{key}: {value}"</c>, and <c>Product.TryGetAttributeValue</c>
    /// returns the TAG ITSELF when the cited key is a whole tag — so a card rendered
    /// <c>Catalogue · compat:backpack-strap: compat:backpack-strap</c>. That line exists to carry
    /// the catalogue's own fact about the product; when key equals value it carries none, and it
    /// carries none in the most confident-looking form the renderer has, a colon-separated pair
    /// that reads like a measurement.
    /// </para>
    /// <para>
    /// ⚠ <b>The specimens are the catalogue's, not authored.</b> Every tag on every product is put
    /// through the real <see cref="RecommendationPrinter.FormatAttributeEvidence"/>, so the row
    /// cannot pass by testing a shape the corpus does not contain. It also asserts the OTHER
    /// direction — a genuine spec pair must still render <c>key: value</c> — because a "fix" that
    /// swallowed the informative case would satisfy the first clause completely.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckCatalogueEvidenceLineCarriesAFact()
    {
        int tautologies = 0, productsAffected = 0, tagsChecked = 0;
        var examples = new List<string>();

        foreach (var product in Catalogue.Default.All)
        {
            bool affected = false;

            foreach (var tag in product.Tags)
            {
                // The key a model would cite for a tag IS the tag; this is the lookup the
                // evidence filter performs before the printer ever sees it.
                if (!product.TryGetAttributeValue(tag, out var value)) continue;

                tagsChecked++;
                if (!string.Equals(tag, value, StringComparison.Ordinal)) continue;

                affected = true;
                string rendered = RecommendationPrinter.FormatAttributeEvidence(tag, value);

                if (rendered.Contains($"{tag}: {value}", StringComparison.Ordinal))
                {
                    tautologies++;
                    if (examples.Count < 3) examples.Add($"{product.Id} \"{Shorten(rendered, 44)}\"");
                }
            }

            if (affected) productsAffected++;
        }

        // The other direction: a real spec pair must keep the shape that carries its fact.
        var withSpec = Catalogue.Default.All.FirstOrDefault(p => p.Specs.Count > 0);
        var spec = withSpec?.Specs.First();
        string specRendered = spec is null
            ? ""
            : RecommendationPrinter.FormatAttributeEvidence(spec.Value.Key, spec.Value.Value);
        bool specKeepsItsPair = spec is not null
            && string.Equals(specRendered, $"{spec.Value.Key}: {spec.Value.Value}", StringComparison.Ordinal);

        bool measured = tagsChecked > 0 && spec is not null;
        bool ok = measured && tautologies == 0 && specKeepsItsPair;

        return new ControlRowSnapshot(
            "CatalogueEvidenceLineCarriesAFact",
            "the CATALOGUE side of an evidence line must carry a catalogue fact. It was built as \"{key}: {value}\", "
          + "and Product.TryGetAttributeValue returns the TAG ITSELF when the cited key is a whole tag, so a card "
          + "rendered \"compat:backpack-strap: compat:backpack-strap\" — no fact, in the most confident-looking "
          + "form the renderer has. Driven over EVERY tag-style attribute the real catalogue holds, through the "
          + "real RecommendationPrinter.FormatAttributeEvidence, never over an authored specimen. It asserts BOTH "
          + "directions: no rendered line repeats the key as its own value, AND a genuine spec pair still renders "
          + "\"key: value\" — a fix that swallowed the informative case would satisfy the first clause completely.",
            $"{tagsChecked} tag-style attribute(s) resolve across the catalogue · {productsAffected} product(s) hold "
          + $"at least one tag whose value IS its key — the shape that produced the tautology · rendered as a "
          + $"key-is-its-own-value pair: {tautologies}"
          + (tautologies == 0 ? "." : $" — e.g. {string.Join(" | ", examples)}.")
          + $" · a real spec pair still renders as a pair: {(specKeepsItsPair ? $"yes (\"{Shorten(specRendered, 40)}\")" : "❌ NO — the fix ate the informative case, which is worse than the defect")}"
          + (measured
                ? ok ? " The line carries a fact or says the catalogue holds none."
                     : " ❌ 8.15 is open on this tree."
                : " ❌ VACUOUS: the catalogue produced no tag-style attribute and/or no spec pair, so neither clause "
                + "was exercised. NOT reported as a pass."),
            Tripped: ok);
    }

    // ══ Plan item 7.1 / ADR-031 S1, second clause — a stored snapshot says what produced it ════

    /// <summary>
    /// GATING. Drives the store's real byte-producing seam and asserts that what lands on disk
    /// names the RESOLVED embedding space and the configured chat deployment, keeps a NaN a NaN,
    /// and carries no credential of any kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this row exists.</b> The suite has two resolved configurations that both claim to be
    /// the product, the deterministic loop is measurably not space-invariant across them (§20, §42),
    /// and §54.6 established that the canonical snapshot file holds <b>whichever space ran last</b>.
    /// Until 7.1's second clause a stored snapshot said <c>Label</c>, its payload and <c>RunAt</c>
    /// and nothing about what produced it, so two files could differ because the agent changed or
    /// because the space did, and the file could not tell you which.
    /// </para>
    /// <para>
    /// <b>It EXECUTES the seam.</b> <see cref="EvalResultStore.Render{T}"/> is the exact expression
    /// <c>Write</c> hands to <c>File.WriteAllText</c>, so this row reads the bytes the store
    /// actually stores. A source-text assertion that "Provenance" appears in <c>EvalResultStore.cs</c>
    /// would be satisfied by the comment that explains it — the shape §34.4 and §55.5 each recorded
    /// once.
    /// </para>
    /// <para>
    /// ⚠ <b>The NaN clause is not decoration.</b> An empty denominator is NaN throughout this suite,
    /// and the store only survives one because it serialises with
    /// <c>AllowNamedFloatingPointLiterals</c>. Attaching provenance means parsing the document and
    /// writing it out again, and a round trip that quietly turned NaN into 0 would convert every
    /// "we could not score this one" into a perfect or a failing score — in a stored file, silently,
    /// after the run that could have noticed had ended.
    /// </para>
    /// <para>
    /// ⚠ <b>The credential clause reports a COUNT, never a value.</b> It asks whether the rendered
    /// document contains the real endpoint's text or the real key's text, and prints only
    /// present/absent. Where no credentials are configured it declares itself INAPPLICABLE for that
    /// clause rather than passing — an absent secret cannot be found in anything.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckEverySnapshotSaysWhatProducedIt()
    {
        const string name = "EverySnapshotSaysWhatProducedIt";
        const string expectation =
            "a stored snapshot must say what produced it, because this suite resolves TWO configurations that both "
          + "claim to be the product and the canonical file holds whichever ran last (§54.6). Driven through "
          + "EvalResultStore.Render — the exact expression Write hands to File.WriteAllText, never a source-text "
          + "check — over a real snapshot record carrying a NaN. The stored document must (1) carry a Provenance "
          + "member, (2) name the RESOLVED embedding space and its model, (3) name the configured chat deployment "
          + "and carry the standing caveat that CONFIGURED is not CALLED — Evals 03, 04 and 07 persist without "
          + "calling any model, (4) round-trip a NaN as a NaN, because an empty denominator is how this suite "
          + "refuses to render 'unscorable' as 0 or 1, and (5) contain no endpoint and no key. Clause 5 is "
          + "reported as present/absent and never prints either value.";

        var space = EmbeddingSpace.Resolve(Catalogue.Default.All);

        // A real snapshot record, with a real NaN in a real field — SoftClassCleanRate is the
        // suite's own "nothing was presented, so the rate is undefined" carrier.
        var specimen = new IntegritySnapshot
        {
            Architecture       = "(control specimen — EverySnapshotSaysWhatProducedIt)",
            Label              = "provenance seam probe",
            SoftClassCleanRate = double.NaN,
        };

        string rendered = EvalResultStore.Render(specimen);

        using var document = JsonDocument.Parse(rendered);
        bool hasBlock = document.RootElement.TryGetProperty(SnapshotProvenance.MemberName, out var block);

        string spaceInFile = hasBlock && block.TryGetProperty("EmbeddingSpace", out var s) ? s.GetString() ?? "" : "";
        string modelInFile = hasBlock && block.TryGetProperty("EmbeddingModel", out var m) ? m.GetString() ?? "" : "";
        string chatInFile  = hasBlock && block.TryGetProperty("ChatDeploymentConfigured", out var c) ? c.GetString() ?? "" : "";
        string noteInFile  = hasBlock && block.TryGetProperty("Note", out var n) ? n.GetString() ?? "" : "";

        bool namesSpace = string.Equals(spaceInFile, space.Source.Name, StringComparison.Ordinal)
                       && string.Equals(modelInFile, space.Source.ModelId, StringComparison.Ordinal);
        bool namesChat  = string.Equals(chatInFile, Config.Model, StringComparison.Ordinal);
        bool carriesNote = string.Equals(noteInFile, SnapshotProvenance.StandingNote, StringComparison.Ordinal);

        // The payload has to survive the parse-and-rewrite the attachment costs.
        var replayed = JsonSerializer.Deserialize<IntegritySnapshot>(rendered, EvalResultStore.StorageJsonOptions);
        bool nanSurvived = replayed is not null && double.IsNaN(replayed.SoftClassCleanRate);

        // Clause 5. Only ever a boolean leaves this block.
        bool credentialsConfigured = Config.IsConfigured;
        bool leaks = credentialsConfigured
                  && (rendered.Contains(Config.Endpoint.ToString(), StringComparison.OrdinalIgnoreCase)
                   || rendered.Contains(Config.Endpoint.Host, StringComparison.OrdinalIgnoreCase)
                   || rendered.Contains(Config.KeyCredential.Key, StringComparison.Ordinal));

        bool ok = hasBlock && namesSpace && namesChat && carriesNote && nanSurvived && !leaks;

        return new ControlRowSnapshot(
            name,
            expectation,
            $"the store's own bytes, for a real IntegritySnapshot with SoftClassCleanRate = NaN · "
          + $"Provenance member: {(hasBlock ? "present" : "❌ ABSENT — a stored snapshot would not say what produced it")} · "
          + $"names the resolved space: {(namesSpace ? $"yes ({spaceInFile} / {modelInFile})" : $"❌ no (file says '{spaceInFile}' / '{modelInFile}', this run resolved '{space.Source.Name}' / '{space.Source.ModelId}')")} · "
          + $"names the configured chat deployment: {(namesChat ? $"yes ({chatInFile})" : $"❌ no (file says '{chatInFile}')")} · "
          + $"carries the CONFIGURED-is-not-CALLED caveat: {(carriesNote ? "yes" : "❌ no — the field would read as proof a model produced the numbers")} · "
          + $"NaN round-trips as NaN: {(nanSurvived ? "yes" : "❌ NO — an undefined rate would be stored as a number")} · "
          + $"credentials in the document: {(credentialsConfigured ? leaks ? "❌ PRESENT" : "absent" : "clause INAPPLICABLE — no endpoint or key is configured in this process, and an absent secret cannot be found in anything. DECLARED, not counted as a pass: this verdict is over the four applicable clauses")}"
          + (ok
                ? ". Two runs already coexist (Write archives the previous file under its own mtime, hundreds of dated archives on disk), so 7.1's acceptance now reads on both clauses."
                : ". ❌ 7.1's second clause is not met on this tree."),
            Tripped: ok);
    }

    // ══ Plan item 1.10, THIRD CLAUSE — the ANSWER-quality arm that replaces the weight ARM D
    //    lost on the real path. Two rows, on purpose, because they are two different KINDS of
    //    fact and §55.4 is the record of what happens when one row's NAME is taken to cover a
    //    lane its BODY never touches.
    //
    //      · CheckSilentDenseWipeoutDetectorCanFireAsync  — GATING. A wiring fact: the census
    //        below is capable of reporting a non-zero number, and capable of reporting zero.
    //      · CheckDenseLegSaysWhenItRankedNothingAsync    — ADVISORY. A corpus/threshold fact:
    //        how many issued queries the shipped floor silently empties.
    //
    //    ⚠ The census is NOT folded into AuthoredQueryPhraseRetrievability. That row is already
    //    ⚠️ FINDING in BOTH spaces because ARM C reads 18 of 56 and ARM C is space-invariant by
    //    design — so an arm added to it could never change anything a reader sees on the panel.
    //    A measurement with no discriminating power on the panel is not a replacement for a lost
    //    one; it is the lost one with extra words.

    /// <summary>
    /// The floor the wipeout probes push the dense leg past. Above the maximum possible cosine,
    /// so every hit the index returns must fall under it — the probe is arithmetic, not a guess.
    /// </summary>
    private const float ImpossibleDenseFloor = 1.01f;

    /// <summary>
    /// The floor the negative probe drops the dense leg to. Below the minimum possible cosine,
    /// so no hit can fall under it.
    /// </summary>
    private const float UnreachableDenseFloor = -1.01f;

    /// <summary>
    /// The non-semantic gates the discovery loop puts on an issued query, and nothing else.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>CatalogueDiscoverySearch.ExecuteAsync</c>'s query on everything that is not a
    /// PRE-FILTER: market, the stock decision (stock is a render-time fact), and the loop's own
    /// per-query top-k. ⚠ It deliberately carries <b>no</b> <c>CategoryPathPrefix</c>, <b>no</b>
    /// attribute <c>HardFilter</c> and <b>no</b> exclusions, so what it isolates is the SCORE
    /// FLOOR. That is a statement about the floor, not a simulation of a round: round 1 gates the
    /// first term of each interest by a category hint and by attribute hints, both of which shrink
    /// the eligible pool, and a smaller pool can push a query either INTO the wipeout class
    /// (fewer products, all under the floor) or OUT of it (nothing eligible at all, which is a
    /// different failure and is counted separately). So this count is neither an upper nor a lower
    /// bound on what the loop meets — it is the floor's own effect on the loop's own query
    /// strings, and the pre-filters are named as not modelled rather than assumed away.
    /// </remarks>
    /// <param name="need">The issued query string.</param>
    private static RetrievalQuery IssuedQuery(string need) => new()
    {
        Need        = need,
        Market      = RetrievalQuery.DefaultMarket,
        InStockOnly = false,
        TopK        = DiscoveryQueryPlanner.TopKPerQuery,
    };

    /// <summary>
    /// GATING. Proves the silent-wipeout census can produce a non-zero reading AND a zero one,
    /// by driving the real <see cref="HybridRetriever"/> at two floors that make the answer
    /// arithmetic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A scan that finds nothing proves nothing until it has been shown it can hit</b> — the
    /// rule §54.8's planted credential wrote down, applied to a detector rather than to a grep.
    /// <see cref="CheckDenseLegSaysWhenItRankedNothingAsync"/> reads three fields of
    /// <see cref="RetrievalDiagnostics"/> and reports a count; a count of zero from a predicate
    /// that can never be true reads exactly like a clean run, which is the failure mode ARM D was
    /// re-scoped for in the first place (plan item 1.10). So the detector is exercised in BOTH
    /// directions on a REAL issued query, against the REAL retriever, on the resolved path:
    /// </para>
    /// <list type="number">
    ///   <item>at <see cref="ImpossibleDenseFloor"/> the same query must be reported as a
    ///         silent wipeout — the dense leg ran, it had hits, and every one was discarded;</item>
    ///   <item>at <see cref="UnreachableDenseFloor"/> it must not be — nothing can fall under a
    ///         floor below the minimum cosine, so a detector that still reports a wipeout is
    ///         reading something other than the floor.</item>
    /// </list>
    /// <para>
    /// ⚠ <b>The specimen is not authored.</b> It is the first query of
    /// <see cref="IssuedQueries"/>, in ordinal order, that the resolved space can embed at all —
    /// a real string the loop really issues. A hand-made query would let this row supply its own
    /// input, and the gate self-examination rule forbids exactly that. If NO issued query embeds,
    /// the probe has no specimen and this row FAILS rather than passing vacuously.
    /// </para>
    /// </remarks>
    /// <param name="ct">Cancellation.</param>
    private static async Task<ControlRowSnapshot> CheckSilentDenseWipeoutDetectorCanFireAsync(CancellationToken ct)
    {
        const string name = "SilentDenseWipeoutDetectorCanFire";
        const string expectation =
            "the silent-dense-wipeout census must be shown able to FIRE and able to stay SILENT before its "
          + "count means anything. A count of 0 from a predicate that cannot be true reads exactly like a "
          + "clean run — the same shape ARM D's 0 of 50 had on the real path (plan item 1.10). Driven on the "
          + "REAL HybridRetriever over a REAL issued query, at two floors that make the answer arithmetic: "
          + "ABOVE the maximum cosine every dense hit must be discarded and the query must be reported as a "
          + "wipeout; BELOW the minimum cosine no hit can be discarded and it must not be. The specimen is the "
          + "first issued query the resolved space can embed, never an authored string — a row may not supply "
          + "its own input.";

        var space   = EmbeddingSpace.Resolve(Catalogue.Default.All);
        var probe   = IssuedQueries().FirstOrDefault(q => !IsDead(EmbedThrough(space.Source, q)));

        if (probe is null)
        {
            return new ControlRowSnapshot(
                name,
                expectation,
                "NO SPECIMEN: not one of the queries the scored personas' interest maps issue embeds in "
              + $"'{space.Source.Name}', so the wipeout detector has nothing to be proven on. That is itself a "
              + "wiring fault — ARM D would be reporting every query dead — and it is NOT reported as a pass.",
                Tripped: false);
        }

        var fired  = await ProbeWipeoutAsync(space.Source, probe, ImpossibleDenseFloor,  ct).ConfigureAwait(false);
        var silent = await ProbeWipeoutAsync(space.Source, probe, UnreachableDenseFloor, ct).ConfigureAwait(false);

        bool ok = fired.Wipeout && !silent.Wipeout;

        return new ControlRowSnapshot(
            name,
            expectation,
            $"SPACE: {space.Source.Name} ({space.Source.ModelId}, {space.Source.Dimensions} dims) · specimen "
          + $"\"{Shorten(probe, 46)}\" (the first issued query this space can embed, ordinal order) · "
          + $"floor {ImpossibleDenseFloor.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)} → "
          + $"{Describe(fired)} — the detector {(fired.Wipeout ? "FIRED" : "DID NOT FIRE, so a 0 from the census means nothing")} · "
          + $"floor {UnreachableDenseFloor.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)} → "
          + $"{Describe(silent)} — the detector {(silent.Wipeout ? "STILL FIRED, so it is not reading the floor" : "stayed silent")}"
          + (ok
                ? ". Both directions verified, so the census's number is a measurement."
                : ". ❌ The census below cannot be read as a measurement until this row is green."),
            Tripped: ok);

        static string Describe((bool Wipeout, RetrievalDiagnostics D) probe) =>
            $"degraded={probe.D.Degraded.ToString().ToLowerInvariant()}, kept={probe.D.DenseCandidates}, "
          + $"cut={probe.D.DenseBelowFloor}";
    }

    /// <summary>
    /// Runs one issued query through a real <see cref="HybridRetriever"/> built on the resolved
    /// source at an explicit floor, and says whether the dense leg silently ranked nothing.
    /// </summary>
    /// <remarks>
    /// A silent wipeout is <c>Degraded == false</c> (the leg ran), <c>DenseCandidates == 0</c>
    /// (it contributed no ranking) and <c>DenseBelowFloor &gt; 0</c> (it HAD hits and the floor
    /// took all of them). The third clause is what separates it from an empty eligible pool,
    /// which is a different fact and is counted separately.
    /// </remarks>
    /// <param name="source">The resolved embedding source.</param>
    /// <param name="need">The issued query.</param>
    /// <param name="floor">The dense score floor to build the retriever at.</param>
    /// <param name="ct">Cancellation.</param>
    private static async Task<(bool Wipeout, RetrievalDiagnostics D)> ProbeWipeoutAsync(
        IEmbeddingSource source,
        string need,
        float floor,
        CancellationToken ct)
    {
        var retriever = await HybridRetriever
            .BuildAsync(
                Catalogue.Default.All,
                source,
                new HybridRetrieverOptions { DenseScoreFloor = floor },
                cancellationToken: ct)
            .ConfigureAwait(false);

        var d = (await retriever.SearchAsync(IssuedQuery(need), ct).ConfigureAwait(false)).Retrieval;
        return (IsSilentWipeout(d), d);
    }

    /// <summary>
    /// The one predicate both new rows read, so the gating probe and the advisory census can
    /// never drift apart on what "the dense leg silently ranked nothing" means.
    /// </summary>
    /// <param name="d">The retriever's own diagnostics block.</param>
    private static bool IsSilentWipeout(RetrievalDiagnostics d)
        => !d.Degraded && d.DenseCandidates == 0 && d.DenseBelowFloor > 0;

    /// <summary>
    /// ADVISORY. Plan item 1.10's third clause: of the issued queries the resolved space CAN
    /// embed, how many have every dense hit discarded by the score floor while the run still
    /// reports <c>Degraded = false</c>?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is the replacement for ARM D's lost weight, and not another ask-ability
    /// check.</b> ARM D asks whether the system can ASK. On <c>--real-vectors</c> that question
    /// cannot fail — a live embedder returns a vector for any non-empty text — so ARM D now
    /// reports REACHABLE / UNREACHABLE there and contributes no count. This row asks the next
    /// question, which can fail in BOTH spaces: having asked, did the dense leg ANSWER, and if it
    /// did not, did anything say so? <c>Degraded</c> means <i>the leg had nothing to run on</i>,
    /// never <i>the leg returned nothing</i>, so a query that embeds fine and has every hit cut by
    /// the floor is reported as a healthy dense retrieval that simply found the customer nothing.
    /// The answer is lexical-only in fact and nothing on the diagnostics block says it.
    /// </para>
    /// <para>
    /// <b>Two failures are counted separately, because they have different remedies.</b> A FLOOR
    /// wipeout (hits existed, the floor took all of them) is a threshold fact and is what this
    /// row's verdict is about. An EMPTY POOL (the pre-filters or the market gate left nothing for
    /// the leg to rank) is a corpus/filter fact; it is printed, it is not in the verdict, and
    /// pooling the two would hide which one a reader has — the aggregate-decomposition rule.
    /// </para>
    /// <para>
    /// ⚠ <b>This row is ADVISORY.</b> Its number is a property of a CALIBRATED THRESHOLD
    /// (<c>CalibratedThresholds</c>, derived per space) meeting an authored corpus. Gating it
    /// would make the honest response to a red panel "move the floor until the count is zero",
    /// which is fitting a threshold to the output it judges — the thing this sample's whole
    /// argument refuses. It is reported loudly instead, exactly as ARM C is.
    /// </para>
    /// <para>
    /// ⚠ <b>Its greenness is conditional on
    /// <see cref="CheckSilentDenseWipeoutDetectorCanFireAsync"/>, which GATES.</b> A zero here
    /// means "no issued query is silently emptied" only while that row is green; on its own it is
    /// indistinguishable from a predicate that cannot be true.
    /// </para>
    /// <para>
    /// <b>Spend:</b> none beyond ARM D's. The queries are the same strings ARM D embeds and
    /// <c>PrecomputedEmbeddingSource</c> memoises the live path per instance on the exact text, so
    /// on <c>--real-vectors</c> every embedding this row needs is already in the memo. The two
    /// probe retrievers rebuild the product index, and every product document is answered from the
    /// committed asset — <b>0 live calls</b>.
    /// </para>
    /// </remarks>
    /// <param name="retriever">The retriever this run bound — the SHIPPED operating point, floor included.</param>
    /// <param name="ct">Cancellation.</param>
    private static async Task<ControlRowSnapshot> CheckDenseLegSaysWhenItRankedNothingAsync(
        IProductRetriever retriever,
        CancellationToken ct)
    {
        var space   = EmbeddingSpace.Resolve(Catalogue.Default.All);
        var queries = IssuedQueries();

        int embedded = 0, dead = 0, floorWipeouts = 0, emptyPools = 0;
        double floor = double.NaN;
        var wipeoutExamples = new List<string>();
        var emptyExamples   = new List<string>();

        foreach (var query in queries)
        {
            if (IsDead(EmbedThrough(space.Source, query))) { dead++; continue; }

            embedded++;

            var d = (await retriever.SearchAsync(IssuedQuery(query), ct).ConfigureAwait(false)).Retrieval;
            floor = d.DenseScoreFloor;

            // ⚠ The verdict goes through IsSilentWipeout — the SAME predicate the gating probe
            // proves. Re-typing the three clauses here would let the census and its own proof
            // drift apart, and then the gating row would be certifying a predicate nothing reads.
            // That drift was live for one ablation cycle and is what ablation A found.
            if (IsSilentWipeout(d))
            {
                floorWipeouts++;
                if (wipeoutExamples.Count < 3)
                {
                    wipeoutExamples.Add($"\"{Shorten(query, 38)}\" ({d.DenseBelowFloor} cut)");
                }

                continue;
            }

            // The other way a dense leg contributes nothing while reporting itself healthy: the
            // gates left it an empty pool, so there was never anything to discard. Different
            // fact, different remedy, counted apart.
            if (!d.Degraded && d.DenseCandidates == 0)
            {
                emptyPools++;
                if (emptyExamples.Count < 2) emptyExamples.Add($"\"{Shorten(query, 38)}\"");
            }
        }

        // Non-vacuity is asserted on this row's OWN denominator, not inferred from the result.
        // An empty embedded set gives the census a floor of exactly zero wipeouts, which is the
        // element-missing shape: nothing to count, reported as nothing wrong.
        bool measured = embedded > 0;
        bool clean    = measured && floorWipeouts == 0;

        return new ControlRowSnapshot(
            "DenseLegSaysWhenItRankedNothing",
            "of the queries the scored personas' interest maps ACTUALLY issue and this space CAN embed, none "
          + "should have every dense hit discarded by the score floor while the run still reports Degraded = "
          + "false. RetrievalDiagnostics.Degraded means the dense leg had nothing to RUN ON — an absent source, "
          + "an unembeddable query, a zero vector — and never that the leg returned nothing. So a query that "
          + "embeds perfectly and has all of its hits cut by the floor produces a lexical-only answer that every "
          + "diagnostic in the run describes as a healthy hybrid one. ⚠ This is plan item 1.10's THIRD CLAUSE: "
          + "the ANSWER-quality question that replaces the weight ARM D lost on --real-vectors, where an "
          + "ask-ability count cannot fail. ⚠ It is ADVISORY because its number is a property of a CALIBRATED "
          + "THRESHOLD meeting an authored corpus, and gating it would make 'move the floor until the count is "
          + "zero' the cheapest remedy — fitting a threshold to the output it judges. ⚠ Its zero is only a "
          + "measurement while SilentDenseWipeoutDetectorCanFire (which GATES) is green. ⚠ It carries no "
          + "category prefix and no attribute filter, so it isolates the FLOOR and is not a simulation of a "
          + "discovery round; the pre-filters shrink the pool and can move a query either way, so this is "
          + "neither an upper nor a lower bound on what the loop meets.",
            $"SPACE: {space.Source.Name} ({space.Source.ModelId}, {space.Source.Dimensions} dims) · dense score "
          + $"floor in force: {(double.IsNaN(floor) ? "NONE — no query reached the dense leg" : floor.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))}"
          + $" · {embedded} of {queries.Count} issued query/queries embed here"
          + (dead == 0 ? " (none dead)" : $" ({dead} dead — ARM D counts those)")
          + $" · SILENTLY EMPTIED BY THE FLOOR: {floorWipeouts} of {embedded}"
          + (floorWipeouts == 0
                ? "."
                : $" — {string.Join(" | ", wipeoutExamples)}. Every one of these ran the dense leg, had hits, "
                + "lost all of them to the floor, and is reported Degraded = false.")
          + $" · counted separately, NOT in the verdict: {emptyPools} query/queries reached the dense leg with an "
          + "EMPTY eligible pool (nothing to rank rather than nothing kept)"
          + (emptyPools == 0 ? "." : $" — {string.Join(" | ", emptyExamples)}.")
          + (measured
                ? clean
                    ? " No issued query is silently emptied at this space's calibrated floor."
                    : " READ THIS BEFORE ANY DENSE-RETRIEVAL CLAIM ABOUT THIS SPACE: for the queries named "
                    + "above the dense leg contributes NOTHING and no diagnostic says so, so their answers are "
                    + "lexical-only in fact while every field describes a healthy hybrid retrieval. ADVISORY — "
                    + "the remedy is a re-derived floor on a named held-out slice (plan item 2.7), never a "
                    + "number chosen until this count reads zero."
                : " ❌ VACUOUS: not one issued query embeds in this space, so this census counted over an empty "
                + "set and its zero is arithmetic rather than a finding. NOT reported as a pass."),
            clean,
            Gating: false);
    }

    /// <summary>
    /// Arm B of <see cref="CheckAuthoredQueryPhrasesRetrieve"/>: how many PRODUCTS the committed
    /// index cannot answer for. Counts an <c>Unavailable</c> (absent from the asset, or the whole
    /// asset rejected on its stamp) exactly like a zero vector, because the dense leg gets the same
    /// nothing from both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>B-21 changed what this arm measures, and the change must be read before the number
    /// is.</b> It used to count how many of the 56 authored PHRASES were present in a committed
    /// query-vector asset. That asset is deleted — it held 71 pre-guessed query texts, a
    /// run-time-composed query was never one of them, and the whole real-vector path retrieved
    /// nothing as a result. Queries are embedded live now, so "is this phrase in the file?" is not
    /// a question that has an answer any more, and an arm that kept asking it would report 56 of 56
    /// dead forever on a path that works.
    /// </para>
    /// <para>
    /// So the arm now measures the surviving, and load-bearing, half: the INDEX. Every product's
    /// embedding document must be answerable straight from the committed asset, with no live path
    /// attached. Its denominator is the CATALOGUE, not the phrase list.
    /// </para>
    /// <para>
    /// ⚠ <b>What this arm CANNOT see, stated because the first version of this remark claimed the
    /// opposite.</b> It said the arm checks "the re-rendered document hashing to a key the file
    /// actually carries", and called that "exactly the check that fails when the document template
    /// is bumped without a rebuild". Neither is true, and the reason is the gate-self-examination
    /// rule this project keeps: <see cref="PrecomputedEmbeddingSource"/>'s loader keys each stored
    /// vector by <c>HashQuery(EmbeddingDocument.ForProduct(product))</c> rendered with THIS build's
    /// template, and this arm then looks the vector up with the same expression on the same product
    /// in the same process. The key is CO-DERIVED, so the lookup cannot fail on the vector's
    /// content and cannot fail on a template change either. What actually catches a template bump
    /// is the <c>documentTemplateVersion</c> STRING comparison at load — a declared version, not a
    /// render — so a change to <see cref="EmbeddingDocument.ForProduct"/> that forgets to bump
    /// <see cref="EmbeddingDocument.TemplateVersion"/> leaves this arm reading 0 of 99 over vectors
    /// that describe text no longer produced anywhere.
    /// </para>
    /// <para>
    /// <b>MEASURED 2026-09-05, rather than reasoned about.</b> The committed asset was reloaded with
    /// every vector ROTATED by one product — all 99 keys still present, every vector describing a
    /// different product, the stamp untouched. This arm read <b>0 of 99 unanswerable</b>. The
    /// corruption is plainly visible: the cosine between <c>GLX-1001</c>'s committed vector and the
    /// rotated file's vector for <c>GLX-1001</c> is <b>0.6438</b>. It is visible to
    /// <see cref="EmbeddingSpace"/>'s space-identity probe, which re-embeds a product document LIVE
    /// and would fail its 0.98 floor — and that probe runs only on the real-vector path, so on the
    /// concept default nothing checks the asset's contents at all.
    /// </para>
    /// <para>
    /// <b>So what the arm genuinely reports</b> is narrower and still worth having: the asset exists
    /// and parses; its model, dimensions, keying and template stamps validate; every vector decodes
    /// to the right length; and every catalogue product id is PRESENT in it — which is what fails
    /// when the catalogue grows without a rebuild, and what read 99 of 99 dead before B-6 committed
    /// an asset at all. It says nothing about whether the numbers in it are the right numbers.
    /// </para>
    /// <para>
    /// It deliberately still attaches no live source: this arm reports on the ASSET, so it must
    /// read the same on a machine with credentials and a machine without. Whether a QUERY can be
    /// embedded on the real-vector path is arms A and D's business, and only on a run that resolved
    /// to it.
    /// </para>
    /// </remarks>
    private static (int Dead, int Total, string Note) MeasureRealVectorArm()
    {
        var products = Catalogue.Default.All;

        PrecomputedEmbeddingSource source;
        try
        {
            // TryLoad, not Load: a stale or missing asset must be REPORTED by this row, not thrown
            // out of Eval 03 as a crash.
            source = PrecomputedEmbeddingSource.TryLoad(products, liveFallback: null);
        }
        catch (Exception ex)
        {
            return (products.Count, products.Count, $"the index could not be read at all ({ex.GetType().Name}).");
        }

        if (source.IsEmpty)
        {
            return (products.Count, products.Count,
                    "NO committed vectors loaded — " +
                    (source.LoadWarnings.Count > 0 ? source.LoadWarnings[0] : "the asset is absent."));
        }

        int dead = 0;
        foreach (var product in products)
        {
            if (product is null) continue;
            if (IsDead(EmbedThrough(source, EmbeddingDocument.ForProduct(product)))) dead++;
        }

        return (dead, products.Count,
                $"{source.CachedVectorCount} committed '{source.ModelId}' product vectors at {source.Dimensions} dims, "
              + $"template {EmbeddingDocument.TemplateVersion}, {source.FallbackCalls} live call(s) made"
              + (source.LoadWarnings.Count == 0 ? "." : $"; {source.LoadWarnings.Count} load warning(s)."));
    }

    /// <summary>
    /// Embeds through an <see cref="IEmbeddingSource"/> synchronously, blocking if the source is a
    /// live one.
    /// </summary>
    /// <remarks>
    /// Before B-21 the comment here said this was safe only because every source this row touches is
    /// offline. That stopped being true when the real-vector path gained a live query embedder, so
    /// the honest description is the one above: this blocks. It is a console eval with no
    /// synchronisation context, the calls are sequential, and the alternative — threading async
    /// through the whole control-row signature for one row — buys nothing here.
    /// </remarks>
    /// <param name="source">The source.</param>
    /// <param name="text">The text.</param>
    private static ReadOnlyMemory<float> EmbedThrough(IEmbeddingSource source, string text)
    {
        var pending = source.EmbedAsync(text);
        return pending.IsCompleted ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// True when a vector gives the dense leg nothing: UNAVAILABLE (the source cannot answer this
    /// text at all) or all-zero (it recognised nothing in it). The two states are different and
    /// are described differently elsewhere; for the purpose of "can this arm ask?" they are the
    /// same nothing.
    /// </summary>
    /// <param name="vector">The vector.</param>
    private static bool IsDead(ReadOnlyMemory<float> vector)
    {
        if (vector.IsUnavailable()) return true;

        foreach (float component in vector.Span)
        {
            if (Math.Abs(component) > 1e-6f) return false;
        }
        return true;
    }

    /// <summary>Trims a query for a console line without hiding that it was trimmed.</summary>
    /// <param name="text">The text.</param>
    /// <param name="budget">Maximum characters.</param>
    private static string Shorten(string text, int budget)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= budget ? single : single[..Math.Max(0, budget - 1)] + "…";
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
        // ⚠ AND THERE MUST BE NO WAY BACK. This used to assert that the k-BLIND overload still
        // counted every pair, because Eval 09 read it. Eval 09 now pairs at equal k and the
        // overload is deleted, so the assertion that replaces it is the stronger one: the type
        // must expose NO pairing method that ignores k. A method whose only property is that it
        // cannot refuse an incomparable pair should not be reachable, and re-adding one — under
        // any name that pairs without a CoverageMetric and a k — turns this row red.
        var pairingMethods = typeof(PairedCoverageReport)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(SignTestOutcome))
            .ToList();
        var kBlind = pairingMethods
            .Where(m => !m.GetParameters().Any(pp => pp.ParameterType == typeof(CoverageMetric)))
            .Select(m => m.Name)
            .ToList();
        if (kBlind.Count > 0)
            problems.Add($"PairedCoverageReport still exposes {kBlind.Count} k-BLIND pairing method(s): {string.Join(", ", kBlind)}. A pairing that cannot refuse an unequal-k pair measures list length.");
        if (pairingMethods.Count == 0)
            problems.Add("PairedCoverageReport exposes NO pairing method at all — the equal-k test has been removed rather than the k-blind one.");

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

    /// <summary>One persona's score beside the floor that persona's own gold produces.</summary>
    /// <remarks>
    /// ⚠ <b>Added for plan item 1.8 (N-8).</b> The helper used to return only the MEAN of the scores
    /// and the MEAN of the floors, and a mean-to-mean comparison hides the shape that matters: an
    /// arm at 1.000 / 0.000 / 0.000 means 0.333 and passes a "below 0.462" bar while being at the
    /// ceiling on a third of the corpus. That is the same defect class as Eval 02's floor gate,
    /// which passed an arm scoring 0.000 / 1.000 / 1.000 on mean 0.667 &gt; floor 0.462.
    /// </remarks>
    /// <param name="Persona">The customer id.</param>
    /// <param name="Score">That customer's latent coverage for this arm, NaN when unscorable.</param>
    /// <param name="Floor">The random-draw floor at THIS arm's own k for that customer's gold.</param>
    private readonly record struct PersonaCoverage(string Persona, double Score, double Floor);

    private static async Task<(double Mean, double Floor, string Detail, int Presented, int Phantom, int Unresolved,
                              IReadOnlyList<PersonaCoverage> PerPersona)>
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
        var perPersona = new List<PersonaCoverage>();
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

            // The PAIR, kept together. A score and a floor from the same customer are the only two
            // numbers that may be compared; averaging each column first destroys the pairing.
            perPersona.Add(new PersonaCoverage(
                persona.Id,
                score.IsScorable ? score.Latent : double.NaN,
                randomFloor));

            detail.Add($"{persona.Id} {Format(score.Latent)} ({score.LatentServed}/{score.LatentTotal})");
        }

        return (
            scores.Count == 0 ? double.NaN : scores.Average(),
            floors.Count == 0 ? double.NaN : floors.Average(),
            string.Join(", ", detail),
            presentedTotal, phantomTotal, unresolvedTotal,
            perPersona);
    }

    // ══ Control 10 — the GATE RENDERER, checked in every branch it has. ══════════════════

    /// <summary>
    /// Proves Eval 02's gate renders the state it OBSERVED, not the state it wishes it had.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §8/B-11. <c>PrintCoverageGate</c> printed the sentence <i>"the single-shot control did NOT
    /// lead the live agent"</i> in BOTH branches; only the emoji changed. A reader who takes a gate
    /// from its sentence — which is what a sentence is for — read a failure as a pass, and the same
    /// shape sat on GATE 1. There is no way to catch that from a green exit code, because the exit
    /// code was right and the rendering was wrong.
    /// </para>
    /// <para>
    /// The check is possible at all because the renderer is now a pure function
    /// (<c>EvalPrinter.CoverageGateLines</c>) rather than a wall of <c>Console.WriteLine</c>. It
    /// asserts the four GATE 2 states produce four DIFFERENT texts, that the failing one says
    /// "DID beat", and that GATE 1's failing branch names the personas that fell below their floor
    /// instead of reprinting the passing claim.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckCoverageGateRendering()
    {
        const int scorable = 12;
        string[] below = ["USR-JV-08", "USR-NK-12"];

        static string Render(bool aboveFloor, IReadOnlyList<string> belowFloor, EvalPrinter.CoverageGate2State state) =>
            string.Join("\n", EvalPrinter.CoverageGateLines(aboveFloor, belowFloor, scorable, state, gate2Detail: null));

        string gate1Pass = Render(true, [], EvalPrinter.CoverageGate2State.ControlDidNotLead);
        string gate1Fail = Render(false, below, EvalPrinter.CoverageGate2State.ControlDidNotLead);
        string controlLed = Render(true, [], EvalPrinter.CoverageGate2State.ControlLed);
        string noPair = Render(true, [], EvalPrinter.CoverageGate2State.NoComparablePair);
        string noControl = Render(true, [], EvalPrinter.CoverageGate2State.NoControlRun);

        // Emoji stripped: two branches that differ only by ✅/❌ are the defect, not the fix.
        static string Body(string text) => text.Replace("✅", "", StringComparison.Ordinal)
                                               .Replace("❌", "", StringComparison.Ordinal);

        var problems = new List<string>();

        if (!controlLed.Contains("❌ GATE 2", StringComparison.Ordinal)
            || !controlLed.Contains("DID beat", StringComparison.Ordinal))
        {
            problems.Add("the control-led branch does not print '❌ GATE 2 … DID beat'");
        }

        if (!gate1Pass.Contains("did NOT beat", StringComparison.Ordinal))
            problems.Add("the passing GATE 2 branch no longer says 'did NOT beat'");

        if (string.Equals(Body(gate1Pass), Body(controlLed), StringComparison.Ordinal))
            problems.Add("GATE 2 pass and GATE 2 control-led differ only by the emoji");

        foreach (var (a, b, what) in new[]
                 {
                     (controlLed, noPair, "control-led vs no-comparable-pair"),
                     (controlLed, noControl, "control-led vs no-control-run"),
                     (noPair, noControl, "no-comparable-pair vs no-control-run"),
                 })
        {
            if (string.Equals(Body(a), Body(b), StringComparison.Ordinal))
                problems.Add($"GATE 2 renders the same text for {what}");
        }

        if (string.Equals(Body(gate1Pass), Body(gate1Fail), StringComparison.Ordinal))
            problems.Add("GATE 1 pass and GATE 1 fail differ only by the emoji");

        foreach (string persona in below)
        {
            if (!gate1Fail.Contains(persona, StringComparison.Ordinal))
                problems.Add($"GATE 1's failing branch does not name {persona}, the persona that fell below its floor");
        }

        if (!gate1Fail.Contains("BELOW", StringComparison.Ordinal))
            problems.Add("GATE 1's failing branch does not say the personas were BELOW their floor");

        return new ControlRowSnapshot(
            "CoverageGateRendering",
            "Eval 02's gate must RENDER THE OBSERVED STATE: the four GATE 2 states must produce four "
          + "different sentences, the control-led one must read '❌ GATE 2 — the single-shot control DID "
          + "beat the live agent', and GATE 1's failing branch must name the personas below their own "
          + "floor. Two branches differing only by ✅/❌ is the defect (§8, B-11), not the fix.",
            problems.Count == 0
                ? "all four GATE 2 branches and both GATE 1 branches render distinct text; the control-led "
                + "branch reads '❌ GATE 2 … DID beat'; the GATE 1 failure names USR-JV-08, USR-NK-12"
                : $"{problems.Count} rendering fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 11 — the PRE-REGISTERED RULE's evaluator, in all three verdicts. ══════════

    /// <summary>
    /// Proves the <c>≥ 10 of 12</c> rule has an evaluator that can reach every one of its three
    /// verdicts — including the two it does not reach on this corpus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §8/B-2. The rule text printed for eleven revisions with no <c>WinsRequired</c>, no threshold
    /// comparison and no verdict anywhere behind it, above a sign-test panel that had once rendered
    /// a green 12/0/0 for a different pair. A rule that cannot fail is not pre-registered — and on
    /// this corpus the live answer is NOT EVALUATED, because the loop arm runs deterministically and
    /// does not enter the sign test, so the run itself can only ever exercise ONE of the three
    /// branches.
    /// </para>
    /// <para>
    /// So the other two are exercised here, with synthetic outcomes: 10 wins of 12 must render MET,
    /// 9 must render NOT MET, and an all-refused comparison must render NOT EVALUATED rather than
    /// quietly passing. If someone pins the verdict to a constant, this row goes red.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckPreRegisteredRuleReachability()
    {
        const string reference = CoverageArms.Live;
        const string entrant = CoverageArms.SingleShot;      // a runnable arm that DOES enter the sign test

        static SignTestOutcome Outcome(string armB, int wins, int losses, int ties, IReadOnlyList<string>? refused = null) =>
            new(CoverageArms.Live, armB, wins, losses, ties,
                PValue: 0.5, MeanDelta: 0, CiLow: double.NaN, CiHigh: double.NaN, MinimumAttainableP: 0.0005,
                Metric: "recall", NotComparable: refused, DeclaredK: CoverageArms.DeclaredK);

        var met = PreRegisteredRule.Evaluate(reference, entrant, [Outcome(entrant, 10, 2, 0)], "synthetic");
        var notMet = PreRegisteredRule.Evaluate(reference, entrant, [Outcome(entrant, 9, 3, 0)], "synthetic");
        var refusedAll = PreRegisteredRule.Evaluate(
            reference, entrant, [Outcome(entrant, 0, 0, 0, ["all 12 refused at unequal k"])], "synthetic");
        var missing = PreRegisteredRule.Evaluate(reference, entrant, [], "synthetic");

        // And the pair the rule is actually ABOUT, exactly as Eval 02 evaluates it.
        var live = PreRegisteredRule.Evaluate(reference, CoverageArms.DiscoveryWorkflow, [], "synthetic");

        var problems = new List<string>();
        if (PreRegisteredRule.WinsRequired != 10) problems.Add($"WinsRequired is {PreRegisteredRule.WinsRequired}, not 10");
        if (met.Verdict != PreRegisteredRuleVerdict.Met) problems.Add($"10 of 12 rendered {met.Label}, not MET");
        if (notMet.Verdict != PreRegisteredRuleVerdict.NotMet) problems.Add($"9 of 12 rendered {notMet.Label}, not NOT MET");
        if (refusedAll.Verdict != PreRegisteredRuleVerdict.NotEvaluated)
            problems.Add($"an all-refused comparison rendered {refusedAll.Label}, not NOT EVALUATED");
        if (missing.Verdict != PreRegisteredRuleVerdict.NotEvaluated)
            problems.Add($"a missing outcome rendered {missing.Label}, not NOT EVALUATED");
        if (live.Verdict == PreRegisteredRuleVerdict.Met)
            problems.Add("the loop-vs-agent pair rendered MET with no outcome on the panel");
        if (live.Reason.Length == 0) problems.Add("the loop-vs-agent verdict carries no reason");

        return new ControlRowSnapshot(
            "PreRegisteredRuleReachable",
            $"the design's ≥ {PreRegisteredRule.WinsRequired}-of-{PreRegisteredRule.PreRegisteredPairs} rule must have an "
          + "EVALUATOR that reaches all three verdicts: 10 wins → MET, 9 wins → NOT MET, every pair refused → NOT "
          + "EVALUATED. On this corpus the live pair only ever reaches the third, so the other two are exercised on "
          + "synthetic outcomes — a rule that cannot fail is not pre-registered (§8, B-2).",
            problems.Count == 0
                ? $"WinsRequired = {PreRegisteredRule.WinsRequired} · 10/2/0 → MET · 9/3/0 → NOT MET · all-refused → NOT "
                + $"EVALUATED · missing outcome → NOT EVALUATED · the live loop-vs-agent pair → {live.Label} "
                + $"({live.Reason})"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 12 — the own-k RE-READ, on reps that presented DIFFERENT counts. ═════════

    /// <summary>
    /// Proves the own-k re-read survives — and stays honest on — the case that crashed the
    /// 2026-09-05 paid run: a live arm whose repetitions presented different numbers of items.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What happened.</b> <c>OwnKReread.FromThisRun</c> graded each repetition at its OWN count
    /// and handed the results to <see cref="CoverageScore.Mean"/>, which refuses — correctly — to
    /// average cuts made at different declared budgets. On that run exactly two personas triggered
    /// it (5 / 6 / 5 and 4 / 5 / 5) and the process died after all 36 live turns, taking both gates
    /// and the cost panel with it. <b>The guard is not the defect and is not relaxed here</b>; the
    /// last check below re-asserts that it still throws.
    /// </para>
    /// <para>
    /// <b>Why it needs a control at all.</b> The condition is invisible to a one-repetition run and
    /// to any run whose arm presents a constant k, so nothing in the free lane could reach it. This
    /// row reaches it deterministically, in milliseconds, with no model and no credentials — and it
    /// pins the DIRECTION of the fix as well as its existence: the common budget is the MINIMUM the
    /// arm presented, not the rounded rep-mean, so a rep that presented more is cut DOWN. Recall is
    /// monotone in k, so that can only lower the number of the arm under test. A re-read that
    /// rounded up would grade a rep at a budget it never presented, in the flattering direction.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckOwnKRereadAtVaryingK()
    {
        const string liveArm = "live — k varies across reps";
        const string controlArm = "control — deterministic";

        var problems = new List<string>();
        var golds = CoveragePersonas.All.ToDictionary(p => p.Id, p => InterestMapGold.Derive(p.Id), StringComparer.Ordinal);

        // Six real catalogue products in one fixed order. Every cut below is a prefix of this list,
        // so every citation the graders resolve is a real one.
        var skus = Catalogue.Default.CoreProducts.Take(6).Select(p => p.Id).ToList();
        var scorable = CoveragePersonas.All.Where(p => golds.TryGetValue(p.Id, out var g) && !g.LatentIsEmpty)
                                           .Select(p => p.Id).Take(3).ToList();

        if (skus.Count < 6 || scorable.Count < 3)
        {
            return new ControlRowSnapshot(
                "OwnKRereadAtVaryingK",
                "the own-k re-read must survive reps that presented different counts.",
                $"FIXTURE UNAVAILABLE — {skus.Count} catalogue product(s) and {scorable.Count} scorable persona(s); "
              + "this row needs 6 and 3. Reported as a fault, not skipped: an unbuildable control is not a passing one.",
                false);
        }

        static PresentedCall Call(string sku, int order) => new(sku, "", "", false, order, null, true, true);
        List<PresentedCall> Take(int n) => [.. skus.Take(n).Select((s, i) => Call(s, i + 1))];

        // The three shapes: the two the live run actually produced, and the uniform one that must
        // not move. 4 / 5 / 5 is the discriminating case — its rounded rep-mean is 5 and its
        // minimum is 4, so a row that reports 5 has rounded UP to a budget one rep never reached.
        var shapes = new (string Persona, int[] Reps, int ExpectedK, bool ExpectedUniform)[]
        {
            (scorable[0], [5, 6, 5], 5, false),
            (scorable[1], [4, 5, 5], 4, false),
            (scorable[2], [5, 5, 5], 5, true),
        };

        var ownK = new PairedCoverageReport();
        foreach (var (persona, repCounts, _, _) in shapes)
        {
            var reps = repCounts.Select(Take).ToList();
            foreach (var rep in reps) ownK.RecordPresented(persona, liveArm, rep);
            ownK.Record(persona, liveArm, CoverageScore.Mean(
                [.. reps.Select(r => InterestCoverageGrader.GradeWithControls(persona, golds, r))]));

            var control = Take(6);
            ownK.RecordPresented(persona, controlArm, control);
            ownK.Record(persona, controlArm, InterestCoverageGrader.GradeWithControls(persona, golds, control));
        }

        PairedCoverageReport report;
        IReadOnlyList<OwnKRereadRow> rows;
        try
        {
            (report, rows, _) = OwnKReread.FromThisRun(ownK, liveArm, [controlArm], golds);
        }
        catch (Exception ex)
        {
            return new ControlRowSnapshot(
                "OwnKRereadAtVaryingK",
                "the own-k re-read must survive a live arm whose reps presented DIFFERENT counts (5/6/5 and 4/5/5 "
              + "on the 2026-09-05 run), cut every cell to ONE budget — the MINIMUM, never a rounded mean — and "
              + "leave the equal-k pairing comparable. The rep-averaging guard must still refuse mixed budgets.",
                $"THREW: {ex.GetType().Name} — {ex.Message} This is the 2026-09-05 crash, reproduced offline in "
              + "milliseconds. Two of twelve personas were enough to end a 36-turn paid run.",
                false);
        }

        foreach (var (persona, repCounts, expectedK, expectedUniform) in shapes)
        {
            var row = rows.FirstOrDefault(r => string.Equals(r.PersonaId, persona, StringComparison.Ordinal));
            if (row is null) { problems.Add($"{persona}: no re-read row at all."); continue; }

            string shape = string.Join("/", repCounts);
            if (row.KLive != expectedK)
                problems.Add($"{persona} presented {shape} and the row reports k = {row.KLive}, not {expectedK} — the minimum.");
            if (row.KUniform != expectedUniform)
                problems.Add($"{persona} presented {shape} and the row reports KUniform = {row.KUniform}.");
            if (!expectedUniform && !row.Note.Contains(shape, StringComparison.Ordinal))
                problems.Add($"{persona}'s note does not print the raw per-rep counts {shape}; the reader cannot see what was cut.");

            if (row.Live.DeclaredK != expectedK || row.Live.PresentedCount != expectedK)
                problems.Add($"{persona}'s LIVE cell is at DeclaredK {row.Live.DeclaredK} / k {row.Live.PresentedCount}, not {expectedK}.");
            if (!row.Live.KUniformAcrossReps)
                problems.Add($"{persona}'s LIVE cell is marked NON-uniform after a common cut, so the equal-k rule will refuse a pair it should accept.");

            if (row.ControlsAtKLive.TryGetValue(controlArm, out var cut) && cut is { } c)
            {
                if (c.DeclaredK != expectedK || c.PresentedCount != expectedK)
                    problems.Add($"{persona}'s CONTROL cell is at DeclaredK {c.DeclaredK} / k {c.PresentedCount}, not {expectedK}.");
            }
            else
            {
                problems.Add($"{persona}: the control was not cut to k = {expectedK} at all.");
            }
        }

        // The cut must run in the NON-FLATTERING direction. Persona 0's second rep presented six;
        // grading it at six can only serve at least as many gold tokens as grading it at five.
        double atSix = InterestCoverageGrader.GradeAtDeclaredK(scorable[0], golds, Take(6), 6).Latent;
        double atFive = InterestCoverageGrader.GradeAtDeclaredK(scorable[0], golds, Take(6), 5).Latent;
        if (atFive > atSix + 1e-12)
            problems.Add($"cutting a 6-item rep to k = 5 RAISED its recall ({atFive:F3} vs {atSix:F3}) — the cut is not a prefix.");

        // The pairing the re-read exists to make possible must actually be available.
        var paired = report.SignTestAtEqualK(liveArm, controlArm, CoverageMetric.Recall);
        if (paired.ComparedN != shapes.Length || paired.Excluded.Count != 0)
        {
            problems.Add($"the equal-k sign test compared {paired.ComparedN} of {shapes.Length} pairs and refused "
                       + $"{paired.Excluded.Count} — a re-read that leaves every pair NOT COMPARABLE has re-read nothing.");
        }

        // ⚠ AND THE GUARD IS STILL A GUARD. Relaxing Mean to get past the crash would have been the
        // one repair that made the eval worse, so this row would go red if anyone did.
        bool guardHeld = false;
        try
        {
            _ = CoverageScore.Mean(
            [
                InterestCoverageGrader.GradeAtDeclaredK(scorable[0], golds, Take(5), 4),
                InterestCoverageGrader.GradeAtDeclaredK(scorable[0], golds, Take(5), 5),
            ]);
        }
        catch (ArgumentException)
        {
            guardHeld = true;
        }

        if (!guardHeld)
            problems.Add("CoverageScore.Mean averaged two cuts made at DIFFERENT declared budgets — the equal-k guard has been relaxed.");

        return new ControlRowSnapshot(
            "OwnKRereadAtVaryingK",
            "the own-k re-read must survive a live arm whose reps presented DIFFERENT counts (5/6/5 and 4/5/5 on "
          + "the 2026-09-05 run, which it crashed on), cut every cell of a row to ONE budget — the MINIMUM the arm "
          + "presented, never a rounded mean that no rep reached — leave the equal-k pairing COMPARABLE, print the "
          + "raw per-rep counts, and leave a uniform persona untouched. CoverageScore.Mean must still REFUSE mixed "
          + "budgets: the guard is not the defect.",
            problems.Count == 0
                ? $"5/6/5 → k = 5 (non-uniform, counts printed) · 4/5/5 → k = 4, the MINIMUM, where the rounded mean "
                + $"would have said 5 · 5/5/5 → k = 5, uniform · {paired.ComparedN} of {shapes.Length} pairs comparable, "
                + $"0 refused · cutting 6 → 5 moved recall {atSix:F3} → {atFive:F3} (never up) · Mean still refuses "
                + "two budgets"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 13 — Eval 09's rule must say NOT COMPARABLE, and its remedy must fit the run. ══

    /// <summary>
    /// Proves the three things Eval 09's decision rule has to do once its pairing became equal-k:
    /// refuse to call an unmade comparison a draw, keep the ordinary verdicts reachable, and print
    /// a remedy derived from THIS run's ledger rather than from a previous run's diagnosis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an undecidable comparison needs its own verdict.</b> An exact sign test over zero
    /// pairs returns p = 1.0000 by arithmetic. Fed to a rule that reads only "p &lt; alpha?", that
    /// renders as NO DIFFERENCE DETECTED — the two arms agreeing — when what actually happened is
    /// that no pair could be compared at all. It is the flattering misreading, and it is the one a
    /// run with 0 of 21 reps at the agent's k would have produced.
    /// </para>
    /// <para>
    /// <b>Why the remedy is checked.</b> The ArmNotLive panel prescribed raising
    /// <c>DiscoveryLoopOptions.ModelCallTimeout</c> unconditionally, citing a 2026-09-04 run in
    /// which 6 of 7 calls were abandoned at the ceiling. The 2026-09-05 run's ledger read 120
    /// attempted / 120 returned / 0 cancelled and five stages fell back anyway — on unparseable
    /// output. Both ledgers are replayed here and the text must differ between them.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckEval09RuleAndRemedy()
    {
        var problems = new List<string>();

        static SignTestOutcome Pairing(int wins, int losses, int ties, double p, IReadOnlyList<string>? refused = null) =>
            new(Eval09_HypothesisComparison.ArmSingleAgent, Eval09_HypothesisComparison.ArmWorkflow,
                wins, losses, ties, PValue: p, MeanDelta: 0.0, CiLow: double.NaN, CiHigh: double.NaN,
                MinimumAttainableP: wins + losses > 0 ? Eval09PreRegistration.TheoreticalMinimumTwoSidedP(wins + losses) : 1.0,
                Metric: "recall", NotComparable: refused, DeclaredK: CoverageArms.DeclaredK);

        static Eval09Budget Budget(int attempted, int returned, int cancelled) =>
            new(BothArmsRan: true, BothArmsReportedTokens: cancelled == 0,
                AgentTokensPerTurn: 100_000, WorkflowTokensPerTurn: 90_000, Ratio: 1.11,
                Reasons: cancelled == 0 ? [] : ["a call was cancelled"],
                WorkflowAttempted: attempted, WorkflowReturned: returned, WorkflowCancelled: cancelled, WorkflowFailed: 0);

        var cleanBudget = Budget(120, 120, 0);
        var loopLeads = Pairing(6, 2, 4, 0.2891);

        // ── 1. An UNMADE comparison is not a draw. ──
        var refusedAll = Pairing(0, 0, 0, 1.0,
            [.. Enumerable.Range(1, 12).Select(i => $"USR-{i:00} (k 5 vs 7)")]);
        var undecidable = Eval09PreRegistration.Decide(refusedAll, loopLeads, cleanBudget, silentCells: 0, voidedCells: 0);

        if (undecidable.Outcome != Eval09Outcome.NotComparableAtEqualK)
            problems.Add($"12 refused pairs and p = 1.0000 rendered {undecidable.Outcome}, not NotComparableAtEqualK — an unmade comparison read as agreement between the arms.");
        if (!undecidable.Headline.Contains("NOT COMPARABLE", StringComparison.Ordinal))
            problems.Add("the undecidable headline does not say NOT COMPARABLE.");
        if (!undecidable.Reasons.Any(r => r.Contains("NOT COMPARABLE", StringComparison.Ordinal) && r.Contains("CLAUSE 1", StringComparison.Ordinal)))
            problems.Add("clause 1 does not report the refusal — it still reads as a significance result.");

        // ── 2. …and the ordinary verdicts are still reachable. Refusing everything would be an
        //       equally useless rule, and it would be invisible from the row above alone. ──
        var noDifference = Eval09PreRegistration.Decide(Pairing(4, 6, 2, 0.7539), loopLeads, cleanBudget, 0, 0);
        if (noDifference.Outcome != Eval09Outcome.NoDifferenceDetected)
            problems.Add($"a comparable, non-significant pairing rendered {noDifference.Outcome}, not NoDifferenceDetected.");

        var workflowWins = Eval09PreRegistration.Decide(Pairing(11, 1, 0, 0.0063), loopLeads, cleanBudget, 0, 0);
        if (workflowWins.Outcome != Eval09Outcome.WorkflowWins)
            problems.Add($"a comparable, significant workflow lead rendered {workflowWins.Outcome}, not WorkflowWins — the new branch swallowed a real result.");

        // ── 2b. THE LOOP CONTROL IS SUBJECT TO THE SAME RULE, and it gates the exit code.
        //
        //   `Losses <= Wins` is trivially TRUE at 0/0, so once the pairing became equal-k an
        //   all-refused rubber-stamp comparison passed the clause whose only job is to void an
        //   architecture claim — and passed GATE 3, which decides Eval 09's exit code. An absent
        //   control is not a cleared one.
        static SignTestOutcome StampPairing(int wins, int losses, int ties, IReadOnlyList<string>? refused = null) =>
            new(Eval09_HypothesisComparison.ArmRubberStamp, Eval09_HypothesisComparison.ArmWorkflow,
                wins, losses, ties, PValue: 1.0, MeanDelta: 0.0, CiLow: double.NaN, CiHigh: double.NaN,
                MinimumAttainableP: 1.0, Metric: "recall", NotComparable: refused, DeclaredK: CoverageArms.DeclaredK);

        var stampRefusedAll = StampPairing(0, 0, 0, [.. Enumerable.Range(1, 12).Select(i => $"USR-{i:00} (k 5 vs 7)")]);

        if (Eval09PreRegistration.LoopIsLoadBearing(stampRefusedAll))
            problems.Add("an all-refused rubber-stamp comparison PASSED the loop-is-load-bearing test — 0 ≤ 0 read as a control that was cleared, and GATE 3 turns on that.");
        if (!Eval09PreRegistration.LoopIsLoadBearing(StampPairing(3, 3, 6)))
            problems.Add("a comparable TIE failed the loop-is-load-bearing test — the fix went too far and now refuses a real observation of no difference.");
        if (Eval09PreRegistration.LoopIsLoadBearing(StampPairing(2, 7, 0)))
            problems.Add("a rubber stamp that LED still passed the loop-is-load-bearing test.");

        var stampUndecidable = Eval09PreRegistration.Decide(Pairing(11, 1, 0, 0.0063), stampRefusedAll, cleanBudget, 0, 0);
        if (stampUndecidable.Outcome != Eval09Outcome.NotComparableAtEqualK)
            problems.Add($"a significant workflow lead with an ALL-REFUSED rubber-stamp control rendered {stampUndecidable.Outcome} — a win declared while the clause that would void it was never evaluated.");
        if (!stampUndecidable.Reasons.Any(r => r.Contains("CLAUSE 3", StringComparison.Ordinal) && r.Contains("NOT COMPARABLE", StringComparison.Ordinal)))
            problems.Add("clause 3 does not report the refusal — it still reads 'the rubber stamp did not lead' on a comparison that was never made.");

        // ── 3. The remedy must come from THIS run's ledger. ──
        var judged = new Eval09JudgedReport(Eval09PreRegistration.JudgedCriteria);
        var notLive = new Eval09Verdict(Eval09Outcome.ArmNotLive, "NO WIN — the live arm was not live.", []);

        string AllReturned() => string.Join(" ", Eval09_HypothesisComparison.NegativeResultText(
            notLive, Pairing(4, 5, 3, 0.7539), Budget(120, 120, 0), 0.701, 0.750, judged));
        string SomeCancelled() => string.Join(" ", Eval09_HypothesisComparison.NegativeResultText(
            notLive, Pairing(4, 5, 3, 0.7539), Budget(7, 1, 6), 0.701, 0.750, judged));

        string returnedText = AllReturned();
        string cancelledText = SomeCancelled();

        // The marker is the PRESCRIPTION, not the identifier. The corrected text still names
        // ModelCallTimeout — to say it would fix nothing — so matching the bare symbol would go
        // red on the fix itself.
        const string prescribesTimeout = "raise the per-call ceiling";

        if (returnedText.Contains(prescribesTimeout, StringComparison.Ordinal))
            problems.Add("with 120 attempted / 120 returned / 0 cancelled the remedy STILL prescribes raising the per-call ceiling — a fix for a fault that did not occur.");
        if (!returnedText.Contains("parse", StringComparison.OrdinalIgnoreCase))
            problems.Add("with every call returned the remedy does not name unparseable output, which is the only remaining way a stage can fall back.");
        if (!returnedText.Contains("120 attempted", StringComparison.Ordinal))
            problems.Add("the remedy does not quote the ledger it claims to be derived from.");
        if (!cancelledText.Contains(prescribesTimeout, StringComparison.Ordinal))
            problems.Add("with 6 of 7 calls cancelled the remedy no longer offers the timeout fix — the correction went too far and now misses the case it was written for.");
        if (string.Equals(returnedText, cancelledText, StringComparison.Ordinal))
            problems.Add("the remedy text is IDENTICAL for a run whose calls all returned and one whose calls were cancelled — it is not derived from the ledger at all.");

        // ── 3b. AND THE PANEL MUST NOT QUOTE THE UNMADE COMPARISON EITHER.
        //
        //   ArmNotLive, SilenceInTheComparison and Confounded all fire BEFORE the NOT-COMPARABLE
        //   branch, so an undecidable primary reaches their prose. It used to interpolate
        //   primary.Wins/Losses/Ties and primary.PValue unconditionally, and on the 2026-09-05 run
        //   shape (voided cells + 0 comparable pairs) the panel read "the paired result ran 0/0/0
        //   (W/L/T) in neither direction … at p = 1.0000" — the exact reading clause 1 had just
        //   been rewritten to refuse, one box higher up the page.
        string voidedOnUndecidable = string.Join(" ", Eval09_HypothesisComparison.NegativeResultText(
            notLive, refusedAll, cleanBudget, 0.701, 0.750, judged));

        if (voidedOnUndecidable.Contains("0/0/0", StringComparison.Ordinal))
            problems.Add("the ArmNotLive panel still prints a W/L/T of 0/0/0 for a comparison in which every pair was REFUSED — an unmade comparison rendered as a tied one.");
        if (voidedOnUndecidable.Contains("p = 1.0000", StringComparison.Ordinal))
            problems.Add("the ArmNotLive panel still quotes p = 1.0000 for an undecidable primary — a number produced by arithmetic over zero pairs, printed as a measurement.");
        if (!voidedOnUndecidable.Contains("NOT COMPARABLE", StringComparison.Ordinal))
            problems.Add("the ArmNotLive panel does not say the endpoint was NOT COMPARABLE, so the reader is left to infer it from a missing number.");

        // …and it still reports a real one. Suppressing the numbers on every verdict would look
        // identical on the row above and would delete the finding rather than qualify it.
        string voidedOnComparable = string.Join(" ", Eval09_HypothesisComparison.NegativeResultText(
            notLive, Pairing(4, 5, 3, 0.7539), cleanBudget, 0.701, 0.750, judged));
        if (!voidedOnComparable.Contains("4/5/3", StringComparison.Ordinal) || !voidedOnComparable.Contains("p = 0.7539", StringComparison.Ordinal))
            problems.Add("the ArmNotLive panel no longer prints the W/L/T and p of a comparison that WAS made — the suppression is unconditional.");

        // ── 4. And the monotonicity the honest report rests on. Cutting an over-filled answer to
        //       the declared budget can only REMOVE served gold, never add it — which is why
        //       "the workflow's own-k number is its best case at equal k" is a fact, not a hope. ──
        var golds = CoveragePersonas.All.ToDictionary(p => p.Id, p => InterestMapGold.Derive(p.Id), StringComparer.Ordinal);
        string persona = CoveragePersonas.All.First(p => golds.TryGetValue(p.Id, out var g) && !g.LatentIsEmpty).Id;
        var eight = Catalogue.Default.CoreProducts.Take(8)
            .Select((prod, i) => new PresentedCall(prod.Id, "", "", false, i + 1, null, true, true)).ToList();
        double atEight = InterestCoverageGrader.GradeAtDeclaredK(persona, golds, eight, 8).Latent;
        double atFive = InterestCoverageGrader.GradeAtDeclaredK(persona, golds, eight, CoverageArms.DeclaredK).Latent;
        if (atFive > atEight + 1e-12)
            problems.Add($"cutting an 8-item answer to k = {CoverageArms.DeclaredK} RAISED recall ({atFive:F3} vs {atEight:F3}) — the cut is not a prefix and the monotonicity argument does not hold.");

        // ── 5. THE METER UNDER CLAUSE 2 MUST NOT FOLD HALF A USAGE BLOCK IN AS A ZERO. ──
        //
        //   Clause 2 is the precondition that decides whether this eval may name a winner at all,
        //   and every number in it comes off Eval09TokenLedger. Until 2026-09-06 the ledger's guard
        //   read `InputTokenCount is null AND OutputTokenCount is null`, so a response with one side
        //   missing was recorded as a COMPLETE reading with a zero on the missing side:
        //   ReturnedWithoutUsage stayed 0, UsageComplete stayed true, GATE 2 passed, and the ratio
        //   was computed from a half-measured total. Direction: FLATTERING — a lower bound printed
        //   as a spend. It is the same defect §60.2 fixed in the agent's ChatSpend and did not fix
        //   here. Three states, three readings, asserted apart.
        var whole = new Eval09TokenLedger("probe · whole");
        whole.RecordTurn();
        whole.RecordReturned(new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 });

        var half = new Eval09TokenLedger("probe · half");
        half.RecordTurn();
        half.RecordReturned(new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = 100, OutputTokenCount = null });

        var absent = new Eval09TokenLedger("probe · absent");
        absent.RecordTurn();
        absent.RecordReturned(null);

        if (!whole.UsageComplete)
            problems.Add("a ledger fed ONE complete usage block reads incomplete — the guard has gone red on the good case and would call every run confounded.");
        if (half.UsageComplete)
            problems.Add("a ledger fed HALF a usage block still reads COMPLETE, so clause 2's ratio is computed from a total with a hole in it and GATE 2 passes on an unmeasured budget.");
        if (half.PartialUsage != 1)
            problems.Add($"a half usage block was counted as {half.PartialUsage} partial call(s), so the panel cannot name what is missing.");
        if (half.ReturnedWithoutUsage != 0)
            problems.Add("a half usage block was counted as NO usage at all — the two are different facts and the tokens that DID arrive would be discarded.");
        if (absent.ReturnedWithoutUsage != 1 || absent.PartialUsage != 0)
            problems.Add("an absent usage block is no longer counted as absent — the original hole this ledger was built to see.");
        if (half.UsageGap is not { } halfGap || !halfGap.Contains("LOWER BOUND", StringComparison.Ordinal))
            problems.Add("the gap line for a half usage block does not call the total a LOWER BOUND, so a partial spend prints as a spend.");
        if (absent.UsageGap is not { } absentGap)
            problems.Add("an absent usage block produces NO gap line at all, so the run reports nothing missing.");
        else if (string.Equals(absentGap, half.UsageGap, StringComparison.Ordinal))
            problems.Add("the absent-block gap line and the half-block gap line are IDENTICAL — the panel cannot tell a reader which of the two happened, which is the conflation this clause exists for.");
        else if (absentGap.Contains("LOWER BOUND", StringComparison.Ordinal))
            problems.Add("the absent-block gap line calls its total a LOWER BOUND — an absent block contributes nothing at all, and borrowing the half-block's wording erases the difference from the other side.");
        if (string.Equals(half.Accounting, absent.Accounting, StringComparison.Ordinal))
            problems.Add("the per-arm accounting line is IDENTICAL for a half block and an absent one.");

        return new ControlRowSnapshot(
            "Eval09RuleAndRemedy",
            "Eval 09 pairs at equal k now, so its rule must (a) render an all-refused comparison as NOT COMPARABLE "
          + "rather than as NO DIFFERENCE — an empty sign test returns p = 1.0000 by arithmetic and that is the "
          + "absence of a comparison, not agreement; (b) still reach WorkflowWins and NoDifferenceDetected on "
          + "comparable pairings, or the refusal is just a rule that never decides; (c) apply the SAME rule to the "
          + "rubber-stamp control, which gates the exit code through GATE 3 and whose 'Losses ≤ Wins' test is "
          + "trivially true at 0/0; (d) keep the W/L/T and the p-value out of the PANEL as well, on the verdicts "
          + "that fire before the NOT-COMPARABLE branch; (e) print an ArmNotLive remedy DERIVED from the run's own "
          + "ledger — no timeout fix on a run with 0 cancelled calls, and the timeout fix still offered on one with "
          + "6; (f) rest on a cut that is a prefix, so cutting to k can only lower recall; and (g) meter clause 2 "
          + "with a ledger that treats HALF a usage block as neither a complete reading nor an absent one — folding "
          + "the missing side in as a zero left UsageComplete true and computed the ratio from a lower bound, the "
          + "same defect §60.2 fixed in the agent's ChatSpend and did not fix here.",
            problems.Count == 0
                ? $"12 refused → NOT COMPARABLE (clause 1 names the refusal) · 4/6/2 p=0.7539 → NoDifferenceDetected · "
                + $"11/1/0 p=0.0063 → WorkflowWins · an all-refused rubber stamp FAILS the load-bearing test and voids "
                + $"an 11/1 lead (clause 3 names the refusal), a tie still passes · the ArmNotLive panel prints no "
                + $"0/0/0 and no p on an undecidable primary, and still prints 4/5/3 p=0.7539 on a comparable one · "
                + $"remedy at 120/120/0 names parsing and NOT the timeout, at 7/1/6 names the timeout, and the two "
                + $"texts differ · cutting 8 → {CoverageArms.DeclaredK} moved recall "
                + $"{atEight:F3} → {atFive:F3} (never up) · ledger: whole block complete=True, half block "
                + $"complete={half.UsageComplete} partial={half.PartialUsage} no-usage={half.ReturnedWithoutUsage} "
                + $"and its gap says LOWER BOUND, absent block no-usage={absent.ReturnedWithoutUsage} partial="
                + $"{absent.PartialUsage} — three states, three readings"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 14 — the judge's echo must join, and a real invention must still not. ═════

    /// <summary>
    /// Proves Eval 05's criterion join survives the form the evaluator itself renders criteria in,
    /// and that it has not become promiscuous in the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What actually happened on 2026-09-05.</b>
    /// <c>src/AgentEval.Core/Core/ChatClientEvaluator.cs:46</c> prints the rubric as
    /// <c>$"{i + 1}. {c}"</c>. The judge echoed each criterion back with that ordinal attached, and
    /// <c>Reconcile</c> compared it against the UNPREFIXED declared text — so the exact match, the
    /// normalised match and the 48-character prefix match all failed on a three-character offset.
    /// Every declared criterion lost its verdict, every returned criterion became "a criterion
    /// nobody declared", and three cells scored 0.0/100 as an artefact. USR-NB-01's SEPARATION
    /// failure is downstream of it. <b>The judge did not invent a rubric; we did not recognise our
    /// own text.</b>
    /// </para>
    /// <para>
    /// <b>Both directions, because a looser matcher is the obvious wrong fix.</b> The echo must
    /// join; a criterion that really is different must still be refused; and the join must not be
    /// positional — the same criteria returned in reverse order must land on their own axes, not
    /// on their neighbours'.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckJudgeEchoJoins()
    {
        var problems = new List<string>();
        var rubric = Eval05_RecommendationQuality.Criteria.Discovery;

        // Exactly what ChatClientEvaluator renders, and therefore exactly what a faithful judge
        // echoes. Derived from the renderer's own expression, not transcribed from a log.
        static IReadOnlyList<CriterionResult> AsRendered(
            IReadOnlyList<Eval05_RecommendationQuality.WeightedCriterion> rubric, bool reversed = false) =>
        [
            .. (reversed ? rubric.Reverse() : rubric)
                .Select(w => (Index: rubric.ToList().FindIndex(x => x.Criterion.Key == w.Criterion.Key), w.Criterion))
                .Select(x => new CriterionResult
                {
                    Criterion = $"{x.Index + 1}. {x.Criterion.Text}",
                    Met = true,
                    Explanation = $"echoed for {x.Criterion.Key}",
                })
        ];

        // ── 1. The echo joins, and every declared criterion gets ITS OWN verdict. ──
        var (judged, extra) = Eval05_RecommendationQuality.Reconcile(rubric, AsRendered(rubric));

        int noVerdict = judged.Count(j => j.Met is null);
        if (noVerdict > 0)
            problems.Add($"{noVerdict} of {rubric.Count} declared criteria got NO verdict from a judge that echoed the rubric exactly as ChatClientEvaluator rendered it ('1. …'). That is the 2026-09-05 defect: 24 lost verdicts over 3 cells.");
        if (extra.Count > 0)
            problems.Add($"{extra.Count} echoed criterion(s) were reported as UNDECLARED although each is one of the {rubric.Count} declared ones with the evaluator's own ordinal in front of it.");
        foreach (var j in judged.Where(j => j.Met is not null))
        {
            if (!j.Explanation.Contains(j.Criterion.Key, StringComparison.Ordinal))
                problems.Add($"'{j.Criterion.Key}' was joined to the verdict for a DIFFERENT criterion ({j.Explanation}).");
        }

        // ── 2. …and it is not positional. Same criteria, reverse order, same answers. ──
        var (reversedJudged, reversedExtra) = Eval05_RecommendationQuality.Reconcile(rubric, AsRendered(rubric, reversed: true));
        if (reversedJudged.Count(j => j.Met is null) > 0 || reversedExtra.Count > 0)
            problems.Add("the same criteria returned in REVERSE order no longer join — the matcher depends on order.");
        foreach (var j in reversedJudged.Where(j => j.Met is not null))
        {
            if (!j.Explanation.Contains(j.Criterion.Key, StringComparison.Ordinal))
                problems.Add($"in reverse order '{j.Criterion.Key}' picked up another criterion's verdict ({j.Explanation}) — the join is POSITIONAL.");
        }

        // ── 3. A genuine invention must STILL be refused, and diagnosed as one. ──
        var invented = Eval05_RecommendationQuality.Reconcile(rubric,
        [
            new CriterionResult { Criterion = "1. The answer is written in iambic pentameter.", Met = true, Explanation = "invented" },
        ]);
        if (invented.Extra.Count != 1)
            problems.Add($"a criterion nobody declared was ACCEPTED ({invented.Extra.Count} refused, expected 1) — the ordinal fix made the matcher promiscuous.");
        else if (invented.Extra[0].LooksLikeAJoinFailure)
            problems.Add($"an invented criterion was diagnosed as a JOIN FAILURE ({invented.Extra[0].Diagnosis}) — the two faults are being merged again.");
        if (invented.Judged.Count(j => j.Met is null) != rubric.Count)
            problems.Add("an invented criterion silently supplied a verdict for a declared one.");

        // ── 4. And the diagnosis points the right way on the real case. ──
        var echoDiagnosis = Eval05_RecommendationQuality.Diagnose(
            $"1. {rubric[0].Criterion.Text}", rubric);
        if (!echoDiagnosis.LooksLikeAJoinFailure)
            problems.Add($"an echoed criterion is diagnosed as INVENTED ({echoDiagnosis.Diagnosis}) — the report would blame the judge for our matcher.");
        if (!string.Equals(echoDiagnosis.NearestKey, rubric[0].Criterion.Key, StringComparison.Ordinal))
            problems.Add($"the echo's nearest declared criterion is '{echoDiagnosis.NearestKey}', not '{rubric[0].Criterion.Key}'.");

        // ── 5. The enumeration stripper must not eat real words. ──
        var stripperCases = new[]
                 {
                     ("no sentence states a price", "no sentence states a price", "a two-letter word followed by a space is not a label"),
                     ("it asks at least one question", "it asks at least one question", "a word is not an ordinal"),
                     ("1. every recommendation", "every recommendation", "an ordinal IS an ordinal"),
                     ("- every recommendation", "every recommendation", "a bullet is a bullet"),
                     ("criterion 1 is met", "criterion 1 is met", "a long leading word is never a label"),
                     // ⚠ THE SHORT-WORD HOLE, found by the review pass of 2026-09-06. Every case above
                     // uses a leading word of FOUR OR MORE letters, so the table certified a rule that
                     // ate every short one: "re-check…" became "check…" and "ai-generated…" became
                     // "generated…". A separator is not enough — the run has to be shaped like a label
                     // AND be followed by a space.
                     ("re-check the sources", "re-check the sources", "a two-letter word plus a hyphen is a WORD"),
                     ("ai-generated text is labelled", "ai-generated text is labelled", "so is a two-letter initialism"),
                     ("top-3 results are relevant", "top-3 results are relevant", "and a three-letter one"),
                     ("no: the answer refuses", "no: the answer refuses", "a colon does not make a word a label"),
                     ("i.e. the second reading", "i.e. the second reading", "a marker is followed by a SPACE"),
                     ("iv. the fourth criterion", "the fourth criterion", "a real roman numeral still IS a label"),
                 };
        foreach (var (input, expected, why) in stripperCases)
        {
            string got = Eval05_RecommendationQuality.StripEnumeration(input);
            if (!string.Equals(got, expected, StringComparison.Ordinal))
                problems.Add($"StripEnumeration(\"{input}\") = \"{got}\", expected \"{expected}\" — {why}.");
        }

        return new ControlRowSnapshot(
            "JudgeEchoJoinsToDeclaredRubric",
            "ChatClientEvaluator.cs:46 renders the rubric as \"1. <text>\", so a judge that echoes faithfully returns "
          + "the ordinal too. Eval 05's join must recognise that as OUR criterion — on 2026-09-05 it did not, and 24 "
          + "verdicts over 3 cells were discarded as 'criteria nobody declared', scoring those cells 0.0/100 as an "
          + "artefact. The join must also stay strict: a genuinely different criterion is still refused and diagnosed "
          + "as INVENTED rather than as a join failure, the match is by TEXT and never by position, and the ordinal "
          + "stripper never eats a real leading word.",
            problems.Count == 0
                ? $"all {rubric.Count} echoed criteria join and carry their OWN verdicts · the same set in reverse "
                + "order joins identically (not positional) · an invented criterion is still refused and diagnosed "
                + $"INVENTED · an echo is diagnosed JOIN FAILURE against '{echoDiagnosis.NearestKey}' "
                + $"({echoDiagnosis.OverlapChars} shared chars) · {stripperCases.Length} of {stripperCases.Length} stripper cases behave, six of them short leading words"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 15 — a contentless request is not covered by whatever came back. ═════════

    /// <summary>
    /// Proves the coverage gate keys on whether an interest NAMES anything, not on how the
    /// retriever happened to score a ranked list — and that the two thresholds involved did not
    /// move to achieve it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The regression.</b> Deriving the dense floor per space (MEASUREMENT_STATUS §22) moved the
    /// real-vectors floor DOWN, from a transported 0.280 to 0.223. Luca Ferrari (USR-LF-04) has one
    /// purchase, zero independent signals and types <i>"Hi — what do you recommend for me?"</i>. He
    /// went from 0 candidates and <c>GAPS_UNRESOLVABLE</c> — the correct abstention — to 2
    /// candidates, a second discovery round and five recommendations, two of them espresso
    /// accessories credited to an <i>"Over-ear wireless"</i> interest.
    /// </para>
    /// <para>
    /// <b>Why this row is space-independent, and what it therefore cannot show.</b> The regression
    /// only appears under <c>--real-vectors</c>, which embeds every query LIVE and spends. So the
    /// mechanism is exercised here instead: a coverage row is SYNTHESISED with more candidates and
    /// a higher score than the real-space run produced, and the gate must still refuse it. That
    /// proves the gate no longer keys on the score. It does <b>not</b> prove the end-to-end
    /// real-space run abstains again — that needs a paid run and is not claimed.
    /// </para>
    /// <para>
    /// <b>Both directions, and the thresholds.</b> An interest that names something real must still
    /// be covered on the same row shape, or the fix is just a gate that never approves; and
    /// <c>MinCandidateScore</c> and the pre-calibration dense floor must both still read the values
    /// they were derived at, because moving a calibrated number to make one persona come out right
    /// is the failure this row exists to make visible.
    /// </para>
    /// </remarks>
    private static async Task<ControlRowSnapshot> CheckContentlessRequestIsNotCoveredAsync(
        IProductRetriever retriever,
        CancellationToken ct)
    {
        var problems = new List<string>();

        // ── The interest a contentless utterance produces, built by the SHIPPED mapper. ──
        static Interest SessionRequest(string utterance) =>
            DiscoveryInterestMapping.ToInterest(
                new InterestSignal(
                    Label: utterance,
                    Strength: 1.0,
                    EvidenceKind: InterestEvidenceKinds.StatedInSession,
                    EvidencePurchaseIds: []),
                "I-1");

        var contentless = SessionRequest(GalaxusDemoPrompts.LucaThinSignal);
        var vocabulary = InterestAttribution.Vocabulary(contentless);

        if (vocabulary.Count != 0)
        {
            problems.Add($"the contentless request \"{Shorten(GalaxusDemoPrompts.LucaThinSignal, 40)}\" produced an "
                       + $"attribution vocabulary of [{string.Join(", ", vocabulary)}] — it should name NOTHING.");
        }

        // ⚠ And specifically not OUR OWN label prefix. "stated this session: " is written by
        //   DiscoveryInterestMapping, not by the customer, so a product whose text contained
        //   "session" would otherwise have counted as covering the request — the harness supplying
        //   an input to its own gate.
        foreach (string ours in InterestAttribution.Fold(DiscoveryInterestMapping.SessionRequestLabelPrefix)
                     .Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (vocabulary.Contains(ours, StringComparer.Ordinal))
                problems.Add($"'{ours}' comes from OUR label prefix and is in the attribution vocabulary.");
        }

        // A request that DOES name something must keep its vocabulary.
        var stated = SessionRequest("I need a 58 mm espresso tamper and a scale");
        if (!InterestAttribution.Vocabulary(stated).Contains("espresso", StringComparer.Ordinal))
            problems.Add("a request that names 'espresso' lost it from the attribution vocabulary — the fix is eating real words.");

        // ── The gate, on a row RICHER than the one the real space produced. ──
        static InterestCoverage Row(bool vocabularyEmpty, int candidates, double bestScore)
        {
            var coverage = new InterestCoverage { InterestId = "I-1", AttributionVocabularyEmpty = vocabularyEmpty };
            coverage.QueriesRun.Add("a query that ran");
            for (int i = 0; i < candidates; i++) coverage.CandidateProductIds.Add($"GLX-90{i:00}");
            coverage.BestScore = bestScore;
            return coverage;
        }

        // 5 candidates and a perfect score — strictly more than the 2 the lower real-space floor
        // let through — and it must STILL be uncovered and starved.
        var namesNothing = Row(vocabularyEmpty: true, candidates: 5, bestScore: 1.0);
        if (CatalogueDiscoverySearch.ClassifyCoverage(namesNothing) != CoverageStatus.Uncovered)
        {
            problems.Add($"an interest that names NOTHING was classified "
                       + $"{CatalogueDiscoverySearch.ClassifyCoverage(namesNothing)} on 5 candidates at score 1.000 — "
                       + "the gate is still reading the retriever's ranking rather than the interest.");
        }

        if (!namesNothing.IsStarved)
            problems.Add("an interest that names NOTHING does not report IsStarved.");

        // ── THE POSITIVE DIRECTION, FROM A REAL INGEST (plan item 8.21, precondition 1). ──
        //
        // ⚠ It used to be `Row(vocabularyEmpty: false, candidates: 5, bestScore: 1.0)` — a hand-made
        //   InterestCoverage with CandidateProductIds populated and AttributableProductIds EMPTY,
        //   which NO real ingest produces: CatalogueDiscoverySearch fills both in the same loop.
        //   MEASURED (MEASUREMENT_STATUS §31.2): under the attribution gate that plan item 8.21
        //   decides to ship, that hand-made object is refused and this row reports "the gate refuses
        //   everything", which is false. That is the control-fixture hazard the 8.14 arc named, in
        //   its mirror image — there the fixture was KINDER than reality and the control was blind;
        //   here it is POORER than reality and the control cries wolf. Either way, a control whose
        //   specimen is hand-made is not testing the path it claims to.
        //
        //   The specimen is now the first coverage row the shipped loop actually produces for an
        //   interest that names something and got attributable candidates back. The NEGATIVE
        //   direction above stays hand-made on purpose: 5 candidates at score 1.000 is RICHER than
        //   anything this corpus produces, so it is the harder test in the direction that matters.
        DiscoveryState? realState = null;
        Interest? realInterest = null;
        InterestCoverage? realRow = null;

        foreach (string personaId in Personas.AllPersonaIds)
        {
            var loopOptions = new Galaxus.RecommendationAgent.Workflows.DiscoveryLoopOptions(
                Offline: true,
                SessionRequest: GalaxusEvalPrompt.UtteranceFrom(Personas.CanonicalPromptFor(personaId)),
                Retriever: retriever,
                Progress: null,
                Nodes: null);

            var loopRun = await GalaxusDiscoveryLoop.RunAsync(personaId, loopOptions, ct).ConfigureAwait(false);

            foreach (var candidateInterest in loopRun.State.Interests)
            {
                var row = loopRun.State.CoverageFor(candidateInterest.Id);
                if (row.QueriesRun.Count == 0) continue;
                if (row.AttributionVocabularyEmpty) continue;
                if (row.AttributableProductIds.Count == 0) continue;
                if (row.CandidateProductIds.Count < DiscoveryState.MinCandidatesForCoverage) continue;

                realState = loopRun.State;
                realInterest = candidateInterest;
                realRow = row;
                break;
            }

            if (realRow is not null) break;
        }

        string specimen = "NONE";
        if (realRow is null || realInterest is null || realState is null)
        {
            problems.Add("no coverage row anywhere in the authored cohort names something AND came back with an "
                       + "attributable candidate — this row's positive direction has no real specimen, so it cannot "
                       + "distinguish a working gate from one that refuses everything.");
        }
        else
        {
            specimen = $"{realState.CustomerId}/{realInterest.Id} ({realRow.CandidateProductIds.Count} candidate(s), "
                     + $"{realRow.AttributableProductIds.Count} attributable, best {realRow.BestScore:0.0000})";

            if (CatalogueDiscoverySearch.ClassifyCoverage(realRow) != CoverageStatus.Covered)
            {
                problems.Add("a REAL ingest row that names something and has "
                           + $"{realRow.AttributableProductIds.Count} attributable candidate(s) was not Covered "
                           + $"({specimen}) — the gate refuses everything.");
            }

            if (realRow.IsStarved)
                problems.Add($"a REAL ingest row that names something reports IsStarved ({specimen}).");

            // …and through the path the reviewer's veto actually takes, not a convenience property.
            if (CoverageVerdictProjection.Starved(realState)
                    .Any(i => string.Equals(i.Id, realInterest.Id, StringComparison.Ordinal)))
            {
                problems.Add("CoverageVerdictProjection.Starved vetoed a REAL ingest row that names something "
                           + $"({specimen}) — the veto refuses everything and the negative row above proves nothing.");
            }
        }

        // ── No materially different query exists for it, so the loop must not go round again. ──
        var state = new DiscoveryState { CustomerId = Personas.LucaUserId, Market = "CH", Language = "fr", SessionRequest = GalaxusDemoPrompts.LucaThinSignal };
        state.Interests.Add(contentless);
        var live = state.CoverageFor(contentless.Id);
        live.QueriesRun.Add(GalaxusDemoPrompts.LucaThinSignal);
        live.CandidateProductIds.Add("GLX-3004");
        live.CandidateProductIds.Add("GLX-3005");
        live.BestScore = 1.0;
        live.AttributionVocabularyEmpty = true;

        // ⚠ THROUGH THE PATH THE WORKFLOW ACTUALLY TAKES, not through a convenience property.
        //
        // `InterestCoverage.IsStarved` above is read by NOTHING in the shipped workflow: the
        // reviewer's veto goes through CoverageVerdictProjection.Starved, which asks the question
        // itself, and DiscoveryPreGate.TryRejectCheaply routes on that. PROVEN by re-introducing
        // the defect in place: with the AttributionVocabularyEmpty clause deleted from Starved and
        // everything else fixed, this control still went GREEN and `-- 3` exited 0. A control that
        // asserts a property the production path does not consult certifies the property, not the
        // behaviour.
        var starved = CoverageVerdictProjection.Starved(state);
        if (!starved.Any(i => string.Equals(i.Id, contentless.Id, StringComparison.Ordinal)))
        {
            problems.Add("CoverageVerdictProjection.Starved — the path the reviewer's veto and the pre-model gate "
                       + $"actually take — did NOT list an interest that names nothing, on {live.CandidateProductIds.Count} "
                       + $"candidate(s) at best score {live.BestScore:0.000}. The gate is still reading the ranking there.");
        }

        // ── And the line the pre-gate PRINTS must name the reason it actually fired. ──
        //
        // The pre-gate published one sentence per starved interest and it was a constant: "has no
        // candidate above the score floor (0.0120)". There are two ways to be starved now, and on
        // THIS one candidates cleared the floor comfortably — so the printed line sent the reader
        // to a threshold, which is the fix that must not be made. Same shape as Eval 09's clause-5
        // remedy blaming a timeout that had not fired.
        var pregateLines = new List<string>();
        var sink = new CapturingProgressSink(pregateLines);
        CoverageReviewGate.TryRejectCheaply(state, Catalogue.Default, sink);

        string pregate = string.Join(" | ", pregateLines);
        if (!pregate.Contains("NAMES NOTHING", StringComparison.Ordinal))
            problems.Add($"the pre-gate did not say WHY this interest is starved — it printed: \"{Shorten(pregate, 70)}\".");
        if (pregate.Contains("no candidate above the score floor", StringComparison.Ordinal))
            problems.Add("the pre-gate blames the SCORE FLOOR for an interest whose candidates cleared it — the printed remedy points at the one number that must not be moved.");

        var gap = CoverageGapWriter.Write(state, Catalogue.Default, contentless);
        if (gap is not null)
        {
            problems.Add($"a gap with a 'next query' was written for an interest that names nothing (\"{Shorten(gap.NextQuery ?? "", 40)}\") — "
                       + "the loop would go round again to re-rank the same arbitrary list instead of reporting GAPS_UNRESOLVABLE.");
        }

        if (live.LastGapReason is not { Length: > 0 })
            problems.Add("nothing was recorded on the coverage row to say WHY no query was written — the refusal is silent.");

        // ── The thresholds have NOT moved. ──
        //
        // ⚠ READ THROUGH A LOCAL, deliberately. `DiscoveryState.MinCandidateScore` is a `const`, so
        //   comparing it inline made the whole clause compile-time unreachable (CS0162,
        //   MEASUREMENT_STATUS §24.7 item 3). The assertion still fires the moment the constant
        //   changes — that is the point of it — but a warning that says "this code cannot run" on a
        //   control is the last sentence anyone should have to argue with.
        double minCandidateScore = DiscoveryState.MinCandidateScore;
        if (minCandidateScore != 0.012)
            problems.Add($"MinCandidateScore is {minCandidateScore}, not 0.012 — a threshold moved to paper over the gate.");
        if (Math.Abs(HybridRetriever.DefaultDenseScoreFloor - CalibratedThresholds.PreCalibration.DenseScoreFloor) > 1e-9)
            problems.Add("the pre-calibration dense floor no longer equals the value the transport rule is anchored to.");

        return new ControlRowSnapshot(
            "ContentlessRequestIsNotCovered",
            "an interest that NAMES NOTHING must never be covered, however many candidates a query returned for it "
          + "and however well they scored — a query with no content still returns a ranked list, because something "
          + "is always top of one. That is what let Luca's \"Hi — what do you recommend for me?\" turn from "
          + "GAPS_UNRESOLVABLE into five recommendations when the re-derived real-vectors dense floor let two "
          + "arbitrary products through. The gate must also still COVER an interest that does name something, no "
          + "query may be written for one that does not, and MinCandidateScore and the dense floor must both still "
          + "read their derived values: fixing this by moving a calibrated threshold is the failure, not the fix.",
            problems.Count == 0
                ? $"the contentless request names NOTHING (vocabulary empty, and our own '"
                + $"{DiscoveryInterestMapping.SessionRequestLabelPrefix.Trim()}' prefix is excluded from it) "
                + "· 5 candidates at score 1.000 → UNCOVERED and STARVED · the POSITIVE direction is a REAL "
                + $"ingest specimen, {specimen} → COVERED and not vetoed · no next query is written, with "
                + "the reason recorded · MinCandidateScore "
                + $"still {DiscoveryState.MinCandidateScore:0.000}, dense floor still "
                + $"{HybridRetriever.DefaultDenseScoreFloor:0.000}. ⚠ SPACE-INDEPENDENT: this proves the MECHANISM, "
                + "not that a --real-vectors run abstains again. That needs a paid run."
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 21 — the gate was fixed and the TRAY was not (plan item 8.18). ═════════════
    //
    /// <summary>
    /// An interest that NAMES NOTHING must not put a single product in front of the customer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this row exists for is the unclosed half of <c>aae2024d</c>.</b> That commit
    /// made the coverage gate ask the right question, and the loop obeyed: the interest goes
    /// UNCOVERED and STARVED, the reviewer reports <c>GAPS_UNRESOLVABLE</c> and no second query is
    /// written. The ANSWER did not obey. The candidates retrieved in round 1 — before the gate ran
    /// — are already in <c>DiscoveryState.Candidates</c>, and the Ranker reads the candidate set,
    /// never the coverage ledger. MEASURED on <c>--real-vectors</c> at <c>41cd09a2</c>: Luca
    /// Ferrari's five products became <b>two</b>, and a customer who named nothing was still shown
    /// a tray.
    /// </para>
    /// <para>
    /// <b>It drives the production path, not a convenience property.</b> The previous row in this
    /// panel shipped asserting <c>InterestCoverage.IsStarved</c>, which nothing in the workflow
    /// reads (correction ⑬ item 3). So this one runs the two real SKUs the real-vector run
    /// actually surfaced — <c>GLX-7001</c> and <c>GLX-7006</c> — through
    /// <see cref="DiscoveryPostChecks.Apply"/>, the one seam BOTH rankers pass through, and then
    /// through <see cref="DiscoveryPresentation.Render"/>, which is what writes
    /// <c>FinalAnswer</c> — the field Eval 07's GATE C measures.
    /// </para>
    /// <para>
    /// <b>Both directions, and the threshold.</b> The same two SKUs credited to an interest that
    /// DOES name something must survive and must produce a non-empty answer, or this is a filter
    /// that refuses everything and would look identical on the row above. And the drop reason must
    /// NOT name a score floor: there are two ways to present nothing now, and pointing the reader
    /// at <c>MinCandidateScore</c> for the one where the candidates cleared it comfortably is the
    /// fix that must not be made — the same shape as the pre-gate line corrected in
    /// <c>41cd09a2</c>.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckUnnameableInterestPresentsNothing()
    {
        var problems = new List<string>();
        var catalogue = Catalogue.Default;

        // The two products the --real-vectors run actually put in front of Luca. Real SKUs, so a
        // catalogue change that removes them fails this row loudly instead of silently emptying it.
        string[] skus = ["GLX-7001", "GLX-7006"];

        static Interest SessionRequest(string utterance, string id) =>
            DiscoveryInterestMapping.ToInterest(
                new InterestSignal(
                    Label: utterance,
                    Strength: 1.0,
                    EvidenceKind: InterestEvidenceKinds.StatedInSession,
                    EvidencePurchaseIds: []),
                id);

        // One state builder used for both directions, so the ONLY difference between the two runs
        // below is what the customer said. Anything else differing would confound the comparison.
        DiscoveryState Build(Interest interest)
        {
            var state = new DiscoveryState
            {
                CustomerId = Personas.LucaUserId,
                Market = "CH",
                Language = "fr",
                SessionRequest = interest.Label,
            };
            state.Interests.Add(interest);

            var coverage = state.CoverageFor(interest.Id);
            coverage.QueriesRun.Add(state.SessionRequest);
            coverage.AttributionVocabularyEmpty = InterestAttribution.NamesNothing(interest);

            foreach (string sku in skus)
            {
                if (!catalogue.TryGet(sku, out var product) || product is null)
                {
                    problems.Add($"{sku} is not in the catalogue — this row is asserting nothing.");
                    continue;
                }

                coverage.CandidateProductIds.Add(sku);
                state.Candidates.Add(DiscoveryProjection.ToCandidate(
                    catalogue, product, score: 0.500, interest.Id, state.SessionRequest));
            }

            return state;
        }

        var contentless = Build(SessionRequest(GalaxusDemoPrompts.LucaThinSignal, "I-1"));
        var naming = Build(SessionRequest("I need a 58 mm espresso tamper and a scale", "I-1"));

        // ── The Ranker SELECTS them. That is not the defect and must stay true, or the filter
        //    below would be screening an empty list and would prove nothing. ──
        contentless.Ranked.AddRange(DeterministicRanker.Select(contentless, catalogue));
        naming.Ranked.AddRange(DeterministicRanker.Select(naming, catalogue));

        if (contentless.Ranked.Count == 0)
            problems.Add("the deterministic Ranker selected NOTHING for the contentless interest before the filter ran — "
                       + "this row cannot show the filter fired, because there was nothing to filter.");

        var lines = new List<string>();
        var sink = new CapturingProgressSink(lines);

        // ⚠ THROUGH DiscoveryPostChecks.Apply — the seam the deterministic Ranker AND the model
        //   Ranker both end in. A filter placed inside either one leaves the other open.
        var contentlessTrace = DiscoveryPostChecks.Apply(contentless, catalogue, sink);
        var namingTrace = DiscoveryPostChecks.Apply(naming, catalogue, sink);

        if (contentless.Ranked.Count != 0)
        {
            problems.Add($"{contentless.Ranked.Count} product(s) survived the post-checks for an interest that names "
                       + $"NOTHING ({string.Join(", ", contentless.Ranked.Select(r => r.ProductId))}) — the coverage gate "
                       + "refuses the interest and the tray is still built out of what the contentless query returned.");
        }

        if (naming.Ranked.Count != skus.Length)
        {
            problems.Add($"only {naming.Ranked.Count} of {skus.Length} product(s) survived for an interest that DOES name "
                       + "something — the filter refuses everything, which would look identical on the row above.");
        }

        // ── The drop is VISIBLE and names the right cause. ──
        string reasons = string.Join(" | ", contentless.DroppedSkus.Select(d => d.Reason));
        if (contentless.DroppedSkus.Count != skus.Length)
            problems.Add($"{contentless.DroppedSkus.Count} drop(s) recorded for {skus.Length} removed product(s) — a drop nobody can see is not a guardrail.");
        if (!reasons.Contains("names nothing", StringComparison.Ordinal))
            problems.Add($"the drop reason does not say the interest names nothing — it said: \"{Shorten(reasons, 70)}\".");
        if (reasons.Contains("score floor", StringComparison.Ordinal) || reasons.Contains("MinCandidateScore", StringComparison.Ordinal))
            problems.Add("the drop reason blames a SCORE FLOOR for candidates that cleared it — the printed remedy points at the one number that must not be moved.");
        if (naming.DroppedSkus.Count != 0)
            problems.Add($"{naming.DroppedSkus.Count} product(s) were dropped for an interest that names something.");

        // The Ranker publishes these lines verbatim as its trace, so a reader sees the check fire.
        string trace = string.Join(" | ", contentlessTrace);
        if (!trace.Contains("unnameable interest", StringComparison.Ordinal))
            problems.Add($"the Ranker's trace has no line for this check — it printed: \"{Shorten(trace, 70)}\".");
        if (!trace.Contains("(2 dropped)", StringComparison.Ordinal))
            problems.Add($"the trace line does not report the two drops — it printed: \"{Shorten(trace, 90)}\".");

        // …and on a map where nothing is unnameable the arm is INAPPLICABLE, printed as such. A
        // check that cannot fire on a customer must not print a tick beside their name.
        string namingTraceText = string.Join(" | ", namingTrace);
        if (!namingTraceText.Contains("ARM INAPPLICABLE", StringComparison.Ordinal))
            problems.Add("with no unnameable interest on the map the arm printed a result rather than ARM INAPPLICABLE — "
                       + "a check with a chance floor of 1.0 must say so, not show a tick.");

        // ── And what the CUSTOMER ends up with. FinalAnswer is the field Eval 07's GATE C reads. ──
        DiscoveryPresentation.Render(contentless, catalogue, sink, modelProse: null, print: false);
        DiscoveryPresentation.Render(naming, catalogue, sink, modelProse: null, print: false);

        if (contentless.Presented.Count != 0)
            problems.Add($"{contentless.Presented.Count} item(s) reached the customer for an interest that names nothing.");
        if (contentless.FinalAnswer.Length != 0)
        {
            problems.Add($"the composed answer is {contentless.FinalAnswer.Length} character(s) long where it must be zero — "
                       + $"\"{Shorten(contentless.FinalAnswer, 60)}\". An abstention that still writes several hundred "
                       + "characters is not an abstention, and Eval 07's GATE C measures exactly this length.");
        }

        if (naming.Presented.Count == 0 || naming.FinalAnswer.Length == 0)
        {
            problems.Add($"an interest that DOES name something presented {naming.Presented.Count} item(s) and composed "
                       + $"{naming.FinalAnswer.Length} character(s) — the answer path is broken for everyone, not just for the refused case.");
        }

        // ── The thresholds have NOT moved. Same assertion as the row above, for the same reason:
        //    this defect has an available "fix" that consists of raising a calibrated number. ──
        double minCandidateScore = DiscoveryState.MinCandidateScore;   // via a local: see control 20's note on CS0162
        if (minCandidateScore != 0.012)
            problems.Add($"MinCandidateScore is {minCandidateScore}, not 0.012 — a threshold moved to paper over the tray.");

        return new ControlRowSnapshot(
            "UnnameableInterestPresentsNothing",
            "an interest that NAMES NOTHING must reach the customer with an EMPTY tray and a zero-character answer, not "
          + "with a shorter one. aae2024d fixed the coverage gate — the loop refuses the interest and stops — and left the "
          + "presentation path alone, so the candidates retrieved before the gate ran still flowed through the Ranker to "
          + "the Presenter: on --real-vectors Luca Ferrari went from five products to two, and two is not zero. The filter "
          + "must sit where BOTH rankers pass, must still present the same products for an interest that does name "
          + "something, must record a visible drop, and must not blame the score floor for candidates that cleared it.",
            problems.Count == 0
                ? $"{skus.Length} real candidate(s) ({string.Join(", ", skus)}) selected by the Ranker for a contentless "
                + $"request → {contentless.Ranked.Count} survive the post-checks, {contentless.DroppedSkus.Count} drop(s) "
                + $"recorded naming the interest rather than a threshold, {contentless.Presented.Count} presented, "
                + $"FinalAnswer {contentless.FinalAnswer.Length} char(s) · the SAME two candidates on an interest that "
                + $"names something → {naming.Ranked.Count} survive, {naming.Presented.Count} presented, "
                + $"{naming.FinalAnswer.Length} char(s) · MinCandidateScore still {DiscoveryState.MinCandidateScore:0.000}"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // == 2.11 -- is MinCandidateScore a THRESHOLD at all? ==================================
    //
    /// <summary>
    /// Measures how often the <c>BestScore &lt; MinCandidateScore</c> clause is what decides a
    /// coverage verdict, across every authored customer, on the shipped deterministic path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Plan item 2.11 asks for this constant to be calibrated on the same held-out split as the
    /// other four cuts. Before deriving a number, measure whether the number decides anything.</b>
    /// That ordering is the point: a cut re-derived on a population it never actually cuts is a
    /// figure with a provenance and no consequence, and it would be reported as if it had one.
    /// </para>
    /// <para>
    /// ⚠ <b>And the constant is doing TWO structurally different jobs under one name.</b> In
    /// <c>CatalogueDiscoverySearch.ClassifyCoverage</c> and <c>CoverageVerdictProjection.Starved</c>
    /// it is a <i>cut</i> on a distribution. In
    /// <c>DeterministicRanker.Confidence</c> it is the <i>half-saturation constant</i> of the
    /// squashing transform <c>s / (s + k)</c> — the score at which the retrieval term equals 0.5 —
    /// which is not a threshold at all. Re-deriving it as a cut therefore moves every workflow-arm
    /// confidence, and confidence is what <c>ConfidenceBands</c> routes on. Those bands were
    /// themselves derived on this same held-out split, so calibrating this one constant would
    /// silently move the quantity another calibrated cut is applied to, and the derivation would
    /// never have looked at it. That coupling is the finding; it is reported here rather than
    /// resolved, because splitting one constant into two is a behaviour change and this row is not.
    /// </para>
    /// <para>
    /// <b>Advisory, never gating.</b> It reports a property of the corpus, not a wiring fault. The
    /// two rows that pin the constant's VALUE at 0.012 stay gating and are untouched.
    /// </para>
    /// </remarks>
    private static async Task<ControlRowSnapshot> CheckMinCandidateScoreDecidesNothingAsync(
        IProductRetriever retriever,
        CancellationToken ct)
    {
        var scores = new List<double>();
        int rowsWithCandidates = 0;
        int decidedByTheCut = 0;
        int decidedByNamesNothing = 0;
        int emptyCandidateSets = 0;
        var deciders = new List<string>();

        foreach (string personaId in Personas.AllPersonaIds)
        {
            var options = new Galaxus.RecommendationAgent.Workflows.DiscoveryLoopOptions(
                Offline: true,
                SessionRequest: GalaxusEvalPrompt.UtteranceFrom(Personas.CanonicalPromptFor(personaId)),
                Retriever: retriever,
                Progress: null,
                Nodes: null);

            var run = await GalaxusDiscoveryLoop.RunAsync(personaId, options, ct).ConfigureAwait(false);

            foreach (var interest in run.State.Interests)
            {
                var coverage = run.State.CoverageFor(interest.Id);
                if (coverage.QueriesRun.Count == 0) continue;      // unexplored is not a verdict

                if (coverage.AttributionVocabularyEmpty) { decidedByNamesNothing++; continue; }
                if (coverage.CandidateProductIds.Count == 0) { emptyCandidateSets++; continue; }

                rowsWithCandidates++;
                scores.Add(coverage.BestScore);

                // The ONLY rows this constant decides: something came back, it names something,
                // and the fused score is what refuses it.
                if (coverage.BestScore < DiscoveryState.MinCandidateScore)
                {
                    decidedByTheCut++;
                    deciders.Add($"{personaId}/{interest.Id} at {coverage.BestScore:0.0000}");
                }
            }
        }

        scores.Sort();
        double min = scores.Count > 0 ? scores[0] : double.NaN;
        double median = scores.Count > 0 ? scores[scores.Count / 2] : double.NaN;

        // Headroom, stated as a ratio rather than a difference, because the quantity is an RRF
        // fusion score with no upper bound of interest and a difference would not travel.
        string headroom = scores.Count > 0 && DiscoveryState.MinCandidateScore > 0
            ? $"{min / DiscoveryState.MinCandidateScore:0.0}x"
            : "n/a";

        string observed =
            $"{Personas.AllPersonaIds.Count} customer(s) · {rowsWithCandidates} coverage row(s) with candidates that name "
          + $"something · the cut decided {decidedByTheCut} of them"
          + (deciders.Count == 0 ? string.Empty : ": " + string.Join(", ", deciders.Take(4)))
          + $" · so the fit population's ADMIT RATE at the anchor is {(rowsWithCandidates == 0 ? double.NaN : 1.0 - ((double)decidedByTheCut / rowsWithCandidates)):0.000}"
          + $" · lowest observed BestScore {min:0.0000} ({headroom} the cut), median {median:0.0000}"
          + $" · for comparison the OTHER two clauses decided {decidedByNamesNothing} (names nothing) and "
          + $"{emptyCandidateSets} (no candidate at all)"
          + $" · MinCandidateScore = {DiscoveryState.MinCandidateScore:0.000}; the half-saturation term of "
          + $"DeterministicRanker.Confidence's s/(s+k) is now the SEPARATE constant "
          + $"RetrievalConfidenceHalfSaturation = {DiscoveryState.RetrievalConfidenceHalfSaturation:0.000} "
          + "(split 2026-09-06 at equal value, so nothing moved), and the row above no longer has to be read "
          + "as a statement about ConfidenceBands as well";

        return new ControlRowSnapshot(
            "MinCandidateScoreDecidesNothing",
            "plan item 2.11 wants this cut calibrated. Before a number is derived, measure what the number "
          + "decides: how many coverage rows on the whole authored cohort are refused by BestScore < "
          + "MinCandidateScore ALONE — candidates came back, the interest names something, and only the fused "
          + "score says no. Reported with the lowest score the corpus actually produces, so the headroom is "
          + "visible rather than assumed, and with the count the other two clauses decide, so the cut is not "
          + "credited with their work",
            observed,
            Tripped: true,
            Gating: false);
    }

    // == The prose a reader meets FIRST must describe the case it is attached to, IN THIS SPACE. ==
    //
    /// <summary>
    /// Every Eval 07 topology case's own <c>Why</c> text must carry an <c>OBSERVED PER SPACE</c>
    /// clause for every embedding space, and the clause for the space this process RESOLVED must
    /// match the loop-back count, the round count and the frozen stop reason the run produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this row was built for.</b> Marco's case text said <i>"Two loop-backs, three
    /// rounds … gaps-unresolvable, not the round cap"</i> and Mirjam's said <i>"LOOPS ONCE and exits
    /// DEGRADED on no-progress"</i>. Measured in the default concept space: Marco is 1 loop-back / 2
    /// rounds / <c>no-progress</c>, Mirjam is 2 / 3 / <c>gaps-unresolvable</c>. The two descriptions
    /// were each other's. <b>No pin and no verdict was affected</b> — the pins are
    /// <c>ExpectsLoopBack</c> and <c>PresentsAnswerText</c>, identical for both — which is exactly why
    /// nothing caught it for the eval's whole life: the wrong sentence was in the one field nothing
    /// reads mechanically, in the eval whose GATE B is red and whose prose is therefore the first
    /// thing a diagnosing reader believes.
    /// </para>
    /// <para>
    /// ⚠ <b>AND THE FIX WAS RIGHT IN ONE SPACE ONLY — measured 2026-09-06, Wave 4 verification
    /// run.</b> The first revision of this row compared the prose against whatever run the resolved
    /// space produced, and the prose it was written against was the concept space's. The first time
    /// anyone ran <c>-- 3 --real-vectors</c> after that fix, this row went <b>red with 2 faults</b>
    /// and took the command's exit code from a published <b>0</b> to <b>1</b>. The deterministic loop
    /// is <b>not space-invariant</b>: Marco and Mirjam SWAP round counts between the spaces, Mirjam's
    /// exit disposition flips DEGRADED → APPROVED, and <c>no-progress</c> is unreachable on the real
    /// path. So a single sentence describing "the run" is wrong in whichever space it was not written
    /// for, and correcting the sentence again would only have moved the defect back. <b>The case now
    /// states a clause PER SPACE and this row checks the resolved one</b> — resolved, never
    /// requested, because <c>--real-vectors</c> falls back to the concept space without credentials
    /// and a row that read the request would then assert the wrong space's claim on a laptop with no
    /// key.
    /// </para>
    /// <para>
    /// ⚠ <b>A third case was wrong in BOTH spaces, and the first revision could not see it.</b>
    /// Renzo's text asserted in the present tense that <i>"the reviewer sends him back for more
    /// discovery twice and then approves"</i>; he exits at round 1 in both spaces, which is the very
    /// failure GATE B reports two lines below that sentence. The row missed it because it examined
    /// only cases whose prose happened to name a frozen stop reason, and Renzo's named none — the
    /// scope limit declared in <c>MEASUREMENT_STATUS</c> §41.4, realised within one wave. <b>The row
    /// now requires a clause on EVERY case</b>, so the set it examines is the corpus rather than
    /// whatever subset chose to be examinable.
    /// </para>
    /// <para>
    /// <b>Both joins are derived, not hand-written.</b> (a) The eval lane's stop-reason strings and
    /// the workflow's <c>DiscoveryStopReason</c> enum are deliberately separate types
    /// (<c>RealDiscoveryLoopArm.MapStopReason</c>'s remarks say why), and re-typing that table here
    /// would let this row certify a copy of it — so the enum member name is kebab-cased mechanically
    /// and the row FAILS unless the resulting set is exactly <see cref="DiscoveryStopReasons.All"/>.
    /// (b) The set of spaces a clause must cover is every non-<c>Auto</c> member of
    /// <c>EmbeddingSpaceChoice</c>, read off the enum. Adding a third space therefore turns this row
    /// red until every case describes it, which is the correct outcome: the alternative is five
    /// descriptions silently claiming to cover a space nobody measured.
    /// </para>
    /// <para>
    /// ⚠ <b>It asserts its own input, twice over.</b> A prose scan that finds no clause is
    /// indistinguishable from a prose scan that read no cases, so the row fails unless it parsed a
    /// clause for <i>every</i> case and names each one it checked. And a frozen stop reason appearing
    /// anywhere OUTSIDE a clause is a fault in itself — that free-floating word is the space-blind
    /// sentence this whole mechanism exists to retire, and leaving it unflagged would let the old
    /// defect return in prose the row no longer reads.
    /// </para>
    /// <para>
    /// <b>What it does NOT do.</b> The clause is a pin on the DESCRIPTION, never on the verdict. It
    /// cannot make a gate pass, it is not <c>ExpectsLoopBack</c>, and Renzo's clause deliberately
    /// records a run that contradicts his pin. Gating on the description is safe precisely because
    /// the description scores nothing.
    /// </para>
    /// </remarks>
    /// <param name="retriever">The bound retriever; each case runs the real deterministic loop.</param>
    /// <param name="ct">Cancellation.</param>
    private static async Task<ControlRowSnapshot> CheckTopologyCaseProseMatchesTheRunAsync(
        IProductRetriever retriever,
        CancellationToken ct)
    {
        var problems = new List<string>();

        // ── The join, derived from the enum's own member names and then CHECKED. ──
        var kebab = new Dictionary<DiscoveryStopReason, string>();
        foreach (DiscoveryStopReason value in Enum.GetValues<DiscoveryStopReason>())
        {
            string name = value.ToString();
            var sb = new System.Text.StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i])) sb.Append('-');
                sb.Append(char.ToLowerInvariant(name[i]));
            }

            kebab[value] = sb.ToString();
        }

        var derived = new HashSet<string>(kebab.Values, StringComparer.Ordinal);
        foreach (string canonical in DiscoveryStopReasons.All)
        {
            if (!derived.Contains(canonical))
            {
                problems.Add($"'{canonical}' is not produced by kebab-casing any DiscoveryStopReason member — the "
                           + "two lanes' names have diverged, so this row cannot join them and must not pretend to.");
            }
        }

        // ── The SECOND derived join: which spaces a clause has to cover. Read off the enum, so a
        //    third space turns this row red until every case describes it, rather than letting five
        //    descriptions silently claim to cover a space nobody measured. `Auto` is not a space; it
        //    is the absence of a request, and it always resolves to one of the others.
        var spaces = Enum.GetValues<EmbeddingSpaceChoice>()
            .Where(s => s != EmbeddingSpaceChoice.Auto)
            .ToList();

        // ⚠ RESOLVED, never REQUESTED. `--real-vectors` falls back to the concept space when there
        //   are no credentials, and a row that read the request would assert the wrong space's claim
        //   on every machine without a key — passing or failing for a reason that has nothing to do
        //   with the prose.
        var resolution = EmbeddingSpace.Current;
        if (resolution is null)
        {
            problems.Add("no embedding space has resolved in this process, so there is no way to know which "
                       + "per-space clause applies — refused rather than checked against a guess.");
        }

        EmbeddingSpaceChoice resolved = resolution?.Chosen ?? EmbeddingSpaceChoice.Auto;

        int casesWithAClause = 0;
        var checkedCases = new List<string>();

        foreach (var topologyCase in Eval07_WorkflowTopology.Cases)
        {
            // ── parse every per-space clause out of the prose ──────────────────────────────────
            var claims = new Dictionary<EmbeddingSpaceChoice, (int LoopBacks, int Rounds, string Reason)>();
            var clauseSpans = new List<(int Start, int Length)>();

            foreach (EmbeddingSpaceChoice space in spaces)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    topologyCase.Why,
                    $@"\b{System.Text.RegularExpressions.Regex.Escape(space.ToString())}\s+(\d+)\s+loop-backs?\s*/\s*(\d+)\s+rounds?\s*/\s*([a-z][a-z-]*)",
                    System.Text.RegularExpressions.RegexOptions.None,
                    TimeSpan.FromSeconds(2));

                if (!match.Success)
                {
                    problems.Add($"{topologyCase.PersonaId}'s Why text carries no OBSERVED PER SPACE clause for "
                               + $"{space} — a description that covers one space silently claims both, and this "
                               + "corpus is measurably not space-invariant.");
                    continue;
                }

                claims[space] = (
                    int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                    match.Groups[3].Value);
                clauseSpans.Add((match.Index, match.Length));
            }

            // ⚠ A frozen reason OUTSIDE a clause is the space-blind sentence this mechanism retires.
            //   Blank the clauses out and look at what is left, so the old defect cannot come back
            //   in prose the row no longer reads.
            var outsideClauses = new System.Text.StringBuilder(topologyCase.Why);
            foreach (var (start, length) in clauseSpans) outsideClauses.Remove(start, length).Insert(start, new string(' ', length));
            string remainder = outsideClauses.ToString();

            foreach (string stray in DiscoveryStopReasons.All.Where(r => remainder.Contains(r, StringComparison.Ordinal)))
            {
                problems.Add($"{topologyCase.PersonaId}'s Why text names {stray} OUTSIDE an OBSERVED PER SPACE "
                           + "clause — an unqualified stop reason is a claim about both spaces, and it is false in "
                           + "at least one of them for two of these five customers.");
            }

            if (claims.Count != spaces.Count) continue;
            casesWithAClause++;

            var options = new Galaxus.RecommendationAgent.Workflows.DiscoveryLoopOptions(
                Offline: true,
                SessionRequest: GalaxusEvalPrompt.UtteranceFrom(Personas.CanonicalPromptFor(topologyCase.PersonaId)),
                Retriever: retriever,
                Progress: null,
                Nodes: null);

            var run = await GalaxusDiscoveryLoop.RunAsync(topologyCase.PersonaId, options, ct).ConfigureAwait(false);
            string observedReason = kebab[run.State.StopReason];
            int observedLoopBacks = run.RoutesTaken.Count(
                id => string.Equals(id, DiscoveryRouteIds.ReviewToMoreDiscovery, StringComparison.Ordinal));
            int observedRounds = run.State.DiscoveryRound;

            if (!claims.TryGetValue(resolved, out var claim))
            {
                problems.Add($"{topologyCase.PersonaId} has no clause for the RESOLVED space {resolved} — "
                           + "nothing to check this run against.");
                continue;
            }

            checkedCases.Add($"{topologyCase.PersonaId} claims {claim.LoopBacks}/{claim.Rounds}/{claim.Reason} in "
                           + $"{resolved} and runs {observedLoopBacks}/{observedRounds}/{observedReason}");

            // ⚠ ALL THREE, not the reason alone. The Wave-4 defect was a swap, and two customers
            //   swapped BOTH counts between spaces while one of them kept a matching reason — a row
            //   that checked the reason alone would have certified half of it.
            if (claim.Reason != observedReason)
            {
                problems.Add($"{topologyCase.PersonaId}'s {resolved} clause names {claim.Reason} and the run ends "
                           + $"in {observedReason} — the sentence a reader meets first describes a different case.");
            }

            if (claim.LoopBacks != observedLoopBacks)
            {
                problems.Add($"{topologyCase.PersonaId}'s {resolved} clause claims {claim.LoopBacks} loop-back(s) "
                           + $"and the route trace carries {observedLoopBacks}.");
            }

            if (claim.Rounds != observedRounds)
            {
                problems.Add($"{topologyCase.PersonaId}'s {resolved} clause claims {claim.Rounds} round(s) and the "
                           + $"producer's counter says {observedRounds}.");
            }
        }

        if (casesWithAClause != Eval07_WorkflowTopology.Cases.Count)
        {
            problems.Add($"only {casesWithAClause} of {Eval07_WorkflowTopology.Cases.Count} case(s) carry a complete "
                       + "per-space clause — a scan that compared a subset is not a verdict, and the subset that "
                       + "opted out is exactly where the third defect was hiding.");
        }

        return new ControlRowSnapshot(
            "TopologyCaseProseMatchesTheRun",
            "an Eval 07 case's Why text is the first thing a reader diagnosing a GATE B failure believes, and "
          + "nothing read it mechanically. MEASURED: Marco's text described Mirjam's run and Mirjam's described "
          + "Marco's; and Renzo's asserted he 'is sent back for more discovery twice and then approves' when he "
          + "exits at round 1 in BOTH spaces — the very failure GATE B reports below the sentence. ⚠ The first "
          + "revision of this row was itself right in ONE space: the deterministic loop is NOT space-invariant, "
          + "Marco and Mirjam swap round counts, Mirjam's exit flips DEGRADED to APPROVED, and no-progress is "
          + "unreachable on the real path — so `-- 3 --real-vectors` went 0 to 1 the first time it ran. EVERY case "
          + "must therefore carry an OBSERVED PER SPACE clause for EVERY non-Auto EmbeddingSpaceChoice, the "
          + "RESOLVED space's clause must match the run's loop-backs, rounds AND stop reason, and a frozen reason "
          + "outside a clause is itself a fault. Both joins are derived — the reason set by kebab-casing "
          + "DiscoveryStopReason and REFUSED unless it equals DiscoveryStopReasons.All, the space set off the enum",
            problems.Count == 0
                ? $"resolved space {resolved} · {casesWithAClause} of {Eval07_WorkflowTopology.Cases.Count} case(s) "
                + $"carry a clause for all {spaces.Count} space(s) and every one matches the run · "
                + $"{string.Join(" · ", checkedCases)} · the derived join covers all "
                + $"{DiscoveryStopReasons.All.Count} frozen reasons"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // == Eval 07 GATE B + 8.21 precondition 3 -- can the NEGATIVE direction be re-established? ==
    //
    /// <summary>
    /// Over all fourteen authored customers: who does NOT traverse the loop-back edge, and whose
    /// non-loop would SURVIVE both changes that are currently blocked on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this row exists.</b> Two open items are blocked on the same corpus fact and neither
    /// had measured it. Eval 07's GATE B fails on <c>USR-RB-10</c>; the prescribed remedy (rank
    /// review-snippet proposals by the terms the vocabulary will actually admit) re-pins Renzo and
    /// then <b>flips Nadia</b>, the ⭐ negative-direction case — §28.2 refused it for that reason.
    /// Plan item 8.21's decision (gate coverage on attribution: YES) <b>also flips Nadia</b> —
    /// §31.3 precondition 3. So the corpus's only loop-back negative cell is fragile in two
    /// independent directions at once, and the question underneath both is one question:
    /// <i>is there a customer whose non-loop survives?</i>
    /// </para>
    /// <para>
    /// <b>What is measured, and why it is POLICY-INDEPENDENT.</b> The loop-back edge reads
    /// <c>OpenGaps.Count > 0</c>. On this corpus, measured in §28.1, the only thing that opens a
    /// gap for a non-abstaining customer is a mid-run proposal that <c>TryAcceptProposal</c>
    /// admitted, and admission needs at least one proposed term inside
    /// <see cref="QueryVocabulary"/>. So rather than simulate one remedy — which would certify a
    /// re-implementation of it and nothing else — this row measures the <b>pool the remedy would
    /// draw from</b>: how many of the run's observed review snippets carry at least one novel token
    /// the vocabulary would admit. <b>A customer whose admissible pool is zero cannot be made to
    /// loop by ANY re-ranking of that pool</b>, because there is nothing in it to promote. That is
    /// a statement about the corpus, not about a candidate fix.
    /// </para>
    /// <para>
    /// ⚠ <b>A POOL OF ZERO OUT OF ZERO IS NOT A MEASURED ZERO — corrected 2026-09-06 by review.</b>
    /// This row's first revision printed the pool as a bare count and reported exactly one survivor,
    /// <c>USR-LF-04</c>, whose pool is <b>0 of 0 snippets</b>: it is the ABSTENTION persona, its
    /// discovery round returns nothing, and there is no pool for the metric to be about. Every other
    /// customer's denominator is 13–27. So the one figure the conclusion rested on was the one place
    /// the instrument degenerates — applicability read off the RESULT instead of the INPUT, which is
    /// the shape this panel has a standing rule about. The denominator is now printed, a survivor
    /// with an empty denominator is labelled <c>SURVIVES VACUOUSLY</c> rather than <c>⭐ SURVIVES
    /// BOTH</c>, and the two are counted separately. <b>A vacuous survivor is not a replacement
    /// cell</b>: Nadia's ⭐ is worth something because the reviewer ran on a customer with a full
    /// tray and chose not to loop, and the row now prints each survivor's existing Eval 07 pins so
    /// that a survivor already pinned <c>PresentsAnswerText: false</c> cannot be read as one.
    /// </para>
    /// <para>
    /// <b>Novelty is measured against the MAPPER-origin map</b> — the known-token set the reviewer
    /// starts round 1 with — because that set only grows as reviewer-inferred interests are added,
    /// so it is the larger novel set and the larger pool. ⚠ <b>The direction label that used to sit
    /// here was inverted, and is corrected.</b> It called the larger pool "the unflattering choice";
    /// a larger pool makes FEWER customers durable, which makes <i>"no replacement cell exists"</i>
    /// — the conclusion §28.2 and §31.3 draw from this row to justify a deferral — <b>easier</b> to
    /// say, not harder. The choice is conservative about each per-customer durability claim and
    /// anti-conservative about the aggregate the row is actually used for. Rather than argue the
    /// adjective, the row now <b>computes both</b> and reports whether the durable set moves:
    /// measured 2026-09-06, the two pools are identical on all ten non-looping customers and differ
    /// only on looping ones, whose figure is an unused over-count. The methodological choice is
    /// therefore a measured no-op rather than an unexaminable preference.
    /// ⚠ For a customer that DID loop, the pool figure is an over-count (it includes snippets from
    /// rounds that only exist because it looped) and is labelled as such; for a customer that did
    /// NOT loop there was only ever one round, so the figure is exact — and those are precisely
    /// the customers this row is about.
    /// </para>
    /// <para>
    /// ⚠ <b>Advisory, and it must stay advisory.</b> It reports a property of the corpus. Gating on
    /// it would create an incentive to author a customer until the cell exists, which is the corpus
    /// supplying the bar for the change under test — the move §28.2 and §31.3 both refused.
    /// </para>
    /// </remarks>
    /// <param name="retriever">The bound retriever; the loop searches for real.</param>
    /// <param name="ct">Cancellation.</param>
    private static async Task<ControlRowSnapshot> CheckLoopBackNegativeDirectionCensusAsync(
        IProductRetriever retriever,
        CancellationToken ct)
    {
        var catalogue = Catalogue.Default;

        var doesNotLoop = new List<string>();
        var durable = new List<string>();
        var vacuous = new List<string>();
        var mapChoiceMoved = new List<string>();
        var perCustomer = new List<string>();
        int looping = 0;

        // The pool, as a function of the known-token set and the vocabulary built from it, so the
        // MAPPER-map and FINAL-map variants are the SAME code on two inputs rather than two copies.
        static int Pool(
            DiscoveryState state,
            HashSet<string> known,
            Galaxus.RecommendationAgent.Workflows.QueryVocabulary vocabulary)
        {
            int pool = 0;
            foreach (var signal in state.ObservedSignals)
            {
                var novel = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string token in Galaxus.RecommendationAgent.Workflows.QueryVocabulary.Tokenize(signal.Snippet))
                {
                    if (token.Length < 4) continue;
                    if (known.Contains(token)) continue;
                    if (Galaxus.RecommendationAgent.Workflows.QueryVocabulary.NeutralTokens.Contains(token)) continue;
                    if (!seen.Add(token)) continue;
                    novel.Add(token);
                }

                if (novel.Count == 0) continue;

                // The SAME filter TryAcceptProposal applies, on the same terms it would be handed.
                var scratch = new List<DroppedQueryTerm>();
                if (vocabulary.Filter(novel.Take(ReviewSnippetInterestProposer.MaxProposedTerms), "census", scratch).Count > 0)
                    pool++;
            }

            return pool;
        }

        static HashSet<string> KnownTokens(IEnumerable<Interest> interests)
        {
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var interest in interests)
            {
                foreach (string token in Galaxus.RecommendationAgent.Workflows.QueryVocabulary.Tokenize(interest.Label)) known.Add(token);
                foreach (string term in interest.QueryTerms)
                    foreach (string token in Galaxus.RecommendationAgent.Workflows.QueryVocabulary.Tokenize(term)) known.Add(token);
            }

            return known;
        }

        foreach (string personaId in Personas.AllPersonaIds)
        {
            var options = new Galaxus.RecommendationAgent.Workflows.DiscoveryLoopOptions(
                Offline: true,
                SessionRequest: GalaxusEvalPrompt.UtteranceFrom(Personas.CanonicalPromptFor(personaId)),
                Retriever: retriever,
                Progress: null,
                Nodes: null);

            var run = await GalaxusDiscoveryLoop.RunAsync(personaId, options, ct).ConfigureAwait(false);
            var state = run.State;

            // ── The pool, under BOTH maps. ──
            //
            // MAPPER-origin: the known-token set the reviewer starts round 1 with. FINAL: everything
            // the map ended up holding. The mapper map is the SMALLER known set, so it leaves the
            // LARGER novel set and the LARGER pool.
            //
            // ⚠ Both are computed because the choice between them is not self-evidently safe: a
            //   larger pool makes fewer customers durable, which makes "no replacement cell exists"
            //   — the conclusion this row is quoted for — EASIER to say. Reporting whether the
            //   durable set actually moves retires that question with a number instead of an
            //   adjective. The mapper figure stays the one the verdict uses.
            var mapperInterests = state.Interests.Where(i => i.Origin == InterestOrigin.Mapper).ToList();

            int admissiblePool = Pool(
                state,
                KnownTokens(mapperInterests),
                Galaxus.RecommendationAgent.Workflows.QueryVocabulary.Build(catalogue, mapperInterests, state.SessionRequest));

            int admissiblePoolFinalMap = Pool(
                state,
                KnownTokens(state.Interests),
                Galaxus.RecommendationAgent.Workflows.QueryVocabulary.Build(catalogue, state.Interests, state.SessionRequest));

            // ── 8.21's bite: rows the attribution gate would move out of COVERED. ──
            int coveredWithNothingItNames = 0;
            foreach (var interest in state.Interests)
            {
                var coverage = state.CoverageFor(interest.Id);
                if (coverage.QueriesRun.Count == 0) continue;
                if (CatalogueDiscoverySearch.ClassifyCoverage(coverage) != CoverageStatus.Covered) continue;
                if (coverage.AttributableProductIds.Count == 0) coveredWithNothingItNames++;
            }

            if (run.Looped) looping++;
            else doesNotLoop.Add(personaId);

            // ⚠ The MAP CHOICE, made checkable. If the two pools ever disagree on a customer that
            //   does NOT loop, the verdict below depends on which map was picked and the row must
            //   say so out loud rather than let the docstring's adjective settle it.
            if (!run.Looped && admissiblePool != admissiblePoolFinalMap)
                mapChoiceMoved.Add($"{personaId} (mapper {admissiblePool} vs final {admissiblePoolFinalMap})");

            bool survivesBoth = !run.Looped && admissiblePool == 0 && coveredWithNothingItNames == 0;

            // ⚠ 0 OF 0 IS NOT A MEASURED 0. A survivor whose run produced no review snippets at all
            //   survives because the metric has no denominator there, not because a pool was
            //   examined and found barren. Counted apart, and never given the ⭐.
            bool vacuousSurvivor = survivesBoth && state.ObservedSignals.Count == 0;

            if (survivesBoth)
            {
                durable.Add(personaId);
                if (vacuousSurvivor) vacuous.Add(personaId);
            }

            perCustomer.Add(
                $"{personaId} {(run.Looped ? "loops" : "does NOT loop")} at round {state.DiscoveryRound}"
              + $" · admissible snippet pool {admissiblePool} of {state.ObservedSignals.Count} snippet(s)"
              + $" (final-map {admissiblePoolFinalMap}){(run.Looped ? " · over-count" : string.Empty)}"
              + $" · COVERED-with-nothing-it-names {coveredWithNothingItNames}"
              + (survivesBoth
                  ? vacuousSurvivor
                      ? $" · ⚠ SURVIVES VACUOUSLY — 0 of 0, there is no pool here to re-rank{Eval07Pins(personaId)}"
                      : $" · ⭐ SURVIVES BOTH{Eval07Pins(personaId)}"
                  : string.Empty));
        }

        int examined = durable.Count - vacuous.Count;

        string observed =
            $"{Personas.AllPersonaIds.Count} customer(s): {looping} loop, {doesNotLoop.Count} do not "
          + $"({string.Join(", ", doesNotLoop)}) · of the non-looping ones, "
          + $"{durable.Count} would still not loop under ANY proposal-selection policy AND are untouched by the "
          + $"attribution gate: {(durable.Count == 0 ? "NONE" : string.Join(", ", durable))} — "
          + $"but {examined} of those {durable.Count} was reached by EXAMINING a pool; "
          + (vacuous.Count == 0
              ? "none is vacuous"
              : $"{vacuous.Count} survives on 0 of 0 snippets ({string.Join(", ", vacuous)}), which is the "
              + "arithmetic of an empty set and not a measured barren pool")
          + $" · MAP CHOICE: {(mapChoiceMoved.Count == 0
              ? "the mapper-origin and final maps give the SAME pool on every non-looping customer, so the "
              + "verdict does not depend on which was chosen"
              : $"⚠ the two maps DISAGREE on {string.Join(", ", mapChoiceMoved)} — the verdict depends on the choice")}"
          + " · " + string.Join(" | ", perCustomer);

        return new ControlRowSnapshot(
            "LoopBackNegativeDirectionCensus",
            "Eval 07 GATE B and plan item 8.21's third precondition are blocked on the SAME corpus fact, and "
          + "neither had measured it: the corpus's only loop-back negative cell (USR-NB-01) is flipped by the "
          + "GATE B remedy (§28.2) and independently by the attribution gate (§31.3). This row asks whether a "
          + "replacement cell exists, and it asks it POLICY-INDEPENDENTLY: a customer whose pool of review "
          + "snippets contains not one novel token the vocabulary would admit cannot be made to loop by any "
          + "RE-RANKING of that pool, because there is nothing in it to promote. ⚠ CORRECTED 2026-09-06 by "
          + "review, twice. (1) A pool of 0 OF 0 is not a measured zero — the row's only survivor is the "
          + "abstention persona, whose run produces no snippets at all while every other customer's "
          + "denominator is 13-27, so the one figure the conclusion rested on was the one place the metric "
          + "degenerates. The denominator is printed, a 0-of-0 survivor is labelled VACUOUS not ⭐, and each "
          + "survivor's existing Eval 07 pins are printed so a cell already pinned PresentsAnswerText:false "
          + "cannot be read as a replacement for Nadia's. (2) The mapper-origin map was called the "
          + "'unflattering' choice; a LARGER pool makes FEWER customers durable and so makes 'no replacement "
          + "exists' EASIER to say, which is the flattering direction for the deferral this row is quoted "
          + "for. Both maps are now computed and the row reports whether the durable set moves",
            observed,
            Tripped: true,
            Gating: false);
    }

    /// <summary>
    /// The Eval 07 pins already carried by <paramref name="personaId"/>, or a note that it is not an
    /// Eval 07 case — derived from <see cref="Eval07_WorkflowTopology.Cases"/>, never re-typed.
    /// </summary>
    /// <remarks>
    /// A census survivor is only interesting as a REPLACEMENT for the ⭐ negative-direction cell, and
    /// a customer already pinned <c>PresentsAnswerText: false</c> is the abstention cell rather than
    /// a customer whose reviewer ran on a full tray and chose not to loop. Printing the pins is what
    /// stops "SURVIVES" being read as "is a replacement".
    /// </remarks>
    /// <param name="personaId">The customer.</param>
    private static string Eval07Pins(string personaId)
    {
        var existing = Eval07_WorkflowTopology.Cases
            .FirstOrDefault(c => string.Equals(c.PersonaId, personaId, StringComparison.Ordinal));

        return existing is null
            ? " (not an Eval 07 case today)"
            : $" (ALREADY an Eval 07 case: ExpectsLoopBack={existing.ExpectsLoopBack}, "
            + $"PresentsAnswerText={existing.PresentsAnswerText}"
            + (existing.PresentsAnswerText ? ")" : " — the ABSTENTION cell, not a replacement for the ⭐)");
    }

    // == 2.11's PRECONDITION -- one constant, two jobs, and the coupling that hid between them. ==
    //
    /// <summary>
    /// The coverage CUT and the confidence SHAPE PARAMETER must be two separate constants, and each
    /// site must read the one that belongs to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this row exists for.</b> <c>DiscoveryState.MinCandidateScore</c> was a
    /// <i>cut</i> in <c>CatalogueDiscoverySearch.ClassifyCoverage</c> and
    /// <c>CoverageVerdictProjection.Starved</c> AND the <i>half-saturation constant</i> of
    /// <c>DeterministicRanker.Confidence</c>'s <c>s / (s + k)</c>. Plan item 2.11 asks for the cut
    /// to be derived on the held-out split that derived the other four; doing that would have moved
    /// every workflow-arm confidence, and confidence is what <c>ConfidenceBands</c> — itself derived
    /// on that same split — routes trays on. <b>One calibrated quantity would have moved through
    /// another, through a coupling neither derivation can see.</b> The two constants are now
    /// separate and carry the same value, so the split moved no number; what it removed is the
    /// coupling, and this row is what stops it coming back.
    /// </para>
    /// <para>
    /// <b>Why it reads source rather than values.</b> Both constants are <c>const double 0.012</c>
    /// today, so <i>no runtime observation can distinguish</i> a ranker that reads the shape
    /// parameter from one that reads the cut — that is precisely what makes the split inert and
    /// also what makes a value-based check vacuous here. The discriminator has to be which symbol
    /// each site names, so this row scans the three call sites and asserts the separation in BOTH
    /// directions: the ranker's squash must name the shape parameter and must not name the cut, and
    /// the two coverage sites must name the cut and must not name the shape parameter.
    /// </para>
    /// <para>
    /// ⚠ <b>It asserts its own INPUT.</b> A source scan that finds no offender is indistinguishable
    /// from one that opened the wrong file — the silent-<c>{}</c> shape this repository has a
    /// standing rule about. This row fails if a file is missing, if a method body is not located,
    /// if either constant is absent from <c>DiscoveryState</c>, or if the reflected field is not a
    /// compile-time literal. It also prints both values, so a future divergence is visible rather
    /// than merely permitted.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckCoverageCutIsNotTheConfidenceShapeParameter()
    {
        const string Cut = "MinCandidateScore";
        const string Shape = "RetrievalConfidenceHalfSaturation";

        var problems = new List<string>();

        // ── 1. Both constants must EXIST, as literals, on the type the sites read them from. ──
        var cutField = typeof(DiscoveryState).GetField(Cut);
        var shapeField = typeof(DiscoveryState).GetField(Shape);

        if (cutField is null || !cutField.IsLiteral)
            problems.Add($"DiscoveryState.{Cut} is missing or is not a compile-time constant.");
        if (shapeField is null || !shapeField.IsLiteral)
        {
            problems.Add($"DiscoveryState.{Shape} is missing or is not a compile-time constant — without it there is "
                       + "nothing for the ranker to read and the two jobs are one constant again.");
        }

        // ── 2. The three call sites, each scoped to the method that owns it. ──────────────
        //      (file, method signature to scope to, symbol required, symbol forbidden)
        (string File, string Method, string Required, string Forbidden)[] sites =
        [
            (Path.Combine("Workflows", "DeterministicDiscoveryNodes.cs"),
             "public static double Confidence(", Shape, Cut),
            (Path.Combine("Workflows", "CatalogueDiscoverySearch.cs"),
             "static CoverageStatus ClassifyCoverage(", Cut, Shape),
            (Path.Combine("Workflows", "CoverageReview.cs"),
             "public static IReadOnlyList<Interest> Starved(", Cut, Shape),
        ];

        int bodiesRead = 0;

        foreach (var (file, method, required, forbidden) in sites)
        {
            string path = Path.Combine(AgentSourceRoot(), file);
            if (!File.Exists(path))
            {
                problems.Add($"{file} was not found — this scan cannot say anything about a file it did not open.");
                continue;
            }

            string source = File.ReadAllText(path);
            string? body = MethodBody(source, method);
            if (body is null)
            {
                problems.Add($"{method}…) was not located in {file} — the signature moved, so the scan is reading "
                           + "something other than the site it names.");
                continue;
            }

            bodiesRead++;

            // Comments in these bodies deliberately NAME the other constant to explain the split,
            // so the scan reads code only. Anything else would forbid the explanation.
            string code = StripComments(body);

            if (!code.Contains(required, StringComparison.Ordinal))
                problems.Add($"{method}…) in {file} does not read {required} — the site is not wired to the constant that belongs to it.");
            if (code.Contains(forbidden, StringComparison.Ordinal))
            {
                problems.Add($"{method}…) in {file} reads {forbidden} — that is the coupling 2.11's precondition "
                           + "removed: a cut and a half-saturation constant cannot be calibrated as one number.");
            }
        }

        if (bodiesRead != sites.Length)
            problems.Add($"only {bodiesRead} of {sites.Length} method bodies were read — a partial scan is not a verdict.");

        double cutValue = DiscoveryState.MinCandidateScore;
        double shapeValue = DiscoveryState.RetrievalConfidenceHalfSaturation;

        return new ControlRowSnapshot(
            "CoverageCutIsNotTheConfidenceShapeParameter",
            "plan item 2.11's recorded PRECONDITION. MinCandidateScore was one constant doing two structurally "
          + "different jobs — a cut in ClassifyCoverage and Starved, and the half-saturation constant of "
          + "DeterministicRanker.Confidence's s/(s+k), which ConfidenceBands routes trays on. Re-deriving it as a "
          + "cut would have moved a second calibrated quantity through a coupling neither derivation can see. The "
          + "two are now separate constants AT THE SAME VALUE, so the split is arithmetically inert and no printed "
          + "number moved; this row asserts, in both directions and per method body, that each site reads the "
          + "constant that belongs to it. ⚠ It reads SOURCE because no runtime observation can tell the two apart "
          + "while they carry equal values — which is the same fact that makes the split safe",
            problems.Count == 0
                ? $"{bodiesRead} of {sites.Length} method bodies scanned · Confidence reads {Shape} and not {Cut} · "
                + $"ClassifyCoverage and Starved read {Cut} and not {Shape} · both constants are compile-time "
                + $"literals · {Cut} = {cutValue:0.0000}, {Shape} = {shapeValue:0.0000}"
                + (Math.Abs(cutValue - shapeValue) < double.Epsilon
                    ? " — equal, which is why nothing moved; they are no longer required to stay equal"
                    : " — ⚠ THEY HAVE DIVERGED, so every confidence and every tray routing has moved since the split")
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    /// <summary>
    /// The brace-matched body of the first method in <paramref name="source"/> whose text starts
    /// with <paramref name="signature"/>, or null when the signature is not there.
    /// </summary>
    /// <remarks>
    /// Scoping to a body is the whole point: <c>DeterministicDiscoveryNodes.cs</c> legitimately
    /// names the cut elsewhere in the file (the pre-gate prints it), so a file-level grep would
    /// report a coupling that is not there.
    /// </remarks>
    private static string? MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return null;

        int open = source.IndexOf('{', at);
        if (open < 0) return null;

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        return null;
    }

    /// <summary>
    /// Strips double-quoted string literals so a scan reads code, not the sentences the code
    /// PRINTS.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Fourth sighting of the <c>baca28e4</c> shape.</b> A control that forbids a source
    /// pattern will describe that pattern in its own printed prose, and the description then
    /// satisfies the scan. It has now happened to a grep count (<c>baca28e4</c>), to the CI recap's
    /// <i>"Eval 07: passed"</i> (twice) and to plan item 8.5's own dry-run check, whose printed
    /// explanation quotes the <c>judge &amp;&amp; !dryRun</c> it exists to forbid. Comment-stripping
    /// alone does not help: a string literal is code.
    /// </remarks>
    /// <param name="body">Source text, ideally already comment-stripped.</param>
    private static string StripStringLiterals(string body)
    {
        var sb = new System.Text.StringBuilder(body.Length);
        bool inString = false;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }              // an escape consumes the next char
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Strips line comments so a scan reads code, not the prose that explains it.</summary>
    private static string StripComments(string body)
    {
        var kept = new List<string>();
        foreach (string line in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            int slashes = line.IndexOf("//", StringComparison.Ordinal);
            kept.Add(slashes < 0 ? line : line[..slashes]);
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// The <c>samples/Galaxus.RecommendationAgent</c> source directory — the AGENT project, not the
    /// evals project <see cref="SampleSourceRoot"/> returns.
    /// </summary>
    /// <remarks>Throws rather than returning a path whose files are absent, for the reason stated on
    /// <see cref="SampleSourceRoot"/>: a scan that opened nothing must not read as a scan that found
    /// nothing.</remarks>
    private static string AgentSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0)
            {
                string root = Path.Combine(dir.FullName, "samples", "Galaxus.RecommendationAgent");
                if (File.Exists(Path.Combine(root, "Workflows", "DiscoveryState.cs"))) return root;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "the agent project's source directory was not found from " + AppContext.BaseDirectory);
    }

    // == 1.4 / N-4 -- "above chance" must be a TEST, not a comparison. ====================
    //
    /// <summary>
    /// Proves the suite's one "is this above chance?" decision is an exact one-sided binomial, at
    /// the sizes this corpus actually has, and that the OLD rule would have answered differently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It tests the shipped decision, not a copy of it.</b> Every ▲ in the forced-choice panel,
    /// the instrument caveat's "arms that beat chance" count, and the oracle-discrimination row all
    /// route through <see cref="ExactBinomial.AboveChance"/>; this row calls the same method. A row
    /// that re-implemented the test would certify its own arithmetic and nothing else — the
    /// co-derived-operand shape this panel already has two entries about.
    /// </para>
    /// <para>
    /// <b>Both directions, and the counterfactual is part of the assertion.</b> The row fails if
    /// 2 of 12 or 3 of 12 at a 1/12 floor come back ABOVE chance, and it also fails if 7 of 12 does
    /// not — a test that refused everything would pass a one-sided check and be worthless. It
    /// additionally records that <c>rate &gt; floor</c> says YES to all three, so the two ticks this
    /// change removes are visible in the report rather than only in a commit message.
    /// </para>
    /// <para>
    /// <b>And the arithmetic is pinned against a reference, not against itself.</b> The three
    /// p-values are checked to 1e-4 against values computed independently from the binomial upper
    /// tail (R: <c>binom.test(x, 12, 1/12, alternative = "greater")</c>).
    /// </para>
    /// <para>
    /// ⚠ <b>SCOPE, corrected 2026-09-06 by review.</b> This row's first revision claimed that
    /// <i>every</i> ▲ in the suite comes from this test. It does not, and the row never checked it:
    /// <c>CoverageScore.AboveOwnFloor</c>, <c>CoverageScore.AbovePrecisionFloor</c> and Eval 02b's
    /// two per-case markers are still <c>rate &gt; floor</c>, and the first of those is what Eval
    /// 02's GATE 1 reads through <c>PairedCoverageReport.EveryPersonaAboveOwnFloor</c>. Their null
    /// is Poisson-binomial rather than binomial, so this class is the wrong instrument for them and
    /// converting them is a separate item with a declared GATE 1 movement. What this row certifies
    /// is the FORCED-CHOICE decision and the three sites that share it.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckAboveChanceIsAnExactTest()
    {
        var problems = new List<string>();
        const double floor = 1.0 / 12.0;

        var two   = ExactBinomial.AboveChance(2, 12, floor);
        var three = ExactBinomial.AboveChance(3, 12, floor);
        var seven = ExactBinomial.AboveChance(7, 12, floor);

        // ── the two ticks this change removes ──
        if (two.Above)
            problems.Add($"2 of 12 against a 1/12 floor came back ABOVE chance ({ExactBinomial.FormatP(two.P)}) — that is the defect, not the fix.");
        if (three.Above)
            problems.Add($"3 of 12 against a 1/12 floor came back ABOVE chance ({ExactBinomial.FormatP(three.P)}).");

        // ── and the tick it must KEEP, or the test refuses everything ──
        if (!seven.Above)
            problems.Add($"7 of 12 against a 1/12 floor did NOT come back above chance ({ExactBinomial.FormatP(seven.P)}) — a test that says no to everything is not a test.");

        // ── the reference values, so the arithmetic is not certified by itself ──
        if (Math.Abs(two.P   - 0.26400914) > 1e-6) problems.Add($"P(X>=2 | n=12, p=1/12) is {two.P:0.00000000}, reference 0.26400914.");
        if (Math.Abs(three.P - 0.07201153) > 1e-6) problems.Add($"P(X>=3 | n=12, p=1/12) is {three.P:0.00000000}, reference 0.07201153.");
        if (Math.Abs(seven.P - 0.00001515) > 1e-8) problems.Add($"P(X>=7 | n=12, p=1/12) is {seven.P:0.00000000}, reference 0.00001515.");

        // ── the BOUNDARY, so the test is not merely refusing everything below 7 ──
        //
        // 4 of 12 is the smallest observation this floor admits: p = 0.01383 < 0.05, where 3 of 12
        // is 0.07201 > 0.05. Pinning the PAIR pins the decision edge, which "refuses 3, accepts 7"
        // on its own does not.
        var four = ExactBinomial.AboveChance(4, 12, floor);
        if (!four.Above)
            problems.Add($"4 of 12 was refused ({ExactBinomial.FormatP(four.P)}) — the cut sits above its own boundary.");
        if (Math.Abs(four.P - 0.01383043) > 1e-6)
            problems.Add($"P(X>=4 | n=12, p=1/12) is {four.P:0.00000000}, reference 0.01383043.");

        // ── the edges an empty denominator produces, because this suite meets them ──
        if (!double.IsNaN(ExactBinomial.UpperTailP(0, 0, floor)))
            problems.Add("zero trials returned a number — an empty denominator is not a result.");
        if (ExactBinomial.AboveChance(0, 12, floor).Above)
            problems.Add("an arm that scored NOTHING came back above chance.");

        // ── and it must not have become monotone in the wrong direction ──
        if (ExactBinomial.UpperTailP(3, 12, floor) > ExactBinomial.UpperTailP(2, 12, floor))
            problems.Add("the upper tail grew with the observation — the test is inverted.");

        // ── an IMPOSSIBLE observation must not become the panel's most confident tick ──
        //
        // P(X >= 13 | n = 12) is 0, so a caller that produced more wins than trials would have been
        // handed p = 0 and the greenest ▲ on screen. That is a broken caller, not a strong result.
        if (ExactBinomial.AboveChance(13, 12, floor).Above)
            problems.Add("13 of 12 came back ABOVE chance — an impossible observation printed the most confident verdict in the panel.");

        // ── the MULTIPLICITY family is computed from the run, not quoted from a constant ──
        //
        // Pinned against 1 - 0.95^m, computed independently. The shipped panel tests SIX arms
        // (0.265), and the first revision printed the five-arm figure (0.226) underneath it — an
        // understatement, which is the flattering direction for a lone ▲.
        double fwer5 = ExactBinomial.FamilyWiseErrorRate(5);
        double fwer6 = ExactBinomial.FamilyWiseErrorRate(6);
        if (Math.Abs(fwer5 - 0.22621906) > 1e-6)
            problems.Add($"the family-wise error rate over 5 tests is {fwer5:0.00000000}, reference 0.22621906.");
        if (Math.Abs(fwer6 - 0.26490811) > 1e-6)
            problems.Add($"the family-wise error rate over 6 tests is {fwer6:0.00000000}, reference 0.26490811.");
        if (!double.IsNaN(ExactBinomial.FamilyWiseErrorRate(1)))
            problems.Add("a single test reported a FAMILY-wise error rate — one test is not a family.");
        if (fwer6 <= fwer5)
            problems.Add("the family-wise error rate did not grow with the family — a bigger panel cannot be safer.");

        bool oldRuleSaysYesToAll = (2.0 / 12.0 > floor) && (3.0 / 12.0 > floor) && (7.0 / 12.0 > floor);

        return new ControlRowSnapshot(
            "AboveChanceIsAnExactTest",
            "every FORCED-CHOICE \u25b2 must come from an EXACT one-sided binomial upper tail at p \u2264 "
          + $"{ExactBinomial.Alpha:0.00}, through the one method the printer calls. At n = 12 against a 1/12 "
          + "floor that must REFUSE 2 of 12 and 3 of 12 and ACCEPT 7 of 12 \u2014 a rule that refuses everything "
          + "passes a one-sided check and measures nothing. The p-values are pinned against an "
          + "independent reference; zero trials must give NaN rather than a verdict; an IMPOSSIBLE observation "
          + "must not print the panel's most confident tick; and the multiplicity family must be COMPUTED from "
          + "the arms a run tested, since a hard-coded family size can only understate the error rate. "
          + "\u26a0 SCOPE: this is the forced-choice decision and the three sites that share it, NOT every \u25b2 "
          + "in the suite \u2014 AboveOwnFloor, AbovePrecisionFloor and Eval 02b's two markers are still "
          + "rate > floor, and the first of those is what Eval 02's GATE 1 reads",
            problems.Count == 0
                ? $"2 of 12 {ExactBinomial.FormatP(two.P)} \u25bc \u00b7 3 of 12 {ExactBinomial.FormatP(three.P)} \u25bc \u00b7 "
                + $"4 of 12 {ExactBinomial.FormatP(four.P)} \u25b2 (the boundary) \u00b7 "
                + $"7 of 12 {ExactBinomial.FormatP(seven.P)} \u25b2 \u00b7 0 trials \u2192 NaN \u00b7 0 of 12 \u25bc \u00b7 "
                + "13 of 12 \u2192 refused \u00b7 "
                + $"the OLD rule (rate > floor) said \u25b2 to all three: {oldRuleSaysYesToAll} \u2014 so this change "
                + "removes exactly two unearned ticks and keeps the earned one. \u26a0 The paid \u00a727.4 panel HAS "
                + "an arm at 3 of 12 (Demo 2's deterministic arm, 0.250), so a verdict DOES move: \u25b2 \u2192 \u25bc. "
                + $"\u26a0 No multiplicity correction: 5 tests {ExactBinomial.FamilyWiseErrorRate(5):0.000}, "
                + $"6 tests {ExactBinomial.FamilyWiseErrorRate(6):0.000} \u2014 the shipped panel tests SIX"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 27 — the forced-choice count was a count of nothing (stage-2 smoke, 2026-09-06). ══
    //
    /// <summary>
    /// The integer the forced-choice panel hands the exact binomial must be a COUNT OF PERSONAS,
    /// and the chance floor the instrument caveat quotes must be the floor the panel tests against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two defects, both found by running the RUN_PROTOCOL's own stage-2 probe
    /// (<c>-- 2 --only USR-NB-01</c>, live), and both invisible on the full cohort.</b>
    /// </para>
    /// <list type="number">
    ///   <item><description><b>A rate and a count that contradict each other on one line.</b> The
    ///   panel printed <c>▼ Single Agent (Robin) 0.667 (0 of 1) chance 0.083 p = 1.0000</c>. A
    ///   persona's forced-choice cell is <c>CoverageScore.Mean</c>'s average over that arm's reps,
    ///   so on a 3-rep arm it is 0, ⅓, ⅔ or 1 — not a Bernoulli outcome. The panel integerised the
    ///   MEAN OF THOSE MEANS with <c>Math.Floor(rate × personas)</c>, which counts nothing: ⅔ of one
    ///   persona became "0 of 1". On the shipped 12-persona paid cohort
    ///   (<c>eval02_coverage_ab.json</c>, 2026-09-06 02:56:46) the same expression printed
    ///   <b>6 of 12</b> for a live arm <b>7</b> of whose twelve cells are majority wins, and
    ///   <b>7 of those 12 cells are split across reps</b> — so this was never probe-only.
    ///   </description></item>
    ///   <item><description><b>A chance floor of 1.000, with a conclusion hanging off it.</b>
    ///   <c>EvalPrinter.InstrumentCaveat</c> derived the floor as 1 / (personas that RAN). The
    ///   forced choice is decided against every persona's gold in the corpus, so on the probe the
    ///   caveat printed <i>"NO arm beats the forced-choice chance rate of 1.000 … Nothing here is
    ///   evidence about personalisation"</i> — an unbeatable bar and therefore an unfalsifiable
    ///   sentence — twelve lines above a panel whose own header said the chance was 0.083. It is
    ///   the floor-above-attainable shape, printed as a finding about the system.</description></item>
    /// </list>
    /// <para>
    /// ⚠ <b>What this row must NOT be "fixed" into.</b> Counting persona × rep would make every
    /// cell integral and delete the problem — and would be pseudo-replication.
    /// <c>CoverageScore.Mean</c> refuses it in terms, and inflating n by the rep count inflates any
    /// significance claim by √reps. The unit stays the persona; the reduction is stated
    /// (<c>ForcedChoiceTally</c>: majority of that persona's reps, a split rep is a loss) and the
    /// split cells are printed so the reader can see where the tally and the mean part company.
    /// </para>
    /// <para>
    /// <b>It tests the shipped path.</b> The tally comes from <c>PairedCoverageReport</c> and the
    /// sentence from <c>EvalPrinter.InstrumentCaveat</c> — the same two methods Eval 02 and Eval 09
    /// print from. A row that re-implemented either would certify its own arithmetic.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckForcedChoiceCountIsACountOfPersonas()
    {
        var problems = new List<string>();
        const double corpusFloor = 1.0 / 12.0;

        static CoverageScore Cell(double forced) =>
            new(0.5, double.NaN, 1, 2, 0, 0, 5, 0, 0,
                LatentFloor: 0.15, ForcedChoice: forced, DeclaredK: 5, PresentedBeforeCut: 5);

        // ── the stage-2 probe's exact shape: ONE persona, three reps, split 1/0/1 ──
        var probe = new PairedCoverageReport();
        probe.Record("P1", CoverageArms.Live, Cell(2.0 / 3.0));

        var probeTally = probe.ForcedChoiceTally(CoverageArms.Live);
        if (probeTally.Trials != 1)
            problems.Add($"the probe reported {probeTally.Trials} trial(s) for one persona.");
        if (probeTally.Wins != 1)
            problems.Add($"a persona identified on 2 of its 3 reps counted {probeTally.Wins} win(s) — that is the '0.667 (0 of 1)' defect.");
        if (probeTally.Split != 1)
            problems.Add($"the split cell was not reported ({probeTally.Split}) — a reader cannot see that the rate and the count are two reductions.");
        if (probe.ForcedChoiceSplitCells(CoverageArms.Live).Count != 1)
            problems.Add("the split cell was not nameable, so the panel could not print WHICH persona split.");

        // ── and the counterfactual, so the defect stays visible in the report ──
        double probeRate = probe.ForcedChoiceRate(CoverageArms.Live);
        int oldFloorWins = (int)Math.Floor((probeRate * probeTally.Trials) + 1e-9);
        int oldRoundWins = (int)Math.Round(probeRate * probeTally.Trials);
        if (oldFloorWins != 0)
            problems.Add("the OLD floor expression no longer reproduces the defect it is here to record — the counterfactual is stale.");

        // ── a tie across reps is a LOSS, the same rule the forced choice uses within one answer ──
        var tie = new PairedCoverageReport();
        tie.Record("P1", CoverageArms.Live, Cell(0.5));
        if (tie.ForcedChoiceTally(CoverageArms.Live).Wins != 0)
            problems.Add("a persona whose reps split evenly counted as a WIN — a tie is a loss everywhere else in this panel.");

        // ── clean cells must still be exact: no reduction may perturb a 0/1 corpus ──
        var clean = new PairedCoverageReport();
        clean.Record("P1", CoverageArms.SingleShot, Cell(1.0));
        clean.Record("P2", CoverageArms.SingleShot, Cell(0.0));
        clean.Record("P3", CoverageArms.SingleShot, Cell(1.0));
        var cleanTally = clean.ForcedChoiceTally(CoverageArms.SingleShot);
        if (cleanTally is not (2, 3, 0))
            problems.Add($"a deterministic arm's clean 1/0/1 tallied {cleanTally.Wins} of {cleanTally.Trials} with {cleanTally.Split} split — expected 2 of 3 with none split.");
        if (Math.Abs(clean.ForcedChoiceRate(CoverageArms.SingleShot) - (2.0 / 3.0)) > 1e-9)
            problems.Add("the rate moved on a corpus with no split cells — the tally must not feed back into the mean.");

        // ── an arm nobody scored is UNDECIDABLE, never zero wins out of zero ──
        var empty = new PairedCoverageReport();
        empty.Record("P1", CoverageArms.Popularity, Cell(double.NaN));
        var emptyTally = empty.ForcedChoiceTally(CoverageArms.Popularity);
        if (emptyTally.Trials != 0 || emptyTally.Wins != 0)
            problems.Add($"an arm with no defined outcome tallied {emptyTally.Wins} of {emptyTally.Trials} rather than 0 of 0.");
        if (!double.IsNaN(ExactBinomial.UpperTailP(emptyTally.Wins, emptyTally.Trials, corpusFloor)))
            problems.Add("0 of 0 produced a p-value — an empty denominator is not a result.");

        // ── THE CAVEAT'S FLOOR IS THE PANEL'S FLOOR, and it is not recomputed from who ran ──
        //
        // The probe report holds ONE persona. Derived locally that is 1/1 = 1.000, which nothing
        // can beat. The caller derives 1/12 from the GOLD map, and that is the number the sentence
        // must carry.
        var caveat = EvalPrinter.InstrumentCaveat(probe, corpusFloor);
        string caveatText = string.Join(" ", caveat);
        if (caveatText.Contains("chance rate of 1.000", StringComparison.Ordinal))
            problems.Add("the caveat quoted a chance rate of 1.000 — an unbeatable floor makes its conclusion unfalsifiable.");
        if (!caveatText.Contains("chance rate of 0.083", StringComparison.Ordinal))
            problems.Add($"the caveat did not quote the floor it was given (1/12 = 0.083): \"{Shorten(caveatText, 90)}\".");

        // ── and it must SUPPRESS the sentence rather than invent a floor when given none ──
        var noFloor = EvalPrinter.InstrumentCaveat(probe, double.NaN);
        if (string.Join(" ", noFloor).Contains("chance rate of", StringComparison.Ordinal))
            problems.Add("a NaN floor still produced a chance-rate sentence — the caveat invented a bar.");

        return new ControlRowSnapshot(
            "ForcedChoiceCountIsACountOfPersonas",
            "the integer the forced-choice panel hands the exact binomial must be a COUNT OF PERSONAS produced "
          + "by a stated reduction (majority of that persona's reps; a split rep is a loss), never "
          + "Math.Floor(mean × personas) — a mean of per-persona means is not a success count. And the "
          + "instrument caveat must quote the floor the PANEL tests against, derived by the caller from the GOLD "
          + "map, never 1 / (personas that happened to run): a floor of 1.000 is unbeatable, so the sentence that "
          + "hangs off it cannot be false. Both defects are invisible at n = 12 and both fire on the "
          + "RUN_PROTOCOL's own stage-2 probe",
            problems.Count == 0
                ? $"probe (1 persona, reps 1/0/1): rate {Format(probeRate)} → won {probeTally.Wins} of {probeTally.Trials}, "
                + $"{probeTally.Split} split · the OLD expressions gave floor → {oldFloorWins} (the shipped "
                + $"'0.667 (0 of 1)') and round → {oldRoundWins} · an even rep split is a LOSS · a clean "
                + "1/0/1 still tallies 2 of 3 with 0 split · 0 of 0 → NaN, not a verdict · caveat quotes "
                + "0.083 (given), never 1.000 (derived from who ran), and prints NO chance sentence at all when "
                + "given NaN · ⚠ MEASURED on the shipped paid cohort: 7 of the live arm's 12 cells are SPLIT "
                + "across reps, so the panel printed 6 of 12 where the cells say 7 — p = 0.000199 → 0.000015, "
                + "▲ either way"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 29 — a run that says it spends must say how much (2026-09-06 verification run). ══
    //
    /// <summary>
    /// Every command that warns it is spending must also report what it spent, from the provider's
    /// own usage blocks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> On <c>--real-vectors</c> every eval prints <i>"This run EMBEDS QUERIES
    /// LIVE. It needs credentials and it spends — a fraction of a cent, but not zero"</i>.
    /// <c>EmbeddingSpace.PrintLiveSpend</c> reports the actual figure — distinct texts embedded,
    /// requests the memo absorbed, prompt tokens read from the responses' own usage blocks — and it
    /// was called from exactly two places, <b>neither of them an eval</b>: Demo 01 and
    /// <c>ThresholdCalibration</c>. Measured on this wave's own sweep: <b>thirteen</b> real-vector
    /// commands declared a cost and <b>one</b> printed a number.
    /// </para>
    /// <para>
    /// <b>Why that is not the safe end of the rule.</b> RUN_PROTOCOL's cost discipline says to
    /// report cost from the provider's usage blocks and never from an estimate. Reporting NOTHING
    /// is not conservative — it is outside the rule, and it leaves "a fraction of a cent" as the
    /// only figure a reader has, which is an assertion nobody measured. It is the same shape as a
    /// dry run that persists silently (item 8.19): the machinery was right and the reporting was
    /// missing.
    /// </para>
    /// <para>
    /// <b>Both halves are checked.</b> That the eval entry point calls the reporter at all, and
    /// that the reporter still reads the PROVIDER's counters rather than estimating — including its
    /// LOWER BOUND caveat for responses that carry no usage block, which is the clause that stops a
    /// missing usage block being reported as zero.
    /// </para>
    /// <para>
    /// 🔴 <b>SCOPE, corrected at its origin 2026-09-06 (plan item 8.17). This row's NAME reads as a
    /// general rule and its BODY is one lane.</b> Every check below names
    /// <c>EmbeddingSpace.PrintLiveSpend</c>, so the whole subject is the EMBEDDING lane. The CHAT
    /// lane — the discovery loop's four model-backed stages, the lane that spends most — was never
    /// in scope, and this row was green across two verification runs that each recorded
    /// <c>agent -- 2</c> making three live model calls and printing no token count at all. It was
    /// not inert: its ablations were executed and reproduce. It was SCOPED, and the name
    /// over-promised, which is the flattering direction. The chat lane is now held by
    /// <see cref="CheckTheChatLaneSaysWhatItSpent"/>; neither row covers the other's lane, and that
    /// is why there are two.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckARunThatSaysItSpendsSaysHowMuch()
    {
        var problems = new List<string>();

        string agentRoot = Path.Combine(Directory.GetParent(SampleSourceRoot())!.FullName, "Galaxus.RecommendationAgent");

        // BOTH entry points, because both print the "it spends" warning. The eval suite's was
        // missing outright; the agent's reported only on Demo 01, so `-- 2 --offline
        // --real-vectors` declared a cost and reported none.
        foreach (var (label, path) in new[]
                 {
                     ("the eval suite's entry point", Path.Combine(SampleSourceRoot(), "Program.cs")),
                     ("the agent's entry point", Path.Combine(agentRoot, "Program.cs")),
                 })
        {
            if (!File.ReadAllText(path).Contains("EmbeddingSpace.PrintLiveSpend()", StringComparison.Ordinal))
            {
                problems.Add($"{label} never calls EmbeddingSpace.PrintLiveSpend, so its --real-vectors commands warn "
                           + "that they spend and then report no figure at all.");
            }
        }

        var reporter = typeof(Galaxus.RecommendationAgent.Retrieval.EmbeddingSpace).GetMethod(
            nameof(Galaxus.RecommendationAgent.Retrieval.EmbeddingSpace.PrintLiveSpend),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (reporter is null)
        {
            problems.Add("EmbeddingSpace.PrintLiveSpend no longer exists — the figure has no source.");
        }
        else
        {
            string body = File.ReadAllText(Path.Combine(agentRoot, "Retrieval", "EmbeddingSpace.cs"));

            if (!body.Contains("azure.PromptTokens", StringComparison.Ordinal))
                problems.Add("the spend reporter no longer reads the provider's PromptTokens — a cost that is not from a usage block is an estimate.");
            if (!body.Contains("LOWER BOUND", StringComparison.Ordinal))
                problems.Add("the reporter dropped its LOWER BOUND caveat, so a response carrying no usage block would be reported as costing nothing.");
            if (!body.Contains("EMBEDS QUERIES LIVE", StringComparison.Ordinal))
                problems.Add("the 'this run spends' warning is gone — this row pairs a warning with a figure, and there is no warning left to pair.");

            // ⚠ Demo 01 calls the reporter inside its own panel AND the agent's finally calls it
            //   again. Without the print-once latch a reader who added the two totals would double
            //   the bill, so the latch is part of the fix, not an optimisation.
            if (!body.Contains("_liveSpendPrinted = true;", StringComparison.Ordinal))
                problems.Add("the reporter no longer LATCHES — the field may still be declared, but nothing sets it, so a run with two call sites prints two totals for one bill.");
        }

        return new ControlRowSnapshot(
            "ARunThatSaysItSpendsSaysHowMuch",
            "⚠ THE EMBEDDING LANE ONLY — the chat lane is TheChatLaneSaysWhatItSpent, and this row was green while "
          + "that one's defect was open, twice. "
          + "a command that prints \"This run EMBEDS QUERIES LIVE … it spends\" must also print what it spent, from "
          + "the provider's own usage blocks. MEASURED on the 2026-09-06 verification sweep: EmbeddingSpace."
          + "PrintLiveSpend was called from Demo 01 and ThresholdCalibration and from no eval, so 13 of the 14 "
          + "real-vector commands declared a cost and reported none — leaving \"a fraction of a cent\" as the only "
          + "figure a reader had, which is an assertion nobody measured. Reporting nothing is not the conservative "
          + "end of RUN_PROTOCOL's cost rule, it is outside it",
            problems.Count == 0
                ? "BOTH entry points call EmbeddingSpace.PrintLiveSpend in a finally, so it fires whichever eval or "
                + "demo ran and whatever it returned · the reporter still reads the provider's PromptTokens rather "
                + "than estimating · its LOWER BOUND caveat for responses without a usage block is intact · it is "
                + "print-once per process, so Demo 01's in-panel call and the finally cannot bill a reader twice · "
                + "and it prints NOTHING on the concept path, where nothing is spent"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 30 — the CHAT lane spent and said nothing, for two whole verification runs. ══
    //
    /// <summary>
    /// A model call that spends must say how much, from the PROVIDER's usage block, and an absent
    /// usage block must not render as a zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this row exists for, and the row that did NOT catch it.</b>
    /// <c>ARunThatSaysItSpendsSaysHowMuch</c> reads as a general rule and is not one: every check in
    /// its body names <c>EmbeddingSpace.PrintLiveSpend</c>, so its whole subject is the EMBEDDING
    /// lane. The CHAT lane — four model-backed stages of the discovery loop, the lane that spends
    /// most — was never in its scope, which is why it stayed green across two verification runs
    /// that both recorded <c>agent -- 2</c> making three live model calls and printing no token
    /// count at all. The row was not inert; it was SCOPED, and its name over-promised. Direction:
    /// flattering, because a reader takes a green tick beside that sentence as covering the lane
    /// that costs the most.
    /// </para>
    /// <para>
    /// <b>The root cause, which is neither "the provider does not report it" nor "nobody asked".</b>
    /// <c>DiscoveryModelCall.RunAsync</c> called <c>AIAgent.RunAsync</c> and returned
    /// <c>response.Text</c>, dropping <c>AgentResponse.Usage</c> — the property the provider fills
    /// in on that exact call. The control that settles it is inside this repository:
    /// <c>MAFAgentAdapter</c> makes the identical call against the same deployment and reads that
    /// same property, and the evals that go through it report <c>token-estimated 0 · unaccounted
    /// 0</c>. So the usage arrived and our code threw it away.
    /// </para>
    /// <para>
    /// <b>Every check here EXECUTES the seam rather than reading it.</b> A source-text assertion
    /// that <c>Usage</c> is mentioned would be satisfied by this very comment — the shape §34.4
    /// already recorded once, where a latch was asserted by an identifier its own dead field
    /// supplied. So the row drives a real <c>DiscoveryModelCall</c> against two stub clients, one
    /// that returns a usage block and one that does not, and reads what landed in
    /// <c>DiscoveryState.Spend</c>.
    /// </para>
    /// <para>
    /// ⚠ <b>Its SUBJECT is all three chat lanes, and it was one when it shipped.</b> Item 8.17
    /// metered the discovery loop and Eval 08's workflow arm because that is where it was filed, and
    /// this row's name says <i>the chat lane</i>. Demo 01's live single-agent turn is a chat lane
    /// too — the same deployment, the same <c>AIAgent.RunAsync</c>, the same <c>response.Usage</c> —
    /// and it was reading that response for its tool calls and never for its usage, while printing
    /// the EMBEDDING lane's spend a few lines below. The review pass of 2026-09-06 metered it and
    /// added it to clause 4. <b>The lesson is one wave old and was repeated inside the fix for
    /// it:</b> a row's NAME is not its subject; count the lanes its body actually reaches.
    /// </para>
    /// <list type="number">
    ///   <item><description><b>WIRED:</b> a response carrying usage lands in the state, with the
    ///   provider's exact numbers and not a rounding of them.</description></item>
    ///   <item><description><b>ABSENCE ≠ ZERO:</b> a response carrying no usage adds no tokens,
    ///   counts as <c>CallsWithoutUsage</c>, and RENDERS in different words from a call that
    ///   reported a genuine zero. This is the check that fails when a missing figure is laundered
    ///   into a free one.</description></item>
    ///   <item><description><b>NEVER ESTIMATED:</b> the meter has no path from a string to a token
    ///   count, so a later "helpful" fallback cannot make our tokenizer look like the
    ///   provider's bill.</description></item>
    ///   <item><description><b>REPORTED:</b> all three spending lanes — Demo 01, Demo 02 and Eval
    ///   08's workflow arm — read the meter. A meter nobody prints is the state this row was written
    ///   to end.</description></item>
    /// </list>
    /// </remarks>
    private static ControlRowSnapshot CheckTheChatLaneSaysWhatItSpent()
    {
        var problems = new List<string>();

        // ── 1 + 2. Drive the real seam, twice, with no network and no credentials ──────────
        var withUsage = ProbeChatSpend(new UsageDetails { InputTokenCount = 1234, OutputTokenCount = 56 });
        var reportedZero = ProbeChatSpend(new UsageDetails { InputTokenCount = 0, OutputTokenCount = 0 });
        var noUsage = ProbeChatSpend(usage: null);

        // ⚠ THE HALF-BLOCK, added by the review pass of 2026-09-06. The absence rule was applied to
        //   the WHOLE block and then `?? 0` was written for each half, so a response reporting a
        //   prompt count and NO completion count read as `1,234 prompt + 0 completion`, `Complete`
        //   stayed true, and Eval08LiveWorkflowArm would have handed that to the harness as
        //   TokensAreEstimated = false. Same defect as check 2, one level down, and in the same
        //   flattering direction.
        var halfBlock = ProbeChatSpend(new UsageDetails { InputTokenCount = 1234 });

        if (withUsage.CallsWithUsage != 1 || withUsage.PromptTokens != 1234 || withUsage.CompletionTokens != 56)
        {
            problems.Add("a chat response carrying a usage block did not reach DiscoveryState.Spend intact "
                       + $"(read {withUsage.CallsWithUsage} call(s), {withUsage.PromptTokens} prompt, "
                       + $"{withUsage.CompletionTokens} completion; expected 1 / 1234 / 56). The lane spends and "
                       + "reports nothing again.");
        }

        if (noUsage.CallsWithoutUsage != 1 || noUsage.CallsWithUsage != 0
            || noUsage.PromptTokens != 0 || noUsage.CompletionTokens != 0)
        {
            problems.Add("a chat response with NO usage block was not recorded as an absence "
                       + $"({noUsage.CallsWithUsage} with usage, {noUsage.CallsWithoutUsage} without, "
                       + $"{noUsage.PromptTokens} prompt tokens; expected 0 / 1 / 0).");
        }

        if (halfBlock.CallsWithPartialUsage != 1 || halfBlock.CallsWithUsage != 0
            || halfBlock.PromptTokens != 1234 || halfBlock.CompletionTokens != 0 || halfBlock.Complete)
        {
            problems.Add("a usage block carrying ONE of the two counts was not recorded as a PARTIAL reading "
                       + $"({halfBlock.CallsWithUsage} complete, {halfBlock.CallsWithPartialUsage} partial, "
                       + $"{halfBlock.PromptTokens} prompt, {halfBlock.CompletionTokens} completion, "
                       + $"Complete={halfBlock.Complete}; expected 0 / 1 / 1234 / 0 / False). A completion count "
                       + "the provider never sent is being summed as a measured zero, and a total nobody measured "
                       + "in full is being published as complete.");
        }

        string halfText = string.Join(" ", DescribeSpend(halfBlock));
        if (!halfText.Contains("LOWER BOUND", StringComparison.Ordinal))
        {
            problems.Add("a half-reported usage block renders without a LOWER BOUND caveat, so a partial total "
                       + $"reads as a whole one: \"{halfText}\".");
        }

        // The one that matters most: a MISSING figure and a MEASURED ZERO must not print the same
        // sentence. Both are "0 tokens" arithmetically and only one of them is a measurement.
        string zeroText = string.Join(" ", DescribeSpend(reportedZero));
        string absentText = string.Join(" ", DescribeSpend(noUsage));

        if (zeroText.Length == 0 || absentText.Length == 0)
        {
            problems.Add("the meter rendered nothing for a call that was made — a lane that spent must produce a line.");
        }
        else if (string.Equals(zeroText, absentText, StringComparison.Ordinal))
        {
            problems.Add("a provider-reported ZERO and a MISSING usage block render identically "
                       + $"(\"{zeroText}\"), so a figure nobody measured reads as a free run.");
        }
        else if (!absentText.Contains("UNKNOWN", StringComparison.Ordinal))
        {
            problems.Add("the missing-usage rendering does not say UNKNOWN, so an absence is being reported "
                       + $"as something else: \"{absentText}\".");
        }

        // A figure that renders differently on different machines cannot be summed out of a log by
        // the next reader. The first live run of this meter printed `7’202` on a Swiss box — the same
        // machine-culture shape as the `C4`-renders-USD-as-CHF defect Eval 08 already carries.
        string figure = string.Join(" ", DescribeSpend(withUsage));
        if (!figure.Contains("1,234", StringComparison.Ordinal))
        {
            problems.Add("the token figure is not rendered in the invariant culture "
                       + $"(expected the group separator in \"1,234\"; got \"{figure}\"), so its text depends on "
                       + "who ran it and a log grep for it fails on another machine.");
        }

        // ── 3. The meter may never derive a token count from text ──────────────────────────
        string agentRoot = Path.Combine(Directory.GetParent(SampleSourceRoot())!.FullName, "Galaxus.RecommendationAgent");
        string meter = File.ReadAllText(Path.Combine(agentRoot, "Workflows", "ChatSpend.cs"));
        foreach (string forbidden in new[] { "EstimateTokens", ".Length / 4", "Length / 4.0" })
        {
            if (meter.Contains(forbidden, StringComparison.Ordinal))
            {
                problems.Add($"ChatSpend contains '{forbidden}' — it has grown a path from OUR text to a token "
                           + "count, which measures our tokenizer and not the provider's bill.");
            }
        }

        // ── 4. Both spending lanes must actually READ the meter ────────────────────────────
        foreach (var (label, path, needle) in new[]
                 {
                     ("Demo 02", Path.Combine(agentRoot, "Demos", "Demo02_InterestMapWorkflow.cs"), "PrintChatSpend(result.State)"),
                     ("Eval 08's workflow arm", Path.Combine(SampleSourceRoot(), "Evals", "Eval08_StochasticStability.cs"), "PrintWorkflowChatSpend(workflowArm)"),
                     // ⚠ THE THIRD LANE, added by the review pass of 2026-09-06. Demo 01's live
                     //   single-agent turn is a chat lane too — the same deployment, the same
                     //   AIAgent.RunAsync — and it read response.Messages for the tool trace and
                     //   never response.Usage, so it printed a tool-call COUNT, elapsed seconds and
                     //   the EMBEDDING lane's spend, and no chat bill at all. This row's NAME said
                     //   "the chat lane" while its body reached one of the three: the same
                     //   over-promise §55.4 recorded about ARunThatSaysItSpendsSaysHowMuch, made by
                     //   the row written to fix it.
                     ("Demo 01", Path.Combine(agentRoot, "Demos", "Demo01_RecommendationAgent.cs"), "chatSpend.Record(response.Usage)"),
                     ("Demo 01's printer", Path.Combine(agentRoot, "Demos", "Demo01_RecommendationAgent.cs"), "PrintChatSpend(chatSpend)"),
                 })
        {
            if (!WithoutCommentLines(File.ReadAllText(path)).Contains(needle, StringComparison.Ordinal))
            {
                problems.Add($"{label} no longer prints the chat meter, so it makes live model calls and reports "
                           + "a call COUNT in place of a bill — which is the state item 8.17 was opened for.");
            }
        }

        return new ControlRowSnapshot(
            "TheChatLaneSaysWhatItSpent",
            "the discovery loop's model calls must report PROVIDER-reported tokens, a call whose response carried "
          + "no usage block must render as UNKNOWN rather than as zero, and ALL THREE spending lanes must print it. "
          + "MEASURED: DiscoveryModelCall returned response.Text and dropped AgentResponse.Usage, so `agent -- 2` "
          + "made 3 live model calls and printed no token count on two consecutive verification runs, and Eval 08's "
          + "workflow arm fell back to the harness's text-length estimate over text REPLAYED from workflow state. "
          + "⚠ ARunThatSaysItSpendsSaysHowMuch was green throughout: every check in it names EmbeddingSpace, so its "
          + "subject is the EMBEDDING lane and its name over-promises",
            problems.Count == 0
                ? "a stubbed response carrying usage 1234/56 reaches DiscoveryState.Spend exactly · a response with "
                + "no usage block adds no tokens and counts as an absence · a provider-reported ZERO and a MISSING "
                + "block render in different words, and the missing one says UNKNOWN · the figure renders in the "
                + "INVARIANT culture, so a log grep survives the machine · ChatSpend has no path from text to "
                + "tokens · Demo 01, Demo 02 and Eval 08's workflow arm all print the meter"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    /// <summary>
    /// The source with every whole-line comment removed, so a needle search cannot be satisfied by
    /// a comment that merely MENTIONS the call.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Found by ablating this row's own clause 4, on 2026-09-06.</b> The clause was a plain
    /// <c>File.ReadAllText(...).Contains(needle)</c>, so commenting the call site OUT — the most
    /// natural way anyone would disable it — left the needle sitting in the comment and the row
    /// GREEN. That is the exact shape this row's remarks cite twice (§34.4's latch asserted by its
    /// own dead field, §55.5's <i>"a source-text assertion would be satisfied by the comment
    /// explaining it"</i>), reproduced inside the check written to avoid it. The earlier ablation
    /// missed it only because it deleted the call rather than commenting it out.
    /// </remarks>
    /// <param name="source">The file's text.</param>
    private static string WithoutCommentLines(string source)
    {
        var kept = source
            .Split('\n')
            .Where(line =>
            {
                string t = line.TrimStart();
                return !t.StartsWith("//", StringComparison.Ordinal)
                    && !t.StartsWith("*", StringComparison.Ordinal)
                    && !t.StartsWith("/*", StringComparison.Ordinal);
            });

        return string.Join('\n', kept);
    }

    /// <summary>
    /// Runs ONE real <c>DiscoveryModelCall</c> turn against a stub whose response carries
    /// <paramref name="usage"/>, and returns what the state's meter recorded.
    /// </summary>
    /// <remarks>
    /// The whole point is that this goes through the shipped call path — the same
    /// <c>ChatClientAgent</c>, the same <c>RunAsync</c>, the same <c>state.Spend.Record</c> — rather
    /// than testing <c>ChatSpend</c> in isolation. A meter that is correct and unwired is the defect,
    /// not the fix.
    /// </remarks>
    /// <param name="usage">The usage block the stub's response carries, or null for none.</param>
    private static ChatSpendSnapshot ProbeChatSpend(UsageDetails? usage)
    {
        var state = new DiscoveryState { CustomerId = "USR-CTL-30", Market = "CH", Language = "de" };
        var call = new DiscoveryModelCall(new UsageStubChatClient(usage), NullDiscoveryProgressSink.Instance);

        _ = call.RunAsync("spend-meter-probe", "You are a control.", "Reply with anything.", state, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

        return state.Spend.Snapshot();
    }

    /// <summary>Renders a snapshot through the same words the demos print.</summary>
    /// <param name="snapshot">The snapshot.</param>
    private static IReadOnlyList<string> DescribeSpend(ChatSpendSnapshot snapshot)
    {
        var spend = new ChatSpend();
        for (int i = 0; i < snapshot.CallsWithUsage; i++)
        {
            spend.Record(new UsageDetails
            {
                InputTokenCount = i == 0 ? snapshot.PromptTokens : 0,
                OutputTokenCount = i == 0 ? snapshot.CompletionTokens : 0,
            });
        }

        for (int i = 0; i < snapshot.CallsWithPartialUsage; i++)
        {
            spend.Record(new UsageDetails { InputTokenCount = i == 0 ? snapshot.PromptTokens : 0 });
        }

        for (int i = 0; i < snapshot.CallsWithoutUsage; i++) spend.RecordNoResponse();
        return spend.Describe();
    }

    /// <summary>
    /// A chat client whose only interesting property is whether its response carries a usage block.
    /// </summary>
    /// <remarks>
    /// <c>StubChatClient</c> deliberately sets none — which is correct for it, and is why a dry run
    /// of Eval 08 now exercises the ABSENCE branch — so this control needs its own stub to drive the
    /// positive case.
    /// </remarks>
    private sealed class UsageStubChatClient(UsageDetails? usage) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"ok\":true}"))
            {
                ModelId = "usage-stub",
                FinishReason = ChatFinishReason.Stop,
                Usage = usage,
            });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose() { }
    }

    // ══ Control 28 — the CI chain stubbed a model-free eval, and hid a red gate (2026-09-06). ══
    //
    /// <summary>
    /// No eval the CI chain declares <c>NeedsModel: false</c> may be handed <c>--dry-run</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect, measured before the fix.</b> <c>--ci --dry-run</c> — the form
    /// <c>Program.cs</c> itself recommends CI use, and the form every wave of this plan has run —
    /// printed <i>"Eval 07: passed."</i> and returned <b>exit 0</b>, while <c>-- 7</c>, the
    /// identical measurement on the identical tree, returned <b>exit 1</b> with <b>GATE B ❌</b>.
    /// Eval 07 makes no model call on any path, so <c>--dry-run</c> had nothing to stub: its dry-run
    /// form runs ONE of five cases and asserts only the plumbing. The chain passed
    /// <c>parsed.DryRun</c> straight through, so the suite's only currently-failing gate was
    /// invisible to the suite's own CI command.
    /// </para>
    /// <para>
    /// <b>Why that is worse than an ordinary false green.</b> <c>RunCiAsync</c>'s own header
    /// justifies putting Eval 07 in the chain with the sentence <i>"an eval that is not in the chain
    /// has its failures reported nowhere at all"</i> — and under the recommended invocation its
    /// failures were reported nowhere at all. The same argument had already been settled for Evals
    /// 03 and 04 (RUN_PROTOCOL, plan item 8.19): a model-free eval gets no <c>dryRun</c> parameter,
    /// because replacing a real free measurement with a stubbed copy of itself makes the cheapest
    /// honest measurement in the suite worse in order to make a sentence true. Eval 07 is the third
    /// model-free eval and it was the exception nobody had noticed.
    /// </para>
    /// <para>
    /// <b>What this row does NOT do.</b> It does not forbid Eval 07's dry-run form. Run by hand,
    /// <c>-- 7 --dry-run</c> is a fast, loud plumbing check that can and does fail, and its header
    /// says so. What is forbidden is the CHAIN choosing it.
    /// </para>
    /// <para>
    /// It reads <c>Program.cs</c> rather than reasoning about it — the same technique
    /// <c>EveryEvalDeclaresItsSnapshotPolicy</c> and <c>EveryControlRowIsContained</c> use — so a
    /// future edit that re-introduces the pass-through turns this row red instead of turning a gate
    /// green.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckCiChainRunsModelFreeEvalsForReal()
    {
        var problems = new List<string>();
        string source = File.ReadAllText(Path.Combine(SampleSourceRoot(), "Program.cs"));

        // The CI chain's step table: `new("Eval NN", "…", NeedsModel: X, Slow: Y, () => …)`.
        var steps = System.Text.RegularExpressions.Regex.Matches(
            source,
            @"new\(""(?<name>Eval [0-9a-c]+)"",[^)]*?NeedsModel:\s*(?<needs>true|false),\s*Slow:\s*(?:true|false),\s*\(\)\s*=>\s*(?<body>[^;]*?)\)\),",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        if (steps.Count < 11)
        {
            problems.Add($"only {steps.Count} CI step(s) were recognised in Program.cs — the scan is not reading the "
                       + "chain it thinks it is, so its verdict means nothing.");
        }

        var modelFree = new List<string>();
        var stubbed = new List<string>();

        foreach (System.Text.RegularExpressions.Match step in steps)
        {
            if (step.Groups["needs"].Value != "false") continue;
            string name = step.Groups["name"].Value;
            modelFree.Add(name);

            // `dryRun: parsed.DryRun` — or any variable at all — is the defect. `dryRun: false` is
            // the fix, and no `dryRun:` argument (Evals 03 and 04) is stronger still.
            var arg = System.Text.RegularExpressions.Regex.Match(step.Groups["body"].Value, @"dryRun:\s*(?<v>[A-Za-z0-9_.]+)");
            if (arg.Success && arg.Groups["v"].Value != "false")
                stubbed.Add($"{name} is driven with dryRun: {arg.Groups["v"].Value}");
        }

        if (modelFree.Count < 3)
        {
            problems.Add($"the chain declares only {modelFree.Count} model-free eval(s) — Evals 03, 04 and 07 all call "
                       + "no model, so a smaller number means a declaration went stale.");
        }

        foreach (string s in stubbed)
        {
            problems.Add($"{s} — a model-free eval has nothing to stub, so this replaces a real free measurement with a "
                       + "partial copy of itself and can only turn a red gate green in CI.");
        }

        return new ControlRowSnapshot(
            "CiChainRunsModelFreeEvalsForReal",
            "no eval the CI chain declares NeedsModel: false may be handed --dry-run. Such an eval has nothing to "
          + "stub, so a dry-run form of it is a partial copy that can only lose failures. MEASURED before the fix: "
          + "`--ci --dry-run` printed \"Eval 07: passed.\" and exited 0 while `-- 7` — the identical free measurement "
          + "on the identical tree — exited 1 with GATE B failing, because Eval 07's dry run scores ONE of five cases. "
          + "RunCiAsync's own header puts Eval 07 in the chain so that \"an eval that is not in the chain has its "
          + "failures reported nowhere at all\", and under the recommended invocation they were reported nowhere at "
          + "all. Evals 03 and 04 settled this in plan item 8.19 by taking no dryRun parameter; Eval 07 keeps its "
          + "hand-run plumbing form and the CHAIN passes dryRun: false",
            problems.Count == 0
                ? $"{steps.Count} CI step(s) scanned in Program.cs · model-free: {string.Join(", ", modelFree)} · "
                + "none of them is driven with a dryRun variable · ⚠ CONSEQUENCE, DECLARED: `--ci --dry-run` now "
                + "returns 1, not 0, because Eval 07's GATE B is genuinely red — the exit code moved, the system "
                + "did not"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 22 — the refusal detectors could not fire on the live shape (plan items 8.14/8.7). ══
    //
    /// <summary>
    /// The tool-layer refusal detectors must read the shape the LIVE harness records, not the shape
    /// a hand-built control produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> <c>Eval01.DetectOptOutBackstop</c> and <c>Eval06.HasBudgetRefusal</c> both
    /// tested <c>call.Result is string json</c>. <c>AIFunctionFactory.Create</c> marshals a
    /// <c>Task&lt;string&gt;</c> tool's return value through <c>JsonSerializer</c>, so the object
    /// that reaches <c>FunctionResultContent.Result</c> — and from there
    /// <c>ToolCallRecord.Result</c> — is a <c>JsonElement</c>. Neither detector could return true on
    /// a live turn, ever. Eval 01 printed <i>"the tool-layer backstop was never exercised this
    /// turn"</i> on the 2026-09-05 opt-out case and <c>SUITE_SUMMARY</c> §4 left it open as
    /// <i>"either a containment hole or a blind detector"</i>. This row settles it as the blind
    /// detector, and it settles it by RUNNING the marshalling rather than by reasoning about it.
    /// </para>
    /// <para>
    /// ⚠ <b>This is why the row invokes the real AIFunction.</b> Every scripted control in this
    /// panel builds its <c>FunctionResultContent</c> by hand, and a hand-built result is a
    /// <c>string</c> — so the stub was kinder than the model in exactly the sense
    /// <c>RUN_PROTOCOL.md</c> names, and no control built that way could have caught this. It costs
    /// nothing: the tool is deterministic, reads the in-memory catalogue and calls no model.
    /// </para>
    /// <para>
    /// <b>Both directions.</b> The refusal must be FOUND in the marshalled shape, an ordinary
    /// successful result must NOT be mistaken for a refusal, and the detector must not fall back to
    /// matching the ARGUMENTS — a refusal code echoed into a query is not the architecture refusing
    /// anything.
    /// </para>
    /// </remarks>
    private static async Task<ControlRowSnapshot> CheckRefusalDetectorsSeeTheRealShapeAsync()
    {
        var problems = new List<string>();
        string shape;

        var opted = UserProfiles.Require(Personas.NadiaUserId).WithPersonalization(false);
        GalaxusTools.ClearProfileOverrides();
        GalaxusTools.OverrideProfile(opted);

        object? refusal;
        object? ordinary;
        try
        {
            // The REAL function object the agent is built from — RecommendationAgentFactory.cs:148
            // creates it exactly this way.
            var interestMap = AIFunctionFactory.Create(GalaxusTools.GetInterestMap);
            refusal = await interestMap.InvokeAsync(new AIFunctionArguments { ["userId"] = opted.Id })
                                       .ConfigureAwait(false);

            GalaxusTools.ClearProfileOverrides();
            ordinary = await interestMap.InvokeAsync(new AIFunctionArguments { ["userId"] = Personas.NadiaUserId })
                                        .ConfigureAwait(false);
        }
        finally
        {
            GalaxusTools.ClearProfileOverrides();
        }

        shape = refusal?.GetType().Name ?? "(null)";

        // ── 1. The shape itself. If this ever becomes a string the detectors are no longer being
        //       tested against anything, and the row must say so rather than quietly passing. ──
        if (refusal is string)
        {
            problems.Add("the marshalled tool result is a string — the shape this row exists to pin has changed, so "
                       + "the assertions below no longer distinguish the fixed detector from the broken one.");
        }

        // ── 2. The OLD detector, spelled out here, must FAIL on that shape. This is the row's
        //       negative control: it proves the new one is doing work rather than agreeing. ──
        static bool OldDetector(object? result, string code) =>
            result is string json && json.Contains(code, StringComparison.Ordinal);

        if (OldDetector(refusal, ToolRefusalCodes.PersonalizationDisabled))
        {
            problems.Add("the `Result is string` test SUCCEEDS on the marshalled refusal — this row is not exercising "
                       + "the defect it was written for.");
        }

        // ── 3. The shipped detector must find it. ──
        string rendered = ToolResultText.Of(refusal);
        if (!rendered.Contains(ToolRefusalCodes.PersonalizationDisabled, StringComparison.Ordinal))
        {
            problems.Add($"the opt-out refusal is invisible to the detector on a {shape} result — it rendered "
                       + $"\"{Shorten(rendered, 60)}\". Eval 01 would print \"the tool-layer backstop was never "
                       + "exercised this turn\" for a refusal that did fire.");
        }

        // ── 4. …and must not report a refusal for a result that is not one. ──
        string ordinaryText = ToolResultText.Of(ordinary);
        if (ordinaryText.Contains(ToolRefusalCodes.PersonalizationDisabled, StringComparison.Ordinal))
            problems.Add("an ORDINARY interest-map result reads as a refusal — the detector says yes to everything.");
        if (ordinaryText.Length == 0)
            problems.Add("an ordinary result rendered to nothing, so the negative direction above is vacuous.");

        // ── 5. Through the trace-level API, on records shaped like the live ones, both ways. ──
        static ToolUsageReport Trace(params ToolCallRecord[] calls)
        {
            var report = new ToolUsageReport();
            foreach (var call in calls) report.AddCall(call);
            return report;
        }

        static ToolCallRecord Call(string name, object? result, IDictionary<string, object?>? arguments = null) =>
            new() { Name = name, CallId = $"call-{name}", Arguments = arguments, Result = result, WasExecuted = true };

        var live = Trace(
            Call(nameof(GalaxusTools.GetUserProfile), ordinary),
            Call(nameof(GalaxusTools.GetInterestMap), refusal));

        if (!ToolResultText.AnyResultContains(live, ToolRefusalCodes.PersonalizationDisabled))
            problems.Add("AnyResultContains missed the refusal in a two-call trace shaped like a live one.");

        var clean = Trace(Call(nameof(GalaxusTools.GetInterestMap), ordinary));
        if (ToolResultText.AnyResultContains(clean, ToolRefusalCodes.PersonalizationDisabled))
            problems.Add("AnyResultContains reported a refusal in a trace that contains none.");

        // ── 6. RESULTS only. A refusal code the model echoed into a QUERY is not the architecture
        //       refusing anything, and counting it would let the agent trip its own backstop. ──
        var echoed = Trace(Call(
            nameof(GalaxusTools.SearchProductsByMeaning),
            ordinary,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = ToolRefusalCodes.PersonalizationDisabled }));
        if (ToolResultText.AnyResultContains(echoed, ToolRefusalCodes.PersonalizationDisabled))
        {
            problems.Add("a refusal code echoed into a tool ARGUMENT was counted as the tool having refused — the "
                       + "detector reads the agent's own text as evidence about the architecture.");
        }

        // ── 7. The list the report uses to say whether the backstop was TEMPTED must be derived
        //       from the tools' behaviour, IN BOTH DIRECTIONS. "Never fired" was one sentence for
        //       two opposite findings — an agent that never asked, and an architecture that failed
        //       to refuse one that did — and it was printed for a live turn in which the agent DID
        //       call GetInterestMap. The list decides which of the two a reader is told.
        //
        //  ⚠ MEMBERSHIP MUST EQUAL BEHAVIOUR, not imply it. A first version asserted only "every
        //    NAMED tool refuses", and deleting GetInterestMap from the list left this row GREEN:
        //    a shrunk list passes vacuously, and the report would then have called the exact live
        //    turn that started all this "never tempted". Every user-keyed structured tool is
        //    invoked and membership is asserted to EQUAL refusal.
        (string Name, Func<string, ValueTask<object?>> Invoke)[] userKeyedTools =
        [
            (nameof(GalaxusTools.GetUserProfile),
                async id => await AIFunctionFactory.Create(GalaxusTools.GetUserProfile)
                    .InvokeAsync(new AIFunctionArguments { ["userId"] = id }).ConfigureAwait(false)),
            (nameof(GalaxusTools.GetPurchaseHistory),
                async id => await AIFunctionFactory.Create(GalaxusTools.GetPurchaseHistory)
                    .InvokeAsync(new AIFunctionArguments { ["userId"] = id }).ConfigureAwait(false)),
            (nameof(GalaxusTools.GetInterestMap),
                async id => await AIFunctionFactory.Create(GalaxusTools.GetInterestMap)
                    .InvokeAsync(new AIFunctionArguments { ["userId"] = id }).ConfigureAwait(false)),
        ];

        // ⚠ AND THE ROW'S OWN LIST IS DERIVED-CHECKED, one level down from the hazard above. The
        //   list immediately above is hand-written, so a user-keyed tool added later that refuses
        //   under the opt-out and is on NEITHER list would leave this row green — the same
        //   "a shrunk list passes vacuously" shape the row exists to close for
        //   BehaviouralHistoryToolNames. The set of tools that TAKE a userId is a fact about the
        //   tool surface, so it is read off the surface rather than restated here.
        string[] derivedUserKeyed =
        [
            .. typeof(GalaxusTools)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.GetParameters().Any(p => string.Equals(p.Name, "userId", StringComparison.Ordinal)))
                .Select(m => m.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];

        string[] exercised = [.. userKeyedTools.Select(t => t.Name).Order(StringComparer.Ordinal)];

        if (derivedUserKeyed.Length == 0)
            problems.Add("no tool on GalaxusTools takes a 'userId' — the derivation below is asserting nothing.");
        if (!derivedUserKeyed.SequenceEqual(exercised, StringComparer.Ordinal))
        {
            problems.Add($"this row invokes [{string.Join(", ", exercised)}] and the tool surface declares user-keyed "
                       + $"[{string.Join(", ", derivedUserKeyed)}] — a tool that takes a customer id and is not "
                       + "exercised here can refuse, or fail to, without this row noticing.");
        }

        int refusing = 0;
        GalaxusTools.OverrideProfile(opted);
        try
        {
            foreach (var (name, invoke) in userKeyedTools)
            {
                bool refuses = ToolResultText.Of(await invoke(opted.Id).ConfigureAwait(false))
                    .Contains(ToolRefusalCodes.PersonalizationDisabled, StringComparison.Ordinal);
                bool listed = ToolSurfaceInvariant.BehaviouralHistoryToolNames
                    .Contains(name, StringComparer.OrdinalIgnoreCase);

                if (refuses) refusing++;

                if (refuses && !listed)
                {
                    problems.Add($"'{name}' REFUSES under the opt-out and is not on BehaviouralHistoryToolNames — the "
                               + "report would tell a reader the backstop was never tempted on a turn that called it.");
                }
                else if (!refuses && listed)
                {
                    problems.Add($"'{name}' is named as forbidden under the opt-out and did NOT refuse — the list is a "
                               + "claim the tools do not honour.");
                }
            }
        }
        finally
        {
            GalaxusTools.ClearProfileOverrides();
        }

        // Both directions have to be non-vacuous: at least one tool must refuse and at least one
        // must not, or the equality above is satisfied by an empty side.
        if (refusing == 0)
            problems.Add("no user-keyed tool refused under the opt-out at all — the containment is gone, or this row is testing nothing.");
        if (refusing == userKeyedTools.Length)
            problems.Add("every user-keyed tool refused, GetUserProfile included — the opt-out now hides the customer's own identity, not just their behaviour.");

        return new ControlRowSnapshot(
            "RefusalDetectorsSeeTheRealShape",
            "the two detectors that report whether the TOOL LAYER refused — Eval 01's opt-out backstop and Eval 06's "
          + "budget refusal — must read the result shape a live harness records. Both tested `Result is string`, and "
          + "AIFunctionFactory marshals a tool's return value into a JsonElement, so both had a chance floor of ZERO on "
          + "the only path that matters: Eval 01 printed \"the tool-layer backstop was never exercised this turn\" for a "
          + "refusal that had fired, and SUITE_SUMMARY §4 could not say whether that was a containment hole or a blind "
          + "detector. This row invokes the REAL AIFunction, because every hand-built control result is a string and no "
          + "scripted control could have caught it.",
            problems.Count == 0
                ? $"the marshalled refusal arrives as {shape}, so `Result is string` is FALSE on it (the old detector "
                + $"cannot fire) · the shipped detector finds '{ToolRefusalCodes.PersonalizationDisabled}' in it · an "
                + "ordinary interest map does NOT read as a refusal · the trace-level API answers both ways on "
                + "live-shaped records · a refusal code echoed into an ARGUMENT is not counted"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 23 — one refusal code must not answer to another's name (found LIVE, 2026-09-06). ══
    //
    /// <summary>
    /// A tool-result detector asked for refusal code A must not fire on a payload whose declared
    /// code is B.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>The defect, MEASURED on the live Eval 06 run of 2026-09-06 and reproduced on a
    /// second.</b> <c>ToolJson.SearchCapExhausted</c> serialises <c>status = "budget_exhausted"</c>
    /// beside <c>code = "search_cap_exhausted"</c>. The budget detector was a bare substring match,
    /// so case T-03 — which spent <b>16 of its 24</b> refusable calls and hit the DISTINCT-SEARCH
    /// cap three times at 8/8 — failed the claim <i>"the turn stayed inside its 24-call budget"</i>
    /// with the message <i>"the turn asked for more calls than its budget allowed"</i>, printed
    /// beside its own <c>budget 16/24 ⚠ OVERRUN</c>. Two numbers on one line that contradict each
    /// other; a reader cannot tell which is the measurement, and the persisted
    /// <c>eval06_trajectory.json</c> recorded <c>BudgetOverrun: true</c> for a turn that did not
    /// overrun.
    /// </para>
    /// <para>
    /// ⚠ <b>This became reachable only because the detector stopped being blind.</b> The previous
    /// version tested <c>Result is string</c>, false on every marshalled result, so the budget
    /// claim passed VACUOUSLY on every case of every run ever made. Plan item 8.14 fixed that; this
    /// row closes what the fix then exposed. Two extremes in sequence — a claim that could never
    /// fail, then one that failed for the wrong reason — and neither was visible from a dry run,
    /// because both stubs return hand-built strings that carry exactly one code.
    /// </para>
    /// <para>
    /// <b>Derived, not restated, and BOTH directions.</b> The codes are read off
    /// <c>ToolRefusalCodes</c> by reflection, and the payload for each is produced by the tool
    /// layer's own serialiser rather than written here — so a code added later, or a payload whose
    /// fields change, is covered without editing this row. Every ordered pair of distinct codes is
    /// checked for a false positive AND every code is checked against its own payload for a false
    /// negative: a matcher that answered <see langword="false"/> to everything would pass the first
    /// half alone.
    /// </para>
    /// <para>
    /// <b>The live shape, not a string.</b> Each payload is round-tripped into a
    /// <see cref="JsonElement"/> first, because that is what <c>AIFunctionFactory</c> hands the
    /// harness and a control that tested the string would be the stub-kinder-than-reality shape
    /// <c>RUN_PROTOCOL.md</c> names.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckRefusalCodesDoNotAnswerForEachOther()
    {
        var problems = new List<string>();

        // ── The codes, off the tool surface. A code that exists and is not listed here cannot be
        //    covered by a row that restates the list. ──
        string[] codes =
        [
            .. typeof(ToolRefusalCodes)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];

        if (codes.Length < 2)
        {
            problems.Add($"ToolRefusalCodes yielded {codes.Length} code(s) — the cross-check below is vacuous.");
        }

        // ── The payloads, from the tool layer's own producers. The two that do NOT go through
        //    ToolJson.Refused are the two whose `status` collides, which is the whole point. ──
        static string PayloadFor(string code) => code switch
        {
            ToolRefusalCodes.BudgetExhausted => ToolJson.BudgetExhausted(24, 24),
            ToolRefusalCodes.SearchCapExhausted => ToolJson.SearchCapExhausted(8, 8),
            ToolRefusalCodes.AlreadyReturned => ToolJson.AlreadyReturned("SearchProductsByMeaning", 3, ["GLX-1001"]),
            _ => ToolJson.Refused(code, "control payload — the reason text is not what any detector reads."),
        };

        // The shape a live harness records: AIFunctionFactory marshals a Task<string> through
        // JsonSerializer, so the string arrives as a JsonElement holding a JSON string.
        static object Marshalled(string payload) =>
            JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload));

        static ToolUsageReport TraceOf(object result)
        {
            var report = new ToolUsageReport();
            report.AddCall(new ToolCallRecord
            {
                Name = nameof(GalaxusTools.SearchProductsByMeaning),
                CallId = "call-refusal",
                Result = result,
                WasExecuted = true,
            });
            return report;
        }

        // ── 1. NON-VACUITY, in the row's own terms. If no payload ever collides loosely any more,
        //       the defect this row was written for is gone and the assertions below stop
        //       distinguishing the fixed detector from the broken one. Say so; do not pass. ──
        int looseCollisions = 0;
        foreach (string declared in codes)
        {
            var trace = TraceOf(Marshalled(PayloadFor(declared)));
            foreach (string probe in codes)
            {
                if (string.Equals(declared, probe, StringComparison.Ordinal)) continue;
                if (ToolResultText.AnyResultContains(trace, probe)) looseCollisions++;
            }
        }

        if (looseCollisions == 0)
        {
            problems.Add("no refusal payload collides under the LOOSE matcher any more — this row is no longer "
                       + "exercising the defect it was written for, so its green result would mean nothing. "
                       + "Re-derive it against whatever the payloads now share, or retire it deliberately.");
        }

        // ── 2. FALSE POSITIVES — the direction that failed T-03. ──
        foreach (string declared in codes)
        {
            string payload = PayloadFor(declared);
            var trace = TraceOf(Marshalled(payload));

            string? read = ToolResultText.RefusalCodeOf(Marshalled(payload));
            if (!string.Equals(read, declared, StringComparison.Ordinal))
            {
                problems.Add($"the payload for '{declared}' declares code '{read ?? "(none)"}' — the producer and the "
                           + "reader disagree, so every verdict below is about the wrong thing.");
            }

            foreach (string probe in codes)
            {
                if (string.Equals(declared, probe, StringComparison.Ordinal)) continue;

                if (ToolResultText.AnyResultHasRefusalCode(trace, probe))
                {
                    problems.Add($"a '{declared}' refusal answers to the name '{probe}'. That is how Eval 06 failed "
                               + "T-03 for a 24-call budget it never reached: 16 of 24 spent, three "
                               + "distinct-search-cap refusals, and a claim that named the wrong cap.");
                }
            }
        }

        // ── 3. FALSE NEGATIVES — or a matcher that says no to everything would pass part 2. ──
        int found = 0;
        foreach (string declared in codes)
        {
            var trace = TraceOf(Marshalled(PayloadFor(declared)));
            if (ToolResultText.AnyResultHasRefusalCode(trace, declared)) found++;
            else problems.Add($"the shipped detector cannot find '{declared}' in its own payload — it is blind, not precise.");
        }

        if (found == 0)
            problems.Add("no code was found in its own payload at all; part 2 above passed vacuously.");

        // ── 4. The two detectors that ship must be the precise one. A row that proved the matcher
        //       correct while Eval 01 and Eval 06 called the loose one would be testing a function
        //       nothing uses — the shape §7 rule 6 flags.
        foreach ((string file, string member) in new[]
                 {
                     ("Evals/Eval01_CatalogueIntegrity.cs", "DetectOptOutBackstop"),
                     ("Evals/Eval06_ToolTrajectory.cs", "HasBudgetRefusal"),
                 })
        {
            string source = File.ReadAllText(Path.Combine(SampleSourceRoot(), file));
            int at = source.IndexOf(member + "(ToolUsageReport", StringComparison.Ordinal);
            if (at < 0)
            {
                problems.Add($"{member} was not found in {file} — this clause is asserting nothing.");
                continue;
            }

            string body = source[at..Math.Min(source.Length, at + 240)];
            if (!body.Contains(nameof(ToolResultText.AnyResultHasRefusalCode), StringComparison.Ordinal))
            {
                problems.Add($"{file}'s {member} does not read the declared code — it is still on the loose matcher, "
                           + "so this row proves a function that ships nowhere.");
            }
        }

        return new ControlRowSnapshot(
            "RefusalCodesDoNotAnswerForEachOther",
            "a detector asked whether the tool layer returned refusal code A must not fire on a payload whose declared "
          + "code is B. MEASURED live: ToolJson.SearchCapExhausted carries status = \"budget_exhausted\" with code = "
          + "\"search_cap_exhausted\", so Eval 06 failed T-03 for a 24-call budget it never reached — 16 of 24 spent, "
          + "three distinct-search-cap refusals at 8/8 — and persisted BudgetOverrun: true for a turn that did not "
          + "overrun. Reachable only after 8.14 stopped the detector being blind, and invisible to a dry run, whose "
          + "stubbed results carry one code each.",
            problems.Count == 0
                ? $"{codes.Length} code(s) derived from ToolRefusalCodes · {looseCollisions} loose collision(s) still "
                + $"present in the payloads, so the row is not vacuous · 0 cross-matches under the shipped detector · "
                + $"{found} of {codes.Length} code(s) found in their own payload · both shipped detectors read the "
                + "declared code"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 24 — the run must be able to say what it wrote (plan item 8.19). ═══════════
    //
    /// <summary>
    /// The write ledger the <c>--ci --dry-run</c> banner reports must be driven by the write path
    /// itself, and must agree with the files on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> The banner printed <i>"no model was called and no snapshot was
    /// written"</i> unconditionally. Evals 03 and 04 call no model, so the CI chain hands them no
    /// <c>dryRun</c> argument, so they run for real inside a dry run and persist — MEASURED,
    /// <c>eval03_controls</c> and <c>eval04_injection</c> moved at 01:26:14 inside a dry run that
    /// ran 01:26:12–01:26:19, and then did it a second time inside the run that wrote §24.7 up.
    /// The writes are correct; the claim was the defect.
    /// </para>
    /// <para>
    /// <b>What this row pins, and what it deliberately does not.</b> It cannot observe a CI chain
    /// from inside one eval. What it can do — and what the banner's honesty actually rests on — is
    /// prove the ledger is WIRED to the write path: a key that was written appears, a key that was
    /// not does not, and the file the ledger names is on disk with a fresh timestamp. A banner
    /// reading a hand-maintained list would be a second claim about the code; §2.4 records that the
    /// last enumerated call-site list in this programme was wrong by 20 %.
    /// </para>
    /// <para>
    /// ⚠ The probe writes and then DELETES its own snapshot. A number in a shared store that no
    /// gate reads is the hazard Eval 08 states in code as its reason for persisting nothing.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckWriteLedgerMatchesTheStore()
    {
        var problems = new List<string>();
        const string probeKey = "eval03_writeledger_probe";
        string probePath = Path.Combine(EvalResultStore.StorageLocation, $"{probeKey}.json");

        var before = EvalResultStore.KeysWrittenThisRun;
        if (before.Contains(probeKey, StringComparer.Ordinal))
            problems.Add($"'{probeKey}' was already in the ledger before this row wrote anything — the probe is not isolated.");

        var beforeWrite = DateTime.UtcNow.AddSeconds(-2);

        try
        {
            EvalResultStore.SaveControls(probeKey, new ControlSnapshot
            {
                Label = "write-ledger probe — deleted immediately, never a record of anything",
                Controls = [],
                AllControlsTripped = true,
            });

            var after = EvalResultStore.KeysWrittenThisRun;

            // ── 1. The ledger saw it. ──
            if (!after.Contains(probeKey, StringComparer.Ordinal))
            {
                problems.Add($"a snapshot was written and '{probeKey}' is NOT in KeysWrittenThisRun — the banner would "
                           + "print \"no snapshot was written\" over a store that had just moved, which is the exact "
                           + "sentence 8.19 exists to remove.");
            }

            // ── 2. …and it did not invent anything else. ──
            if (after.Count != before.Count + 1)
            {
                problems.Add($"the ledger went from {before.Count} to {after.Count} key(s) on ONE write — it is counting "
                           + "something other than writes, so the banner's list is not the run's list.");
            }

            if (after.Contains("eval99_never_written", StringComparer.Ordinal))
                problems.Add("the ledger contains a key nothing ever wrote.");

            // ── 2b. …and while the probe file exists, the BANNER's view must see it too. This is
            //        the property that decides what a reader is told, and it is a different list. ──
            if (!EvalResultStore.SnapshotsWrittenThisRun.Contains(probeKey, StringComparer.Ordinal))
                problems.Add("the key is in the ledger and not in SnapshotsWrittenThisRun while its file is on disk — the banner reads the wrong list.");

            // ── 3. The file the ledger names is really there, and it is THIS run's. Asserting the
            //       ledger alone would certify the ledger; the banner's reader will go and look. ──
            if (!File.Exists(probePath))
            {
                problems.Add("the ledger recorded a write and no file landed — the ledger is ahead of the store.");
            }
            else if (File.GetLastWriteTimeUtc(probePath) < beforeWrite)
            {
                problems.Add("the file the ledger names is older than this row — the ledger is reporting somebody "
                           + "else's write as ours.");
            }

            // ── 4. BOTH chokepoints. Eval 02b and 02c persist through OfflineSnapshotStore, not
            //       through EvalResultStore, and a store that writes without recording would put
            //       the banner straight back where it was. Verified by reflection rather than by
            //       calling it, because calling it would write a second file into a shared store. ──
            var save = typeof(OfflineSnapshotStore).GetMethod(
                nameof(OfflineSnapshotStore.Save), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (save is null)
            {
                problems.Add("OfflineSnapshotStore.Save was not found — the second write chokepoint has moved and this row is stale.");
            }
            else
            {
                string body = File.ReadAllText(Path.Combine(SampleSourceRoot(), "OfflineSnapshotStore.cs"));
                if (!body.Contains("EvalResultStore.RecordWrite", StringComparison.Ordinal))
                {
                    problems.Add("OfflineSnapshotStore writes snapshots and does not record them in the ledger — "
                               + "Evals 02b and 02c would persist invisibly to the banner.");
                }
            }
        }
        finally
        {
            // Written to prove the wiring, never kept: a snapshot no gate reads is a hazard.
            if (File.Exists(probePath)) File.Delete(probePath);
        }

        if (File.Exists(probePath))
            problems.Add("the probe snapshot is still on disk — this row leaves a record of nothing in a shared store.");

        // ── 5. And once the file is gone the BANNER must stop naming it, or the sentence that
        //       replaced "no snapshot was written" points a reader at a file that is not there. ──
        if (EvalResultStore.SnapshotsWrittenThisRun.Contains(probeKey, StringComparer.Ordinal))
        {
            problems.Add("the banner still names the probe after its file was deleted — it would send a reader to look "
                       + "for a snapshot that does not exist.");
        }
        if (!EvalResultStore.KeysWrittenThisRun.Contains(probeKey, StringComparer.Ordinal))
            problems.Add("the raw ledger forgot a write — deleting a file must not erase the record that a write happened.");

        return new ControlRowSnapshot(
            "WriteLedgerMatchesTheStore",
            "a run has to be able to say what it wrote. The `--ci --dry-run` banner printed \"no model was called and no "
          + "snapshot was written\" unconditionally, and Evals 03 and 04 — which call no model, so the chain passes them "
          + "no --dry-run argument — persisted inside every one of those runs. The writes are correct and stay; the "
          + "banner now reports EvalResultStore.KeysWrittenThisRun. That ledger must be driven by the write path itself "
          + "(both chokepoints), must not invent keys, and must name a file that is really on disk and really this run's.",
            problems.Count == 0
                ? $"one probe write → the key appears in the ledger, the ledger grew by exactly 1 (from {before.Count}), "
                + "the named file was on disk with this run's timestamp, both write chokepoints record, and the probe "
                + "snapshot was deleted rather than left in the store"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    /// <summary>
    /// The <c>samples/Galaxus.RecommendationAgent.Evals</c> source directory, found by walking up
    /// from the build output to the solution root.
    /// </summary>
    /// <remarks>
    /// Source-reading controls exist here already (the meta-lane grep gate in <c>src/</c>); the
    /// thing that made that one honest was asserting something about its own INPUT, so this one
    /// throws rather than returning a path whose files are absent.
    /// </remarks>
    private static string SampleSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0)
            {
                string root = Path.Combine(dir.FullName, "samples", "Galaxus.RecommendationAgent.Evals");
                if (File.Exists(Path.Combine(root, "OfflineSnapshotStore.cs"))) return root;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "the eval project's source directory was not found from " + AppContext.BaseDirectory);
    }

    // ══ Control 25 — silence about persistence is the defect (plan item 8.20). ═══════════
    //
    /// <summary>
    /// Every eval must DECLARE whether it persists a snapshot, and the declaration must match what
    /// the file actually does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> Evals 05 and 06 persisted nothing and said nothing about it. Eval 08 also
    /// persists nothing and states its reason in code — nothing consumes a stability snapshot, and a
    /// number in a shared store that no gate reads is a hazard. Two silences that look identical
    /// from outside, one deliberate and two not, and no way to tell which is which without reading
    /// three files. <b>The silence was the thing to fix, not the absence of a file.</b>
    /// </para>
    /// <para>
    /// <b>Membership must EQUAL behaviour.</b> A control asserting only "every eval declares
    /// something" would pass on a file declaring <c>writes</c> and writing nothing — which is the
    /// original defect wearing a comment. The declared policy is compared against whether the file
    /// actually calls a store, in both directions.
    /// </para>
    /// <para>
    /// ⚠ <b>It asserts its own INPUT.</b> A source scan that finds no offenders is
    /// indistinguishable from a source scan that found no files — the silent-<c>{}</c> shape this
    /// repository has a standing rule about, and the exact defect <c>8f3e11c7</c> fixed in the
    /// meta-lane grep gate. This row fails if it scanned too few files, if nothing declares
    /// <c>writes</c>, or if nothing declares <c>deliberately-none</c>.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckEveryEvalDeclaresItsSnapshotPolicy()
    {
        var problems = new List<string>();
        const string marker = "// SNAPSHOT-POLICY:";
        const int minimumFilesScanned = 10;

        string evalsDir = Path.Combine(SampleSourceRoot(), "Evals");
        var files = Directory.GetFiles(evalsDir, "Eval*.cs")
            .Concat(Directory.GetFiles(evalsDir, "NegativeControls.cs"))
            .Where(f => !Path.GetFileName(f).Equals("EvalPanel.cs", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        int writes = 0;
        int none = 0;

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            string body = File.ReadAllText(file);

            string? declaration = body.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith(marker, StringComparison.Ordinal));

            if (declaration is null)
            {
                problems.Add($"{name} declares no SNAPSHOT-POLICY — whether it persists is knowable only by reading it.");
                continue;
            }

            string value = declaration[marker.Length..].Trim();
            bool declaresWrites = value.StartsWith("writes", StringComparison.Ordinal);
            bool declaresNone = value.StartsWith("deliberately-none", StringComparison.Ordinal);

            if (!declaresWrites && !declaresNone)
            {
                problems.Add($"{name}'s SNAPSHOT-POLICY is neither 'writes' nor 'deliberately-none': \"{Shorten(value, 40)}\".");
                continue;
            }

            // A "deliberately-none" with no reason after it is the silence again, spelled as a tag.
            if (declaresNone && value.Length <= "deliberately-none".Length + 20)
                problems.Add($"{name} declares deliberately-none and gives no reason — that is the silence 8.20 exists to remove.");

            bool actuallyWrites = body.Contains("EvalResultStore.Save", StringComparison.Ordinal)
                               || body.Contains("OfflineSnapshotStore.Save", StringComparison.Ordinal);

            if (declaresWrites && !actuallyWrites)
                problems.Add($"{name} declares it writes a snapshot and calls no store — a comment is not a record.");
            if (declaresNone && actuallyWrites)
                problems.Add($"{name} declares deliberately-none and calls a store — the declaration is stale.");

            // ⚠ AND WHAT THE RUN PRINTS MUST NOT CONTRADICT WHAT IT WROTE (Wave 2 review).
            //   Eval 06's live gate printed "Eval 06 writes no snapshot: … an unread result file is
            //   a liability" and then, three lines later, "📁 Snapshot saved". MEASURED on the live
            //   run of 2026-09-06 01:20:57Z. The sentence was true when it was written; item 8.20
            //   made it false and did not come back for it — 8.19's defect exactly, one file over,
            //   reintroduced by the fix for the item beside it.
            //
            //   The rule, and its LIMIT stated rather than implied: in a file that declares it
            //   writes, a PRINTED denial of persistence must be attributable to a dry run — the
            //   words "dry run" in the same printed literal or in the six lines either side of it.
            //   `if (dryRun) return;` deliberately does NOT count: it is what made Eval 06's
            //   sentence live-only, so accepting it would accept the defect. This cannot see a
            //   denial phrased in words it does not know; it can see every one in the suite today.
            if (declaresWrites)
            {
                string[] lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                string[] denials = ["no snapshot", "writes no snapshot", "nothing written", "not persist"];

                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains("Console.WriteLine", StringComparison.Ordinal)) continue;
                    if (!denials.Any(d => lines[i].Contains(d, StringComparison.OrdinalIgnoreCase))) continue;

                    int from = Math.Max(0, i - 6);
                    int to = Math.Min(lines.Length, i + 7);
                    string window = string.Join(' ', lines[from..to]);

                    if (!window.Contains("dry run", StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{name} declares it WRITES and prints a denial of it that no dry run explains, "
                                   + $"at line {i + 1}: \"{Shorten(lines[i].Trim(), 80)}\" — a run that tells the "
                                   + "reader it left nothing behind and then leaves a file is 8.19 with a different "
                                   + "sentence.");
                    }
                }
            }

            if (declaresWrites) writes++; else none++;
        }

        // ── The two NEW records must survive the round trip, with the AWKWARD values the live
        //    path can actually produce. Neither write is reachable from a dry run — both sit on the
        //    live branch — so RUN_PROTOCOL's stage 1 is structurally blind to them, and a record
        //    that throws or silently loses a field would first be discovered on a paid run.
        //
        //    ⚠ NaN IS THE AWKWARD VALUE HERE, not a null. It is how this suite spells an EMPTY
        //      DENOMINATOR, EvalResultStore's serialiser is configured for it on purpose, and a
        //      store that turned it into 0 would turn "we could not score this" into "it scored
        //      zero" — the flattering direction. A probe carrying only plausible values would be
        //      the stub-kinder-than-reality shape RUN_PROTOCOL names.
        const string qualityProbe = "eval05_quality_probe";
        const string trajectoryProbe = "eval06_trajectory_probe";
        string qualityPath = Path.Combine(EvalResultStore.StorageLocation, $"{qualityProbe}.json");
        string trajectoryPath = Path.Combine(EvalResultStore.StorageLocation, $"{trajectoryProbe}.json");

        // ⚠ CONTAINED. A serialiser that refuses one of these values throws, and an uncontained
        //   throw here unwinds the whole control panel — 23 rows and the snapshot lost to row 24.
        //   That is correction ⑬ item 6's lesson, in the row that was written after it. The type
        //   and message are printed verbatim, because "the record did not round-trip" without the
        //   exception is a finding nobody can act on.
        try
        {
            EvalResultStore.SaveQuality(qualityProbe, new QualitySnapshot
            {
                Label = "round-trip probe",
                Cells =
                [
                    new QualityCellSnapshot("USR-XX-99", "agent", double.NaN, 0, true, 0, 0, 3, null, "the turn threw"),
                    new QualityCellSnapshot("USR-XX-99", "popularity", 42.5, 61, false, 5, 5, 0, 0.1234m, null),
                ],
                GatePassed = false,
                InstrumentFailures = 1,
                JudgeModel = "probe",
                JudgeSpreadPoints = Eval05_RecommendationQuality.MeasuredJudgeSpreadPoints,
            });

            var quality = EvalResultStore.LoadQuality(qualityProbe);
            if (quality is null)
            {
                problems.Add("the Eval 05 record did not read back at all.");
            }
            else
            {
                if (quality.Cells.Count != 2)
                    problems.Add($"the Eval 05 record round-tripped {quality.Cells.Count} cell(s) of 2.");
                else
                {
                    if (!double.IsNaN(quality.Cells[0].WeightedScore))
                        problems.Add($"a NaN weighted score came back as {quality.Cells[0].WeightedScore} — an unscorable cell would read as a scored one.");
                    if (!quality.Cells[0].InstrumentFailed)
                        problems.Add("the instrument-failure flag was lost on the round trip — a score with no flag beside it is how correction ⑫ happened.");
                    if (quality.Cells[1].CostUsd != 0.1234m)
                        problems.Add($"a decimal cost came back as {quality.Cells[1].CostUsd}.");
                }

                if (quality.JudgeSpreadPoints != Eval05_RecommendationQuality.MeasuredJudgeSpreadPoints)
                    problems.Add("the judge spread was lost — the bound on every score in the file is not in the file.");
            }

            EvalResultStore.SaveTrajectory(trajectoryProbe, new TrajectorySnapshot
            {
                Label = "round-trip probe",
                Cases =
                [
                    new TrajectoryCaseSnapshot("T-99", false, ["a claim that did not hold"],
                        ["GetUserProfile", "SearchProductsByMeaning", "GetInterestMap"], 0, 0, 3, 12, false, null),
                ],
                GatePassed = false,
                Model = "probe",
            });

            var trajectory = EvalResultStore.LoadTrajectory(trajectoryProbe);
            if (trajectory is null)
            {
                problems.Add("the Eval 06 record did not read back at all.");
            }
            else if (trajectory.Cases.Count != 1)
            {
                problems.Add($"the Eval 06 record round-tripped {trajectory.Cases.Count} case(s) of 1.");
            }
            else
            {
                // The ORDER is the whole subject of Eval 06 — an unordered set would lose the only
                // thing the record carries that no other file in the store does.
                if (!trajectory.Cases[0].ToolNames.SequenceEqual(["GetUserProfile", "SearchProductsByMeaning", "GetInterestMap"], StringComparer.Ordinal))
                    problems.Add($"the tool ORDER did not survive: [{string.Join(", ", trajectory.Cases[0].ToolNames)}].");
                if (trajectory.Cases[0].FailedClaims.Count != 1)
                    problems.Add("the failed claims were lost on the round trip.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problems.Add($"the round trip threw {ex.GetType().Name}: {Shorten(ex.Message, 90)} — a record that cannot "
                       + "be written is a record the live run will discover on the paid path, which is where this "
                       + "suite can least afford to find one.");
        }
        finally
        {
            if (File.Exists(qualityPath)) File.Delete(qualityPath);
            if (File.Exists(trajectoryPath)) File.Delete(trajectoryPath);
        }

        // ── The row's own input. Without these three, an empty scan reads as a clean suite. ──
        if (files.Count < minimumFilesScanned)
            problems.Add($"only {files.Count} eval file(s) were scanned from '{evalsDir}' — this row is asserting almost nothing.");
        if (writes == 0)
            problems.Add("no eval declares that it writes a snapshot — the scan is not reading what it thinks it is.");
        if (none == 0)
            problems.Add("no eval declares deliberately-none — the 'declaration must match behaviour' check has only one side to test.");

        return new ControlRowSnapshot(
            "EveryEvalDeclaresItsSnapshotPolicy",
            "silence about persistence is the defect. Evals 05 and 06 wrote no snapshot and said nothing about it, "
          + "while Eval 08 wrote none and stated its reason in code — three identical-looking silences, one "
          + "deliberate and two accidental, and no way to tell them apart without reading three files. Every eval now "
          + "carries a SNAPSHOT-POLICY line, 'deliberately-none' must carry a reason, and the declaration is checked "
          + "AGAINST the file's actual store calls in both directions — a comment is not a record, and a stale "
          + "declaration is worse than none. The row also asserts its own input, because a source scan that finds no "
          + "offenders and a source scan that found no files look identical.",
            problems.Count == 0
                ? $"{files.Count} eval file(s) scanned · {writes} declare 'writes' and call a store · {none} declare "
                + "'deliberately-none' with a reason and call none · every declaration matches the file's behaviour · "
                + "both NEW records round-tripped with the awkward values (NaN weighted score, null cost, null error, "
                + "the tool ORDER), and their probe files were deleted"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    /// <summary>Collects the DETAIL lines of every published discovery event, so a control can read what a node PRINTED.</summary>
    /// <remarks>
    /// A sentence a node publishes is part of what this suite ships — the 2026-09-05 arc turned on
    /// a printed remedy that named the wrong cause twice — so it has to be reachable by a control
    /// rather than only by a human reading a console.
    /// </remarks>
    /// <param name="lines">The list every event's detail lines are appended to.</param>
    private sealed class CapturingProgressSink(List<string> lines) : IDiscoveryProgressSink
    {
        public void Publish(DiscoveryEvent discoveryEvent)
        {
            if (discoveryEvent?.Detail is { } detail) lines.AddRange(detail);
        }
    }

    private static string Format(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

    // Four decimals where three would round a ±0.01 band to a digit that cannot show which side of it a number fell.
    private static string F4(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);


    // ══ VACUITY IS DECLARED, NOT INFERRED ═══════════════════════════════════════════════════
    //
    /// <summary>
    /// Eval 09's judged panel must read a criterion's vacuity off the CRITERION, never off the floor
    /// arm's met rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Plan item 8.16 #5.</b> The panel used to print <i>"VACUOUS — an answer that recommends
    /// nothing satisfies it"</i> whenever the contentless floor arm's met rate reached 1.000. That is
    /// applicability read out of the RESULT rather than out of the INPUT, and measured on the
    /// 2026-09-05 paid run it was <b>wrong on two of the three rows it fired on</b>: criteria 3 and 5
    /// were met by the floor arm because <c>ContentlessFloorArm.Answer</c> says those things in so
    /// many words, deliberately. The label's effect was to discount criterion 5 — where the workflow
    /// scored 0.000 against a floor that had EARNED 1.000, at p = 0.0005 — as carrying no information.
    /// </para>
    /// <para>
    /// This row checks three things, and the third is what makes the first two worth anything:
    /// </para>
    /// <list type="number">
    ///   <item><description>the crossing function is exercised on all six combinations of (declared,
    ///   floor) and each returns a DIFFERENT reading — a classifier that answers the same thing for
    ///   a vacuous criterion and an earned one is the label being replaced;</description></item>
    ///   <item><description>the declaration ledger is not extreme — <b>at least one criterion
    ///   declared vacuous and at least one not</b>. An all-false ledger is a ledger nothing writes
    ///   to, and it reads exactly like a rubric with no vacuity problem;</description></item>
    ///   <item><description><b>the restated criterion 4 is rejected in its SUPERSEDED form.</b> The
    ///   old wording is frozen in <c>GalaxusEvalCriteria.SupersededLanguageCriterion</c> — it is what
    ///   the paid run actually sent to the judge, not a fixture written today — and the row asserts
    ///   that the shipped rubric no longer contains it and that the replacement carries an explicit
    ///   existential the old one lacked. Without this the first two checks would pass over a rubric
    ///   that had never been fixed.</description></item>
    /// </list>
    /// <para>
    /// ⚠ <b>Scope, declared.</b> Check 3 compares TEXT. It proves the criterion now demands something
    /// present; it cannot prove a judge will honour the demand. That is a judged run, and 8.16 #5
    /// stays open for exactly that half.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckVacuityIsDeclaredNotInferred()
    {
        var problems = new List<string>();

        // ── 1. The crossing function separates facts the old rule conflated. ──
        //
        //   The old rule was `floor >= 0.999 => VACUOUS`. Under it, rows (a) and (c) below are
        //   indistinguishable, and (c) is the shape that discounted criterion 5.
        var vacuousAndHighFloor = Eval09PreRegistration.CaveatFor(declaredVacuous: true, floorMetRate: 1.0);
        var vacuousAndLowFloor = Eval09PreRegistration.CaveatFor(declaredVacuous: true, floorMetRate: 0.0);
        var vacuousAndNoFloor = Eval09PreRegistration.CaveatFor(declaredVacuous: true, floorMetRate: double.NaN);
        var earnedEveryTime = Eval09PreRegistration.CaveatFor(declaredVacuous: false, floorMetRate: 1.0);
        var earnedSometimes = Eval09PreRegistration.CaveatFor(declaredVacuous: false, floorMetRate: 0.5);
        var nothingToSay = Eval09PreRegistration.CaveatFor(declaredVacuous: false, floorMetRate: 0.0);

        if (vacuousAndHighFloor == earnedEveryTime)
            problems.Add("a criterion DECLARED vacuous and one the floor arm EARNS every time read the same — the label the panel prints cannot tell an empty-set artefact from a hard row, which is the defect this row exists for.");
        if (vacuousAndHighFloor != JudgedRowCaveat.VacuousAndUninterpretable)
            problems.Add($"declared-vacuous with a floor of 1.000 read {vacuousAndHighFloor} — the one combination that makes a row unreadable is not being named.");
        if (vacuousAndLowFloor != JudgedRowCaveat.DeclaredVacuousButFloorDisagrees)
            problems.Add($"declared-vacuous with a floor of 0.000 read {vacuousAndLowFloor} — the judge disagreeing with the criterion's own logic is a calibration fact and it is being dropped.");
        if (vacuousAndNoFloor != JudgedRowCaveat.DeclaredVacuousFloorUnmeasured)
            problems.Add($"declared-vacuous with an UNMEASURED floor read {vacuousAndNoFloor} — an absent floor is not a zero floor.");
        if (earnedEveryTime != JudgedRowCaveat.FloorEarnsItEveryTime || earnedSometimes != JudgedRowCaveat.FloorEarnsItSometimes)
            problems.Add("a floor the contentless arm EARNS is not reported as earned, so a live arm below it still reads as an artefact rather than as a finding.");
        if (nothingToSay != JudgedRowCaveat.None)
            problems.Add("a criterion that is neither vacuous nor met by the floor still prints a caveat — a warning on every row is a warning on none.");

        // …and the prose must actually differ, not just the enum. The old defect was a SENTENCE that
        // asserted a false mechanism; an enum nobody renders differently would reproduce it.
        string vacuousText = Eval09PreRegistration.CaveatText(vacuousAndHighFloor, 1.0);
        string earnedText = Eval09PreRegistration.CaveatText(earnedEveryTime, 1.0);
        if (string.Equals(vacuousText, earnedText, StringComparison.Ordinal))
            problems.Add("the printed caveat is IDENTICAL for a vacuous criterion and an earned one — the crossing exists in the enum and not on the page.");
        // ⚠ Matched on the MECHANISM the old label asserted, not on the word "vacuous". The first
        //   revision of this check looked for the word and went red on the corrected text, which
        //   says "the row is HARD, not vacuous" — a control that fires on its own fix.
        const string EmptySetMechanism = "arithmetic of the empty set";
        if (earnedText.Contains(EmptySetMechanism, StringComparison.OrdinalIgnoreCase))
            problems.Add("the caveat for a criterion the floor arm EARNS still asserts the empty-set mechanism — that is the false sentence the old label printed, in new words.");
        if (!earnedText.Contains("EARNS", StringComparison.Ordinal))
            problems.Add("the caveat for a criterion the floor arm EARNS does not say it was earned, so a live arm below that floor still reads as an artefact.");
        if (!vacuousText.Contains(EmptySetMechanism, StringComparison.OrdinalIgnoreCase))
            problems.Add("the caveat for a genuinely vacuous criterion no longer names the empty-set mechanism, so a reader cannot tell why the row is unreadable.");
        if (Eval09PreRegistration.CaveatText(JudgedRowCaveat.None, 0.0).Length != 0)
            problems.Add("a row with no caveat still prints one.");

        // ── 2. The declaration ledger must not be extreme in either direction. ──
        var declared = GalaxusEvalCriteria.AdvisoryCriteria;
        int vacuousCount = declared.Count(c => c.VacuousOnAnAnswerWithNoRecommendations);

        if (declared.Count == 0)
            problems.Add("the advisory rubric is empty, so this row is asserting nothing.");
        else if (vacuousCount == 0 || vacuousCount == declared.Count)
            problems.Add($"{vacuousCount} of {declared.Count} criteria are declared vacuous — an all-or-nothing ledger is a ledger nobody filled in, and it reads exactly like a rubric with no vacuity problem.");

        if (declared.Any(c => string.IsNullOrWhiteSpace(c.Text)))
            problems.Add("a criterion carries no text.");
        if (!declared.Select(c => c.Text).SequenceEqual(GalaxusEvalCriteria.Advisory, StringComparer.Ordinal))
            problems.Add("GalaxusEvalCriteria.Advisory is no longer the projection of AdvisoryCriteria — a rubric with two copies acquires two vintages.");
        if (!GalaxusEvalCriteria.Advisory.SequenceEqual(Eval09PreRegistration.JudgedCriteria, StringComparer.Ordinal))
            problems.Add("Eval 09 judges a different list from the one whose vacuity is declared here.");

        // ── 3. THE POSITIVE SPECIMEN — the wording that actually shipped must be REJECTED. ──
        //
        //   Checks 1 and 2 would both pass over a rubric that had never been restated. This is the
        //   half that proves it was.
        const string ExistentialMarker = "does NOT meet this criterion";

        if (GalaxusEvalCriteria.Advisory.Any(c => c.Contains(GalaxusEvalCriteria.SupersededLanguageCriterion, StringComparison.Ordinal)))
            problems.Add("the SUPERSEDED criterion-4 wording is still in the shipped rubric — an answer with no reasoning meets it by the arithmetic of the empty set, and the floor arm scored 1.000 on it while both live arms scored 0.000.");
        if (GalaxusEvalCriteria.SupersededLanguageCriterion.Contains(ExistentialMarker, StringComparison.Ordinal))
            problems.Add("the frozen superseded wording already contains the existential marker, so it is no longer a specimen this check can fail on — the positive control has gone vacuous itself.");

        var languageCriteria = GalaxusEvalCriteria.Advisory
            .Where(c => c.Contains("own language", StringComparison.Ordinal)).ToList();
        if (languageCriteria.Count == 0)
            problems.Add("no criterion mentions the customer's own language any more — the restatement deleted the criterion instead of repairing it.");
        else if (!languageCriteria.All(c => c.Contains(ExistentialMarker, StringComparison.Ordinal)))
            problems.Add("the language criterion does not say what an answer with no recommendation reasons scores, so it is still satisfiable by an answer that reasons about nothing.");

        return new ControlRowSnapshot(
            "VacuityIsDeclaredNotInferred",
            "a judged criterion's vacuity is a property of the CRITERION, declared per criterion, never inferred "
          + "from the floor arm's met rate — and the superseded criterion-4 wording, which an empty answer met by "
          + "the arithmetic of the empty set, is refused",
            problems.Count == 0
                ? $"{vacuousCount} of {declared.Count} criteria declared vacuous · all six (declared × floor) "
                + "combinations read differently and print different prose · the superseded language criterion is "
                + "absent from the shipped rubric and lacks the existential its replacement carries "
                + "⚠ TEXT ONLY — that a judge HONOURS the existential needs a judged run (8.16 #5)"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 44 — a zero in the cost table must say WHICH zero it is (plan item 8.3). ═══
    //
    /// <summary>
    /// The cost printer must render an arm that reaches no model and an arm whose usage never
    /// arrived DIFFERENTLY, and the state it renders from must be recorded by the recorder rather
    /// than inferred from the totals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Plan item 8.3 was WRONG AS SPECIFIED, and this row is what it should have said.</b> It
    /// asked for <c>"—"</c> when <c>ModelId is null</c>. <c>ArmCostSnapshot</c> carried no model id
    /// and <c>ArmCost</c> carried nothing but running totals, so the printer had no input that could
    /// distinguish the two zeroes: rendering <c>—</c> on a zero total relabels a true zero as
    /// unknown, rendering <c>$0.0000</c> labels an unknown as zero, and
    /// <c>MEASUREMENT_STATUS</c> §55 forbids exactly that pair being rendered alike.
    /// </para>
    /// <para>
    /// ⚠ <b>The state is derived from the INPUT, never from the RESULT.</b> §61.8's defect — five
    /// dry-run checks that read applicability out of what came back — is the shape available here,
    /// because a zero total is precisely the value that cannot answer "was there a model?".
    /// <c>ArmCost.State</c> reads the run counters the recorder incremented.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckCostRowsSayWhichZeroTheyMean()
    {
        var problems = new List<string>();

        static PerformanceMetrics Metrics(int? prompt, int? completion, decimal? cost, string? model) =>
            new()
            {
                StartTime = DateTimeOffset.UnixEpoch,
                EndTime = DateTimeOffset.UnixEpoch.AddSeconds(2),
                PromptTokens = prompt,
                CompletionTokens = completion,
                EstimatedCost = cost,
                ModelUsed = model,
            };

        // The same object the harness produces when a response carried NO usage block: a count it
        // computed from the text, stamped as an estimate. MAFEvaluationHarness:145.
        static PerformanceMetrics EstimatedMetrics(int prompt, int completion) =>
            new()
            {
                StartTime = DateTimeOffset.UnixEpoch,
                EndTime = DateTimeOffset.UnixEpoch.AddSeconds(2),
                PromptTokens = prompt,
                CompletionTokens = completion,
                EstimatedCost = null,
                ModelUsed = null,
                TokensAreEstimated = true,
            };

        var report = new PairedCoverageReport();
        // Two ways to be model-free, and BOTH must land on the same state: no metrics object at
        // all, and a metrics object from an arm the registry declares reaches no model. The second
        // is the one that ships — the harness hands every arm a PerformanceMetrics.
        report.RecordCost("deterministic", null, reachesAModel: false);
        report.RecordCost("deterministic", Metrics(0, 0, null, null), reachesAModel: false);
        report.RecordCost("silent-model", Metrics(null, null, null, "gpt-5.5"));
        report.RecordCost("no-metrics-at-all", null, reachesAModel: true);
        report.RecordCost("half-block", Metrics(1_234, null, null, "gpt-5.5"));
        report.RecordCost("measured", Metrics(100, 20, 0.0031m, "gpt-5.5"));
        report.RecordCost("measured", Metrics(100, 20, 0.0031m, "gpt-5.5"));

        var deterministic = report.CostOf("deterministic");
        var silent = report.CostOf("silent-model");
        var noMetrics = report.CostOf("no-metrics-at-all");
        var half = report.CostOf("half-block");
        var measured = report.CostOf("measured");
        var notRun = report.CostOf("never-recorded");

        // ── 1. The five states are the five the recorder can actually produce. ──
        (string Label, PairedCoverageReport.ArmCost Cost, PairedCoverageReport.ArmCostState Want)[] states =
        [
            ("deterministic", deterministic, PairedCoverageReport.ArmCostState.NoModel),
            ("silent-model", silent, PairedCoverageReport.ArmCostState.LowerBound),
            // The harness handed back NOTHING for an arm that does reach a model. That is the
            // absence, and it must not collapse into the deterministic arm's measured zero.
            ("no-metrics-at-all", noMetrics, PairedCoverageReport.ArmCostState.LowerBound),
            ("half-block", half, PairedCoverageReport.ArmCostState.LowerBound),
            ("measured", measured, PairedCoverageReport.ArmCostState.Measured),
            ("never-recorded", notRun, PairedCoverageReport.ArmCostState.NotRun),
        ];
        foreach (var (label, cost, want) in states)
        {
            if (cost.State != want)
                problems.Add($"'{label}' reads {cost.State}, not {want} — the recorded run states no longer decide it.");
        }

        // ── 2. THE PAIR THIS ROW EXISTS FOR: the deterministic arm and the silent model arm have
        //       IDENTICAL totals (0 tokens, 0.0000) and must not render the same text. The equality
        //       is asserted first, so the separation below cannot be coming from the arithmetic.
        if (deterministic.PromptTokens + deterministic.CompletionTokens != silent.PromptTokens + silent.CompletionTokens
            || deterministic.EstimatedCost != silent.EstimatedCost)
        {
            problems.Add("the two arms this row separates no longer have identical totals, so the separation below "
                       + "could be coming from the arithmetic rather than from the recorded state — the row is vacuous.");
        }

        var (detRow, detNote) = EvalPrinter.CostRow("deterministic", deterministic);
        var (silRow, silNote) = EvalPrinter.CostRow("silent-model", silent);
        var (halfRow, halfNote) = EvalPrinter.CostRow("half-block", half);
        var (measRow, measNote) = EvalPrinter.CostRow("measured", measured);
        var (nrRow, nrNote) = EvalPrinter.CostRow("never-recorded", notRun);

        static string WithoutLabel(string row, string label) => row.Replace(label, "", StringComparison.Ordinal).Trim();

        if (string.Equals(WithoutLabel(detRow, "deterministic"), WithoutLabel(silRow, "silent-model"), StringComparison.Ordinal))
        {
            problems.Add("a deterministic arm and a model arm that reported no usage render IDENTICALLY once the label "
                       + "is discounted — the exact pair §55 forbids rendering alike.");
        }

        // ── 3. Each row's text has to carry the claim its state makes. ──
        // The measured zero prints as a NUMBER, in the same currency rendering every other row uses
        // — a figure whose text depends on who ran it cannot be read out of a log (§55.6).
        string zeroMoney = 0m.ToString("C4", System.Globalization.CultureInfo.InvariantCulture);
        if (!detRow.Contains(zeroMoney, StringComparison.Ordinal) || detRow.Contains('≥'))
            problems.Add("the NO MODEL row does not print its zero as a plain number in the shared currency "
                       + "rendering; a measured zero must read as a measurement.");
        if (detNote is null || !detNote.Contains("measured, not missing", StringComparison.Ordinal))
            problems.Add("the NO MODEL row does not say its zero was measured.");
        if (!silRow.Contains('≥') || silNote is null || !silNote.Contains("LOWER BOUND", StringComparison.Ordinal))
            problems.Add("a model arm with no usage block does not render as a LOWER BOUND — an absence is being shown as a total.");
        if (halfNote is null || !halfNote.Contains("HALF", StringComparison.Ordinal))
            problems.Add("a HALF usage block is not named (§60.2: the rule applies to the halves, not only the block).");
        if (!measRow.Contains("240", StringComparison.Ordinal) || measNote is not null)
            problems.Add("a fully measured arm does not render as a plain total with no caveat.");
        if (!nrRow.Contains('—') || nrNote is null || !nrNote.Contains("NOT RUN", StringComparison.Ordinal))
            problems.Add("an arm that was never run does not read as NOT RUN — 'no turns' is not 'no cost'.");

        // ── 4. The model id reaches the row, and its ABSENCE is named rather than left blank. ──
        if (!measRow.Contains("gpt-5.5", StringComparison.Ordinal))
            problems.Add("the model id does not reach the printed row — 8.3's own premise ('the printer cannot know') would still hold.");
        var anonymous = new PairedCoverageReport();
        anonymous.RecordCost("anonymous", Metrics(10, 5, 0.001m, null));
        var (anonRow, _) = EvalPrinter.CostRow("anonymous", anonymous.CostOf("anonymous"));
        if (!anonRow.Contains("model NOT NAMED", StringComparison.Ordinal))
            problems.Add("a model turn whose metrics named no model renders indistinguishably from a deterministic arm.");
        if (anonymous.CostOf("anonymous").RunsWithoutModelId != 1)
            problems.Add("a metrics object with no ModelUsed was not counted as one.");

        // ── 4b. THE DECLARATION IS CHECKED AGAINST ITS EVIDENCE, and an ESTIMATE is not evidence.
        //        `CoverageArm.ReachesAModel` is a declaration and nothing checked it: the review
        //        pass of 2026-09-06 flipped the live arm's to false and Eval 02 printed
        //        "NO MODEL — all 24 turn(s) ran without reaching one … This zero is measured, not
        //        missing." over 2,952 recorded tokens, with `-- 3` still exit 0 and NOT CAUGHT = 0.
        //        The parameter DEFAULTS to false, so that is also the mis-declaration you get by
        //        forgetting. Both halves are asserted here because either alone is useless:
        //        provider usage on a declared-free arm must CONTRADICT, and a harness estimate on
        //        one must NOT (MAFEvaluationHarness:145 invents a count from text for every
        //        deterministic arm, so a check on the numbers alone fires on all five of them).
        var contradiction = new PairedCoverageReport();
        contradiction.RecordCost("declared-free", Metrics(500, 100, null, "gpt-5.5"), reachesAModel: false);
        var contradicted = contradiction.CostOf("declared-free");
        if (contradicted.State != PairedCoverageReport.ArmCostState.Contradicted)
        {
            problems.Add($"an arm DECLARED model-free that reported a provider usage block reads "
                       + $"{contradicted.State}, not Contradicted — the declaration outranks its own "
                       + "evidence and a mis-declared live arm keeps printing a measured zero.");
        }
        var (contRow, contNote) = EvalPrinter.CostRow("declared-free", contradicted);
        if (contRow.Contains("500", StringComparison.Ordinal) || contRow.Contains("600", StringComparison.Ordinal)
            || contNote is null || !contNote.Contains("CONTRADICTED", StringComparison.Ordinal))
        {
            problems.Add("a contradicted arm still prints a number — a declaration and its evidence "
                       + "disagreeing is not a smaller measurement, it is no measurement.");
        }

        var estimated = new PairedCoverageReport();
        estimated.RecordCost("declared-free", EstimatedMetrics(500, 100), reachesAModel: false);
        if (estimated.CostOf("declared-free").State != PairedCoverageReport.ArmCostState.NoModel)
        {
            problems.Add("a HARNESS-ESTIMATED token count on a declared model-free arm reads as a "
                       + "contradiction — every deterministic arm carries one, so this would fire on all "
                       + "of them and the check would have to be removed rather than trusted.");
        }

        // ── 4c. AND THE ESTIMATE IS NAMED WHEREVER IT IS COUNTED. A count the harness computed
        //        from the output's own text is not a provider measurement, and the row said "token
        //        counts are complete" over 24 of them.
        var guessed = new PairedCoverageReport();
        guessed.RecordCost("guessed", EstimatedMetrics(500, 100), reachesAModel: true);
        var (guessRow, guessNote) = EvalPrinter.CostRow("guessed", guessed.CostOf("guessed"));
        if (!guessRow.Contains('≈') || guessNote is null
            || !guessNote.Contains("ESTIMATED FROM TEXT", StringComparison.Ordinal)
            || guessNote.Contains("token counts are complete", StringComparison.Ordinal))
        {
            problems.Add("a token count the HARNESS estimated from text renders as a provider "
                       + "measurement — SpendLedger and Evals 07-09 all read TokensAreEstimated and this "
                       + "panel did not.");
        }

        // ── 4c-bis. THE MONEY COLUMN OBEYS THE SAME RULE AS THE TOKEN COLUMN. An arm whose turns
        //            all reported tokens and NONE reported a cost estimate is state Measured, and
        //            its money cell printed a bare ¤0.0000 — byte-identical to the NO MODEL arm's
        //            measured zero two rows above it. The footnote said LOWER BOUND; the CELL said
        //            zero, and a reader scanning a table reads cells. Asserted against the NO MODEL
        //            arm's own rendering rather than against a literal, so the two cannot converge.
        var costless = new PairedCoverageReport();
        costless.RecordCost("tokens-but-no-price", Metrics(100, 20, null, "gpt-5.5"));
        var costlessArm = costless.CostOf("tokens-but-no-price");
        if (costlessArm.State != PairedCoverageReport.ArmCostState.Measured)
            problems.Add($"the money-column case is being reached through {costlessArm.State}, not Measured "
                       + "— the check below is about a different branch than the one that shipped the defect.");
        var (costlessRow, _) = EvalPrinter.CostRow("tokens-but-no-price", costlessArm);
        string moneyCellOfAMeasuredZero = zeroMoney;
        if (costlessRow.Contains(" " + moneyCellOfAMeasuredZero, StringComparison.Ordinal)
            || !costlessRow.Contains("≥", StringComparison.Ordinal))
        {
            problems.Add("an arm whose model turns reported NO cost estimate prints its money as a plain "
                       + "figure, identical to a NO MODEL arm's measured zero — an absence rendered as a "
                       + "total, in the money column, which is the one §55 is about.");
        }

        // ── 4d. THE SHIPPED REGISTRY, cross-checked against a field declared for another purpose.
        //        Kind decides repetition and the sign test; ReachesAModel decides the money. A
        //        mis-declaration now has to be made twice, consistently, to pass.
        foreach (var registered in CoverageArms.All)
        {
            bool live = registered.Kind == CoverageArmKind.Live;
            if (live != registered.ReachesAModel)
            {
                problems.Add($"arm '{registered.Label}' is Kind={registered.Kind} and declares "
                           + $"ReachesAModel={registered.ReachesAModel}. The two fields are declared "
                           + "independently and for different consumers; disagreeing means one of them "
                           + "is wrong and the cost panel is reading the one that decides the money.");
            }
        }

        // ── 5. It reaches the STORED record too, and a pre-8.3 document says it cannot answer. ──
        var snapshot = new ArmCostSnapshot(2, 4_000, 200, 40, 0.0062m, 0, 2, 0, 0, 0, 0, ["gpt-5.5"]);
        if (!snapshot.StateIsRecorded || snapshot.ModelIds is null || snapshot.ModelIds.Count != 1)
            problems.Add("a snapshot written now does not carry the run state and the model id.");
        var legacy = new ArmCostSnapshot(2, 4_000, 200, 40, 0.0062m);
        if (legacy.StateIsRecorded)
            problems.Add("a pre-8.3 snapshot claims to carry a run state it does not have — the defaults are being read "
                       + "as a measurement, which is the same class of error 8.3 is about.");

        return new ControlRowSnapshot(
            "CostRowsSayWhichZeroTheyMean",
            "the cost table must not render 'this arm reaches no model' and 'this arm's usage never arrived' the same "
          + "way. Both are 0 tokens and $0.0000, and MEASUREMENT_STATUS §55 forbids rendering them alike. Plan item "
          + "8.3 asked for a rendering change against a `ModelId` field that did not exist, so it was unimplementable "
          + "as written: the printer had no input that could tell the two apart. The state is now RECORDED by "
          + "PairedCoverageReport.RecordCost — from whether a metrics object arrived, not from what the totals came "
          + "to — and read here off EvalPrinter.CostRow's returned strings rather than off a console scrollback.",
            problems.Count == 0
                ? "all five states reproduce from recorded run counts (NotRun · NoModel · LowerBound x3, one of "
                + "them an arm that reaches a model and got NO metrics object · Measured) · "
                + "the deterministic arm and the silent model arm carry IDENTICAL totals and render differently · the "
                + "measured zero prints as $0.0000 and says so · a missing usage block prints ≥ and LOWER BOUND · a "
                + "HALF block is named separately · an unnamed model reads 'model NOT NAMED' · a pre-8.3 snapshot "
                + "reports that it cannot answer instead of defaulting to NoModel · a declared-model-free arm that "
                + "reported a PROVIDER usage block reads CONTRADICTED and prints no number, while a HARNESS-ESTIMATED "
                + "count on the same arm does NOT · an estimated count renders ≈ and says it is a guess rather than "
                + $"'token counts are complete' · and all {CoverageArms.All.Count} registered arms agree with their "
                + "own Kind about whether they reach a model"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 45 — B-18's spread must never invent a zero, and must bound conservatively. ══
    //
    /// <summary>
    /// The rep-to-rep spread panel (design §8.1 row 19 / B-18, plan item 8.1) must report
    /// <c>NOT REPEATED</c> for an arm that ran once rather than a spread of zero, must use the
    /// SAMPLE sd, must drop an unscorable rep rather than score it, and must bound a paired delta
    /// with the arm's WIDEST movement rather than its median.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The flattering direction is the one to guard.</b> Measured on this corpus's own dry run:
    /// the live arm's answers are identical across reps on 11 of 12 cells and move by 0.250 on the
    /// twelfth, so the MEDIAN rep-to-rep range is <b>0.000</b>. A median bound would certify every
    /// non-zero delta in the suite as "outside the instrument's noise" — for free, and in the
    /// direction that makes the headline look stronger. The check below is a positive control on
    /// exactly that: a delta the median would pass must read INSIDE the noise.
    /// </para>
    /// <para>
    /// The second half is the recurring <i>absence-is-not-a-zero</i> shape one layer down. A
    /// single-run arm has no spread; printing 0.000 for it claims "no variation was observed" where
    /// the truth is "variation was never observable", and 0.000 is the more impressive of the two.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckRepSpreadNeverInventsAZero()
    {
        var problems = new List<string>();

        // ── 1. One rep is NOT a spread of zero. ──
        var single = RepSpread.Of("USR-X", "arm", [0.5]);
        if (single.ReadableAsSpread || !double.IsNaN(single.Sd) || !double.IsNaN(single.Range))
            problems.Add("a one-rep cell reports a spread; one run is a whole distribution, and 0.000 for it is a claim.");
        if (!string.Equals(single.Verdict, "NOT REPEATED", StringComparison.Ordinal))
            problems.Add($"a one-rep cell reads '{single.Verdict}' rather than NOT REPEATED.");

        // ── 2. Two identical reps ARE a spread, and it is zero. The two zeroes are different. ──
        var identical = RepSpread.Of("USR-X", "arm", [0.5, 0.5]);
        if (!identical.ReadableAsSpread || identical.Range != 0 || identical.Sd != 0)
            problems.Add("two identical reps do not read as a measured zero spread.");
        if (string.Equals(identical.Verdict, single.Verdict, StringComparison.Ordinal))
            problems.Add("'ran once' and 'ran twice and did not move' read the same — the pair this row separates.");

        // ── 3. SAMPLE sd, not population. Over [0, 1] the two differ by sqrt(2). ──
        var twoApart = RepSpread.Of("USR-X", "arm", [0.0, 1.0]);
        if (Math.Abs(twoApart.Sd - (1.0 / Math.Sqrt(2.0))) > 1e-9)
            problems.Add($"sd over [0, 1] is {twoApart.Sd:F6}; the SAMPLE sd is 0.707107 and the population sd 0.5.");

        // ── 4. An unscorable rep is DROPPED, not scored. ──
        var withNaN = RepSpread.Of("USR-X", "arm", [0.4, double.NaN]);
        if (withNaN.Reps != 1 || withNaN.ReadableAsSpread)
            problems.Add("an unscorable rep was counted; a NaN rep is an absence and must lower the rep count, "
                       + "not enter the spread as a value.");
        var allNaN = RepSpread.Of("USR-X", "arm", [double.NaN, double.NaN]);
        if (allNaN.Reps != 0 || !string.Equals(allNaN.Verdict, "NO REPS SCORED", StringComparison.Ordinal))
            problems.Add("a cell where nothing was scorable does not say so.");

        // ── 5. THE POSITIVE CONTROL ON THE FLATTERING DIRECTION. The corpus's own shape: eleven
        //       cells that did not move and one that moved 0.250. Median 0.000, widest 0.250. ──
        var report = new RepSpreadReport("control channel");
        for (int i = 0; i < 11; i++) report.Record(RepSpread.Of($"USR-{i:00}", "live", [0.5, 0.5]));
        report.Record(RepSpread.Of("USR-11", "live", [0.25, 0.50]));
        report.Record(RepSpread.Of("USR-00", "deterministic", [0.5]));

        var summary = report.SummaryFor("live");
        if (summary.MedianRange != 0.0 || Math.Abs(summary.WidestRange - 0.25) > 1e-9 || summary.CellsThatMoved != 1)
            problems.Add($"the control's own shape did not reproduce (median {summary.MedianRange:F3}, widest "
                       + $"{summary.WidestRange:F3}, moved {summary.CellsThatMoved}) — the discrimination below is vacuous.");

        var inside = report.CompareToOwnNoise("live", 0.10);
        if (inside.Verdict != NoiseVerdict.InsideNoise)
            problems.Add($"a delta of 0.100 reads {inside.Verdict} against a widest movement of 0.250. The MEDIAN "
                       + "is 0.000 and would have passed it — the bound has slipped to the flattering statistic.");
        var outside = report.CompareToOwnNoise("live", 0.30);
        if (outside.Verdict != NoiseVerdict.OutsideNoise)
            problems.Add("a delta of 0.300 does not read as outside a widest movement of 0.250 — the bound now "
                       + "refuses everything, which is no more a measurement than accepting everything.");

        // ── 5b. THE VERDICT SATURATES AND THE COUNT MUST NOT. On the persisted paid run the live
        //        arm's widest movement is exactly 1.000 (a persona went 0.000 → 1.000 on the same
        //        question), and latent coverage lives in [0, 1] — so NOTHING can read "outside" and
        //        the verdict stops discriminating. The COUNT of cells that moved more than the
        //        delta keeps working at the saturating end.
        var saturated = new RepSpreadReport("saturating channel");
        double[] ranges = [0.0, 0.0, 0.0, 0.1, 0.1, 0.1, 0.5, 0.5, 0.5, 1.0, 1.0, 1.0];
        for (int i = 0; i < ranges.Length; i++)
            saturated.Record(RepSpread.Of($"USR-{i:00}", "live", [0.0, ranges[i]]));

        var atFive = saturated.CompareToOwnNoise("live", 0.05);
        var atThirty = saturated.CompareToOwnNoise("live", 0.30);
        var atNinety = saturated.CompareToOwnNoise("live", 0.90);

        // The verdict cannot tell these three apart: the bound is the metric's own maximum.
        if (atFive.Verdict != NoiseVerdict.InsideNoise || atThirty.Verdict != NoiseVerdict.InsideNoise
            || atNinety.Verdict != NoiseVerdict.InsideNoise)
        {
            problems.Add("the saturating fixture did not reproduce (a widest range of 1.000 must swallow every "
                       + "delta in [0, 1]), so the check below is vacuous.");
        }

        // The COUNT can, and that is why it is the statistic the panel leads with.
        if (atFive.CellsMovingMore != 9 || atThirty.CellsMovingMore != 6 || atNinety.CellsMovingMore != 3
            || atFive.CellsReadable != 12)
        {
            problems.Add($"the cell count no longer discriminates at the saturating end: 0.05 → "
                       + $"{atFive.CellsMovingMore}, 0.30 → {atThirty.CellsMovingMore}, 0.90 → "
                       + $"{atNinety.CellsMovingMore} of {atFive.CellsReadable}; expected 9, 6, 3 of 12. Without it "
                       + "the panel has only a verdict that reads INSIDE for everything.");
        }

        // ── 6. TWO ABSENCES, TWO SENTENCES. One message for both is the defect this suite keeps
        //       re-finding, and it was live in this panel's own first build. ──
        var noSpread = report.CompareToOwnNoise("deterministic", 0.10);
        var noDelta = report.CompareToOwnNoise("live", double.NaN);
        if (noSpread.Verdict != NoiseVerdict.NoSpreadRecorded)
            problems.Add("an arm that ran once is not reported as bounding nothing.");
        if (noDelta.Verdict != NoiseVerdict.NoDelta)
            problems.Add("a comparison that produced no delta is not reported as such.");
        if (string.Equals(noSpread.Describe(), noDelta.Describe(), StringComparison.Ordinal))
            problems.Add("'no spread was recorded' and 'no delta was produced' print the SAME sentence — one "
                       + "message for two different causes, which is how the first build of this panel blamed a "
                       + "readable spread for a missing delta.");

        // ── 7. An arm with no rows at all bounds nothing either. ──
        if (report.CompareToOwnNoise("never-recorded", 0.10).Verdict != NoiseVerdict.NoSpreadRecorded)
            problems.Add("an arm with no recorded cells does not report that it bounds nothing.");

        return new ControlRowSnapshot(
            "RepSpreadNeverInventsAZero",
            "B-18's rep-to-rep spread must not manufacture a number where none was measured, and must bound a "
          + "paired delta with the arm's WIDEST movement rather than its median. MEASURED on this corpus: the "
          + "live arm is identical across reps on 11 of 12 cells and moves 0.250 on the twelfth, so the median "
          + "range is 0.000 — a median bound would certify every non-zero delta in the suite as 'outside the "
          + "noise', for free and in the flattering direction. The same rule one layer down: an arm that ran "
          + "ONCE has no spread, and 0.000 is a claim rather than an absence.",
            problems.Count == 0
                ? "one rep reads NOT REPEATED with sd and range UNDEFINED · two identical reps read as a MEASURED "
                + "zero and the two verdicts differ · the sd is the SAMPLE sd (0.707107 over [0, 1], not 0.5) · a "
                + "NaN rep lowers the rep count instead of entering the spread · a delta of 0.100 reads INSIDE a "
                + "widest movement of 0.250 that the median 0.000 would have passed · 0.300 reads outside it · "
                + "at the SATURATING end (widest 1.000, the metric's own maximum) the inside/outside verdict "
                + "cannot discriminate 0.05 from 0.90 and the CELL COUNT still does, 9 / 6 / 3 of 12 · "
                + "'no spread recorded' and 'no delta' print DIFFERENT sentences"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 46 — the judge must be exercisable for nothing (plan item 8.5). ═══════════
    //
    /// <summary>
    /// Eval 01's advisory judge must be reachable under <c>--dry-run</c>, and its parser must map
    /// every reply shape a real judge can produce — including the two that are not verdicts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect 8.5 names was an explicit <c>!dryRun</c>.</b> Eval 01 read
    /// <c>if (judge &amp;&amp; !dryRun) await RunAdvisoryJudgeAsync(...)</c>, so
    /// <c>-- 1 --dry-run --judge</c> entered the judged path ZERO times, printed no judge panel and
    /// exited 0. A dry run that certifies a path it never enters is worse than no dry run, because
    /// it reads as coverage. That is the THIRD structural blindness in this suite's dry runs.
    /// </para>
    /// <para>
    /// ⚠ <b>Two independent legs, because either alone is weak.</b> The BEHAVIOURAL leg drives the
    /// real <c>RecommendationJustificationJudge</c> against the real
    /// <see cref="StubJudgeClient"/> and reads the verdicts, so the parser's discipline is measured
    /// rather than asserted. The SOURCE leg reads Eval 01's own dispatch, comment-stripped, and
    /// fails if the judge is put back behind <c>!dryRun</c> — because the behavioural leg cannot
    /// see that: removing the wiring removes the checks with it, and the eval goes back to exiting
    /// 0 in silence. §60.4's lesson is honoured — the needle is matched against COMMENT-STRIPPED
    /// source, so commenting the call out does not satisfy it.
    /// </para>
    /// </remarks>
    private static async Task<ControlRowSnapshot> CheckTheJudgedPathIsReachableWithoutPayingAsync()
    {
        var problems = new List<string>();

        // ── LEG 1 — behaviour. The real judge, the real parser, the awkward stub. ──
        var stub = new StubJudgeClient();
        var judge = new RecommendationJustificationJudge(stub);
        var verdicts = new List<JustificationVerdict>();

        // A SKU that is in the catalogue and a non-empty reason, so neither code short-circuit
        // fires and every call actually reaches the transport. Five calls = one full script cycle.
        string sku = Catalogue.Default.All[0].Id;
        for (int i = 0; i < StubJudgeClient.Script.Count; i++)
        {
            var call = new PresentedCall(sku, "a reason that is not empty", "", false, i + 1, null, true, true);
            verdicts.Add((await judge.JudgeAsync(call, Personas.NadiaUserId).ConfigureAwait(false)).Verdict);
        }

        if (stub.CallCount != StubJudgeClient.Script.Count)
            problems.Add($"the transport was reached {stub.CallCount} time(s) for {StubJudgeClient.Script.Count} "
                       + "judgement(s) — a short-circuit fired and this leg is not testing the parser.");

        JustificationVerdict[] expected =
        [
            JustificationVerdict.Supported,
            JustificationVerdict.Unsupported,
            JustificationVerdict.Inconclusive,
            JustificationVerdict.InstrumentFailure,   // "MAYBE" — an unknown verdict, NOT the nearest bucket
            JustificationVerdict.InstrumentFailure,   // not JSON at all
        ];
        for (int i = 0; i < expected.Length; i++)
        {
            if (verdicts[i] != expected[i])
                problems.Add($"reply shape '{StubJudgeClient.Script[i]}' parsed to {verdicts[i]}, expected {expected[i]}.");
        }

        if (stub.ShapesEmitted.Count != StubJudgeClient.Script.Count)
        {
            problems.Add($"the stub reports {stub.ShapesEmitted.Count} of {StubJudgeClient.Script.Count} shapes "
                       + "emitted — it is no longer awkward enough to exercise the parser's failure branches.");
        }

        // The two SHORT-CIRCUITS must still fire without a model call, and they are the two places
        // silence could be scored as a pass.
        int before = stub.CallCount;
        var empty = await judge.JudgeAsync(new PresentedCall(sku, "   ", "", false, 1, null, true, true),
                                           Personas.NadiaUserId).ConfigureAwait(false);
        var unknownSku = await judge.JudgeAsync(new PresentedCall("GLX-NOT-A-SKU", "a reason", "", false, 1, null, true, true),
                                                Personas.NadiaUserId).ConfigureAwait(false);
        if (empty.Verdict != JustificationVerdict.Unsupported)
            problems.Add("an EMPTY reason did not short-circuit to Unsupported — silence would be judgeable.");
        if (unknownSku.Verdict != JustificationVerdict.Unsupported)
            problems.Add("a SKU absent from the catalogue did not short-circuit to Unsupported.");
        if (stub.CallCount != before)
            problems.Add($"the short-circuits made {stub.CallCount - before} model call(s); they exist precisely so "
                       + "that no model can be persuaded to call an empty reason supported.");

        // ── LEG 2 — the wiring, read off Eval 01's own dispatch. ──
        //
        // Needed because leg 1 survives the regression: put the judge back behind `!dryRun` and the
        // dry-run checks vanish along with it, so nothing goes red.
        string evalPath = Path.Combine(SampleSourceRoot(), "Evals", "Eval01_CatalogueIntegrity.cs");
        if (!File.Exists(evalPath))
        {
            problems.Add("Eval01_CatalogueIntegrity.cs was not found — this leg cannot say anything about a file it did not open.");
        }
        else
        {
            // ⚠ COMMENTS *AND* STRING LITERALS. This row's own printed explanation quotes the
            // pattern it forbids, and Eval 01's dry-run check prints it too — so a comment-stripped
            // scan finds the needle in the sentence that describes it and reports the defect
            // restored. Fourth sighting of the `baca28e4` shape; see StripStringLiterals.
            string code = StripStringLiterals(StripComments(File.ReadAllText(evalPath)));
            if (code.Contains("judge && !dryRun", StringComparison.Ordinal))
            {
                problems.Add("Eval 01 dispatches the judge behind `judge && !dryRun` again — --judge --dry-run enters "
                           + "the judged path ZERO times and exits 0, which is 8.5's defect restored.");
            }
            if (!code.Contains("StubJudgeClient", StringComparison.Ordinal))
                problems.Add("Eval 01 no longer references StubJudgeClient — there is no free judge to run.");
            if (!code.Contains("PrintJudgeDryRunVerdict", StringComparison.Ordinal))
                problems.Add("Eval 01 no longer prints the judged dry-run checks, so a judged dry run asserts nothing.");
            // ⚠ The CONJUNCTION, not the identifier. Ablating this by writing
            // `_ = judgeWiringHeld; return DryRunPlumbingHeld(report) ? 0 : 1;` left the name in
            // the file and the row GREEN — a needle satisfied by a use that discards the value.
            // Measured: that ablation exited 0 on `-- 3` before this line was tightened.
            // ⚠ DECLARED LIMITATION: this is a text match on one spelling. A semantically identical
            // rewrite would trip it falsely, which is the price of checking a wiring the control
            // cannot execute — Eval 03 does not run Eval 01's fifteen stub turns.
            if (!code.Contains("DryRunPlumbingHeld(report) && judgeWiringHeld", StringComparison.Ordinal))
            {
                problems.Add("the judged dry run's verdict no longer reaches the exit code as a conjunct — the checks "
                           + "would print red and the command would still exit 0.");
            }
        }

        return new ControlRowSnapshot(
            "TheJudgedPathIsReachableWithoutPaying",
            "Eval 01's advisory judge must be exercisable under --dry-run, and its parser must handle every reply "
          + "shape a real judge produces. It used to be dispatched behind `judge && !dryRun`, so --judge --dry-run "
          + "entered the judged path ZERO times, printed nothing and exited 0 — the third structural blindness in "
          + "this suite's dry runs, and the only one that was blind by an explicit flag. This row drives the REAL "
          + "judge against the awkward stub and reads the verdicts, then reads Eval 01's own dispatch, "
          + "comment-stripped, because removing the wiring would remove the dry-run checks with it and nothing "
          + "would go red.",
            problems.Count == 0
                ? $"{StubJudgeClient.Script.Count} reply shapes drive the real parser to Supported · Unsupported · "
                + "Inconclusive · InstrumentFailure (unknown verdict string, NOT the nearest bucket) · "
                + "InstrumentFailure (not JSON) · both code short-circuits fire with ZERO model calls, so silence "
                + "and an unknown SKU can never be judged supported · Eval 01 dispatches the stub judge under "
                + "--dry-run, prints its checks and feeds them into the exit code"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 47 — the surface the CUSTOMER reads is screened too (plan item 8.6 / N-11a). ══
    //
    /// <summary>
    /// D3c screens the <c>reason</c> tool ARGUMENT. The composed ANSWER must be screened by the
    /// same rule, with the same customer-raised exemption — and the result must be REPORTED without
    /// entering the zero-tolerance gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The hole.</b> Nothing read <c>AgentResponse.Text</c>. An agent can keep every
    /// <c>reason</c> argument clean and open its answer with <i>"given your pregnancy…"</i> — which
    /// is precisely the channel D3c exists to close, on the surface a regulator would look at
    /// first. The screen is shown able to fire HERE, because it does not fire on the corpus.
    /// </para>
    /// <para>
    /// ⚠ <b>Inertness, measured and declared, exactly as 1.7's N-11b was.</b> N-11b's guard was
    /// reported inert because candidate counts are 27 / 32 / 40 and never 0; this one is inert for
    /// a different and weaker reason — the only answers it has ever seen are a stub's fixed prose,
    /// because no paid Eval 01 run has been made since it was built. So its chance of firing on
    /// the shipped corpus is UNKNOWN rather than measured-zero, and that is the honest statement.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckTheAnswerTheCustomerReadsIsScreenedToo()
    {
        var problems = new List<string>();

        var probeCase = IntegrityCases.All[0];

        // ── 1. THE POSITIVE CONTROL. A screen that finds nothing proves nothing until it is shown
        //       able to hit, and this one finds nothing on the corpus it ships with. ──
        const string DirtyAnswer =
            "Here is what I picked. Given your pregnancy I have kept the load light, and your "
          + "hearing aid should pair with the second item.";
        var dirty = CatalogueIntegrityGrader.Grade(probeCase, null, null, DirtyAnswer);
        if (dirty.AnswerLeaks.Count == 0)
        {
            problems.Add("a planted answer naming 'pregnancy' and 'hearing aid' produced NO leak — the screen "
                       + "cannot fire, so its silence on the corpus means nothing.");
        }

        // ── 2. It is the SAME rule as D3c: the customer-raised exemption reaches it. ──
        var raisedCase = probeCase with { Utterance = probeCase.Utterance + " I am shopping for my pregnancy." };
        var exempt = CatalogueIntegrityGrader.Grade(raisedCase, null, null, DirtyAnswer);
        if (exempt.AnswerLeaks.Contains("pregnancy", StringComparer.OrdinalIgnoreCase))
        {
            problems.Add("a term the CUSTOMER raised is still reported as a leak — suppression is about unsolicited "
                       + "inference, never about refusing to serve a stated need, and the answer channel has "
                       + "drifted from D3c's rule.");
        }
        if (!exempt.AnswerLeaks.Contains("hearing aid", StringComparer.OrdinalIgnoreCase))
        {
            problems.Add("raising ONE special-category term exempted the others — the exact defect §C-08 recorded "
                       + "for D3c, reproduced one layer out.");
        }

        // ── 3. AN ABSENCE IS NOT A ZERO. An unscreened answer and a clean answer both give an
        //       empty list, and they are different facts. ──
        var unscreened = CatalogueIntegrityGrader.Grade(probeCase, null, null, null);
        var cleanAnswer = CatalogueIntegrityGrader.Grade(probeCase, null, null, "Here are three bags.");
        if (unscreened.AnswerWasScreened)
            problems.Add("a null answer reports that it WAS screened.");
        if (!cleanAnswer.AnswerWasScreened)
            problems.Add("a screened clean answer reports that it was not screened.");
        if (unscreened.AnswerLeaks.Count != 0 || cleanAnswer.AnswerLeaks.Count != 0)
            problems.Add("the two empty cases are not both empty, so the flag below is not what distinguishes them.");
        if (unscreened.AnswerWasScreened == cleanAnswer.AnswerWasScreened)
            problems.Add("'not screened' and 'screened and clean' are indistinguishable — an absence rendering as a "
                       + "zero, in the flattering direction.");

        // ── 4. REPORTED, NOT GATED — measured as a DIFFERENCE, never as an absolute. ──
        //
        //   ⚠ The first cut of this check asserted `dirty.Defects.Count == 0`, which is a claim
        //   about the probe case (a turn with no tools fails P0), not about the screen. Comparing
        //   the dirty answer's defect list against the clean answer's isolates the one variable.
        var cleanBaseline = CatalogueIntegrityGrader.Grade(probeCase, null, null, "Here are three bags.");
        if (dirty.Defects.Count != cleanBaseline.Defects.Count
            || dirty.HasHardDefect != cleanBaseline.HasHardDefect
            || dirty.Clean != cleanBaseline.Clean)
        {
            problems.Add($"a dirty ANSWER changed the defect list ({cleanBaseline.Defects.Count} → "
                       + $"{dirty.Defects.Count}). SuppressedSignalLeak is zero-tolerance, so that moves Eval 01's "
                       + "verdicts on a paid record no model-free change can re-take — 8.8's ruling, and this row "
                       + "inherits it.");
        }

        // ── 5. The two channels are INDEPENDENT. A clean tool argument with a dirty answer is
        //       exactly the state 8.6 exists for: a LEAK with no change to the defects. ──
        if (dirty.AnswerLeaks.Count == 0 || cleanBaseline.AnswerLeaks.Count != 0)
        {
            problems.Add("the answer channel is not independent of D3c — a clean tool argument with a dirty answer "
                       + "does not produce a leak that a clean answer does not, which is the whole state 8.6 was "
                       + "filed for.");
        }

        // ── 6. Eval 01 actually HANDS the answer over. Without this the screen ships unreachable.
        string evalPath = Path.Combine(SampleSourceRoot(), "Evals", "Eval01_CatalogueIntegrity.cs");
        if (!File.Exists(evalPath))
        {
            problems.Add("Eval01_CatalogueIntegrity.cs was not found — this leg cannot say anything about a file it did not open.");
        }
        else
        {
            string code = StripStringLiterals(StripComments(File.ReadAllText(evalPath)));
            if (!code.Contains("result.ActualOutput", StringComparison.Ordinal))
            {
                problems.Add("Eval 01 no longer passes the agent's answer to the grader, so the screen is present and "
                           + "unreachable — the shape a dead property has.");
            }
        }

        return new ControlRowSnapshot(
            "TheAnswerTheCustomerReadsIsScreenedToo",
            "D3c screens the `reason` TOOL ARGUMENT. Nothing screened the prose the agent composes around it, which "
          + "is the only text the customer sees — so an agent could keep every reason clean and open its answer "
          + "with 'given your pregnancy…', the exact channel D3c exists to close. Plan item 8.6 / N-11a adds that "
          + "screen with the SAME blocklist and the SAME customer-raised exemption, REPORTED and never gated. "
          + "⚠ It does NOT fire on this corpus, and unlike 1.7's N-11b that is not a measured zero: the only "
          + "answers it has ever been shown are a stub's fixed prose, because no paid Eval 01 run has been made "
          + "since it was built. This row is where it is shown able to hit at all.",
            problems.Count == 0
                ? $"a planted answer naming 'pregnancy' and 'hearing aid' produces {dirty.AnswerLeaks.Count} leak(s) "
                + $"and leaves the defect list at {cleanBaseline.Defects.Count}, exactly where a clean answer leaves "
                + "it — reported, not gated · a term the CUSTOMER raised is exempt while the others in the same "
                + "sentence are NOT (§C-08's defect, one layer out) · 'not screened' and 'screened and clean' are "
                + "distinguishable rather than both an empty list · Eval 01 hands the grader result.ActualOutput, "
                + "so the screen is reachable rather than dead"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 48 — ADR-031 S2b's denominator (plan item 7.6). ═══════════════════════════
    //
    /// <summary>
    /// <c>applicableFraction</c> must be <c>ObservationCensus.Measured / Total</c> and must never be
    /// <c>(Total − NotApplicable) / Total</c> — pooling <c>NotApplicable</c> with <c>NotMeasured</c>
    /// is the exact defect ADR-030 exists to prevent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two absences, two owners.</b> A case that could not test the thing is a CORPUS finding; a
    /// run in which the instrument did not run is an OPERATIONAL one. A fraction that cannot tell
    /// them apart reports a broken harness as a well-scoped corpus, and that is the FLATTERING
    /// direction — the pooled form is always the larger number.
    /// </para>
    /// <para>
    /// ⚠ <b>This is also the first place the Galaxus sample uses an ADR-030 Slice 2 TYPE.</b> Until
    /// now the sample's adoption was strings, not types: its own <c>Observation</c> is a local
    /// <c>private sealed record</c> in Eval 07 and its <c>ChanceFloors</c> is a local helper, and
    /// there is no <c>using AgentEval.Evals.Meta</c> anywhere in it. A library type with no
    /// consumer outside its own tests is a type whose contract nobody has had to live with.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckApplicableFractionDoesNotPoolTwoAbsences()
    {
        var problems = new List<string>();

        // ── 1. The rule, on a census where the two forms CAN differ. ──
        var mixed = new AgentEval.Evals.Meta.ObservationCensus(Measured: 8, NotApplicable: 3, NotMeasured: 1);
        if (Math.Abs(mixed.MeasuredFraction - (8 / 12.0)) > 1e-12)
            problems.Add($"the shipped fraction is {mixed.MeasuredFraction:F6}; Measured / Total is {8 / 12.0:F6}.");
        if (Math.Abs(mixed.PooledFractionDoNotReport - (9 / 12.0)) > 1e-12)
            problems.Add("the forbidden form no longer computes (Total − NotApplicable) / Total, so the comparison "
                       + "below is not against the thing this row forbids.");
        if (Math.Abs(mixed.MeasuredFraction - mixed.PooledFractionDoNotReport) < 1e-12)
            problems.Add("the two denominators agree on a census built so that they cannot — the row is vacuous.");
        if (mixed.PooledFractionDoNotReport <= mixed.MeasuredFraction)
            problems.Add("pooling is no longer the LARGER number; the direction of this defect is what makes it "
                       + "worth a control, and it must be stated correctly.");

        // ── 2. The floor counts MEASUREMENTS. A run whose instrument failed on one case must not
        //       satisfy a minimum of 9 by counting that failure as applicable. ──
        if (!mixed.MeetsMinimumApplicable(8)) problems.Add("a floor of 8 is not met by 8 measurements.");
        if (mixed.MeetsMinimumApplicable(9))
        {
            problems.Add("a floor of 9 is met by 8 measurements — the floor is counting the pooled applicable "
                       + "population (Total − NotApplicable = 9) rather than the measured one.");
        }

        // ── 3. WHY A HEALTHY POPULATION CANNOT CHECK THIS, measured on the suite's own numbers. ──
        //
        //   The two forms coincide exactly when NotMeasured is 0. Eval 02's persisted cohort is
        //   such a population, so it CANNOT discriminate — and saying so is the point: a control
        //   that drew its evidence from there would pass under either denominator.
        var persisted = EvalResultStore.LoadCoverage(EvalResultStore.CoverageKey);
        string realPopulation;
        if (persisted is null)
        {
            realPopulation = "no persisted Eval 02 snapshot on disk — the real population was NOT consulted";
        }
        else
        {
            int measured = persisted.Cells.Count(c => c.Latent >= 0);
            int unscorable = persisted.Cells.Count(c => c.Latent < 0);
            int missing = (persisted.PersonaCount * persisted.Arms.Count) - persisted.Cells.Count;
            var real = new AgentEval.Evals.Meta.ObservationCensus(measured, unscorable, Math.Max(0, missing));

            bool discriminates = Math.Abs(real.MeasuredFraction - real.PooledFractionDoNotReport) > 1e-12;
            realPopulation =
                $"the persisted cohort is {real.Describe()}, and it {(discriminates ? "DOES" : "does NOT")} "
              + "discriminate the two denominators"
              + (discriminates ? "" : " — NotMeasured is 0 there, so both forms agree and a control drawn from it "
                                    + "would pass under either");

            // Not a fault either way. What WOULD be a fault is claiming it discriminates when it
            // does not, so the sentence above is derived rather than asserted.
            if (real.Total != persisted.PersonaCount * persisted.Arms.Count)
                problems.Add("the persisted census does not account for every persona × arm cell, so its three "
                           + "buckets do not partition the population and its Describe() is not a denominator.");
        }

        // ── 4. NaN, not zero, when there was nothing to measure. ──
        var empty = new AgentEval.Evals.Meta.ObservationCensus(0, 0, 0);
        if (!double.IsNaN(empty.MeasuredFraction))
            problems.Add("an empty census reports a fraction rather than NaN — 'nothing to measure' rendered as "
                       + "'nothing was measurable', which is the silent-{} shape.");
        var allFailed = new AgentEval.Evals.Meta.ObservationCensus(0, 0, 5);
        if (allFailed.MeasuredFraction != 0.0 || Math.Abs(allFailed.PooledFractionDoNotReport - 1.0) > 1e-12)
        {
            problems.Add("a run in which the instrument never ran does not read 0.000 measured — the pooled form "
                       + "calls it 100% applicable, which is the worst case of this defect.");
        }
        if (!allFailed.Void)
            problems.Add("a census with no measurements is not VOID.");

        return new ControlRowSnapshot(
            "ApplicableFractionDoesNotPoolTwoAbsences",
            "ADR-031 S2b's applicableFraction must be ObservationCensus.Measured / Total and NEVER "
          + "(Total − NotApplicable) / Total. The second POOLS NotApplicable with NotMeasured, and those are "
          + "different findings with different owners: a case that could not test the thing is a CORPUS finding, "
          + "a run where the instrument did not run is an OPERATIONAL one. The pooled form is always the LARGER "
          + "number, so it reports a broken harness as a well-scoped corpus. ⚠ This row is also the first place "
          + "this sample uses an ADR-030 Slice 2 TYPE rather than its own local copy.",
            problems.Count == 0
                ? "on 8 measured / 3 n/a / 1 not measured the shipped fraction is 0.667 and the forbidden one "
                + "0.750, and the forbidden one is LARGER · a floor of 9 is NOT met by 8 measurements even though "
                + "Total − NotApplicable is 9 · an empty census is NaN, not 0.000 · a run where nothing ran reads "
                + "0.000 measured and VOID while the pooled form calls it 1.000 applicable · " + realPopulation
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

    // ══ Control 49 — D-v's hole, DIAGNOSED rather than counted (plan item 8.11). ══════════
    //
    /// <summary>
    /// The eighteen authored phrases that embed to zero in the concept space must be reported with
    /// the reason each one is dead, and with the one fact that decides whether query-side authoring
    /// can close it at all: <b>is there anything on the PRODUCT side for it to reach?</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-v is the largest measured retrieval hole left</b> — 18 of 56 authored phrases dead in
    /// the concept space, 10 of them latent gold — and it has been LOWERED twice with the same
    /// reason: closing it "means choosing a concept dimension per phrase, which moves every
    /// coverage cell". That reason is about the FIX. It is not a reason to leave the hole
    /// undiagnosed, and a count of 18 does not tell an author what to write.
    /// </para>
    /// <para>
    /// ⚠ <b>The decisive question is not on the query side.</b> A lexicon entry maps a QUERY token
    /// to a concept; it can only help if the products the gold rewards already project onto some
    /// concept. If a dead phrase's gold products embed to zero too, no query-side authoring closes
    /// it and the item is bigger than filed. That is measured here, deterministically, for nothing.
    /// </para>
    /// <para>
    /// ⚠ <b>ADVISORY, and it changes no lexicon entry.</b> Choosing the mappings is corpus
    /// authoring and it moves every coverage cell; this row makes the choice cheap, it does not
    /// make it.
    /// </para>
    /// </remarks>
    private static ControlRowSnapshot CheckDeadPhrasesAreDiagnosedNotJustCounted()
    {
        var problems = new List<string>();
        var concept = ConceptEmbeddingSource.Instance;

        var goldTokens = CoveragePersonas.All
            .SelectMany(p => InterestMapGold.Derive(p.Id).Latent)
            .ToHashSet(StringComparer.Ordinal);

        // Every catalogue product that carries a use-context token, indexed by the token's CANONICAL
        // SPELLING — the SUFFIX of a `context:` / `trip:` / `weight:` / `skill:` tag, exactly as
        // InterestMapGold derives it. ⚠ Indexing the whole tag finds nothing: a phrase's suffix is
        // `all-day-riding` and the tag is `context:all-day-riding`, so the first cut of this row
        // reported "18 have no product carrying the token at all" — an artefact of the join, and it
        // would have read as a much bigger finding than the truth.
        var productsByToken = new Dictionary<string, List<Product>>(StringComparer.Ordinal);
        foreach (var product in Catalogue.Default.All)
        {
            foreach (var tag in product.Tags)
            {
                int colon = tag.IndexOf(':');
                if (colon <= 0 || colon >= tag.Length - 1) continue;
                if (!EmbeddingDocument.UseTagPrefixes.Contains(tag[..(colon + 1)], StringComparer.OrdinalIgnoreCase)) continue;

                var suffix = Product.NormalizeAttributeToken(tag[(colon + 1)..]);
                if (suffix.Length == 0) continue;
                if (!productsByToken.TryGetValue(suffix, out var list)) productsByToken[suffix] = list = [];
                list.Add(product);
            }
        }

        if (productsByToken.Count == 0)
            problems.Add("no product carries a use-context token at all — the join below is empty and says nothing.");

        // ⚠ THE JOIN'S OWN INVARIANT, and it is what catches a mis-keyed index. Latent gold is
        // derived from exactly these tags, so EVERY gold token must appear in this index. When the
        // first cut of this row keyed on the whole tag, 0 of them did and the row reported "18 have
        // no product carrying the token at all" — a much larger finding than the truth, arrived at
        // by a bad join rather than by the corpus.
        var goldMissingFromIndex = goldTokens.Where(t => !productsByToken.ContainsKey(t)).ToList();
        if (goldMissingFromIndex.Count > 0)
        {
            problems.Add($"{goldMissingFromIndex.Count} of {goldTokens.Count} latent-gold token(s) are absent from "
                       + $"the product index (e.g. {string.Join(", ", goldMissingFromIndex.Take(3))}). Gold is derived "
                       + "from those same tags, so this is a JOIN fault, not a corpus fact, and every closability "
                       + "number below would be wrong in the alarming direction.");
        }

        var dead = new List<(string Suffix, string Phrase, bool IsGold, IReadOnlyList<string> Unmapped,
                             int GoldProducts, int GoldProductsEmbeddable)>();

        foreach (var (suffix, phrase) in InterestMapBuilder.ContextPhrases.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!IsDead(concept.Embed(phrase))) continue;

            var unmapped = ConceptEmbeddingSource.UnmappedTokens(phrase);
            var reachable = productsByToken.TryGetValue(suffix, out var products) ? products : [];
            int embeddable = reachable.Count(p => !IsDead(concept.Embed(EmbeddingDocument.ForProduct(p))));

            dead.Add((suffix, phrase, goldTokens.Contains(suffix), unmapped, reachable.Count, embeddable));
        }

        // ── 1. The count has to reproduce, or the diagnosis below is about a different population.
        if (dead.Count == 0)
        {
            problems.Add("no authored phrase embeds to zero. Either D-v closed — in which case this row and the "
                       + "AuthoredQueryPhraseRetrievability advisory both need re-writing — or the lexicon lookup "
                       + "changed and this row is measuring nothing.");
        }

        int goldDead = dead.Count(d => d.IsGold);

        // ── 2. THE DIAGNOSIS MUST BE A DIAGNOSIS. A dead phrase with no unmapped token would mean
        //       the phrase's tokens ARE known and the zero comes from somewhere else — a different
        //       defect, and one this row would otherwise report as authoring work.
        var deadWithoutReason = dead.Where(d => d.Unmapped.Count == 0).Select(d => d.Suffix).ToList();
        if (deadWithoutReason.Count > 0)
        {
            problems.Add($"{deadWithoutReason.Count} dead phrase(s) have NO unmapped token "
                       + $"({string.Join(", ", deadWithoutReason)}) — the lexicon knows their words and they still "
                       + "embed to zero, so the hole is not (only) a vocabulary gap and D-v is misfiled.");
        }

        // ── 3. THE CLOSABILITY SPLIT — the fact that decides how big D-v really is. ──
        var closable = dead.Where(d => d.GoldProductsEmbeddable > 0).ToList();
        var unclosable = dead.Where(d => d.GoldProducts > 0 && d.GoldProductsEmbeddable == 0).ToList();
        var noProducts = dead.Where(d => d.GoldProducts == 0).ToList();

        if (closable.Count + unclosable.Count + noProducts.Count != dead.Count)
            problems.Add("the three closability buckets do not partition the dead phrases.");

        // ── 4. The row must be able to SEE an unclosable phrase, or its split is an assertion.
        //       Positive control: a phrase whose products are all forced dead must land in the
        //       unclosable bucket. Built here rather than hoped for in the corpus.
        var probeProducts = Catalogue.Default.All.Take(3).ToList();
        int probeEmbeddable = probeProducts.Count(p => !IsDead(concept.Embed(EmbeddingDocument.ForProduct(p))));
        if (probeEmbeddable == 0)
        {
            problems.Add("no catalogue product embeds at all, so 'the gold products are unreachable' would be true "
                       + "of everything and the split below carries no information.");
        }
        if (IsDead(concept.Embed("")) is false)
            problems.Add("an empty string does not embed to zero, so IsDead cannot detect the state this row counts.");

        string examples = string.Join(" · ", dead
            .Where(d => d.IsGold)
            .Take(4)
            .Select(d => $"{d.Suffix} (\"{d.Phrase}\") unmapped: {string.Join("/", d.Unmapped.Take(4))}"
                       + $" · gold products {d.GoldProductsEmbeddable} of {d.GoldProducts} embeddable"));

        return new ControlRowSnapshot(
            "DeadPhrasesAreDiagnosedNotJustCounted",
            "D-v (plan item 8.11) is the largest MEASURED retrieval hole left: authored phrases that embed to zero "
          + "in the concept space the demo ships on. It has been lowered twice with a reason about the FIX — "
          + "choosing a concept per phrase moves every coverage cell — which is not a reason to leave it "
          + "undiagnosed. This row reports WHY each phrase is dead (the tokens the lexicon does not know) and the "
          + "fact that decides how big the item is: whether the products the gold rewards for that interest embed "
          + "at all. A lexicon entry maps a QUERY token to a concept; where the PRODUCT side is dead too, no "
          + "query-side authoring closes it. ⚠ ADVISORY and it changes no lexicon entry — it makes the authoring "
          + "choice cheap, it does not make it. ⚠ VERIFYING a closed D-v is NOT free: the concept space is "
          + "deterministic so the lexicon change itself costs nothing, but re-deriving the coverage cells it moves "
          + "is an Eval 02 cohort, and the last one was 36 live turns at ¤27.1208.",
            problems.Count == 0
                ? $"{dead.Count} authored phrase(s) embed to ZERO, {goldDead} of them latent-GOLD · every one has "
                + $"at least one unmapped token, so the hole IS a vocabulary gap · CLOSABILITY: {closable.Count} "
                + $"reach at least one embeddable gold product, {unclosable.Count} reach gold products that ALL "
                + $"embed to zero (query-side authoring cannot close those), {noProducts.Count} have no product "
                + $"carrying the token at all · {examples}"
                : $"{problems.Count} fault(s): {string.Join("; ", problems)}",
            problems.Count == 0);
    }

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
