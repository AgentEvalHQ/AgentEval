// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Comparison;
using Xunit;

namespace AgentEval.Tests.Comparison;

public class StochasticVerdictDecisionTests
{
    [Theory]
    [InlineData(-1, 0, 0, 0.6, "passedRuns")]
    [InlineData(0, -1, 1, 0.6, "notMeasuredRuns")]
    [InlineData(0, 0, -1, 0.6, "totalRuns")]
    [InlineData(2, 0, 1, 0.6, "passedRuns")]
    [InlineData(0, 2, 1, 0.6, "notMeasuredRuns")]
    [InlineData(1, 1, 1, 0.6, "passedRuns")]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, 0.6, "passedRuns")]
    [InlineData(0, 0, 0, -0.1, "successRateThreshold")]
    [InlineData(0, 0, 0, 1.1, "successRateThreshold")]
    [InlineData(0, 0, 0, double.NaN, "successRateThreshold")]
    [InlineData(0, 0, 0, double.PositiveInfinity, "successRateThreshold")]
    [InlineData(0, 0, 0, double.NegativeInfinity, "successRateThreshold")]
    public void DecideVerdict_InvalidArguments_RejectsInsteadOfReturningAVerdict(
        int passed, int missing, int total, double threshold, string parameter)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            StochasticResult.DecideVerdict(passed, missing, total, threshold));
        Assert.Equal(parameter, error.ParamName);
    }

    [Theory]
    [InlineData(0, 0, 0, 0.6, StochasticVerdict.NotMeasured)]
    [InlineData(0, 5, 5, 0, StochasticVerdict.NotMeasured)]
    [InlineData(3, 2, 5, 0.6, StochasticVerdict.Passed)]
    [InlineData(1, 1, 5, 0.6, StochasticVerdict.Failed)]
    [InlineData(1, 3, 5, 0.6, StochasticVerdict.Undecided)]
    [InlineData(2, 1, 5, 0.6, StochasticVerdict.Undecided)]
    [InlineData(0, 0, 1, 0, StochasticVerdict.Passed)]
    [InlineData(1, 0, 1, 1, StochasticVerdict.Passed)]
    [InlineData(0, 0, 1, 1, StochasticVerdict.Failed)]
    [InlineData(int.MaxValue - 1, 1, int.MaxValue, 1, StochasticVerdict.Undecided)]
    public void DecideVerdict_ValidCounts_UsesAllPlannedRuns(
        int passed, int missing, int total, double threshold, StochasticVerdict expected) =>
        Assert.Equal(expected, StochasticResult.DecideVerdict(passed, missing, total, threshold));
}
