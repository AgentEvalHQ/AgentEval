// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// A named, timestamped snapshot of a memory benchmark run with full metadata.
/// This is the central model for persistence and comparison.
/// One baseline = one JSON file in the baselines/ folder.
/// </summary>
public class MemoryBaseline
{
    /// <summary>Unique identifier (e.g., "bl-a1b2c3d4").</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name (e.g., "v2.1 gpt-4o SlidingWindow(50)").</summary>
    public required string Name { get; init; }

    /// <summary>Description of what this baseline captures.</summary>
    public string? Description { get; init; }

    /// <summary>When the baseline was captured.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Deterministic config hash for timeline vs. comparison routing.
    /// Copied from <see cref="AgentBenchmarkConfig.ConfigurationId"/>.
    /// </summary>
    public required string ConfigurationId { get; init; }

    /// <summary>Full agent configuration metadata.</summary>
    public required AgentBenchmarkConfig AgentConfig { get; init; }

    /// <summary>Benchmark execution metadata.</summary>
    public required BenchmarkExecutionInfo Benchmark { get; init; }

    /// <summary>Stored overall score (0-100). Consult IsComplete and HasMeasuredScore before treating it as a full-run result.</summary>
    public required double OverallScore { get; init; }

    /// <summary>Grade computed from the stored score for compatibility. A run grade is established only when IsComplete is true.</summary>
    public required string Grade { get; init; }

    /// <summary>Stars computed from the stored score for compatibility. A run rating is established only when IsComplete is true.</summary>
    public required int Stars { get; init; }

    /// <summary>Whether the run measured its applicable workload completely. Null means older or other producers did not record completeness.</summary>
    public bool? IsComplete { get; init; }

    /// <summary>Whether OverallScore contains an observation rather than an empty-run placeholder. Null means availability was not recorded.</summary>
    public bool? HasMeasuredScore { get; init; }

    /// <summary>Per-category results keyed by category name (e.g., "Basic Retention").</summary>
    public required Dictionary<string, CategoryScoreEntry> CategoryResults { get; init; }

    /// <summary>Pre-computed pentagon dimension scores (5 axes).</summary>
    public required Dictionary<string, double> DimensionScores { get; init; }

    /// <summary>Actionable recommendations from the benchmark runner.</summary>
    public required List<string> Recommendations { get; init; }

    /// <summary>Optional tags for filtering and grouping.</summary>
    public List<string> Tags { get; init; } = [];
}
