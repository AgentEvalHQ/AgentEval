// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Normalised score produced by an eval, including pass/fail disposition and severity.</summary>
public sealed record EvalScore(
    double Value,
    int? Ordinal,
    string Label,
    bool Passed,
    double? Threshold,
    string Severity,
    double? Confidence);
