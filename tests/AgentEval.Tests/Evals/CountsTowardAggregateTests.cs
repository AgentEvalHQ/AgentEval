// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// ADR-030 Slice 1.2. One predicate, five call sites. The five aggregation strategies each carried
/// their own copy of "is this leaf a real quality signal", written at two different times, and one
/// of them had drifted.
/// </summary>
public class CountsTowardAggregateTests
{
    private sealed class StubEval(string key) : IEval
    {
        public string Key => key;
        public string Name => key;
        public string Category => "test";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static EvalResult Leaf(EvalScore score) =>
        new(new("k", "n", "c", "1.0.0"), score, new(null, null, null, null, null),
            new("atomic-code", null, null, null, null, 0, false), DateTimeOffset.UtcNow);

    private static EvalResult Measured(double value, string severity = "none", string label = "pass") =>
        Leaf(new(value, null, label, label == "pass", null, severity, null));

    private static EvalResult Inapplicable() => Leaf(EvalScore.NotApplicable());

    private static EvalComponent Comp() => new(new StubEval("c"), 1.0);

    // ── the predicate itself ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("pass", true)]
    [InlineData("fail", true)]
    [InlineData("warn", true)]
    [InlineData("skipped", false)]
    [InlineData("error", false)]
    [InlineData("inapplicable", false)]
    public void CountsTowardAggregate_ReadsTheLabel(string label, bool expected)
    {
        var score = new EvalScore(0.5, null, label, false, null, "none", null);

        Assert.Equal(expected, score.CountsTowardAggregate());
    }

    [Fact]
    public void CountsTowardAggregate_ReadsTheStateToo_NotJustTheLabel()
    {
        // ADR-030 §4.2 leaves Label deliberately unguarded (historical artifacts round-trip through
        // it), so the predicate must read BOTH operands or a "pass"-labelled non-measurement leaks in.
        var mislabelled = new EvalScore(0.0, null, "pass", false, null, "none", null)
        {
            Measurement = MeasurementState.NotMeasured
        };

        Assert.False(mislabelled.CountsTowardAggregate());
    }

    // ── the acceptance criterion: n/a is not a zero ────────────────────────────────────────────

    [Fact]
    public void InapplicableLeaf_DoesNotEqual_ZeroScoredLeaf()
    {
        // {0.8, 0.8, n/a} is a mean over TWO; {0.8, 0.8, 0.0} is a mean over three. Before this
        // slice the only things that compiled were 0.0 (reads as a genuine zero and averages as
        // one) and Skipped (reads as "not run"). Both are wrong, and 0.0 fails in the UNflattering
        // direction, which makes it easier to defend as conservative. It is not conservative.
        var withNa = new[] { Measured(0.8), Measured(0.8), Inapplicable() };
        var withZero = new[] { Measured(0.8), Measured(0.8), Measured(0.0, label: "fail") };
        var comps = new[] { Comp(), Comp(), Comp() };

        var (naScore, _) = WeightedSumAggregation.Instance.Aggregate(withNa, comps);
        var (zeroScore, _) = WeightedSumAggregation.Instance.Aggregate(withZero, comps);

        Assert.Equal(0.80, naScore, precision: 10);
        Assert.Equal(0.5333333333, zeroScore, precision: 8);
        Assert.NotEqual(naScore, zeroScore, precision: 8);
    }

    [Fact]
    public void EveryStrategy_ExcludesAnInapplicableLeaf()
    {
        var results = new[] { Measured(0.8), Measured(0.6), Inapplicable() };
        var comps = new[] { Comp(), Comp(), Comp() };
        var measuredOnly = new[] { Measured(0.8), Measured(0.6) };
        var measuredComps = new[] { Comp(), Comp() };

        foreach (var strategy in new IAggregationStrategy[]
                 {
                     WeightedSumAggregation.Instance,
                     MinAggregation.Instance,
                     WeightedMedianAggregation.Instance,
                     MajorityVoteAggregation.Instance,
                     CapByWorstAggregation.Instance,
                 })
        {
            var withNa = strategy.Aggregate(results, comps);
            var without = strategy.Aggregate(measuredOnly, measuredComps);

            Assert.Equal(without.Score, withNa.Score, precision: 10);
            Assert.Equal(without.Severity, withNa.Severity);
        }
    }

    // ── the CapByWorst asymmetry, fixed as a side effect ───────────────────────────────────────

    [Fact]
    public void CapByWorst_ErrorLeaf_NoLongerCapsTheComposite()
    {
        // The recorded asymmetry (ADR-030 §3.3): CapByWorst filtered "skipped" but not "error",
        // where the other four filtered both. It was safe only because every "error" leaf in the
        // tree happens to carry severity "none" — nothing enforced that. An infrastructure failure
        // dressed as a critical violation could cap the whole composite at 0.40.
        var results = new[]
        {
            Measured(0.95),
            Leaf(new(0.0, null, "error", false, null, "critical", null)),
        };
        var comps = new[] { Comp(), Comp() };

        var (score, severity) = CapByWorstAggregation.Instance.Aggregate(results, comps);

        Assert.Equal(0.95, score, precision: 10);
        Assert.NotEqual("critical", severity);
    }

    [Fact]
    public void CapByWorst_InapplicableLeaf_DoesNotCap()
    {
        var results = new[] { Measured(0.95), Leaf(EvalScore.NotApplicable("critical")) };
        var comps = new[] { Comp(), Comp() };

        var (score, severity) = CapByWorstAggregation.Instance.Aggregate(results, comps);

        Assert.Equal(0.95, score, precision: 10);
        Assert.NotEqual("critical", severity);
    }

    [Fact]
    public void CapByWorst_RealCriticalFail_StillCaps()
    {
        // The fix must not disarm the cap for the case it exists for.
        var results = new[] { Measured(0.95), Measured(0.10, "critical", "fail") };
        var comps = new[] { Comp(), Comp() };

        var (score, severity) = CapByWorstAggregation.Instance.Aggregate(results, comps);

        Assert.True(score <= 0.40, $"Expected the critical cap to hold, got {score}");
        Assert.Equal("critical", severity);
    }

    // ── the census bucket ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Census_CountsTheThreeBuckets()
    {
        var census = new[]
        {
            Measured(0.8).Score,
            Measured(0.6).Score,
            EvalScore.NotApplicable(),
            EvalResult.Skipped(new StubEval("s"), "budget filter").Score,
        }.Census();

        Assert.Equal(2, census.Measured);
        Assert.Equal(1, census.NotApplicable);
        Assert.Equal(1, census.NotMeasured);
        Assert.Equal(4, census.Total);
    }
}
