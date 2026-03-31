// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using static AgentEval.Memory.Models.MemoryBenchmarkResult;
using Xunit;

namespace AgentEval.Memory.Tests.Reporting;

public class BaselineExtensionsTests
{
    private static readonly AgentBenchmarkConfig TestConfig = new()
    {
        AgentName = "TestAgent",
        ModelId = "gpt-4o",
        ReducerStrategy = "SlidingWindow(50)",
        MemoryProvider = "InMemory"
    };

    [Fact]
    public void ToBaseline_PopulatesAllFields()
    {
        var result = CreateBenchmarkResult();

        var baseline = result.ToBaseline("Test v1", TestConfig, description: "Test run");

        Assert.StartsWith("bl-", baseline.Id);
        Assert.Equal("Test v1", baseline.Name);
        Assert.Equal("Test run", baseline.Description);
        Assert.Equal(result.OverallScore, baseline.OverallScore);
        Assert.Equal(result.Grade, baseline.Grade);
        Assert.Equal(result.Stars, baseline.Stars);
        Assert.Equal(result.BenchmarkName, baseline.Benchmark.Preset);
        Assert.Equal(result.Duration, baseline.Benchmark.Duration);
        Assert.Equal("TestAgent", baseline.AgentConfig.AgentName);
    }

    [Fact]
    public void ToBaseline_ConfigurationId_MatchesConfig()
    {
        var result = CreateBenchmarkResult();
        var baseline = result.ToBaseline("Test", TestConfig);

        Assert.Equal(TestConfig.ConfigurationId, baseline.ConfigurationId);
    }

    [Fact]
    public void ToBaseline_DimensionScores_ComputedViaPentagon()
    {
        var result = CreateBenchmarkResult();
        var baseline = result.ToBaseline("Test", TestConfig);

        // Quick preset has 3 categories → 3 axes
        Assert.True(baseline.DimensionScores.Count >= 3);
        Assert.True(baseline.DimensionScores.ContainsKey("Recall"));
    }

