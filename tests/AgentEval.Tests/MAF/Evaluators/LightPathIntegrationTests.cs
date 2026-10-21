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

        // Assert — should still return a result (ToolSuccessMetric is not measured when no tool data was recorded)
        Assert.True(result.Metrics.ContainsKey("code_tool_success"));
    }

    [Fact]
    public async Task LightPath_NoToolCallInTheTranscript_ToolSuccessIsNotMeasured()
    {
        // The flat path cannot vouch for the whole turn, so a transcript with no tool call is "not recorded" (ToolUsage
        // null, T10), and ToolSuccessMetric is not measured (T58). It read PASS 100 ("No tools called").
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hi!")]);

        var evaluator = AgentEvalEvaluators.ToolSuccess();
        var result = await evaluator.EvaluateAsync(messages, response);

        var metric = Assert.IsType<NumericMetric>(result.Metrics["code_tool_success"]);
        Assert.Null(metric.Value);
        Assert.True(metric.Interpretation!.Failed);
        Assert.Contains("not measured", metric.Interpretation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LightPath_GivenTheWholeConversation_NoToolCallIsARecordedNone()
    {
        // AgentEvalAgentEvaluator hands the flat evaluators the item's whole conversation and says so; a turn with no
        // tool call is then a measured "none", so an expected tool that was never called is a real failure. Without the
        // marker it was "not recorded" and the selection check measured nothing.
        var messages = new List<ChatMessage> { new(ChatRole.User, "Find me a flight") };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Here is a flight.")]);
        var additionalContext = new EvaluationContext[] { new AgentEvalWholeConversationContext() };

        var result = await AgentEvalEvaluators.Agentic(["search_flights"]).EvaluateAsync(messages, response, additionalContext: additionalContext);

        var metric = Assert.IsType<NumericMetric>(result.Metrics["code_tool_selection"]);
        Assert.NotNull(metric.Value);
        Assert.True(metric.Interpretation!.Failed);
        Assert.DoesNotContain("not measured", metric.Interpretation.Reason, StringComparison.Ordinal);
    }
}
