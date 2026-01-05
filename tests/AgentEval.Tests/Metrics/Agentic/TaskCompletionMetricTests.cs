// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for TaskCompletionMetric - an LLM-based agentic metric.
/// </summary>
public class TaskCompletionMetricTests
{
    [Fact]
    public async Task EvaluateAsync_TaskCompleted_ReturnsPassingScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 95,
                "criteriaResults": [
                    {"criterion": "The response addresses the user's request", "met": true, "reason": "Yes, fully addressed"},
                    {"criterion": "The output is complete and actionable", "met": true, "reason": "Complete"}
                ],
                "reasoning": "Task was successfully completed."
            }
            """;
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new TaskCompletionMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Calculate 2 + 2",
            Output = "The result is 4."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("TaskCompletion", result.MetricName);
        Assert.Equal(95, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_TaskNotCompleted_ReturnsFailingScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 30,
                "criteriaResults": [
                    {"criterion": "The response addresses the user's request", "met": false, "reason": "Did not answer"},
                    {"criterion": "The output is complete and actionable", "met": false, "reason": "Incomplete"}
                ],
                "reasoning": "Task was not completed."
            }
            """;
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new TaskCompletionMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Book a flight to Paris",
            Output = "I cannot help with that."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(30, result.Score);
        Assert.False(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_WithCustomCriteria_UsesThemInPrompt()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient("""{"score": 80, "reasoning": "OK"}""");
        var customCriteria = new[] { "CUSTOM_CRITERION_1", "CUSTOM_CRITERION_2" };
        var metric = new TaskCompletionMetric(fakeChatClient, customCriteria);
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done"
        };
        
        // Act
        await metric.EvaluateAsync(context);
        
        // Assert
        var prompt = fakeChatClient.LastPrompt;
        Assert.NotNull(prompt);
        Assert.Contains("CUSTOM_CRITERION_1", prompt);
        Assert.Contains("CUSTOM_CRITERION_2", prompt);
    }
    
    [Fact]
    public async Task EvaluateAsync_WithToolUsage_IncludesToolsInPrompt()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient("""{"score": 90, "reasoning": "Good"}""");
        var metric = new TaskCompletionMetric(fakeChatClient);
        
        var toolUsage = new Models.ToolUsageReport();
        toolUsage.AddCall(new Models.ToolCallRecord { Name = "SearchTool", CallId = "1" });
        toolUsage.AddCall(new Models.ToolCallRecord { Name = "BookingTool", CallId = "2" });
        
        var context = new EvaluationContext
        {
            Input = "Book a flight",
            Output = "Booked!",
            ToolUsage = toolUsage
        };
        
        // Act
        await metric.EvaluateAsync(context);
        
        // Assert
        var prompt = fakeChatClient.LastPrompt;
        Assert.NotNull(prompt);
        Assert.Contains("SearchTool", prompt);
        Assert.Contains("BookingTool", prompt);
    }
    
    [Fact]
    public async Task EvaluateAsync_MalformedLlmResponse_ReturnsFail()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient("Not valid JSON!");
        var metric = new TaskCompletionMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("parse", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }
    
    [Fact]
    public void Constructor_NullChatClient_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new TaskCompletionMetric(null!));
    }
    
    [Fact]
    public async Task Properties_ReturnsCorrectMetadata()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var metric = new TaskCompletionMetric(fakeChatClient);
        
        // Assert
        Assert.Equal("TaskCompletion", metric.Name);
        Assert.False(metric.RequiresToolUsage);
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(200, 100)]
    public async Task EvaluateAsync_ScoreOutOfRange_ClampedTo0To100(int llmScore, int expectedScore)
    {
        // Arrange
        var fakeResponse = $$"""{"score": {{llmScore}}, "reasoning": "test"}""";
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new TaskCompletionMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.InRange(result.Score, 0, 100);
        Assert.Equal(expectedScore, result.Score);
    }
}
