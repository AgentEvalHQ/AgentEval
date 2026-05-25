// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Calibration;

/// <summary>
/// Statistical metrics for assessing agentic judge calibration quality.
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
    /// Returns <see cref="double.NaN"/> when <c>pe ≈ 1.0</c> (degenerate expected-agreement —
    /// typically single-class data where every entry has the same label). Mathematically the
    /// kappa is undefined in this case (0/0); historically this guard returned <c>1.0</c>
    /// trivially, which was misleading on regulator-facing reports. Callers should treat NaN as
    /// "kappa undefined — review the golden dataset for class balance" rather than as a PASS.
    /// Aggregator gates that compare <c>kappa &gt;= threshold</c> naturally evaluate to false on
    /// NaN, which forces operator review.
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

        // Degenerate guard (F-004): when pe ≈ 1.0 the kappa expression is 0/0. Return NaN so
        // the consumer renders "undefined" and the aggregator gate fails the pillar (forces
        // operator review). Pre-F-004 this returned 1.0 — misleading on regulator-facing reports.
        if (Math.Abs(1.0 - pe) < 1e-10) return double.NaN;

        return (po - pe) / (1.0 - pe);
    }
}
