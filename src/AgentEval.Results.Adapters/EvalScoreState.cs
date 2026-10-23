// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters;

// One state mapping for native production and legacy conversion.
internal static class EvalScoreState
{
    internal static AefState Of(EvalScore score)
    {
        ArgumentNullException.ThrowIfNull(score);
        return Of(score.Measurement, score.Label, score.Passed);
    }

    // Summary observations retain these exact facts without manufacturing a numeric placeholder.
    internal static AefState Of(MeasurementState measurement, string label, bool passed) => measurement switch
        {
            MeasurementState.NotApplicable => AefState.NotApplicable,
            MeasurementState.NotMeasured => label switch
            {
                "error" => AefState.Error,
                "skipped" => AefState.Skipped,
                _ => AefState.NotMeasured,
            },
            _ => label switch
            {
                "pass" => AefState.Passed,
                "fail" => AefState.Failed,
                "warn" => AefState.Warn,
                "error" => AefState.Error,
                "skipped" => AefState.Skipped,
                "inapplicable" => AefState.NotApplicable,
                "inconclusive" => AefState.Inconclusive,
                "scored" => AefState.Scored,   // measured, with no verdict: a benchmark that publishes no pass mark
                _ => passed ? AefState.Passed : AefState.Failed,
            },
        };
}
