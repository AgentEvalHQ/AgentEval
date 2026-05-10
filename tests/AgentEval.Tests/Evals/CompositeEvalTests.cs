// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

public class CompositeEvalTests
{
    // ── Stub ─────────────────────────────────────────────────────────────────

    private sealed class StubAtomic(string key, double value, string severity = "none", bool passed = true, double cost = 0.001, bool cacheHit = false)
        : AtomicEval(key, key, "test", "1.0.0")
    {
        public override Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                Metric: new(Key, Name, Category, Version),
                Score: new(value, null, passed ? "pass" : "fail", passed, null, severity, null),
                Details: new(null, null, null, null, null),
                Provenance: new("atomic-code", null, null, null, null, cost, cacheHit),
                EvaluatedAt: DateTimeOffset.UtcNow));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EvalInput Input => new(Query: "test");

    private static CompositeEval MakeComposite(
        IReadOnlyList<EvalComponent> components,
        IAggregationStrategy? aggregation = null,
        double? threshold = null) =>
        new(
            key: "composite-test",
            name: "Composite Test",
            category: "quality",
            version: "1.0.0",
            components: components,
            aggregation: aggregation ?? WeightedSumAggregation.Instance,
            threshold: threshold);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_ThreeComponents_WeightedSumScore_SubResultsCountMatchesComponents()
    {
        var components = new EvalComponent[]
        {
            new(new StubAtomic("a", 0.8), Weight: 1),
            new(new StubAtomic("b", 0.6), Weight: 2),
            new(new StubAtomic("c", 1.0), Weight: 1)
        };
        var sut = MakeComposite(components);

        var result = await sut.EvaluateAsync(Input);

        // Weighted sum: (0.8*1 + 0.6*2 + 1.0*1) / 4 = (0.8 + 1.2 + 1.0) / 4 = 3.0/4 = 0.75
        Assert.Equal(0.75, result.Score.Value, precision: 10);
        Assert.Equal(3, result.Details.SubResults!.Count);
    }

    [Fact]
    public async Task EvaluateAsync_OneHighSeveritySub_CompositeSeverityIsHigh()
    {
        var components = new EvalComponent[]
        {
            new(new StubAtomic("a", 0.9, severity: "none",   passed: true)),
            new(new StubAtomic("b", 0.2, severity: "high",   passed: false)),
            new(new StubAtomic("c", 0.8, severity: "medium", passed: false))
        };
        var sut = MakeComposite(components);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("high", result.Score.Severity);
    }

    [Fact]
    public async Task EvaluateAsync_CostRollup_SumsCostsCacheHitOnlyWhenAllHit()
    {
        // All cacheHit=false => AllCacheHits=false; costs sum to 0.003
        var components = new EvalComponent[]
        {
            new(new StubAtomic("a", 1.0, cost: 0.001, cacheHit: false)),
            new(new StubAtomic("b", 1.0, cost: 0.002, cacheHit: false)),
            new(new StubAtomic("c", 1.0, cost: 0.000, cacheHit: false))
        };
        var sut = MakeComposite(components);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal(0.003, result.Provenance.EstimatedCost, precision: 10);
        Assert.False(result.Provenance.CacheHit);
    }

    [Fact]
    public async Task EvaluateAsync_AllCacheHits_CacheHitIsTrue()
    {
        var components = new EvalComponent[]
        {
            new(new StubAtomic("a", 1.0, cost: 0.001, cacheHit: true)),
            new(new StubAtomic("b", 1.0, cost: 0.002, cacheHit: true))
        };
        var sut = MakeComposite(components);

        var result = await sut.EvaluateAsync(Input);

        Assert.True(result.Provenance.CacheHit);
    }

    [Fact]
    public async Task EvaluateAsync_NestedComposite_WorksAsComponent()
    {
        // Inner composite wraps two stubs, outer wraps the inner.
        var innerComponents = new EvalComponent[]
        {
            new(new StubAtomic("x", 0.6)),
            new(new StubAtomic("y", 0.8))
        };
        var inner = new CompositeEval(
            "inner", "Inner", "quality", "1.0.0",
            innerComponents, WeightedSumAggregation.Instance);

        var outerComponents = new EvalComponent[]
        {
            new(inner),
            new(new StubAtomic("z", 1.0))
        };
        var outer = MakeComposite(outerComponents);

        var result = await outer.EvaluateAsync(Input);

        // Inner score = (0.6 + 0.8)/2 = 0.7. Outer = (0.7 + 1.0)/2 = 0.85
        Assert.Equal(0.85, result.Score.Value, precision: 10);
        Assert.Equal("composite", result.Provenance.Type);
        Assert.Equal(2, result.Details.SubResults!.Count);  // outer has 2 direct sub-results
    }

    [Fact]
    public async Task EvaluateAsync_WithThreshold_BelowThreshold_FailedAndLabelFail()
    {
        // Score = (0.6 + 0.7)/2 = 0.65, threshold=0.80 => fail
        var components = new EvalComponent[]
        {
            new(new StubAtomic("a", 0.6)),
            new(new StubAtomic("b", 0.7))
        };
        var sut = MakeComposite(components, threshold: 0.80);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal(0.80, result.Score.Threshold);
        Assert.False(result.Score.Passed);
        Assert.Equal("fail", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_NoThreshold_MediumSeverity_WarnLabel_NotPassed()
    {
        // Threshold-less verdict matrix: severity "medium" => label="warn", passed=false.
        // (high/critical => fail; none/low => pass.)
        var components = new EvalComponent[]
        {
            new(new StubAtomic("a", 0.55, severity: "medium", passed: false))
        };
        var sut = MakeComposite(components);   // no threshold

        var result = await sut.EvaluateAsync(Input);

        Assert.False(result.Score.Passed);
        Assert.Equal("medium", result.Score.Severity);
        Assert.Equal("warn", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_NoThreshold_LowSeverity_PassLabel_Passed()
    {
        // Threshold-less + "low" or "none" severity => pass.
        var components = new EvalComponent[]
        {
            new(new StubAtomic("a", 0.95, severity: "low", passed: true))
        };
        var sut = MakeComposite(components);

        var result = await sut.EvaluateAsync(Input);

        Assert.True(result.Score.Passed);
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_NoThreshold_HighSeverity_FailLabel_NotPassed()
    {
        // No threshold, severity="high" => passed=false, label="fail"
        var components = new EvalComponent[]
        {
            new(new StubAtomic("a", 0.2, severity: "high", passed: false))
        };
        var sut = MakeComposite(components);   // no threshold

        var result = await sut.EvaluateAsync(Input);

        Assert.False(result.Score.Passed);
        Assert.Equal("high", result.Score.Severity);
        Assert.Equal("fail", result.Score.Label);
    }

    [Fact]
    public void Constructor_EmptyComponents_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new CompositeEval(
                "k", "n", "c", "1.0.0",
                components: [],
                aggregation: WeightedSumAggregation.Instance));
    }

    [Fact]
    public async Task EvaluateAsync_NullInput_ThrowsArgumentNullException()
    {
        var components = new EvalComponent[]
        {
            new(new StubAtomic("a", 1.0))
        };
        var sut = MakeComposite(components);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => sut.EvaluateAsync(null!));
    }

    [Fact]
    public async Task EvaluateAsync_AggregationStrategyName_StoredInDetails()
    {
        var components = new EvalComponent[] { new(new StubAtomic("a", 1.0)) };
        var sut = MakeComposite(components, aggregation: WeightedSumAggregation.Instance);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("WeightedSum", result.Details.AggregationStrategy);
    }

    // ── EvalComponent.Required = false suppression (batch-1 surface) ─────────

    [Fact]
    public async Task EvaluateAsync_OptionalFailingComponent_VerdictIsPass()
    {
        // Required pass + optional high-severity fail → verdict label must be
        // "pass" (the optional fail's severity drives Score.Severity for
        // transparency, but does NOT propagate into the verdict-label decision).
        // Per the batch-1 fix in CompositeEval:78-94.
        var components = new EvalComponent[]
        {
            new(new StubAtomic("required-pass", 0.95, severity: "none", passed: true), Weight: 1.0, Required: true),
            new(new StubAtomic("optional-fail", 0.10, severity: "high", passed: false), Weight: 1.0, Required: false),
        };
        var sut = MakeComposite(components);

        var result = await sut.EvaluateAsync(Input);

        // Verdict label reflects required-only severity rollup → pass.
        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
    }

    [Fact]
    public async Task EvaluateAsync_RequiredFailDominates_RegardlessOfOptionalPasses()
    {
        // Required fail + optional pass → required fail's severity propagates
        // to the verdict label.
        var components = new EvalComponent[]
        {
            new(new StubAtomic("required-fail", 0.10, severity: "high", passed: false), Weight: 1.0, Required: true),
            new(new StubAtomic("optional-pass", 0.95, severity: "none", passed: true),  Weight: 1.0, Required: false),
        };
        var sut = MakeComposite(components);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("high", result.Score.Severity);
        Assert.Equal("fail", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_AllOptionalAllFail_VerdictLabelIsPass()
    {
        // No required components, all optional, all failing → verdict label
        // must be "pass" (no required component dragged it down). Score.Severity
        // still reflects the aggregation-level worst-case (for diagnostics),
        // but the consumer-facing verdict is governed by required-component
        // severities only.
        var components = new EvalComponent[]
        {
            new(new StubAtomic("opt-1", 0.10, severity: "critical", passed: false), Weight: 1.0, Required: false),
            new(new StubAtomic("opt-2", 0.10, severity: "high",     passed: false), Weight: 1.0, Required: false),
        };
        var sut = MakeComposite(components);

        var result = await sut.EvaluateAsync(Input);

        // Verdict label = pass because there are no required components that
        // failed; the optional fails' severity does not propagate into the
        // label decision.
        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
    }

    // ── Threshold validation (batch-5 surface) ───────────────────────────────

    [Fact]
    public void Constructor_ThresholdOutOfRange_ThrowsArgumentOutOfRange()
    {
        var components = new EvalComponent[] { new(new StubAtomic("a", 1.0)) };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CompositeEval("k", "n", "c", "1.0.0", components,
                WeightedSumAggregation.Instance, threshold: 1.5));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CompositeEval("k", "n", "c", "1.0.0", components,
                WeightedSumAggregation.Instance, threshold: -0.1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CompositeEval("k", "n", "c", "1.0.0", components,
                WeightedSumAggregation.Instance, threshold: double.NaN));
    }
}
