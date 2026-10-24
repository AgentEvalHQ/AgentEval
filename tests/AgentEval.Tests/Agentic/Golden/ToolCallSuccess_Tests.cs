// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Process;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>tool_call_success</c> evaluator.
/// Key: tool_call_success | Category: agentic-process
/// Deterministic-first: reads structured status fields when available; falls back to LLM.
/// </summary>
public class ToolCallSuccessEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_call_success", new FixedScoreEvaluator(100));

        Assert.Equal("tool_call_success", eval.Key);
        Assert.Equal("Tool Call Success", eval.Name);
        Assert.Equal("agentic-process", eval.Category);
    }

    /// <summary>
    /// Deterministic path: ToolCalls present with status=success fields in JSON results.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_DeterministicPath_AllSuccess_ReportsPass()
    {
        var eval = new ToolCallSuccessEval(new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "Call two tools",
            Response: "Both succeeded.",
            ToolCalls: new[]
            {
                new ToolCall("tool_a", null, "{\"status\":\"success\",\"data\":\"ok\"}"),
                new ToolCall("tool_b", null, "{\"status\":\"success\",\"data\":\"ok\"}"),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"tool_call_success deterministic: expected Passed==true, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    /// <summary>
    /// Deterministic path: ToolCalls present with status=error in JSON result.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_DeterministicPath_HasError_ReportsFail()
    {
        var eval = new ToolCallSuccessEval(new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "Call a tool",
            Response: "Tool failed.",
            ToolCalls: new[]
            {
                new ToolCall("tool_a", null, "{\"status\":\"error\",\"error\":\"timeout\"}"),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"tool_call_success deterministic error path: expected Passed==false, got score={result.Score.Value}");
    }

    /// <summary>
    /// LLM fallback path: the calls' results carry no structured status, so the evaluator delegates to the LLM judge,
    /// which is shown the calls. With stub score=100 the result should pass.
    /// </summary>
    /// <remarks>
    /// These two tests used to reach the fallback with no tool calls at all, so the judge graded a reply with nothing to
    /// check it against (T68). Unstructured results are the fallback's real trigger.
    /// </remarks>
    [Fact]
    public async Task EvaluateAsync_LlmPath_UnstructuredResults_UsesJudge_HighScore_ReportsPass()
    {
        var judge = new RecordingScoreEvaluator(100);
        var eval = new ToolCallSuccessEval(judge);
        var input = new EvalInput(
            Query: "Find flights to Paris",
            Response: "There are three flights to Paris.",
            ToolCalls: new[]
            {
                new ToolCall("search_flights", new Dictionary<string, object> { ["destination"] = "Paris" },
                    "Found 3 flights: AA101, DL205, UA309"),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"tool_call_success LLM fallback: expected Passed==true with stub=100, got score={result.Score.Value}");
        Assert.Contains("search_flights", Assert.Single(judge.Inputs));
    }

    /// <summary>
    /// LLM fallback path: an unstructured error result, low-score stub → result should fail.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_LlmPath_UnstructuredResults_LowScore_ReportsFail()
    {
        var judge = new RecordingScoreEvaluator(10);
        var eval = new ToolCallSuccessEval(judge);
        var input = new EvalInput(
            Query: "Find flights to Paris",
            Response: "Something went wrong.",
            ToolCalls: new[]
            {
                new ToolCall("search_flights", new Dictionary<string, object> { ["destination"] = "Paris" },
                    "HTTP 503 Service Unavailable"),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"tool_call_success LLM fallback: expected Passed==false with stub=10, got score={result.Score.Value}");
        Assert.Contains("search_flights", Assert.Single(judge.Inputs));
    }
}
