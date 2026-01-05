// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.RAG;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for ContextPrecisionMetric - an LLM-based RAG metric.
/// </summary>
public class ContextPrecisionMetricTests
{
    [Fact]
    public async Task EvaluateAsync_HighPrecision_ReturnsPassingScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 95,
                "relevantParts": ["Capital city information", "Population data"],
                "irrelevantParts": [],
                "reasoning": "All context is highly relevant to the question."
            }
            """;
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new ContextPrecisionMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "What is the capital of France?",
            Output = "Paris is the capital of France.",
            Context = "France is a country in Europe. Its capital is Paris, which has a population of 2 million."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("ContextPrecision", result.MetricName);
        Assert.Equal(95, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_LowPrecision_ReturnsFailingScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 25,
                "relevantParts": ["Capital mention"],
                "irrelevantParts": ["Recipe instructions", "Weather forecast", "Movie reviews"],
                "reasoning": "Most of the context is irrelevant noise."
            }
            """;
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new ContextPrecisionMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "What is the capital of France?",
            Output = "Paris",
            Context = "Here's a recipe for pasta. The weather is sunny. Paris is a city. Check out this movie review."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(25, result.Score);
        Assert.False(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_NoContext_ReturnsFail()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var metric = new ContextPrecisionMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = null
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("requires context", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fakeChatClient.ReceivedMessages);
    }
    
    [Fact]
    public async Task EvaluateAsync_EmptyContext_ReturnsFail()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var metric = new ContextPrecisionMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = ""
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_MalformedLlmResponse_ReturnsFail()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient("Invalid JSON response");
        var metric = new ContextPrecisionMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = "Some context"
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
        var metric = new ContextPrecisionMetric(fakeChatClient);
        
        // Assert
        Assert.Equal("ContextPrecision", metric.Name);
        Assert.True(metric.RequiresContext);
        Assert.False(metric.RequiresGroundTruth);
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
        var metric = new ContextPrecisionMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = "Context"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.InRange(result.Score, 0, 100);
        Assert.Equal(expectedScore, result.Score);
    }
}
