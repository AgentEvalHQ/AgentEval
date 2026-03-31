// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// Per-category score entry for baseline serialization.
/// Maps from BenchmarkCategoryResult (internal runtime model)
/// to a serializable snapshot suitable for JSON persistence.
/// </summary>
public class CategoryScoreEntry
{
    /// <summary>Score (0-100).</summary>
    public required double Score { get; init; }

    /// <summary>Letter grade (A/B/C/D/F) — same thresholds as <see cref="MemoryBenchmarkResult.Grade"/>.</summary>
    public required string Grade { get; init; }

    /// <summary>Whether this category was skipped.</summary>
    public required bool Skipped { get; init; }

    /// <summary>Number of scenarios run for this category (1 for Quick, 2+ for Standard/Full).</summary>
    public int ScenarioCount { get; init; } = 1;

    /// <summary>Actionable recommendation for this category, if score is weak.</summary>
    public string? Recommendation { get; init; }

    /// <summary>Stochastic data from multi-run evaluation. Null for single-run (default).</summary>
    public StochasticData? Stochastic { get; init; }
}

/// <summary>
/// Statistical data from running the same benchmark multiple times.
/// Forward-compatible — populated by future stochastic evaluation mode.
/// </summary>
public class StochasticData
{
    /// <summary>Number of runs performed.</summary>
    public required int Runs { get; init; }

    /// <summary>Mean score across all runs.</summary>
    public required double Mean { get; init; }

    /// <summary>Standard deviation of scores.</summary>
    public required double StdDev { get; init; }

    /// <summary>Minimum score across all runs.</summary>
    public required double Min { get; init; }

    /// <summary>Maximum score across all runs.</summary>
    public required double Max { get; init; }

    /// <summary>Coefficient of variation (StdDev / Mean). Lower = more consistent.</summary>
    public double CoefficientOfVariation => Mean > 0 ? StdDev / Mean : 0;
}
