// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

public class MajorityVoteAggregationTests
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

    private readonly MajorityVoteAggregation _sut = new();

    [Fact]
    public void Aggregate_ThreePassingVotes_ScoreIsMeanAndSeverityReflectsWinningLabel()
    {
        // Arrange — all three pass; mean = (0.9+0.8+0.85)/3 ≈ 0.85.
        // Pass wins the vote 3-0; severity should reflect the WINNING label
        // (= "none" for pass), not the max severity across all voters. The
        // old behavior collapsed everything to max severity regardless of
        // vote count, which contradicted the documented majority semantics
        // (Opus review I4).
        var results = new[]
        {
            MakeResult(0.90, "none", "pass"),
            MakeResult(0.80, "low",  "pass"),
            MakeResult(0.85, "none", "pass"),
        };
        var components = new[] { Comp(), Comp(), Comp() };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert
        var expectedMean = (0.90 + 0.80 + 0.85) / 3.0;
        Assert.Equal(expectedMean, score, precision: 10);
        Assert.Equal("none", severity); // pass won → severity is "none"
    }

    [Fact]
    public void Aggregate_TwoFailsOnePas_ScoreIsMeanSeverityIsMax()
    {
        // Arrange — 2 fails (high) + 1 pass (none); majority = fail; severity rolls up to high
        var results = new[]
        {
            MakeResult(0.20, "high", "fail"),
            MakeResult(0.25, "high", "fail"),
            MakeResult(0.90, "none", "pass"),
        };
        var components = new[] { Comp(), Comp(), Comp() };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert
        var expectedMean = (0.20 + 0.25 + 0.90) / 3.0;
        Assert.Equal(expectedMean, score, precision: 10);
        Assert.Equal("high", severity); // max across all voting results
    }

    [Fact]
    public void Aggregate_OnePassOneWarnOneFail_TieBreaksToMostSevere()
    {
        // Arrange — three-way tie (1 pass, 1 warn, 1 fail); severity should be highest = critical
        var results = new[]
        {
            MakeResult(0.95, "none",     "pass"),
            MakeResult(0.55, "medium",   "warn"),
            MakeResult(0.10, "critical", "fail"),
        };
        var components = new[] { Comp(), Comp(), Comp() };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert — severity rolls up to critical regardless of who "won"
        Assert.Equal("critical", severity);
        var expectedMean = (0.95 + 0.55 + 0.10) / 3.0;
        Assert.Equal(expectedMean, score, precision: 10);
    }

    [Fact]
    public void Aggregate_AllSkipped_Returns0AndNone()
    {
        // Arrange
        var results = new[] { SkippedResult(), SkippedResult(), SkippedResult() };
        var components = new[] { Comp(), Comp(), Comp() };

        // Act
        var (score, severity) = _sut.Aggregate(results, components);

        // Assert
        Assert.Equal(0, score);
        Assert.Equal("none", severity);
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
    public void Name_IsMajorityVote()
    {
        Assert.Equal("MajorityVote", _sut.Name);
    }

    [Fact]
    public void Instance_ReturnsSingleton()
    {
        Assert.IsType<MajorityVoteAggregation>(MajorityVoteAggregation.Instance);
        Assert.Same(MajorityVoteAggregation.Instance, MajorityVoteAggregation.Instance);
    }
}
