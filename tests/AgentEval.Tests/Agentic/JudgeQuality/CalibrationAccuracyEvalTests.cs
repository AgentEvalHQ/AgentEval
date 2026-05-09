// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.JudgeQuality;
using Xunit;

namespace AgentEval.Tests.Agentic.JudgeQuality;

/// <summary>
/// Unit tests for <see cref="CalibrationAccuracyEval"/>.
/// </summary>
public class CalibrationAccuracyEvalTests
{
    private static EvalInput BuildInput(IEnumerable<(string Expected, string Actual)> pairs) =>
        new(Query: "test", Metadata: new Dictionary<string, object>
        {
            ["calibration_pairs"] = pairs.ToList()
        });

    [Fact]
    public async Task EvaluateAsync_100PercentAccuracy_Passes()
    {
        var eval = new CalibrationAccuracyEval(passThreshold: 0.85);
        var input = BuildInput(new[]
        {
            ("pass", "pass"),
            ("fail", "fail"),
            ("pass", "pass"),
            ("warn", "warn"),
        });

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
        Assert.Equal(1.0, result.Score.Value, precision: 5);
    }

    [Fact]
    public async Task EvaluateAsync_ZeroAccuracy_Fails()
    {
        var eval = new CalibrationAccuracyEval(passThreshold: 0.85);
        var input = BuildInput(new[]
        {
            ("pass", "fail"),
            ("fail", "pass"),
            ("pass", "fail"),
        });

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("fail", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(0.0, result.Score.Value, precision: 5);
    }

    [Fact]
    public async Task EvaluateAsync_50PercentAccuracy_Fails()
    {
        var eval = new CalibrationAccuracyEval(passThreshold: 0.85);
        // 2 of 4 correct
        var input = BuildInput(new[]
        {
            ("pass", "pass"),   // correct
            ("fail", "fail"),   // correct
            ("pass", "fail"),   // wrong
            ("fail", "pass"),   // wrong
        });

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("fail", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(0.5, result.Score.Value, precision: 5);
        // Dimensions should record pair count
        Assert.Equal(4.0, result.Details.Dimensions!["pair_count"], precision: 5);
    }
}
