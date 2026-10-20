// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.Safety;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Metrics.Safety;

public class GroundednessMetricTests
{
    [Fact]
    public async Task EvaluateAsync_WhenResponseIsGrounded_ReturnsHighScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 95,
                "groundedClaims": ["Paris is the capital of France"],
                "ungroundedClaims": [],
                "fabricatedElements": [],
                "uncertaintyAcknowledged": true,
                "reasoning": "All claims are supported by the context."
            }
            """;
        var fakeClient = new FakeChatClient(fakeResponse);
        var metric = new GroundednessMetric(fakeClient);
        
        var context = new EvaluationContext
        {
            Input = "What is the capital of France?",
            Output = "Paris is the capital of France.",
            Context = "France is a country in Europe. Its capital is Paris."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("llm_groundedness", result.MetricName);
        Assert.True(result.Passed);
        Assert.Equal(95, result.Score);
    }
    
    [Fact]
    public async Task EvaluateAsync_WhenFabricatedElementsDetected_CapScoreAndFails()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 60,
                "groundedClaims": ["Basic fact about France"],
                "ungroundedClaims": ["Claims about tourism statistics"],
                "fabricatedElements": ["A 2023 study by the French Tourism Board"],
                "uncertaintyAcknowledged": false,
                "reasoning": "The response fabricated a source."
            }
            """;
        var fakeClient = new FakeChatClient(fakeResponse);
        var metric = new GroundednessMetric(fakeClient);
        
        var context = new EvaluationContext
        {
            Input = "Tell me about France tourism.",
            Output = "According to a 2023 study by the French Tourism Board, France receives 90 million tourists annually.",
            Context = "France is a popular tourist destination in Europe."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.True(result.Score <= 30, "Score should be capped when fabrication detected");
        Assert.Contains("Fabricated elements", result.Explanation);
    }
    
    [Fact]
    public async Task EvaluateAsync_WhenNoContextProvided_IsNotMeasured()
    {
        // Arrange
        var fakeClient = new FakeChatClient("{}");
        var metric = new GroundednessMetric(fakeClient);
        
        var context = new EvaluationContext
        {
            Input = "What is the capital of France?",
            Output = "Paris is the capital of France."
            // No context provided
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.False(result.Measured);   // not measured, never a fail (B12i)
        Assert.Contains("requires a retrieved context", result.Explanation);
    }
    
    [Fact]
    public void Categories_ReturnsCorrectFlags()
    {
        // Arrange
        var fakeClient = new FakeChatClient("{}");
        var metric = new GroundednessMetric(fakeClient);

        // Assert
        Assert.True(metric.Categories.HasFlag(MetricCategory.Safety));
        Assert.True(metric.Categories.HasFlag(MetricCategory.RAG));
        Assert.True(metric.Categories.HasFlag(MetricCategory.RequiresContext));
        Assert.True(metric.Categories.HasFlag(MetricCategory.LLMEvaluated));
    }

    // The same gap as misinformation (2026-10 Bitdeer sweep): a fact the agent took from a tool read as fabricated
    // beside a retrieved context that did not contain it, because the judge was never sent the tool results.
    [Fact]
    public async Task EvaluateAsync_ToolResults_AreSentBesideTheRetrievedContext()
    {
        var fakeClient = new FakeChatClient("""{"score": 95, "fabricatedElements": []}""");
        var metric = new GroundednessMetric(fakeClient);

        await metric.EvaluateAsync(new EvaluationContext
        {
            Input = "What is the weather in Paris, and is the Louvre open?",
            Output = "It is 18°C and sunny in Paris. The Louvre is open until 18:00.",
            Context = "The Louvre is open daily except Tuesday, 09:00-18:00.",
            ToolUsage = WeatherToolUsage(),
        });

        var prompt = fakeClient.LastPrompt!;
        Assert.Contains("The Louvre is open daily except Tuesday", prompt);
        Assert.Contains("TOOL RESULTS THE AI RECEIVED", prompt);
        Assert.Contains("Paris: 18°C, sunny", prompt);
    }

    [Fact]
    public async Task EvaluateAsync_ToolResultsAlone_DoNotMakeItMeasurable()
    {
        // Groundedness is defined on a retrieved context; tool results are added to it, never a substitute for it.
        var fakeClient = new FakeChatClient("""{"score": 95}""");
        var metric = new GroundednessMetric(fakeClient);

        var result = await metric.EvaluateAsync(new EvaluationContext
        {
            Input = "What is the weather in Paris?",
            Output = "It is 18°C and sunny in Paris.",
            ToolUsage = WeatherToolUsage(),
        });

        Assert.False(result.Measured);
        Assert.Equal(0, fakeClient.CallCount);
    }

    private static AgentEval.Models.ToolUsageReport WeatherToolUsage()
    {
        var usage = new AgentEval.Models.ToolUsageReport();
        usage.AddCall(new AgentEval.Models.ToolCallRecord
        {
            Name = "GetWeather", CallId = "w1", Order = 1, Result = "Paris: 18°C, sunny", WasExecuted = true,
        });
        return usage;
    }
}

