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
    string? AggregationStrategy);
