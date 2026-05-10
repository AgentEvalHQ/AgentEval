// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

public class MultiJudgeWrapperTests
{
    // ── Stubs ─────────────────────────────────────────────────────────────────

    /// <summary>A fixed-result stub eval that returns a predetermined <see cref="EvalResult"/>.</summary>
    private sealed class FixedResultEval : AtomicEval
    {
        private readonly EvalResult _result;

        public FixedResultEval(string key, EvalResult result)
            : base(key, key, "test", "1.0.0")
        {
            _result = result;
        }

        public override Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(_result);
    }

    private static EvalResult MakeResult(
        string key,
        double value,
        string severity = "none",
        string label = "pass",
        double cost = 0.0) =>
        new(
            Metric: new(key, key, "test", "1.0.0"),
            Score: new(value, null, label, label == "pass", null, severity, null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-llm", null, null, null, null, cost, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static EvalComponent JudgeComp(string key, double value, string severity = "none",
        string label = "pass", double weight = 1.0, double cost = 0.0)
    {
        var result = MakeResult(key, value, severity, label, cost);
        return new EvalComponent(new FixedResultEval(key, result), weight);
    }

    private static MultiJudgeWrapper MakeWrapper(
        IReadOnlyList<EvalComponent> judges,
        IAggregationStrategy? aggregation = null) =>
        new MultiJudgeWrapper(
            key: "test-multi",
            name: "Test Multi-Judge",
            category: "test",
            version: "1.0.0",
            judges: judges,
            aggregation: aggregation ?? WeightedMedianAggregation.Instance);

    private static readonly EvalInput Input = new(Query: "q", Response: "r");

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_ThreeJudgesAgreePass_CompositeIsPass()
    {
        // Arrange — all 3 judges return pass with high scores
        var judges = new[]
        {
            JudgeComp("j1", 0.90, "none", "pass"),
            JudgeComp("j2", 0.92, "none", "pass"),
            JudgeComp("j3", 0.88, "none", "pass"),
        };
        var sut = MakeWrapper(judges);

        // Act
        var result = await sut.EvaluateAsync(Input);

        // Assert
        Assert.True(result.Score.Passed);
        Assert.Equal("pass", result.Score.Label);
        Assert.Equal("none", result.Score.Severity);
    }

    [Fact]
    public async Task EvaluateAsync_OneJudgeFails_TwoPass_WeightedMedianTracksPass()
    {
        // Arrange — 2 judges pass (score=0.9), 1 fails (score=0.1), equal weights.
        // Sorted: 0.1 (cum=1), 0.9 (cum=2 >= 1.5) → median = 0.9 → severity = max = high
        // The single fail has "high" severity, which rolls up and causes "warn" label.
        var judges = new[]
        {
            JudgeComp("j1", 0.90, "none", "pass"),
            JudgeComp("j2", 0.90, "none", "pass"),
            JudgeComp("j3", 0.10, "high", "fail"),
        };
        var sut = MakeWrapper(judges, WeightedMedianAggregation.Instance);

        // Act
        var result = await sut.EvaluateAsync(Input);

        // Assert — median = 0.9 but severity rollup = "high" → label = "fail"
        Assert.Equal(0.90, result.Score.Value, precision: 10);
        Assert.Equal("high", result.Score.Severity);
        // high severity → label = fail
        Assert.Equal("fail", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_OneCriticalSeverityJudge_SeverityRollsUpToCritical()
    {
        // Arrange — two judges pass with "none" severity, one critical fail
        var judges = new[]
        {
            JudgeComp("j1", 0.95, "none",     "pass"),
            JudgeComp("j2", 0.95, "none",     "pass"),
            JudgeComp("j3", 0.10, "critical", "fail"),
        };
        var sut = MakeWrapper(judges, WeightedMedianAggregation.Instance);

        // Act
        var result = await sut.EvaluateAsync(Input);

        // Assert — severity always rolls up to max
        Assert.Equal("critical", result.Score.Severity);
        Assert.Equal("fail", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_CostRollup_SumOfJudgeCosts()
    {
        // Arrange — judges with costs 0.10, 0.20, 0.30
        var judges = new[]
        {
            JudgeComp("j1", 0.90, "none", "pass", cost: 0.10),
            JudgeComp("j2", 0.90, "none", "pass", cost: 0.20),
            JudgeComp("j3", 0.90, "none", "pass", cost: 0.30),
        };
        var sut = MakeWrapper(judges);

        // Act
        var result = await sut.EvaluateAsync(Input);

        // Assert
        Assert.Equal(0.60, result.Provenance.EstimatedCost, precision: 10);
    }

    [Fact]
    public async Task EvaluateAsync_SubResultsAreIndividualJudgeResults()
    {
        // Arrange
        var judges = new[]
        {
            JudgeComp("j1", 0.90, "none", "pass"),
            JudgeComp("j2", 0.85, "none", "pass"),
        };
        var sut = MakeWrapper(judges);

        // Act
        var result = await sut.EvaluateAsync(Input);

        // Assert — 2 sub-results (one per judge)
        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(2, result.Details.SubResults!.Count);
        Assert.Equal("WeightedMedian", result.Details.AggregationStrategy);
    }

    [Fact]
    public void Constructor_EmptyJudges_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new MultiJudgeWrapper("k", "n", "c", "1.0.0", [], WeightedMedianAggregation.Instance));
    }

    [Fact]
    public void Constructor_NullJudges_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new MultiJudgeWrapper("k", "n", "c", "1.0.0", null!, WeightedMedianAggregation.Instance));
    }

    [Fact]
    public void Constructor_NullAggregation_ThrowsArgumentNullException()
    {
        // Act & Assert
        var judges = new[] { JudgeComp("j1", 0.9) };
        Assert.Throws<ArgumentNullException>(() =>
            new MultiJudgeWrapper("k", "n", "c", "1.0.0", judges, null!));
    }

    [Fact]
    public async Task EvaluateAsync_NullInput_ThrowsArgumentNullException()
    {
        // Arrange
        var judges = new[] { JudgeComp("j1", 0.9) };
        var sut = MakeWrapper(judges);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.EvaluateAsync(null!));
    }

