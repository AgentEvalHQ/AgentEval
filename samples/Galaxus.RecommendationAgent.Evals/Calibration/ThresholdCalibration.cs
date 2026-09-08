// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using Galaxus.RecommendationAgent.Retrieval;

namespace Galaxus.RecommendationAgent.Evals.Calibration;

/// <summary>
/// Derives the four space-dependent score cuts for the RESOLVED embedding space, on a held-out
/// split that is named before anything is fitted, and prints the whole derivation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Run order matters and the tool enforces it.</b> The concept space goes first: it is where the
/// operating point α is READ, because all four pre-calibration constants were chosen while only the
/// concept space existed. The real-vector run then loads the concept run's record and transports
/// that same α; asked to run without it, it refuses rather than quietly choosing its own.
/// </para>
/// <para>
/// <b>Cost.</b> The concept run spends nothing. The real-vector run embeds every distinct interest
/// label live — the product side is served from the committed index for free — plus one
/// space-identity probe, and prints what it spent.
/// </para>
/// </remarks>
public static class ThresholdCalibration
{
    /// <summary>
    /// RULE 2's budget: at most ONE arbitrary catalogue product clears the cut, per query, by
    /// chance. Fixed by the catalogue's size rather than chosen — one over ninety-nine.
    /// </summary>
    public static double ChanceAdmitBudget => 1.0 / Catalogue.Default.All.Count;

    /// <summary>Decimal places every derived value is reported and shipped at.</summary>
    public const int Places = 3;

    /// <summary>Derives, prints and stores the calibration for the resolved space.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>0 on success, 2 when the run was misdriven, 3 when nothing could be measured.</returns>
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine();
        Console.WriteLine("══ THRESHOLD CALIBRATION — the three space-dependent cuts, derived per space ══");
        Console.WriteLine();

        // ── 0. THE SPLIT, NAMED BEFORE ANY NUMBER IS COLLECTED ────────────────
        CalibrationSplit.SelfCheck();
        PrintSplit();
        PrintRules();

        var catalogue = Catalogue.Default;
        var products  = catalogue.All;

        var resolution = EmbeddingSpace.Resolve(products);
        var spaceName  = resolution.Chosen == EmbeddingSpaceChoice.RealVectors ? "real-vectors" : "concept";

        Console.WriteLine($"  Space          {spaceName}  ({resolution.Source.Name}, {resolution.Source.Dimensions} dimensions)");
        Console.WriteLine($"  Why            {resolution.Reason}");
        if (resolution.FellBack)
        {
            Console.WriteLine();
            Console.WriteLine("  ⛔ This run FELL BACK to the concept space. A calibration record written under the name of a");
            Console.WriteLine("     space it did not measure would be worse than no record at all. Nothing is stored.");
            return 3;
        }

        // The transport anchor. The real-vector run may not choose its own.
        CalibrationRecord? conceptRecord = null;
        if (resolution.Chosen == EmbeddingSpaceChoice.RealVectors)
        {
            conceptRecord = CalibrationRecord.Load(CalibrationRecord.ConceptFileName);
            if (conceptRecord is null)
            {
                Console.WriteLine();
                Console.WriteLine("  ⛔ No concept-space record. α is READ from the concept space and transported — a real-vector");
                Console.WriteLine("     run that derived its own α would be choosing an operating point, not moving one.");
                Console.WriteLine("     Run:  dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- cal --concept-vectors");
                return 2;
            }
        }

        // ── 1. POPULATIONS, on the SHIPPED arithmetic and the PRE-CALIBRATION anchor ──
        var personas = CalibrationPopulations.BuildPersonas();
        var fit      = personas.Where(p => CalibrationSplit.IsFit(p.PersonaId)).ToArray();
        var held     = personas.Where(p => CalibrationSplit.IsHeldOut(p.PersonaId)).ToArray();

        var abstaining = personas.Where(p => p.Abstains).Select(p => $"{p.PersonaId} ({p.AbstainReason})").ToArray();

