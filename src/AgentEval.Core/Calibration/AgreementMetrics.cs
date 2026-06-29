// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Calibration;

/// <summary>
/// Shared categorical-agreement metrics (single source of truth, reachable from every sub-project that
/// references <c>AgentEval.Core</c>). Used by the compliance calibration runners and by the RedTeam
/// judge-agreement harness (ADR-021 §8). All comparisons are case-insensitive.
/// </summary>
public static class AgreementMetrics
{
    /// <summary>
    /// Computes accuracy: the fraction of entries where the actual verdict matches the expected verdict
    /// (case-insensitive). Returns <c>0</c> for an empty collection.
    /// </summary>
    public static double Accuracy(IReadOnlyList<(string Expected, string Actual)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0) return 0;
        var matches = pairs.Count(p => string.Equals(p.Expected, p.Actual, StringComparison.OrdinalIgnoreCase));
        return (double)matches / pairs.Count;
    }

    /// <summary>
    /// Computes Cohen's kappa — categorical inter-rater agreement corrected for chance.
    /// kappa = (po - pe) / (1 - pe). Returns <c>0</c> for an empty collection, and
    /// <see cref="double.NaN"/> when <c>pe ≈ 1.0</c> (degenerate single-class data — kappa undefined,
    /// 0/0). Callers treat NaN as "undefined — review class balance", never as a pass: a
    /// <c>kappa &gt;= threshold</c> gate naturally evaluates false on NaN.
    /// </summary>
    public static double CohensKappa(IReadOnlyList<(string Expected, string Actual)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0) return 0;

        var labels = pairs
            .SelectMany(p => new[] { p.Expected, p.Actual })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var n = pairs.Count;

        double po = (double)pairs.Count(p =>
            string.Equals(p.Expected, p.Actual, StringComparison.OrdinalIgnoreCase)) / n;

        var expectedDist = labels.ToDictionary(
            l => l,
            l => (double)pairs.Count(p => string.Equals(p.Expected, l, StringComparison.OrdinalIgnoreCase)) / n,
            StringComparer.OrdinalIgnoreCase);

        var actualDist = labels.ToDictionary(
            l => l,
            l => (double)pairs.Count(p => string.Equals(p.Actual, l, StringComparison.OrdinalIgnoreCase)) / n,
            StringComparer.OrdinalIgnoreCase);

        double pe = labels.Sum(l => expectedDist[l] * actualDist[l]);

        // Degenerate guard (F-004): pe ≈ 1.0 ⇒ kappa is 0/0 ⇒ NaN (undefined), never a trivial 1.0.
        if (Math.Abs(1.0 - pe) < 1e-10) return double.NaN;

        return (po - pe) / (1.0 - pe);
    }

    /// <summary>
    /// Per-class F1 over the expected/actual labels: for each label <c>c</c>,
    /// precision = TP / (TP + FP), recall = TP / (TP + FN), F1 = 2·P·R / (P + R) (0 when P+R = 0).
    /// Keyed case-insensitively by the label's first-seen casing. Empty input ⇒ empty map.
    /// </summary>
    public static IReadOnlyDictionary<string, double> PerClassF1(IReadOnlyList<(string Expected, string Actual)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (pairs.Count == 0) return result;

        var labels = pairs
            .SelectMany(p => new[] { p.Expected, p.Actual })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var c in labels)
        {
            var tp = pairs.Count(p => Eq(p.Actual, c) && Eq(p.Expected, c));
            var fp = pairs.Count(p => Eq(p.Actual, c) && !Eq(p.Expected, c));
            var fn = pairs.Count(p => !Eq(p.Actual, c) && Eq(p.Expected, c));
            var precision = tp + fp == 0 ? 0.0 : (double)tp / (tp + fp);
            var recall = tp + fn == 0 ? 0.0 : (double)tp / (tp + fn);
            result[c] = precision + recall == 0 ? 0.0 : 2 * precision * recall / (precision + recall);
        }
        return result;
    }

    /// <summary>
    /// Macro-averaged F1: the unweighted mean of <see cref="PerClassF1"/> over all labels. Returns
    /// <c>0</c> for an empty collection.
    /// </summary>
    public static double MacroF1(IReadOnlyList<(string Expected, string Actual)> pairs)
    {
        var perClass = PerClassF1(pairs);
        return perClass.Count == 0 ? 0.0 : perClass.Values.Average();
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
