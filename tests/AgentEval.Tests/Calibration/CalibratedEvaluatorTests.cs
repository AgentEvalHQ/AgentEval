// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Calibration;
using AgentEval.Core;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Calibration;

/// <summary>
/// Unit tests for CalibratedEvaluator — multi-model criteria-based evaluation
/// that implements IEvaluator for drop-in use with MAFEvaluationHarness.
/// </summary>
public class CalibratedEvaluatorTests
{
    private const string GoodEvalJson = """
        {
            "criteriaResults": [
                {"criterion": "Is accurate", "met": true, "explanation": "Response is factually correct"},
                {"criterion": "Is concise", "met": true, "explanation": "Response is brief"}
            ],
            "overallScore": 85,
            "summary": "Good response overall",
            "improvements": ["Could add more detail"]
        }
        """;

    private const string MediumEvalJson = """
        {
            "criteriaResults": [
                {"criterion": "Is accurate", "met": true, "explanation": "Mostly accurate"},
                {"criterion": "Is concise", "met": false, "explanation": "Too verbose"}
            ],
            "overallScore": 65,
            "summary": "Average response",
            "improvements": ["Be more concise", "Stay focused"]
        }
        """;

    private const string LowEvalJson = """
        {
            "criteriaResults": [
                {"criterion": "Is accurate", "met": false, "explanation": "Contains errors"},
                {"criterion": "Is concise", "met": false, "explanation": "Rambling"}
            ],
            "overallScore": 30,
            "summary": "Poor response",
            "improvements": ["Fix factual errors"]
        }
        """;

    #region Core Aggregation

