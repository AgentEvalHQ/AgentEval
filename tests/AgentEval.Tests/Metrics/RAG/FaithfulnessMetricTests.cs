// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.RAG;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for FaithfulnessMetric - an LLM-based RAG metric.
/// </summary>
public class FaithfulnessMetricTests
{
    [Fact]
    public async Task EvaluateAsync_HighFaithfulness_ReturnsPassingScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 95,
                "faithfulClaims": ["Paris is the capital of France"],
                "hallucinatedClaims": [],
                "reasoning": "The response is fully supported by the context."
            }
            """;
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new FaithfulnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "What is the capital of France?",
            Output = "Paris is the capital of France.",
            Context = "France is a country in Europe. Its capital is Paris."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("Faithfulness", result.MetricName);
        Assert.Equal(95, result.Score);
        Assert.True(result.Passed);
        Assert.Single(fakeChatClient.ReceivedMessages);
    }
    
    [Fact]
    public async Task EvaluateAsync_WithHallucinations_ReturnsLowerScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 40,
                "faithfulClaims": ["Paris is a city"],
                "hallucinatedClaims": ["Paris has 10 million people", "Paris was founded in 52 BC"],
                "reasoning": "The response contains claims not in the context."
            }
            """;
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new FaithfulnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Tell me about Paris",
            Output = "Paris is a city. It has 10 million people and was founded in 52 BC.",
            Context = "Paris is a city in France."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(40, result.Score);
        Assert.False(result.Passed); // Default passing threshold is 70
        Assert.NotNull(result.Details);
        Assert.Contains("hallucinatedClaims", result.Details.Keys);
    }
    
    [Fact]
    public async Task EvaluateAsync_NoContext_ReturnsFail()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient(); // No response needed
        var metric = new FaithfulnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "What is the capital of France?",
            Output = "Paris is the capital of France.",
            Context = null // No context provided
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("requires context", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fakeChatClient.ReceivedMessages); // Should not call LLM
    }
    
    [Fact]
    public async Task EvaluateAsync_MalformedLlmResponse_ReturnsFail()
    {
        // Arrange - LLM returns invalid JSON
        var fakeChatClient = new FakeChatClient("This is not valid JSON at all!");
        var metric = new FaithfulnessMetric(fakeChatClient);
        
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
    public async Task EvaluateAsync_PromptContainsContextAndOutput()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient("""{"score": 80, "reasoning": "OK"}""");
        var metric = new FaithfulnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "UNIQUE_INPUT_12345",
            Output = "UNIQUE_OUTPUT_67890",
            Context = "UNIQUE_CONTEXT_ABCDE"
        };
        
        // Act
        await metric.EvaluateAsync(context);
        
        // Assert - verify the prompt includes our test values
        var prompt = fakeChatClient.LastPrompt;
        Assert.NotNull(prompt);
        Assert.Contains("UNIQUE_CONTEXT_ABCDE", prompt);
        Assert.Contains("UNIQUE_OUTPUT_67890", prompt);
        Assert.Contains("UNIQUE_INPUT_12345", prompt);
    }
    
    [Fact]
    public async Task EvaluateAsync_EmptyContext_ReturnsFail()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var metric = new FaithfulnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = "" // Empty string context
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("requires context", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }
    
    [Fact]
    public async Task Properties_ReturnsCorrectMetadata()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var metric = new FaithfulnessMetric(fakeChatClient);
        
        // Assert
        Assert.Equal("Faithfulness", metric.Name);
        Assert.True(metric.RequiresContext);
        Assert.False(metric.RequiresGroundTruth);
    }

    #region Edge Cases - Score Boundaries

    [Theory]
    [InlineData(-50, 0)]    // Negative score should clamp to 0
    [InlineData(-1, 0)]     // Just below zero
    [InlineData(0, 0)]      // Zero is valid
    [InlineData(100, 100)]  // Max valid
    [InlineData(101, 100)]  // Just above max
    [InlineData(150, 100)]  // Well above max
    [InlineData(999999, 100)] // Extreme value
    public async Task EvaluateAsync_ScoreOutOfRange_ClampedTo0To100(int llmScore, int expectedScore)
    {
        // Arrange
        var fakeResponse = $$"""{"score": {{llmScore}}, "reasoning": "test"}""";
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new FaithfulnessMetric(fakeChatClient);
        
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

    #endregion

    #region Edge Cases - Special Characters

    [Theory]
    [InlineData("日本語テスト", "Japanese text")]
    [InlineData("مرحبا", "Arabic text")]
    [InlineData("🚀💡🔥", "Emoji")]
    [InlineData("Line1\nLine2\nLine3", "Newlines")]
    [InlineData("Tab\there\tand\tthere", "Tabs")]
    [InlineData("Quote: \"Hello\"", "Quotes")]
    [InlineData("Backslash: \\path\\to\\file", "Backslashes")]
    public async Task EvaluateAsync_SpecialCharacters_HandledCorrectly(string specialText, string description)
    {
        // Arrange
        var fakeResponse = """{"score": 80, "reasoning": "Handled special chars"}""";
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new FaithfulnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = $"Question with {specialText}",
            Output = $"Answer with {specialText}",
            Context = $"Context with {specialText}"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.True(result.Passed, $"Failed for {description}: {specialText}");
        Assert.NotNull(fakeChatClient.LastPrompt);
    }

    #endregion

    #region Edge Cases - LLM Response Variations

    [Fact]
    public async Task EvaluateAsync_LlmReturnsOnlyScore_HandlesGracefully()
    {
        // Arrange - minimal response
        var fakeResponse = """{"score": 75}""";
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new FaithfulnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = "Context"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(75, result.Score);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task EvaluateAsync_LlmReturnsEmptyJson_ReturnsFail()
    {
        // Arrange
        var fakeResponse = """{}""";
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new FaithfulnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = "Context"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert - should handle missing score gracefully
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public async Task EvaluateAsync_LlmReturnsScoreAsString_ParsesCorrectly()
    {
        // Arrange - some LLMs might return score as string
        var fakeResponse = """{"score": "85", "reasoning": "test"}""";
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new FaithfulnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = "Context"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert - should parse string to number
        Assert.Equal(85, result.Score);
    }

    [Fact]
    public async Task EvaluateAsync_LlmReturnsVeryLongReasoning_HandlesGracefully()
    {
        // Arrange - very long reasoning text
        var longReasoning = new string('A', 10000);
        var fakeResponse = $$"""{"score": 80, "reasoning": "{{longReasoning}}"}""";
        var fakeChatClient = new FakeChatClient(fakeResponse);
        var metric = new FaithfulnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = "Context"
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(80, result.Score);
        Assert.True(result.Passed);
    }

    #endregion

    #region Edge Cases - Whitespace Only Content

    [Fact]
    public async Task EvaluateAsync_WhitespaceOnlyContext_ReturnsFail()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var metric = new FaithfulnessMetric(fakeChatClient);
        
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer",
            Context = "   \t\n   " // Whitespace only
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
    }

    #endregion
}
