// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// Result of comparing two or more memory baselines.
/// Contains per-dimension score comparisons and radar chart data for visualization.
/// </summary>
/// <remarks>
/// T3.4 (LR1-003, 2026-05-25): renamed from <c>BaselineComparison</c> to
/// disambiguate from <see cref="AgentEval.Output.BaselineComparison"/> (the
/// run-vs-saved-baseline shape on <c>IOutputStoreReader</c>). Memory's
/// baselines are pentagon-dimension comparisons across multiple memory
/// baselines; Output's are run-summary deltas. They are NOT interchangeable.
/// </remarks>
public class MemoryBaselineComparison
{
    /// <summary>The baselines being compared.</summary>
    public required IReadOnlyList<MemoryBaseline> Baselines { get; init; }

    /// <summary>Per-dimension comparison across all baselines.</summary>
    public required IReadOnlyList<DimensionComparison> Dimensions { get; init; }

    /// <summary>
    /// ID of the baseline with the highest overall score among those that measured at least one dimension, or
    /// <see langword="null"/> when none did: a baseline that measured nothing is not a contender.
    /// </summary>
    public required string? BestBaselineId { get; init; }

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

    /// <summary>
    /// Score per baseline ID, for the baselines that measured this dimension only. A baseline that did not measure it
    /// has no entry (read with <c>TryGetValue</c>) and is listed in <see cref="NotMeasuredBy"/>.
    /// </summary>
    public required IReadOnlyDictionary<string, double> Scores { get; init; }

    /// <summary>IDs of the compared baselines that did not measure this dimension.</summary>
    public IReadOnlyList<string> NotMeasuredBy { get; init; } = [];

    /// <summary>
    /// Highest score across the baselines that measured this dimension, or <see langword="null"/> when none did: no
    /// baseline scored 0 there (T13).
    /// </summary>
    public double? BestScore => Scores.Count > 0 ? Scores.Values.Max() : null;

    /// <summary>ID of the baseline with the best score for this dimension, among those that measured it.</summary>
    /// <remarks>Returns <c>null</c> when there are no scores.</remarks>
    public string? BestBaselineId => Scores.Count > 0 ? Scores.MaxBy(kvp => kvp.Value).Key : null;
}
