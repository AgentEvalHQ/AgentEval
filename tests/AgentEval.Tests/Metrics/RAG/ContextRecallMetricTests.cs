// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.RAG;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for ContextRecallMetric - an LLM-based RAG metric.
/// </summary>
public class ContextRecallMetricTests
{
    [Fact]
    public async Task EvaluateAsync_HighRecall_ReturnsPassingScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 100,
                "informationPresent": ["Paris is the capital", "France is in Europe"],
                "informationMissing": [],
                "reasoning": "All information needed is in the context."
            }
            """;
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new ContextRecallMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "What is the capital of France?",
            Output = "Paris is the capital of France.",
            Context = "France is a European country. Its capital city is Paris.",
            GroundTruth = "Paris is the capital of France, which is located in Europe."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("ContextRecall", result.MetricName);
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_LowRecall_ReturnsFailingScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 30,
                "informationPresent": ["Paris mentioned"],
                "informationMissing": ["Population figure", "Founded date", "Region name"],
                "reasoning": "Most critical information is missing from context."
            }
            """;
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new ContextRecallMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Tell me about Paris",
            Output = "Paris has 2 million people.",
            Context = "Paris is a city.",
            GroundTruth = "Paris has a population of 2.1 million, was founded in 52 BC, and is in Île-de-France."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(30, result.Score);
        Assert.False(result.Passed);
        Assert.NotNull(result.Details);
        Assert.Contains("missingInformation", result.Details.Keys);
    }
    
    [Fact]
    public async Task EvaluateAsync_NoContext_ReturnsFail()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var metric = new ContextRecallMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = null,
            GroundTruth = "Expected answer"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("requires context", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fakeChatClient.ReceivedMessages);
    }
    
    [Fact]
    public async Task EvaluateAsync_NoGroundTruth_ReturnsFail()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var metric = new ContextRecallMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = "Some context",
            GroundTruth = null
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("requires ground truth", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fakeChatClient.ReceivedMessages);
    }
    
    [Fact]
    public async Task EvaluateAsync_MalformedLlmResponse_ReturnsFail()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient("Not JSON at all");
        var metric = new ContextRecallMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = "Context",
            GroundTruth = "Ground truth"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("parse", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }
    
    [Fact]
    public async Task Properties_ReturnsCorrectMetadata()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var metric = new ContextRecallMetric(fakeChatClient);
        
        // Assert
        Assert.Equal("ContextRecall", metric.Name);
        Assert.True(metric.RequiresContext);
        Assert.True(metric.RequiresGroundTruth);
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(120, 100)]
    public async Task EvaluateAsync_ScoreOutOfRange_ClampedTo0To100(int llmScore, int expectedScore)
    {
        // Arrange
        var fakeResponse = $$"""{"score": {{llmScore}}, "reasoning": "test"}""";
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new ContextRecallMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = "Context",
            GroundTruth = "Ground truth"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.InRange(result.Score, 0, 100);
        Assert.Equal(expectedScore, result.Score);
    }
}
