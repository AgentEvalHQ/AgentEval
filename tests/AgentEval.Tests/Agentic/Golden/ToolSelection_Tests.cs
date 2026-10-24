// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>tool_selection</c> evaluator.
/// Key: tool_selection | Category: agentic-process | Threshold: 0.70
/// </summary>
public class ToolSelectionEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_selection", new FixedScoreEvaluator(100));

        Assert.Equal("tool_selection", eval.Key);
        Assert.Equal("Tool Selection", eval.Name);
        Assert.Equal("agentic-process", eval.Category);
    }

    // The run's tool calls, not prose about them (T68): these inputs used to be "I called search_flights." with no tool
    // calls, so the judge was shown a claim, never the call. The replies below never name the tool, so the judge input
    // carries the name only if the calls reached it.

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var judge = new RecordingScoreEvaluator(100);
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_selection", judge);
        var input = new EvalInput(
            Query: "Search for flights from Seattle to Paris",
            Response: "I found three flights from Seattle to Paris.",
            ToolCalls: new[]
            {
                new ToolCall("search_flights",
                    new Dictionary<string, object> { ["origin"] = "SEA", ["destination"] = "CDG" },
                    "{\"flights\":[\"AA101\",\"DL205\",\"UA309\"]}"),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"tool_selection: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
        Assert.Contains("search_flights", Assert.Single(judge.Inputs));
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var judge = new RecordingScoreEvaluator(10);
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_selection", judge);
        var input = new EvalInput(
            Query: "Search for flights from Seattle to Paris",
            Response: "I have emailed you about flights.",
            ToolCalls: new[]
            {
                new ToolCall("send_email",
                    new Dictionary<string, object> { ["to"] = "user@example.com", ["subject"] = "Flights" },
                    "{\"sent\":true}"),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"tool_selection: expected Passed==false with stub score=10, got score={result.Score.Value}");
        Assert.Contains("send_email", Assert.Single(judge.Inputs));
    }
}
