// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Snapshots;

/// <summary>
/// Result of a snapshot comparison.
/// </summary>
public class SnapshotComparisonResult
{
    /// <summary>Whether the snapshot matched.</summary>
    public bool IsMatch { get; init; }

    /// <summary>Differences found.</summary>
    public List<SnapshotDifference> Differences { get; init; } = new();

    /// <summary>Fields that were ignored.</summary>
    public List<string> IgnoredFields { get; init; } = new();

    /// <summary>Fields that used semantic comparison.</summary>
    public List<SemanticComparisonResult> SemanticResults { get; init; } = new();

    // Internal mutable flag used during comparison building
    internal bool IsMatchInternal { get; set; } = true;
}

/// <summary>
/// A difference between expected and actual values.
/// </summary>
public record SnapshotDifference(
    string Path,
    string Expected,
    string Actual,
    string Message);

/// <summary>
/// Result of semantic similarity comparison.
/// </summary>
public record SemanticComparisonResult(
    string Path,
    string Expected,
    string Actual,
    double Similarity,
    bool Passed);
