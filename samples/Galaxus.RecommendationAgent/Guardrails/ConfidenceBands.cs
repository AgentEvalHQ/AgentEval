// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Retrieval;

namespace Galaxus.RecommendationAgent.Guardrails;

/// <summary>Which tray a self-reported confidence routes a recommendation into (§F.7).</summary>
public enum ConfidenceBand
{
    /// <summary>Below <see cref="ConfidenceBands.SecondaryThreshold"/>, or not a usable number. Removed.</summary>
    Dropped,

    /// <summary>Between the two thresholds. Routed to <c>also_consider</c>.</summary>
    Secondary,

    /// <summary>At or above <see cref="ConfidenceBands.PrimaryThreshold"/>. Routed to <c>recommendations</c>.</summary>
    Primary
}

/// <summary>
/// Stage 7 of the guardrails (§F.7): routes recommendations between the primary tray, the
/// secondary tray, and the floor, by the confidence the model reported for each.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>These thresholds are UNMEASURED, and self-reported LLM confidence is not calibrated
/// until someone measures it.</b> The honest statement is that this is a routing heuristic
/// which keeps weak items out of the primary tray — not a probability. The calibration story
/// (a reliability curve of stated confidence against gold-set correctness) belongs to the eval
/// lane and has not been run. Claiming calibration you have not measured is exactly the failure
/// this design is trying to avoid, so the numbers are stated as what they are: two constants
/// somebody chose.
/// </para>
/// <para>
/// A confidence that is NaN, infinite, negative or above 1 is not a low confidence — it is not
/// a confidence at all, and it is dropped under its own reason
/// (<see cref="GuardrailReasons.ConfidenceOutOfRange"/>) rather than being clamped into a band.
/// Coercing a malformed number into a passing one is how a broken instrument reads clean.
/// </para>
/// <para>
/// Banding runs over BOTH incoming trays and re-routes across them: an item the model put in
/// <c>also_consider</c> with confidence 0.90 is promoted, and one it put in
/// <c>recommendations</c> with 0.50 is demoted. The model proposes; the band decides.
/// </para>
/// <para>
/// ⚠ <b>These two numbers are UNMEASURED and, worse, they are SPACE-DEPENDENT — measured
/// 2026-09-05 (B-9).</b> Half of <c>Demo01.Confidence</c> is a cosine, and a cosine's typical
/// magnitude is a property of the embedding space, not of the product. The 24-dimension authored
/// concept space produces large cosines between related texts; <c>text-embedding-3-small</c>
/// produces small ones for the same pairs. So the SAME catalogue, the SAME interest map and the
/// SAME six products land in different trays depending only on the flag:
/// </para>
/// <list type="bullet">
///   <item><c>-- 1 --offline</c> (concept): confidences 0.46–0.80, six items, three demoted, none dropped.</item>
///   <item><c>-- 1 --offline --real-vectors</c>: confidences 0.40–0.59, so NOTHING clears
///         <see cref="PrimaryThreshold"/> — five demoted to "also consider" and one dropped under
///         <see cref="SecondaryThreshold"/>. The primary tray is empty, and not because the
///         products are worse.</item>
/// </list>
/// <para>
/// <see cref="IEmbeddingSource.SuggestedDenseScoreFloor"/> already says a retrieval floor belongs to
/// a SPACE and may not be carried between them. These thresholds have exactly the same property and
/// nobody had said so. They are NOT re-tuned here: picking a second pair of numbers to make the
/// real-vector tray look like the concept tray would be fitting the threshold to the output, which
/// is the failure this project keeps a rule about. The honest statement is that a band assignment
/// is only comparable within one space, and the space is printed above every tray.
/// </para>
/// <para>
/// ✅ <b>DERIVED PER SPACE 2026-09-05 — the paragraph above is SUPERSEDED in its conclusion and
/// upheld in its diagnosis.</b> The two numbers are no longer unmeasured and no longer shared:
/// <see cref="CalibratedThresholds"/> carries one row per space, each derived on a fit slice that
/// EXCLUDES all four demo personas, by a rule written down before the numbers. What it found:
/// </para>
/// <list type="bullet">
///   <item><b>concept</b> 0.703 / 0.455 against 0.70 / 0.45 — the moves are 0.003 and 0.005, no fit
///         row lies between old and new, and re-running all four demo personas in this space
///         produced BYTE-IDENTICAL output. A calibration that changes nothing is a real result.</item>
///   <item><b>real-vectors</b> 0.520 / 0.437. The old 0.70 admitted <b>0.000</b> of the forty-two
///         fit-slice confidences — that population's 95th percentile is 0.587 — so the empty
///         primary tray this remark reports for three personas is a property of the SPACE, measured
///         as a distribution rather than as three anecdotes. At 0.520 the tray fills: Marco's and
///         Sofia's demo-01 trays go from 5 demoted / 0 primary to 0 demoted / 5 primary, Nadia's
///         from 5 demoted to 3.</item>
/// </list>
/// <para>
/// ⚠ <b>And the held-out slice REFUSED to corroborate the real row.</b> 0.520 admits 0.286 of the
/// fit slice — its target — and <b>0.722</b> of the four demo personas it was never fitted on. Their
/// confidences run higher than the cohort's in this space. Declared and NOT repaired: a cut moved
/// because the held-out slice came back unflattering is a cut fitted on the held-out slice. Read the
/// real-space bands as an operating point that holds on the ten customers it was derived from and
/// demonstrably not on the four it was not.
/// </para>
/// </remarks>
public static class ConfidenceBands
{
    /// <summary>
    /// At or above this, a recommendation reaches the primary tray. DERIVED PER SPACE — see
    /// <see cref="CalibratedThresholds"/>.
    /// </summary>
    /// <remarks>
    /// No longer a <c>const</c>, and the change is the point: a compile-time constant cannot depend
    /// on which embedding space a run resolved, and this number does. It reads
    /// <see cref="CalibratedThresholds.Current"/>, which is keyed off the RESOLVED source, so a run
    /// that fell back to the concept space is banded by the concept space's cut.
    /// </remarks>
    public static double PrimaryThreshold => CalibratedThresholds.Current.ConfidencePrimary;

