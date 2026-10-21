// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>tool_call_accuracy</c> aggregate evaluator.
/// Key: tool_call_accuracy | Category: agentic-process | Threshold: 0.70
/// Composite of 5 process sub-evaluators with configured weights.
/// </summary>
public class ToolCallAccuracyAggregateEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_call_accuracy", new FixedScoreEvaluator(100));

        Assert.Equal("tool_call_accuracy", eval.Key);
        Assert.Equal("Tool Call Accuracy", eval.Name);
        Assert.Equal("agentic-process", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_call_accuracy", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "Use tools correctly",
            Response: "All tools called correctly.",
            ToolCalls: new[]
            {
                new ToolCall("search", null, "{\"status\":\"success\",\"results\":[]}"),
            },
            // The definitions the schema check validates against. Without them it does not run, and since it is
            // required the aggregate cannot pass on the judge alone (#203 review, B3).
            // A real (empty-required) schema: since B6c-12 an empty map is not a schema, and a call against it is not checked.
            ToolDefinitions: new[] { new ToolDefinition("search", "Search", new Dictionary<string, object> { ["type"] = "object" }) });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"tool_call_accuracy: expected Passed==true with stub score=100, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        // The run's tool call, not a reply describing it (T68): this input used to carry no tool calls at all.
        var judge = new RecordingScoreEvaluator(10);
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_call_accuracy", judge);
        var input = new EvalInput(
            Query: "Find flights from NYC to London",
            Response: "Everything failed.",
            ToolCalls: new[]
            {
                new ToolCall("search_flights", new Dictionary<string, object> { ["origin"] = "NYC" },
                    "{\"status\":\"error\",\"error\":\"upstream timeout\"}"),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"tool_call_accuracy: expected Passed==false with stub score=10, got score={result.Score.Value}");
        Assert.Contains(judge.Inputs, i => i.Contains("search_flights", StringComparison.Ordinal));
    }
}
