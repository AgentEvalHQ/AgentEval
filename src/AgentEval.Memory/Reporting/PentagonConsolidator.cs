// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;
using static AgentEval.Memory.Models.MemoryBenchmarkResult;

namespace AgentEval.Memory.Reporting;

/// <summary>
/// Maps 11 benchmark categories to 5 pentagon dimensions for radar visualization.
/// <para>
/// Consolidation mapping:
/// <list type="bullet">
///   <item>Recall = avg(BasicRetention, ReachBackDepth)</item>
///   <item>Resilience = avg(NoiseResilience, ReducerFidelity)</item>
///   <item>Temporal = avg(TemporalReasoning, FactUpdateHandling, ConflictResolution)</item>
///   <item>Persistence = avg(CrossSession, MultiSessionReasoning)</item>
///   <item>Organization = avg(MultiTopic, Abstention, PreferenceExtraction)</item>
/// </list>
/// </para>
/// Handles gracefully when categories are skipped (uses available data only).
/// If no source categories are available for an axis, that axis is omitted.
/// </summary>
public static class PentagonConsolidator
{
    /// <summary>The 5 pentagon axis names in display order.</summary>
    public static readonly IReadOnlyList<string> Axes =
        ["Recall", "Resilience", "Temporal", "Persistence", "Organization"];

    /// <summary>
    /// Consolidates 8 benchmark category results into 5 pentagon dimension scores.
    /// </summary>
    /// <param name="categoryResults">The raw category results from a benchmark run.</param>
    /// <returns>Dictionary of dimension name to score. Missing axes are omitted.</returns>
    public static Dictionary<string, double> Consolidate(
        IReadOnlyList<BenchmarkCategoryResult> categoryResults)
    {
        ArgumentNullException.ThrowIfNull(categoryResults);

        var lookup = categoryResults
            .Where(c => !c.Skipped)
            .ToDictionary(c => c.ScenarioType, c => c.Score);

        var scores = new Dictionary<string, double>();

        // Recall = avg(BasicRetention, ReachBackDepth)
        AddAveraged(scores, "Recall", lookup,
            BenchmarkScenarioType.BasicRetention, BenchmarkScenarioType.ReachBackDepth);

        // Resilience = avg(NoiseResilience, ReducerFidelity)
        AddAveraged(scores, "Resilience", lookup,
            BenchmarkScenarioType.NoiseResilience, BenchmarkScenarioType.ReducerFidelity);

        // Temporal = avg(TemporalReasoning, FactUpdateHandling, ConflictResolution)
        {
            var temporalScores = new List<double>();
            if (lookup.TryGetValue(BenchmarkScenarioType.TemporalReasoning, out var temp)) temporalScores.Add(temp);
            if (lookup.TryGetValue(BenchmarkScenarioType.FactUpdateHandling, out var upd)) temporalScores.Add(upd);
            if (lookup.TryGetValue(BenchmarkScenarioType.ConflictResolution, out var conf)) temporalScores.Add(conf);
            if (temporalScores.Count > 0) scores["Temporal"] = temporalScores.Average();
        }

        // Persistence = avg(CrossSession, MultiSessionReasoning)
        AddAveraged(scores, "Persistence", lookup,
            BenchmarkScenarioType.CrossSession, BenchmarkScenarioType.MultiSessionReasoning);

        // Organization = avg(MultiTopic, Abstention, PreferenceExtraction)
        AddAveraged(scores, "Organization", lookup,
            BenchmarkScenarioType.MultiTopic, BenchmarkScenarioType.Abstention,
            BenchmarkScenarioType.PreferenceExtraction);

        return scores;
    }

    private static void AddAveraged(
        Dictionary<string, double> scores,
        string axisName,
        Dictionary<BenchmarkScenarioType, double> lookup,
        params BenchmarkScenarioType[] types)
    {
        var available = types
            .Where(t => lookup.ContainsKey(t))
            .Select(t => lookup[t])
            .ToList();

        if (available.Count > 0)
            scores[axisName] = available.Average();
        // If none available, omit the axis
    }
}
