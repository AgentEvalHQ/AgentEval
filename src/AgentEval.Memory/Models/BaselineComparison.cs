// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// Result of comparing two or more baselines.
/// Contains per-dimension score comparisons and radar chart data for visualization.
/// </summary>
public class BaselineComparison
{
    /// <summary>The baselines being compared.</summary>
    public required IReadOnlyList<MemoryBaseline> Baselines { get; init; }

    /// <summary>Per-dimension comparison across all baselines.</summary>
    public required IReadOnlyList<DimensionComparison> Dimensions { get; init; }

    /// <summary>ID of the baseline with the highest overall score.</summary>
    public required string BestBaselineId { get; init; }

    /// <summary>Radar chart data for pentagon visualization.</summary>
    public required RadarChartData RadarChart { get; init; }
}

/// <summary>
/// Score comparison for a single dimension across multiple baselines.
/// </summary>
public class DimensionComparison
{
    /// <summary>Dimension name (e.g., "Recall", "Resilience").</summary>
    public required string DimensionName { get; init; }

    /// <summary>Score per baseline ID.</summary>
    public required IReadOnlyDictionary<string, double> Scores { get; init; }

    /// <summary>Highest score across all baselines for this dimension.</summary>
    public double BestScore => Scores.Count > 0 ? Scores.Values.Max() : 0;

    /// <summary>ID of the baseline with the best score for this dimension.</summary>
    public string BestBaselineId => Scores.Count > 0 ? Scores.MaxBy(kvp => kvp.Value).Key : "";
}