        // The floor is PINNED to the pre-calibration anchor while the populations are collected, so
        // the derivation is a function of the corpus and the anchor alone. Left free, the confidence
        // population would move the moment the derived dense floor shipped and the calibration would
        // stop reproducing itself.
        var retriever = await HybridRetriever.BuildAsync(
            products,
            resolution.Source,
            new HybridRetrieverOptions { DenseScoreFloor = CalibratedThresholds.PreCalibration.DenseScoreFloor },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var skippedFit  = new List<string>();
        var skippedHeld = new List<string>();

        var denseFit  = new ScorePopulation(CalibrationPopulations.DenseFloorName, "fit", "one dense cosine in a per-leg candidate list",
            await CalibrationPopulations.DenseScoresAsync(fit, retriever, skippedFit, cancellationToken).ConfigureAwait(false));
        var denseHeld = new ScorePopulation(CalibrationPopulations.DenseFloorName, "held-out", "one dense cosine in a per-leg candidate list",
            await CalibrationPopulations.DenseScoresAsync(held, retriever, skippedHeld, cancellationToken).ConfigureAwait(false));
        var denseNull = new ScorePopulation(CalibrationPopulations.DenseFloorName, "chance", "one query against one arbitrary catalogue product",
            await CalibrationPopulations.NullDenseScoresAsync(fit, retriever, cancellationToken).ConfigureAwait(false));

        var attrFit  = new ScorePopulation(CalibrationPopulations.AttributionName, "fit", "one interest label against one product document",
            await CalibrationPopulations.AttributionMatchesAsync(fit, cancellationToken).ConfigureAwait(false));
        var attrHeld = new ScorePopulation(CalibrationPopulations.AttributionName, "held-out", "one interest label against one product document",
            await CalibrationPopulations.AttributionMatchesAsync(held, cancellationToken).ConfigureAwait(false));
        var attrArm  = new ScorePopulation(CalibrationPopulations.AttributionName, "offline-arm", "one interest label against one issued query",
            await CalibrationPopulations.AttributionMatchesOfflineArmAsync(fit, cancellationToken).ConfigureAwait(false));

        var confFit  = new ScorePopulation("confidence", "fit", "one presented product's confidence",
            await CalibrationPopulations.PresentedConfidencesAsync(fit, retriever, cancellationToken).ConfigureAwait(false));
        var confHeld = new ScorePopulation("confidence", "held-out", "one presented product's confidence",
            await CalibrationPopulations.PresentedConfidencesAsync(held, retriever, cancellationToken).ConfigureAwait(false));
        var confNull = new ScorePopulation("confidence", "chance", "one interest against one arbitrary catalogue product",
            await CalibrationPopulations.NullConfidencesAsync(fit, cancellationToken).ConfigureAwait(false));

        // The attribution population's own chance distribution IS the label × arbitrary-product
        // population — every row is a signal against a product retrieval never chose. So the fit
        // population and the chance population are the same rows, and saying so is the honest way
        // to report rule 2 here rather than manufacturing a second sample.
        var attrNull = attrFit;

        PrintCohort(personas, abstaining, skippedFit, skippedHeld, attrArm);

        // ── 2. THE FOUR DERIVATIONS ───────────────────────────────────────────
        var anchor = CalibratedThresholds.PreCalibration;

        var cuts = new List<CutDerivation>
        {
            Derive(CalibrationPopulations.DenseFloorName,       anchor.DenseScoreFloor,   denseFit, denseHeld, denseNull, conceptRecord, chanceRuleApplies: true),
            Derive(CalibrationPopulations.AttributionName,      anchor.AttributionFloor,  attrFit,  attrHeld,  attrNull,  conceptRecord, chanceRuleApplies: true),
            Derive(CalibrationPopulations.ConfidencePrimaryName, anchor.ConfidencePrimary, confFit, confHeld, confNull, conceptRecord, chanceRuleApplies: false),
            Derive(CalibrationPopulations.ConfidenceSecondaryName, anchor.ConfidenceSecondary, confFit, confHeld, confNull, conceptRecord, chanceRuleApplies: false),
        };

        PrintCuts(cuts, spaceName);

        var record = new CalibrationRecord(
            spaceName,
            resolution.Reason,
            DateTimeOffset.UtcNow,
            CalibrationSplit.Fit,
            CalibrationSplit.HeldOut,
            abstaining,
            [.. skippedFit, .. skippedHeld],
            cuts);

        record.Save(resolution.Chosen == EmbeddingSpaceChoice.RealVectors
            ? CalibrationRecord.RealVectorsFileName
            : CalibrationRecord.ConceptFileName);

        Console.WriteLine();
        Console.WriteLine($"  Record         {Path.Combine(CalibrationRecord.StorageLocation, spaceName)}…json");
        EmbeddingSpace.PrintLiveSpend("  ");
        Console.WriteLine();

        return 0;
    }

