// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Captures how and at what cost an eval result was produced.</summary>
public sealed record EvalProvenance(
    string Type,
    string? JudgeModel,
    string? PromptId,
    string? PromptHash,
    int? TokensUsed,
    double EstimatedCost,
    bool CacheHit);
