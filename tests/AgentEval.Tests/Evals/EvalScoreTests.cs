// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// Tests for <see cref="EvalScore"/> — focused on the Phase-4 (Task 4.2)
/// NaN / Infinity guard on <see cref="EvalScore.Value"/>. The v1 JSON schema
/// constrains the score to <c>number, minimum: 0, maximum: 1</c>; non-finite
/// floats serialise as <c>"NaN"</c> / <c>"Infinity"</c> which would fail schema
/// validation deep in the persistence layer. Reject at the ctor instead.
/// </summary>
public class EvalScoreTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Ctor_NonFiniteValue_ThrowsArgumentOutOfRange(double badValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvalScore(
                Value: badValue,
                Ordinal: 0,
                Label: "fail",
                Passed: false,
                Threshold: 0.8,
                Severity: "high",
                Confidence: null));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(-0.001)]   // outside [0,1] but FINITE — that's a downstream concern, ctor accepts
    [InlineData(1.001)]    // ditto: ctor doesn't enforce range, only IsFinite
    public void Ctor_FiniteValue_Accepts(double goodValue)
    {
        var score = new EvalScore(
            Value: goodValue,
            Ordinal: 0,
            Label: "fail",
            Passed: false,
            Threshold: 0.8,
            Severity: "high",
            Confidence: null);

        Assert.Equal(goodValue, score.Value);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Ctor_NonFiniteThreshold_ThrowsArgumentOutOfRange(double badThreshold)
    {
        // GAP-11: a non-null Threshold must be finite too, or it serialises as invalid JSON.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvalScore(0.5, 0, "fail", false, Threshold: badThreshold, Severity: "high", Confidence: null));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Ctor_NonFiniteConfidence_ThrowsArgumentOutOfRange(double badConfidence)
    {
        // GAP-11: a non-null Confidence must be finite too.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvalScore(0.5, 0, "fail", false, Threshold: 0.8, Severity: "high", Confidence: badConfidence));
    }

    [Fact]
    public void Ctor_NullThresholdAndConfidence_Accepts()
    {
        var score = new EvalScore(0.5, 0, "fail", false, Threshold: null, Severity: "high", Confidence: null);
        Assert.Null(score.Threshold);
        Assert.Null(score.Confidence);
    }
}