    [Fact]
    public async Task EvaluateAsync_WithMultipleJudges_AggregatesOverallScore()
    {
        // Arrange — 2 judges with scores 85 and 65, median = 75
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("JudgeA", new FakeChatClient(GoodEvalJson)),
                ("JudgeB", new FakeChatClient(MediumEvalJson))
            },
            new CalibratedJudgeOptions { Strategy = VotingStrategy.Median });

        // Act
        var result = await evaluator.EvaluateAsync(
            "What is AI?", "AI is artificial intelligence.",
            new[] { "Is accurate", "Is concise" });

        // Assert — median of 85 and 65 = (85+65)/2 = 75
        Assert.Equal(75, result.OverallScore);
    }

    [Fact]
    public async Task EvaluateAsync_WithMultipleJudges_MajorityVotesPerCriterion()
    {
        // Arrange — 3 judges: 2 say "Is accurate" met, 1 says not met → majority = met
        // "Is concise": 1 met, 2 not met → majority = not met
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("A", new FakeChatClient(GoodEvalJson)),   // accurate=met, concise=met
                ("B", new FakeChatClient(MediumEvalJson)), // accurate=met, concise=not met
                ("C", new FakeChatClient(LowEvalJson))     // accurate=not met, concise=not met
            });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate", "Is concise" });

        // Assert
        Assert.Equal(2, result.CriteriaResults.Count);

        var accurate = result.CriteriaResults.First(c => c.Criterion == "Is accurate");
        Assert.True(accurate.Met); // 2/3 say met → majority

        var concise = result.CriteriaResults.First(c => c.Criterion == "Is concise");
        Assert.False(concise.Met); // 1/3 say met → minority
    }

    [Fact]
    public async Task EvaluateAsync_WhenJudgesDisagree_CriterionMetByMajority()
    {
        // Arrange — 2 judges, tied on "Is accurate" (1 met, 1 not met) → not met (majority = >50%)
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("A", new FakeChatClient(GoodEvalJson)),  // accurate=met
                ("B", new FakeChatClient(LowEvalJson))    // accurate=not met
            });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate" });

        // Assert — 1/2 = 50% exactly, not > 50%, so NOT met
        var accurate = result.CriteriaResults.First(c => c.Criterion == "Is accurate");
        Assert.False(accurate.Met);
    }

    [Fact]
    public async Task EvaluateAsync_SummaryIncludesAgreementPercentage()
    {
        // Arrange
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("A", new FakeChatClient(GoodEvalJson)),
                ("B", new FakeChatClient(GoodEvalJson))
            });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate" });

        // Assert — summary should mention judge count and agreement
        Assert.Contains("2 judges", result.Summary);
        Assert.Contains("agreement", result.Summary);
        // Identical scores (85, 85) → stdDev=0, CV=0, agreement=100%
        Assert.Contains("100%", result.Summary);
    }

    [Fact]
    public async Task EvaluateAsync_MergesImprovementsFromAllJudges()
    {
        // Arrange — GoodEvalJson has "Could add more detail", MediumEvalJson has "Be more concise", "Stay focused"
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("A", new FakeChatClient(GoodEvalJson)),
                ("B", new FakeChatClient(MediumEvalJson))
            });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate" });

        // Assert — union of improvements, deduplicated
        Assert.Contains("Could add more detail", result.Improvements);
        Assert.Contains("Be more concise", result.Improvements);
        Assert.Contains("Stay focused", result.Improvements);
    }

    #endregion

    #region Resilience

    [Fact]
    public async Task EvaluateAsync_WhenOneJudgeFails_UsesRemainingJudges()
    {
        // Arrange
        var failingClient = new FakeChatClient();
        failingClient.ThrowOnNextCall = true;
        failingClient.ThrowMessage = "Model overloaded";

        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("Failing", failingClient),
                ("Good", new FakeChatClient(GoodEvalJson))
            },
            new CalibratedJudgeOptions { MinimumJudgesRequired = 1 });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate" });

        // Assert — should still get a result from the surviving judge
        Assert.Equal(85, result.OverallScore);
    }

    [Fact]
    public async Task EvaluateAsync_WhenAllJudgesFail_ThrowsInvalidOperation()
    {
        // Arrange
        var fail1 = new FakeChatClient();
        fail1.ThrowOnNextCall = true;
        fail1.ThrowMessage = "Error 1";

        var fail2 = new FakeChatClient();
        fail2.ThrowOnNextCall = true;
        fail2.ThrowMessage = "Error 2";

        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("A", fail1),
                ("B", fail2)
            },
            new CalibratedJudgeOptions { MinimumJudgesRequired = 1 });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            evaluator.EvaluateAsync("input", "output", new[] { "Is accurate" }));

        Assert.Contains("0 of 2", ex.Message);
        Assert.Contains("1 are required", ex.Message);
    }

    [Fact]
    public async Task EvaluateAsync_WhenContinueOnFailureFalse_ThrowsOnFirstFailure()
    {
        // Arrange
        var failingClient = new FakeChatClient();
        failingClient.ThrowOnNextCall = true;
        failingClient.ThrowMessage = "Judge crashed";

        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("Failing", failingClient),
                ("Good", new FakeChatClient(GoodEvalJson))
            },
            new CalibratedJudgeOptions { ContinueOnJudgeFailure = false });

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            evaluator.EvaluateAsync("input", "output", new[] { "Is accurate" }));
    }

    #endregion

    #region Voting Strategies

    [Fact]
    public async Task EvaluateAsync_WithMeanStrategy_ReturnsMeanScore()
    {
        // Arrange — scores 85 and 65, mean = 75
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("A", new FakeChatClient(GoodEvalJson)),   // 85
                ("B", new FakeChatClient(MediumEvalJson))  // 65
            },
            new CalibratedJudgeOptions { Strategy = VotingStrategy.Mean });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate" });

        // Assert — mean of 85 and 65 = 75
        Assert.Equal(75, result.OverallScore);
    }

    [Fact]
    public async Task EvaluateAsync_WithMedianStrategy_UsesMedianScore()
    {
        // Arrange — 3 judges: 85, 65, 30 → median = 65
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("A", new FakeChatClient(GoodEvalJson)),    // 85
                ("B", new FakeChatClient(MediumEvalJson)),  // 65
                ("C", new FakeChatClient(LowEvalJson))      // 30
            },
            new CalibratedJudgeOptions { Strategy = VotingStrategy.Median });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate" });

        // Assert — median of [30, 65, 85] = 65
        Assert.Equal(65, result.OverallScore);
    }

    [Fact]
    public async Task EvaluateAsync_WithWeightedStrategy_AppliesWeights()
    {
        // Arrange — score 85 (weight=3) and 65 (weight=1) → (85*3+65*1)/4 = 80
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("Heavy", new FakeChatClient(GoodEvalJson)),   // 85
                ("Light", new FakeChatClient(MediumEvalJson))  // 65
            },
            new CalibratedJudgeOptions
            {
                Strategy = VotingStrategy.Weighted,
                JudgeWeights = new Dictionary<string, double>
                {
                    ["Heavy"] = 3.0,
                    ["Light"] = 1.0
                }
            });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate" });

        // Assert — (85*3 + 65*1) / 4 = 320/4 = 80
        Assert.Equal(80, result.OverallScore);
    }

    [Fact]
    public async Task EvaluateAsync_WithMedianStrategy_EvenCount_AveragesMiddleTwo()
    {
        // Arrange — 4 judges: 30, 65, 85, 85 → median = (65+85)/2 = 75
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("A", new FakeChatClient(GoodEvalJson)),    // 85
                ("B", new FakeChatClient(MediumEvalJson)),  // 65
                ("C", new FakeChatClient(LowEvalJson)),     // 30
                ("D", new FakeChatClient(GoodEvalJson))     // 85
            },
            new CalibratedJudgeOptions { Strategy = VotingStrategy.Median });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate" });

        // Assert — median of [30, 65, 85, 85] = (65+85)/2 = 75
        Assert.Equal(75, result.OverallScore);
    }

    [Fact]
    public async Task EvaluateAsync_WithWeightedStrategy_MissingWeight_DefaultsToOne()
    {
        // Arrange — only "Heavy" has explicit weight (3.0); "Light" missing → defaults to 1.0
        // (85*3 + 65*1) / 4 = 80
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("Heavy", new FakeChatClient(GoodEvalJson)),   // 85
                ("Light", new FakeChatClient(MediumEvalJson))  // 65
            },
            new CalibratedJudgeOptions
            {
                Strategy = VotingStrategy.Weighted,
                JudgeWeights = new Dictionary<string, double>
                {
                    ["Heavy"] = 3.0
                    // "Light" intentionally omitted — should default to 1.0
                }
            });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate" });

        // Assert — (85*3 + 65*1) / 4 = 320/4 = 80
        Assert.Equal(80, result.OverallScore);
    }

    [Fact]
    public async Task EvaluateAsync_WhenCancelled_ThrowsOperationCancelled()
    {
        // Arrange
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[] { ("A", new FakeChatClient(GoodEvalJson)) });
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert — should propagate OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            evaluator.EvaluateAsync("input", "output", new[] { "Is accurate" }, cts.Token));
    }

    [Fact]
    public async Task EvaluateAsync_WithUnanimousStrategy_WhenConsensus_ReturnsAverage()
    {
        // Arrange — scores 85 and 85, tolerance 10 → consensus, returns mean = 85
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("A", new FakeChatClient(GoodEvalJson)),  // 85
                ("B", new FakeChatClient(GoodEvalJson))   // 85
            },
            new CalibratedJudgeOptions
            {
                Strategy = VotingStrategy.Unanimous,
                ConsensusTolerance = 10.0
            });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate" });

        // Assert — scores are identical, within tolerance → mean = 85
        Assert.Equal(85, result.OverallScore);
    }

    [Fact]
    public async Task EvaluateAsync_WithUnanimousStrategy_WhenNoConsensus_Throws()
    {
        // Arrange — scores 85 and 30, tolerance 10 → no consensus (spread = 55 > 10)
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("A", new FakeChatClient(GoodEvalJson)),  // 85
                ("B", new FakeChatClient(LowEvalJson))    // 30
            },
            new CalibratedJudgeOptions
            {
                Strategy = VotingStrategy.Unanimous,
                ConsensusTolerance = 10.0
            });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            evaluator.EvaluateAsync("input", "output", new[] { "Is accurate" }));

        Assert.Contains("consensus", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Integration & Construction

    [Fact]
    public void CalibratedEvaluator_ImplementsIEvaluator()
    {
        // Arrange & Act
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[] { ("A", new FakeChatClient(GoodEvalJson)) });

        // Assert — verify it's assignable to IEvaluator (drop-in replacement check)
        IEvaluator asInterface = evaluator;
        Assert.NotNull(asInterface);
        Assert.Single(evaluator.JudgeNames);
        Assert.Equal("A", evaluator.JudgeNames[0]);
    }

    [Fact]
    public void Constructor_WithEmptyJudges_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new CalibratedEvaluator(Array.Empty<(string, IChatClient)>()));
    }

    [Fact]
    public void Constructor_WithNullJudges_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new CalibratedEvaluator((IEnumerable<(string, IChatClient)>)null!));
    }

    [Fact]
    public async Task EvaluateAsync_WithSingleJudge_ReturnsJudgeResultDirectly()
    {
        // Arrange
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[] { ("Solo", new FakeChatClient(GoodEvalJson)) });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate", "Is concise" });

        // Assert — single judge = its own score, no aggregation
        Assert.Equal(85, result.OverallScore);
        Assert.Equal(2, result.CriteriaResults.Count);
        Assert.True(result.CriteriaResults[0].Met);
        Assert.True(result.CriteriaResults[1].Met);
    }

    [Fact]
    public async Task EvaluateAsync_CriterionExplanation_IncludesJudgeNames()
    {
        // Arrange
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("GPT-4o", new FakeChatClient(GoodEvalJson)),
                ("Claude", new FakeChatClient(MediumEvalJson))
            });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate" });

        // Assert — combined explanation should include judge names
        var accurate = result.CriteriaResults.First(c => c.Criterion == "Is accurate");
        Assert.Contains("GPT-4o", accurate.Explanation);
        Assert.Contains("Claude", accurate.Explanation);
        Assert.Contains("Majority vote", accurate.Explanation);
    }

    [Fact]
    public async Task EvaluateAsync_WhenCriterionMissingFromJudge_HandlesGracefully()
    {
        // Arrange — judges return "Is accurate" but we also ask for "Is polite" which no judge evaluates
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("A", new FakeChatClient(GoodEvalJson)),
                ("B", new FakeChatClient(MediumEvalJson))
            });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate", "Is polite" });

        // Assert — "Is polite" should be present but marked as not met
        Assert.Equal(2, result.CriteriaResults.Count);
        var polite = result.CriteriaResults.First(c => c.Criterion == "Is polite");
        Assert.False(polite.Met);
        Assert.Contains("No judges returned a result", polite.Explanation);
    }

    [Fact]
    public void Constructor_WithInvalidOptions_ThrowsOnValidation()
    {
        // Arrange — MaxParallelJudges = 0 violates Validate()
        var invalidOptions = new CalibratedJudgeOptions { MaxParallelJudges = 0 };

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new CalibratedEvaluator(
                new (string, IChatClient)[] { ("A", new FakeChatClient(GoodEvalJson)) },
                invalidOptions));
    }

    [Fact]
    public async Task EvaluateAsync_WithWeightedStrategy_NoWeightsConfigured_FallsBackToAverage()
    {
        // Arrange — Weighted strategy but JudgeWeights is null → falls back to simple average
        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ("A", new FakeChatClient(GoodEvalJson)),    // 85
                ("B", new FakeChatClient(MediumEvalJson))   // 65
            },
            new CalibratedJudgeOptions
            {
                Strategy = VotingStrategy.Weighted,
                JudgeWeights = null  // no weights → average fallback
            });

        // Act
        var result = await evaluator.EvaluateAsync(
            "input", "output", new[] { "Is accurate" });

        // Assert — no weights → simple average: (85+65)/2 = 75
        Assert.Equal(75, result.OverallScore);
    }

    #endregion
}
