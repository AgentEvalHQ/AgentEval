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
        // The skipped siblings stay out of the denominator, so the one measured leaf still sets the score. With
        // MinimumMeasuredShare = 0 it decides the label exactly as it did before that bar existed. The skipped
        // siblings are OPTIONAL: a required one that did not run withholds the pass (the tests below, #203).
        var sut = new CompositeEval("composite", "Composite", "test", "1.0.0", new EvalComponent[]
        {
            new(new SkippingEval("a"), Required: false),
            new(new FixedEval("b", 0.9, passed: true)),
            new(new SkippingEval("c"), Required: false),
        }, WeightedSumAggregation.Instance) { MinimumMeasuredShare = 0 };

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
        Assert.Equal(0.9, result.Score.Value, precision: 10);
    }

    [Fact]
    public async Task OneRealPassingLeafAmongSkipped_IsAWarnUnderTheDefaultBar()
    {
        // One of three measured is below the default half: nothing failed, but the pass is not the composite's.
        var sut = Composite(new EvalComponent[]
        {
            new(new SkippingEval("a")),
            new(new FixedEval("b", 0.9, passed: true)),
            new(new SkippingEval("c")),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
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

    // ── A REQUIRED component that did not run (#203) ──────────────────────────────────────────────
    // Only a required "error" used to block the verdict. A required component that returned
    // EvalResult.Skipped (a required input, trace or telemetry missing) was left out and the composite
    // passed on the rest: q7 in the EvalPort adapter's sample, "exact pass + required judge skipped → pass,
    // Measured 1 of 2". A pass cannot rest on a required component that did not run.

    [Fact]
    public async Task RequiredSkipped_WithAPassingSibling_IsAWarn_NamingTheComponent()
    {
        var sut = new CompositeEval("composite", "Composite", "test", "1.0.0", new EvalComponent[]
        {
            new(new FixedEval("exact", 1.0, passed: true)),
            new(new SkippingEval("judge")),
        }, WeightedSumAggregation.Instance);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(1.0, result.Score.Value, precision: 10); // the score is still the measured part's
        Assert.Contains("could not attest their own pass: judge", result.Details.Summary!, StringComparison.Ordinal);
        Assert.Equal(result.Details.Summary, Assert.Single(result.Details.Recommendations!));
    }

    [Fact]
    public async Task RequiredSkipped_WithAThresholdMet_IsStillAWarn()
    {
        var sut = Composite(new EvalComponent[]
        {
            new(new FixedEval("exact", 1.0, passed: true)),
            new(new SkippingEval("judge")),
        }, threshold: 0.5);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task OptionalSkipped_WithAPassingSibling_StillPasses()
    {
        var sut = Composite(new EvalComponent[]
        {
            new(new FixedEval("exact", 1.0, passed: true)),
            new(new SkippingEval("judge"), Required: false),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
    }

    [Fact]
    public async Task RequiredSkipped_DoesNotSoftenAMeasuredFailure()
    {
        var sut = Composite(new EvalComponent[]
        {
            new(new FixedEval("exact", 0.1, passed: false, severity: "high")),
            new(new SkippingEval("judge")),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("fail", result.Score.Label);
    }

    [Fact]
    public async Task RequiredErrored_StillWinsOverRequiredSkipped()
    {
        var sut = Composite(new EvalComponent[]
        {
            new(new FixedEval("exact", 1.0, passed: true)),
            new(new FixedEval("judge", 0.0, passed: false, label: "error")),
            new(new SkippingEval("telemetry")),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("error", result.Score.Label);
    }

    [Fact]
    public async Task RequiredInapplicable_NeverBlocksThePass()
    {
        // NotApplicable is the CASE not being able to test the thing (ADR-030), not a component that did not run.
        var sut = Composite(new EvalComponent[]
        {
            new(new FixedEval("exact", 1.0, passed: true)),
            new(new FixedEval("judge", 0.0, passed: false, label: "inapplicable")),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
    }

    [Fact]
    public async Task RequiredNestedCompositeThatMeasuredNothing_KeepsTheParentFromPassing()
    {
        var inner = new CompositeEval("inner", "Inner", "test", "1.0.0", new EvalComponent[]
        {
            new(new SkippingEval("a")),
            new(new SkippingEval("b")),
        }, WeightedSumAggregation.Instance);
        var sut = Composite(new EvalComponent[]
        {
            new(new FixedEval("exact", 1.0, passed: true)),
            new(inner),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("skipped", result.Details.SubResults![1].Score.Label);
        Assert.Equal("warn", result.Score.Label);
        Assert.Contains("inner", result.Details.Summary!, StringComparison.Ordinal);
    }

    // ── One level down (found reviewing #203) ─────────────────────────────────────────────────────
    // A nested composite that could not attest its own pass reports warn with severity "none" (nothing failed),
    // so the parent's severity path read it as clean and passed: the warn vanished one level up.

    private static CompositeEval InnerWithARequiredSkip() =>
        new("inner", "Inner", "test", "1.0.0", new EvalComponent[]
        {
            new(new FixedEval("exact", 1.0, passed: true)),
            new(new SkippingEval("judge")),
        }, WeightedSumAggregation.Instance);

    [Theory]
    [InlineData(null)]
    [InlineData(0.5)]
    public async Task RequiredNestedCompositeThatCouldNotAttestItsPass_KeepsTheParentFromPassing(double? threshold)
    {
        var sut = Composite(new EvalComponent[]
        {
            new(new FixedEval("other", 1.0, passed: true)),
            new(InnerWithARequiredSkip()),
        }, threshold: threshold);

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("warn", result.Details.SubResults![1].Score.Label);
        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Contains("inner", result.Details.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoLevelsDown_TheWarnStillReachesTheTop()
    {
        var middle = new CompositeEval("middle", "Middle", "test", "1.0.0", new EvalComponent[]
        {
            new(new FixedEval("m", 1.0, passed: true)),
            new(InnerWithARequiredSkip()),
        }, WeightedSumAggregation.Instance);
        var sut = Composite(new EvalComponent[]
        {
            new(new FixedEval("top", 1.0, passed: true)),
            new(middle),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("warn", result.Score.Label);
    }

    [Fact]
    public async Task OptionalNestedCompositeThatCouldNotAttestItsPass_DoesNotBlock()
    {
        var sut = Composite(new EvalComponent[]
        {
            new(new FixedEval("other", 1.0, passed: true)),
            new(InnerWithARequiredSkip(), Required: false),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task RequiredNestedCompositeWhoseLeavesAreAllInapplicable_DoesNotBlock()
    {
        // Such a composite reports "skipped" today (ADR-030 Slice 1.4(ii) is unbuilt), but it is a corpus finding —
        // the case cannot test any of it — not a component that did not run.
        var inner = new CompositeEval("na_inner", "NA inner", "test", "1.0.0", new EvalComponent[]
        {
            new(new FixedEval("a", 0.0, passed: false, label: "inapplicable")),
            new(new FixedEval("b", 0.0, passed: false, label: "inapplicable")),
        }, WeightedSumAggregation.Instance);
        var sut = Composite(new EvalComponent[]
        {
            new(new FixedEval("other", 1.0, passed: true)),
            new(inner),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("skipped", result.Details.SubResults![1].Score.Label);
        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
    }

    // ── State, not label (review round 2, H-A / M-2) ──────────────────────────────────────────────
    // A nested composite tells its parent it withheld a pass through Score.Measurement (NotMeasured), and that the case
    // cannot test it through NotApplicable — never through the label: a warn is as often a measured soft fail.

    private static CompositeEval MediumSeverityChild() =>
        new("medium_child", "Medium child", "test", "1.0.0", new EvalComponent[]
        {
            new(new FixedEval("ok", 1.0, passed: true)),
            new(new FixedEval("soft_fail", 0.5, passed: false, severity: "medium")),
        }, WeightedSumAggregation.Instance);

    [Fact]
    public async Task NestedComposite_ThatWithheldItsPass_RecordsNotMeasured()
    {
        var result = await InnerWithARequiredSkip().EvaluateAsync(Input);

        Assert.Equal("warn", result.Score.Label);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.NotMeasured, result.Score.Measurement);
    }

    [Fact]
    public async Task NestedMediumSeverityWarn_IsAMeasurement_NotAnAttestationGap()
    {
        var child = await MediumSeverityChild().EvaluateAsync(Input);
        Assert.Equal("warn", child.Score.Label);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.Measured, child.Score.Measurement);

        // Severity path: the parent warns because of the child's medium severity — as before — and says nothing about
        // a component that did not run.
        var severityParent = await Composite(new EvalComponent[]
        {
            new(new FixedEval("other", 1.0, passed: true)),
            new(MediumSeverityChild()),
        }).EvaluateAsync(Input);
        Assert.Equal("warn", severityParent.Score.Label);
        Assert.DoesNotContain("could not attest", severityParent.Details.Summary ?? "", StringComparison.Ordinal);

        // Threshold path: the parent reads the score (1.0 and 0.75 → 0.875 ≥ 0.8) and passes, as before. The first version
        // of the rule turned this into a warn because the child's label was warn.
        var thresholdParent = await Composite(new EvalComponent[]
        {
            new(new FixedEval("other", 1.0, passed: true)),
            new(MediumSeverityChild()),
        }, threshold: 0.8).EvaluateAsync(Input);
        Assert.Equal("pass", thresholdParent.Score.Label);
    }

    [Fact]
    public async Task RequiredAllInapplicableChild_WithAnOptionalComponentThatErrored_DoesNotBlock()
    {
        var child = new CompositeEval("na_child", "NA child", "test", "1.0.0", new EvalComponent[]
        {
            new(new FixedEval("required_na", 0.0, passed: false, label: "inapplicable")),
            new(new FixedEval("optional_err", 0.0, passed: false, label: "error"), Required: false),
        }, WeightedSumAggregation.Instance);
        var sut = Composite(new EvalComponent[]
        {
            new(new FixedEval("other", 1.0, passed: true)),
            new(child),
        });

        var result = await sut.EvaluateAsync(Input);

        var childResult = result.Details.SubResults![1];
        Assert.Equal("skipped", childResult.Score.Label);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.NotApplicable, childResult.Score.Measurement);
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task ReviewRound2_S2_PartlyAndWhollyInapplicableChildren_BothLeaveTheParentAPass()
    {
        // The coverage bar is per level: a child that measured 1 of 5 is its own soft finding (warn, measured), not a
        // component that did not run, so it does not block; a child that can test nothing is inapplicable and does not
        // block either. (Flattened into one level, both would trip the parent's own coverage bar instead.)
        var partly = new CompositeEval("partly", "Partly", "test", "1.0.0", new EvalComponent[]
        {
            new(new FixedEval("p", 1.0, passed: true)),
            new(new FixedEval("n1", 0.0, passed: false, label: "inapplicable")),
            new(new FixedEval("n2", 0.0, passed: false, label: "inapplicable")),
            new(new FixedEval("n3", 0.0, passed: false, label: "inapplicable")),
            new(new FixedEval("n4", 0.0, passed: false, label: "inapplicable")),
        }, WeightedSumAggregation.Instance);
        var wholly = new CompositeEval("wholly", "Wholly", "test", "1.0.0", Enumerable.Range(0, 5)
            .Select(i => new EvalComponent(new FixedEval($"n{i}", 0.0, passed: false, label: "inapplicable")))
            .ToArray(), WeightedSumAggregation.Instance);

        var withPartly = await Composite(new EvalComponent[] { new(new FixedEval("x", 1.0, passed: true)), new(partly) }).EvaluateAsync(Input);
        var withWholly = await Composite(new EvalComponent[] { new(new FixedEval("x", 1.0, passed: true)), new(wholly) }).EvaluateAsync(Input);

        Assert.Equal("warn", withPartly.Details.SubResults![1].Score.Label);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.Measured, withPartly.Details.SubResults![1].Score.Measurement);
        Assert.Equal("pass", withPartly.Score.Label);
        Assert.Equal("pass", withWholly.Score.Label);
    }

    [Fact]
    public async Task ReviewRound2_S3_AnOptionalSkipUnderARequiredInapplicable_DoesNotBlockTheGrandparent()
    {
        var withOptionalSkip = new CompositeEval("c1", "C1", "test", "1.0.0", new EvalComponent[]
        {
            new(new FixedEval("na", 0.0, passed: false, label: "inapplicable")),
            new(new SkippingEval("opt"), Required: false),
        }, WeightedSumAggregation.Instance);
        var withoutIt = new CompositeEval("c2", "C2", "test", "1.0.0", new EvalComponent[]
        {
            new(new FixedEval("na", 0.0, passed: false, label: "inapplicable")),
        }, WeightedSumAggregation.Instance);

        var a = await Composite(new EvalComponent[] { new(new FixedEval("x", 1.0, passed: true)), new(withOptionalSkip) }).EvaluateAsync(Input);
        var b = await Composite(new EvalComponent[] { new(new FixedEval("x", 1.0, passed: true)), new(withoutIt) }).EvaluateAsync(Input);

        Assert.Equal("pass", a.Score.Label);
        Assert.Equal("pass", b.Score.Label);
    }

    [Fact]
    public async Task BothBarsFiring_TheSummaryGivesBothReasons()
    {
        var sut = Composite(new EvalComponent[]
        {
            new(new FixedEval("exact", 1.0, passed: true)),
            new(new SkippingEval("judge")),
            new(new SkippingEval("telemetry"), Required: false),
            new(new SkippingEval("trace"), Required: false),
        });

        var result = await sut.EvaluateAsync(Input);

        Assert.Equal("warn", result.Score.Label);
        Assert.Contains("could not attest their own pass: judge", result.Details.Summary!, StringComparison.Ordinal);
        Assert.Contains("below the 50", result.Details.Summary!, StringComparison.Ordinal);
    }
}
