// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;

namespace AgentEval.Memory.Reporting;

/// <summary>
/// Compares two or more baselines by computing per-dimension deltas
/// and building radar chart data for visualization.
/// </summary>
public class BaselineComparer : IBaselineComparer
{
    public BaselineComparison Compare(IReadOnlyList<MemoryBaseline> baselines)
    {
        ArgumentNullException.ThrowIfNull(baselines);
        if (baselines.Count == 0)
            throw new ArgumentException("At least one baseline is required.", nameof(baselines));

        // Collect all dimension names across all baselines
        var allDimensions = baselines
            .SelectMany(b => b.DimensionScores.Keys)
            .Distinct()
            .ToList();

        // Build per-dimension comparisons — include every baseline for every dimension,
        // using 0 as the default for baselines that do not define a score for that dimension.
        // This keeps per-dimension tables and radar charts consistent (no silent omissions).
        var dimensions = allDimensions.Select(dim => new DimensionComparison
        {
            DimensionName = dim,
            Scores = baselines
                .ToDictionary(b => b.Id, b => b.DimensionScores.GetValueOrDefault(dim, 0))
        }).ToList();

        // Determine best baseline (highest overall score)
        // Count > 0 is guaranteed by the guard above, so MaxBy always returns non-null
        var bestBaseline = baselines.MaxBy(b => b.OverallScore)
            ?? throw new InvalidOperationException("Unable to determine best baseline.");

        // Build radar chart data
        var axes = PentagonConsolidator.Axes.Where(a => allDimensions.Contains(a)).ToList();
        var radarChart = new RadarChartData
        {
            Axes = axes,
            Series = baselines.Select(b => new RadarChartSeries
            {
                Name = b.Name,
                Values = axes.Select(a => b.DimensionScores.GetValueOrDefault(a, 0)).ToList()
            }).ToList()
        };

        return new BaselineComparison
        {
            Baselines = baselines,
            Dimensions = dimensions,
            BestBaselineId = bestBaseline.Id,
            RadarChart = radarChart
        };
    }
}
