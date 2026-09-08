// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// The four score cuts that belong to an embedding SPACE rather than to the product.
/// </summary>
/// <param name="DenseScoreFloor">
/// <see cref="HybridRetriever"/>'s dense cosine floor: a candidate below it is not fused.
/// </param>
/// <param name="AttributionFloor">
/// <c>Demo01.AttributionFloor</c>: a derived interest below it does not explain a product.
/// </param>
/// <param name="ConfidencePrimary">
/// <c>ConfidenceBands.PrimaryThreshold</c>: at or above it, a recommendation reaches the primary tray.
/// </param>
/// <param name="ConfidenceSecondary">
/// <c>ConfidenceBands.SecondaryThreshold</c>: below it, a recommendation is dropped entirely.
/// </param>
public sealed record SpaceThresholds(
    float DenseScoreFloor,
    double AttributionFloor,
    double ConfidencePrimary,
    double ConfidenceSecondary);

/// <summary>
/// The ONE place the four space-dependent cuts are written down, one row per embedding space,
/// each row DERIVED rather than chosen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> Until 2026-09-05 three constants — a dense floor, an attribution
/// floor and a pair of confidence bands — had ONE value serving TWO embedding spaces, and
/// <c>MEASUREMENT_STATUS.md</c> §20.13 item 3 says what was owed: *"they should be derived per
/// space from one held-out slice, in one pass, rather than three times by hand"*. This is that one
/// pass's result. <see cref="IEmbeddingSource.SuggestedDenseScoreFloor"/> was already the right
/// SEAM for one of the three and carried the same value on both sides of it; the other two had no
/// seam at all.
/// </para>
/// <para>
/// <b>THE DERIVATION, in one paragraph.</b> The held-out slice is named first, in
/// <c>Galaxus.RecommendationAgent.Evals.Calibration.CalibrationSplit</c>: the four DEMO personas
/// are held out and the other ten authored customers are the fit slice — that direction, so that no
/// cut can have been steered by a tray anybody looks at. Each cut screens a score distribution; the
/// distribution is collected on the fit slice, in both spaces, from the shipped arithmetic. The
/// rule is EQUAL-TAIL TRANSPORT: read α, the fraction of the CONCEPT fit population the old
/// one-size constant admits, then take each space's own cut to be the smallest score its own fit
/// population produces whose admitted tail is still within α. Nothing is swept and nothing is moved
/// to make an output look right. The full record — populations, percentiles, realised rates on the
/// held-out slice — is committed beside the harness under
/// <c>samples/Galaxus.RecommendationAgent.Evals/Calibration/derived/</c>.
/// </para>
/// <para>
/// ⚠ <b>What transport CANNOT establish, stated at the top rather than in a footnote.</b> It
/// preserves the operating point the project already shipped. It does not show that operating point
/// was ever right: α is READ from the concept space, so the concept row reproduces the old constant
/// by construction and only the real row moves. The one thing the concept row is tested on is
/// STABILITY — whether the old constant's admit rate is the same on customers the derivation never
/// saw. A second, independent rule (a chance-tail cut on the null distribution) is derived and
/// REPORTED by the same harness precisely because transport cannot answer that question, and where
/// the two rules disagree the report says so.
/// </para>
/// <para>
/// <b>A future home.</b> ADR-030 Slice 2's <c>ChanceFloor.Empirical</c> machinery is the right
/// long-term expression of the second rule — a chance floor derived from a measured null rather
/// than from a closed form. Slice 2 is behind unratified questions, so this is done by hand here
/// and the migration is named rather than assumed.
/// </para>
/// </remarks>
public static class CalibratedThresholds
{
    /// <summary>
    /// The one-value-for-two-spaces constants this table replaces, kept permanently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not history for its own sake — it is the transport rule's ANCHOR.</b> α is defined as the
    /// admit rate of these values on the concept fit slice, so a calibration run must be able to
    /// read them after the derived values have shipped. Deleting them would make the derivation
    /// unreproducible the moment it took effect, and a threshold whose derivation cannot be re-run
    /// is a threshold somebody chose.
    /// </para>
    /// <para>
    /// Provenance of each: the dense floor 0.28 was <c>UncalibratedDenseScoreFloor</c>, carried
    /// identically by the concept and the Azure source and documented in both as TO-CALIBRATE; the
    /// attribution floor 0.20 and the bands 0.70 / 0.45 were documented as UNMEASURED. All four
    /// were chosen while only the 24-dimension concept space existed.
    /// </para>
    /// </remarks>
    public static SpaceThresholds PreCalibration { get; } = new(
        DenseScoreFloor: 0.28f,
        AttributionFloor: 0.20,
        ConfidencePrimary: 0.70,
        ConfidenceSecondary: 0.45);

