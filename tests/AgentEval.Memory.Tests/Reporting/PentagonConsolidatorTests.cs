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

    [Fact]
    public void Consolidate_ConflictResolution_FoldsIntoTemporal()
    {
        var categories = new List<BenchmarkCategoryResult>
        {
            Cat(BenchmarkScenarioType.TemporalReasoning, 80),
            Cat(BenchmarkScenarioType.FactUpdateHandling, 70),
            Cat(BenchmarkScenarioType.ConflictResolution, 60)
        };

        var result = PentagonConsolidator.Consolidate(categories);

        Assert.True(result.ContainsKey("Temporal"));
        Assert.Equal(70, result["Temporal"]); // avg(80, 70, 60) = 70
    }

    [Fact]
    public void Consolidate_MultiSessionReasoning_FoldsIntoPersistence()
    {
        var categories = new List<BenchmarkCategoryResult>
        {
            Cat(BenchmarkScenarioType.CrossSession, 90),
            Cat(BenchmarkScenarioType.MultiSessionReasoning, 50)
        };

        var result = PentagonConsolidator.Consolidate(categories);

        Assert.True(result.ContainsKey("Persistence"));
        Assert.Equal(70, result["Persistence"]); // avg(90, 50) = 70
    }

    [Fact]
    public void Consolidate_All11Categories_Returns5Axes_Correct()
    {
        var categories = CreateAll11Categories(
            retention: 90, temporal: 80, noise: 70, depth: 88,
            updates: 73, multiTopic: 80, crossSession: 85, reducer: 68,
            abstention: 60, conflictResolution: 55, multiSessionReasoning: 75);

        var result = PentagonConsolidator.Consolidate(categories);

        Assert.Equal(5, result.Count);
        Assert.Equal((90 + 88) / 2.0, result["Recall"]);
        Assert.Equal((70 + 68) / 2.0, result["Resilience"]);
        Assert.Equal((80 + 73 + 55) / 3.0, result["Temporal"]); // 3-way avg with ConflictResolution
        Assert.Equal((85 + 75) / 2.0, result["Persistence"]);   // avg with MultiSessionReasoning
        Assert.Equal((80 + 60) / 2.0, result["Organization"]);  // avg with Abstention
    }

    // ═══════════════════════════════════════════════════════════════
    // New category variation tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Consolidate_NewCategoriesAtExtremes_HandledCorrectly()
    {
        // All new categories at 0, old categories at 100
        var categories = CreateAll11Categories(
            retention: 100, temporal: 100, noise: 100, depth: 100,
            updates: 100, multiTopic: 100, crossSession: 100, reducer: 100,
            abstention: 0, conflictResolution: 0, multiSessionReasoning: 0);

        var result = PentagonConsolidator.Consolidate(categories);

        Assert.Equal(100, result["Recall"]);          // (100+100)/2
        Assert.Equal(100, result["Resilience"]);       // (100+100)/2
        Assert.Equal((100 + 100 + 0) / 3.0, result["Temporal"]);  // ConflictResolution=0 drags down
        Assert.Equal((100 + 0) / 2.0, result["Persistence"]);     // MultiSession=0 drags down
        Assert.Equal((100 + 0) / 2.0, result["Organization"]);    // Abstention=0 drags down
    }

    [Fact]
    public void Consolidate_OnlyNewCategories_ReturnsPartialAxes()
    {
        // Only the 3 new categories present
        var categories = new List<BenchmarkCategoryResult>
        {
            Cat(BenchmarkScenarioType.Abstention, 70),
            Cat(BenchmarkScenarioType.ConflictResolution, 80),
            Cat(BenchmarkScenarioType.MultiSessionReasoning, 60)
        };

        var result = PentagonConsolidator.Consolidate(categories);

        // Should produce 3 axes: Organization (Abstention), Temporal (ConflictResolution), Persistence (MultiSession)
        Assert.Equal(70, result["Organization"]);
        Assert.Equal(80, result["Temporal"]);
        Assert.Equal(60, result["Persistence"]);
        // Recall and Resilience should NOT be present (no source categories)
        Assert.False(result.ContainsKey("Recall"));
        Assert.False(result.ContainsKey("Resilience"));
    }

    [Fact]
    public void Consolidate_SkippedNewCategories_ExcludedFromAverage()
    {
        var categories = new List<BenchmarkCategoryResult>
        {
            Cat(BenchmarkScenarioType.MultiTopic, 80),
            Cat(BenchmarkScenarioType.Abstention, 40, skipped: true), // Skipped — should not affect Organization
            Cat(BenchmarkScenarioType.TemporalReasoning, 70),
            Cat(BenchmarkScenarioType.FactUpdateHandling, 60),
            Cat(BenchmarkScenarioType.ConflictResolution, 50, skipped: true) // Skipped
        };

        var result = PentagonConsolidator.Consolidate(categories);

        Assert.Equal(80, result["Organization"]); // Only MultiTopic, Abstention is skipped
        Assert.Equal((70 + 60) / 2.0, result["Temporal"]); // ConflictResolution is skipped
    }

    // --- Helpers ---

    private static BenchmarkCategoryResult Cat(BenchmarkScenarioType type, double score, bool skipped = false)
        => new()
        {
            CategoryName = type.ToString(),
            Score = score,
            Weight = 0.09,
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

    private static List<BenchmarkCategoryResult> CreateAll11Categories(
        double retention, double temporal, double noise, double depth,
        double updates, double multiTopic, double crossSession, double reducer,
        double abstention, double conflictResolution, double multiSessionReasoning)
    {
        var list = CreateAll8Categories(retention, temporal, noise, depth, updates, multiTopic, crossSession, reducer);
        list.Add(Cat(BenchmarkScenarioType.Abstention, abstention));
        list.Add(Cat(BenchmarkScenarioType.ConflictResolution, conflictResolution));
        list.Add(Cat(BenchmarkScenarioType.MultiSessionReasoning, multiSessionReasoning));
        return list;
    }
}