    /// <summary>
    /// Below this, a recommendation is dropped entirely. DERIVED PER SPACE — see
    /// <see cref="CalibratedThresholds"/>.
    /// </summary>
    public static double SecondaryThreshold => CalibratedThresholds.Current.ConfidenceSecondary;

    /// <summary>Classifies one confidence value.</summary>
    /// <param name="confidence">The model's self-reported confidence, nominally 0..1.</param>
    public static ConfidenceBand Classify(double confidence)
    {
        if (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence < 0.0 || confidence > 1.0)
            return ConfidenceBand.Dropped;

        if (confidence >= PrimaryThreshold) return ConfidenceBand.Primary;
        return confidence >= SecondaryThreshold ? ConfidenceBand.Secondary : ConfidenceBand.Dropped;
    }

    /// <summary>True when <paramref name="confidence"/> is not a usable number in 0..1.</summary>
    /// <param name="confidence">The model's self-reported confidence.</param>
    public static bool IsOutOfRange(double confidence) =>
        double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence < 0.0 || confidence > 1.0;

    /// <summary>Re-routes both trays by band and drops everything below the floor.</summary>
    /// <param name="set">The answer so far.</param>
    /// <param name="context">The catalogue-derived bar. Unused by this stage; taken for pipeline uniformity.</param>
    /// <param name="ledger">The ledger every drop and demotion is written to.</param>
    public static RecommendationSet Apply(RecommendationSet set, GuardrailContext context, GuardrailLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledger);

        var primary = new List<RecommendationDto>();
        var secondary = new List<RecommendationDto>();

        Route(set.Recommendations, fromPrimaryTray: true, primary, secondary, ledger);
        Route(set.AlsoConsider, fromPrimaryTray: false, primary, secondary, ledger);

        return set with { Recommendations = primary, AlsoConsider = secondary };
    }

    private static void Route(
        IReadOnlyList<RecommendationDto> items,
        bool fromPrimaryTray,
        List<RecommendationDto> primary,
        List<RecommendationDto> secondary,
        GuardrailLedger ledger)
    {
        foreach (var item in items)
        {
            if (IsOutOfRange(item.Confidence))
            {
                ledger.Drop(GuardrailStage.ConfidenceBands, GuardrailReasons.ConfidenceOutOfRange, item.ProductId,
                    string.Create(CultureInfo.InvariantCulture,
                        $"confidence {item.Confidence} is not a number in 0..1; a malformed confidence is dropped rather than clamped into a passing band"));
                continue;
            }

            switch (Classify(item.Confidence))
            {
                case ConfidenceBand.Primary:
                    primary.Add(item);
                    break;

                case ConfidenceBand.Secondary:
                    if (fromPrimaryTray)
                    {
                        ledger.Demote(GuardrailStage.ConfidenceBands, GuardrailReasons.LowConfidence, item.ProductId,
                            string.Create(CultureInfo.InvariantCulture,
                                $"confidence {item.Confidence:0.00} is below the primary threshold {PrimaryThreshold:0.00} — moved to 'also consider'"));
                    }

                    secondary.Add(item);
                    break;

                default:
                    ledger.Drop(GuardrailStage.ConfidenceBands, GuardrailReasons.LowConfidence, item.ProductId,
                        string.Create(CultureInfo.InvariantCulture,
                            $"confidence {item.Confidence:0.00} is below the floor {SecondaryThreshold:0.00}"));
                    break;
            }
        }
    }
}
