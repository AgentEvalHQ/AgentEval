// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// AE-08. <see cref="EvalScore"/> guarded <see cref="EvalScore.Value"/>, <see cref="EvalScore.Threshold"/>
/// and <see cref="EvalScore.Confidence"/> against NaN / Infinity in a property <i>initializer</i>, which
/// the constructor runs but a <c>with { Value = double.NaN }</c> copy bypasses — the copy invokes the
/// init accessor directly, and an auto-property's accessor validates nothing. A non-finite score could
/// therefore be manufactured by copying a valid one, and it surfaced only when the artifact failed
/// schema validation downstream (if at all). These tests pin the fix from the copy side; the
/// constructor-path tests in <see cref="EvalScoreTests"/> are untouched.
/// </summary>
public class EvalScoreWithExpressionTests
{
    private static EvalScore Valid()
        => new(Value: 0.5, Ordinal: 0, Label: "warn", Passed: false, Threshold: 0.8, Severity: "medium", Confidence: 0.9);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Score_WithNonFiniteValue_IsRefused(double bad)
    {
        var score = Valid();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => score with { Value = bad });

        Assert.Equal("Value", ex.ParamName);
        Assert.Equal(0.5, score.Value);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Score_WithNonFiniteThreshold_IsRefused(double bad)
    {
        var score = Valid();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => score with { Threshold = bad });

        Assert.Equal("Threshold", ex.ParamName);
        Assert.Equal(0.8, score.Threshold);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Score_WithNonFiniteConfidence_IsRefused(double bad)
    {
        var score = Valid();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => score with { Confidence = bad });

        Assert.Equal("Confidence", ex.ParamName);
        Assert.Equal(0.9, score.Confidence);
    }

    [Fact]
    public void Score_WithNaNValueDressedAsAPass_IsStillRefused()
    {
        // The flattering combination: a NaN score relabelled as a pass in the same copy. The number
        // of members the copy also rewrites must not change the answer.
        var score = Valid();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            score with { Value = double.NaN, Label = "pass", Passed = true });
    }

    [Fact]
    public void Score_WithFiniteValue_IsAllowed_AndOtherMembersAreKept()
    {
        var score = Valid();

        var copy = score with { Value = 0.95 };

        Assert.Equal(0.95, copy.Value);
        Assert.Equal(score.Ordinal, copy.Ordinal);
        Assert.Equal(score.Label, copy.Label);
        Assert.Equal(score.Passed, copy.Passed);
        Assert.Equal(score.Threshold, copy.Threshold);
        Assert.Equal(score.Severity, copy.Severity);
        Assert.Equal(score.Confidence, copy.Confidence);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(1.001)]
    public void Score_WithOutOfRangeFiniteValue_IsAllowed_MatchingTheCtor(double finiteButOutOfRange)
    {
        // The invariant is finiteness, not [0,1]: the constructor path pins that a finite value outside
        // the schema range is accepted (range is a downstream concern), and the copy path mirrors the
        // constructor exactly rather than being stricter than it.
        var copy = Valid() with { Value = finiteButOutOfRange };

        Assert.Equal(finiteButOutOfRange, copy.Value);
    }

    [Fact]
    public void Score_WithNullThresholdAndConfidence_IsAllowed()
    {
        var copy = Valid() with { Threshold = null, Confidence = null };

        Assert.Null(copy.Threshold);
        Assert.Null(copy.Confidence);
        Assert.Equal(0.5, copy.Value);
    }

    [Fact]
    public void Score_WithUnvalidatedMembersOnly_KeepsValidatedMembers()
    {
        var copy = Valid() with { Label = "pass", Passed = true, Severity = "none", Ordinal = 2 };

        Assert.Equal(0.5, copy.Value);
        Assert.Equal(0.8, copy.Threshold);
        Assert.Equal(0.9, copy.Confidence);
        Assert.Equal("pass", copy.Label);
        Assert.True(copy.Passed);
        Assert.Equal("none", copy.Severity);
        Assert.Equal(2, copy.Ordinal);
    }

    [Fact]
    public void Ctor_Paths_AreUnchanged()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvalScore(double.NaN, 0, "fail", false, 0.8, "high", null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvalScore(0.5, 0, "fail", false, double.PositiveInfinity, "high", null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvalScore(0.5, 0, "fail", false, 0.8, "high", double.NegativeInfinity));

        var ok = new EvalScore(1.001, 0, "fail", false, null, "high", null);
        Assert.Equal(1.001, ok.Value);
        Assert.Null(ok.Threshold);
        Assert.Null(ok.Confidence);
    }

    [Fact]
    public void Equality_And_Deconstruct_AreUnchanged()
    {
        var a = Valid();
        var b = Valid();
        var (value, ordinal, label, passed, threshold, severity, confidence) = a;

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a with { Value = 0.6 });
        Assert.Equal((0.5, 0, "warn", false, 0.8, "medium", 0.9), (value, ordinal, label, passed, threshold, severity, confidence));
    }

    [Fact]
    public void JsonRoundTrip_IsUnchanged()
    {
        var score = Valid();

        var json = JsonSerializer.Serialize(score);
        var back = JsonSerializer.Deserialize<EvalScore>(json);

        Assert.Equal(score, back);
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(7, names.Count);
        Assert.All(new[] { "Value", "Ordinal", "Label", "Passed", "Threshold", "Severity", "Confidence" },
            expected => Assert.Contains(expected, names));
        Assert.DoesNotContain(names, n => n.StartsWith('_'));
    }
}
