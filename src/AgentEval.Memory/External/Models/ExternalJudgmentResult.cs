// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.Models;

/// <summary>
/// Result of an external benchmark judge evaluation.
/// Carries both binary correctness (for official compatibility) and a raw score (for analysis).
/// </summary>
public class ExternalJudgmentResult
{
    /// <summary>Binary correctness: true = judge said "yes", false = "no".</summary>
    public required bool Correct { get; init; }

    /// <summary>Raw score 0-100 for granular analysis. 100 if correct, 0 if not (for binary mode).</summary>
    public required double RawScore { get; init; }

    /// <summary>Optional explanation from the judge.</summary>
    public string? Explanation { get; init; }

    /// <summary>Tokens consumed by the judge call.</summary>
    public int TokensUsed { get; init; }
}
