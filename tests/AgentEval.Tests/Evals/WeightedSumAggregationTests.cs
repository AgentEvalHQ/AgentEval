// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

public class WeightedSumAggregationTests
{
    private sealed class StubEval(string key) : IEval
    {
        public string Key => key;
        public string Name => key;
        public string Category => "test";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private static EvalResult MakeResult(double value, string severity = "none", string label = "pass") =>
        new(
            Metric: new("k", "n", "c", "1.0.0"),
            Score: new(value, null, label, label == "pass", null, severity, null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static EvalResult SkippedResult() => EvalResult.Skipped(new StubEval("x"), "reason");

    private static EvalComponent Comp(double weight = 1.0) =>
        new(new StubEval("c"), weight);

    private readonly WeightedSumAggregation _sut = new();

    [Fact]
    public void Aggregate_EqualWeights_AllPass_EqualsArithmeticMean()
    {
        var results = new[] { MakeResult(0.8), MakeResult(0.6), MakeResult(1.0) };
        var components = new[] { Comp(1), Comp(1), Comp(1) };

        var (score, _) = _sut.Aggregate(results, components);

        Assert.Equal((0.8 + 0.6 + 1.0) / 3, score, precision: 10);
    }

    [Fact]
    public void Aggregate_UnequalWeights_ReflectsWeighting()
    {
        var results = new[] { MakeResult(1.0), MakeResult(0.0) };
        var components = new[] { Comp(3), Comp(1) };

        var (score, _) = _sut.Aggregate(results, components);

        Assert.Equal(0.75, score, precision: 10);
    }

    [Fact]
    public void Aggregate_OneSkipped_SkippedContributionOmitted()
    {
        var results = new[] { MakeResult(0.5), SkippedResult() };
        var components = new[] { Comp(1), Comp(1) };

        var (score, _) = _sut.Aggregate(results, components);

        Assert.Equal(0.5, score, precision: 10);
    }

    [Fact]
    public void Aggregate_AllSkipped_ScoreIsZero_SeverityIsNone()
    {
        var results = new[] { SkippedResult(), SkippedResult() };
        var components = new[] { Comp(1), Comp(1) };

        var (score, severity) = _sut.Aggregate(results, components);

        Assert.Equal(0, score);
        Assert.Equal("none", severity);
    }

    [Fact]
    public void Aggregate_MismatchedCounts_ThrowsInvalidOperationException()
    {
        var results = new[] { MakeResult(0.5) };
        var components = new[] { Comp(1), Comp(1) };

        Assert.Throws<InvalidOperationException>(() => _sut.Aggregate(results, components));
    }

    [Fact]
    public void Aggregate_ZeroWeightComponent_ContributesNothing()
    {
        var results = new[] { MakeResult(0.8), MakeResult(0.2) };
        var components = new[] { Comp(1), Comp(0) };

        var (score, _) = _sut.Aggregate(results, components);

        Assert.Equal(0.8, score, precision: 10);
    }

    [Fact]
    public void Aggregate_NullResults_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _sut.Aggregate(null!, new[] { Comp(1) }));
    }

    [Fact]
    public void Aggregate_NullComponents_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _sut.Aggregate(new[] { MakeResult(0.5) }, null!));
    }

    [Fact]
    public void Instance_ReturnsSingletonOfCorrectType()
    {
        Assert.IsType<WeightedSumAggregation>(WeightedSumAggregation.Instance);
        Assert.Same(WeightedSumAggregation.Instance, WeightedSumAggregation.Instance);
    }

    [Fact]
    public void Name_IsWeightedSum()
    {
        Assert.Equal("WeightedSum", _sut.Name);
    }

    // ── "error" is neutral (same as "skipped") ────────────────────────────────────

    private static EvalResult ErrorResult() => MakeResult(0.0, "none", "error");

    [Fact]
    public void Aggregate_OneError_ErrorContributionOmitted()
    {
        // An infra-failure leaf (score=0, label="error") must not drag the composite below threshold.
        // Same semantics as "skipped": omit from weighted sum entirely.
        var results = new[] { MakeResult(0.8), ErrorResult() };
        var components = new[] { Comp(1), Comp(1) };

        var (score, _) = _sut.Aggregate(results, components);

        Assert.Equal(0.8, score, precision: 10);   // error leaf not included in denominator
    }

    [Fact]
    public void Aggregate_AllError_ScoreIsZero_SeverityIsNone()
    {
        // When every leaf is an infra failure the composite should not explode: score=0, severity=none.
        var results = new[] { ErrorResult(), ErrorResult() };
        var components = new[] { Comp(1), Comp(1) };

        var (score, severity) = _sut.Aggregate(results, components);

        Assert.Equal(0, score);
        Assert.Equal("none", severity);
    }

    [Fact]
    public void Aggregate_MixOfSkippedAndError_BothOmitted_OnlyRealContributes()
    {
        var results = new[] { MakeResult(0.6), SkippedResult(), ErrorResult() };
        var components = new[] { Comp(1), Comp(1), Comp(1) };

        var (score, _) = _sut.Aggregate(results, components);

        Assert.Equal(0.6, score, precision: 10);   // only the real leaf contributes
    }
}
