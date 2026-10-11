// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// <see cref="EvalDetails.Lineage"/>: which of a composite's rules decided its label, and which components (AEF's
/// <c>aggregation.rulePath</c> and <c>decisive</c>, S1 #5a). It describes the verdict; it is never serialised.
/// </summary>
public class CompositeEvalLineageTests
{
    private sealed class Fixed(string key, string label, double value, string severity = "medium") : IEval
    {
        public string Key => key;
        public string Name => key;
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) => Task.FromResult(
            label == "skipped"
                ? EvalResult.Skipped(this, "did not run")
                : new EvalResult(
                    new(key, key, "test", "1.0.0"),
                    new EvalScore(value, null, label, label == "pass", null, label == "pass" ? "none" : severity, null),
                    new(null, null, null, null, null),
                    new("atomic-code", null, null, null, null, 0, false),
                    DateTimeOffset.UtcNow));
    }

    private static readonly EvalInput Input = new("q", "r");

    private static async Task<(string Label, EvalLineage Lineage)> Run(double? threshold, params EvalComponent[] components)
    {
        var result = await new CompositeEval("c", "C", "test", "1.0.0", components, WeightedSumAggregation.Instance, threshold)
            .EvaluateAsync(Input);
        return (result.Score.Label, result.Details.Lineage!);
    }

    [Fact]
    public async Task AScoreAgainstTheThreshold_IsThreshold()
    {
        var (label, lineage) = await Run(0.7, new(new Fixed("a", "pass", 1.0), 0.5), new(new Fixed("b", "fail", 0.2), 0.5));

        Assert.Equal("fail", label);   // 0.6 < 0.7
        Assert.Equal("threshold", lineage.RulePath);
        Assert.Empty(lineage.Decisive);
    }

    [Fact]
    public async Task WithoutAThreshold_TheSeverityDecides()
    {
        var (label, lineage) = await Run(null, new(new Fixed("a", "pass", 1.0), 0.5), new(new Fixed("b", "fail", 0.0, "high"), 0.5));

        Assert.Equal("fail", label);
        Assert.Equal("severity", lineage.RulePath);
    }

    [Fact]
    public async Task ARequiredComponentThatErrored_IsRequiredError_NamingIt()
    {
        var (label, lineage) = await Run(0.5, new(new Fixed("a", "pass", 1.0), 0.5, true), new(new Fixed("judge", "error", 0.0), 0.5, true));

        Assert.Equal("error", label);
        Assert.Equal("required-error", lineage.RulePath);
        Assert.Equal(["judge"], lineage.Decisive);
    }

    [Fact]
    public async Task NothingMeasured_IsNothingMeasured()
    {
        var (label, lineage) = await Run(0.5, new(new Fixed("a", "skipped", 0), 0.5), new(new Fixed("b", "skipped", 0), 0.5));

        Assert.Equal("skipped", label);
        Assert.Equal("nothing-measured", lineage.RulePath);
    }

    [Fact]
    public async Task ARequiredComponentThatDidNotRun_WithholdsThePass_AsRequiredNotMeasured()
    {
        var (label, lineage) = await Run(0.5, new(new Fixed("a", "pass", 1.0), 0.5, true), new(new Fixed("trace", "skipped", 0), 0.5, true));

        Assert.Equal("warn", label);
        Assert.Equal("required-not-measured", lineage.RulePath);
        Assert.Equal(["trace"], lineage.Decisive);
    }

    [Fact]
    public async Task AFailEffect_IsFailureEffect_NamingTheComponent()
    {
        var (label, lineage) = await Run(0.5,
            new(new Fixed("a", "pass", 1.0), 0.9),
            new(new Fixed("safety", "fail", 0.0), 0.1) { OnFailure = ComponentFailureEffect.Fail });

        Assert.Equal("fail", label);   // the average (0.9) clears 0.5; the effect decides
        Assert.Equal("failure-effect", lineage.RulePath);
        Assert.Equal(["safety"], lineage.Decisive);
    }

    [Fact]
    public async Task APassOnAMinorityOfTheComponents_IsUnderCovered()
    {
        var composite = new CompositeEval("c", "C", "test", "1.0.0",
            [new(new Fixed("a", "pass", 1.0), 0.34), new(new Fixed("b", "skipped", 0), 0.33, false), new(new Fixed("d", "skipped", 0), 0.33, false)],
            WeightedSumAggregation.Instance, threshold: 0.5) { MinimumMeasuredShare = 0.5 };

        var result = await composite.EvaluateAsync(Input);

        Assert.Equal("warn", result.Score.Label);
        Assert.Equal("under-covered", result.Details.Lineage!.RulePath);
    }

    [Fact]
    public async Task TheLineage_IsNeverSerialised()
    {
        var result = await new CompositeEval("c", "C", "test", "1.0.0", [new(new Fixed("a", "pass", 1.0), 1.0)],
            WeightedSumAggregation.Instance, threshold: 0.5).EvaluateAsync(Input);

        Assert.NotNull(result.Details.Lineage);
        Assert.DoesNotContain("ineage", JsonSerializer.Serialize(result.Details), StringComparison.Ordinal);
    }
}
