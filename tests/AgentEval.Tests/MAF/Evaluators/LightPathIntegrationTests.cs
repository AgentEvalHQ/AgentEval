// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.MAF.Evaluators;

namespace AgentEval.Tests.MAF.Evaluators;

/// <summary>
/// End-to-end integration tests for the light path.
/// These tests verify the full flow: ChatMessage → ConversationExtractor → IMetric → ResultConverter → MEAI EvaluationResult.
/// No live LLM required — uses code-based metrics (ToolSuccessMetric).
/// </summary>
public class LightPathIntegrationTests
{
    [Fact]
    public async Task LightPath_ToolSuccessMetric_AllToolsSucceed_ReturnsMaxScore()
    {
        // Arrange — conversation with successful tool call
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What's the weather in Seattle?"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "get_weather",
                new Dictionary<string, object?> { ["city"] = "Seattle" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "72°F and sunny")]),
        };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant,
            "The weather in Seattle is 72°F and sunny.")]);

        // Act — run ToolSuccessMetric through the light path
        var evaluator = AgentEvalEvaluators.ToolSuccess();
        var result = await evaluator.EvaluateAsync(messages, response);

        // Assert
        Assert.True(result.Metrics.ContainsKey("code_tool_success"));
        var metric = Assert.IsType<NumericMetric>(result.Metrics["code_tool_success"]);
        Assert.Equal(5.0, metric.Value!.Value, precision: 1); // 100/100 → 5.0
        Assert.False(metric.Interpretation!.Failed);
    }

    [Fact]
    public async Task LightPath_CompositeEvaluator_RunsMultipleCodeMetrics()
    {
        // Arrange — conversation with tool calls
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Search and book"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "SearchFlights")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "found")]),
        };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Booked!")]);

        // Act — run Agentic bundle with expected tools
        var evaluator = AgentEvalEvaluators.Agentic(["SearchFlights"]);
        var result = await evaluator.EvaluateAsync(messages, response);

        // Assert — both metrics should be present
        Assert.True(result.Metrics.ContainsKey("code_tool_success"));
        Assert.True(result.Metrics.ContainsKey("code_tool_selection"));
        Assert.Equal(2, result.Metrics.Count);

        // Both should pass — tool was called and succeeded
        foreach (var (name, metric) in result.Metrics)
        {
            var numeric = Assert.IsType<NumericMetric>(metric);
            Assert.False(numeric.Interpretation!.Failed, $"Metric {name} should not have failed");
        }
    }

    [Fact]
    public async Task LightPath_WithAdditionalContext_PassesThroughToMetric()
    {
        // Arrange — use AgentEvalExpectedToolsContext
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Do something"),
        };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Done")]);
        var additionalContext = new Microsoft.Extensions.AI.Evaluation.EvaluationContext[]
        {
            new AgentEvalExpectedToolsContext(["ExpectedTool"]),
        };

        // Act — tool success with no actual tool calls
        var evaluator = AgentEvalEvaluators.ToolSuccess();
        var result = await evaluator.EvaluateAsync(messages, response, additionalContext: additionalContext);

        // Assert — should still return a result (ToolSuccessMetric passes when no tools called)
        Assert.True(result.Metrics.ContainsKey("code_tool_success"));
    }

    [Fact]
    public async Task LightPath_NoToolCalls_ToolSuccessReturnsPass()
    {
        // ToolSuccessMetric returns Pass(100) when no tools are called
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hi!")]);

        var evaluator = AgentEvalEvaluators.ToolSuccess();
        var result = await evaluator.EvaluateAsync(messages, response);

        var metric = Assert.IsType<NumericMetric>(result.Metrics["code_tool_success"]);
        Assert.Equal(5.0, metric.Value!.Value, precision: 1); // 100 → 5.0
        Assert.False(metric.Interpretation!.Failed);
    }
}
