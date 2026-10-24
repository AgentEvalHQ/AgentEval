// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>task_adherence</c> evaluator.
/// Key: task_adherence | Category: system-outcome | Threshold: 0.70
/// Composite with 5 equal-weight sub-dimensions.
/// </summary>
public class TaskAdherenceEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_adherence", new FixedScoreEvaluator(100));

        Assert.Equal("task_adherence", eval.Key);
        Assert.Equal("Task Adherence", eval.Name);
        Assert.Equal("system-outcome", eval.Category);
    }

    // The agent's instructions: four of the five leaves grade the response against them.
    private const string Instructions = "Answer in one sentence. Never discuss pricing.";

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_adherence", new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "test", Response: "compliant response", SystemMessage: Instructions);

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"task_adherence: expected Passed==true with stub score=100, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_NoSystemMessage_TheRuleLeavesAreNotMeasured_AndAPerfectJudgeCannotPass()
    {
        // Before 1.3.0 the rule, procedural, presentation and authorization judges graded "the system rules specified in
        // the system message" with no system message, and a judge at 100 passed the agent on rules nobody had stated.
        var judge = new RecordingScoreEvaluator(100);
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_adherence", judge);

        var result = await eval.EvaluateAsync(new EvalInput(Query: "test", Response: "compliant response"));

        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.NotMeasured, result.Score.Measurement);
        foreach (var leaf in new[] { "rule_adherence", "procedural_adherence", "presentation_adherence", "authorization_adherence" })
        {
            var sub = Assert.Single(result.Details.SubResults!, s => s.Metric.Key == leaf);
            Assert.Equal("skipped", sub.Score.Label);
            Assert.Contains("system message", sub.Details.Summary!, StringComparison.Ordinal);
        }
        Assert.Equal("pass", Assert.Single(result.Details.SubResults!, s => s.Metric.Key == "goal_adherence").Score.Label);
        Assert.Single(judge.Inputs);   // the goal leaf only
    }

    [Fact]
    public async Task EvaluateAsync_TheRuleLeaves_AreShownTheAgentsInstructions()
    {
        var judge = new RecordingScoreEvaluator(100);
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_adherence", judge);

        await eval.EvaluateAsync(new EvalInput(Query: "test", Response: "compliant response", SystemMessage: Instructions));

        Assert.Equal(5, judge.Inputs.Count);
        Assert.Equal(4, judge.Inputs.Count(i => i.Contains("<agent_instructions>", StringComparison.Ordinal)
                                               && i.Contains("Never discuss pricing", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_adherence", new FixedScoreEvaluator(10));
        var input = new EvalInput(Query: "test", Response: "non-compliant response", SystemMessage: Instructions);

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"task_adherence: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
