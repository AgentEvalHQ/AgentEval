// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Core;
using AgentEval.MAF.Evaluators;

namespace AgentEval.Tests.MAF.Evaluators;

public class ResultConverterTests
{
    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(25, 2.0)]
    [InlineData(50, 3.0)]
    [InlineData(75, 4.0)]
    [InlineData(100, 5.0)]
    public void ToMEAI_ScoreConversion_MapsCorrectly(double agentEvalScore, double expectedMEAIScore)
    {
        var metricResult = MetricResult.Pass("test_metric", agentEvalScore, "test");

        var result = ResultConverter.ToMEAI(metricResult);

        Assert.True(result.Metrics.ContainsKey("test_metric"));
        var numeric = Assert.IsType<NumericMetric>(result.Metrics["test_metric"]);
        Assert.Equal(expectedMEAIScore, numeric.Value!.Value, precision: 1);
    }

    [Fact]
    public void ToMEAI_WithPassingResult_HasNotFailedInterpretation()
    {
        var metricResult = MetricResult.Pass("good_metric", 85, "Looks great");

        var result = ResultConverter.ToMEAI(metricResult);

        var numeric = Assert.IsType<NumericMetric>(result.Metrics["good_metric"]);
        Assert.NotNull(numeric.Interpretation);
        Assert.False(numeric.Interpretation!.Failed);
        Assert.Equal(EvaluationRating.Good, numeric.Interpretation.Rating);
    }

    [Fact]
    public void ToMEAI_WithFailingResult_HasFailedInterpretation()
    {
        var metricResult = MetricResult.Fail("bad_metric", "Below threshold", 30);

        var result = ResultConverter.ToMEAI(metricResult);

        var numeric = Assert.IsType<NumericMetric>(result.Metrics["bad_metric"]);
        Assert.NotNull(numeric.Interpretation);
        Assert.True(numeric.Interpretation!.Failed);
    }

    [Fact]
    public void ToMEAI_PreservesOriginalScoreInReason()
    {
        var metricResult = MetricResult.Pass("test", 92, "Excellent quality");

        var result = ResultConverter.ToMEAI(metricResult);

        var numeric = Assert.IsType<NumericMetric>(result.Metrics["test"]);
        Assert.Contains("92/100", numeric.Interpretation!.Reason);
        Assert.Contains("Excellent quality", numeric.Interpretation.Reason);
    }

    [Fact]
    public void AddToEvaluationResult_MultipleCalls_AddsMultipleMetrics()
    {
        var result = new Microsoft.Extensions.AI.Evaluation.EvaluationResult();

        ResultConverter.AddToEvaluationResult(result, MetricResult.Pass("metric_a", 90));
        ResultConverter.AddToEvaluationResult(result, MetricResult.Pass("metric_b", 75));
        ResultConverter.AddToEvaluationResult(result, MetricResult.Fail("metric_c", "failed", 20));

        Assert.Equal(3, result.Metrics.Count);
        Assert.True(result.Metrics.ContainsKey("metric_a"));
        Assert.True(result.Metrics.ContainsKey("metric_b"));
        Assert.True(result.Metrics.ContainsKey("metric_c"));
    }

    [Theory]
    // Mid-range representative values
    [InlineData(95, EvaluationRating.Exceptional)]
    [InlineData(80, EvaluationRating.Good)]
    [InlineData(55, EvaluationRating.Average)]
    [InlineData(30, EvaluationRating.Poor)]
    [InlineData(10, EvaluationRating.Poor)]   // was Inconclusive — fixed by B6
    // Boundary values (T4 coverage)
    [InlineData(100, EvaluationRating.Exceptional)]
    [InlineData(90,  EvaluationRating.Exceptional)]
    [InlineData(89,  EvaluationRating.Good)]
    [InlineData(75,  EvaluationRating.Good)]
    [InlineData(74,  EvaluationRating.Average)]
    [InlineData(50,  EvaluationRating.Average)]
    [InlineData(49,  EvaluationRating.Poor)]
    [InlineData(25,  EvaluationRating.Poor)]
    [InlineData(24,  EvaluationRating.Poor)]
    [InlineData(0,   EvaluationRating.Poor)]
    public void ToMEAI_RatingMapping_IsCorrect(double score, EvaluationRating expectedRating)
    {
        var metricResult = score >= 70
            ? MetricResult.Pass("test", score)
            : MetricResult.Fail("test", "fail", score);

        var result = ResultConverter.ToMEAI(metricResult);

        var numeric = Assert.IsType<NumericMetric>(result.Metrics["test"]);
        Assert.Equal(expectedRating, numeric.Interpretation!.Rating);
    }
}
