// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using Xunit;

namespace AgentEval.Memory.Tests.Reporting;

public class BaselineComparerTests
{
    private readonly BaselineComparer _comparer = new();

    [Fact]
    public void Compare_TwoBaselines_CorrectDimensionComparisons()
    {
        var baselines = new List<MemoryBaseline>
        {
            CreateBaseline("bl-1", "Config A", 80, recall: 90, resilience: 70),
            CreateBaseline("bl-2", "Config B", 85, recall: 75, resilience: 95)
        };

        var comparison = _comparer.Compare(baselines);

        Assert.Equal(2, comparison.Baselines.Count);

        var recall = comparison.Dimensions.First(d => d.DimensionName == "Recall");
        Assert.Equal(90, recall.Scores["bl-1"]);
        Assert.Equal(75, recall.Scores["bl-2"]);
        Assert.Equal("bl-1", recall.BestBaselineId);

        var resilience = comparison.Dimensions.First(d => d.DimensionName == "Resilience");
        Assert.Equal("bl-2", resilience.BestBaselineId);
    }

    [Fact]
    public void Compare_ThreeBaselines_BestIdentifiedCorrectly()
    {
        var baselines = new List<MemoryBaseline>
        {
            CreateBaseline("bl-1", "A", 70),
            CreateBaseline("bl-2", "B", 90),
            CreateBaseline("bl-3", "C", 80)
        };

        var comparison = _comparer.Compare(baselines);
        Assert.Equal("bl-2", comparison.BestBaselineId);
    }

    [Fact]
    public void Compare_RadarChart_Has5PentagonAxes()
    {
        var baselines = new List<MemoryBaseline>
        {
            CreateBaseline("bl-1", "A", 80),
            CreateBaseline("bl-2", "B", 85)
        };

        var comparison = _comparer.Compare(baselines);

        Assert.Equal(5, comparison.RadarChart.Axes.Count);
        Assert.Contains("Recall", comparison.RadarChart.Axes);
        Assert.Contains("Resilience", comparison.RadarChart.Axes);
        Assert.Contains("Temporal", comparison.RadarChart.Axes);
        Assert.Contains("Persistence", comparison.RadarChart.Axes);
        Assert.Contains("Organization", comparison.RadarChart.Axes);
    }

    [Fact]
    public void Compare_RadarChart_SeriesMatchBaselineCount()
    {
        var baselines = new List<MemoryBaseline>
        {
            CreateBaseline("bl-1", "A", 80),
            CreateBaseline("bl-2", "B", 85),
            CreateBaseline("bl-3", "C", 90)
        };

        var comparison = _comparer.Compare(baselines);
        Assert.Equal(3, comparison.RadarChart.Series.Count);
    }

    [Fact]
    public void Compare_SingleBaseline_Works()
    {
        var baselines = new List<MemoryBaseline>
        {
            CreateBaseline("bl-1", "Solo", 82)
        };

        var comparison = _comparer.Compare(baselines);

        Assert.Single(comparison.Baselines);
        Assert.Equal("bl-1", comparison.BestBaselineId);
        Assert.Single(comparison.RadarChart.Series);
    }

    [Fact]
    public void Compare_EmptyList_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _comparer.Compare([]));
    }

    [Fact]
    public void Compare_NullList_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _comparer.Compare(null!));
    }

    [Fact]
    public void Compare_TiedScores_ReturnsFirstBaseline()
    {
        var baselines = new List<MemoryBaseline>
        {
            CreateBaseline("bl-1", "A", 85),
            CreateBaseline("bl-2", "B", 85)  // Same score
        };

        var comparison = _comparer.Compare(baselines);
        // MaxBy returns first element on ties
        Assert.Equal("bl-1", comparison.BestBaselineId);
    }

    [Fact]
    public void Compare_RadarChart_MissingDimensionsDefaultToZero()
    {
        var b1 = CreateBaseline("bl-1", "Full", 80);
        var b2 = CreateBaseline("bl-2", "Partial", 75);
        b2.DimensionScores.Remove("Persistence");

        var comparison = _comparer.Compare([b1, b2]);

        // b2's Persistence should default to 0 in radar values
        var b2Series = comparison.RadarChart.Series.First(s => s.Name == "Partial");
        var persIdx = comparison.RadarChart.Axes.ToList().IndexOf("Persistence");
        Assert.True(persIdx >= 0, "Persistence axis should exist");
        Assert.Equal(0, b2Series.Values[persIdx]);
    }

    [Fact]
    public void Compare_MissingDimensions_HandledGracefully()
    {
        var b1 = CreateBaseline("bl-1", "Full", 80);
        var b2 = CreateBaseline("bl-2", "Partial", 75);
        b2.DimensionScores.Remove("Persistence");
        b2.DimensionScores.Remove("Organization");

        var baselines = new List<MemoryBaseline> { b1, b2 };
        var comparison = _comparer.Compare(baselines);

        // Should still have all 5 dimensions (bl-1 has them all)
        Assert.Equal(5, comparison.Dimensions.Count);

        // Every dimension should include ALL baselines — baselines missing a dimension get score 0.
        // This keeps per-dimension tables and radar charts consistent (no silent omissions).
        var persistence = comparison.Dimensions.First(d => d.DimensionName == "Persistence");
        Assert.Equal(2, persistence.Scores.Count);
        Assert.True(persistence.Scores.ContainsKey("bl-1"));
        Assert.True(persistence.Scores.ContainsKey("bl-2"));
        Assert.Equal(0, persistence.Scores["bl-2"]); // bl-2 removed this dimension → defaults to 0
    }

    // --- Helpers ---

    private static MemoryBaseline CreateBaseline(
        string id, string name, double overall,
        double recall = 85, double resilience = 70,
        double temporal = 75, double persistence = 80,
        double organization = 78)
    {
        return new MemoryBaseline
        {
            Id = id,
            Name = name,
            Timestamp = DateTimeOffset.UtcNow,
            ConfigurationId = "test-config",
            AgentConfig = new AgentBenchmarkConfig { AgentName = "TestAgent" },
            Benchmark = new BenchmarkExecutionInfo { Preset = "Quick", Duration = TimeSpan.FromSeconds(5) },
            OverallScore = overall,
            Grade = "B",
            Stars = 4,
            CategoryResults = new Dictionary<string, CategoryScoreEntry>
            {
                ["Basic Retention"] = new() { Score = recall, Grade = "B", Skipped = false }
            },
            DimensionScores = new Dictionary<string, double>
            {
                ["Recall"] = recall,
                ["Resilience"] = resilience,
                ["Temporal"] = temporal,
                ["Persistence"] = persistence,
                ["Organization"] = organization
            },
            Recommendations = [],
            Tags = []
        };
    }
}
