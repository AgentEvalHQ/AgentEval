// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// Gatekeeper next-wave item — joins every judge axis's most recent <see cref="CalibrationReport"/> (typically
/// sourced from <see cref="ICalibrationReportStore.LoadLatestAllAsync"/>) into one composite fleet-health view.
/// Mirrors <c>SkillSecurityIndex</c>'s honesty discipline: a missing (never-calibrated) axis is NEVER
/// fabricated into a passing score — it is reported as un-measured (<see cref="GatekeeperFleetHealthReport.NeverCalibratedAxes"/>),
/// and the composite means are computed only over axes that actually have a report.
/// <para>High value for an ops/Mission-Control surface once one exists (Mission Control's compliance-matrix
/// GraphQL layer is a real, live precedent for exactly this kind of cross-cutting health surface) — this type
/// itself is transport-agnostic, no MC/CLI dependency.</para>
/// <para><b>Experimental:</b> shipped 2026-07-17, no real-world consumer yet (no CLI verb, no Mission Control
/// wiring) — the shape may still change once one exists. Suppress via
/// <c>&lt;NoWarn&gt;$(NoWarn);AGENTEVAL_GATEKEEPER_PREVIEW001&lt;/NoWarn&gt;</c> once you've read this note.</para>
/// </summary>
[Experimental("AGENTEVAL_GATEKEEPER_PREVIEW001")]
public static class GatekeeperFleetHealthIndex
{
    /// <summary>
    /// Computes the composite fleet-health report.
    /// </summary>
    /// <param name="reportsByAxis">
    /// Every axis the caller wants to track, mapped to its latest report — <see langword="null"/> for an axis
    /// that has never been calibrated. Pass the FULL expected axis set here (not just the ones with a report)
    /// so <see cref="GatekeeperFleetHealthReport.NeverCalibratedAxes"/> can actually report a gap; an axis
    /// missing from this dictionary entirely is invisible to this method, not the same as a <see langword="null"/> entry.
    /// </param>
    /// <param name="staleAfter">A report older than this is listed in <see cref="GatekeeperFleetHealthReport.StaleAxes"/> — informational, never excluded from the composite means.</param>
    /// <param name="clock">Clock for the staleness check. Defaults to <see cref="TimeProvider.System"/>.</param>
    public static GatekeeperFleetHealthReport Compute(
        IReadOnlyDictionary<string, CalibrationReport?> reportsByAxis,
        TimeSpan staleAfter,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(reportsByAxis);

        // Walk key-value pairs together (not Values-then-Axis separately) so NeverCalibratedAxes and
        // StaleAxes both use the DICTIONARY KEY as the axis label consistently — a report's own .Axis
        // field could in principle diverge from the key a caller chose to file it under, and mixing label
        // sources between the two lists would make them reference inconsistent label spaces.
        var measured = new List<CalibrationReport>();
        var neverCalibratedList = new List<string>();
        var staleList = new List<string>();
        foreach (var (axisKey, report) in reportsByAxis)
        {
            if (report is null)
            {
                neverCalibratedList.Add(axisKey);
                continue;
            }

            measured.Add(report);
            if (report.IsStale(staleAfter, clock))
            {
                staleList.Add(axisKey);
            }
        }

        var neverCalibrated = neverCalibratedList.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var stale = staleList.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        double? meanAccuracy = measured.Count > 0 ? measured.Average(r => r.DecisiveAccuracy) : null;
        var totalDangerousErrors = measured.Sum(r => r.DangerousErrorCount);
        double? meanKappa = measured.Count > 0 ? measured.Average(r => r.KappaVsGold) : null;

        var explanation = measured.Count == 0
            ? $"No axis has ever been calibrated ({reportsByAxis.Count} axis(es) tracked, all NEVER calibrated) — nothing to score."
            : $"{measured.Count}/{reportsByAxis.Count} axis(es) calibrated; mean decisive accuracy {meanAccuracy:P1}; " +
              $"{totalDangerousErrors} total dangerous error(s) across all calibrated axes" +
              (neverCalibrated.Length > 0 ? $"; NEVER calibrated: {string.Join(", ", neverCalibrated)}" : "") +
              (stale.Length > 0 ? $"; STALE (older than {staleAfter.TotalDays:F0}d): {string.Join(", ", stale)}" : "") +
              ".";

        return new GatekeeperFleetHealthReport(
            reportsByAxis, meanAccuracy, totalDangerousErrors, meanKappa, stale, neverCalibrated, explanation);
    }
}

/// <summary>The composite Gatekeeper Fleet Health result — see <see cref="GatekeeperFleetHealthIndex.Compute"/>.</summary>
/// <param name="ReportsByAxis">Every tracked axis mapped to its latest report (<see langword="null"/> = never calibrated) — the raw input, echoed back for a caller that wants per-axis detail.</param>
/// <param name="MeanDecisiveAccuracy">Mean <see cref="CalibrationReport.DecisiveAccuracy"/> across calibrated axes only. <see langword="null"/> if zero axes have a report — never fabricated.</param>
/// <param name="TotalDangerousErrors">Sum of <see cref="CalibrationReport.DangerousErrorCount"/> across calibrated axes — the number that matters most, per this repo's grading motto.</param>
/// <param name="MeanKappa">Mean <see cref="CalibrationReport.KappaVsGold"/> across calibrated axes only. <see langword="null"/> if zero axes have a report.</param>
/// <param name="StaleAxes">Axes whose report is older than the caller-supplied staleness threshold — informational, does not affect the means.</param>
/// <param name="NeverCalibratedAxes">Axes with no report at all.</param>
/// <param name="Explanation">Human-readable summary — states plainly which axes were/weren't measured, never silently treats a missing axis as passing.</param>
[Experimental("AGENTEVAL_GATEKEEPER_PREVIEW001")]
public sealed record GatekeeperFleetHealthReport(
    IReadOnlyDictionary<string, CalibrationReport?> ReportsByAxis,
    double? MeanDecisiveAccuracy,
    int TotalDangerousErrors,
    double? MeanKappa,
    IReadOnlyList<string> StaleAxes,
    IReadOnlyList<string> NeverCalibratedAxes,
    string Explanation);
