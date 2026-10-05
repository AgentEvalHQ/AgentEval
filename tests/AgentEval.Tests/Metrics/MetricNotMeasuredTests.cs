// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
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
    public void TheMeaiBridge_ReportsNotMeasured_AsInconclusive_NotFailed()
    {
        var meai = ResultConverter.ToMEAI(MetricResult.NotMeasured("llm_faithfulness", "no context"));
        var metric = (NumericMetric)meai.Metrics["llm_faithfulness"];

        Assert.Null(metric.Value);
        Assert.False(metric.Interpretation!.Failed);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation.Rating);
    }

    [Fact]
    public async Task TheQualityPreset_OnTheNativeMafPath_DoesNotFailAnItemWithoutContext()
    {
        // The MAF doc's headline example: AgentEvalEvaluators.Quality(judge).AsAgentEvaluator(chatConfig) failed every
        // item - faithfulness failed at 0 without a context, and the native path never forwarded one.
        var judge = new FakeChatClient();
        var evaluator = AgentEvalEvaluators.Quality(judge).AsAgentEvaluator(new ChatConfiguration(judge));

        var results = await evaluator.EvaluateAsync([new EvalItem("What is the capital of France?", "Paris.")]);

        var faithfulness = results.Items[0].Metrics["llm_faithfulness"];
        Assert.False(faithfulness.Interpretation!.Failed);
        Assert.Contains("not measured", faithfulness.Interpretation.Reason, StringComparison.Ordinal);
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
