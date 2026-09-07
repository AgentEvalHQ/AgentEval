// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;

namespace AgentEval.Tests.Evals;

/// <summary>
/// Task 1.1. The five aggregation strategies read nothing from an <see cref="EvalComponent"/> except
/// its <see cref="EvalComponent.Weight"/> — <c>grep '\.Eval\b|\.Required\b'</c> over
/// <c>src/AgentEval.Core/Evals/Aggregations/</c> returns <b>0</b>, and the only reader of
/// <see cref="EvalComponent.Eval"/> anywhere is <c>CompositeEval.cs:114</c>. Four throwing
/// <c>IEval</c> stubs existed solely to satisfy the <see cref="EvalComponent"/> constructor so a
/// weight could be passed. <c>AggregateWeights</c> removes the reason for them.
/// </summary>
public class AggregateWeightsTests
{
    private static EvalResult Leaf(double value, string label, string severity, bool passed) => new(
        Metric: new("leaf", "Leaf", "test", "1.0.0"),
        Score: new(value, null, label, passed, null, severity, null),
        Details: new(null, null, null, null, null),
        Provenance: new("atomic-code", null, null, null, null, 0.0, false),
        EvaluatedAt: DateTimeOffset.Parse("2026-09-07T00:00:00Z"));

    private static readonly string[] Labels = ["pass", "fail", "skipped"];
    private static readonly string[] Severities = ["none", "low", "medium", "high", "critical"];

