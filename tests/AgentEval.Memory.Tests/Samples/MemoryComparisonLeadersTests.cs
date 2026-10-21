// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using AgentEval.Samples;
using Xunit;

namespace AgentEval.Memory.Tests.Samples;

/// <summary>
/// G7 (Benchmark Reporting)'s winner column and "best overall" line, as shipped. Until 2026-10 the first model listed took
/// every tie, and a dimension a model did not measure printed as the comparer's 0. The comparisons here are built by the
/// real <see cref="BaselineComparer"/>, which no longer fills that 0: it names the models that did not measure a
/// dimension instead.
/// </summary>
public class MemoryComparisonLeadersTests
{
    [Fact]
    public void EqualOverallScores_AreATie_InRunOrder_NotAWinForTheFirstModel()
    {
        var glm = Baseline("glm", 66.7);
        var deepseek = Baseline("deepseek", 66.7);
        var qwen = Baseline("qwen", 50.0);

        var leaders = MemoryComparisonLeaders.Overall([glm, deepseek, qwen]);

        Assert.Equal(new[] { "glm", "deepseek" }, leaders.Select(b => b.Name));
        Assert.Equal(MemoryComparisonLeaders.Tie, MemoryComparisonLeaders.Winner(leaders));
    }

    [Fact]
    public void AHigherOverallScore_IsTheOneLeader()
    {
        var leaders = MemoryComparisonLeaders.Overall([Baseline("glm", 60.0), Baseline("deepseek", 61.0)]);

        Assert.Equal("deepseek", MemoryComparisonLeaders.Winner(leaders));
    }

    [Fact]
    public void ATiedDimension_HasNoWinner_AndAHigherOneHasItsModel()
    {
        var glm = Baseline("glm", 70, ("Recall", 80), ("Temporal", 60));
        var deepseek = Baseline("deepseek", 70, ("Recall", 80), ("Temporal", 65));
        var comparison = new BaselineComparer().Compare([glm, deepseek]);

        Assert.Equal(MemoryComparisonLeaders.Tie, WinnerOn(comparison, "Recall"));
        Assert.Equal("deepseek", WinnerOn(comparison, "Temporal"));
    }

    [Fact]
    public void AModelThatDidNotMeasureADimension_IsNotInTheRunning()
    {
        // glm measured 0 on Abstention; deepseek did not measure it, and the comparer gives deepseek no score there. A
        // measured 0 is a score, an unmeasured dimension is not, so this is glm's dimension and not a tie at 0.
        var glm = Baseline("glm", 50, ("Abstention", 0));
        var deepseek = Baseline("deepseek", 50);
        var comparison = new BaselineComparer().Compare([glm, deepseek]);
        Assert.False(comparison.Dimensions.Single(d => d.DimensionName == "Abstention").Scores.ContainsKey(deepseek.Id));

        Assert.Equal("glm", WinnerOn(comparison, "Abstention"));
    }

    [Fact]
    public void ADimensionNoModelMeasured_IsNotMeasured()
    {
        var comparison = new BaselineComparer().Compare([Baseline("glm", 50, ("Recall", 40)), Baseline("deepseek", 50, ("Recall", 45))]);

        Assert.Empty(MemoryComparisonLeaders.OnDimension(comparison.Baselines, "Temporal"));
        Assert.Equal(MemoryComparisonLeaders.NotMeasured, MemoryComparisonLeaders.Winner([]));
    }

    [Fact]
    public void NoneSeparated_OnlyWhenTheOverallScoreAndEveryDimensionTie()
    {
        var allTied = new BaselineComparer().Compare(
            [Baseline("glm", 70, ("Recall", 80), ("Temporal", 60)), Baseline("deepseek", 70, ("Recall", 80), ("Temporal", 60))]);
        var oneDimensionApart = new BaselineComparer().Compare(
            [Baseline("glm", 70, ("Recall", 80), ("Temporal", 60)), Baseline("deepseek", 70, ("Recall", 80), ("Temporal", 65))]);
        var overallApart = new BaselineComparer().Compare(
            [Baseline("glm", 70, ("Recall", 80)), Baseline("deepseek", 71, ("Recall", 80))]);

        Assert.True(MemoryComparisonLeaders.NoneSeparated(allTied));
        Assert.False(MemoryComparisonLeaders.NoneSeparated(oneDimensionApart));
        Assert.False(MemoryComparisonLeaders.NoneSeparated(overallApart));
    }

    private static string WinnerOn(MemoryBaselineComparison comparison, string dimension) =>
        MemoryComparisonLeaders.Winner(MemoryComparisonLeaders.OnDimension(comparison.Baselines, dimension));

    private static MemoryBaseline Baseline(string name, double overall, params (string Dimension, double Score)[] dimensions) => new()
    {
        Id = $"bl-{name}",
        Name = name,
        Timestamp = DateTimeOffset.UtcNow,
        ConfigurationId = $"config-{name}",
        AgentConfig = new AgentBenchmarkConfig { AgentName = "MemoryAgent", ModelId = name },
        Benchmark = new BenchmarkExecutionInfo { Preset = "Quick", Duration = TimeSpan.FromSeconds(5) },
        OverallScore = overall,
        Grade = "C",
        Stars = 3,
        CategoryResults = new Dictionary<string, CategoryScoreEntry>(),
        DimensionScores = dimensions.ToDictionary(d => d.Dimension, d => d.Score),
        Recommendations = [],
    };
}
