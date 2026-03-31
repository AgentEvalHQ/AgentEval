using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.Evaluators;

public class ReducerEvaluationResultTests
{
    [Fact]
    public void FidelityScore_WithAllRetained_Returns100()
    {
        var result = new ReducerEvaluationResult
        {
            ScenarioName = "test",
            FactResults =
            [
                new ReducerFactResult { Fact = MemoryFact.Create("fact1"), Retained = true, Score = 95 },
                new ReducerFactResult { Fact = MemoryFact.Create("fact2"), Retained = true, Score = 90 },
                new ReducerFactResult { Fact = MemoryFact.Create("fact3"), Retained = true, Score = 85 }
            ],
            Duration = TimeSpan.FromSeconds(1)
        };

        Assert.Equal(100, result.FidelityScore);
        Assert.Equal(3, result.RetainedCount);
        Assert.Equal(0, result.LostCount);
        Assert.True(result.Passed);
        Assert.False(result.HasCriticalLoss);
    }

    [Fact]
    public void FidelityScore_WithPartialRetention_CalculatesCorrectly()
    {
        var result = new ReducerEvaluationResult
        {
            ScenarioName = "test",
            FactResults =
            [
                new ReducerFactResult { Fact = MemoryFact.Create("fact1"), Retained = true, Score = 95 },
                new ReducerFactResult { Fact = MemoryFact.Create("fact2"), Retained = false, Score = 30 },
                new ReducerFactResult { Fact = MemoryFact.Create("fact3"), Retained = true, Score = 85 }
            ],
            Duration = TimeSpan.FromSeconds(1)
        };

        // 2/3 ≈ 66.67%
        Assert.True(result.FidelityScore > 66 && result.FidelityScore < 67);
        Assert.Equal(2, result.RetainedCount);
        Assert.Equal(1, result.LostCount);
        Assert.False(result.Passed); // Below 80% threshold
    }

    [Fact]
    public void HasCriticalLoss_WhenHighImportanceFactLost_ReturnsTrue()
    {
        var result = new ReducerEvaluationResult
        {
            ScenarioName = "test",
            FactResults =
            [
                new ReducerFactResult { Fact = MemoryFact.Create("allergy", "medical", 100), Retained = false, Score = 10 },
                new ReducerFactResult { Fact = MemoryFact.Create("preference"), Retained = true, Score = 90 }
            ],
            Duration = TimeSpan.FromSeconds(1)
        };

        Assert.True(result.HasCriticalLoss);
        Assert.False(result.Passed); // Critical loss means not passed
    }

    [Fact]
    public void LostFacts_OrderedByImportanceDescending()
    {
        var result = new ReducerEvaluationResult
        {
            ScenarioName = "test",
            FactResults =
            [
                new ReducerFactResult { Fact = MemoryFact.Create("low", "misc", 20), Retained = false, Score = 10 },
                new ReducerFactResult { Fact = MemoryFact.Create("high", "medical", 100), Retained = false, Score = 5 },
                new ReducerFactResult { Fact = MemoryFact.Create("medium", "schedule", 60), Retained = false, Score = 15 }
            ],
            Duration = TimeSpan.FromSeconds(1)
        };

        Assert.Equal(3, result.LostFacts.Count);
        Assert.Equal("high", result.LostFacts[0].Content);
        Assert.Equal("medium", result.LostFacts[1].Content);
        Assert.Equal("low", result.LostFacts[2].Content);
    }
}
