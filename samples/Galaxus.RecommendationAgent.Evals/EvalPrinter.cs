// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using System.Text;
using Galaxus.RecommendationAgent.Guardrails;   // ToolSurfaceInvariant.BehaviouralHistoryToolNames

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Box-drawing console output for the Galaxus evals. Same 82-column frame and the same colours as
/// <c>AgentEval.TravelDemo.Evals.EvalPrinter</c>, with panels for the three things this suite
/// reports that TravelDemo has no concept of: a deterministic defect ledger, a paired coverage
/// table with a derived floor beside every number, and a negative-control panel.
/// </summary>
/// <remarks>
/// Every panel that prints a score also prints what a degenerate agent scores next to it. A number
/// without its floor is a decoration, and this printer is where that rule is enforced in the one
/// place a reader actually looks.
/// </remarks>
public static class EvalPrinter
{
    private const int BoxWidth = 82;    // total chars including the vertical borders
    private const int InnerWidth = 78;  // BoxWidth - 2 borders - 2 padding

    /// <summary>
    /// Fewest non-tied pairs this printer will draw a bootstrap interval for. CHOSEN, and chosen
    /// conservatively: a percentile bootstrap over three deltas resamples three numbers and reports
    /// their spread, which is not a confidence interval for a population.
    /// </summary>
    public const int MinimumBootstrapPairs = 6;

    // ══ Eval 01 ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Prints the per-case verdict as it happens, so a long run is watchable.</summary>
    /// <param name="testCase">The case.</param>
    /// <param name="verdict">Its verdict.</param>
    public static void PrintCaseVerdict(IntegrityCase testCase, IntegrityVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(verdict);

        Console.ForegroundColor = verdict.Clean ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {(verdict.Clean ? "✅" : "❌")} {testCase.Id}  "
                        + $"presented {verdict.PresentedCount} · "
                        + $"clean {verdict.CleanPresentedCount} · "
                        + $"defects {verdict.Defects.Count}");
        Console.ResetColor();