    /// <summary>
    /// The authored 24-dimension concept space. DERIVED 2026-09-05; record
    /// <c>Calibration/derived/calibration.concept.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three of the four rows did not move, and that is a result, not an absence of one.</b> α is
    /// read from THIS space, so the concept row reproduces the pre-calibration constant by
    /// construction wherever the constant already sat on a value the population produces — which the
    /// dense floor (0.280) and the attribution floor (0.200) both did. The two bands moved by 0.003
    /// and 0.005: the rule returns the smallest score the fit population actually TAKES whose
    /// admitted tail is still within α, and 0.700 and 0.450 are not scores anything took. Both moves
    /// are immaterial — no fit row lies between the old and the new value — and both are shipped
    /// anyway, because rounding a derived value back onto the constant it was meant to replace is
    /// the tuning move running backwards.
    /// </para>
    /// <para>
    /// <b>What the held-out slice said about this row.</b> The dense floor and the attribution floor
    /// generalise (realised admit rate on customers never fitted on: 0.740 against α 0.803, and
    /// 0.310 against α 0.331). The two bands do NOT: the demo personas' confidences run higher than
    /// the cohort's, so 0.455 admits every one of the eighteen held-out rows where α is 0.738. That
    /// is declared and NOT repaired — moving a cut because a held-out number came back unflattering
    /// would convert the held-out slice into a second fit slice.
    /// </para>
    /// <para>
    /// ⚠ <b>What rule 2 said, and it is the uncomfortable half.</b> A chance-tail cut on the same
    /// corpus puts the dense floor at <b>0.839</b>, not 0.280 — because in this space 0.280 is
    /// cleared by <b>57 %</b> of ARBITRARY catalogue products for an arbitrary query. The dense
    /// floor barely filters here; it is close to a no-op wearing a threshold's name. Transport
    /// cannot see that, which is exactly why rule 2 is computed. It is reported and not shipped:
    /// moving the floor to 0.839 is a redesign of retrieval, not a calibration of it.
    /// </para>
    /// </remarks>
    public static SpaceThresholds Concept { get; } = new(
        DenseScoreFloor: 0.280f,
        AttributionFloor: 0.200,
        ConfidencePrimary: 0.703,
        ConfidenceSecondary: 0.455);

    /// <summary>
    /// The real <c>text-embedding-3-small</c> space. DERIVED 2026-09-05; record
    /// <c>Calibration/derived/calibration.real-vectors.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every row moved, and one of them moved a lot.</b> Measured in this space at the OLD
    /// constants, on the fit slice: the dense floor admitted 0.377 where α is 0.803; the attribution
    /// floor admitted 0.417 where α is 0.331; and <c>PrimaryThreshold</c> 0.70 admitted
    /// <b>0.000</b> — not a single one of the forty-two fit confidences reached it, the fit
    /// population's 95th percentile being 0.587. §20.11 item 1 reported the empty primary tray as an
    /// observation about three personas; this is the same fact as a distribution, and it is not a
    /// property of those personas.
    /// </para>
    /// <para>
    /// <b>What the held-out slice said.</b> The attribution floor generalises (0.361 against α
    /// 0.331). The other three do not: the dense floor admits 0.972 of held-out rows against α
    /// 0.803, the drop line 0.889 against 0.738, and the primary line <b>0.722 against 0.286</b>.
    /// The demo personas sit higher in this space than the cohort does, on every one of the three.
    /// Declared, not repaired, and it bounds what these three numbers may be said to be: an
    /// operating point that holds on the ten customers it was derived from, and demonstrably not on
    /// the four it was not.
    /// </para>
    /// <para>
    /// ⚠ <b>Rule 2 disagrees with the dense floor here too, in the other direction:</b> a chance-tail
    /// cut is 0.417 against transport's 0.223, and 0.223 is cleared by 24 % of arbitrary catalogue
    /// products. Transport moved this floor DOWN — it admits more, not less — because the operating
    /// point it inherited was itself permissive. Reported, not shipped, for the concept row's reason.
    /// </para>
    /// </remarks>
    public static SpaceThresholds RealVectors { get; } = new(
        DenseScoreFloor: 0.223f,
        AttributionFloor: 0.221,
        ConfidencePrimary: 0.520,
        ConfidenceSecondary: 0.437);

    /// <summary>The row for one space.</summary>
    /// <param name="space">A RESOLVED space — never <see cref="EmbeddingSpaceChoice.Auto"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="space"/> is not a resolved space.</exception>
    public static SpaceThresholds For(EmbeddingSpaceChoice space) => space switch
    {
        EmbeddingSpaceChoice.ConceptVectors => Concept,
        EmbeddingSpaceChoice.RealVectors    => RealVectors,
        _ => throw new ArgumentOutOfRangeException(
                 nameof(space),
                 space,
                 "Thresholds are a property of a RESOLVED space. 'Auto' is a request, not a space.")
    };

    /// <summary>
    /// The row in force for this process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed off the RESOLVED source (<see cref="EmbeddingSpace.Current"/>), never off the
    /// requested one: <c>--real-vectors</c> without credentials falls back to the concept space,
    /// and a run that retrieved in the concept space must be banded by the concept space's cuts.
    /// Reading <see cref="EmbeddingSpace.Requested"/> here would be exactly the half-and-half state
    /// the selector exists to prevent.
    /// </para>
    /// <para>
    /// Before anything has resolved this returns <see cref="Concept"/>. That is not a fallback in
    /// disguise: nothing has retrieved yet, so no real-space score can be in play, and the concept
    /// row is what <see cref="EmbeddingSpace.AutoPrefers"/> would resolve to anyway.
    /// </para>
    /// </remarks>
    public static SpaceThresholds Current => EmbeddingSpace.Current is { } resolution
        ? For(resolution.Chosen)
        : Concept;
}
