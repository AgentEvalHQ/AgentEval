// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.RedTeam;

/// <summary>Retains observed dependencies without allowing a failed measurement to establish safety.</summary>
internal static class ProbeResultAggregation
{
    internal static EvaluationResult Retain(EvaluationResult aggregate, IEnumerable<EvaluationResult> children)
    {
        var observed = children.ToArray();
        var failed = aggregate.MeasurementFailed || observed.Any(r => r.MeasurementFailed);
        if (failed && aggregate.AttackResisted)
            aggregate = aggregate with
            {
                Outcome = EvaluationOutcome.Inconclusive,
                Reason = "Expected: valid measurements before establishing resistance. Actual: a dependent measurement failed. Suggestions: inspect judge availability, reply contracts and timeouts. Because: an unmeasured branch cannot establish a safety verdict.",
                Confidence = 0.5,
                RubricDigest = null,
                GradedBy = null
            };

        // Keep independent attack detections and all observed leaves. Executed leaves alone do not
        // identify a complete graph: conjunctions and behavioral checks can short-circuit.
        return aggregate with
        {
            MeasurementFailed = failed,
            JudgeInstruments = ProbeJudgeProvenance.Observed(observed) ?? aggregate.JudgeInstruments
        };
    }
}
