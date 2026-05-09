// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

public class CapByWorstAggregationTests
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

    private readonly CapByWorstAggregation _sut = new();

    [Fact]
    public void Aggregate_AllPassing_ReturnsWeightedSumUnchanged()
    {
        // Arrange
        var results = new[]
        {
            MakeResult(0.9, "none", "pass"),
            MakeResult(0.8, "low",  "pass"),
        };
        var components = new[] { Comp(1), Comp(1) };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert
        var (expectedScore, expectedSeverity) = WeightedSumAggregation.Instance.Aggregate(results, components);
        Assert.Equal(expectedScore, score, precision: 10);
        Assert.Equal(expectedSeverity, severity);
    }

    [Fact]
    public void Aggregate_OneCriticalFail_ScoreCappedAt040_SeverityIsCritical()
    {
        // Arrange
        var results = new[]
        {
            MakeResult(0.95, "none",     "pass"),
            MakeResult(0.10, "critical", "fail"),
        };
        var components = new[] { Comp(1), Comp(1) };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert
        Assert.True(score <= 0.40, $"Expected score <= 0.40, got {score}");
        Assert.Equal("critical", severity);
    }

    [Fact]
    public void Aggregate_OneHighFail_NoCritical_ScoreCappedAt069_SeverityIsHigh()
    {
        // Arrange
        var results = new[]
        {
            MakeResult(0.95, "none", "pass"),
            MakeResult(0.10, "high", "fail"),
        };
        var components = new[] { Comp(1), Comp(1) };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert
        Assert.True(score <= 0.69, $"Expected score <= 0.69, got {score}");
        Assert.Equal("high", severity);
    }

    [Fact]
    public void Aggregate_BothCriticalAndHighFails_CriticalCapWins()
    {
        // Arrange — both critical and high fail present; critical cap should dominate
        var results = new[]
        {
            MakeResult(0.95, "none",     "pass"),
            MakeResult(0.30, "critical", "fail"),
            MakeResult(0.20, "high",     "fail"),
        };
        var components = new[] { Comp(1), Comp(1), Comp(1) };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert
        Assert.True(score <= 0.40, $"Expected score <= 0.40 (critical cap wins), got {score}");
        Assert.Equal("critical", severity);
    }

    [Fact]
    public void Aggregate_CriticalPassing_NoCapApplied_ReturnsWeightedSum()
    {
        // Arrange — severity=critical but passed=true; should NOT trigger the cap
        var results = new[]
        {
            MakeResult(0.95, "critical", "pass"),
            MakeResult(0.90, "none",     "pass"),
        };
        var components = new[] { Comp(1), Comp(1) };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert
        var (expectedScore, expectedSeverity) = WeightedSumAggregation.Instance.Aggregate(results, components);
        Assert.Equal(expectedScore, score, precision: 10);
        Assert.Equal(expectedSeverity, severity);
    }

    [Fact]
    public void Aggregate_SkippedResult_DoesNotTriggerCap()
    {
        // Arrange — a skipped result whose severity would be "critical" if it were a
        // real result; skipped results must NOT count as fails for cap purposes.
        // We mix one skipped result and one genuine passing result.
        var results = new[]
        {
            MakeResult(0.90, "none", "pass"),
            SkippedResult(),                   // skipped — should not trigger cap
        };
        var components = new[] { Comp(1), Comp(1) };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert — no cap; weighted-sum delegates to WeightedSumAggregation which
        // also skips skipped entries, so the result should equal the passing entry.
        var (expectedScore, _) = WeightedSumAggregation.Instance.Aggregate(results, components);
        Assert.Equal(expectedScore, score, precision: 10);
        Assert.NotEqual("critical", severity);
        Assert.NotEqual("high", severity);
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
        Assert.IsType<CapByWorstAggregation>(CapByWorstAggregation.Instance);
        Assert.Same(CapByWorstAggregation.Instance, CapByWorstAggregation.Instance);
    }

    [Fact]
    public void Name_IsCapByWorst()
    {
        // Assert
        Assert.Equal("CapByWorst", _sut.Name);
    }
}
