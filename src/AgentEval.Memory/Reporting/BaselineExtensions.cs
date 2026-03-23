// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;
using static AgentEval.Memory.Models.MemoryBenchmarkResult; // ComputeGrade

namespace AgentEval.Memory.Reporting;

/// <summary>
/// Extension methods to convert a <see cref="MemoryBenchmarkResult"/> into a <see cref="MemoryBaseline"/>.
/// </summary>
public static class BaselineExtensions
{
    /// <summary>
    /// Creates a persistable baseline snapshot from a benchmark result.
    /// </summary>
    /// <param name="result">The benchmark result to snapshot.</param>
    /// <param name="name">Human-readable baseline name (e.g., "v2.1 Production").</param>
    /// <param name="config">Agent configuration metadata.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="tags">Optional tags for filtering.</param>
    /// <returns>A <see cref="MemoryBaseline"/> ready for persistence.</returns>
    public static MemoryBaseline ToBaseline(
        this MemoryBenchmarkResult result,
        string name,
        AgentBenchmarkConfig config,
        string? description = null,
        IReadOnlyList<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(config);

        var categoryResults = new Dictionary<string, CategoryScoreEntry>();
        foreach (var cat in result.CategoryResults)
        {
            categoryResults[cat.CategoryName] = new CategoryScoreEntry
            {
                Score = cat.Score,
                Grade = ComputeGrade(cat.Score),
                Skipped = cat.Skipped,
                Recommendation = FindRecommendation(result.Recommendations, cat.ScenarioType)
            };
        }

        var dimensionScores = PentagonConsolidator.Consolidate(result.CategoryResults);

        return new MemoryBaseline
        {
            Id = $"bl-{Guid.NewGuid():N}"[..11],
            Name = name,
            Description = description,
            Timestamp = DateTimeOffset.UtcNow,
            ConfigurationId = config.ConfigurationId,
            AgentConfig = config,
            Benchmark = new BenchmarkExecutionInfo
            {
                Preset = result.BenchmarkName,
                Duration = result.Duration
            },
            OverallScore = result.OverallScore,
            Grade = result.Grade,
            Stars = result.Stars,
            CategoryResults = categoryResults,
            DimensionScores = dimensionScores,
            Recommendations = result.Recommendations.ToList(),
            Tags = tags?.ToList() ?? []
        };
    }

    // ComputeGrade is imported via 'using static MemoryBenchmarkResult'

    private static string? FindRecommendation(
        IReadOnlyList<string> recommendations,
        BenchmarkScenarioType scenarioType)
    {
        if (recommendations.Count == 0) return null;

        // Match recommendation keywords to scenario types
        var keywords = scenarioType switch
        {
            BenchmarkScenarioType.BasicRetention => "context management",
            BenchmarkScenarioType.TemporalReasoning => "timestamps",
            BenchmarkScenarioType.NoiseResilience => "semantic memory",
            BenchmarkScenarioType.ReachBackDepth => "context window",
            BenchmarkScenarioType.FactUpdateHandling => "overwrites outdated",
            BenchmarkScenarioType.MultiTopic => "topic-based",
            BenchmarkScenarioType.CrossSession => "persistent memory",
            BenchmarkScenarioType.ReducerFidelity => "reducer",
            BenchmarkScenarioType.Abstention => "hallucination",
            BenchmarkScenarioType.ConflictResolution => "conflicting",
            BenchmarkScenarioType.MultiSessionReasoning => "multi-session",
            _ => null
        };

        if (keywords is null) return null;

        return recommendations.FirstOrDefault(r =>
            r.Contains(keywords, StringComparison.OrdinalIgnoreCase));
    }
}
