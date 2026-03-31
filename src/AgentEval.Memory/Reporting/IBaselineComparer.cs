// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;

namespace AgentEval.Memory.Reporting;

/// <summary>
/// Compares two or more baselines and produces a comparison report
/// with per-dimension deltas and radar chart data.
/// </summary>
public interface IBaselineComparer
{
    /// <summary>
    /// Compares the provided baselines and returns a structured comparison.
    /// </summary>
    BaselineComparison Compare(IReadOnlyList<MemoryBaseline> baselines);
}
