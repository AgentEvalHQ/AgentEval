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
    public void VotingStrategy_ContainsAllExpectedMembers()
    {
        // Assert all 4 strategies exist by name (not ordinal — ordinals are an implementation detail)
        var names = Enum.GetNames<VotingStrategy>();
        Assert.Equal(4, names.Length);
        Assert.Contains("Median", names);
        Assert.Contains("Mean", names);
        Assert.Contains("Unanimous", names);
        Assert.Contains("Weighted", names);
    }
    
    #endregion
    
    #region CalibratedResult Tests
    
    [Fact]
    public void CalibratedResult_ComputedProperties_CalculateFromJudgeScores()
    {
        // Arrange — set JudgeScores, verify computed properties derive correctly
        var result = new CalibratedResult
        {
            Score = 90,
            Agreement = 95,
            JudgeScores = new Dictionary<string, double>
            {
                ["Judge1"] = 90,
                ["Judge2"] = 92,
                ["Judge3"] = 88
            },
            StandardDeviation = 2.0,
            Strategy = VotingStrategy.Median,
            HasConsensus = true
        };
        
        // Assert — JudgeCount and MeanScore are computed, not set
        Assert.Equal(3, result.JudgeCount);
        Assert.Equal(90, result.MeanScore); // (90+92+88)/3 = 90
        Assert.True(result.HasConsensus);
    }
    
    [Fact]
    public void CalibratedResult_WithEmptyScores_MeanScoreReturnsZero()
    {
        // Arrange — edge case: no judges succeeded
        var result = new CalibratedResult
        {
            Score = 0,
            Agreement = 0,
            JudgeScores = new Dictionary<string, double>(),
            Strategy = VotingStrategy.Mean,
            HasConsensus = false
        };

        // Assert
        Assert.Equal(0, result.JudgeCount);
        Assert.Equal(0, result.MeanScore);
    }

    [Fact]
    public void CalibratedJudgeOptions_Validate_ThrowsOnInvalidValues()
    {
        // Negative ConsensusTolerance
        Assert.Throws<ArgumentException>(() =>
            new CalibratedJudgeOptions { ConsensusTolerance = -1 }.Validate());

        // ConfidenceLevel out of range (> 1)
        Assert.Throws<ArgumentException>(() =>
            new CalibratedJudgeOptions { ConfidenceLevel = 1.5 }.Validate());

        // ConfidenceLevel out of range (< 0)
        Assert.Throws<ArgumentException>(() =>
            new CalibratedJudgeOptions { ConfidenceLevel = -0.1 }.Validate());

        // MaxParallelJudges < 1
        Assert.Throws<ArgumentException>(() =>
            new CalibratedJudgeOptions { MaxParallelJudges = 0 }.Validate());

        // MinimumJudgesRequired < 1
        Assert.Throws<ArgumentException>(() =>
            new CalibratedJudgeOptions { MinimumJudgesRequired = 0 }.Validate());
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
    public void Constructor_WithAutoNamedClients_AssignsSequentialNames()
    {
        // Arrange — use the IEnumerable<IChatClient> constructor (auto-naming)
        var clients = new List<IChatClient>
        {
            new FakeChatClient("{}"),
            new FakeChatClient("{}"),
            new FakeChatClient("{}")
        };

        // Act
        var judge = new CalibratedJudge(clients);

        // Assert — auto-names are "Judge1", "Judge2", "Judge3"
        Assert.Equal(3, judge.JudgeNames.Count);
        Assert.Equal("Judge1", judge.JudgeNames[0]);
        Assert.Equal("Judge2", judge.JudgeNames[1]);
        Assert.Equal("Judge3", judge.JudgeNames[2]);

        // Options default should be used
        Assert.Equal(VotingStrategy.Median, judge.Options.Strategy);
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
    public async Task EvaluateAsync_WithConfidenceInterval_CalculatesCorrectBounds()
    {
        // Arrange — 3 judges, df=2, 95% CI → t=4.303
        // Scores: 85, 88, 92 → mean=88.333, stddev=3.512, SE=3.512/√3=2.028
        // Margin = 4.303 * 2.028 = 8.726
        // Lower = 88.333 - 8.726 = 79.607, Upper = 88.333 + 8.726 = 97.060
        var clients = new Dictionary<string, IChatClient>
        {
            ["Judge1"] = new FakeChatClient("""{"score": 85, "explanation": "Good"}"""),
            ["Judge2"] = new FakeChatClient("""{"score": 88, "explanation": "Good"}"""),
            ["Judge3"] = new FakeChatClient("""{"score": 92, "explanation": "Good"}""")
        };
        var judges = clients.Select(kv => (kv.Key, kv.Value)).ToArray();
        var options = new CalibratedJudgeOptions { CalculateConfidenceInterval = true, ConfidenceLevel = 0.95 };
        var calibratedJudge = new CalibratedJudge(judges, options);
        
        var context = CreateSampleContext();
        
        // Act
        var result = await calibratedJudge.EvaluateAsync(context,
            judgeName => new FaithfulnessMetric(clients[judgeName]));
        
        // Assert — verify t(df=2, 95%) = 4.303 produces correct CI bounds
        Assert.NotNull(result.ConfidenceLower);
        Assert.NotNull(result.ConfidenceUpper);
        Assert.InRange(result.ConfidenceLower!.Value, 79.0, 80.5);  // ~79.6
        Assert.InRange(result.ConfidenceUpper!.Value, 96.0, 98.0);  // ~97.1
        // The old buggy t=2.5 would have given CI [83.3, 93.4] — much too narrow
    }
    
    [Fact]
    public async Task EvaluateAsync_CalculatesStandardDeviation_ExactValue()
    {
        // Arrange — scores 80, 90, 100 → mean=90, sample stddev = sqrt(((80-90)²+(90-90)²+(100-90)²)/2) = 10.0
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
        
        // Assert — exact sample standard deviation for [80, 90, 100]
        Assert.Equal(10.0, result.StandardDeviation, precision: 1);
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
    
    [Fact]
    public async Task EvaluateAsync_WithWeightedStrategy_AppliesWeightsCorrectly()
    {
        // Arrange
        var clients = new Dictionary<string, IChatClient>
        {
            ["Heavy"] = new FakeChatClient("""{"score": 100, "explanation": "Perfect"}"""),
            ["Light"] = new FakeChatClient("""{"score": 50, "explanation": "Mediocre"}""")
        };
        var judges = clients.Select(kv => (kv.Key, kv.Value)).ToArray();
        var options = new CalibratedJudgeOptions
        {
            Strategy = VotingStrategy.Weighted,
            JudgeWeights = new Dictionary<string, double>
            {
                ["Heavy"] = 3.0,
                ["Light"] = 1.0
            }
        };
        var calibratedJudge = new CalibratedJudge(judges, options);
        var context = CreateSampleContext();

        // Act
        var result = await calibratedJudge.EvaluateAsync(context,
            judgeName => new FaithfulnessMetric(clients[judgeName]));

        // Assert: Weighted (100*3 + 50*1) / (3+1) = 87.5
        Assert.Equal(87.5, result.Score);
        Assert.Equal(VotingStrategy.Weighted, result.Strategy);
    }
    
    [Fact]
    public async Task EvaluateAsync_WithWeightedStrategy_WhenFirstJudgeFails_AppliesCorrectWeights()
    {
        // Arrange
        var failingClient = new FakeChatClient();
        failingClient.ThrowOnNextCall = true;
        failingClient.ThrowMessage = "API timeout";
        
        var clients = new Dictionary<string, IChatClient>
        {
            ["Failing"] = failingClient,
            ["JudgeB"] = new FakeChatClient("""{"score": 80, "explanation": "Good"}"""),
            ["JudgeC"] = new FakeChatClient("""{"score": 90, "explanation": "Great"}""")
        };
        var options = new CalibratedJudgeOptions
        {
            Strategy = VotingStrategy.Weighted,
            MinimumJudgesRequired = 2,
            JudgeWeights = new Dictionary<string, double>
            {
                ["Failing"] = 5.0,
                ["JudgeB"] = 2.0,
                ["JudgeC"] = 1.0
            }
        };
        var judge = new CalibratedJudge(
            clients.Select(kv => (kv.Key, kv.Value)).ToArray(), options);
        var context = CreateSampleContext();

        // Act
        var result = await judge.EvaluateAsync(context,
            jn => new FaithfulnessMetric(clients[jn]));

        // Assert: Failing judge excluded. JudgeB=80*2, JudgeC=90*1 → (160+90)/3 = 83.33
        Assert.Equal(2, result.JudgeCount);
        Assert.InRange(result.Score, 83.0, 84.0);
    }
    
    [Fact]
    public async Task EvaluateAsync_WithUnanimousStrategy_DivergentScores_ThrowsInvalidOperation()
    {
        // Arrange
        var clients = new Dictionary<string, IChatClient>
        {
            ["J1"] = new FakeChatClient("""{"score": 30, "explanation": "Bad"}"""),
            ["J2"] = new FakeChatClient("""{"score": 90, "explanation": "Great"}""")
        };
        var options = new CalibratedJudgeOptions
        {
            Strategy = VotingStrategy.Unanimous,
            ConsensusTolerance = 10
        };
        var judge = new CalibratedJudge(
            clients.Select(kv => (kv.Key, kv.Value)).ToArray(), options);
        var context = CreateSampleContext();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            judge.EvaluateAsync(context, jn => new FaithfulnessMetric(clients[jn])));
    }
    
    [Fact]
    public async Task EvaluateAsync_WhenContinueOnJudgeFailureFalse_ThrowsOnFirstFailure()
    {
        // Arrange
        var failingClient = new FakeChatClient();
        failingClient.ThrowOnNextCall = true;
        failingClient.ThrowMessage = "Judge API error";
        
        var successClient = new FakeChatClient("""{"score": 85, "explanation": "Good"}""");
        
        var judges = new (string, IChatClient)[]
        {
            ("Failing", failingClient),
            ("Success", successClient)
        };
        var options = new CalibratedJudgeOptions { ContinueOnJudgeFailure = false };
        var calibratedJudge = new CalibratedJudge(judges, options);
        var context = CreateSampleContext();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            calibratedJudge.EvaluateAsync(context, jn =>
                new FaithfulnessMetric(jn == "Failing" ? failingClient : successClient)));
    }
    
    [Fact]
    public void CalibratedResult_Summary_And_JudgeBreakdown_FormatCorrectly()
    {
        // Arrange
        var result = new CalibratedResult
        {
            Score = 85.0,
            Agreement = 95.0,
            JudgeScores = new Dictionary<string, double> { ["J1"] = 80, ["J2"] = 90 },
            ConfidenceLower = 78.5,
            ConfidenceUpper = 91.5,
            StandardDeviation = 7.07,
            Strategy = VotingStrategy.Median,
            HasConsensus = true
        };

        // Assert Summary
        Assert.Contains("85.0", result.Summary);
        Assert.Contains("95%", result.Summary);
        Assert.Contains("✅", result.Summary);
        Assert.Contains("78.5", result.Summary);
        Assert.Contains("91.5", result.Summary);
        
        // Assert JudgeBreakdown
        Assert.Contains("J1", result.JudgeBreakdown);
        Assert.Contains("J2", result.JudgeBreakdown);
        Assert.Contains("80.0", result.JudgeBreakdown);
        Assert.Contains("90.0", result.JudgeBreakdown);
    }
    
    #endregion
    
    #region Factory & API Surface Tests

    [Fact]
    public void Create_StaticFactory_ReturnsConfiguredJudge()
    {
        // Arrange
        var client1 = new FakeChatClient("{}");
        var client2 = new FakeChatClient("{}");

        // Act — test both Create() overloads
        var judge1 = CalibratedJudge.Create(
            ("ModelA", client1), ("ModelB", client2));

        var customOptions = new CalibratedJudgeOptions { Strategy = VotingStrategy.Weighted };
        var judge2 = CalibratedJudge.Create(customOptions,
            ("ModelA", client1), ("ModelB", client2));

        // Assert
        Assert.Equal(2, judge1.JudgeNames.Count);
        Assert.Contains("ModelA", judge1.JudgeNames);
        Assert.Contains("ModelB", judge1.JudgeNames);
        Assert.Equal(VotingStrategy.Median, judge1.Options.Strategy); // default

        Assert.Equal(VotingStrategy.Weighted, judge2.Options.Strategy); // custom
        Assert.Equal(2, judge2.Judges.Count);
    }

    [Fact]
    public async Task EvaluateAsync_BelowMinimumJudges_ThrowsWithErrorDetails()
    {
        // Arrange — all judges fail but MinimumJudgesRequired = 2
        var fail1 = new FakeChatClient();
        fail1.ThrowOnNextCall = true;
        fail1.ThrowMessage = "Model overloaded";
        var fail2 = new FakeChatClient();
        fail2.ThrowOnNextCall = true;
        fail2.ThrowMessage = "Rate limited";

        var judges = new (string, IChatClient)[] { ("A", fail1), ("B", fail2) };
        var options = new CalibratedJudgeOptions { MinimumJudgesRequired = 2 };
        var judge = new CalibratedJudge(judges, options);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            judge.EvaluateAsync(CreateSampleContext(),
                jn => new FaithfulnessMetric(jn == "A" ? fail1 : fail2)));

        Assert.Contains("0 of 2", ex.Message);
        Assert.Contains("2 are required", ex.Message);
    }

    [Fact]
    public async Task EvaluateAsync_WithMedianStrategy_EvenJudges_AveragesMiddleTwo()
    {
        // Arrange — 4 judges: median of even count = average of middle 2
        var clients = new Dictionary<string, IChatClient>
        {
            ["J1"] = new FakeChatClient("""{"score": 60, "explanation": "Low"}"""),
            ["J2"] = new FakeChatClient("""{"score": 70, "explanation": "Lowish"}"""),
            ["J3"] = new FakeChatClient("""{"score": 80, "explanation": "Good"}"""),
            ["J4"] = new FakeChatClient("""{"score": 100, "explanation": "Perfect"}""")
        };
        var judges = clients.Select(kv => (kv.Key, kv.Value)).ToArray();
        var options = new CalibratedJudgeOptions { Strategy = VotingStrategy.Median };
        var judge = new CalibratedJudge(judges, options);

        // Act
        var result = await judge.EvaluateAsync(CreateSampleContext(),
            jn => new FaithfulnessMetric(clients[jn]));

        // Assert — median of [60,70,80,100] = (70+80)/2 = 75
        Assert.Equal(75, result.Score);
    }

    [Fact]
    public async Task EvaluateAsync_WithSingleJudge_ReturnsNullConfidenceInterval()
    {
        // Arrange — 1 judge, CI requires >= 2 scores
        var client = new FakeChatClient("""{"score": 90, "explanation": "Great"}""");
        var judges = new (string, IChatClient)[] { ("Solo", client) };
        var options = new CalibratedJudgeOptions { CalculateConfidenceInterval = true };
        var judge = new CalibratedJudge(judges, options);

        // Act
        var result = await judge.EvaluateAsync(CreateSampleContext(),
            _ => new FaithfulnessMetric(client));

        // Assert — CI should be null with only 1 score
        Assert.Null(result.ConfidenceLower);
        Assert.Null(result.ConfidenceUpper);
        Assert.Equal(0, result.StandardDeviation);
        Assert.Equal(100, result.Agreement); // single judge = 100% agreement
    }

    [Fact]
    public async Task EvaluateAsync_WithWeightedStrategy_NoWeightsConfigured_FallsBackToAverage()
    {
        // Arrange — Weighted strategy but no JudgeWeights set → should fall back to simple average
        var clients = new Dictionary<string, IChatClient>
        {
            ["J1"] = new FakeChatClient("""{"score": 80, "explanation": "Good"}"""),
            ["J2"] = new FakeChatClient("""{"score": 100, "explanation": "Great"}""")
        };
        var judges = clients.Select(kv => (kv.Key, kv.Value)).ToArray();
        var options = new CalibratedJudgeOptions
        {
            Strategy = VotingStrategy.Weighted,
            JudgeWeights = null   // no weights
        };
        var judge = new CalibratedJudge(judges, options);

        // Act
        var result = await judge.EvaluateAsync(CreateSampleContext(),
            jn => new FaithfulnessMetric(clients[jn]));

        // Assert — no weights → simple average: (80+100)/2 = 90
        Assert.Equal(90, result.Score);
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
