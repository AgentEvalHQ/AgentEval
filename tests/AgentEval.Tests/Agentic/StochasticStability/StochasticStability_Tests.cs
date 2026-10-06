// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.StochasticStability;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.StochasticStability;

/// <summary>
/// Golden tests for <c>stochastic_stability</c> evaluator.
/// Key: stochastic_stability | Category: operational | Threshold: 0.80
/// </summary>
public class StochasticStabilityEvalTests
{
    private static EvalResult MakeResult(bool passed, double score, string label = "pass") =>
        new(
            Metric: new("stub", "Stub", "test", "1.0.0"),
            Score: new(score, null, label != "pass" ? label : passed ? "pass" : "fail", passed, 0.70, "none", null),   // the label was ignored
            Details: new(null, null, null, null, null),
            Provenance: new("stub", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static EvalInput MakeInput(IEnumerable<EvalResult> runResults) =>
        new(Query: "stability-test",
            Metadata: new Dictionary<string, object>
            {
                [StochasticStabilityEval.MetadataRunResultsKey] = runResults
            });

    [Fact]
    public async Task ARunThatProducedNoVerdict_IsNotAnUnstableRun()
    {
        // Review round 3 (the B9b class, swept in B10i): an errored or skipped run entered the success rate as a failure
        // and the variance as a 0, so a judge outage read as agent instability.
        var stable = new[] { MakeResult(true, 0.95), MakeResult(true, 0.95), MakeResult(true, 0.95), MakeResult(true, 0.95) };
        var withOutages = stable.Concat([MakeResult(false, 0.0, "error"), MakeResult(false, 0.0, "skipped")]);

        var clean = await new StochasticStabilityEval().EvaluateAsync(MakeInput(stable));
        var result = await new StochasticStabilityEval().EvaluateAsync(MakeInput(withOutages));

        Assert.Equal("pass", clean.Score.Label);
        Assert.Equal(clean.Score.Value, result.Score.Value, 6);       // the outages do not move the measurement
        Assert.Equal("warn", result.Score.Label);                     // but a pass on part of the runs is not a clean pass
        Assert.Equal(2, result.Details.Dimensions!["runs_without_verdict"]);
    }

    [Fact]
    public async Task AWithheldRun_IsNotAFailedRun()
    {
        // Review round 4 L (B10s): the filter read label strings, so a run that withheld its pass (warn, not measured)
        // counted as a failed run at its placeholder score.
        var withheld = MakeResult(false, 0.0, "warn");
        withheld = withheld with { Score = withheld.Score with { Measurement = AgentEval.Evals.Meta.MeasurementState.NotMeasured } };
        var stable = new[] { MakeResult(true, 0.95), MakeResult(true, 0.95), MakeResult(true, 0.95) };

        var result = await new StochasticStabilityEval().EvaluateAsync(MakeInput(stable.Append(withheld)));

        Assert.Equal(1, result.Details.Dimensions!["runs_without_verdict"]);
        Assert.Equal(1.0, result.Details.Dimensions!["success_rate_across_runs"]);
        Assert.Equal("warn", result.Score.Label);   // a pass on part of the runs
    }

    [Theory]
    [InlineData("""{"value":0.0}""")]                                                              // no "passed": no verdict
    [InlineData("""{"score":{"value":0.0,"passed":false,"label":"warn","measurement":"notMeasured"}}""")]
    [InlineData("""{"score":{"value":0.0,"passed":false,"label":"warn","measurement":1}}""")]       // NotApplicable as a number (B10v)
    [InlineData("""{"score":{"value":0.0,"passed":false,"label":"warn","measurement":2}}""")]       // NotMeasured as a number
    [InlineData("""{"score":{"value":0.0,"passed":false,"label":"error"}}""")]
    public async Task AJsonRunWithNoVerdict_IsNotAFailedRun(string unmeasured)
    {
        var json = $"""[{"{"}"value":0.95,"passed":true,"label":"pass"{"}"},{"{"}"value":0.95,"passed":true,"label":"pass"{"}"},{unmeasured}]""";
        var input = new EvalInput(Query: "stability-test",
            Metadata: new Dictionary<string, object> { [StochasticStabilityEval.MetadataRunResultsKey] = json });

        var result = await new StochasticStabilityEval().EvaluateAsync(input);

        Assert.Equal(1, result.Details.Dimensions!["runs_without_verdict"]);
        Assert.Equal(1.0, result.Details.Dimensions!["success_rate_across_runs"]);
    }

    [Fact]
    public async Task AJsonRunInTheDocumentedMinimalForm_IsAVerdict_AndAFailureCounts()
    {
        // Review round 5 M-1 (B10v): the XML doc's minimum is value + passed; B10s dropped such a run as "no verdict" (no
        // label), so two passes and an explicit failure read WARN 1.000 instead of a failure, and three minimal runs SKIPPED.
        var json = """[{"value":0.95,"passed":true},{"value":0.95,"passed":true},{"score":{"value":0.10,"passed":false}}]""";
        var input = new EvalInput(Query: "stability-test",
            Metadata: new Dictionary<string, object> { [StochasticStabilityEval.MetadataRunResultsKey] = json });

        var result = await new StochasticStabilityEval().EvaluateAsync(input);

        Assert.Equal(0, result.Details.Dimensions!["runs_without_verdict"]);
        Assert.Equal(2.0 / 3.0, result.Details.Dimensions!["success_rate_across_runs"], 6);
        Assert.Equal("fail", result.Score.Label);
    }

    [Fact]
    public async Task FewerThanTwoRunsWithAVerdict_IsSkipped()
    {
        var result = await new StochasticStabilityEval().EvaluateAsync(
            MakeInput([MakeResult(true, 0.9), MakeResult(false, 0.0, "error"), MakeResult(false, 0.0, "error")]));

        Assert.Equal("skipped", result.Score.Label);
    }

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("stochastic_stability", new FixedScoreEvaluator(100));

        Assert.Equal("stochastic_stability", eval.Key);
        Assert.Equal("Stochastic Stability", eval.Name);
        Assert.Equal("operational", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_AllRunsPass_ReportsHighStability()
    {
        // 5 runs all passing with similar scores → high stability → should pass
        var eval = new StochasticStabilityEval(passThreshold: 0.80);
        var runResults = Enumerable.Range(0, 5)
            .Select(_ => MakeResult(true, 0.92));
        var input = MakeInput(runResults);

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"stochastic_stability: expected Passed==true for 5 consistent passing runs, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
        // Score should be high — all runs pass, variance near 0, failure mode consistency = 1.0
        Assert.True(result.Score.Value >= 0.90,
            $"Expected stability score >= 0.90 for all-passing runs, got {result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_OscillatingResults_ReportsLowStability()
    {
        // 5 runs alternating pass/fail → low success rate, high variance → should fail
        var eval = new StochasticStabilityEval(passThreshold: 0.80);
        var runResults = new[]
        {
            MakeResult(true,  0.95),
            MakeResult(false, 0.20),
            MakeResult(true,  0.90),
            MakeResult(false, 0.15),
            MakeResult(true,  0.88),
        };
        var input = MakeInput(runResults);

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"stochastic_stability: expected Passed==false for oscillating runs, got score={result.Score.Value}");
        // 3/5 passes → success rate = 0.60; high variance; composite should be low
        Assert.True(result.Score.Value < 0.80,
            $"Expected stability score < 0.80 for oscillating runs, got {result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_FewerThanTwoRuns_ReturnsSkipped()
    {
        // Only 1 run result provided → should return skipped
        var eval = new StochasticStabilityEval(passThreshold: 0.80);
        var input = MakeInput([MakeResult(true, 0.90)]);

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);  // StochasticStabilityEval requires at least 2 run results.
    }
}
