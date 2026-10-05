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

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Constructor_ThresholdNonFinite_ThrowsArgumentOutOfRange(double badThreshold)
    {
        // Phase-4 (Task 4.1) — IsFinite guard parity with sibling evals.
        // NaN comparisons silently fail (any < or > returns false) which would
        // otherwise let NaN bypass the [0,1] range check.
        var judges = new[] { JudgeComp("j1", 0.9) };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MultiJudgeWrapper("k", "n", "c", "1.0.0", judges, WeightedMedianAggregation.Instance, threshold: badThreshold));
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

    // ── Nothing measured is no verdict (#203 review, round 2 M-1) ─────────────────────────────────
    // Every judge errored or skipped: the aggregation returned (0, "none") and both verdict paths read it as a pass,
    // so a panel none of whose judges answered passed its parent.

    [Fact]
    public async Task EveryJudgeErrored_IsError_NotPass()
    {
        var sut = MakeWrapper([JudgeComp("a", 0, label: "error"), JudgeComp("b", 0, label: "error")]);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("error", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Contains("No judge produced a measurement", result.Details.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryJudgeSkipped_WithAThreshold_IsSkipped_NotPass()
    {
        var sut = new MultiJudgeWrapper("k", "n", "c", "1.0.0",
            [JudgeComp("a", 0, label: "skipped"), JudgeComp("b", 0, label: "skipped")],
            WeightedMedianAggregation.Instance, threshold: 0.0);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.NotMeasured, result.Score.CensusBucket());
    }

    [Fact]
    public async Task EveryJudgeInapplicable_IsNotApplicable_AndDoesNotBlockAParent()
    {
        var panel = MakeWrapper([JudgeComp("a", 0, label: "inapplicable"), JudgeComp("b", 0, label: "inapplicable")]);
        var parent = new CompositeEval("p", "P", "test", "1.0.0", new EvalComponent[]
        {
            new(new FixedResultEval("x", MakeResult("x", 1.0))),
            new(panel),
        }, WeightedSumAggregation.Instance);

        var panelResult = await panel.EvaluateAsync(Input);
        var parentResult = await parent.EvaluateAsync(Input);

        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.NotApplicable, panelResult.Score.Measurement);
        Assert.Equal("pass", parentResult.Score.Label);
    }

    [Fact]
    public async Task APanelThatDidNotAnswer_KeepsARequiringParentFromPassing()
    {
        var panel = MakeWrapper([JudgeComp("a", 0, label: "skipped"), JudgeComp("b", 0, label: "skipped")]);
        var parent = new CompositeEval("p", "P", "test", "1.0.0", new EvalComponent[]
        {
            new(new FixedResultEval("x", MakeResult("x", 1.0))),
            new(panel),
        }, WeightedSumAggregation.Instance);

        var result = await parent.EvaluateAsync(Input);

        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task APartlyMeasuredPanel_WithOnlyOptionalJudgesMissing_IsDecidedByTheJudgesThatAnswered()
    {
        var sut = MakeWrapper([JudgeComp("a", 0.9), JudgeComp("b", 0, label: "error") with { Required = false }]);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("pass", result.Score.Label);
    }

    // ── B10g (review round 3 M4): a panel honours each judge's Required ──────────────────────────────────────────

    [Fact]
    public async Task ARequiredJudgeThatErrored_LeavesNoVerdict_NotAPassOnTheOthers()
    {
        // The GDPR/EU AuditGrade panel declares every judge required; with two of three errored it passed on one judge.
        var sut = ThresholdPanel(JudgeComp("j1", 0.95), JudgeComp("j2", 0, label: "error"), JudgeComp("j3", 0, label: "error"));

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("error", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Contains("j2", result.Details.Summary);
    }

    [Fact]
    public async Task ARequiredJudgeThatDidNotRun_WithholdsThePass()
    {
        var sut = ThresholdPanel(JudgeComp("j1", 0.95), JudgeComp("j2", 0.9), JudgeComp("j3", 0, label: "skipped"));

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("warn", result.Score.Label);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.NotMeasured, result.Score.Measurement);
        Assert.Contains("j3", result.Details.Summary);
    }

    [Fact]
    public async Task WithoutAThreshold_ACriticalDissentTheVoteOutweighs_WithholdsThePass()
    {
        // The comment claimed "without a threshold the severity path already fails on high"; a majority vote of two
        // passes outweighed a critical failure and read pass.
        var sut = MakeWrapper(
            [JudgeComp("j1", 1.0), JudgeComp("j2", 1.0), JudgeComp("j3", 0.1, severity: "critical", label: "fail")],
            MajorityVoteAggregation.Instance);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("warn", result.Score.Label);
        Assert.Contains("critical", result.Details.Summary);
    }

    // ── B10n (review round 4 M4 + L11): decided = fails even with every errored required judge at its best ─────────

    [Fact]
    public async Task UnderMajorityVote_AFailureTheMissingJudgesCouldOutvote_IsNotDecided()
    {
        // Two required judges errored; the three that answered vote 2 fail : 1 pass at critical, which the old heuristic
        // (fail, no threshold, high/critical) took as decided. With the two at their best the vote is 3 pass : 2 fail.
        var sut = MakeWrapper(
            [JudgeComp("e1", 0, label: "error"), JudgeComp("e2", 0, label: "error"), JudgeComp("p", 1.0),
             JudgeComp("f1", 0.1, "critical", "fail"), JudgeComp("f2", 0.1, "critical", "fail")],
            MajorityVoteAggregation.Instance);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("error", result.Score.Label);
        Assert.Contains("e1", result.Details.Summary);
    }

    [Theory]
    [InlineData(0.10, "fail")]    // (1.0 + 0.1 + 0.1) / 3 = 0.40 < 0.70: decided, whatever the missing judge said
    [InlineData(0.60, "error")]   // (1.0 + 0.6 + 0.6) / 3 = 0.73: the missing judge could have passed it
    public async Task WithAThreshold_TheJudgesThatAnsweredDecideOnlyWhatTheMissingOneCouldNotLift(double value, string label)
    {
        var sut = new MultiJudgeWrapper("panel", "Panel", "test", "1.0.0",
            [JudgeComp("missing", 0, label: "error"), JudgeComp("a", value, label: "fail"), JudgeComp("b", value, label: "fail")],
            WeightedSumAggregation.Instance, threshold: 0.70);

        Assert.Equal(label, (await sut.EvaluateAsync(Input)).Score.Label);
    }

    // ── B6c-3 (mid-branch review): a pass the panel cannot agree on is withheld ────────────────────────────────────

    private static MultiJudgeWrapper ThresholdPanel(params EvalComponent[] judges) =>
        new("panel", "Panel", "test", "1.0.0", judges, WeightedMedianAggregation.Instance, threshold: 0.80);

    [Fact]
    public async Task ASevereDissentBelowThePanelsBar_WithholdsThePass_AndNamesIt()
    {
        var result = await ThresholdPanel(
            JudgeComp("j1", 0.95), JudgeComp("j2", 0.95), JudgeComp("j3", 0.20, "critical", "fail")).EvaluateAsync(Input);

        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.NotMeasured, result.Score.CensusBucket());
        Assert.Equal("medium", result.Score.Severity);   // a warn means medium; the dissent's severity is named
        Assert.Contains("1 of 3 judges found a critical failure", result.Details.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMildDissent_IsAbsorbedByTheMedian_AndThePassReportsNoSeverity()
    {
        var result = await ThresholdPanel(
            JudgeComp("j1", 0.95), JudgeComp("j2", 0.95), JudgeComp("j3", 0.50, "medium", "fail")).EvaluateAsync(Input);

        Assert.Equal("pass", result.Score.Label);
        Assert.Equal("none", result.Score.Severity);   // the absorbed dissent does not ride along on the pass
    }

    [Fact]
    public async Task AJudgeFailingOnlyItsOwnStricterBar_IsNotADissent()
    {
        // Above the panel's 0.80 bar, below the judge's own: the panel's bar is the configured rule.
        var result = await ThresholdPanel(JudgeComp("j1", 0.85, "high", "fail")).EvaluateAsync(Input);

        Assert.Equal("pass", result.Score.Label);
    }
}
