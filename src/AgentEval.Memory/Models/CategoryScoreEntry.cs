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

    /// <summary>
    /// Number of items behind this score. External-benchmark baselines set it to the category's
    /// question count. The native memory benchmark's <c>ToBaseline</c> does not set it, so native
    /// baselines carry the default of 1 whatever the preset ran.
    /// </summary>
    public int ScenarioCount { get; init; } = 1;

    /// <summary>Actionable recommendation for this category, if score is weak.</summary>
    public string? Recommendation { get; init; }

    /// <summary>
    /// Multi-run statistics for this category. Not populated by any shipped runner: no AgentEval code
    /// path sets it, so it is <see langword="null"/> on every baseline AgentEval produces, and the
    /// shipped report does not read it. It exists so a caller that runs the benchmark several times
    /// can record its own statistics on the baseline.
    /// </summary>
    public StochasticData? Stochastic { get; init; }
}

/// <summary>
/// Statistical data from running the same benchmark multiple times.
/// Not populated by any shipped runner; see <see cref="CategoryScoreEntry.Stochastic"/>.
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
