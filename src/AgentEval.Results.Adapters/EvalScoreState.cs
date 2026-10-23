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
        return score.Measurement switch
        {
            MeasurementState.NotApplicable => AefState.NotApplicable,
            MeasurementState.NotMeasured => score.Label switch
            {
                "error" => AefState.Error,
                "skipped" => AefState.Skipped,
                _ => AefState.NotMeasured,
            },
            _ => score.Label switch
            {
                "pass" => AefState.Passed,
                "fail" => AefState.Failed,
                "warn" => AefState.Warn,
                "error" => AefState.Error,
                "skipped" => AefState.Skipped,
                "inapplicable" => AefState.NotApplicable,
                "inconclusive" => AefState.Inconclusive,
                "scored" => AefState.Scored,   // measured, with no verdict: a benchmark that publishes no pass mark
                _ => score.Passed ? AefState.Passed : AefState.Failed,
            },
        };
    }
}
