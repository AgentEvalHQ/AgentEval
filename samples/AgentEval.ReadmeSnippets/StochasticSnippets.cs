// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.ReadmeSnippets;

/// <summary>README: stochastic evaluation (assert on the pass rate across repeated runs).</summary>
public static class StochasticSnippets
{
    public static async Task Stochastic(IStochasticRunner stochasticRunner, IEvaluableAgent agent, TestCase testCase)
    {
        // begin-snippet: stochastic
        var result = await stochasticRunner.RunStochasticTestAsync(
            agent, testCase,
            new StochasticOptions(
                Runs: 20,                     // Run 20 times
                SuccessRateThreshold: 0.85)); // 85% of runs must pass

        result.Should()
            .HavePassRateAtLeast(0.85)        // reliability
            .HaveMeanScoreAtLeast(80)         // avg quality
            .HaveStandardDeviationAtMost(10); // consistency
        // end-snippet
    }
}
