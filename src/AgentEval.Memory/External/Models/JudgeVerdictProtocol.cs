// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.Models;

/// <summary>Controls how a judge verdict is requested from, and recovered from, the model.</summary>
public enum JudgeVerdictProtocol
{
    /// <summary>
    /// Ask for prose and recover the verdict from its leading token. This is the historical protocol and
    /// remains the default so sealed benchmark bases stay comparable.
    /// </summary>
    /// <remarks>
    /// Known failure mode: the parser vetoes a leading "yes" when the word "no" appears later in the
    /// response, which fires on ordinary reasoning prose such as "there is no discrepancy". The veto is
    /// deterministic per input, so an affected question fails on every run rather than intermittently.
    /// </remarks>
    FreeText,

    /// <summary>
    /// Ask the provider for a JSON object carrying a closed <c>verdict</c> field and a separate
    /// <c>reasoning</c> field, so reasoning text can never contaminate the verdict.
    /// </summary>
    /// <remarks>
    /// Uses the provider's structured-output facility when available and degrades through plain JSON mode
    /// to an unconstrained call, because the prompt requests JSON either way. A response that is still
    /// unusable yields <see cref="JudgeOutcomeStatus.Invalid"/> — never an exception, a silent
    /// <see cref="JudgeOutcomeStatus.No"/>, or a guess.
    /// </remarks>
    StructuredJson
}

/// <summary>Controls whether a verdict is decided by one judge call or by judging predicates separately.</summary>
public enum JudgeDecompositionMode
{
    /// <summary>One judge call decides the whole question. Default; reproduces historical behaviour.</summary>
    None,

    /// <summary>
    /// Split the gold answer into predicates, ask whether the response supports each one, then combine
    /// with <see cref="PredicateCombinationRule"/>. Costs one provider call per predicate.
    /// </summary>
    PerPredicate
}

/// <summary>
/// How per-predicate outcomes combine into one verdict. Stated explicitly rather than implied, and
/// echoed onto <see cref="ExternalJudgmentResult.PredicateCombinationRule"/> so a stored result records
/// the rule that produced it.
/// </summary>
public enum PredicateCombinationRule
{
    /// <summary>
    /// Every predicate must be supported for the verdict to be <see cref="JudgeOutcomeStatus.Yes"/>.
    /// Default, because the official LongMemEval standard prompt scores a partial answer as incorrect
    /// ("If the response only contains a subset of the information required by the answer, answer no").
    /// </summary>
    AllMustHold,

    /// <summary>
    /// More than half the predicates must be supported. Deliberately not the default: it disagrees with
    /// official LongMemEval scoring and exists for measuring how much the all-must-hold rule costs.
    /// </summary>
    Majority
}
