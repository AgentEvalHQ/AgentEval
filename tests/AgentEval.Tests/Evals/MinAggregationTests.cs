// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

public class MinAggregationTests
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

    private readonly MinAggregation _sut = new();

    [Fact]
    public void Aggregate_AllPassing_ReturnsMinScore()
    {
        // Arrange
        var results = new[]
        {
            MakeResult(1.0, "none", "pass"),
            MakeResult(0.6, "low",  "pass"),
            MakeResult(0.8, "none", "pass"),
        };
        var components = new[] { Comp(1), Comp(1), Comp(1) };

        // Act
        var (score, _) = _sut.Aggregate(results, components);

        // Assert
        Assert.Equal(0.6, score, precision: 10);
    }

    [Fact]
    public void Aggregate_MixedPassFail_ReturnsFailingMin()
    {
        // Arrange — the failing result has the lowest score; Min should surface it
        var results = new[]
        {
            MakeResult(0.9, "none", "pass"),
            MakeResult(0.2, "high", "fail"),
            MakeResult(0.8, "none", "pass"),
        };
        var components = new[] { Comp(1), Comp(1), Comp(1) };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert
        Assert.Equal(0.2, score, precision: 10);
        Assert.Equal("high", severity);
    }

    [Fact]
    public void Aggregate_AllSkipped_Returns0AndNone()
    {
        // Arrange
        var results = new[] { SkippedResult(), SkippedResult() };
        var components = new[] { Comp(1), Comp(1) };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert
        Assert.Equal(0, score);
        Assert.Equal("none", severity);
    }

    [Fact]
    public void Aggregate_SeverityRollup_OneHighResult_CompositeIsHigh()
    {
        // Arrange
        var results = new[]
        {
            MakeResult(0.9, "none", "pass"),
            MakeResult(0.7, "high", "pass"),
        };
        var components = new[] { Comp(1), Comp(1) };

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
        var components = new[] { Comp(1), Comp(1) };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _sut.Aggregate(results, components));
    }

    [Fact]
    public void Aggregate_NullResults_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            _sut.Aggregate(null!, new[] { Comp(1) }));
    }

    [Fact]
    public void Aggregate_NullComponents_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            _sut.Aggregate(new[] { MakeResult(0.5) }, null!));
    }

    [Fact]
    public void Instance_ReturnsSingletonOfCorrectType()
    {
        // Assert
        Assert.IsType<MinAggregation>(MinAggregation.Instance);
        Assert.Same(MinAggregation.Instance, MinAggregation.Instance);
    }

    [Fact]
    public void Name_IsMin()
    {
        // Assert
        Assert.Equal("Min", _sut.Name);
    }
}
