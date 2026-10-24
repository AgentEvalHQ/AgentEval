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
    public MemoryBaselineComparison Compare(IReadOnlyList<MemoryBaseline> baselines)
    {
        ArgumentNullException.ThrowIfNull(baselines);
        if (baselines.Count == 0)
            throw new ArgumentException("At least one baseline is required.", nameof(baselines));

        // Collect all dimension names across all baselines
        var allDimensions = baselines
            .SelectMany(b => b.DimensionScores.Keys)
            .Distinct()
            .ToList();

        // Build per-dimension comparisons. A baseline that did not measure a dimension (a skipped category, an agent
        // without session reset, a benchmark with other dimension names) has no score for it: it is left out of
        // Scores and named in NotMeasuredBy, never filled with a 0 that could lose, or win, the dimension.
        var dimensions = allDimensions.Select(dim => new DimensionComparison
        {
            DimensionName = dim,
            Scores = baselines
                .Where(b => b.DimensionScores.ContainsKey(dim))
                .ToDictionary(b => b.Id, b => b.DimensionScores[dim]),
            NotMeasuredBy = baselines
                .Where(b => !b.DimensionScores.ContainsKey(dim))
                .Select(b => b.Id)
                .ToList(),
            NotRankedBy = baselines.Where(b => b.IsComplete != true || b.HasMeasuredScore == false).Select(b => b.Id).ToList(),
        }).ToList();

        // Keep partial observations in the comparison, but require recorded complete evidence to establish a best.
        // A baseline that measured no dimension cannot win by its empty-run placeholder (T13).
        var bestBaseline = baselines
            .Where(b => b.IsComplete == true && b.HasMeasuredScore != false && b.DimensionScores.Count > 0)
            .MaxBy(b => b.OverallScore);

        // Build radar chart data over the pentagon axes every baseline measured; an axis only some measured is
        // named in OmittedAxes rather than drawn with a made-up 0 for the others.
        var presentAxes = PentagonConsolidator.Axes.Where(a => allDimensions.Contains(a)).ToList();
        var axes = presentAxes.Where(a => baselines.All(b => b.DimensionScores.ContainsKey(a))).ToList();
        var radarChart = new RadarChartData
        {
            Axes = axes,
            OmittedAxes = presentAxes.Except(axes).ToList(),
            Series = baselines.Select(b => new RadarChartSeries
            {
                Name = b.Name,
                Values = axes.Select(a => b.DimensionScores[a]).ToList()
            }).ToList()
        };

        return new MemoryBaselineComparison
        {
            Baselines = baselines,
            Dimensions = dimensions,
            BestBaselineId = bestBaseline?.Id,
            RadarChart = radarChart
        };
    }
}
