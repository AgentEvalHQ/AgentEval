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
    IReadOnlyList<string> Examples)
{
    /// <summary>
    /// False when the class could not be checked: <c>token_under_reporting</c> when no chat-boundary response reports
    /// its token usage, so the tokens actually consumed are unknown. <see cref="Count"/> and <see cref="Score"/> are then
    /// placeholders (0 and 1.0) that <see cref="TraceFidelityReport.OverallScore"/> leaves out; the class used to read
    /// as clean.
    /// </summary>
    public bool Measured { get; init; } = true;
}

/// <summary>The structured result of reconciling an agent-boundary trace against a chat-boundary trace.</summary>
/// <param name="Discrepancies">One entry per discrepancy class (always all six, in canonical order).</param>
/// <param name="OverallScore">
/// Severity-weighted root fidelity score, 0–1 (1 = perfectly faithful), over the classes that were measured
/// (<see cref="TraceFidelityDiscrepancy.Measured"/>), their weights scaled up to the full weight.
/// </param>
public sealed record TraceFidelityReport(
    IReadOnlyList<TraceFidelityDiscrepancy> Discrepancies,
    double OverallScore);
