// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.RAG;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for RelevanceMetric - an LLM-based RAG metric.
/// </summary>
public class RelevanceMetricTests
{
    [Fact]
    public async Task EvaluateAsync_HighRelevance_ReturnsPassingScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 95,
                "addressesQuestion": true,
                "staysOnTopic": true,
                "irrelevantParts": [],
                "reasoning": "The response directly addresses the question."
            }
            """;
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new RelevanceMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "What is the capital of France?",
            Output = "The capital of France is Paris."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("Relevance", result.MetricName);
        Assert.Equal(95, result.Score);
        Assert.True(result.Passed);
        Assert.Single(fakeChatClient.ReceivedMessages);
    }
    
    [Fact]
    public async Task EvaluateAsync_OffTopicResponse_ReturnsLowerScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 30,
                "addressesQuestion": false,
                "staysOnTopic": false,
                "irrelevantParts": ["Discussion about weather", "Historical trivia"],
                "reasoning": "The response goes off on tangents and doesn't answer the question."
            }
            """;
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new RelevanceMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "What is 2 + 2?",
            Output = "The weather is nice today. By the way, did you know that ancient Romans used abacuses?"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(30, result.Score);
        Assert.False(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_MalformedLlmResponse_ReturnsFail()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient("This is not valid JSON!");
        var metric = new RelevanceMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("parse", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }
    
    [Fact]
    public async Task EvaluateAsync_PromptContainsInputAndOutput()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient("""{"score": 80, "reasoning": "OK"}""");
        var metric = new RelevanceMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "UNIQUE_INPUT_ABC123",
            Output = "UNIQUE_OUTPUT_XYZ789"
        };
        
        // Act
        await metric.EvaluateAsync(context);
        
        // Assert
        var prompt = fakeChatClient.LastPrompt;
        Assert.NotNull(prompt);
        Assert.Contains("UNIQUE_INPUT_ABC123", prompt);
        Assert.Contains("UNIQUE_OUTPUT_XYZ789", prompt);
    }
    
    [Fact]
    public async Task Properties_ReturnsCorrectMetadata()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var metric = new RelevanceMetric(fakeChatClient);
        
        // Assert
        Assert.Equal("Relevance", metric.Name);
        Assert.False(metric.RequiresContext);
        Assert.False(metric.RequiresGroundTruth);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public async Task EvaluateAsync_ScoreOutOfRange_ClampedTo0To100(int llmScore, int expectedScore)
    {
        // Arrange
        var fakeResponse = $$"""{"score": {{llmScore}}, "reasoning": "test"}""";
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new RelevanceMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.InRange(result.Score, 0, 100);
        Assert.Equal(expectedScore, result.Score);
    }

    [Theory]
    [InlineData("日本語テスト")]
    [InlineData("🚀💡🔥")]
    [InlineData("Line1\nLine2")]
    public async Task EvaluateAsync_SpecialCharacters_HandledCorrectly(string specialText)
    {
        // Arrange
        var fakeResponse = """{"score": 80, "reasoning": "OK"}""";
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new RelevanceMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = $"Question with {specialText}",
            Output = $"Answer with {specialText}"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.True(result.Passed);
    }
}
