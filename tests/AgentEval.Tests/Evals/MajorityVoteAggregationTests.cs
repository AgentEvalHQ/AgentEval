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
        // Phase-7 Task 7.6 refined the contract: severity rolls up from the
        // WINNING-LABEL voters' actual severities (not all voters, not hard-
        // coded "none"). For three pass votes with severities [none, low, none],
        // the max winning-vote severity is "low" — preserving the "barely
        // passing, watch this" signal that the prior hard-coded "none" lost.
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
        Assert.Equal("low", severity); // pass-winners' max severity is "low"
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

    // ── "error" is neutral (same as "skipped") (17) ────────────────────────────────

    private static EvalResult ErrorResult() => MakeResult(0.0, "none", "error");

    [Fact]
    public void Aggregate_OneError_ErrorContributionOmitted_FromMeanScore()
    {
        // Regression (issue #17): an "error" label never counts as a pass/warn/fail vote (its label matches
        // none of them), so the winning-label logic was always safe — but WITHOUT excluding it, its
        // placeholder score=0 still polluted the returned meanScore: (0.9+0.9+0.0)/3=0.6 instead of 0.9.
        var results = new[]
        {
            MakeResult(0.90, "none", "pass"),
            MakeResult(0.90, "none", "pass"),
            ErrorResult(),
        };
        var components = new[] { Comp(), Comp(), Comp() };

        var (score, severity) = _sut.Aggregate(results, components);

        Assert.Equal(0.90, score, precision: 10);   // error leaf excluded from the mean
        Assert.Equal("none", severity);             // winning label is still "pass"
    }

    [Fact]
    public void Aggregate_AllError_Returns0AndNone()
    {
        var results = new[] { ErrorResult(), ErrorResult() };
        var components = new[] { Comp(), Comp() };

        var (score, severity) = _sut.Aggregate(results, components);

        Assert.Equal(0, score);
        Assert.Equal("none", severity);
    }
}
