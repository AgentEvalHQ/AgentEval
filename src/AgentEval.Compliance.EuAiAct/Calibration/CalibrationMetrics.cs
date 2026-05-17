// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Compliance.EuAiAct.Calibration;

/// <summary>
/// Statistical metrics for assessing judge calibration quality.
/// </summary>
public static class CalibrationMetrics
{
    /// <summary>
    /// Computes accuracy: the fraction of entries where the actual verdict matches
    /// the expected verdict (case-insensitive).
    /// </summary>
    /// <param name="pairs">
    /// Pairs of (expected verdict, actual verdict). Both sides are compared
    /// case-insensitively.
    /// </param>
    /// <returns>
    /// A value in [0, 1]. Returns <c>0</c> for an empty collection.
    /// </returns>
    public static double Accuracy(IReadOnlyList<(string Expected, string Actual)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0) return 0;
        var matches = pairs.Count(p =>
            string.Equals(p.Expected, p.Actual, StringComparison.OrdinalIgnoreCase));
        return (double)matches / pairs.Count;
    }

    /// <summary>
    /// Computes Cohen's kappa — a measure of categorical inter-rater agreement that
    /// corrects for chance agreement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// kappa = (po - pe) / (1 - pe), where:
    /// <list type="bullet">
    ///   <item><description><c>po</c> — observed agreement (proportion of matching verdicts).</description></item>
    ///   <item><description>
    ///     <c>pe</c> — expected agreement by chance: for each label c,
    ///     <c>p_expected(c) * p_actual(c)</c>.
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Returns <c>1.0</c> when pe = 1.0 (all raters agree on every label with identical
    /// marginal distributions — trivially perfect agreement, divide-by-zero guard).
    /// </para>
    /// </remarks>
    /// <param name="pairs">Pairs of (expected verdict, actual verdict).</param>
    /// <returns>
    /// kappa in (-inf, 1]. Returns <c>0</c> for an empty collection.
    /// </returns>
    public static double CohensKappa(IReadOnlyList<(string Expected, string Actual)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0) return 0;

        var labels = pairs
            .SelectMany(p => new[] { p.Expected, p.Actual })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var n = pairs.Count;

        // Observed agreement
        double po = (double)pairs.Count(p =>
            string.Equals(p.Expected, p.Actual, StringComparison.OrdinalIgnoreCase)) / n;

        // Marginal proportions for each rater
        var expectedDist = labels.ToDictionary(
            l => l,
            l => (double)pairs.Count(p =>
                string.Equals(p.Expected, l, StringComparison.OrdinalIgnoreCase)) / n,
            StringComparer.OrdinalIgnoreCase);

        var actualDist = labels.ToDictionary(
            l => l,
            l => (double)pairs.Count(p =>
                string.Equals(p.Actual, l, StringComparison.OrdinalIgnoreCase)) / n,
            StringComparer.OrdinalIgnoreCase);

        double pe = labels.Sum(l => expectedDist[l] * actualDist[l]);

        // Guard: if pe == 1.0, all agreement is by chance (trivial case)
        if (Math.Abs(1.0 - pe) < 1e-10) return 1.0;

        return (po - pe) / (1.0 - pe);
    }
}
