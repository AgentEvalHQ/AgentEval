// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.Models;

/// <summary>
/// Outcome for a single predicate under <see cref="JudgeDecompositionMode.PerPredicate"/>.
/// </summary>
/// <remarks>
/// Reported per predicate rather than only in aggregate: a combined score says a question failed, which
/// a single-judge run already said. Knowing <i>which</i> claim the response did not support is the only
/// thing decomposition adds.
/// </remarks>
public sealed class JudgePredicateResult
{
    /// <summary>Zero-based position of this predicate in the extracted gold-answer order.</summary>
    public required int Index { get; init; }

    /// <summary>The claim drawn from the gold answer that this call judged.</summary>
    public required string Predicate { get; init; }

    /// <summary>
    /// Typed outcome for this predicate alone. <see cref="JudgeOutcomeStatus.Yes"/> means the response
    /// supported the claim.
    /// </summary>
    public required JudgeOutcomeStatus Status { get; init; }

    /// <summary>Bounded AgentEval-owned failure code when the status is not Yes or No.</summary>
    public string? SafeFailureCode { get; init; }

    /// <summary>Judge reasoning for this predicate, when the protocol supplied one.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Provider calls spent on this predicate, including retries.</summary>
    public int LlmCallCount { get; init; }

    /// <summary>Tokens spent on this predicate.</summary>
    public int TokensUsed { get; init; }
}
