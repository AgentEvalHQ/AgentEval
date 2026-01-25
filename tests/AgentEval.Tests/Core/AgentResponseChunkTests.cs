// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// Tests for AgentResponseChunk including streaming usage capture.
/// </summary>
public class AgentResponseChunkTests
{
    [Fact]
    public void AgentResponseChunk_DefaultValues_ShouldBeCorrect()
    {
        var chunk = new AgentResponseChunk();
        
        Assert.Null(chunk.Text);
        Assert.Null(chunk.ToolCallStarted);
        Assert.Null(chunk.ToolCallCompleted);
        Assert.Null(chunk.Usage);
        Assert.False(chunk.IsComplete);
    }
    
    [Fact]
    public void AgentResponseChunk_WithText_ShouldStoreText()
    {
        var chunk = new AgentResponseChunk { Text = "Hello" };
        
        Assert.Equal("Hello", chunk.Text);
        Assert.False(chunk.IsComplete);
    }
    
    [Fact]
    public void AgentResponseChunk_WithUsage_ShouldStoreUsage()
    {
        var usage = new TokenUsage
        {
            PromptTokens = 100,
            CompletionTokens = 50
        };
        
        var chunk = new AgentResponseChunk
        {
            IsComplete = true,
            Usage = usage
        };
        
        Assert.True(chunk.IsComplete);
        Assert.NotNull(chunk.Usage);
        Assert.Equal(100, chunk.Usage.PromptTokens);
        Assert.Equal(50, chunk.Usage.CompletionTokens);
        Assert.Equal(150, chunk.Usage.TotalTokens);
    }
    
    [Fact]
    public void AgentResponseChunk_FinalChunk_ShouldHaveIsCompleteTrue()
    {
        var chunk = new AgentResponseChunk { IsComplete = true };
        
        Assert.True(chunk.IsComplete);
        Assert.Null(chunk.Text);
    }
    
    [Fact]
    public void AgentResponseChunk_WithToolCallStarted_ShouldStoreToolInfo()
    {
        var toolInfo = new ToolCallInfo
        {
            Name = "GetWeather",
            CallId = "call_123",
            Arguments = new Dictionary<string, object?> { { "city", "Seattle" } }
        };
        
        var chunk = new AgentResponseChunk { ToolCallStarted = toolInfo };
        
        Assert.NotNull(chunk.ToolCallStarted);
        Assert.Equal("GetWeather", chunk.ToolCallStarted.Name);
        Assert.Equal("call_123", chunk.ToolCallStarted.CallId);
    }
    
    [Fact]
    public void AgentResponseChunk_WithToolCallCompleted_ShouldStoreResult()
    {
        var resultInfo = new ToolResultInfo
        {
            CallId = "call_123",
            Result = "72°F, Sunny"
        };
        
        var chunk = new AgentResponseChunk { ToolCallCompleted = resultInfo };
        
        Assert.NotNull(chunk.ToolCallCompleted);
        Assert.Equal("call_123", chunk.ToolCallCompleted.CallId);
        Assert.Equal("72°F, Sunny", chunk.ToolCallCompleted.Result);
        Assert.Null(chunk.ToolCallCompleted.Exception);
    }
    
    [Fact]
    public void FinalStreamingChunk_WithUsage_SupportsStreamingCostCalculation()
    {
        // Simulates the final chunk from streaming that includes usage data
        var finalChunk = new AgentResponseChunk
        {
            IsComplete = true,
            Usage = new TokenUsage
            {
                PromptTokens = 500,
                CompletionTokens = 200
            }
        };
        
        // This simulates what MAFEvaluationHarness does when extracting usage
        Assert.True(finalChunk.IsComplete);
        Assert.NotNull(finalChunk.Usage);
        Assert.Equal(700, finalChunk.Usage.TotalTokens);
        
        // Verify cost can be calculated from streaming usage
        var estimatedCost = ModelPricing.EstimateCost("gpt-4o", 
            finalChunk.Usage.PromptTokens, 
            finalChunk.Usage.CompletionTokens);
        
        Assert.NotNull(estimatedCost);
        Assert.True(estimatedCost > 0);
    }
}
