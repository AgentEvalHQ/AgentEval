// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Compliance.EuAiAct.Calibration;

/// <summary>
/// Statistical metrics for assessing judge calibration quality. The math is single-sourced in
/// <see cref="AgentEval.Calibration.AgreementMetrics"/> (AgentEval.Core) so the RedTeam judge-agreement
/// harness and the compliance calibration runners share one implementation (ADR-021 §8). This thin
/// forwarder preserves the existing public surface for consumers in this assembly.
/// </summary>
public static class CalibrationMetrics
{
    /// <summary>Fraction of entries where actual matches expected (case-insensitive); <c>0</c> for empty input.</summary>
    public static double Accuracy(IReadOnlyList<(string Expected, string Actual)> pairs)
        => AgentEval.Calibration.AgreementMetrics.Accuracy(pairs);

    /// <summary>Cohen's kappa (chance-corrected agreement); <c>0</c> for empty input, <see cref="double.NaN"/>
    /// when expected agreement is degenerate (<c>pe ≈ 1.0</c> — single-class data, kappa undefined).</summary>
    public static double CohensKappa(IReadOnlyList<(string Expected, string Actual)> pairs)
        => AgentEval.Calibration.AgreementMetrics.CohensKappa(pairs);
}
