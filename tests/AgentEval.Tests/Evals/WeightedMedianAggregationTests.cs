// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

public class WeightedMedianAggregationTests
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
            Provenance: new("atomic-llm", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static EvalResult SkippedResult() => EvalResult.Skipped(new StubEval("x"), "reason");

    private static EvalComponent Comp(double weight = 1.0) =>
        new(new StubEval("c"), weight);

    private readonly WeightedMedianAggregation _sut = new();

    [Fact]
    public void Aggregate_EqualWeights_ThreeScores_MedianIsMiddleValue()
    {
        // Arrange — [0.2, 0.5, 0.9] with equal weights 1.0 each.
        // Sorted: 0.2 (cum=1), 0.5 (cum=2 >= total/2=1.5) → median = 0.5
        var results = new[]
        {
            MakeResult(0.9, "none", "pass"),
            MakeResult(0.2, "none", "pass"),
            MakeResult(0.5, "none", "pass"),
        };
        var components = new[] { Comp(1.0), Comp(1.0), Comp(1.0) };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert
        Assert.Equal(0.5, score, precision: 10);
        Assert.Equal("none", severity);
    }

    [Fact]
    public void Aggregate_UnequalWeights_DominantWeightDeterminesMedian()
    {
        // Arrange — scores [0.2, 0.5, 0.9] with weights [1.0, 5.0, 1.0].
        // Total = 7; halfWeight = 3.5.
        // Sorted: (0.2, w=1, cum=1), (0.5, w=5, cum=6 >= 3.5) → median = 0.5
        var results = new[]
        {
            MakeResult(0.2, "none", "pass"),
            MakeResult(0.5, "none", "pass"),
            MakeResult(0.9, "none", "pass"),
        };
        var components = new[] { Comp(1.0), Comp(5.0), Comp(1.0) };

        // Act
        var (score, _) = _sut.Aggregate(results, components);

        // Assert — the heavy-weight middle value wins
        Assert.Equal(0.5, score, precision: 10);
    }

    [Fact]
    public void Aggregate_TwoHighWeightJudgesAgree_MedianTracksAgreement()
    {
        // Arrange — two judges score 0.9 (weight 3.0 each) and one dissents with 0.1 (weight 1.0).
        // Total = 7; halfWeight = 3.5.
        // Sorted: (0.1, w=1, cum=1), (0.9, w=3, cum=4 >= 3.5) → median = 0.9
        var results = new[]
        {
            MakeResult(0.90, "none", "pass"),
            MakeResult(0.90, "none", "pass"),
            MakeResult(0.10, "high", "fail"),
        };
        var components = new[] { Comp(3.0), Comp(3.0), Comp(1.0) };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert — median tracks the agreeing majority
        Assert.Equal(0.90, score, precision: 10);
        // Severity still rolls up to the max (high) even though median is good
        Assert.Equal("high", severity);
    }

    [Fact]
    public void Aggregate_AllSkipped_Returns0AndNone()
    {
        // Arrange
        var results = new[] { SkippedResult(), SkippedResult() };
        var components = new[] { Comp(1.0), Comp(1.0) };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert
        Assert.Equal(0, score);
        Assert.Equal("none", severity);
    }

    [Fact]
    public void Aggregate_SeverityRollup_AnyHighSub_CompositeIsHigh()
    {
        // Arrange — even though the median score is good, one sub has "high" severity
        var results = new[]
        {
            MakeResult(0.85, "none", "pass"),
            MakeResult(0.90, "high", "pass"),
            MakeResult(0.80, "none", "pass"),
        };
        var components = new[] { Comp(1.0), Comp(1.0), Comp(1.0) };

        // Act
        var (_, severity) = _sut.Aggregate(results, components);

        // Assert
        Assert.Equal("high", severity);
    }

    [Fact]
    public void Aggregate_MismatchedCounts_ThrowsInvalidOperationException()
    {
        // Arrange
        var results = new[] { MakeResult(0.5) };
        var components = new[] { Comp(), Comp() };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _sut.Aggregate(results, components));
    }

    [Fact]
    public void Aggregate_NullResults_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            _sut.Aggregate(null!, new[] { Comp() }));
    }

    [Fact]
    public void Aggregate_NullComponents_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            _sut.Aggregate(new[] { MakeResult(0.5) }, null!));
    }

    [Fact]
    public void Name_IsWeightedMedian()
    {
        Assert.Equal("WeightedMedian", _sut.Name);
    }

    [Fact]
    public void Instance_ReturnsSingleton()
    {
        Assert.IsType<WeightedMedianAggregation>(WeightedMedianAggregation.Instance);
        Assert.Same(WeightedMedianAggregation.Instance, WeightedMedianAggregation.Instance);
    }
}
