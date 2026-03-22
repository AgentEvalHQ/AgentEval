using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.Evaluators;

public class MemoryBenchmarkResultTests
{
    [Fact]
    public void OverallScore_WeightedAverage_CalculatesCorrectly()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Test",
            Duration = TimeSpan.FromSeconds(5),
            CategoryResults =
            [
                new BenchmarkCategoryResult { CategoryName = "Cat1", Score = 100, Weight = 0.5, ScenarioType = BenchmarkScenarioType.BasicRetention, Duration = TimeSpan.FromSeconds(1) },
                new BenchmarkCategoryResult { CategoryName = "Cat2", Score = 60, Weight = 0.5, ScenarioType = BenchmarkScenarioType.NoiseResilience, Duration = TimeSpan.FromSeconds(1) }
            ]
        };

        Assert.Equal(80, result.OverallScore);
    }

    [Fact]
    public void Grade_GradeBoundaries_Correct()
    {
        Assert.Equal("A", MakeBenchmarkResult(90).Grade);
        Assert.Equal("A", MakeBenchmarkResult(100).Grade);
        Assert.Equal("B", MakeBenchmarkResult(80).Grade);
        Assert.Equal("B", MakeBenchmarkResult(89).Grade);
        Assert.Equal("C", MakeBenchmarkResult(70).Grade);
        Assert.Equal("D", MakeBenchmarkResult(60).Grade);
        Assert.Equal("F", MakeBenchmarkResult(59).Grade);
        Assert.Equal("F", MakeBenchmarkResult(0).Grade);
    }

    [Fact]
    public void Stars_StarBoundaries_Correct()
    {
        Assert.Equal(5, MakeBenchmarkResult(95).Stars);
        Assert.Equal(4, MakeBenchmarkResult(80).Stars);
        Assert.Equal(3, MakeBenchmarkResult(65).Stars);
        Assert.Equal(2, MakeBenchmarkResult(45).Stars);
        Assert.Equal(1, MakeBenchmarkResult(30).Stars);
    }

    [Fact]
    public void Passed_AtThreshold_ReturnsTrue()
    {
        Assert.True(MakeBenchmarkResult(70).Passed);
        Assert.True(MakeBenchmarkResult(100).Passed);
        Assert.False(MakeBenchmarkResult(69).Passed);
        Assert.False(MakeBenchmarkResult(0).Passed);
    }

    [Fact]
    public void WeakCategories_BelowThreshold_Listed()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Test",
            Duration = TimeSpan.FromSeconds(5),
            CategoryResults =
            [
                new BenchmarkCategoryResult { CategoryName = "Good", Score = 90, Weight = 0.4, ScenarioType = BenchmarkScenarioType.BasicRetention, Duration = TimeSpan.FromSeconds(1) },
                new BenchmarkCategoryResult { CategoryName = "Bad", Score = 40, Weight = 0.3, ScenarioType = BenchmarkScenarioType.NoiseResilience, Duration = TimeSpan.FromSeconds(1) },
                new BenchmarkCategoryResult { CategoryName = "OK", Score = 75, Weight = 0.3, ScenarioType = BenchmarkScenarioType.TemporalReasoning, Duration = TimeSpan.FromSeconds(1) }
            ]
        };

        Assert.Single(result.WeakCategories);
        Assert.Equal("Bad", result.WeakCategories[0]);
    }

    [Fact]
    public void Recommendations_ForWeakCategories_GeneratesActionable()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Test",
            Duration = TimeSpan.FromSeconds(5),
            CategoryResults =
            [
                new BenchmarkCategoryResult { CategoryName = "Basic Retention", Score = 50, Weight = 0.5, ScenarioType = BenchmarkScenarioType.BasicRetention, Duration = TimeSpan.FromSeconds(1) },
                new BenchmarkCategoryResult { CategoryName = "Temporal Reasoning", Score = 40, Weight = 0.5, ScenarioType = BenchmarkScenarioType.TemporalReasoning, Duration = TimeSpan.FromSeconds(1) }
            ]
        };

        Assert.Equal(2, result.Recommendations.Count);
        Assert.Contains("temporal", result.Recommendations[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retention", result.Recommendations[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recommendations_AllGood_EncouragingMessage()
    {
        var result = MakeBenchmarkResult(95);

        Assert.Single(result.Recommendations);
        Assert.Contains("performing well", result.Recommendations[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BenchmarkCategoryResult_Stars_CalculatesCorrectly()
    {
        var cat = new BenchmarkCategoryResult
        {
            CategoryName = "Test",
            Score = 85,
            Weight = 0.5,
            ScenarioType = BenchmarkScenarioType.BasicRetention,
            Duration = TimeSpan.FromSeconds(1)
        };

        Assert.Equal(4, cat.Stars);
    }

    [Fact]
    public void MemoryBenchmark_QuickPreset_Has3Categories()
    {
        var bench = MemoryBenchmark.Quick;
        Assert.Equal("Quick", bench.Name);
        Assert.Equal(3, bench.Categories.Count);
        Assert.Equal(1.0, bench.Categories.Sum(c => c.Weight), 2);
    }

    [Fact]
    public void MemoryBenchmark_StandardPreset_Has7Categories()
    {
        var bench = MemoryBenchmark.Standard;
        Assert.Equal("Standard", bench.Name);
        Assert.Equal(7, bench.Categories.Count);
        Assert.Equal(1.0, bench.Categories.Sum(c => c.Weight), 2);
    }

    [Fact]
    public void MemoryBenchmark_FullPreset_Has9Categories()
    {
        var bench = MemoryBenchmark.Full;
        Assert.Equal("Full", bench.Name);
        Assert.Equal(9, bench.Categories.Count);
        Assert.Equal(1.0, bench.Categories.Sum(c => c.Weight), 2);
    }

    [Fact]
    public void OverallScore_SkippedCategoriesExcluded_RenormalizesWeights()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Test",
            Duration = TimeSpan.FromSeconds(5),
            CategoryResults =
            [
                new BenchmarkCategoryResult { CategoryName = "Good", Score = 100, Weight = 0.5, ScenarioType = BenchmarkScenarioType.BasicRetention, Duration = TimeSpan.FromSeconds(1) },
                new BenchmarkCategoryResult { CategoryName = "Skipped", Score = 0, Weight = 0.5, ScenarioType = BenchmarkScenarioType.CrossSession, Duration = TimeSpan.Zero, Skipped = true, SkipReason = "Not supported" }
            ]
        };

        // Skipped excluded → only Good remains → renormalized weight 0.5/0.5 = 1.0 → score = 100
        Assert.Equal(100, result.OverallScore);
    }

    [Fact]
    public void WeakCategories_SkippedExcluded_NotListed()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Test",
            Duration = TimeSpan.FromSeconds(5),
            CategoryResults =
            [
                new BenchmarkCategoryResult { CategoryName = "Good", Score = 90, Weight = 0.5, ScenarioType = BenchmarkScenarioType.BasicRetention, Duration = TimeSpan.FromSeconds(1) },
                new BenchmarkCategoryResult { CategoryName = "Skipped", Score = 0, Weight = 0.5, ScenarioType = BenchmarkScenarioType.CrossSession, Duration = TimeSpan.Zero, Skipped = true, SkipReason = "Not supported" }
            ]
        };

        Assert.Empty(result.WeakCategories);
        Assert.Single(result.SkippedCategories);
        Assert.Equal("Skipped", result.SkippedCategories[0]);
    }

    [Fact]
    public void Recommendations_SkippedCategories_NoteSkipReason()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Test",
            Duration = TimeSpan.FromSeconds(5),
            CategoryResults =
            [
                new BenchmarkCategoryResult { CategoryName = "Good", Score = 90, Weight = 0.5, ScenarioType = BenchmarkScenarioType.BasicRetention, Duration = TimeSpan.FromSeconds(1) },
                new BenchmarkCategoryResult { CategoryName = "Cross-Session", Score = 0, Weight = 0.5, ScenarioType = BenchmarkScenarioType.CrossSession, Duration = TimeSpan.Zero, Skipped = true, SkipReason = "Agent does not implement ISessionResettableAgent" }
            ]
        };

        // Should have 1 recommendation for the skipped category + 1 encouraging for the passing category
        Assert.Contains(result.Recommendations, r => r.Contains("skipped", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Recommendations, r => r.Contains("performing well", StringComparison.OrdinalIgnoreCase));
    }

    // Helper: creates a benchmark result with a single category at the given score (weight=1.0)
    private static MemoryBenchmarkResult MakeBenchmarkResult(double score) => new()
    {
        BenchmarkName = "Test",
        Duration = TimeSpan.FromSeconds(1),
        CategoryResults =
        [
            new BenchmarkCategoryResult
            {
                CategoryName = "Main",
                Score = score,
                Weight = 1.0,
                ScenarioType = BenchmarkScenarioType.BasicRetention,
                Duration = TimeSpan.FromSeconds(1)
            }
        ]
    };
}
