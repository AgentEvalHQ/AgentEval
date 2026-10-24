// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using Xunit;

namespace AgentEval.Memory.Tests.Reporting;

/// <summary>
/// A baseline that did not measure a dimension (a skipped category, an agent without session reset, a benchmark with
/// other dimension names) has no score for it in the comparison: no 0 that loses, or wins, the dimension, and no 0 on
/// the radar (T09).
/// </summary>
public class BaselineComparerNotMeasuredTests
{
    private readonly BaselineComparer _comparer = new();

    [Fact]
    public void ADimensionABaselineDidNotMeasure_HasNoScoreForIt_AndNamesIt()
    {
        var b1 = Baseline("bl-1", "Full", Pentagon());
        var b2 = Baseline("bl-2", "Partial", Pentagon(without: "Persistence"));

        var persistence = _comparer.Compare([b1, b2]).Dimensions.Single(d => d.DimensionName == "Persistence");

        Assert.False(persistence.Scores.ContainsKey("bl-2"));
        Assert.Equal(["bl-2"], persistence.NotMeasuredBy);
        Assert.Equal(80, persistence.Scores["bl-1"]);
    }

    [Fact]
    public void ABaselineThatDidNotMeasureADimension_DoesNotWinIt_OverAMeasuredZero()
    {
        // deepseek never measured Abstention; glm measured 0. MaxBy over {deepseek: 0, glm: 0} gave it to deepseek.
        var deepseek = Baseline("bl-deepseek", "deepseek", new() { ["Recall"] = 70 });
        var glm = Baseline("bl-glm", "glm", new() { ["Recall"] = 60, ["Abstention"] = 0 });

        var abstention = _comparer.Compare([deepseek, glm]).Dimensions.Single(d => d.DimensionName == "Abstention");

        Assert.Equal("bl-glm", abstention.BestBaselineId);
        Assert.Equal(["bl-deepseek"], abstention.NotMeasuredBy);
    }

    [Fact]
    public void TheRadar_LeavesOutAnAxisOnlySomeBaselinesMeasured_AndSaysSo()
    {
        var b1 = Baseline("bl-1", "Full", Pentagon());
        var b2 = Baseline("bl-2", "Partial", Pentagon(without: "Persistence"));

        var radar = _comparer.Compare([b1, b2]).RadarChart;

        Assert.DoesNotContain("Persistence", radar.Axes);
        Assert.Equal(["Persistence"], radar.OmittedAxes);
        foreach (var (series, baseline) in radar.Series.Zip(new[] { b1, b2 }))
        {
            for (var i = 0; i < radar.Axes.Count; i++)
            {
                Assert.Equal(baseline.DimensionScores[radar.Axes[i]], series.Values[i]);
            }
        }
    }

    [Fact]
    public void ANativeBaseline_AgainstALongMemEvalOne_SharesOnlyTheAxisBothMeasured()
    {
        var native = Baseline("bl-native", "native", Pentagon());
        var longMemEval = Baseline("bl-lme", "lme", new()
        {
            ["Information Extraction"] = 60,
            ["Multi-Session"] = 40,
            ["Temporal"] = 50,
            ["Knowledge Update"] = 55,
            ["Abstention"] = 70,
        });

        var comparison = _comparer.Compare([native, longMemEval]);

        foreach (var dimension in comparison.Dimensions)
        {
            foreach (var id in dimension.Scores.Keys)
            {
                var baseline = comparison.Baselines.Single(b => b.Id == id);
                Assert.True(baseline.DimensionScores.ContainsKey(dimension.DimensionName),
                    $"{id} has a score for {dimension.DimensionName}, which it never measured");
            }
        }
        Assert.Equal(["Temporal"], comparison.RadarChart.Axes);
    }

    [Fact]
    public void BaselinesThatMeasuredEveryDimension_AreComparedAsBefore()
    {
        // Guard: nothing is left out when nothing is missing.
        var comparison = _comparer.Compare([Baseline("bl-1", "A", Pentagon()), Baseline("bl-2", "B", Pentagon())]);

        Assert.All(comparison.Dimensions, d => Assert.Equal(2, d.Scores.Count));
        Assert.All(comparison.Dimensions, d => Assert.Empty(d.NotMeasuredBy));
        Assert.Equal(PentagonConsolidator.Axes, comparison.RadarChart.Axes);
        Assert.Empty(comparison.RadarChart.OmittedAxes);
    }

    [Fact]
    public void TheShippedReport_DoesNotFillAMissingScoreWithZero()
    {
        // report.html reads the baseline files itself. A dimension or category a baseline did not measure (or skipped)
        // must render as a gap or n/a, not as 0 % with a delta of minus the other side.
        var assembly = typeof(MemoryBaseline).Assembly;
        var name = assembly.GetManifestResourceNames().First(n => n.EndsWith("report.html"));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        Assert.DoesNotContain("dimension_scores?.[a]??0", content);
        Assert.DoesNotContain("dimension_scores?.[dim]??0", content);
        Assert.DoesNotContain("?.score??0", content);
    }

    private static Dictionary<string, double> Pentagon(string? without = null)
    {
        var scores = new Dictionary<string, double>
        {
            ["Recall"] = 85,
            ["Resilience"] = 70,
            ["Temporal"] = 75,
            ["Persistence"] = 80,
            ["Organization"] = 78,
        };
        if (without is not null)
        {
            scores.Remove(without);
        }
        return scores;
    }

    private static MemoryBaseline Baseline(string id, string name, Dictionary<string, double> dimensions) => new()
    {
        Id = id,
        Name = name,
        Timestamp = DateTimeOffset.UtcNow,
        ConfigurationId = "test-config",
        IsComplete = true,
        HasMeasuredScore = true,
        AgentConfig = new AgentBenchmarkConfig { AgentName = "TestAgent" },
        Benchmark = new BenchmarkExecutionInfo { Preset = "Quick", Duration = TimeSpan.FromSeconds(5) },
        OverallScore = 70,
        Grade = "B",
        Stars = 4,
        CategoryResults = new Dictionary<string, CategoryScoreEntry>(),
        DimensionScores = dimensions,
        Recommendations = [],
        Tags = [],
    };
}
