// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>tool_efficiency</c> evaluator.
/// Key: tool_efficiency | Category: agentic-process | Threshold: 0.80
/// AgentEval-original; no direct Foundry equivalent.
/// </summary>
public class ToolEfficiencyEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_efficiency", new FixedScoreEvaluator(100));

        Assert.Equal("tool_efficiency", eval.Key);
        Assert.Equal("Tool Efficiency", eval.Name);
        Assert.Equal("agentic-process", eval.Category);
    }

    // The run's tool calls, not prose about them (T68): these inputs used to describe the calls in the reply ("Called
    // tool 10 times redundantly.") with no tool calls recorded. The replies below never name the tool, so the judge
    // input carries the name only if the calls reached it.

    private static ToolCall WeatherCall() =>
        new("get_weather", new Dictionary<string, object> { ["city"] = "Paris" }, "{\"tempC\":18,\"sky\":\"sunny\"}");

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var judge = new RecordingScoreEvaluator(100);
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_efficiency", judge);
        var input = new EvalInput(
            Query: "What is the weather in Paris?",
            Response: "It is 18°C and sunny in Paris.",
            ToolCalls: new[] { WeatherCall() });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"tool_efficiency: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
        Assert.Contains("get_weather", Assert.Single(judge.Inputs));
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var judge = new RecordingScoreEvaluator(10);
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_efficiency", judge);
        var input = new EvalInput(
            Query: "What is the weather in Paris?",
            Response: "It is 18°C and sunny in Paris.",
            // The same call with the same arguments, five times over.
            ToolCalls: Enumerable.Range(0, 5).Select(_ => WeatherCall()).ToArray());

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"tool_efficiency: expected Passed==false with stub score=10, got score={result.Score.Value}");
        Assert.Contains("get_weather", Assert.Single(judge.Inputs));
    }
}