    /// <summary>
    /// One cut point, derived. Rule 1 always; rule 2 only where a chance budget has a meaning.
    /// </summary>
    private static CutDerivation Derive(
        string name,
        double preCalibration,
        ScorePopulation fit,
        ScorePopulation held,
        ScorePopulation chance,
        CalibrationRecord? conceptRecord,
        bool chanceRuleApplies)
    {
        // α: read from the concept space's fit population at the pre-calibration constant, and then
        // never re-chosen. In the concept run that is this run's own population; in the real-vector
        // run it is the stored concept record's.
        var alpha = conceptRecord is null ? fit.AdmitRate(preCalibration) : conceptRecord.TargetFor(name);

        var derived = Round(fit.CutAtAdmitRate(alpha));

        var nullValue = chanceRuleApplies ? Round(chance.CutAtAdmitRate(ChanceAdmitBudget)) : double.NaN;

        return new CutDerivation(
            Threshold: name,
            PreCalibrationValue: preCalibration,
            FitRows: fit.Count,
            HeldOutRows: held.Count,
            TargetAdmitRate: alpha,
            DerivedValue: derived,
            DerivedFitAdmitRate: fit.AdmitRate(derived),
            DerivedHeldOutAdmitRate: held.AdmitRate(derived),
            PreCalibrationFitAdmitRate: fit.AdmitRate(preCalibration),
            PreCalibrationHeldOutAdmitRate: held.AdmitRate(preCalibration),
            NullDerivedValue: nullValue,
            NullAdmitRateAtDerived: chance.AdmitRate(derived),
            NullRows: chance.Count,
            FitPercentiles: new Dictionary<string, double>
            {
                ["p05"] = Round(fit.Percentile(5)),
                ["p25"] = Round(fit.Percentile(25)),
                ["p50"] = Round(fit.Percentile(50)),
                ["p75"] = Round(fit.Percentile(75)),
                ["p90"] = Round(fit.Percentile(90)),
                ["p95"] = Round(fit.Percentile(95)),
                ["p99"] = Round(fit.Percentile(99)),
                ["mean"] = Round(fit.Mean),
            });
    }

    /// <summary>
    /// Round half away from zero to <see cref="Places"/>. The ONLY adjustment any derived value
    /// receives — stated so that "derived, never tuned" can be checked rather than believed.
    /// </summary>
    private static double Round(double value) =>
        double.IsNaN(value) ? double.NaN : Math.Round(value, Places, MidpointRounding.AwayFromZero);

    private static void PrintSplit()
    {
        Console.WriteLine("  ── THE SPLIT (named before anything was fitted) ─────────────────────────────");
        Console.WriteLine("     unit: the CUSTOMER. Two rows from one interest map are not independent, so a");
        Console.WriteLine("     case-level split would leak the fit slice into the held-out slice through the map.");
        Console.WriteLine();
        Console.WriteLine($"     HELD OUT ({CalibrationSplit.HeldOut.Count})  {string.Join("  ", CalibrationSplit.HeldOut)}");
        Console.WriteLine("                   the four personas whose trays the demos PRINT — held out on purpose, so");
        Console.WriteLine("                   \"the number that makes the trays look right\" is structurally unavailable.");
        Console.WriteLine($"     FIT      ({CalibrationSplit.Fit.Count})  {string.Join("  ", CalibrationSplit.Fit)}");
        Console.WriteLine();
    }