    /// <summary>
    /// The transcription of <c>PerformanceBenchmark.CapByWorstAggregate</c> as it stood at
    /// <c>PerformanceBenchmark.cs:717-745</c>, before task 1.1 deleted it. It is reproduced verbatim
    /// (weights inlined at 1.0, which is what perf passed) so that deleting the original is a change
    /// whose behaviour is PROVEN equal rather than assumed equal.
    /// </summary>
    private static (double Score, string Severity) PerfPrivateCopy(IReadOnlyList<EvalResult> results)
    {
        double weightSum = 0, weightedSum = 0;
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Score.Label == "skipped") continue;
            weightSum += 1.0;
            weightedSum += results[i].Score.Value * 1.0;
        }
        var rawScore = weightSum > 0 ? weightedSum / weightSum : 0;

        bool hasCrit = results.Any(r => r.Score.Label != "skipped" && r.Score.Severity == "critical" && !r.Score.Passed);
        bool hasHigh = results.Any(r => r.Score.Label != "skipped" && r.Score.Severity == "high" && !r.Score.Passed);

        if (hasCrit) return (Math.Min(rawScore, 0.40), "critical");
        if (hasHigh) return (Math.Min(rawScore, 0.69), "high");

        var severities = results
            .Where(r => r.Score.Label != "skipped")
            .Select(r => r.Score.Severity)
            .ToList();
        var worstSeverity = severities
            .OrderByDescending(s => s switch { "critical" => 4, "high" => 3, "medium" => 2, "low" => 1, _ => 0 })
            .FirstOrDefault() ?? "none";
        return (rawScore, worstSeverity);
    }

    [Fact]
    public void PerfCapByWorst_PrivateCopy_EqualsCoreStrategy_OnEveryLeafShapePerfProduces()
    {
        // Perf builds exactly three leaves (latency, throughput, cost) at weight 1.0. This walks every
        // shape those three can take: label × severity × passed, cubed. If Core and the private copy
        // ever disagree, this names the exact triple — and that shape would be a DECLARED behaviour
        // change, not a silent one.
        //
        // Differences that exist by construction and are inert on perf's inputs:
        //   · Core skips Weight <= 0 (WeightedSumAggregation.cs:35) — perf's weights are all 1.0.
        //   · Core excludes "error"/"inapplicable" via CountsTowardAggregate (EvalScoreExtensions.cs:39-45)
        //     — perf emits neither: grep '"error"|"inapplicable"' PerformanceBenchmark.cs → 0.
        //   · Core rolls severity through SeverityRollup.Max; the copy sorts ordinally with a "none"
        //     fallback. Same order, different spelling.
        var shapes = (
            from label in Labels
            from severity in Severities
            from passed in new[] { true, false }
            select Leaf(passed ? 0.9 : 0.1, label, severity, passed)).ToList();

        var weights = new[] { 1.0, 1.0, 1.0 };
        var mismatches = new List<string>();

        foreach (var a in shapes)
        {
            foreach (var b in shapes)
            {
                foreach (var c in shapes)
                {
                    var leaves = new[] { a, b, c };
                    var mine = PerfPrivateCopy(leaves);
                    var core = CapByWorstAggregation.AggregateWeights(leaves, weights);

                    if (Math.Abs(mine.Score - core.Score) > 1e-12 || mine.Severity != core.Severity)
                    {
                        mismatches.Add(
                            $"[{a.Score.Label}/{a.Score.Severity}/{a.Score.Passed}] "
                            + $"[{b.Score.Label}/{b.Score.Severity}/{b.Score.Passed}] "
                            + $"[{c.Score.Label}/{c.Score.Severity}/{c.Score.Passed}] "
                            + $"copy={mine} core={core}");
                    }
                }
            }
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {shapes.Count * shapes.Count * shapes.Count} leaf triples disagree. "
            + "Each is a behaviour change that deleting the private copy would ship silently:"
            + Environment.NewLine + string.Join(Environment.NewLine, mismatches.Take(10)));
    }

    [Fact]
    public void TheTranscriptionAndCore_DisagreeOnAnErrorLeaf_WhichIsWhyPerfMustNotEmitOne()
    {
        // THE DISCRIMINATING CONTROL for the equivalence test above, and it is not the one the plan
        // proposed. The plan said: add a component with Weight = 0, Core excludes it, the
        // transcription includes it, the test fails. That ablation CANNOT work — a zero weight
        // contributes 0 to both the numerator and the denominator of a weighted mean, so excluding
        // it is arithmetically invisible. Verified by running it: Core's `weights[i] <= 0` skip was
        // commented out and all four tests stayed green.
        //
        // The real boundary is CountsTowardAggregate. Core excludes "error" and "inapplicable"
        // (EvalScoreExtensions.cs:39-45); the transcription only excludes "skipped". They agree on
        // every shape perf produces precisely BECAUSE perf emits neither — grep '"error"' over
        // PerformanceBenchmark.cs returns 0. This test pins that reason, so the equivalence above is
        // known to hold for a stated cause rather than by luck.
        var good = Leaf(1.0, "pass", "none", passed: true);
        var errored = Leaf(0.0, "error", "none", passed: false);
        var leaves = new[] { good, errored };

        var core = CapByWorstAggregation.AggregateWeights(leaves, [1.0, 1.0]);
        var transcription = PerfPrivateCopy(leaves);

        Assert.Equal(1.0, core.Score, precision: 12);            // Core drops the error leaf entirely
        Assert.Equal(0.5, transcription.Score, precision: 12);   // the copy averages it in at 0.0
        Assert.NotEqual(transcription.Score, core.Score);
    }

    [Fact]
    public void EveryStrategy_AgreesWithItsOwnComponentEntryPoint()
    {
        // AggregateWeights is the body; Aggregate(results, components) forwards to it. This pins that
        // the forwarding is real, so the instance path and the weights path can never drift.
        var leaves = new[]
        {
            Leaf(0.8, "pass", "low", passed: true),
            Leaf(0.2, "fail", "high", passed: false),
            Leaf(0.5, "pass", "medium", passed: true),
        };
        var weights = new[] { 1.0, 2.0, 3.0 };
        var components = weights.Select(w => new EvalComponent(new NoopEval(), Weight: w)).ToArray();

        Assert.Equal(WeightedSumAggregation.Instance.Aggregate(leaves, components), WeightedSumAggregation.AggregateWeights(leaves, weights));
        Assert.Equal(MinAggregation.Instance.Aggregate(leaves, components), MinAggregation.AggregateWeights(leaves, weights));
        Assert.Equal(CapByWorstAggregation.Instance.Aggregate(leaves, components), CapByWorstAggregation.AggregateWeights(leaves, weights));
        Assert.Equal(MajorityVoteAggregation.Instance.Aggregate(leaves, components), MajorityVoteAggregation.AggregateWeights(leaves, weights));
        Assert.Equal(WeightedMedianAggregation.Instance.Aggregate(leaves, components), WeightedMedianAggregation.AggregateWeights(leaves, weights));
    }

    [Fact]
    public void AggregateWeights_RefusesAMisalignedWeightList()
    {
        var leaves = new[] { Leaf(1.0, "pass", "none", passed: true) };

        Assert.Throws<InvalidOperationException>(() => WeightedSumAggregation.AggregateWeights(leaves, [1.0, 1.0]));
    }

    private sealed class NoopEval : IEval
    {
        public string Key => "noop";
        public string Name => "Noop";
        public string Category => "test";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
            => throw new NotSupportedException("Not invoked: this test only needs the component's Weight.");
    }
}
