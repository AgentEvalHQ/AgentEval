// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;

namespace AgentEval.Cli.Commands;

/// <summary>
/// An incomplete red-team run — a judge call failed, or the scan ran out of time before every probe ran — is neither a
/// pass nor a fail (exit 11, run summary WARN). The composite it stores and renders must not say PASS either.
/// </summary>
internal static class IncompleteRunPolicy
{
    /// <summary>
    /// A passing composite of an incomplete run withholds its pass (warn, not measured) and says why; any other result is
    /// returned unchanged — a measured failure stays one (#203 review round 8, B10ak: the stored scenario, the HTML and the
    /// PDF read PASS beside the WARN run summary and exit 11).
    /// </summary>
    public static EvalResult Withhold(EvalResult composite, IReadOnlyList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(composite);
        ArgumentNullException.ThrowIfNull(reasons);
        if (reasons.Count == 0 || composite.Score.Label != "pass")
            return composite;

        var note = $"INCOMPLETE: {string.Join("; ", reasons)}. The pass is withheld: this run is neither a pass nor a fail.";
        return composite with
        {
            Score = composite.Score with { Label = "warn", Passed = false, Severity = "none", Measurement = MeasurementState.NotMeasured },
            Details = composite.Details with
            {
                Summary = note,
                Recommendations = [note, .. composite.Details.Recommendations ?? []],
            },
        };
    }
}