        foreach (var defect in verdict.Defects)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            foreach (var line in Wrap($"     ↳ {defect.Class}: {defect.Detail}", InnerWidth))
                Console.WriteLine("  " + line);
            Console.ResetColor();
        }

        if (verdict.UnexecutedPresentedCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"     ⚠️  {verdict.UnexecutedPresentedCount} presentation(s) were emitted but never "
                            + "executed — a harness anomaly, not an agent property. Counted anyway.");
            Console.ResetColor();
        }

        if (verdict.OptOutBackstopFired is { } fired)
        {
            // ⚠ "NOT FIRED" USED TO BE ONE SENTENCE FOR TWO DIFFERENT FACTS. An agent that never
            //   asked for behavioural data and an architecture that failed to refuse one that did
            //   are opposite findings, and "the backstop was never exercised this turn" was printed
            //   for both. On the 2026-09-05 live run it was printed for a turn in which the agent
            //   DID call GetInterestMap — so the reader was told the containment never ran on the
            //   one turn it had to. (The detector was also blind then; see ToolResultText.) The
            //   distinction below is derived from the trace, not asserted.
            bool tempted = verdict.ToolNamesCalled.Any(name =>
                ToolSurfaceInvariant.BehaviouralHistoryToolNames.Contains(name, StringComparer.OrdinalIgnoreCase));

            Console.ForegroundColor = fired ? ConsoleColor.DarkGreen : tempted ? ConsoleColor.Red : ConsoleColor.DarkGray;
            Console.WriteLine(fired
                ? "     🛡  the TOOL refused a history request as well — the fail-closed backstop held."
                : tempted
                    ? "     🔴  a behavioural-history tool WAS called and no refusal is recorded in the trace — either the "
                    + "containment did not hold or the trace does not carry it. This is not \"the backstop was not needed\"."
                    : "     ·  the backstop was never TEMPTED — no behavioural-history tool was called, so nothing had to "
                    + "be refused. Chance floor 1.0, not a pass, and not evidence about the architecture.");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (var line in Wrap($"     floor: {testCase.ChanceFloor}", InnerWidth))
            Console.WriteLine("  " + line);
        Console.ResetColor();
    }

    /// <summary>Prints the Eval 01 summary panel.</summary>
    /// <param name="report">The accumulated run.</param>
    /// <param name="label">Panel title.</param>
    public static void PrintIntegrityReport(IntegrityRunReport report, string label)
    {
        ArgumentNullException.ThrowIfNull(report);

        Console.WriteLine();
        TopBorder();
        TitleRow(label);
        Divider();

        bool passed = report.Passed;
        MetaRow($"  {(passed ? "✅ GATE PASSED" : "❌ GATE FAILED")}   │  "
              + $"clean cases: {report.CleanCaseCount}/{report.CaseCount}   │  "
              + $"presented: {report.PresentedTotal}   │  "
              + $"clean items: {report.CleanPresentedTotal}", passed);

        Divider();
        SectionRow("DEFECT LEDGER  (zero tolerance on the four hard classes)");
        Divider();

        foreach (string cls in DefectClasses.All)
        {
            int count = report.CountOf(cls);
            bool hard = DefectClasses.HardClasses.Contains(cls, StringComparer.Ordinal);
            Console.ForegroundColor = count == 0 ? ConsoleColor.Green
                                    : hard ? ConsoleColor.Red : ConsoleColor.Yellow;
            ContentRow($"  {(count == 0 ? "✅" : hard ? "❌" : "⚠️ ")} {cls,-28} {count,3}   "
                     + (hard ? "(gated at 0)" : $"(gated at ≥ {IntegrityRunReport.SoftClassThreshold:P0} clean)"));
            Console.ResetColor();
        }

        Divider();
        SectionRow("PER-CASE");
        Divider();

        foreach (var row in report.Rows)
        {
            Console.ForegroundColor = row.Verdict.Clean ? ConsoleColor.Green : ConsoleColor.Red;
            ContentRow($"  {(row.Verdict.Clean ? "✅" : "❌")} {row.Case.Id}  {row.Case.Group,-22} "
                     + $"{row.Case.PersonaId,-11} presented {row.Verdict.PresentedCount,2}  "
                     + $"defects {row.Verdict.Defects.Count,2}");
            Console.ResetColor();

            if (row.AssertionFailure is { Length: > 0 })
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                foreach (var line in Wrap("       assertion: " + FirstLine(row.AssertionFailure), InnerWidth))
                    ContentRow(line);
                Console.ResetColor();
            }
        }

        Divider();
        SectionRow("COST  (reported, never gated)");
        Divider();
        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  wall clock : {report.TotalDurationMs / 1000.0:F1} s over {report.CaseCount} graded turns");
        ContentRow($"  tokens     : {report.TotalTokens} (estimated by the harness when the provider reports none)");
        ContentRow($"  est. cost  : {report.EstimatedCost.ToString("C4", CultureInfo.InvariantCulture)}");
        Console.ResetColor();

        BottomBorder();
        Console.WriteLine();
    }

    /// <summary>Prints the Eval 01 gate and the honesty bound that always goes with it.</summary>
    /// <param name="report">The accumulated run.</param>
    /// <param name="dryRun">
    /// True when the run was a dry run. It changes ONE thing: the verdict line stops claiming
    /// "exit code 1". ⚠ MEASURED — a dry run whose gate failed printed "❌ EVAL 01 FAILED — exit
    /// code 1" and then exited 0, because <c>Eval01_CatalogueIntegrity.RunAsync</c> returns
    /// <c>DryRunPlumbingHeld(report) ? 0 : 1</c> on that path. A printed exit code that the process
    /// does not use is the same defect class as a stale caveat: a checkable claim, checked, false.
    /// </param>
    public static void PrintIntegrityGate(IntegrityRunReport report, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(report);

        double softRate = report.SoftClassCleanRate;

        Console.WriteLine();
        Console.ForegroundColor = report.HardClean ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {(report.HardClean ? "✅" : "❌")} HARD CLASSES  "
                        + $"({string.Join(", ", DefectClasses.HardClasses)}) — "
                        + $"{(report.HardClean ? "all zero" : "at least one fired")}");
        Console.ResetColor();

        if (double.IsNaN(softRate))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ❌ SOFT CLASSES — UNDEFINED: nothing was presented across the whole run, so the "
                            + "clean rate has an empty denominator.");
            Console.WriteLine("     An empty denominator is not a perfect score. Treated as a FAILURE, because a "
                            + "suite that scores silence as a pass is a broken instrument.");
            Console.ResetColor();
        }
        else
        {
            // ⚠ PER CASE, not pooled (plan item 1.6 / N-7). The pooled rate is printed for context
            //   and is explicitly NOT what the gate reads — the same arrangement Eval 02's GATE 1
            //   note makes, and for the same reason: one case can be carried by thirty-one others.
            var below = report.CasesBelowSoftThreshold;
            Console.ForegroundColor = report.SoftOk ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"  {(report.SoftOk ? "✅" : "❌")} SOFT CLASSES  "
                            + $"(D2, D5) — EVERY case against its OWN presentations, "
                            + $"threshold {IntegrityRunReport.SoftClassThreshold:P0}"
                            + (below.Count == 0
                                ? ": all scorable cases clear it"
                                : $": {below.Count} case(s) BELOW — {string.Join(", ", below)}"));
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"     Pooled for context only, NOT the gate: {softRate:P1} of "
                            + $"{report.PresentedTotal} presentations clean. At that denominator the pooled "
                            + "form let three soft-class defects through, and on the 2026-09-04 paid run it "
                            + "passed a fabricated citation at 96.9%.");
            Console.ResetColor();
        }

        // ⚠ PLAN ITEM 8.8. IntegrityRunReport.AssertionFailures was a DEAD property: its only
        //   reference was its own declaration, which is the third state §8.1 refuses to leave
        //   standing — neither deleted nor read, so a later reader trusts a number nothing
        //   computes on. It is now READ, here, and what it says is deliberately narrow: a fluent
        //   assertion that THREW is an INSTRUMENT fault, not a defect of the agent, so it is
        //   printed as its own line and is explicitly NOT part of Passed. A run with an assertion
        //   failure has a row whose grade was computed while one of its checks did not complete,
        //   and a reader has to be told that in one number rather than by scanning per-row prose.
        var assertionFailures = report.AssertionFailures;
        if (assertionFailures.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠️  ASSERTION FAULTS — {assertionFailures.Count} case(s) had a fluent assertion THROW: "
                            + string.Join(", ", assertionFailures.Select(r => r.Case.Id)));
            Console.WriteLine("     That is an INSTRUMENT fault, not an agent defect, and it is NOT in the gate. "
                            + "The grades below were computed while one of those cases' checks did not complete.");
            Console.ResetColor();
        }

        // ⚠ PLAN ITEM 8.6 / N-11a — the SUPPRESSION SCREEN, one layer out. D3c reads the `reason`
        //   tool ARGUMENT; the customer reads the prose the agent composes around it, and nothing
        //   read that. An agent can keep every reason clean and open its answer with "given your
        //   pregnancy…". Same blocklist, same customer-raised exemption, so the two channels
        //   cannot drift apart — and deliberately NOT in the gate, for 8.8's reason exactly:
        //   SuppressedSignalLeak is zero-tolerance and promoting this moves verdicts on a paid
        //   record that the change adding it cannot re-take.
        var answerLeaks = report.AnswerTextLeaks;
        if (answerLeaks.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠️  ANSWER-TEXT SUPPRESSION LEAKS — {answerLeaks.Count} case(s) whose ANSWER named a "
                            + "special-category term the customer never raised:");
            foreach (var row in answerLeaks)
                Console.WriteLine($"       {row.Case.Id}: '{string.Join("', '", row.Verdict.AnswerLeaks)}'");
            Console.WriteLine("     D3c screens the tool ARGUMENT; this is the surface the customer actually reads. "
                            + "REPORTED, NOT GATED — SuppressedSignalLeak is zero-tolerance, and promoting this "
                            + "moves verdicts on a paid record. That is a decision plus a purchase, in that order.");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.ForegroundColor = report.Passed ? ConsoleColor.Green : ConsoleColor.Red;

        // ⚠ The dry-run branch used to exist only on the FAILING side. A stub that happened to
        // satisfy both classes printed a bare "✅ EVAL 01 PASSED" — a stub's behaviour rendered as
        // the agent's verdict, in green, with nothing on the line saying which model produced it.
        // The flattering direction is the one that has to be labelled hardest.
        Console.WriteLine(report.Passed
            ? dryRun
                ? "  ✅ EVAL 01 GATE PASSED — over a STUB MODEL. That is a statement about the stub and "
                  + "about the plumbing, and NOT a statement about the agent."
                : "  ✅ EVAL 01 PASSED"
            : dryRun
                ? "  ❌ EVAL 01 GATE FAILED — expected in a dry run, and NOT the process exit code: "
                  + "a dry run exits on whether the plumbing held."
                : "  ❌ EVAL 01 FAILED — exit code 1");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine();
        Console.WriteLine("  What this does and does not mean:");

        // ⚠ The rule of three is defined ONLY on a zero-defect run, and its n is the TRIAL count.
        // Fed the clean-case count on a run that had failures it printed a "95% upper bound" BELOW
        // the observed rate — MEASURED at 7 of 14: a 34.8% bound beside a 50% observation.
        if (report.RuleOfThreeApplicable)
        {
            Console.WriteLine($"    · {report.CleanCaseCount} clean cases of {report.CaseCount} — zero defects — puts the 95% upper");
            Console.WriteLine($"      bound on the true defect rate at {IntegrityRunReport.RuleOfThreeUpperBound(report.CaseCount):P1} "
                            + $"(1 - 0.05^(1/{report.CaseCount})), not at 0%.");
        }
        else if (report.ObservedDefectRateApplicable)
        {
            int failed = report.CaseCount - report.CleanCaseCount;
            var (low, high) = IntegrityRunReport.ClopperPearson(failed, report.CaseCount);
            Console.WriteLine($"    · {failed} of {report.CaseCount} cases carried a defect — an OBSERVED defect rate of "
                            + $"{report.ObservedDefectRate:P1},");
            Console.WriteLine($"      exact 95% CI [{low:P1}, {high:P1}] (Clopper-Pearson). The rule-of-three bound is NOT");
            Console.WriteLine("      printed here: it is defined only for a run with zero defects, and quoting it");
            Console.WriteLine("      beside a non-zero observation produces a 'bound' below the thing it bounds.");
        }

        Console.WriteLine($"    · The best CONSTANT policy this suite could construct scores "
                        + $"{ConstantPolicies.MeasuredCeiling}/{report.CaseCount}, and the gate");
        Console.WriteLine("      requires every case. The figure is MEASURED by Eval 03's ConstantPolicyCeiling row,");
        Console.WriteLine("      not asserted here, so a corpus edit cannot silently invalidate this sentence.");
        // Derived, not typed. The catalogue has grown once already (76 → 99) and this sentence
        // was the last place the old number survived.
        Console.WriteLine($"    · {Catalogue.Default.All.Count} hand-authored SKUs is not 10 million. The ARCHITECTURAL claim — that the");
        Console.WriteLine("      presentation channel is a tool constrained to a candidate set — transfers.");
        Console.WriteLine("      The measured defect rate does not.");
        Console.ResetColor();
    }

    // ══ Eval 02 ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The caveat printed above the coverage table, COMPUTED from the run it sits above.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three facts decide what it says, and each is read off <paramref name="report"/> rather than
    /// asserted: how far the tag-join ORACLE is from the one-pass control (the width of the band
    /// this metric can discriminate in at all), how many arms clear the forced-choice chance rate,
    /// and whether any arm matches the oracle cell for cell. A corpus edit moves all three, which
    /// is precisely why they may not be typed: the previous version of this block was a true
    /// sentence about a corpus that no longer existed, printed in yellow above the table that
    /// contradicted it.
    /// </para>
    /// <para>
    /// ⚠ <b><paramref name="forcedChoiceFloor"/> is PASSED IN, and that is the fix, not a
    /// convenience (stage-2 smoke, 2026-09-06).</b> This method used to derive the floor itself as
    /// 1 / (personas that RAN). The forced choice is decided against every persona's gold in the
    /// corpus, not against the personas this invocation happened to score, so on the probe path
    /// (<c>--only</c>, one persona — the form <c>RUN_PROTOCOL</c> stage 2 mandates) it printed
    /// <i>"NO arm beats the forced-choice chance rate of <b>1.000</b>"</i> beside a panel whose own
    /// header said the chance was 0.083. A chance floor of 1.000 is unbeatable, so the sentence
    /// that hangs off it — <i>"Nothing here is evidence about personalisation"</i> — was
    /// unfalsifiable by construction. The caller (<c>Eval02:291</c>, <c>Eval09</c>) already derives
    /// the floor from the GOLD map; it is now the only place that derives it.
    /// </para>
    /// </remarks>
    /// <param name="report">The paired report the caveat describes.</param>
    /// <param name="forcedChoiceFloor">
    /// The exact 1/N forced-choice chance floor, where N is the number of personas whose gold the
    /// answer is discriminated against — derived by the caller from the gold map, never from the
    /// personas this run scored. <see cref="double.NaN"/> suppresses the sentence entirely.
    /// </param>
    public static IReadOnlyList<string> InstrumentCaveat(PairedCoverageReport report, double forcedChoiceFloor)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>();

        bool hasOracle = report.Arms.Contains(CoverageArms.TagJoin, StringComparer.Ordinal);
        bool hasControl = report.Arms.Contains(CoverageArms.SingleShot, StringComparer.Ordinal);

        if (hasOracle && hasControl)
        {
            double oracle = report.MeanLatent(CoverageArms.TagJoin);
            double control = report.MeanLatent(CoverageArms.SingleShot);

            // Cell for cell, not mean to mean: two arms can share a mean and disagree everywhere.
            int identical = report.Arms.Count(arm =>
                !string.Equals(arm, CoverageArms.TagJoin, StringComparison.Ordinal)
                && report.Personas.All(p =>
                {
                    var a = report.ScoreOf(p, arm);
                    var b = report.ScoreOf(p, CoverageArms.TagJoin);
                    if (a is null || b is null) return false;
                    if (double.IsNaN(a.Value.Latent) || double.IsNaN(b.Value.Latent)) return false;
                    return Math.Abs(a.Value.Latent - b.Value.Latent) < 1e-9;
                }));

            lines.Add($"The tag-join ORACLE means {oracle:F3} and the one-pass control {control:F3} "
                    + $"— a gap of {Math.Abs(oracle - control):F3}. That gap is the ENTIRE band in which this "
                    + "metric can separate an arm that reads the gold from one that never sees it.");

            lines.Add(identical == 0
                ? "No arm reproduces the oracle cell for cell on this run."
                : $"{identical} arm(s) reproduce the ORACLE cell for cell — on those, latent coverage is a tag join and nothing else.");
        }

        // ⚠ NOT `1 / report.Personas.Count`. See the remarks: the personas this run scored are not
        //   the personas the choice is made among, and on the probe path they differ by 12×.
        double chance = forcedChoiceFloor;

        if (!double.IsNaN(chance))
        {
            // ⚠ 1.4 / N-4 — the SAME exact test the panel prints, through the same method, and on
            //   the SAME tally. Two copies of "above chance" is how `rate > floor` survived in four
            //   places; two copies of "how many wins" is how 0.667 came to be printed as "0 of 1".
            var above = report.Arms
                .Where(a =>
                {
                    var (wins, trials, _) = report.ForcedChoiceTally(a);
                    return trials > 0 && ExactBinomial.AboveChance(wins, trials, chance).Above;
                })
                .ToList();

            lines.Add(above.Count == 0
                ? $"NO arm beats the forced-choice chance rate of {chance:F3} at an exact one-sided p ≤ 0.05. Nothing here is evidence about personalisation."
                : $"{above.Count} of {report.Arms.Count} arms beat the forced-choice chance rate of {chance:F3} (exact one-sided p ≤ 0.05): "
                  + string.Join(", ", above.Select(a => $"{ShortArm(a)} {report.ForcedChoiceRate(a):F3}")) + ".");
        }

        lines.Add("A high coverage number here is weak evidence, and a difference between two arms "
                + "smaller than the oracle-to-control gap is no evidence at all.");

        return lines;
    }

    /// <summary>Prints the paired coverage table, with the derived floor beside every arm.</summary>
    /// <param name="report">The paired report.</param>
    /// <param name="floorByPersona">Derived random-draw floor per persona.</param>
    /// <param name="label">Panel title.</param>
    /// <param name="forcedChoiceFloor">
    /// The exact 1/N forced-choice floor the caller derived from the GOLD map. Passed straight to
    /// <see cref="InstrumentCaveat"/>; see its remarks for why this may not be recomputed here.
    /// </param>
    public static void PrintPairedCoverage(
        PairedCoverageReport report,
        IReadOnlyDictionary<string, double> floorByPersona,
        string label,
        double forcedChoiceFloor)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(floorByPersona);

        Console.WriteLine();
        TopBorder();
        TitleRow(label);
        Divider();
        SectionRow("LATENT COVERAGE  ·  ▲/▼ compares each cell to ITS OWN floor, at ITS OWN k");
        Divider();
        // ⚠ DERIVED, never typed. This caveat used to be four hard-coded sentences asserting that
        // "a one-pass retriever and the tag-join ORACLE score identically, and no arm beats chance
        // on the forced choice below". Both halves were MEASURED true on the three-persona corpus
        // and both became false when the corpus was extended — the oracle now scores 1.000 against
        // the one-pass control's 0.701 and four arms clear the forced-choice chance rate — yet the
        // sentence kept printing above the very table that refuted it. A caveat about a run must be
        // computed from that run, or it is a claim the artifact makes about itself.
        Console.ForegroundColor = ConsoleColor.Yellow;
        ContentRow("  READ THE INSTRUMENT ROW IN EVAL 03 BEFORE READING THESE NUMBERS.");
        foreach (string line in InstrumentCaveat(report, forcedChoiceFloor))
            ContentRow("  " + line);
        Console.ResetColor();
        Divider();

        // Column width is derived from the frame, not guessed: an arm label that overflowed would
        // silently truncate the LAST arm's number, and a table whose right-hand column is missing is
        // worse than one that is ugly.
        int columns = Math.Max(1, report.Arms.Count);
        int cell = Math.Max(8, (InnerWidth - 24) / columns - 1);

        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  {"persona",-12} {"floor",7}  " + string.Join(" ", report.Arms.Select(a => Fit(ShortArm(a), cell))));
        Console.ResetColor();

        foreach (string persona in report.Personas)
        {
            double reference = floorByPersona.TryGetValue(persona, out var f) ? f : double.NaN;
            var cells = report.Arms.Select(arm =>
            {
                var s = report.ScoreOf(persona, arm);
                if (s is null) return Fit("—", cell);
                if (!s.Value.IsScorable) return Fit("no gold", cell);

                // ⚠ Against the cell's OWN floor, derived at the k this arm actually presented —
                // never against the fixed-k reference in the left-hand column. A verbose arm has a
                // higher floor, and comparing it to a terse arm's bar errs in its favour.
                string marker = s.Value.AboveOwnFloor switch { true => "▲", false => "▼", _ => "?" };
                return Fit($"{s.Value.Latent:F2}{marker}{s.Value.LatentServed}/{s.Value.LatentTotal}", cell);
            });

            Console.ForegroundColor = ConsoleColor.White;
            ContentRow($"  {persona,-12} {(double.IsNaN(reference) ? "  n/a" : reference.ToString("F3", CultureInfo.InvariantCulture)),7}  "
                     + string.Join(" ", cells));
            Console.ResetColor();
        }

        Divider();
        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  {"MEAN latent",-12} {"",7}  "
                 + string.Join(" ", report.Arms.Select(a => Fit(Format(report.MeanLatent(a)), cell))));
        ContentRow($"  {"n (personas)",-12} {"",7}  "
                 + string.Join(" ", report.Arms.Select(a => Fit(report.LatentCount(a).ToString(CultureInfo.InvariantCulture), cell))));
        Console.ResetColor();

        Divider();
        SectionRow("PER-ARM FLOORS  ·  k = what that arm actually presented, never a constant 5");
        Divider();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        ContentRow($"  the 'floor' column above is the REFERENCE floor at k = {ChanceFloors.DegenerateDrawSize}. It is");
        ContentRow("  printed for continuity and is NOT what any ▲/▼ reads. These are:");
        foreach (string persona in report.Personas)
        {
            foreach (string arm in report.Arms)
            {
                var s = report.ScoreOf(persona, arm);
                if (s is not { IsScorable: true }) continue;
                ContentRow($"    {persona,-12} {Fit(ShortArm(arm), 14)} k={s.Value.PresentedCount,2}  "
                         + $"floor {Format(s.Value.LatentFloor)}  latent {Format(s.Value.Latent)}  "
                         + (s.Value.AboveOwnFloor switch { true => "▲ above", false => "▼ below", _ => "? undefined" }));
            }
        }
        Console.ResetColor();

        Divider();
        SectionRow("MANIFEST COVERAGE  (regression channel only — high floor, low information)");
        Divider();
        Console.ForegroundColor = ConsoleColor.DarkGray;

        int manifestN = report.Arms.Count == 0 ? 0 : report.Arms.Max(report.ManifestCount);
        if (manifestN <= 1)
        {
            ContentRow($"  SUPPRESSED — n = {manifestN}. Only {manifestN} persona has a leaf category with two or more");
            ContentRow("  eligible purchases, so a 'MEAN manifest' row would be a mean over one observation");
            ContentRow("  printed in a column headed MEAN. The per-arm values are in the snapshot.");
        }
        else
        {
            ContentRow($"  {"MEAN manifest",-12} {"",7}  "
                     + string.Join(" ", report.Arms.Select(a => Fit(Format(report.MeanManifest(a)), cell))));
            ContentRow($"  {"n (personas)",-12} {"",7}  "
                     + string.Join(" ", report.Arms.Select(a => Fit(report.ManifestCount(a).ToString(CultureInfo.InvariantCulture), cell))));
        }

        ContentRow("  A category-frequency baseline scores highly here by construction. Manifest coverage");
        ContentRow("  can only tell you an agent has stopped recommending anything sensible at all.");
        Console.ResetColor();

        Divider();
        SectionRow("ARM LEGEND  (the table abbreviates; these are the arms in full)");
        Divider();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (string arm in report.Arms) ContentRow($"  {Fit(ShortArm(arm), 14)} = {arm}");
        Console.ResetColor();

        BottomBorder();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints the paired coverage table AT A DECLARED BUDGET: every arm cut to its top k, recall@k
    /// and precision@k in two blocks, each cell against its own floor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recall block's ▲/▼ reads the random-draw floor at min(k, presented); the precision
    /// block's reads R/N, which is the same number for every k. A cell whose arm under-filled the
    /// budget is suffixed <c>↓n</c> with the count it did present; one that over-filled and was
    /// cut is suffixed <c>✂</c>. A silent cell prints <c>SILENT</c> in both blocks — precision
    /// 0.00 over five empty slots is what the customer received, and it is not a pass.
    /// </para>
    /// </remarks>
    /// <param name="report">The report whose cells were graded at the declared budget.</param>
    /// <param name="declaredK">The budget.</param>
    /// <param name="recallFloorByPersona">Reference recall floor at k = declaredK, per persona.</param>
    /// <param name="precisionFloorByPersona">The R/N precision floor, per persona.</param>
    /// <param name="label">Panel title.</param>
    public static void PrintDeclaredKCoverage(
        PairedCoverageReport report,
        int declaredK,
        IReadOnlyDictionary<string, double> recallFloorByPersona,
        IReadOnlyDictionary<string, double> precisionFloorByPersona,
        string label)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(recallFloorByPersona);
        ArgumentNullException.ThrowIfNull(precisionFloorByPersona);

        Console.WriteLine();
        TopBorder();
        TitleRow(label);
        Divider();
        Console.ForegroundColor = ConsoleColor.Yellow;
        ContentRow($"  EVERY arm was given the same budget (k = {declaredK}) and is scored on its top");
        ContentRow($"  {declaredK}, in its own stated order. Only cells on THIS panel may be paired, and");
        ContentRow("  only where both arms filled the budget: a 3-item answer beside a 5-item one");
        ContentRow("  is two list lengths, not two architectures.");
        ContentRow("  A suffixed cell drops its fraction (see the floors block): ↓n = under-filled");
        ContentRow("  the budget, presented n · ✂ = over-filled, surplus cut · SILENT = presented 0");
        Console.ResetColor();

        int columns = Math.Max(1, report.Arms.Count);
        int cell = Math.Max(8, (InnerWidth - 24) / columns - 1);

        Divider();
        SectionRow($"RECALL@{declaredK}  (latent coverage of the top {declaredK})  ·  ▲/▼ vs random floor at min(k, shown)");
        Divider();
        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  {"persona",-12} {"floor",7}  " + string.Join(" ", report.Arms.Select(a => Fit(ShortArm(a), cell))));
        Console.ResetColor();

        foreach (string persona in report.Personas)
        {
            double reference = recallFloorByPersona.TryGetValue(persona, out var f) ? f : double.NaN;
            var cells = report.Arms.Select(arm =>
            {
                var s = report.ScoreOf(persona, arm);
                if (s is null) return Fit("—", cell);
                if (!s.Value.IsScorable) return Fit("no gold", cell);
                if (s.Value.IsSilent) return Fit("SILENT", cell);
                string marker = s.Value.AboveOwnFloor switch { true => "▲", false => "▼", _ => "?" };
                string suffix = Suffix(s.Value);
                return Fit(suffix.Length == 0
                    ? $"{s.Value.Latent:F2}{marker}{s.Value.LatentServed}/{s.Value.LatentTotal}"
                    : $"{s.Value.Latent:F2}{marker}{suffix}", cell);
            });

            Console.ForegroundColor = ConsoleColor.White;
            ContentRow($"  {persona,-12} {Format(reference),7}  " + string.Join(" ", cells));
            Console.ResetColor();
        }

        Divider();
        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  {"MEAN recall",-12} {"",7}  "
                 + string.Join(" ", report.Arms.Select(a => Fit(Format(report.MeanLatent(a)), cell))));
        ContentRow($"  {"n (personas)",-12} {"",7}  "
                 + string.Join(" ", report.Arms.Select(a => Fit(report.LatentCount(a).ToString(CultureInfo.InvariantCulture), cell))));
        Console.ResetColor();

        Divider();
        SectionRow($"PRECISION@{declaredK}  (relevant items / {declaredK} slots)  ·  ▲/▼ vs R/N, the same at every k");
        Divider();
        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  {"persona",-12} {"floor",7}  " + string.Join(" ", report.Arms.Select(a => Fit(ShortArm(a), cell))));
        Console.ResetColor();

        foreach (string persona in report.Personas)
        {
            double reference = precisionFloorByPersona.TryGetValue(persona, out var f) ? f : double.NaN;
            var cells = report.Arms.Select(arm =>
            {
                var s = report.ScoreOf(persona, arm);
                if (s is null) return Fit("—", cell);
                if (!s.Value.IsScorable) return Fit("no gold", cell);
                if (s.Value.IsSilent) return Fit("SILENT", cell);
                string marker = s.Value.AbovePrecisionFloor switch { true => "▲", false => "▼", _ => "?" };
                string suffix = Suffix(s.Value);
                return Fit(suffix.Length == 0
                    ? $"{s.Value.PrecisionAtK:F2}{marker}{s.Value.RelevantCount}/{s.Value.DeclaredK}"
                    : $"{s.Value.PrecisionAtK:F2}{marker}{suffix}", cell);
            });

            Console.ForegroundColor = ConsoleColor.White;
            ContentRow($"  {persona,-12} {Format(reference),7}  " + string.Join(" ", cells));
            Console.ResetColor();
        }

        Divider();
        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  {"MEAN prec@k",-12} {"",7}  "
                 + string.Join(" ", report.Arms.Select(a => Fit(Format(MeanPrecision(report, a)), cell))));
        ContentRow($"  {"MEAN k shown",-12} {"",7}  "
                 + string.Join(" ", report.Arms.Select(a => Fit(Format(MeanPresented(report, a), "F1"), cell))));
        Console.ResetColor();

        Divider();
        SectionRow("PER-ARM FLOORS AT THE DECLARED BUDGET");
        Divider();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        ContentRow("  R = recall (served/total) vs its floor, a random draw of min(k, shown) from the");
        ContentRow("  eligible pool — rises with k.  P = precision (relevant/k) vs its floor, R/N over");
        ContentRow("  that pool — the same at every k.  P/shown = relevant over the items actually shown.");
        ContentRow($"    {"persona",-10} {"arm",-13} shown→k  R served  vs floor  P rel/k  vs floor  P/shown");
        foreach (string persona in report.Personas)
        {
            foreach (string arm in report.Arms)
            {
                var s = report.ScoreOf(persona, arm);
                if (s is not { IsScorable: true }) continue;
                ContentRow($"    {persona,-10} {Fit(ShortArm(arm), 13)} {s.Value.PresentedBeforeCut,3}→{s.Value.PresentedCount,-2} "
                         + $"{Format(s.Value.Latent)} {s.Value.LatentServed}/{s.Value.LatentTotal,-3} {Format(s.Value.LatentFloor)}   "
                         + $"{Format(s.Value.PrecisionAtK)} {s.Value.RelevantCount}/{s.Value.DeclaredK,-2} {Format(s.Value.PrecisionFloor)}   "
                         + $"{Format(s.Value.PrecisionOfPresented)}");
            }
        }
        Console.ResetColor();

        Divider();
        SectionRow("ARM LEGEND  (the table abbreviates; these are the arms in full)");
        Divider();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (string arm in report.Arms) ContentRow($"  {Fit(ShortArm(arm), 14)} = {arm}");
        Console.ResetColor();

        BottomBorder();
        Console.WriteLine();

        static string Suffix(CoverageScore s) =>
            s.UnderFilledBudget ? $"↓{s.PresentedBeforeCut}" : s.OverFilledBudget ? "✂" : "";
    }

    /// <summary>
    /// Prints the re-read of a comparison at the LIVE arm's own k: each persona's live cell at
    /// the count it chose, and every deterministic arm cut to that same count.
    /// </summary>
    /// <param name="rows">One row per persona, from <see cref="OwnKReread"/>.</param>
    /// <param name="liveArm">The live arm's label.</param>
    /// <param name="deterministicArms">The re-cut arms, in column order.</param>
    /// <param name="provenance">Where the live cells came from — this run, or a persisted snapshot.</param>
    public static void PrintOwnKReread(
        IReadOnlyList<OwnKRereadRow> rows,
        string liveArm,
        IReadOnlyList<string> deterministicArms,
        string provenance)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(deterministicArms);

        Console.WriteLine();
        TopBorder();
        TitleRow("RE-READ AT THE LIVE ARM'S OWN k  ·  every control CUT to k_live, persona by persona");
        Divider();
        Console.ForegroundColor = ConsoleColor.Yellow;
        ContentRow("  " + provenance);
        ContentRow("  The live cells cannot be raised. They CAN be compared fairly: a control cut to");
        ContentRow("  the live arm's own count is the same quantity as the live cell. This is the");
        ContentRow("  only panel on which a live arm that was never given a budget may be paired.");
        Console.ResetColor();

        if (rows.Count == 0)
        {
            Divider();
            ContentRow("  (no live cells to re-read)");
            BottomBorder();
            Console.WriteLine();
            return;
        }

        int columns = deterministicArms.Count + 1;
        int cell = Math.Max(8, (InnerWidth - 22) / columns - 1);

        Divider();
        SectionRow("RECALL at k_live  ·  ▲/▼ vs the random floor at k_live");
        Divider();
        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  {"persona",-12} {"k",3}  " + Fit(ShortArm(liveArm), cell) + " "
                 + string.Join(" ", deterministicArms.Select(a => Fit(ShortArm(a) + "@k", cell))));
        Console.ResetColor();

        foreach (var row in rows)
        {
            string live = row.KLive == 0
                ? Fit("SILENT", cell)
                : Fit($"{row.Live.Latent:F2}{Marker(row.Live.AboveOwnFloor)}{row.Live.LatentServed}/{row.Live.LatentTotal}", cell);

            var controls = deterministicArms.Select(arm =>
            {
                if (row.KLive == 0) return Fit("n/c", cell);
                if (!row.ControlsAtKLive.TryGetValue(arm, out var c) || c is null) return Fit("short", cell);
                return Fit($"{c.Value.Latent:F2}{Marker(c.Value.AboveOwnFloor)}{c.Value.LatentServed}/{c.Value.LatentTotal}", cell);
            });

            Console.ForegroundColor = row.KLive == 0 ? ConsoleColor.DarkGray : ConsoleColor.White;
            ContentRow($"  {row.PersonaId,-12} {row.KLive,2}{(row.KUniform ? " " : "≈")}  {live} " + string.Join(" ", controls));
            Console.ResetColor();
        }

        Divider();
        SectionRow("PRECISION at k_live  (relevant / k_live)  ·  ▲/▼ vs R/N  ·  n/r = not recorded by that run");
        Divider();
        foreach (var row in rows)
        {
            string live = row.KLive == 0
                ? Fit("SILENT", cell)
                : double.IsNaN(row.Live.PrecisionAtK)
                    ? Fit("n/r", cell)
                    : Fit($"{row.Live.PrecisionAtK:F2}{Marker(row.Live.AbovePrecisionFloor)}{row.Live.RelevantCount}/{row.KLive}", cell);

            var controls = deterministicArms.Select(arm =>
            {
                if (row.KLive == 0) return Fit("n/c", cell);
                if (!row.ControlsAtKLive.TryGetValue(arm, out var c) || c is null) return Fit("short", cell);
                return Fit($"{c.Value.PrecisionAtK:F2}{Marker(c.Value.AbovePrecisionFloor)}{c.Value.RelevantCount}/{row.KLive}", cell);
            });

            Console.ForegroundColor = row.KLive == 0 ? ConsoleColor.DarkGray : ConsoleColor.White;
            ContentRow($"  {row.PersonaId,-12} {row.KLive,2}{(row.KUniform ? " " : "≈")}  {live} " + string.Join(" ", controls));
            Console.ResetColor();
        }

        Divider();
        var comparable = rows.Where(r => r.KLive > 0).ToList();
        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  MEANS over the {comparable.Count} re-readable persona(s) (k_live > 0), mean k_live "
                 + $"{Format(MeanOrNaN(comparable.Select(r => (double)r.KLive)), "F1")}:");
        ContentRow($"    {"arm",-18} {"recall",8} {"precision",10}");
        ContentRow($"    {Fit(ShortArm(liveArm), 18)} {Format(MeanOrNaN(comparable.Select(r => r.Live.Latent))),8} "
                 + $"{Format(MeanOrNaN(comparable.Select(r => r.Live.PrecisionAtK))),10}"
                 + (comparable.All(r => double.IsNaN(r.Live.PrecisionAtK)) ? "   (n/r — not recorded by that run)" : ""));
        foreach (string arm in deterministicArms)
        {
            ContentRow($"    {Fit(ShortArm(arm) + "@k", 18)} "
                     + $"{Format(MeanOrNaN(comparable.Select(r => r.ControlsAtKLive.TryGetValue(arm, out var c) && c is { } s ? s.Latent : double.NaN))),8} "
                     + $"{Format(MeanOrNaN(comparable.Select(r => r.ControlsAtKLive.TryGetValue(arm, out var c) && c is { } s ? s.PrecisionAtK : double.NaN))),10}");
        }
        Console.ResetColor();

        // Identical notes are folded into one line with the personas listed — the snapshot's
        // "rounded rep-mean" caveat applies to every row of a persisted re-read, and twelve copies
        // of one sentence bury the row that says something different.
        var noteGroups = rows
            .Where(r => r.Note.Length > 0)
            .GroupBy(r => r.Note, StringComparer.Ordinal)
            .ToList();
        if (noteGroups.Count > 0)
        {
            Divider();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            foreach (var group in noteGroups)
                ContentRow($"  {string.Join(", ", group.Select(r => r.PersonaId))}: {group.Key}");
            ContentRow("  ≈ beside k = a rounded rep-mean, not a count every rep presented.");
            Console.ResetColor();
        }

        BottomBorder();
        Console.WriteLine();

        static string Marker(bool? above) => above switch { true => "▲", false => "▼", _ => "?" };

        static double MeanOrNaN(IEnumerable<double> values)
        {
            var kept = values.Where(v => !double.IsNaN(v)).ToList();
            return kept.Count == 0 ? double.NaN : kept.Average();
        }
    }

    private static double MeanPrecision(PairedCoverageReport report, string arm)
    {
        var values = report.Personas
            .Select(p => report.ScoreOf(p, arm))
            .Where(s => s is { IsScorable: true } && !double.IsNaN(s.Value.PrecisionAtK))
            .Select(s => s!.Value.PrecisionAtK)
            .ToList();
        return values.Count == 0 ? double.NaN : values.Average();
    }

    private static double MeanPresented(PairedCoverageReport report, string arm)
    {
        var values = report.Personas
            .Select(p => report.ScoreOf(p, arm))
            .Where(s => s is { IsScorable: true })
            .Select(s => (double)Math.Max(0, s!.Value.PresentedBeforeCut))
            .ToList();
        return values.Count == 0 ? double.NaN : values.Average();
    }

    /// <summary>
    /// A short column label for an arm. Takes the part after an em dash when there is one, so
    /// "Baseline — tag join" prints as "tag join" and the table stays inside the frame.
    /// </summary>
    /// <param name="arm">The full arm label.</param>
    public static string ShortArm(string arm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arm);
        int dash = arm.LastIndexOf('—');
        string tail = dash >= 0 && dash < arm.Length - 1 ? arm[(dash + 1)..].Trim() : arm;
        int paren = tail.IndexOf('(');
        return (paren > 0 ? tail[..paren] : tail).Trim();
    }

    /// <summary>
    /// Prints the cross-persona forced choice — the arm of Eval 02 that cannot be saturated.
    /// </summary>
    /// <remarks>
    /// Latent coverage asks "did the answer contain a product carrying a planted tag?". This asks
    /// "was this answer FOR this customer?", which is the only question Eval 02 exists to support,
    /// and its chance floor is exactly 1/N by construction.
    /// </remarks>
    /// <param name="report">The paired report.</param>
    /// <param name="floor">The exact chance floor, 1/N.</param>
    /// <param name="scorablePersonas">How many personas the choice was made among.</param>
    public static void PrintForcedChoice(PairedCoverageReport report, double floor, int scorablePersonas)
    {
        ArgumentNullException.ThrowIfNull(report);

        Console.WriteLine();
        TopBorder();
        TitleRow("CROSS-PERSONA FORCED CHOICE  ·  chance = 1/N exactly, and unsaturable");
        Divider();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        ContentRow("  An arm scores on a persona only when THAT persona's gold is STRICTLY highest");
        ContentRow($"  of all {scorablePersonas} personas' gold on the same answer. A tie is a loss.");
        ContentRow($"  Chance is exactly {Format(floor)} — no corpus edit can raise it, unlike a coverage floor.");
        Console.ResetColor();
        Divider();

        // ⚠ 1.4 / N-4 — an EXACT one-sided binomial, never `rate > floor`. At n = 12 against a
        //   1/12 floor the old rule printed ▲ over 2 of 12 (p = 0.264) and 3 of 12 (p = 0.070).
        //   The decision routes through ExactBinomial.AboveChance so the control row can test THIS
        //   code path rather than a paraphrase of it.
        int tested = 0;

        int splitCells = 0;
        var splitDetail = new List<string>();

        foreach (string arm in report.Arms)
        {
            double rate = report.ForcedChoiceRate(arm);
            // ⚠ A COUNT OF PERSONAS, from the cells, by a stated reduction — never
            //   `Math.Floor(rate * n)` and never `Math.Round(rate * n)`. Both integerise a mean of
            //   per-persona means, which is a count of nothing: on the stage-2 probe FLOOR printed
            //   `0.667 (0 of 1)` and on the shipped paid cohort it printed `6 of 12` for a live arm
            //   whose twelve cells say 7. See PairedCoverageReport.ForcedChoiceTally for the rule
            //   (majority of that persona's reps; a split rep is a loss) and for why the unit stays
            //   the persona rather than the persona × rep.
            var (wins, n, split) = report.ForcedChoiceTally(arm);
            splitCells += split;
            if (split > 0)
                splitDetail.Add($"{ShortArm(arm)} {string.Join(" ", report.ForcedChoiceSplitCells(arm).Select(c => $"{c.PersonaId}={c.Value:F2}"))}");
            var (above, p) = ExactBinomial.AboveChance(wins, n, floor);

            // ⚠ An arm with no trials is UNDECIDABLE, and ▼ would say it LOST. Same convention as
            //   CoverageScore.AboveOwnFloor: "?" for a comparison neither side can make. It is also
            //   not counted into the multiplicity family below — a test that never ran cannot
            //   inflate a family-wise error rate.
            bool decidable = !double.IsNaN(p);
            if (decidable) tested++;

            Console.ForegroundColor = above ? ConsoleColor.Green
                                    : decidable ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
            ContentRow($"  {(above ? "▲" : decidable ? "▼" : "?")} {Fit(arm, 26)} {Format(rate)}  "
                     + $"(won {wins} of {n})  chance {Format(floor)}  {ExactBinomial.FormatP(p)}");
            Console.ResetColor();
        }

        Divider();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        ContentRow("  ▲ means an EXACT one-sided binomial upper tail at or below 0.05, not rate > chance.");
        ContentRow("  ? means the comparison is undecidable — no trials — never that the arm lost.");

        // ⚠ THE RATE AND THE COUNT ARE TWO DIFFERENT REDUCTIONS, and where any cell is split they
        //   disagree. Saying so is the point: the panel used to print one as if it were the other.
        ContentRow("  the rate is the MEAN of the per-persona cells; the count is personas won on a");
        ContentRow("  MAJORITY of their own reps (a split rep is a loss). Reps are NOT independent");
        ContentRow("  trials, so n is personas — never persona x rep (CoverageScore.Mean).");
        if (splitCells > 0)
        {
            ContentRow($"  ⚠ {splitCells} cell(s) SPLIT across reps this run, so rate != won/n:");
            foreach (string d in splitDetail) ContentRow("      " + Fit(d, InnerWidth - 8));
        }
        else
        {
            ContentRow("  no cell was split across reps this run, so rate = won/n exactly.");
        }

        // ⚠ COMPUTED from the arms this run actually tested, never a constant. The first revision
        //   printed "with five arms … ≈ 0.23" beneath a six-arm panel, whose rate is 0.265. A
        //   hard-coded family size can only understate the rate as the panel grows, and a smaller
        //   stated error rate makes a lone ▲ look safer than the panel it came from.
        double fwer = ExactBinomial.FamilyWiseErrorRate(tested);
        ContentRow(double.IsNaN(fwer)
            ? $"  No multiplicity correction is applied — {tested} arm(s) tested, so there is no family."
            : $"  No multiplicity correction is applied across the arms in this panel — with {tested}");
        if (!double.IsNaN(fwer))
            ContentRow($"  arms at {ExactBinomial.Alpha:0.00} the family-wise error rate is ≈ {fwer:0.000}, so read one ▲ accordingly.");
        Console.ResetColor();

        BottomBorder();
        Console.WriteLine();
    }

    /// <summary>Prints the sign test, and says plainly when the n cannot support it.</summary>
    /// <remarks>
    /// ⚠ <b>The row colour comes from the challenger's <see cref="CoverageArmKind"/>, never from
    /// who led</b> (§8, B-12). It used to be <c>ChallengerLeads ? Green : Yellow</c>, which painted
    /// GREEN over the one sentence this panel exists to make legible: the primary CONTROL is an
    /// entrant, and a control that leads means the architecture is not load-bearing. Green for that
    /// is the flattering direction. The direction is still on the row, in words — <c>W/L/T</c> and
    /// the leader notes below the panel — where a reader has to read it rather than absorb it from
    /// a colour.
    /// </remarks>
    /// <param name="outcomes">One outcome per comparison.</param>
    /// <param name="heading">
    /// Optional panel title, so a panel of equal-k comparisons can say which k it was paired at.
    /// </param>
    /// <param name="kindByArm">
    /// The kind of each arm, by label. Supplied by the caller from the arm registry; an arm missing
    /// from it prints in the neutral colour rather than in a flattering one.
    /// </param>
    public static void PrintSignTest(
        IReadOnlyList<SignTestOutcome> outcomes,
        string? heading = null,
        IReadOnlyDictionary<string, CoverageArmKind>? kindByArm = null)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        Console.WriteLine();
        TopBorder();
        TitleRow(heading ?? "Paired sign test — reported, never gated");
        Divider();

        if (outcomes.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            ContentRow("  (no comparisons registered)");
            Console.ResetColor();
        }

        foreach (var o in outcomes)
        {
            // An UNDECIDABLE comparison — every pair refused — is printed as such, in the colour
            // of a non-result. Not green, not "the challenger did not lead": nothing was compared.
            if (o.Undecidable)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                ContentRow($"  {Fit(o.ArmB, 24)} vs {Fit(o.ArmA, 20)}  [{o.Metric}]  UNDECIDABLE — 0 comparable pairs");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ArmColour(o.ArmB, kindByArm);
                // F4, not F3: at twelve paired personas a clean sweep gives p = 0.00049, and F3
                // renders that as "0.000" — a p-value printed as zero is the one number a reader
                // will quote and the one this printer must never round away.
                // Widths trimmed from 26/22 so the p-value survives ContentRow's clip. At F3 the row
                // fitted and printed a sweep of twelve as "p = 0.000"; at F4 it fitted the string but
                // the box then cut it to "p = 0.00". A truncated p-value is worse than a rounded one —
                // it reads as a different, smaller number — so the ARM NAMES give up the characters.
                ContentRow($"  {Fit(o.ArmB, 24)} vs {Fit(o.ArmA, 20)}  "
                         + $"W/L/T {o.Wins}/{o.Losses}/{o.Ties}  p = {o.PValue:F4}"
                         + (string.Equals(o.Metric, "recall", StringComparison.Ordinal) && o.DeclaredK == 0 && o.Excluded.Count == 0
                                ? ""
                                : $"  [{o.Metric}{(o.DeclaredK > 0 ? $" @k={o.DeclaredK}" : o.DeclaredK < 0 ? " @k_live, per persona" : "")}]"));
                Console.ResetColor();

                // The direction, in WORDS, because the colour above no longer carries it. A reader
                // who takes the leader from a colour is reading the printer's opinion; a reader who
                // takes it from this line is reading the run.
                Console.ForegroundColor = ConsoleColor.DarkGray;
                ContentRow($"      direction: {(o.ChallengerLeads ? $"{ShortArm(o.ArmB)} LEADS" : o.Wins == o.Losses ? "no direction" : $"{ShortArm(o.ArmA)} leads")}"
                         + DescribeKind(o.ArmB, kindByArm));
                Console.ResetColor();
            }

            if (o.Excluded.Count > 0)
            {
                // Listed persona by persona. An n that quietly shrank would read as ties; a pair
                // at unequal k is neither a win, nor a loss, nor a tie.
                Console.ForegroundColor = ConsoleColor.Magenta;
                foreach (var line in Wrap($"      NOT COMPARABLE ({o.Excluded.Count}): " + string.Join("; ", o.Excluded), InnerWidth))
                    ContentRow(line);
                Console.ResetColor();
            }

            if (o.Undecidable) continue;

            // ⚠ A percentile bootstrap over three deltas resamples three numbers ten thousand
            // times and reports the spread of THOSE three, which is not a confidence interval for
            // anything. It is suppressed rather than printed with a caveat, because an interval
            // printed beside a mean is read as a result whatever the small print says.
            Console.ForegroundColor = ConsoleColor.DarkGray;
            if (o.EffectiveN >= MinimumBootstrapPairs)
            {
                ContentRow($"      mean Δ = {Format(o.MeanDelta)}   bootstrap 95% CI "
                         + $"[{Format(o.CiLow)}, {Format(o.CiHigh)}]  ({PairedCoverageReport.BootstrapResamples.ToString("N0", CultureInfo.InvariantCulture)} resamples, seed "
                         + $"{PairedCoverageReport.BootstrapSeed})");
            }
            else
            {
                ContentRow($"      mean Δ = {Format(o.MeanDelta)}   bootstrap 95% CI SUPPRESSED");
                ContentRow($"      {o.EffectiveN} non-tied pair(s), under the {MinimumBootstrapPairs} this printer draws an");
                ContentRow($"      interval for. Resampling {o.EffectiveN} number(s) cannot manufacture information");
                ContentRow("      the run did not collect.");
            }
            Console.ResetColor();

            if (o.UnderpoweredByConstruction)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                // ⚠ Scoped to THIS comparison, deliberately. It used to end "…cannot be evaluated
                // on this corpus", which was a claim about the corpus rather than about the pair
                // in hand — and once the corpus reached twelve scorable personas that sentence was
                // simply false while still being printed under a comparison whose ties had dropped
                // its own n to three. A pair is underpowered because ITS pairs tied, not because
                // the analysis set is small.
                foreach (var line in Wrap(
                    $"      ⚠️  n = {o.EffectiveN} after ties are discarded. The smallest two-sided p THIS "
                  + $"comparison can produce is {o.MinimumAttainableP:F4}. No split of its non-tied cases reaches "
                  + "p < 0.05, so this row is a DIRECTION and not a result. It says nothing about the other "
                  + "comparisons on this panel, which have their own n.", InnerWidth))
                    ContentRow(line);
                Console.ResetColor();
            }
        }

        BottomBorder();
        Console.WriteLine();
    }

    /// <summary>The colour a KIND is printed in. One table, used by every panel that names an arm.</summary>
    private static ConsoleColor ArmColour(CoverageArmKind kind) => kind switch
    {
        CoverageArmKind.Live => ConsoleColor.Cyan,
        CoverageArmKind.Control => ConsoleColor.Yellow,
        CoverageArmKind.Baseline => ConsoleColor.DarkYellow,
        CoverageArmKind.Oracle => ConsoleColor.Magenta,
        CoverageArmKind.Loop => ConsoleColor.Blue,
        _ => ConsoleColor.Gray,
    };

    /// <summary>The colour an arm is printed in — its KIND, never its result.</summary>
    private static ConsoleColor ArmColour(string arm, IReadOnlyDictionary<string, CoverageArmKind>? kindByArm) =>
        kindByArm is not null && kindByArm.TryGetValue(arm, out var kind) ? ArmColour(kind) : ConsoleColor.Gray;

    /// <summary>
    /// Wraps a continuation paragraph and keeps its indent — <see cref="Wrap"/> splits on spaces,
    /// so a leading run of them is eaten and the continuation prints hard against the box edge.
    /// </summary>
    /// <param name="text">The paragraph, unindented.</param>
    /// <param name="indent">How far to indent every line of it.</param>
    private static IEnumerable<string> Indented(string text, int indent = 6)
    {
        string pad = new(' ', indent);
        foreach (var line in Wrap(text, InnerWidth - indent)) yield return pad + line;
    }

    /// <summary>
    /// The arm's kind as a word.
    /// </summary>
    /// <remarks>
    /// Three cases, and the middle one is the point. No map at all means this panel has no arm
    /// registry (Eval 09 keeps its own arm model) and the clause is simply omitted. A map that
    /// does not contain the arm means a registry that FORGOT one, and that says so out loud.
    /// </remarks>
    /// <param name="arm">The arm label.</param>
    /// <param name="kindByArm">The registry's kind map, or null when the panel has no registry.</param>
    private static string DescribeKind(string arm, IReadOnlyDictionary<string, CoverageArmKind>? kindByArm) =>
        kindByArm is null ? ""
        : kindByArm.TryGetValue(arm, out var kind) ? $"  ·  {arm} is a {kind.ToString().ToUpperInvariant()} arm"
        : $"  ·  {arm}: KIND NOT REGISTERED";

    /// <summary>
    /// Prints the arm legend: one row per registered arm — label, kind, whether it enters the sign
    /// test, and the note saying what the arm is for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §8/B-12. <c>CoverageArm.Note</c> was written on every arm and read by nothing, which meant
    /// the one sentence that says <i>"do NOT read this arm's coverage number as the design's
    /// headline"</i> existed only in source. A caveat that does not print is not a caveat.
    /// </para>
    /// <para>
    /// The rows are coloured by <see cref="CoverageArmKind"/> — the same rule the sign-test panel
    /// now follows — so a reader learns the colour code here and carries it down the report.
    /// </para>
    /// </remarks>
    /// <param name="arms">Every registered arm, runnable and absent, in report order.</param>
    public static void PrintArmLegend(IReadOnlyList<CoverageArm> arms)
    {
        ArgumentNullException.ThrowIfNull(arms);

        Console.WriteLine();
        TopBorder();
        TitleRow("ARM REGISTRY — label · kind · enters the sign test? · what it is for");
        Divider();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        ContentRow("  Colour = KIND here and on every sign-test panel. It never means 'did well'.");
        ContentRow("  An ORACLE reads the gold; a BASELINE understands nothing. Neither leading");
        ContentRow("  over the live arm is good news, and neither is painted as though it were.");
        Console.ResetColor();
        Divider();

        foreach (CoverageArm arm in arms)
        {
            Console.ForegroundColor = ArmColour(arm.Kind);

            ContentRow($"  {Fit(arm.Label, 32)}  {arm.Kind.ToString().ToUpperInvariant(),-9}  "
                     + $"sign test: {(arm.EntersSignTest ? "YES" : "no ")}"
                     + (arm.IsPrimaryControl ? "  ★ PRIMARY CONTROL" : "")
                     + (arm.IsRunnable ? "" : "  ⛔ DECLARED ABSENT"));
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            foreach (var line in Indented(arm.Note.Length > 0
                         ? arm.Note
                         : "⚠️ NO NOTE — this arm was registered without saying what it is for."))
            {
                ContentRow(line);
            }

            if (!arm.IsRunnable && arm.AbsenceReason.Length > 0)
            {
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.Red;
                foreach (var line in Indented("ABSENCE: " + arm.AbsenceReason)) ContentRow(line);
            }

            Console.ResetColor();
        }

        BottomBorder();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints the verdict on a pre-registered decision rule — MET, NOT MET, or NOT EVALUATED with
    /// the reason attached.
    /// </summary>
    /// <remarks>
    /// §8/B-2. The rule's TEXT used to print in Eval 02's pre-registration block with nothing behind
    /// it: no threshold constant, no comparison, no verdict, and a sign-test panel underneath that
    /// had once shown a green sweep for a different pair. This panel is the evaluator's output, and
    /// it renders in all three states — including the one that says the comparison was never made.
    /// </remarks>
    /// <param name="outcome">The evaluated rule.</param>
    public static void PrintPreRegisteredRule(PreRegisteredRuleOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        Console.WriteLine();
        TopBorder();
        TitleRow("PRE-REGISTERED DECISION RULE — the ≥ 10 of 12 rule, EVALUATED");
        Divider();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (var line in Indented("Rule: " + PreRegisteredRule.Statement, indent: 2)) ContentRow(line);
        Console.ResetColor();
        Divider();

        Console.ForegroundColor = outcome.Verdict switch
        {
            PreRegisteredRuleVerdict.Met => ConsoleColor.Green,
            PreRegisteredRuleVerdict.NotMet => ConsoleColor.Yellow,
            _ => ConsoleColor.Red,
        };
        ContentRow($"  {(outcome.Verdict == PreRegisteredRuleVerdict.Met ? "✅" : outcome.Verdict == PreRegisteredRuleVerdict.NotMet ? "⚠️ " : "❌")} "
                 + $"{outcome.Label}  ·  {ShortArm(outcome.Challenger)} vs {ShortArm(outcome.Reference)}");
        ContentRow($"      required {outcome.WinsRequired} of {outcome.PreRegisteredPairs}  ·  "
                 + (outcome.Verdict == PreRegisteredRuleVerdict.NotEvaluated
                        ? "attained: nothing — the comparison was not made"
                        : $"attained W/L/T {outcome.Wins}/{outcome.Losses}/{outcome.Ties} over {outcome.ComparableN} comparable pair(s)"));
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (var line in Indented(outcome.Reason)) ContentRow(line);
        foreach (var line in Indented(PreRegisteredRule.Supersession)) ContentRow(line);
        Console.ResetColor();

        BottomBorder();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints one channel's REP-TO-REP spread — design §8.1 row 19 / <b>B-18</b> — and, beside it,
    /// whether each reported paired delta is bigger than the repeated arm's own noise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this panel is for.</b> The comparison panels above it report differences of a few
    /// hundredths. This one says how far the SAME arm's answers to the SAME question moved between
    /// repetitions. A delta smaller than that is inside the instrument's own noise, and its
    /// direction is not a finding — a second, independent reason not to read a row, alongside its
    /// p-value.
    /// </para>
    /// <para>
    /// ⚠ <b>Three reps is a very small n for a spread, and the panel says so rather than letting
    /// three decimal places imply otherwise.</b> The sample SD's own relative standard error at
    /// n = 3 is roughly 52 %, so the RANGE is printed first and the SD second.
    /// </para>
    /// <para>
    /// ⚠ <b>A deterministic arm reads NOT REPEATED, never 0.000.</b> One run is that arm's whole
    /// distribution; printing a zero spread for it would claim "no variation was observed" where
    /// the truth is "variation was never observable" — the same class of claim as printing a cost
    /// of zero for a turn that reported no usage.
    /// </para>
    /// </remarks>
    /// <param name="report">The recorded spreads for one channel.</param>
    /// <param name="repeatedArm">The arm whose noise bounds the deltas — the live arm.</param>
    /// <param name="outcomes">The paired outcomes printed above this panel.</param>
    /// <param name="reps">How many repetitions the run asked for.</param>
    public static void PrintRepSpread(
        Graders.RepSpreadReport report,
        string repeatedArm,
        IReadOnlyList<Graders.SignTestOutcome> outcomes,
        int reps)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(repeatedArm);
        ArgumentNullException.ThrowIfNull(outcomes);

        Console.WriteLine();
        TopBorder();
        TitleRow($"B-18 — rep-to-rep SPREAD of {report.Channel} · reported, never gated");
        Divider();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (var line in Indented(
            $"The run asked for {reps} rep(s) on the repeated arm. Every cell above is the MEAN of "
          + "those reps; this panel is what the mean covered up. Read the RANGE first — a sample sd "
          + $"over {reps} value(s) has about {RelativeSeOfSd(reps)} of relative standard error of its own, "
          + "so it is not a tight quantity and three decimal places do not make it one.", 2))
            ContentRow(line);
        Console.ResetColor();
        Divider();

        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  {"arm",-34} {"cells",6} {"moved",6} {"widest",8} {"median",8} {"mean sd",8}");
        Divider();
        foreach (string arm in report.Arms)
        {
            var summary = report.SummaryFor(arm);
            Console.ForegroundColor = summary.IsReadable ? ConsoleColor.White : ConsoleColor.DarkGray;
            ContentRow(summary.IsReadable
                ? $"  {Fit(arm, 34)} {summary.ReadableCells,6} {summary.CellsThatMoved,6} "
                + $"{summary.WidestRange,8:F3} {summary.MedianRange,8:F3} {summary.MeanSd,8:F3}"
                : $"  {Fit(arm, 34)} {summary.Cells,6} {"—",6} {"—",8} {"—",8} {"—",8}   NOT REPEATED");
        }
        Console.ResetColor();

        // ── The comparison this panel exists to make. ────────────────────────────────────
        Divider();
        var bound = report.SummaryFor(repeatedArm);
        if (!bound.IsReadable)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            foreach (var line in Indented(
                $"⚠ {repeatedArm} has NO readable spread, so nothing on this panel bounds anything. An arm "
              + "that ran once cannot say how noisy it is, and answering \"the delta is outside the noise\" "
              + "from an unmeasured noise level would certify every comparison for free.", 2))
                ContentRow(line);
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            foreach (var line in Indented(
                $"Each reported delta against {repeatedArm}'s WIDEST rep-to-rep movement, "
              + bound.WidestRange.ToString("F3", CultureInfo.InvariantCulture)
              + $" — not its median ({bound.MedianRange.ToString("F3", CultureInfo.InvariantCulture)}), which is "
              + $"the flattering statistic here: only {bound.CellsThatMoved} of {bound.ReadableCells} cell(s) "
              + "moved at all, so a median bound would certify every non-zero delta for free. ⚠ A BOUND ON "
              + "MAGNITUDE, never a significance test — the p-value above still decides that. What it adds is "
              + "the case where a delta is unremarkable AND smaller than the arm's own spread, which is two "
              + "reasons not to read its direction rather than one.", 2))
                ContentRow(line);
            Console.ResetColor();

            foreach (var outcome in outcomes)
            {
                var comparison = report.CompareToOwnNoise(repeatedArm, outcome.MeanDelta);
                Console.ForegroundColor = comparison.Verdict switch
                {
                    Graders.NoiseVerdict.OutsideNoise => ConsoleColor.White,
                    Graders.NoiseVerdict.InsideNoise => ConsoleColor.Yellow,
                    _ => ConsoleColor.DarkGray,
                };
                string magnitude = comparison.Verdict == Graders.NoiseVerdict.NoDelta
                    ? "     —"
                    : Math.Abs(outcome.MeanDelta).ToString("F3", CultureInfo.InvariantCulture).PadLeft(6);
                ContentRow($"  {Fit($"{ShortArm(outcome.ArmB)} vs {ShortArm(outcome.ArmA)} ({outcome.Metric})", 40)} "
                         + $"|Δ| {magnitude}  {comparison.Describe()}");
                Console.ResetColor();
            }
        }

        BottomBorder();
        Console.WriteLine();
    }

    /// <summary>
    /// The sample standard deviation's own relative standard error at this n, as a percentage —
    /// approximately 1 / sqrt(2(n − 1)).
    /// </summary>
    /// <remarks>
    /// DERIVED from the rep count the run actually used. It was a hard-coded "three numbers … about
    /// 52%" for one build of this panel, and the first dry run printed it above a table built from
    /// TWO reps. A caveat carrying a number that does not describe the run is the shape this
    /// repository has corrected in three separate documents.
    /// </remarks>
    /// <param name="n">The number of values the sd was taken over.</param>
    private static string RelativeSeOfSd(int n) =>
        n < 2 ? "an undefined amount"
              : (100.0 / Math.Sqrt(2.0 * (n - 1))).ToString("F0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Prints per-arm cost. Goes on the same panel as any win, never on a different one.</summary>
    /// <param name="report">The paired report.</param>
    public static void PrintCostComparison(PairedCoverageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Console.WriteLine();
        TopBorder();
        TitleRow("Cost per arm — reported, never gated");
        Divider();
        Console.ForegroundColor = ConsoleColor.White;
        ContentRow($"  {"arm",-30} {"runs",5} {"seconds",9} {"tokens",9} {"est. cost",12}");
        Divider();

        var footnotes = new List<string>();
        foreach (string arm in report.Arms)
        {
            var (row, footnote) = CostRow(arm, report.CostOf(arm));
            ContentRow(row);
            if (footnote is not null) footnotes.Add(footnote);
        }
        Console.ResetColor();

        Divider();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (var note in footnotes)
            foreach (var line in Indented(note, 2))
                ContentRow(line);
        if (footnotes.Count > 0) ContentRow("");
        ContentRow("  Deterministic arms cost nothing and take milliseconds. That is not an advantage —");
        ContentRow("  it is the reason a baseline that scores well is a problem for the headline, not a win.");
        Console.ResetColor();
        BottomBorder();
        Console.WriteLine();
    }

    /// <summary>
    /// Renders ONE cost row and, when the row is not a plain measurement, the sentence that says why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Plan item 8.3, and it was WRONG AS SPECIFIED.</b> It asked for <c>"—"</c> when
    /// <c>ModelId is null</c>; there was no model id on the record, so the printer could not tell a
    /// deterministic arm that genuinely spent nothing from a model arm whose usage never arrived.
    /// Rendering <c>—</c> on a zero total would relabel a true zero as unknown; rendering
    /// <c>$0.0000</c> labels an unknown as zero. <b>Neither is a rendering choice — the information
    /// was missing.</b> It is now recorded at
    /// <see cref="Graders.PairedCoverageReport.RecordCost"/> and read here as a STATE.
    /// </para>
    /// <para>
    /// Pure on purpose, like <see cref="CoverageGateLines"/>: Eval 03's
    /// <c>CostRowsSayWhichZeroTheyMean</c> asserts on the returned strings, so the branch that must
    /// never render an absence as a zero is checked without a console scrollback.
    /// </para>
    /// </remarks>
    /// <param name="arm">The arm label.</param>
    /// <param name="cost">Its accumulated cost, with the run states the recorder saw.</param>
    /// <returns>The table row, and a footnote when one is owed.</returns>
    public static (string Row, string? Footnote) CostRow(string arm, Graders.PairedCoverageReport.ArmCost cost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arm);
        ArgumentNullException.ThrowIfNull(cost);

        long tokens = (long)cost.PromptTokens + cost.CompletionTokens;
        string seconds = cost.DurationMs / 1000.0 <= 0 ? "—" : (cost.DurationMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture);
        string money = cost.EstimatedCost.ToString("C4", CultureInfo.InvariantCulture);

        (string tokenCell, string costCell, string? footnote) = cost.State switch
        {
            Graders.PairedCoverageReport.ArmCostState.NotRun =>
                ("—", "—",
                 $"{arm}: NOT RUN. No turn was recorded, so nothing is claimed about its cost — "
               + "not zero, and not unknown either."),

            // The ONLY state in which a zero may be printed as a number, and it is printed as a
            // number precisely because it is a measurement: every turn was recorded with no metrics
            // object, which is the caller saying this arm reaches no model.
            // The registry says this arm reaches no model and its own turns reported tokens.
            // BOTH numbers are withheld: a contradiction is not a smaller measurement, and
            // printing either side of it would pick a winner between a declaration and its
            // evidence.
            Graders.PairedCoverageReport.ArmCostState.Contradicted =>
                ("⚠", "⚠",
                 $"{arm}: CONTRADICTED. The arm registry declares this arm reaches no model, and "
               + $"{cost.ModelFreeRunsThatReportedUsage} of {cost.Runs} turn(s) reported token usage "
               + "anyway. No cost is reported for it: the declaration and the evidence disagree, and "
               + "CoverageArm.ReachesAModel is the input the rest of this panel trusts."),

            Graders.PairedCoverageReport.ArmCostState.NoModel =>
                ("0", money,
                 $"{arm}: NO MODEL — all {cost.Runs} turn(s) ran without reaching one, as the arm "
               + "registry declares. This zero is measured, not missing."),

            Graders.PairedCoverageReport.ArmCostState.LowerBound =>
                ($"≥{tokens}", $"≥{money}",
                 $"{arm}: LOWER BOUND. {Describe(cost)} A turn that reported no usage is not a turn "
               + "that cost nothing, so these totals are a floor and the true figure is higher."),

            // ⚠ "Complete" has to mean the PROVIDER said so. MAFEvaluationHarness:145 estimates
            // token counts from text when a response carries no usage block, so a count can be
            // present, non-zero and entirely invented; SpendLedger and Evals 07-09 all read
            // TokensAreEstimated and this panel did not.
            _ => (cost.RunsWithEstimatedTokens > 0
                    ? "≈" + tokens.ToString(CultureInfo.InvariantCulture)
                    : tokens.ToString(CultureInfo.InvariantCulture),
                  // ⚠ THE MONEY COLUMN OBEYS THE SAME RULE AS THE TOKEN COLUMN, and it did not.
                  // A model arm whose turns all reported tokens but NO EstimatedCost is state
                  // Measured, so the money cell printed a bare ¤0.0000 — byte-identical to a NO
                  // MODEL arm's measured zero, which is the pair this whole row exists to keep
                  // apart. It was live on the shipped panel: the live arm reads
                  // "24 of 24 model turn(s) carried no cost estimate" in its FOOTNOTE and ¤0.0000
                  // in its CELL, and a reader scanning the table sees the cell.
                  cost.RunsWithoutCost > 0 ? "≥" + money : money,
                  Caveats(arm, cost)),
        };

        string models = cost.ModelIds.Count switch
        {
            0 when cost.State == Graders.PairedCoverageReport.ArmCostState.NoModel => "",
            0 => "  (model NOT NAMED)",
            1 => $"  ({cost.ModelIds[0]})",
            _ => $"  ({string.Join(", ", cost.ModelIds)})",
        };

        string row = $"  {Fit(arm, 30)} {cost.Runs,5} {seconds,9} {tokenCell,9} {costCell,12}{models}";
        return (row, footnote);

        // The caveats a fully-recorded arm still owes the reader: estimated token counts, and a
        // money column with no cost estimate behind it. Either one alone used to print nothing at
        // all, and the second used to print "token counts are complete" over estimates.
        static string? Caveats(string arm, Graders.PairedCoverageReport.ArmCost c)
        {
            var parts = new List<string>();
            if (c.RunsWithEstimatedTokens > 0)
            {
                parts.Add($"{c.RunsWithEstimatedTokens} of {c.ModelRuns} model turn(s) had their token "
                        + "counts ESTIMATED FROM TEXT by the harness rather than read off a provider "
                        + "usage block, so the token column is a guess and not a measurement");
            }
            else
            {
                parts.Add("token counts are complete");
            }

            if (c.RunsWithoutCost > 0)
            {
                parts.Add($"{c.RunsWithoutCost} of {c.ModelRuns} model turn(s) carried no cost estimate, "
                        + "so the money column is a LOWER BOUND");
            }

            return parts.Count == 1 && c.RunsWithEstimatedTokens == 0 ? null : $"{arm}: {string.Join("; ", parts)}.";
        }

        static string Describe(Graders.PairedCoverageReport.ArmCost c)
        {
            var parts = new List<string>();
            if (c.RunsWithoutUsage > 0) parts.Add($"{c.RunsWithoutUsage} of {c.ModelRuns} model turn(s) reported NO usage block");
            if (c.RunsWithPartialUsage > 0) parts.Add($"{c.RunsWithPartialUsage} reported HALF of one");
            if (c.RunsWithoutCost > 0) parts.Add($"{c.RunsWithoutCost} carried no cost estimate");
            return string.Join("; ", parts) + ".";
        }
    }

    /// <summary>What GATE 2 actually observed. Four states, and three of them fail.</summary>
    /// <remarks>
    /// A bool cannot tell "the control did not lead" from "there was nothing to lead on", and both
    /// of those from "no control was run at all". Collapsing them was how the gate came to print
    /// the passing sentence over a failure: only the emoji changed (§8, B-11).
    /// </remarks>
    public enum CoverageGate2State
    {
        /// <summary>PASS — every equal-k comparison was decidable and the control led on none.</summary>
        ControlDidNotLead,

        /// <summary>FAIL — the control led on at least one decidable equal-k comparison.</summary>
        ControlLed,

        /// <summary>FAIL CLOSED — the control ran, but no persona produced a comparable pair.</summary>
        NoComparablePair,

        /// <summary>FAIL CLOSED — no primary control arm was run at all.</summary>
        NoControlRun,
    }

    /// <summary>
    /// Renders Eval 02's two gates from the OBSERVED state, as text, without printing anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure on purpose: <see cref="PrintCoverageGate"/> prints exactly what this returns, and Eval
    /// 03's <c>CoverageGateRendering</c> control asserts on it directly. A renderer whose only
    /// output is the console can only be checked by a human reading a scrollback, which is how the
    /// failing branch kept the passing sentence through eleven revisions.
    /// </para>
    /// <para>
    /// ⚠ Each branch states what HAPPENED. "did NOT beat" and "DID beat" are different sentences,
    /// not one sentence with two emojis in front of it.
    /// </para>
    /// </remarks>
    /// <param name="aboveFloor">Did EVERY scorable persona clear its own floor?</param>
    /// <param name="belowOwnFloor">The personas that did not, named. Empty when the gate passed.</param>
    /// <param name="scorablePersonas">How many personas the gate read.</param>
    /// <param name="gate2">What GATE 2 observed.</param>
    /// <param name="gate2Detail">Which equal-k comparisons GATE 2 read, and what each said.</param>
    public static IReadOnlyList<string> CoverageGateLines(
        bool aboveFloor,
        IReadOnlyList<string> belowOwnFloor,
        int scorablePersonas,
        CoverageGate2State gate2,
        string? gate2Detail = null)
    {
        ArgumentNullException.ThrowIfNull(belowOwnFloor);

        var lines = new List<string>();

        // ── GATE 1, rendered from what was observed. ─────────────────────────────────────
        if (aboveFloor)
        {
            lines.Add($"  ✅ GATE 1 — every scorable persona ({scorablePersonas} of {scorablePersonas}) is ABOVE that");
            lines.Add("       persona's own floor, derived at the number of items the live arm actually presented.");
            lines.Add("       Per persona, never mean-to-mean: a mean can be carried by one persona while the arm");
            lines.Add("       sits below the floor on the rest.");
        }
        else
        {
            lines.Add($"  ❌ GATE 1 — {belowOwnFloor.Count} of {scorablePersonas} scorable personas are BELOW their OWN floor,");
            lines.Add("       derived at the number of items the live arm actually presented for each:");
            foreach (var line in Wrap(belowOwnFloor.Count > 0
                        ? string.Join(", ", belowOwnFloor)
                        : "(the gate reported a failure but named no persona — the gate and its evidence disagree)",
                    BoxWidth - 7))
            {
                lines.Add("       " + line);
            }
        }

        // ── GATE 2, rendered from what was observed. ─────────────────────────────────────
        switch (gate2)
        {
            case CoverageGate2State.ControlDidNotLead:
                lines.Add("  ✅ GATE 2 — the single-shot control did NOT beat the live agent on ANY equal-k");
                lines.Add("       comparison (recall, at the declared budget where both filled it, and at the live");
                lines.Add("       arm's own k with the control cut to match).");
                break;

            case CoverageGate2State.ControlLed:
                lines.Add("  ❌ GATE 2 — the single-shot control DID beat the live agent on at least one equal-k");
                lines.Add("       comparison. One retrieval pass with no second look matched or bettered the shipped");
                lines.Add("       agent, so whatever advantage the report shows is not architectural.");
                break;

            case CoverageGate2State.NoComparablePair:
                lines.Add("  ❌ GATE 2 — UNDECIDABLE: the primary control had NO equal-k pair with the live agent on");
                lines.Add("       either panel. Every persona was refused (different k, or a silent side). Failing");
                lines.Add("       closed — a comparison that could not be made is not a comparison the agent won.");
                break;

            default:
                lines.Add("  ❌ GATE 2 — UNDECIDABLE: no primary control arm was RUN, so nothing could have taken");
                lines.Add("       the win away. Failing closed — an absent control is not a passed one.");
                break;
        }

        if (gate2Detail is { Length: > 0 })
        {
            foreach (var line in Wrap("read: " + gate2Detail, BoxWidth - 7))
                lines.Add("       " + line);
        }

        return lines;
    }

    /// <summary>Prints Eval 02's gate. Deliberately does NOT gate on "the agent won".</summary>
    /// <remarks>
    /// Every gate line comes from <see cref="CoverageGateLines"/>, so what a reader sees and what
    /// Eval 03's rendering control asserts on are the same strings.
    /// </remarks>
    /// <param name="aboveFloor">Did EVERY scorable persona clear its own floor?</param>
    /// <param name="belowOwnFloor">The personas that did not, named.</param>
    /// <param name="scorablePersonas">How many personas GATE 1 read.</param>
    /// <param name="gate2">What GATE 2 observed.</param>
    /// <param name="notes">Anything else the run needs to say out loud.</param>
    /// <param name="gate2Detail">One line saying which equal-k comparisons GATE 2 actually read, and what each said.</param>
    public static void PrintCoverageGate(
        bool aboveFloor,
        IReadOnlyList<string> belowOwnFloor,
        int scorablePersonas,
        CoverageGate2State gate2,
        IReadOnlyList<string> notes,
        string? gate2Detail = null)
    {
        ArgumentNullException.ThrowIfNull(notes);

        Console.WriteLine();

        var gateLines = CoverageGateLines(aboveFloor, belowOwnFloor, scorablePersonas, gate2, gate2Detail);
        bool inGate2 = false;

        foreach (string line in gateLines)
        {
            if (line.Contains("GATE 2", StringComparison.Ordinal)) inGate2 = true;
            bool ok = inGate2 ? gate2 == CoverageGate2State.ControlDidNotLead : aboveFloor;
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(line);
        }
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine();
        Console.WriteLine("  NOT GATED, on purpose: whether any arm 'won'. Gating on that creates an incentive to");
        Console.WriteLine("  tune the eval until it does — the same shape as letting the artifact under test supply");
        Console.WriteLine("  its own pass criterion.");
        Console.ResetColor();

        foreach (string note in notes)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            foreach (var line in Wrap("  · " + note, BoxWidth))
                Console.WriteLine(line);
            Console.ResetColor();
        }
    }

    // ══ Negative controls ═════════════════════════════════════════════════════════════════

    /// <summary>Prints the wiring self-check.</summary>
    /// <param name="rows">One row per control.</param>
    /// <param name="label">Panel title.</param>
    public static void PrintControlReport(IReadOnlyList<ControlRowSnapshot> rows, string label)
    {
        ArgumentNullException.ThrowIfNull(rows);

        Console.WriteLine();
        TopBorder();
        TitleRow(label);
        Divider();
        SectionRow("A CONTROL THAT PASSES IS A WIRING FAULT, NOT A GOOD AGENT");
        Divider();

        foreach (var row in rows)
        {
            string marker = row.Gating
                ? row.Tripped ? "✅ caught" : "❌ NOT CAUGHT"
                : row.Tripped ? "✅ finding ok" : "⚠️  FINDING";

            Console.ForegroundColor = row.Gating
                ? row.Tripped ? ConsoleColor.Green : ConsoleColor.Red
                : row.Tripped ? ConsoleColor.DarkGreen : ConsoleColor.Yellow;
            ContentRow($"  {marker}  {row.Name}{(row.Gating ? "" : "   (advisory — never gates)")}");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            foreach (var line in Wrap("      expected: " + row.Expectation, InnerWidth)) ContentRow(line);
            Console.ResetColor();

            Console.ForegroundColor = row.Tripped ? ConsoleColor.DarkGreen
                                    : row.Gating ? ConsoleColor.Red : ConsoleColor.Yellow;
            foreach (var line in Wrap("      observed: " + row.Observed, InnerWidth)) ContentRow(line);
            Console.ResetColor();
        }

        Divider();
        bool allTripped = rows.Where(r => r.Gating).All(r => r.Tripped);
        Console.ForegroundColor = allTripped ? ConsoleColor.Green : ConsoleColor.Red;
        ContentRow(allTripped
            ? "  ✅ Every WIRING control was caught. The instrument demonstrably can fail."
            : "  ❌ At least one wiring control slipped through. Treat every clean run above as UNPROVEN.");
        Console.ResetColor();

        var findings = rows.Where(r => !r.Gating && !r.Tripped).ToList();
        if (findings.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            ContentRow($"  ⚠️  {findings.Count} INSTRUMENT FINDING(S) — reported, not gated. Gating on a fact about");
            ContentRow("      the corpus would create an incentive to tune the corpus until it passed.");
            Console.ResetColor();
        }

        BottomBorder();
        Console.WriteLine();
    }

    // ══ Shared ════════════════════════════════════════════════════════════════════════════

    /// <summary>Prints a labelled block of derived chance floors.</summary>
    /// <param name="title">Block title.</param>
    /// <param name="lines">One line per floor, already formatted.</param>
    public static void PrintFloors(string title, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        Console.WriteLine();
        TopBorder();
        TitleRow(title);
        Divider();
        SectionRow("COMPUTED FROM THIS CORPUS AT RUN TIME — not quoted from the design");
        Divider();
        Console.ForegroundColor = ConsoleColor.White;
        foreach (string line in lines)
            foreach (var wrapped in Wrap("  " + line, InnerWidth))
                ContentRow(wrapped);
        Console.ResetColor();
        BottomBorder();
        Console.WriteLine();
    }

    /// <summary>Prints a red banner and the message. Used when a run refuses to proceed.</summary>
    /// <param name="heading">One-line heading.</param>
    /// <param name="detail">The detail, wrapped.</param>
    public static void PrintRefusal(string heading, string detail)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  ⛔ {heading}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        foreach (var line in Wrap("     " + detail, BoxWidth)) Console.WriteLine(line);
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// ⛔ SUPERSEDED by <see cref="CredentialGuard.Blocks"/>. Do not call it from an eval.
    /// </summary>
    /// <remarks>
    /// It said "Skipping Eval NN", which is a statement about the runner, and the six evals that
    /// called it then returned <c>ci ? 3 : 0</c> — so outside CI the process exited 0 and the
    /// sentence a reader actually acts on ("nothing was measured") was never printed at all.
    /// <see cref="CredentialGuard"/> prints the missing MEASUREMENT, names what would have been
    /// measured, says which arms it refuses to substitute, and always exits 3. Kept only so a
    /// future caller finds this note instead of the old banner.
    /// </remarks>
    /// <param name="evalName">Which eval is being skipped.</param>
    [Obsolete("Use CredentialGuard.Blocks — it prints 'NOT MEASURED' and returns exit code 3 unconditionally.")]
    public static void PrintMissingCredentials(string evalName)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine();
        Console.WriteLine($"  ⚠️  Skipping {evalName} — Azure OpenAI credentials required.");
        Console.WriteLine("     Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT.");
        Console.WriteLine();
        Console.ResetColor();
    }

    private static string Format(double value, string format = "F3") =>
        double.IsNaN(value) ? "n/a" : value.ToString(format, CultureInfo.InvariantCulture);

    private static string Fit(string text, int width) =>
        text.Length <= width ? text.PadRight(width) : text[..Math.Max(0, width - 1)] + "…";

    private static string FirstLine(string text)
    {
        int index = text.IndexOf('\n');
        return index < 0 ? text : text[..index].TrimEnd('\r');
    }

    private static IEnumerable<string> Wrap(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var words = text.Split(' ');
        var line = new StringBuilder();
        foreach (var word in words)
        {
            if (line.Length + word.Length + 1 > maxWidth && line.Length > 0)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    // ── Box-drawing helpers, ported verbatim in style from TravelDemo.Evals ───────────────

    private static void TopBorder()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("╔" + new string('═', BoxWidth - 2) + "╗");
        Console.ResetColor();
    }

    private static void BottomBorder()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("╚" + new string('═', BoxWidth - 2) + "╝");
        Console.ResetColor();
    }

    private static void Divider()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("╠" + new string('═', BoxWidth - 2) + "╣");
        Console.ResetColor();
    }

    private static void TitleRow(string title)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("║ ");
        Console.ForegroundColor = ConsoleColor.White;
        var padded = title.PadRight(InnerWidth);
        Console.Write(padded[..Math.Min(padded.Length, InnerWidth)]);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(" ║");
        Console.ResetColor();
    }

    private static void SectionRow(string heading)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("║ ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        var padded = heading.PadRight(InnerWidth);
        Console.Write(padded[..Math.Min(padded.Length, InnerWidth)]);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(" ║");
        Console.ResetColor();
    }

    /// <summary>
    /// One row inside the frame. A row longer than the frame WRAPS; it is never cut.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>This used to truncate at <see cref="InnerWidth"/>, and the truncation hid a false
    /// claim.</b> MEASURED: the yellow caveat above the coverage table asserted "a one-pass
    /// retriever and the tag-join ORACLE score identically, and no arm beats chance on the forced
    /// choice below" — a sentence that stopped being true when the corpus was extended, and whose
    /// contradicting half sat past column 78 on every run. A bootstrap CI row and the
    /// <c>p = 0.0005</c> sign-test row lost their tails the same way. A printer that silently drops
    /// the end of a sentence is a printer that can only ever be checked for the part that fits, so
    /// the tail now wraps onto a continuation line indented two columns past the original.
    /// </remarks>
    /// <param name="content">Row text, already indented by the caller.</param>
    private static void ContentRow(string content)
    {
        if (content.Length <= InnerWidth)
        {
            RawContentRow(content);
            return;
        }

        int indent = 0;
        while (indent < content.Length && content[indent] == ' ') indent++;
        indent = Math.Min(indent, InnerWidth / 2);
        int continuation = Math.Min(indent + 2, InnerWidth / 2);

        bool first = true;
        foreach (var line in Wrap(content.TrimStart(), InnerWidth - continuation))
        {
            RawContentRow(new string(' ', first ? indent : continuation) + line);
            first = false;
        }
    }

    private static void RawContentRow(string content)
    {
        var saved = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("║ ");
        Console.ForegroundColor = saved;
        var padded = content.PadRight(InnerWidth);
        Console.Write(padded[..Math.Min(padded.Length, InnerWidth)]);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(" ║");
        Console.ForegroundColor = saved;
    }

    private static void MetaRow(string meta, bool good)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("║");
        Console.ForegroundColor = good ? ConsoleColor.Green : ConsoleColor.Red;
        var padded = meta.PadRight(BoxWidth - 2);
        Console.Write(padded[..Math.Min(padded.Length, BoxWidth - 2)]);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("║");
        Console.ResetColor();
    }
}
