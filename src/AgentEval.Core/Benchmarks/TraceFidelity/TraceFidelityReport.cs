// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Benchmarks;

/// <summary>One discrepancy class result from a Trace Fidelity reconciliation.</summary>
/// <param name="ClassKey">The discrepancy class key (see <see cref="TraceFidelityRubric"/>).</param>
/// <param name="Severity">Severity label (Critical/High/Medium/Low).</param>
/// <param name="Count">Number of occurrences detected.</param>
/// <param name="Score">Per-class child score, 0–1 (1 = clean).</param>
/// <param name="Examples">Concrete examples (tool name + turn index, etc.).</param>
public sealed record TraceFidelityDiscrepancy(
    string ClassKey,
    string Severity,
    int Count,
    double Score,
    IReadOnlyList<string> Examples);

/// <summary>The structured result of reconciling an agent-boundary trace against a chat-boundary trace.</summary>
/// <param name="Discrepancies">One entry per discrepancy class (always all six, in canonical order).</param>
/// <param name="OverallScore">Severity-weighted root fidelity score, 0–1 (1 = perfectly faithful).</param>
public sealed record TraceFidelityReport(
    IReadOnlyList<TraceFidelityDiscrepancy> Discrepancies,
    double OverallScore);
