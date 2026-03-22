// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using static AgentEval.Memory.Models.MemoryBenchmarkResult;
using Xunit;

namespace AgentEval.Memory.Tests.Reporting;

public class PentagonConsolidatorTests
{
    [Fact]
    public void Consolidate_All8Categories_Returns5Axes()
    {
        var categories = CreateAll8Categories(
            retention: 90, temporal: 80, noise: 70, depth: 88,
            updates: 73, multiTopic: 80, crossSession: 85, reducer: 68);

        var result = PentagonConsolidator.Consolidate(categories);

        Assert.Equal(5, result.Count);
        Assert.Equal((90 + 88) / 2.0, result["Recall"]);       // avg(retention, depth)
        Assert.Equal((70 + 68) / 2.0, result["Resilience"]);   // avg(noise, reducer)
        Assert.Equal((80 + 73) / 2.0, result["Temporal"]);     // avg(temporal, updates)
        Assert.Equal(85, result["Persistence"]);                // crossSession 1:1
        Assert.Equal(80, result["Organization"]);               // multiTopic 1:1
    }

    [Fact]
    public void Consolidate_CrossSessionSkipped_PersistenceOmitted()
    {
        var categories = new List<BenchmarkCategoryResult>
        {
            Cat(BenchmarkScenarioType.BasicRetention, 90),
            Cat(BenchmarkScenarioType.NoiseResilience, 70),
            Cat(BenchmarkScenarioType.CrossSession, 0, skipped: true)
        };

        var result = PentagonConsolidator.Consolidate(categories);

        Assert.False(result.ContainsKey("Persistence"));
        Assert.True(result.ContainsKey("Recall"));
        Assert.True(result.ContainsKey("Resilience"));
    }

    [Fact]
    public void Consolidate_OnlyBasicRetention_RecallEqualsRetention()
    {
        var categories = new List<BenchmarkCategoryResult>
        {
            Cat(BenchmarkScenarioType.BasicRetention, 85)
        };

        var result = PentagonConsolidator.Consolidate(categories);

        Assert.Equal(85, result["Recall"]);
    }

    [Fact]
    public void Consolidate_QuickPreset_Returns3Axes()
    {
        // Quick has: BasicRetention, TemporalReasoning, NoiseResilience
        var categories = new List<BenchmarkCategoryResult>
        {
            Cat(BenchmarkScenarioType.BasicRetention, 90),
            Cat(BenchmarkScenarioType.TemporalReasoning, 80),
            Cat(BenchmarkScenarioType.NoiseResilience, 70)
        };

        var result = PentagonConsolidator.Consolidate(categories);

        Assert.Equal(3, result.Count);
        Assert.Equal(90, result["Recall"]);        // Only BasicRetention, no depth
        Assert.Equal(70, result["Resilience"]);    // Only noise, no reducer
        Assert.Equal(80, result["Temporal"]);      // Only temporal, no updates
    }

    [Fact]
    public void Consolidate_EmptyList_ReturnsEmpty()
    {
        var result = PentagonConsolidator.Consolidate([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Consolidate_AllSkipped_ReturnsEmpty()
    {
        var categories = new List<BenchmarkCategoryResult>
        {
            Cat(BenchmarkScenarioType.BasicRetention, 0, skipped: true),
            Cat(BenchmarkScenarioType.CrossSession, 0, skipped: true)
        };

        var result = PentagonConsolidator.Consolidate(categories);
        Assert.Empty(result);
    }

    [Fact]
    public void Consolidate_BothPairedCategoriesSkipped_AxisOmitted()
    {
        // Both BasicRetention AND ReachBackDepth skipped → "Recall" axis should be absent
        var categories = new List<BenchmarkCategoryResult>
        {
            Cat(BenchmarkScenarioType.BasicRetention, 0, skipped: true),
            Cat(BenchmarkScenarioType.ReachBackDepth, 0, skipped: true),
            Cat(BenchmarkScenarioType.MultiTopic, 80)  // Organization should still exist
        };

        var result = PentagonConsolidator.Consolidate(categories);

        Assert.False(result.ContainsKey("Recall"));
        Assert.True(result.ContainsKey("Organization"));
        Assert.Equal(80, result["Organization"]);
    }

    [Fact]
    public void Consolidate_ExtremeValues_PassedThrough()
    {
        var categories = new List<BenchmarkCategoryResult>
        {
            Cat(BenchmarkScenarioType.BasicRetention, 0),     // Minimum valid
            Cat(BenchmarkScenarioType.ReachBackDepth, 100),   // Maximum valid
        };

        var result = PentagonConsolidator.Consolidate(categories);
        Assert.Equal(50, result["Recall"]); // avg(0, 100) = 50
    }

    // --- Helpers ---

    private static BenchmarkCategoryResult Cat(BenchmarkScenarioType type, double score, bool skipped = false)
        => new()
        {
            CategoryName = type.ToString(),
            Score = score,
            Weight = 0.125,
            ScenarioType = type,
            Duration = TimeSpan.FromSeconds(1),
            Skipped = skipped,
            SkipReason = skipped ? "Test skip" : null
        };

    private static List<BenchmarkCategoryResult> CreateAll8Categories(
        double retention, double temporal, double noise, double depth,
        double updates, double multiTopic, double crossSession, double reducer)
        =>
        [
            Cat(BenchmarkScenarioType.BasicRetention, retention),
            Cat(BenchmarkScenarioType.TemporalReasoning, temporal),
            Cat(BenchmarkScenarioType.NoiseResilience, noise),
            Cat(BenchmarkScenarioType.ReachBackDepth, depth),
            Cat(BenchmarkScenarioType.FactUpdateHandling, updates),
            Cat(BenchmarkScenarioType.MultiTopic, multiTopic),
            Cat(BenchmarkScenarioType.CrossSession, crossSession),
            Cat(BenchmarkScenarioType.ReducerFidelity, reducer)
        ];
}
