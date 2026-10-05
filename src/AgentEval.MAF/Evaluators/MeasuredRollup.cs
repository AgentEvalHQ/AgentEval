// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// The verdict of a report node over already-computed sub-results, read by measurement state — shared by
/// <see cref="MeaiToEvalResultBridge"/> (query and run nodes) and <see cref="UnifiedEvalReport"/> (branches and root).
/// </summary>
/// <remarks>
/// Every sub-result is a required part, as in a <c>CompositeEval</c>. The score is the mean of the measured ones; a part
/// with no verdict is never averaged in as a 0. A measured failure decides (<c>fail</c>); otherwise a part that errored
/// makes the node <c>error</c>; a part that did not run, or withheld its own pass, withholds this one (<c>warn</c>,
/// recorded not measured); a measured warn is a <c>warn</c>. Nothing measured is no verdict — <c>error</c> when a part
/// errored, a withheld <c>warn</c> when a part withheld its pass (its parts were measured, #203 review B9d), else
/// <c>skipped</c>. Before (#203 review rounds 4–5, B10q/B10u), the bridge averaged placeholders, the unified report left
/// errored and skipped parts out and passed on the rest (an unparseable MEAI metric beside a pass read PASS), and both
/// read any non-pass as FAIL/high (a quality WARN shown as FAIL).
/// </remarks>
internal static class MeasuredRollup
{
    internal readonly record struct Verdict(double Value, string Label, bool Passed, string Severity, MeasurementState Measurement);

    internal static Verdict Of(IReadOnlyList<EvalResult> subs)
    {
        ArgumentNullException.ThrowIfNull(subs);

        var measured = subs.Where(s => s.Score.CountsTowardAggregate()).ToList();
        var failing = measured.Where(s => s.Score.Label == "fail").ToList();
        var measuredWarn = measured.Any(s => s.Score.Label == "warn");
        var errored = subs.Any(s => s.Score.Label == "error");
        var withheld = subs.Any(s => s.Score.CensusBucket() == MeasurementState.NotMeasured
                                     && s.Score.Label is not ("error" or "skipped"));
        var notRun = subs.Any(s => !s.Score.CountsTowardAggregate() && s.Score.Label != "error"
                                   && s.Score.CensusBucket() == MeasurementState.NotMeasured);
        var value = measured.Count == 0 ? 0.0 : measured.Average(s => s.Score.Value);

        if (measured.Count == 0)
        {
            return errored ? new(0.0, "error", false, "none", MeasurementState.Measured)
                : withheld ? new(0.0, "warn", false, "none", MeasurementState.NotMeasured)
                : new(0.0, "skipped", false, "none", MeasurementState.Measured);
        }
        if (failing.Count > 0)
            return new(value, "fail", false, SeverityRollup.Max(failing.Select(s => s.Score.Severity).Append("medium")),
                MeasurementState.Measured);
        if (errored)
            return new(value, "error", false, "none", MeasurementState.Measured);
        if (measuredWarn)
        {
            // A warn reports its parts' severity capped at medium, as a composite's warn does (B6c-6).
            var warnSeverity = SeverityRollup.Max(measured.Where(s => s.Score.Label == "warn").Select(s => s.Score.Severity));
            return new(value, "warn", false, warnSeverity is "high" or "critical" ? "medium" : warnSeverity,
                MeasurementState.Measured);
        }
        if (notRun)
            return new(value, "warn", false, "none", MeasurementState.NotMeasured);
        return new(value, "pass", true, "none", MeasurementState.Measured);
    }
}