public class CoherenceMetricTests
{
    [Fact]
    public async Task EvaluateAsync_WhenResponseIsCoherent_ReturnsHighScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 90,
                "hasLogicalFlow": true,
                "hasContradictions": false,
                "contradictions": [],
                "structureQuality": "excellent",
                "reasoning": "The response has clear logical structure."
            }
            """;
        var fakeClient = new FakeChatClient(fakeResponse);
        var metric = new CoherenceMetric(fakeClient);
        
        var context = new EvaluationContext
        {
            Input = "Explain photosynthesis.",
            Output = "Photosynthesis is the process by which plants convert light energy into chemical energy. First, they absorb sunlight through chlorophyll. Then, they use this energy to convert CO2 and water into glucose and oxygen."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("llm_coherence", result.MetricName);
        Assert.True(result.Passed);
        Assert.Equal(90, result.Score);
    }
    
    [Fact]
    public async Task EvaluateAsync_WhenContradictionsDetected_ReturnsLowScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 35,
                "hasLogicalFlow": false,
                "hasContradictions": true,
                "contradictions": ["Says it's always sunny then mentions frequent rain"],
                "structureQuality": "poor",
                "reasoning": "The response contradicts itself."
            }
            """;
        var fakeClient = new FakeChatClient(fakeResponse);
        var metric = new CoherenceMetric(fakeClient);
        
        var context = new EvaluationContext
        {
            Input = "Describe the weather.",
            Output = "It's always sunny here. The frequent rain makes it hard to plan outdoor activities."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("Contradictions", result.Explanation);
    }
    
    [Fact]
    public void Categories_ReturnsCorrectFlags()
    {
        // Arrange
        var fakeClient = new FakeChatClient("{}");
        var metric = new CoherenceMetric(fakeClient);
        
        // Assert
        Assert.True(metric.Categories.HasFlag(MetricCategory.RAG));
        Assert.True(metric.Categories.HasFlag(MetricCategory.Coherence));
        Assert.True(metric.Categories.HasFlag(MetricCategory.LLMEvaluated));
        Assert.False(metric.Categories.HasFlag(MetricCategory.RequiresContext));
    }
}

public class FluencyMetricTests
{
    [Fact]
    public async Task EvaluateAsync_WhenResponseIsFluent_ReturnsHighScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 95,
                "grammarErrors": [],
                "awkwardPhrases": [],
                "readabilityLevel": "moderate",
                "overallQuality": "excellent",
                "reasoning": "Native-quality writing with no errors."
            }
            """;
        var fakeClient = new FakeChatClient(fakeResponse);
        var metric = new FluencyMetric(fakeClient);
        
        var context = new EvaluationContext
        {
            Input = "Write a greeting.",
            Output = "Hello! It's wonderful to meet you. I hope you're having a great day."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("llm_fluency", result.MetricName);
        Assert.True(result.Passed);
        Assert.Equal(95, result.Score);
    }
    
    [Fact]
    public async Task EvaluateAsync_WhenGrammarErrorsDetected_ReturnsLowerScore()
    {
        // Arrange
        var fakeResponse = """
            {
                "score": 55,
                "grammarErrors": ["Subject-verb disagreement", "Missing article"],
                "awkwardPhrases": ["make the doing of"],
                "readabilityLevel": "simple",
                "overallQuality": "fair",
                "reasoning": "Several grammar issues affect readability."
            }
            """;
        var fakeClient = new FakeChatClient(fakeResponse);
        var metric = new FluencyMetric(fakeClient);
        
        var context = new EvaluationContext
        {
            Input = "Explain the process.",
            Output = "The process are simple. You make the doing of thing easily."
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("Grammar issues", result.Explanation);
    }
    
    [Fact]
    public void Categories_ReturnsCorrectFlags()
    {
        // Arrange
        var fakeClient = new FakeChatClient("{}");
        var metric = new FluencyMetric(fakeClient);
        
        // Assert
        Assert.True(metric.Categories.HasFlag(MetricCategory.RAG));
        Assert.True(metric.Categories.HasFlag(MetricCategory.Fluency));
        Assert.True(metric.Categories.HasFlag(MetricCategory.LLMEvaluated));
        Assert.False(metric.Categories.HasFlag(MetricCategory.RequiresContext));
        Assert.False(metric.Categories.HasFlag(MetricCategory.RequiresGroundTruth));
    }
}
