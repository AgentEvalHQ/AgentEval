// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Compliance.Gdpr.Calibration;

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
        => AgentEval.Calibration.AgreementMetrics.Accuracy(pairs);   // single-sourced in AgentEval.Core (ADR-021 §8)

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
    /// kappa in (-inf, 1]. Returns <c>0</c> for an empty collection. Returns
    /// <see cref="double.NaN"/> when expected agreement is degenerate (<c>pe ≈ 1.0</c>).
    /// </returns>
    public static double CohensKappa(IReadOnlyList<(string Expected, string Actual)> pairs)
        => AgentEval.Calibration.AgreementMetrics.CohensKappa(pairs);   // single-sourced in AgentEval.Core (ADR-021 §8)
}
