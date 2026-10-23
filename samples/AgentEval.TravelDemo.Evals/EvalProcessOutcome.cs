// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.TravelDemo.Evals;

/// <summary>Records the displayed verdict using the shared samples' process-exit contract.</summary>
internal static class EvalProcessOutcome
{
    internal static void Record(RunVerdict verdict, string reason)
    {
        switch (verdict)
        {
            case RunVerdict.Pass: break;
            case RunVerdict.Fail: SampleOutcome.Fail(reason); break;
            case RunVerdict.Warn: SampleOutcome.Warn(reason); break;
            case RunVerdict.Incomplete: SampleOutcome.Skip(reason); break;
            default: throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown run verdict.");
        }
    }

    internal static void Record(StochasticVerdict verdict, string reason) => Record(verdict switch
    {
        StochasticVerdict.Pass => RunVerdict.Pass,
        StochasticVerdict.Fail => RunVerdict.Fail,
        StochasticVerdict.Incomplete or StochasticVerdict.NotMeasured => RunVerdict.Incomplete,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown stochastic verdict."),
    }, reason);
}
