// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Calibration;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Metrics.RAG;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Calibration;

/// <summary>
/// Unit tests for CalibratedJudge multi-model evaluation.
/// </summary>
public class CalibratedJudgeTests
{
    #region VotingStrategy Tests
    
    [Fact]
    public void VotingStrategy_HasExpectedValues()
    {
        // Assert all expected strategies exist
        Assert.Equal(0, (int)VotingStrategy.Median);
        Assert.Equal(1, (int)VotingStrategy.Mean);
        Assert.Equal(2, (int)VotingStrategy.Unanimous);
        Assert.Equal(3, (int)VotingStrategy.Weighted);
    }
    
    #endregion
    
    #region CalibratedResult Tests
    
    [Fact]
    public void CalibratedResult_CalculatesStatistics()
    {
        // Arrange
        var judgeScores = new Dictionary<string, double>
        {
            ["Judge1"] = 90,
            ["Judge2"] = 92,
            ["Judge3"] = 88
        };
        
        // Act
        var result = new CalibratedResult
        {
            Score = 90,
            Agreement = 95, // 0-100 scale
            JudgeScores = judgeScores,
            ConfidenceLower = 88.5,
            ConfidenceUpper = 91.5,
            StandardDeviation = 2.0,
            Strategy = VotingStrategy.Median,
            HasConsensus = true
        };
        
        // Assert
        Assert.Equal(90, result.Score);
        Assert.Equal(95, result.Agreement);
        Assert.Equal(3, result.JudgeCount);
        Assert.True(result.HasConsensus);
    }
    
    [Fact]
    public void CalibratedResult_JudgeCount_ReturnsCorrectCount()
    {
        // Arrange
        var result = new CalibratedResult
        {
            Score = 85,
            Agreement = 80,
            JudgeScores = new Dictionary<string, double> 
            { 
                ["J1"] = 80, 
                ["J2"] = 85, 
                ["J3"] = 90 
            },
            Strategy = VotingStrategy.Mean,
            HasConsensus = true
        };
        
        // Assert
        Assert.Equal(3, result.JudgeCount);
    }
    
    [Fact]
    public void CalibratedResult_MeanScore_CalculatesCorrectly()
    {
        // Arrange
        var result = new CalibratedResult
        {
            Score = 85,
            Agreement = 80,
            JudgeScores = new Dictionary<string, double> 
            { 
                ["J1"] = 80, 
                ["J2"] = 85, 
                ["J3"] = 90 
            },
            Strategy = VotingStrategy.Median,
            HasConsensus = true
        };
        
        // Assert
        Assert.Equal(85, result.MeanScore);
    }
    
    #endregion
    
    #region CalibratedJudgeOptions Tests
    
    [Fact]
    public void CalibratedJudgeOptions_HasSensibleDefaults()
    {
        // Act
        var options = new CalibratedJudgeOptions();
        
        // Assert
        Assert.Equal(VotingStrategy.Median, options.Strategy);
        Assert.Equal(10.0, options.ConsensusTolerance);
        Assert.Equal(TimeSpan.FromSeconds(120), options.Timeout);
        Assert.True(options.CalculateConfidenceInterval);
        Assert.Equal(0.95, options.ConfidenceLevel);
        Assert.Equal(3, options.MaxParallelJudges);
        Assert.Equal(1, options.MinimumJudgesRequired);
    }
    
