// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;

namespace AgentEval.Assertions;

/// <summary>
/// Extension methods for stochastic evaluation.
/// </summary>
public static class StochasticExtensions
{
    /// <summary>
    /// Start fluent assertions on a stochastic result.
    /// </summary>
    /// <param name="result">The stochastic result to assert on.</param>
    /// <returns>Fluent assertions for the result.</returns>
    public static StochasticAssertions Should(this StochasticResult result)
    {
        return new StochasticAssertions(result);
    }
}