    private static void PrintRules()
    {
        Console.WriteLine("  ── THE RULES (written down before the numbers) ──────────────────────────────");
        Console.WriteLine("     RULE 1 — EQUAL-TAIL TRANSPORT.  This is the rule that SHIPS.");
        Console.WriteLine("       α := the fraction of the CONCEPT fit population the pre-calibration constant admits.");
        Console.WriteLine("       cut(space) := the smallest score that space's own fit population produces whose");
        Console.WriteLine("                     admitted right tail is still within α.");
        Console.WriteLine("       Free parameters: none. α is read, not chosen.");
        Console.WriteLine("       ⚠ It PRESERVES the shipped operating point. It cannot show that operating point was");
        Console.WriteLine("         right, and by construction the concept row reproduces the old constant. The one");
        Console.WriteLine("         thing it tests there is STABILITY: the same admit rate on customers never fitted on.");
        Console.WriteLine();
        Console.WriteLine("     RULE 2 — CHANCE TAIL.  Reported, NOT shipped. A second, independent opinion.");
        Console.WriteLine($"       cut := the value an arbitrary catalogue product clears at most {1.0 / Catalogue.Default.All.Count:0.0000} of the time");
        Console.WriteLine("              — one expected by-chance admission per query, a budget fixed by the catalogue's");
        Console.WriteLine("              size (1/99) rather than chosen.");
        Console.WriteLine("       Applies only to the two cuts that ask \"is this related at all\": the dense floor and");
        Console.WriteLine("       the attribution floor. Chance has no opinion about which TRAY a related item goes in.");
        Console.WriteLine();
        Console.WriteLine("     HELD-OUT USE — one question, asked once, after the cuts are fixed: does the derived");
        Console.WriteLine("       value admit at the same rate on customers the derivation never saw? No cut is moved");
        Console.WriteLine("       because a held-out number came back unflattering; that move would make the held-out");
        Console.WriteLine("       slice a second fit slice and leave no held-out slice at all.");
        Console.WriteLine();
    }

    private static void PrintCohort(
        IReadOnlyList<CalibrationPersona> personas,
        IReadOnlyList<string> abstaining,
        IReadOnlyList<string> skippedFit,
        IReadOnlyList<string> skippedHeld,
        ScorePopulation offlineArmAttribution)
    {
        var fitLive  = personas.Count(p => CalibrationSplit.IsFit(p.PersonaId) && !p.Abstains);
        var heldLive = personas.Count(p => CalibrationSplit.IsHeldOut(p.PersonaId) && !p.Abstains);

        Console.WriteLine("  ── WHAT ACTUALLY CONTRIBUTED ROWS ──────────────────────────────────────────");
        Console.WriteLine($"     fit customers with a map that reaches retrieval        {fitLive} of {CalibrationSplit.Fit.Count}");
        Console.WriteLine($"     held-out customers with a map that reaches retrieval   {heldLive} of {CalibrationSplit.HeldOut.Count}");

        if (abstaining.Count > 0)
        {
            Console.WriteLine("     abstains before retrieval, so contributes NOTHING:");
            foreach (var line in abstaining) Console.WriteLine($"       · {line}");
        }

        if (skippedFit.Count + skippedHeld.Count > 0)
        {
            Console.WriteLine($"     queries whose dense leg cannot run (unavailable or all-zero vector): {skippedFit.Count} fit, {skippedHeld.Count} held-out");
            Console.WriteLine("       counted, never entered as zeros — a zero row would drag the derived floor down with it.");
            foreach (var line in skippedFit.Concat(skippedHeld)) Console.WriteLine($"       · {line}");
        }

        Console.WriteLine();
        Console.WriteLine("     ⚠ the attribution floor's OFFLINE-ARM population is degenerate and is not fitted on:");
        Console.WriteLine($"       {offlineArmAttribution.Count} rows, of which {offlineArmAttribution.Values.Count(v => v >= 0.999)} sit at 1.000 — a signal matching its own label,");
        Console.WriteLine("       because on that arm the probe IS the searching signal's label. The floor cannot drop");
        Console.WriteLine("       anything there. It is fitted on the label × product-document population instead, which");
        Console.WriteLine("       is what the model path's fallback screens and what the constant's own remarks measure.");
        Console.WriteLine();
    }

