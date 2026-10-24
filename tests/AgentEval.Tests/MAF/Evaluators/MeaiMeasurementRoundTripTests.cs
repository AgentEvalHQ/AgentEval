// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Core.Evals.Rendering;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.System;
using AgentEval.Evals.Agentic.Telemetry;
using AgentEval.Evals.Meta;
using AgentEval.MAF.Evaluators;
using AgentEval.Output;
using AgentEval.Samples.Benchmarks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace AgentEval.Tests.MAF.Evaluators;

public sealed class MeaiMeasurementRoundTripTests
{
    [Fact]
    public async Task Build_TaskAdherenceWithoutInstructions_PreservesUnavailableScoreThroughConsumers()
    {
        var (original, metric, tree) = await RoundTrip(new TaskAdherenceEval(new FixedScoreJudge(100)));

        Assert.Equal("warn", original.Score.Label);
        Assert.Null(original.Score.MeasuredValue());
        Assert.Null(metric.Value);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation!.Rating);
        var query = Assert.Single(tree.Details.SubResults!);
        var overall = Assert.Single(query.Details.SubResults!, r => r.Metric.Key.EndsWith(" (overall)", StringComparison.Ordinal));
        foreach (var score in new[] { overall.Score, query.Score, tree.Score })
        {
            Assert.Equal("warn", score.Label);
            Assert.Equal(MeasurementState.NotMeasured, score.CensusBucket());
            Assert.Null(score.MeasuredValue());
            Assert.False(score.Passed);
            Assert.Equal(1, new[] { score }.ToRunStats().Skipped);
        }
        Assert.False(BenchmarkSampleReporting.BuildRunSummary(tree, "roundtrip").Metrics!.ContainsKey("overallScore"));
        var html = Encoding.UTF8.GetString(await new HtmlEvalResultRenderer().RenderAsync(tree,
            new EvalResultRenderOptions(Subject: new SubjectIdentity(SubjectKind.Agent, "offline roundtrip"), IncludeProvenance: false)));
        Assert.Contains("<span class=\"score\">not measured</span>", html);
    }

    [Theory]
    [InlineData(100, "pass", 1d)]
    [InlineData(0, "fail", 0d)]
    public async Task Build_TaskAdherenceWithInstructions_PreservesMeasuredVerdict(int score, string label, double value)
    {
        var (original, metric, tree) = await RoundTrip(new TaskAdherenceEval(new FixedScoreJudge(score)), instructions: true);

        Assert.NotNull(metric.Value);
        Assert.Equal(label, original.Score.Label);
        Assert.Equal(label, tree.Score.Label);
        Assert.Equal(value, tree.Score.MeasuredValue());
        Assert.Equal(original.Score.Passed, tree.Score.Passed);
        Assert.Equal(value, BenchmarkSampleReporting.BuildRunSummary(tree, "roundtrip").Metrics!["overallScore"]);
    }

    [Theory]
    [InlineData(false, "skipped", MeasurementState.NotMeasured)]
    [InlineData(true, "inapplicable", MeasurementState.NotApplicable)]
    public async Task Build_ToolLatencyWithoutObservations_PreservesNeutralLeafCensus(
        bool knownNoTools, string label, MeasurementState state)
    {
        var telemetry = knownNoTools
            ? new AgenticTelemetry(null, null, null, null, null, null, null, null, new Dictionary<string, double>())
            : null;
        var (original, metric, tree) = await RoundTrip(new ToolLatencyEval(telemetry));
        var query = Assert.Single(tree.Details.SubResults!);
        var overall = Assert.Single(query.Details.SubResults!, r => r.Metric.Key.EndsWith(" (overall)", StringComparison.Ordinal));

        Assert.Equal(label, original.Score.Label);
        Assert.Null(metric.Value);
        Assert.Equal(label, overall.Score.Label);
        Assert.Equal(state, overall.Score.CensusBucket());
        Assert.Null(overall.Score.MeasuredValue());
        Assert.Null(tree.Score.MeasuredValue());
        Assert.False(tree.Score.Passed);
    }

    [Theory]
    [InlineData(null, false, "warn", null)]
    [InlineData(5d, true, "warn", null)]
    [InlineData(null, true, "inapplicable", null)]
    [InlineData(null, true, "skipped", null)]
    [InlineData(null, true, "error", null)]
    [InlineData(null, true, "pass", null)]
    [InlineData(3.5d, false, "warn", 0.625d)]
    [InlineData(1d, false, "fail", 0d)]
    public void Build_LegacyMarker_RecoversNumbersOnlyForAvailableMeasurement(
        double? value, bool inconclusive, string label, double? expected)
    {
        var markerValue = label == "fail" ? "0" : "62.5";
        var metric = new NumericMetric("quality", value, $"AgentEval score: {markerValue}/100 ({label}, severity medium)");
        if (inconclusive)
            metric.Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Inconclusive);
        var result = new EvaluationResult();
        result.Metrics[metric.Name] = metric;
        var tree = MeaiToEvalResultBridge.Build("marker", ["q"], new AgentEvaluationResults("local", [result]));
        var leaf = Assert.Single(Assert.Single(tree.Details.SubResults!).Details.SubResults!);

        Assert.Equal(label, leaf.Score.Label);
        Assert.Equal("medium", leaf.Score.Severity);
        Assert.Equal(expected, leaf.Score.MeasuredValue());
        Assert.False(leaf.Score.Passed);
    }

    [Fact]
    public void Build_KnownRootFailureBesideUnavailableExternalMetric_KeepsMeasuredFailure()
    {
        var result = new EvaluationResult();
        result.Metrics["owner (overall)"] = new NumericMetric("owner (overall)", 1,
            "AgentEval score: 0/100 (fail, severity critical)");
        result.Metrics["external"] = new NumericMetric("external", null,
            "AgentEval score: 100/100 (warn, severity none)");

        var tree = MeaiToEvalResultBridge.Build("failure and gap", ["q"], new AgentEvaluationResults("local", [result]));

        Assert.Equal("fail", tree.Score.Label);
        Assert.Equal("critical", tree.Score.Severity);
        Assert.Equal(0d, tree.Score.MeasuredValue());
        Assert.False(tree.Score.Passed);
    }

    private static async Task<(EvalResult Original, NumericMetric Metric, EvalResult Tree)> RoundTrip(IEval eval, bool instructions = false)
    {
        var evaluator = new AgentEvalCompositeEvaluator(eval);
        var messages = new List<ChatMessage>();
        if (instructions) messages.Add(new(ChatRole.System, "Follow the user's request."));
        messages.Add(new(ChatRole.User, "Return a short answer."));
        var result = await evaluator.EvaluateAsync(messages, new ChatResponse(new ChatMessage(ChatRole.Assistant, "A short answer.")));
        var overall = (NumericMetric)Assert.Single(result.Metrics, kv => kv.Key.EndsWith(" (overall)", StringComparison.Ordinal)).Value;
        return (Assert.Single(evaluator.CapturedResults), overall,
            MeaiToEvalResultBridge.Build("roundtrip", ["Return a short answer."], new AgentEvaluationResults("local", [result])));
    }

    private sealed class FixedScoreJudge(int score) : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(string input, string output,
            IEnumerable<string> criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentEval.Core.EvaluationResult { OverallScore = score, Summary = "Offline fixed score." });
    }
}