    [Fact]
    public void CalibratedJudgeOptions_CanBeCustomized()
    {
        // Act
        var options = new CalibratedJudgeOptions
        {
            Strategy = VotingStrategy.Mean,
            ConsensusTolerance = 5.0,
            Timeout = TimeSpan.FromSeconds(60),
            CalculateConfidenceInterval = false,
            ConfidenceLevel = 0.99,
            MaxParallelJudges = 5
        };
        
        // Assert
        Assert.Equal(VotingStrategy.Mean, options.Strategy);
        Assert.Equal(5.0, options.ConsensusTolerance);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Timeout);
        Assert.False(options.CalculateConfidenceInterval);
        Assert.Equal(0.99, options.ConfidenceLevel);
        Assert.Equal(5, options.MaxParallelJudges);
    }
    
    #endregion
    
    #region CalibratedJudge Tests
    
    [Fact]
    public async Task EvaluateAsync_WithSingleJudge_ReturnsScore()
    {
        // Arrange
        var fakeClient = new FakeChatClient("""{"score": 85, "explanation": "Good response"}""");
        var judges = new List<IChatClient> { fakeClient };
        var calibratedJudge = new CalibratedJudge(judges);
        
        var metric = new FaithfulnessMetric(fakeClient); // Using same client, but will be replaced
        var context = CreateSampleContext();
        
        // Act
        var result = await calibratedJudge.EvaluateAsync(metric, context);
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(1, result.JudgeCount);
    }
    
    [Fact]
    public async Task EvaluateAsync_WithMultipleJudges_AggregatesScores()
    {
        // Arrange
        var clients = new Dictionary<string, IChatClient>
        {
            ["Judge1"] = new FakeChatClient("""{"score": 80, "explanation": "Good"}"""),
            ["Judge2"] = new FakeChatClient("""{"score": 85, "explanation": "Very good"}"""),
            ["Judge3"] = new FakeChatClient("""{"score": 90, "explanation": "Excellent"}""")
        };
        var judges = clients.Select(kv => (kv.Key, kv.Value)).ToArray();
        var calibratedJudge = new CalibratedJudge(judges);
        
        var context = CreateSampleContext();
        
        // Act - use factory to create metric per judge
        var result = await calibratedJudge.EvaluateAsync(context, 
            judgeName => new FaithfulnessMetric(clients[judgeName]));
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.JudgeCount);
        Assert.Equal(3, result.JudgeScores.Count);
    }
    
    [Fact]
    public async Task EvaluateAsync_WithMedianStrategy_ReturnsMedian()
    {
        // Arrange
        var clients = new Dictionary<string, IChatClient>
        {
            ["Judge1"] = new FakeChatClient("""{"score": 70, "explanation": "Low"}"""),
            ["Judge2"] = new FakeChatClient("""{"score": 85, "explanation": "Mid"}"""),
            ["Judge3"] = new FakeChatClient("""{"score": 100, "explanation": "High"}""")
        };
        var judges = clients.Select(kv => (kv.Key, kv.Value)).ToArray();
        var options = new CalibratedJudgeOptions { Strategy = VotingStrategy.Median };
        var calibratedJudge = new CalibratedJudge(judges, options);
        
        var context = CreateSampleContext();
        
        // Act
        var result = await calibratedJudge.EvaluateAsync(context,
            judgeName => new FaithfulnessMetric(clients[judgeName]));
        
        // Assert
        Assert.Equal(VotingStrategy.Median, result.Strategy);
        Assert.Equal(85, result.Score); // Median of 70, 85, 100
    }
    
    [Fact]
    public async Task EvaluateAsync_WithMeanStrategy_ReturnsMean()
    {
        // Arrange
        var clients = new Dictionary<string, IChatClient>
        {
            ["Judge1"] = new FakeChatClient("""{"score": 70, "explanation": "Low"}"""),
            ["Judge2"] = new FakeChatClient("""{"score": 85, "explanation": "Mid"}"""),
            ["Judge3"] = new FakeChatClient("""{"score": 100, "explanation": "High"}""")
        };
        var judges = clients.Select(kv => (kv.Key, kv.Value)).ToArray();
        var options = new CalibratedJudgeOptions { Strategy = VotingStrategy.Mean };
        var calibratedJudge = new CalibratedJudge(judges, options);
        
        var context = CreateSampleContext();
        
        // Act
        var result = await calibratedJudge.EvaluateAsync(context,
            judgeName => new FaithfulnessMetric(clients[judgeName]));
        
        // Assert
        Assert.Equal(VotingStrategy.Mean, result.Strategy);
        Assert.Equal(85, result.Score); // Mean of 70, 85, 100 = 255/3 = 85
    }
    
    [Fact]
    public async Task EvaluateAsync_CalculatesAgreement()
    {
        // Arrange - similar scores should have high agreement
        var clients = new Dictionary<string, IChatClient>
        {
            ["Judge1"] = new FakeChatClient("""{"score": 88, "explanation": "Good"}"""),
            ["Judge2"] = new FakeChatClient("""{"score": 90, "explanation": "Good"}"""),
            ["Judge3"] = new FakeChatClient("""{"score": 92, "explanation": "Good"}""")
        };
        var judges = clients.Select(kv => (kv.Key, kv.Value)).ToArray();
        var calibratedJudge = new CalibratedJudge(judges);
        
        var context = CreateSampleContext();
        
        // Act
        var result = await calibratedJudge.EvaluateAsync(context,
            judgeName => new FaithfulnessMetric(clients[judgeName]));
        
        // Assert - Agreement is 0-100 scale, high similarity means high agreement
        Assert.True(result.Agreement > 70);
        Assert.True(result.HasConsensus);
    }
    
    [Fact]
    public async Task EvaluateAsync_WithDivergentScores_LowAgreement()
    {
        // Arrange - very different scores should have low agreement
        var clients = new Dictionary<string, IChatClient>
        {
            ["Judge1"] = new FakeChatClient("""{"score": 20, "explanation": "Terrible"}"""),
            ["Judge2"] = new FakeChatClient("""{"score": 50, "explanation": "Medium"}"""),
            ["Judge3"] = new FakeChatClient("""{"score": 90, "explanation": "Excellent"}""")
        };
        var judges = clients.Select(kv => (kv.Key, kv.Value)).ToArray();
        var options = new CalibratedJudgeOptions { ConsensusTolerance = 10 };
        var calibratedJudge = new CalibratedJudge(judges, options);
        
        var context = CreateSampleContext();
        
        // Act
        var result = await calibratedJudge.EvaluateAsync(context,
            judgeName => new FaithfulnessMetric(clients[judgeName]));
        
        // Assert - Agreement is 0-100 scale, divergent scores mean low agreement
        Assert.True(result.Agreement < 50);
        Assert.False(result.HasConsensus);
    }
    
    [Fact]
    public async Task EvaluateAsync_WithConfidenceInterval_CalculatesBounds()
    {
        // Arrange
        var clients = new Dictionary<string, IChatClient>
        {
            ["Judge1"] = new FakeChatClient("""{"score": 85, "explanation": "Good"}"""),
            ["Judge2"] = new FakeChatClient("""{"score": 88, "explanation": "Good"}"""),
            ["Judge3"] = new FakeChatClient("""{"score": 92, "explanation": "Good"}""")
        };
        var judges = clients.Select(kv => (kv.Key, kv.Value)).ToArray();
        var options = new CalibratedJudgeOptions { CalculateConfidenceInterval = true };
        var calibratedJudge = new CalibratedJudge(judges, options);
        
        var context = CreateSampleContext();
        
        // Act
        var result = await calibratedJudge.EvaluateAsync(context,
            judgeName => new FaithfulnessMetric(clients[judgeName]));
        
        // Assert
        Assert.NotNull(result.ConfidenceLower);
        Assert.NotNull(result.ConfidenceUpper);
        Assert.True(result.ConfidenceLower < result.Score);
        Assert.True(result.ConfidenceUpper > result.Score);
    }
    
    [Fact]
    public async Task EvaluateAsync_CalculatesStandardDeviation()
    {
        // Arrange
        var clients = new Dictionary<string, IChatClient>
        {
            ["Judge1"] = new FakeChatClient("""{"score": 80, "explanation": "Good"}"""),
            ["Judge2"] = new FakeChatClient("""{"score": 90, "explanation": "Good"}"""),
            ["Judge3"] = new FakeChatClient("""{"score": 100, "explanation": "Good"}""")
        };
        var judges = clients.Select(kv => (kv.Key, kv.Value)).ToArray();
        var calibratedJudge = new CalibratedJudge(judges);
        
        var context = CreateSampleContext();
        
        // Act
        var result = await calibratedJudge.EvaluateAsync(context,
            judgeName => new FaithfulnessMetric(clients[judgeName]));
        
        // Assert - StandardDeviation should be positive for different scores
        Assert.True(result.StandardDeviation > 0);
    }
    
    [Fact]
    public void Constructor_WithEmptyJudges_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            new CalibratedJudge(Array.Empty<(string, IChatClient)>()));
    }
    
    [Fact]
    public void Constructor_WithNullJudges_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            new CalibratedJudge((IEnumerable<(string, IChatClient)>)null!));
    }
    
    #endregion
    
    #region ICalibratedJudge Interface Tests
    
    [Fact]
    public void CalibratedJudge_ImplementsInterface()
    {
        // Arrange
        var judges = new List<IChatClient> { new FakeChatClient("{}") };
        
        // Act
        ICalibratedJudge judge = new CalibratedJudge(judges);
        
        // Assert
        Assert.NotNull(judge);
    }
    
    #endregion
    
    #region Helper Methods
    
    private static EvaluationContext CreateSampleContext()
    {
        return new EvaluationContext
        {
            Input = "What is the capital of France?",
            Output = "The capital of France is Paris.",
            Context = "France is a country in Western Europe. Paris is its capital and largest city.",
            GroundTruth = "Paris"
        };
    }
    
    #endregion
}
