// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// The manifest.json model — auto-generated index of all baselines for an agent.
/// The HTML report loads this to discover available baselines without reading every file.
/// </summary>
public class BenchmarkManifest
{
    /// <summary>Schema version for forward compatibility (currently "1.0").</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>When the manifest was last regenerated.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Generator identifier (e.g., "AgentEval.Memory v1.0.0").</summary>
    public required string GeneratedBy { get; init; }

    /// <summary>Agent-level metadata.</summary>
    public required ManifestAgentInfo Agent { get; init; }

    /// <summary>Baselines grouped by benchmark preset.</summary>
    public required List<ManifestBenchmarkGroup> Benchmarks { get; init; }

    /// <summary>Relative path to archetypes file (e.g., "archetypes.json").</summary>
    public string? Archetypes { get; init; }
}

/// <summary>
/// Agent-level metadata in the manifest.
/// </summary>
public class ManifestAgentInfo
{
    /// <summary>Agent name.</summary>
    public required string Name { get; init; }

    /// <summary>Agent description.</summary>
    public string? Description { get; init; }

    /// <summary>Repository URL.</summary>
    public string? Repository { get; init; }

    /// <summary>Team name.</summary>
    public string? Team { get; init; }
}

/// <summary>
/// A group of baselines that share the same benchmark preset.
/// </summary>
public class ManifestBenchmarkGroup
{
    /// <summary>Benchmark identifier (e.g., "memory-full").</summary>
    public required string BenchmarkId { get; init; }

    /// <summary>Preset name ("Quick", "Standard", "Full").</summary>
    public required string Preset { get; init; }

    /// <summary>Category names in this preset.</summary>
    public required List<string> Categories { get; init; }

    /// <summary>Baselines in this group, ordered by timestamp.</summary>
    public required List<ManifestBaselineEntry> Baselines { get; init; }
}

/// <summary>
/// Summary entry for a single baseline in the manifest.
/// Contains enough data to render the baseline selector without loading the full file.
/// </summary>
public class ManifestBaselineEntry
{
    /// <summary>Baseline ID.</summary>
    public required string Id { get; init; }

    /// <summary>Relative file path (e.g., "baselines/2026-03-15_v2.1.json").</summary>
    public required string File { get; init; }

    /// <summary>Baseline name.</summary>
    public required string Name { get; init; }

    /// <summary>Configuration ID for timeline vs. comparison routing.</summary>
    public required string ConfigurationId { get; init; }

    /// <summary>Timestamp of the benchmark run.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Overall score (0-100).</summary>
    public required double OverallScore { get; init; }

    /// <summary>Letter grade.</summary>
    public required string Grade { get; init; }

    /// <summary>Tags for filtering.</summary>
    public List<string> Tags { get; init; } = [];
}
