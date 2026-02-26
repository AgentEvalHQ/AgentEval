// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Snapshots;

/// <summary>
/// Compares agent responses against saved snapshots, applying scrubbing and optional semantic comparison.
/// </summary>
public interface ISnapshotComparer
{
    /// <summary>
    /// Compares two JSON strings, applying scrubbing and optional semantic comparison.
    /// </summary>
    /// <param name="expected">The expected (baseline) JSON string.</param>
    /// <param name="actual">The actual JSON string to compare.</param>
    /// <returns>A result containing match status, differences, ignored fields, and semantic results.</returns>
    SnapshotComparisonResult Compare(string expected, string actual);

    /// <summary>
    /// Applies scrubbing patterns to normalize a value for comparison.
    /// </summary>
    /// <param name="value">The value to scrub.</param>
    /// <returns>The scrubbed value.</returns>
    string ApplyScrubbing(string value);
}