    // ── Threshold parameter (batch-4 surface) ────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_ThresholdSet_ScoreAboveThreshold_LabelIsPass()
    {
        // Arrange — single high-severity judge but score crosses the threshold.
        // With Threshold set, the label is score-vs-threshold, not severity-driven.
        var judges = new[]
        {
            JudgeComp("j1", 0.85, "high", "fail"),
        };
        var sut = new MultiJudgeWrapper(
            "thr", "Threshold", "test", "1.0.0",
            judges,
            WeightedMedianAggregation.Instance,
            threshold: 0.80);

        // Act
        var result = await sut.EvaluateAsync(Input);

        // Assert
        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
    }

    [Fact]
    public async Task EvaluateAsync_ThresholdSet_ScoreBelowThreshold_LabelIsFail()
    {
        // Arrange — low-severity judges but score below threshold → fail.
        var judges = new[]
        {
            JudgeComp("j1", 0.40, "none", "pass"),
            JudgeComp("j2", 0.40, "none", "pass"),
        };
        var sut = new MultiJudgeWrapper(
            "thr", "Threshold", "test", "1.0.0",
            judges,
            WeightedMedianAggregation.Instance,
            threshold: 0.80);

        // Act
        var result = await sut.EvaluateAsync(Input);

        // Assert — threshold-fail short-circuits the severity-driven matrix
        Assert.Equal("fail", result.Score.Label);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public void Constructor_ThresholdOutOfRange_ThrowsArgumentOutOfRange()
    {
        var judges = new[] { JudgeComp("j1", 0.9) };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MultiJudgeWrapper("k", "n", "c", "1.0.0", judges, WeightedMedianAggregation.Instance, threshold: 1.5));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MultiJudgeWrapper("k", "n", "c", "1.0.0", judges, WeightedMedianAggregation.Instance, threshold: -0.1));
    }

    [Fact]
    public async Task EvaluateAsync_AllJudgesCacheHit_ProvenanceCacheHitIsTrue()
    {
        // Arrange — three judges, all cache hits. Need a custom helper that
        // produces sub-results with CacheHit=true; the existing JudgeComp uses
        // false. Build a result directly.
        EvalComponent CacheHitJudge(string key, double value)
        {
            var result = new EvalResult(
                Metric: new(key, key, "test", "1.0.0"),
                Score: new(value, null, "pass", true, null, "none", null),
                Details: new(null, null, null, null, null),
                Provenance: new("atomic-llm", null, null, null, null, 0.0, true /* cache hit */),
                EvaluatedAt: DateTimeOffset.UtcNow);
            return new EvalComponent(new FixedResultEval(key, result), 1.0);
        }

        var judges = new[]
        {
            CacheHitJudge("j1", 0.9),
            CacheHitJudge("j2", 0.9),
        };
        var sut = MakeWrapper(judges);

        // Act
        var result = await sut.EvaluateAsync(Input);

        // Assert — CostRollup propagates AllCacheHits when every sub is a hit
        Assert.True(result.Provenance.CacheHit);
    }

    [Fact]
    public async Task EvaluateAsync_OneCacheMiss_ProvenanceCacheHitIsFalse()
    {
        // Arrange — one cache hit + one cache miss → CacheHit must be false
        EvalComponent Judge(string key, double value, bool cacheHit)
        {
            var result = new EvalResult(
                Metric: new(key, key, "test", "1.0.0"),
                Score: new(value, null, "pass", true, null, "none", null),
                Details: new(null, null, null, null, null),
                Provenance: new("atomic-llm", null, null, null, null, 0.0, cacheHit),
                EvaluatedAt: DateTimeOffset.UtcNow);
            return new EvalComponent(new FixedResultEval(key, result), 1.0);
        }

        var judges = new[] { Judge("j1", 0.9, true), Judge("j2", 0.9, false) };
        var sut = MakeWrapper(judges);

        var result = await sut.EvaluateAsync(Input);

        Assert.False(result.Provenance.CacheHit);
    }
}
