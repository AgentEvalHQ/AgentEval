// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// Data structure for rendering radar/spider charts (pentagon).
/// Designed to map directly to Chart.js radar chart configuration.
/// </summary>
public class RadarChartData
{
    /// <summary>The dimension names (axes of the chart).</summary>
    public required IReadOnlyList<string> Axes { get; init; }

    /// <summary>Maximum value per axis (typically 100).</summary>
    public double MaxValue { get; init; } = 100;

    /// <summary>One series per baseline being compared.</summary>
    public required IReadOnlyList<RadarChartSeries> Series { get; init; }
}

/// <summary>
/// A single data series in a radar chart, representing one baseline configuration.
/// </summary>
public class RadarChartSeries
{
    /// <summary>Display name (e.g., "v2.1 gpt-4o SlidingWindow(50)").</summary>
    public required string Name { get; init; }

    /// <summary>Scores per axis, in the same order as <see cref="RadarChartData.Axes"/>.</summary>
    public required IReadOnlyList<double> Values { get; init; }

    /// <summary>Optional color hint for rendering (e.g., "#34d399").</summary>
    public string? Color { get; init; }
}
