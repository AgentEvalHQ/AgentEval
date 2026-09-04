// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// ADR-030 Slice 0.1 (defect D-a). A composite whose every leaf was skipped measured nothing, and
/// "nothing measured" must not render as a pass. Before the fix the <c>Threshold==null</c> verdict
/// path read only severity; <c>SeverityRollup.Max(empty)</c> is <c>"none"</c>, so an all-skipped
/// composite reported <c>label:"pass", passed:true, score:0.0</c> — a green verdict from an
/// instrument that did not run.
/// </summary>
public class CompositeEvalSkippedLeavesTests
{
    private sealed class SkippingEval(string key) : AtomicEval(key, key, "test", "1.0.0")
    {
        public override Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(EvalResult.Skipped(this, $"{key}: input not present"));
    }

    private sealed class FixedEval(string key, double value, bool passed, string severity = "none", string? label = null)
        : AtomicEval(key, key, "test", "1.0.0")
    {
        public override Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                Metric: new(Key, Name, Category, Version),
                Score: new(value, null, label ?? (passed ? "pass" : "fail"), passed, null, severity, null),
                Details: new(null, null, null, null, null),
                Provenance: new("atomic-code", null, null, null, null, 0, false),
                EvaluatedAt: DateTimeOffset.UtcNow));
    }

    private static readonly EvalInput Input = new(Query: "q", Response: "r");

    private static CompositeEval Composite(IReadOnlyList<EvalComponent> components, IAggregationStrategy? aggregation = null, double? threshold = null) =>
        new("composite", "Composite", "test", "1.0.0", components, aggregation ?? WeightedSumAggregation.Instance, threshold);

    [Fact]
    public async Task AllLeavesSkipped_DoesNotReportPass()
    {
        // The §8 acceptance test: three skipped leaves, no threshold. Fails before the fix with
        // label "pass" / passed true.
        var sut = Composite(new EvalComponent[]
        {
            new(new SkippingEval("a")),
            new(new SkippingEval("b")),
            new(new SkippingEval("c")),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal("none", result.Score.Severity);
        Assert.Equal(3, result.Details.SubResults!.Count);
        Assert.NotNull(result.Details.Summary);
        Assert.Contains("skipped", result.Details.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.0)]
    public async Task AllLeavesSkipped_WithThreshold_IsSkipped_NotFailOrPass(double threshold)
    {
        // With a threshold the pre-fix shape was label "fail" (0.0 < 0.5) or "pass" (0.0 >= 0.0):
        // both are verdicts on a measurement that never happened. Either way the honest label is
        // "skipped".
        var sut = Composite(new EvalComponent[] { new(new SkippingEval("a")), new(new SkippingEval("b")) }, threshold: threshold);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(threshold, result.Score.Threshold);
    }

    [Theory]
    [InlineData("Min")]
    [InlineData("WeightedMedian")]
    [InlineData("MajorityVote")]
    [InlineData("CapByWorst")]
    public async Task AllLeavesSkipped_EveryAggregation_IsSkipped(string aggregationName)
    {
        IAggregationStrategy aggregation = aggregationName switch
        {
            "Min"            => MinAggregation.Instance,
            "WeightedMedian" => WeightedMedianAggregation.Instance,
            "MajorityVote"   => MajorityVoteAggregation.Instance,
            "CapByWorst"     => CapByWorstAggregation.Instance,
            _                => throw new ArgumentOutOfRangeException(nameof(aggregationName)),
        };
        var sut = Composite(new EvalComponent[] { new(new SkippingEval("a")), new(new SkippingEval("b")) }, aggregation);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task NestedComposite_AllLeavesSkipped_PropagatesSkippedUpTheTree()
    {
        // A pillar whose every article skipped is itself skipped, and a root whose every pillar
        // skipped is skipped — the lie must not reappear one level up.
        var pillar = Composite(new EvalComponent[] { new(new SkippingEval("a")), new(new SkippingEval("b")) });
        var root = Composite(new EvalComponent[] { new(pillar) });

        var result = await root.EvaluateAsync(Input);

        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal("skipped", result.Details.SubResults![0].Score.Label);
    }

    [Fact]
    public async Task OnlyOptionalLeavesErrored_RestSkipped_IsError_NotPass()
    {
        // Nothing was measured and one leaf errored. The required-error path does not fire (the
        // erroring leaf is optional) and pre-fix this fell through to "pass". The honest label is
        // "error" — an optional judge that could not speak is still the only thing that ran.
        var sut = Composite(new EvalComponent[]
        {
            new(new SkippingEval("a"), Required: true),
            new(new FixedEval("b", 0.0, passed: false, label: "error"), Required: false),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("error", result.Score.Label);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task OneRealLeafAmongSkipped_StillYieldsARealVerdict()
    {
        // Guard: the fix must not widen. A single measured leaf still decides the composite exactly
        // as before, with the skipped siblings excluded from the denominator.
        var sut = Composite(new EvalComponent[]
        {
            new(new SkippingEval("a")),
            new(new FixedEval("b", 0.9, passed: true)),
            new(new SkippingEval("c")),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
        Assert.Equal(0.9, result.Score.Value, precision: 10);
    }

    [Fact]
    public async Task OneRealFailingLeafAmongSkipped_StillFails()
    {
        var sut = Composite(new EvalComponent[]
        {
            new(new SkippingEval("a")),
            new(new FixedEval("b", 0.1, passed: false, severity: "high")),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("fail", result.Score.Label);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task AllLeavesSkipped_ProvenanceIsStillComposite_AndSubResultsAreKept()
    {
        // The composite did run; it is its leaves that skipped. Provenance stays "composite" so the
        // tree is still a composite node in the artifact, and the sub-results carry each reason.
        var sut = Composite(new EvalComponent[] { new(new SkippingEval("a")), new(new SkippingEval("b")) });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("composite", result.Provenance.Type);
        Assert.All(result.Details.SubResults!, s => Assert.Equal("skipped", s.Score.Label));
        Assert.Equal(WeightedSumAggregation.Instance.Name, result.Details.AggregationStrategy);
    }
}
