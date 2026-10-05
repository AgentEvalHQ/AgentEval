// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;

namespace AgentEval.Cli.Commands;

/// <summary>
/// An incomplete red-team run — a judge call failed, or the scan ran out of time before every probe ran — is never a pass:
/// the composite it stores and renders must not say PASS. It is indeterminate (exit 11, run summary WARN) unless what it
/// did measure already fails it: a measured failure stays one (FAIL, exit 9).
/// </summary>
internal static class IncompleteRunPolicy
{
    /// <summary>
    /// A passing composite of an incomplete run withholds its pass (warn, not measured) and says why; one that already
    /// withheld its own pass gets the reasons too (B10at); any other result is returned unchanged — a measured failure stays one (#203 review round 8, B10ak: the stored scenario, the HTML and the
    /// PDF read PASS beside the WARN run summary and exit 11).
    /// </summary>
    public static EvalResult Withhold(EvalResult composite, IReadOnlyList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(composite);
        ArgumentNullException.ThrowIfNull(reasons);
        // A composite that already withheld its own pass (warn, not measured — e.g. a truncated scan, B10ar) still gets the
        // note: it named only what it saw, not a judge failure in the same run (#203 self-review, B10at).
        if (reasons.Count == 0)
            return composite;

        var note = $"INCOMPLETE: {string.Join("; ", reasons)}. The pass is withheld: this run is neither a pass nor a fail.";
        if (composite.Score.Label != "pass")
        {
            // Any other verdict keeps its label but says the run was incomplete (B10ax: a measured warn kept "✅ Strong
            // security posture" and no word of it while the CLI said "neither a pass nor a fail"); a measured failure
            // stays one, and says so.
            if (composite.Score.Label == "fail")
                note = $"INCOMPLETE: {string.Join("; ", reasons)}. What was measured already fails the run.";
            return composite with
            {
                Details = composite.Details with
                {
                    Summary = composite.Details.Summary is null ? note : $"{note} {composite.Details.Summary}",
                    Recommendations = [note, .. (composite.Details.Recommendations ?? []).Where(r => !r.StartsWith("✅", StringComparison.Ordinal))],
                },
            };
        }
        // Through a variable, as every other non-EvalScore site sets it (MetaLaneArchitectureTests' style rule).
        var withheld = MeasurementState.NotMeasured;
        return composite with
        {
            Score = composite.Score with { Label = "warn", Passed = false, Severity = "none", Measurement = withheld },
            Details = composite.Details with
            {
                Summary = note,
                // Without an all-clear line ("✅ Strong security posture …"), which read beside the withheld note (B10ar).
                Recommendations = [note, .. (composite.Details.Recommendations ?? []).Where(r => !r.StartsWith("✅", StringComparison.Ordinal))],
            },
        };
    }

    /// <summary>
    /// Whether the run is indeterminate — run summary WARN, exit 11. An incomplete run is, unless its composite fails: a
    /// failure it measured stands whatever the unmeasured part would show, so the run reads FAIL and exits 9, as the stored
    /// composite and evidence already read it (#203 review round 9 M2, B10ap: the summary read WARN and the run exited 11
    /// beside a FAIL composite and FAIL evidence). A withheld pass, or a warn the rest could still turn into a fail, stays
    /// indeterminate.
    /// </summary>
    public static bool IsIndeterminate(EvalResult composite, IReadOnlyList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(composite);
        ArgumentNullException.ThrowIfNull(reasons);
        return reasons.Count > 0 && composite.Score.Label != "fail";
    }
}
