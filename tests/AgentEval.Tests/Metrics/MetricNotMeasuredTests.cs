// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.MAF.Evaluators;
using AgentEval.Metrics.RAG;
using AgentEval.Models;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace AgentEval.Tests.Metrics;

/// <summary>
/// #203 review round 15 (B12i, M3 + H1): the legacy metrics failed at 0 when an input they need — a retrieved context, a
/// reference answer — was not supplied, and <see cref="MetricResult"/> had no state for it. Through MAF that failed
/// every item: the <c>Quality</c> preset's faithfulness had no context on the native path.
/// </summary>
public sealed class MetricNotMeasuredTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMissingOrBlankInput_IsNotMeasured_NotFailed(string? missing)
    {
        var judge = new FakeChatClient();
        var context = new AgentEval.Core.EvaluationContext { Input = "q", Output = "a", Context = missing, GroundTruth = missing };

        foreach (var metric in new IMetric[]
                 {
                     new FaithfulnessMetric(judge), new ContextPrecisionMetric(judge), new ContextRecallMetric(judge),
                     new AnswerCorrectnessMetric(judge),
                 })
        {
            var result = await metric.EvaluateAsync(context);
            Assert.False(result.Measured, metric.Name);
            Assert.False(result.Passed, metric.Name);   // not a pass either
        }
        Assert.Empty(judge.ReceivedMessages);   // no judge call spent on what cannot be measured
    }

    [Fact]
    public void TheMeaiBridge_FailsNotMeasured_AndTheReportReadsItAsSkipped()
    {
        // #203 review round 16 (B12k): MAF has no item state between pass and fail. As failed:false, a not-measured metric
        // passed an item where nothing was measured, and the reverse bridge read its empty value as an error.
        var meai = ResultConverter.ToMEAI(MetricResult.NotMeasured("llm_faithfulness",
            "Faithfulness requires a retrieved context, and none was supplied: not measured."));
        var metric = (NumericMetric)meai.Metrics["llm_faithfulness"];

        Assert.True(metric.Interpretation!.Failed);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation.Rating);
        Assert.Contains("not measured", metric.Interpretation.Reason, StringComparison.Ordinal);

        var report = MeaiToEvalResultBridge.Build("run", ["q"], new AgentEvaluationResults("agenteval", [meai]));
        var leaf = report.Details.SubResults![0].Details.SubResults![0];
        Assert.Equal("skipped", leaf.Score.Label);
        Assert.Equal(MeasurementState.NotMeasured, leaf.Score.CensusBucket());
        Assert.NotEqual("pass", report.Score.Label);
        Assert.NotEqual("error", report.Score.Label);
    }

    [Fact]
    public async Task AnItemWhereNothingWasMeasured_FailsOnTheNativeMafPath()
    {
        var judge = new FakeChatClient();
        var evaluator = AgentEvalEvaluators.Custom(new FaithfulnessMetric(judge), new AnswerCorrectnessMetric(judge))
            .AsAgentEvaluator(new ChatConfiguration(judge));

        var results = await evaluator.EvaluateAsync([new EvalItem("What is the capital of France?", "Paris.")]);

        Assert.False(results.AllPassed);
        Assert.All(results.Items[0].Metrics.Values,
            m => Assert.Contains("not measured", m.Interpretation!.Reason, StringComparison.Ordinal));
        Assert.Empty(judge.ReceivedMessages);
    }

    [Fact]
    public async Task TheQualityPreset_GradesTheTextAlone()
    {
        // The MAF doc's headline example: AgentEvalEvaluators.Quality(judge).AsAgentEvaluator(chatConfig). Its faithfulness
        // needs a retrieved context that agent.EvaluateAsync's text path cannot pass, so it failed (or, under B12i,
        // passed unmeasured) every item. It stays in RAG / Advanced / Faithfulness().
        var judge = new FakeChatClient();
        var evaluator = AgentEvalEvaluators.Quality(judge).AsAgentEvaluator(new ChatConfiguration(judge));

        var results = await evaluator.EvaluateAsync([new EvalItem("What is the capital of France?", "Paris.")]);

        Assert.Equal(["llm_coherence", "llm_fluency", "llm_relevance"], results.Items[0].Metrics.Keys.Order());
    }

    [Fact]
    public void AReport_LeavesANotMeasuredPlaceholderOutOfTheScores()
    {
        var summary = new TestSummary("suite",
        [
            new TestResult
            {
                TestName = "t",
                Passed = true,
                MetricResults = [MetricResult.Pass("llm_relevance", 90), MetricResult.NotMeasured("llm_faithfulness", "no context")],
            },
        ]);

        var scores = summary.ToEvaluationReport().TestResults[0].MetricScores;

        Assert.Contains("llm_relevance", scores.Keys);
        Assert.DoesNotContain("llm_faithfulness", scores.Keys);
    }
}
