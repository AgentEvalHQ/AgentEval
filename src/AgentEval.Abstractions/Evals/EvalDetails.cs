// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Extended detail block attached to an eval result, including sub-results for composites.</summary>
public sealed record EvalDetails(
    IReadOnlyDictionary<string, double>? Dimensions,
    IReadOnlyList<EvalEvidence>? Evidence,
    IReadOnlyList<string>? Recommendations,
    IReadOnlyList<EvalResult>? SubResults,
    string? AggregationStrategy)
{
    /// <summary>
    /// The judge's overall narrative summary/rationale for this result. Optional.
    /// <para>
    /// Previously the LLM judge's <c>summary</c> field (a one-to-three sentence rationale, often
    /// citing the relevant articles) was produced but dropped when <see cref="EvalResult"/> was
    /// built, so it never reached the evidence file or any report. Surfacing it here lets consumers
    /// show <i>why</i> a control passed or failed, not just the per-criterion verdicts. Declared as
    /// a non-positional init-only property so existing <c>new EvalDetails(...)</c> call sites and
    /// positional deconstruction are unaffected.
    /// </para>
    /// </summary>
    public string? Summary { get; init; }
}
