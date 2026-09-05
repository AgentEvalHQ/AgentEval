// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using Galaxus.RecommendationAgent.Domain;

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
/// </remarks>
public static class ConfidenceBands
{
    /// <summary>At or above this, a recommendation reaches the primary tray. UNMEASURED.</summary>
    public const double PrimaryThreshold = 0.70;

    /// <summary>Below this, a recommendation is dropped entirely. UNMEASURED.</summary>
    public const double SecondaryThreshold = 0.45;

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
