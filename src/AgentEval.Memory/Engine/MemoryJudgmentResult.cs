// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;

namespace AgentEval.Memory.Engine;

/// <summary>
/// Result of memory judgment/scoring.
/// </summary>
public class MemoryJudgmentResult
{
    /// <summary>
    /// Score (0-100) for how well the response demonstrates memory of expected facts.
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// <see langword="false"/> when the judge produced no score: the call failed, or its reply held none or one off the
    /// 0-100 scale (never clamped).
    /// <see cref="Score"/> is then 0 as a placeholder, not a measurement, and aggregates leave the question out and
    /// count it. Through 0.42 a failed call scored 0 and a reply with no score scored 50.
    /// </summary>
    public bool Measured { get; init; } = true;
    
    /// <summary>
    /// Expected facts that were found in the response.
    /// </summary>
    public required IReadOnlyList<MemoryFact> FoundFacts { get; init; }
    
    /// <summary>
    /// Expected facts that were missing from the response.
    /// </summary>
    public required IReadOnlyList<MemoryFact> MissingFacts { get; init; }
    
    /// <summary>
    /// Forbidden facts that were incorrectly present in the response.
    /// </summary>
    public required IReadOnlyList<MemoryFact> ForbiddenFound { get; init; }

    /// <summary>
    /// Whether the judge supplied a usable assessment of forbidden facts. An empty observed list alone does not
    /// establish absence; score-only replies and legacy/custom judgments without this fact default to false.
    /// </summary>
    public bool ForbiddenFactsAssessed { get; init; }
    
    /// <summary>
    /// Detailed explanation of the scoring decision.
    /// </summary>
    public string? Explanation { get; init; }
    
    /// <summary>
    /// Number of tokens used for this judgment.
    /// </summary>
    public int TokensUsed { get; init; }

    /// <summary>
    /// Input tokens the provider reported for the judge call; <see langword="null"/> when it did not report both counts
    /// (<see cref="TokensUsed"/> is then an estimate, and the call is not priced).
    /// </summary>
    public int? InputTokens { get; init; }

    /// <summary>
    /// Output tokens the provider reported for the judge call; <see langword="null"/> when it did not report both counts.
    /// </summary>
    public int? OutputTokens { get; init; }

    /// <summary>
    /// The model that answered the judge call (the response's model id, else the client's default), when known. With
    /// <see cref="InputTokens"/> and <see cref="OutputTokens"/> it prices the call.
    /// </summary>
    public string? JudgeModel { get; init; }
}