    private static void PrintCuts(IReadOnlyList<CutDerivation> cuts, string spaceName)
    {
        Console.WriteLine($"  ── DERIVED VALUES — {spaceName} space ───────────────────────────────────────");
        Console.WriteLine();
        Console.WriteLine("     cut                                 was  →  derived     α      fit    held-out   Δ");
        Console.WriteLine("     ────────────────────────────────────────────────────────────────────────────────────");

        foreach (var cut in cuts)
        {
            var moved = Math.Abs(cut.DerivedValue - cut.PreCalibrationValue) >= 0.0005;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"     {cut.Threshold,-34} {cut.PreCalibrationValue:0.000} → {cut.DerivedValue,7:0.000}  {cut.TargetAdmitRate,6:0.000}  {cut.DerivedFitAdmitRate,6:0.000}  {cut.DerivedHeldOutAdmitRate,8:0.000}  {(moved ? "MOVED" : "same")}"));
        }

        Console.WriteLine();
        Console.WriteLine("     per cut, in full:");
        foreach (var cut in cuts)
        {
            Console.WriteLine();
            Console.WriteLine($"     · {cut.Threshold}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"       population        fit {cut.FitRows} rows, held-out {cut.HeldOutRows} rows, chance {cut.NullRows} rows"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"       fit shape         p05 {cut.FitPercentiles["p05"]:0.000}  p25 {cut.FitPercentiles["p25"]:0.000}  p50 {cut.FitPercentiles["p50"]:0.000}  p75 {cut.FitPercentiles["p75"]:0.000}  p95 {cut.FitPercentiles["p95"]:0.000}  mean {cut.FitPercentiles["mean"]:0.000}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"       old constant      admits {cut.PreCalibrationFitAdmitRate:0.000} of fit, {cut.PreCalibrationHeldOutAdmitRate:0.000} of held-out, IN THIS SPACE"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"       rule 1 derived    {cut.DerivedValue:0.000} — admits {cut.DerivedFitAdmitRate:0.000} of fit (target α {cut.TargetAdmitRate:0.000}), {cut.DerivedHeldOutAdmitRate:0.000} of held-out"));

            var generalises = !double.IsNaN(cut.DerivedHeldOutAdmitRate) &&
                              Math.Abs(cut.DerivedHeldOutAdmitRate - cut.TargetAdmitRate) <= 0.10;
            Console.WriteLine($"       held-out verdict  |realised − α| = {Math.Abs(cut.DerivedHeldOutAdmitRate - cut.TargetAdmitRate):0.000}  → {(generalises ? "consistent with the fit slice" : "DIFFERS from the fit slice — declared, not repaired")}");

            if (!double.IsNaN(cut.NullDerivedValue))
            {
                var agrees = Math.Abs(cut.NullDerivedValue - cut.DerivedValue) <= 0.05;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"       rule 2 (chance)   {cut.NullDerivedValue:0.000} — {(agrees ? "AGREES with rule 1 to within 0.05" : "DISAGREES with rule 1")}"));
            }
            else
            {
                Console.WriteLine("       rule 2 (chance)   not applicable — a tray-routing line is not a relatedness question");
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"       chance diagnostic {cut.NullAdmitRateAtDerived:0.000} of ARBITRARY catalogue products clear the derived value"));
        }

        Console.WriteLine();
    }
}
