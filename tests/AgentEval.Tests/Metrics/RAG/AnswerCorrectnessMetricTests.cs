// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.RAG;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for AnswerCorrectnessMetric - an LLM-based RAG metric.
/// </summary>
public class AnswerCorrectnessMetricTests
{
    [Fact]
    public async Task EvaluateAsync_CorrectAnswer_ReturnsPassingScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 100,
                "factsCorrect": ["Paris is the capital of France"],
                "factsIncorrect": [],
                "factsMissing": [],
                "reasoning": "The answer is completely correct."
            }
            """;
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new AnswerCorrectnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "What is the capital of France?",
            Output = "Paris is the capital of France.",
            GroundTruth = "The capital of France is Paris."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("AnswerCorrectness", result.MetricName);
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_IncorrectAnswer_ReturnsFailingScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 20,
                "factsCorrect": [],
                "factsIncorrect": ["Lyon is not the capital"],
                "factsMissing": ["Paris is the capital"],
                "reasoning": "The answer is factually incorrect."
            }
            """;
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new AnswerCorrectnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "What is the capital of France?",
            Output = "Lyon is the capital of France.",
            GroundTruth = "Paris is the capital of France."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(20, result.Score);
        Assert.False(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_PartiallyCorrect_ReturnsMidScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 60,
                "factsCorrect": ["Paris is the capital"],
                "factsIncorrect": [],
                "factsMissing": ["Population figure", "Country in Europe"],
                "reasoning": "Answer is correct but incomplete."
            }
            """;
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new AnswerCorrectnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Tell me about Paris",
            Output = "Paris is the capital.",
            GroundTruth = "Paris is the capital of France with 2 million people, located in Europe."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(60, result.Score);
        Assert.False(result.Passed); // Below 70 threshold
    }
    
    [Fact]
    public async Task EvaluateAsync_NoGroundTruth_ReturnsFail()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var metric = new AnswerCorrectnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
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
    public async Task EvaluateAsync_EmptyGroundTruth_ReturnsFail()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var metric = new AnswerCorrectnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            GroundTruth = ""
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
        var fakeChatClient = new FakeChatClient("Invalid JSON!");
        var metric = new AnswerCorrectnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            GroundTruth = "Expected"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("parse", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }
    
    [Fact]
    public async Task EvaluateAsync_PromptContainsAllInputs()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient("""{"score": 80, "reasoning": "OK"}""");
        var metric = new AnswerCorrectnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "UNIQUE_INPUT_123",
            Output = "UNIQUE_OUTPUT_456",
            GroundTruth = "UNIQUE_GROUNDTRUTH_789"
        };
        
        // Act
        await metric.EvaluateAsync(context);
        
        // Assert
        var prompt = fakeChatClient.LastPrompt;
        Assert.NotNull(prompt);
        Assert.Contains("UNIQUE_INPUT_123", prompt);
        Assert.Contains("UNIQUE_OUTPUT_456", prompt);
        Assert.Contains("UNIQUE_GROUNDTRUTH_789", prompt);
    }
    
    [Fact]
    public async Task Properties_ReturnsCorrectMetadata()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var metric = new AnswerCorrectnessMetric(fakeChatClient);
        
        // Assert
        Assert.Equal("AnswerCorrectness", metric.Name);
        Assert.False(metric.RequiresContext);
        Assert.True(metric.RequiresGroundTruth);
    }

    [Theory]
    [InlineData(-100, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(999, 100)]
    public async Task EvaluateAsync_ScoreOutOfRange_ClampedTo0To100(int llmScore, int expectedScore)
    {
        // Arrange
        var fakeResponse = $$"""{"score": {{llmScore}}, "reasoning": "test"}""";
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new AnswerCorrectnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            GroundTruth = "Ground truth"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.InRange(result.Score, 0, 100);
        Assert.Equal(expectedScore, result.Score);
    }
}
