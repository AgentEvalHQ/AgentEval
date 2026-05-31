// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Calibration;

/// <summary>
/// Shared calibration statistics (ARC-04). The median / standard-deviation / agreement / weighted-score /
/// consensus / t-value logic was duplicated near-verbatim in <see cref="CalibratedJudge"/> and
/// <see cref="CalibratedEvaluator"/>; both now delegate here so a fix to a formula or the t-table is made
/// once. Pure functions over explicit inputs (option-derived values are passed in).
/// </summary>
internal static class CalibrationMath
{
    /// <summary>Median of the scores (mean of the two middle values for an even count). 0 when empty.</summary>
    public static double Median(IReadOnlyList<double> scores)
    {
        var count = scores.Count;
        if (count == 0) return 0;

        var sorted = scores.OrderBy(s => s).ToList();
        if (count % 2 == 0)
            return (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0;
        return sorted[count / 2];
    }

    /// <summary>Sample standard deviation (n-1). 0 when fewer than two scores.</summary>
    public static double StandardDeviation(IReadOnlyList<double> scores)
    {
        if (scores.Count < 2) return 0;

        var mean = scores.Average();
        var sumSquares = scores.Sum(s => (s - mean) * (s - mean));
        return Math.Sqrt(sumSquares / (scores.Count - 1));
    }

    /// <summary>
    /// Inter-judge agreement on a 0–100 scale: the inverse of the coefficient of variation
    /// (stdDev / mean), clamped to [0, 100]. 100 when fewer than two scores or all scores are zero.
    /// </summary>
    public static double Agreement(IReadOnlyList<double> scores, double stdDev)
    {
        if (scores.Count < 2) return 100;

        var mean = scores.Average();
        if (mean == 0) return scores.All(s => s == 0) ? 100 : 0;

        var cv = stdDev / mean;
        var agreement = Math.Max(0, 100 - (cv * 100));
        return Math.Min(100, agreement);
    }

    /// <summary>
    /// Weighted mean of judge scores using <paramref name="judgeWeights"/> (default weight 1.0 for
    /// unlisted judges). Falls back to the plain mean when no weights are provided or total weight is 0.
    /// </summary>
    public static double WeightedScore(
        IReadOnlyDictionary<string, double> judgeScores,
        IReadOnlyDictionary<string, double>? judgeWeights)
    {
        if (judgeWeights == null || judgeWeights.Count == 0)
            return judgeScores.Values.Average();

        var totalWeight = 0.0;
        var weightedSum = 0.0;
        foreach (var (judgeName, score) in judgeScores)
        {
            var weight = judgeWeights.GetValueOrDefault(judgeName, 1.0);
            weightedSum += score * weight;
            totalWeight += weight;
        }

        return totalWeight > 0 ? weightedSum / totalWeight : judgeScores.Values.Average();
    }

    /// <summary>
    /// True when the judges are within <paramref name="consensusTolerance"/> (max − min ≤ tolerance).
    /// True for fewer than two scores.
    /// </summary>
    public static bool WithinConsensus(IReadOnlyList<double> scores, double consensusTolerance)
    {
        if (scores.Count < 2) return true;
        return (scores.Max() - scores.Min()) <= consensusTolerance;
    }

    /// <summary>
    /// Two-tailed Student's t critical value for the given degrees of freedom and confidence level
    /// (0.90 / 0.95 / 0.99 tiers; a coarse 1.5 below 0.90). Single hand-rolled table (ARC-04).
    /// </summary>
    public static double GetTValue(int degreesOfFreedom, double confidenceLevel)
    {
        if (confidenceLevel >= 0.99)
        {
            return degreesOfFreedom switch
            {
                1 => 63.657, 2 => 9.925, 3 => 5.841, 4 => 4.604,
                5 => 4.032, 6 => 3.707, 7 => 3.499, 8 => 3.355,
                9 => 3.250, 10 => 3.169,
                <= 20 => 2.845, <= 30 => 2.756, _ => 2.576
            };
        }
        if (confidenceLevel >= 0.95)
        {
            return degreesOfFreedom switch
            {
                1 => 12.706, 2 => 4.303, 3 => 3.182, 4 => 2.776,
                5 => 2.571, 6 => 2.447, 7 => 2.365, 8 => 2.306,
                9 => 2.262, 10 => 2.228,
                <= 20 => 2.086, <= 30 => 2.045, _ => 1.960
            };
        }
        if (confidenceLevel >= 0.90)
        {
            return degreesOfFreedom switch
            {
                1 => 6.314, 2 => 2.920, 3 => 2.353, 4 => 2.132,
                5 => 2.015, 6 => 1.943, 7 => 1.895, 8 => 1.860,
                9 => 1.833, 10 => 1.812,
                <= 20 => 1.725, <= 30 => 1.699, _ => 1.645
            };
        }
        return 1.5;
    }
}