    [Fact]
    public void ToBaseline_SkippedCategories_Preserved()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Full",
            Duration = TimeSpan.FromSeconds(10),
            CategoryResults =
            [
                new BenchmarkCategoryResult
                {
                    CategoryName = "Cross-Session",
                    Score = 0,
                    Weight = 0.15,
                    ScenarioType = BenchmarkScenarioType.CrossSession,
                    Duration = TimeSpan.Zero,
                    Skipped = true,
                    SkipReason = "No ISessionResettableAgent"
                }
            ]
        };

        var baseline = result.ToBaseline("Test", TestConfig);

        Assert.True(baseline.CategoryResults["Cross-Session"].Skipped);
    }

    [Fact]
    public void ToBaseline_Tags_DefaultsToEmpty()
    {
        var result = CreateBenchmarkResult();
        var baseline = result.ToBaseline("Test", TestConfig);

        Assert.Empty(baseline.Tags);
    }

    [Fact]
    public void ToBaseline_Tags_PassedThrough()
    {
        var result = CreateBenchmarkResult();
        var baseline = result.ToBaseline("Test", TestConfig, tags: ["prod", "v2.1"]);

        Assert.Equal(2, baseline.Tags.Count);
        Assert.Contains("prod", baseline.Tags);
    }

    [Fact]
    public void ToBaseline_CategoryGrade_MatchesBenchmarkResultGrading()
    {
        // MemoryBenchmarkResult.Grade uses: >=90 → A, >=80 → B, >=70 → C, >=60 → D, else F
        // MemoryBenchmarkResult.ComputeGrade must match exactly
        Assert.Equal("A", MemoryBenchmarkResult.ComputeGrade(95));
        Assert.Equal("A", MemoryBenchmarkResult.ComputeGrade(90));
        Assert.Equal("B", MemoryBenchmarkResult.ComputeGrade(85));  // NOT "A-"
        Assert.Equal("B", MemoryBenchmarkResult.ComputeGrade(80));
        Assert.Equal("C", MemoryBenchmarkResult.ComputeGrade(75));  // NOT "B-"
        Assert.Equal("C", MemoryBenchmarkResult.ComputeGrade(70));
        Assert.Equal("D", MemoryBenchmarkResult.ComputeGrade(65));
        Assert.Equal("D", MemoryBenchmarkResult.ComputeGrade(60));
        Assert.Equal("F", MemoryBenchmarkResult.ComputeGrade(55));
        Assert.Equal("F", MemoryBenchmarkResult.ComputeGrade(0));
    }

    [Fact]
    public void ToBaseline_UniqueIds_GeneratedEachCall()
    {
        var result = CreateBenchmarkResult();
        var baseline1 = result.ToBaseline("Test1", TestConfig);
        var baseline2 = result.ToBaseline("Test2", TestConfig);

        Assert.NotEqual(baseline1.Id, baseline2.Id);
    }

    [Fact]
    public void ToBaseline_Preset_PopulatedFromBenchmarkName()
    {
        var result = CreateBenchmarkResult();
        var baseline = result.ToBaseline("Test", TestConfig);

        Assert.Equal("Quick", baseline.Benchmark.Preset);
    }

    [Fact]
    public void ToBaseline_Duration_PopulatedFromResult()
    {
        var result = CreateBenchmarkResult();
        var baseline = result.ToBaseline("Test", TestConfig);

        Assert.Equal(TimeSpan.FromSeconds(5), baseline.Benchmark.Duration);
    }

    [Theory]
    [InlineData(90.0, "A")]
    [InlineData(89.999, "B")]   // Just below A threshold — must be B, not A
    [InlineData(80.0, "B")]
    [InlineData(79.999, "C")]   // Floating point boundary
    [InlineData(70.0, "C")]
    [InlineData(69.999, "D")]
    [InlineData(60.0, "D")]
    [InlineData(59.999, "F")]
    [InlineData(0, "F")]
    [InlineData(-1, "F")]       // Negative score
    [InlineData(100, "A")]
    [InlineData(150, "A")]      // Out-of-range high
    public void ComputeGrade_BoundaryValues(double score, string expectedGrade)
    {
        Assert.Equal(expectedGrade, MemoryBenchmarkResult.ComputeGrade(score));
    }

    [Fact]
    public void ToBaseline_EmptyRecommendations_NoRecommendationInCategory()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Quick",
            Duration = TimeSpan.FromSeconds(1),
            CategoryResults =
            [
                new BenchmarkCategoryResult
                {
                    CategoryName = "Basic Retention",
                    Score = 95,   // High score — no recommendation generated
                    Weight = 1.0,
                    ScenarioType = BenchmarkScenarioType.BasicRetention,
                    Duration = TimeSpan.FromSeconds(1)
                }
            ]
        };

        var baseline = result.ToBaseline("Test", TestConfig);
        Assert.Null(baseline.CategoryResults["Basic Retention"].Recommendation);
    }

    // --- Helpers ---

    private static MemoryBenchmarkResult CreateBenchmarkResult() => new()
    {
        BenchmarkName = "Quick",
        Duration = TimeSpan.FromSeconds(5),
        CategoryResults =
        [
            new BenchmarkCategoryResult
            {
                CategoryName = "Basic Retention",
                Score = 92,
                Weight = 0.4,
                ScenarioType = BenchmarkScenarioType.BasicRetention,
                Duration = TimeSpan.FromSeconds(2)
            },
            new BenchmarkCategoryResult
            {
                CategoryName = "Temporal Reasoning",
                Score = 78,
                Weight = 0.3,
                ScenarioType = BenchmarkScenarioType.TemporalReasoning,
                Duration = TimeSpan.FromSeconds(1.5)
            },
            new BenchmarkCategoryResult
            {
                CategoryName = "Noise Resilience",
                Score = 65,
                Weight = 0.3,
                ScenarioType = BenchmarkScenarioType.NoiseResilience,
                Duration = TimeSpan.FromSeconds(1.5)
            }
        ]
    };
}
