// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Trust;

/// <summary>A trust band (Phase 3, P3-10) — the qualitative reading of a 0-100 <see cref="TrustScoreResult.Score"/>.</summary>
public enum TrustBand
{
    /// <summary>No signal contributed — nothing to score (score was null).</summary>
    Unknown,

    /// <summary>Below 30 — distrust dominates; investigate before relying on the subject.</summary>
    Critical,

    /// <summary>30 to under 60 — meaningfully degraded trust.</summary>
    Low,

    /// <summary>60 to under 85 — generally trustworthy with some soft signals.</summary>
    Moderate,

    /// <summary>85 and above — strongly trusted.</summary>
    High,
}

/// <summary>
/// One signal's contribution to the composite (P3-10) — its raw score/weight, the POINTS it added to the 0-100
/// composite (0 when excluded), and whether/why it was excluded. The <see cref="WeightedContribution"/> of the
/// included signals sums to the composite score, so the breakdown is fully reconstructable.
/// </summary>
/// <param name="Name">The signal's name.</param>
/// <param name="Score">Its raw 0..1 score.</param>
/// <param name="Weight">Its weight.</param>
/// <param name="WeightedContribution">Points it added to the 0-100 composite (0 when excluded).</param>
/// <param name="Included">Whether it contributed to the composite.</param>
/// <param name="ExclusionReason">Why it was excluded (skipped / error / non-positive weight), or null when included.</param>
public sealed record TrustSignalContribution(
    string Name,
    double Score,
    double Weight,
    double WeightedContribution,
    bool Included,
    string? ExclusionReason);

/// <summary>The composite trust <see cref="Result"/>, its qualitative <see cref="Band"/>, and the per-signal breakdown (P3-10).</summary>
/// <param name="Result">The underlying <see cref="TrustScoreResult"/>.</param>
/// <param name="Band">The band the score falls in.</param>
/// <param name="Contributions">Per-signal contributions, in the order supplied.</param>
public sealed record TrustScoreBreakdown(
    TrustScoreResult Result,
    TrustBand Band,
    IReadOnlyList<TrustSignalContribution> Contributions);

/// <summary>Extends <see cref="TrustScoreCalculator"/> with the P3-10 per-signal breakdown + band classification.</summary>
public static class TrustScoreBreakdownCalculator
{
    /// <summary>Maps a 0-100 composite (or null) to a <see cref="TrustBand"/>.</summary>
    public static TrustBand ClassifyBand(double? score) => score switch
    {
        null => TrustBand.Unknown,
        >= 85.0 => TrustBand.High,
        >= 60.0 => TrustBand.Moderate,
        >= 30.0 => TrustBand.Low,
        _ => TrustBand.Critical,
    };

    /// <summary>Computes the composite AND the per-signal breakdown + band — the same exclusion discipline as <see cref="TrustScoreCalculator.Compute"/>.</summary>
    public static TrustScoreBreakdown ComputeBreakdown(IReadOnlyList<TrustSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var result = TrustScoreCalculator.Compute(signals);

        var weightSum = signals
            .Where(s => s.Label is not ("skipped" or "error") && s.Weight > 0)
            .Sum(s => s.Weight);

        var contributions = new List<TrustSignalContribution>(signals.Count);
        foreach (var signal in signals)
        {
            if (signal.Label is "skipped" or "error")
            {
                contributions.Add(new(signal.Name, signal.Score, signal.Weight, 0.0, Included: false, signal.Label));
                continue;
            }

            if (signal.Weight <= 0)
            {
                contributions.Add(new(signal.Name, signal.Score, signal.Weight, 0.0, Included: false, "non-positive weight"));
                continue;
            }

            var points = weightSum > 0 ? Math.Clamp(signal.Score, 0.0, 1.0) * signal.Weight / weightSum * 100.0 : 0.0;
            contributions.Add(new(signal.Name, signal.Score, signal.Weight, points, Included: true, ExclusionReason: null));
        }

        return new TrustScoreBreakdown(result, ClassifyBand(result.Score), contributions);
    }
}
