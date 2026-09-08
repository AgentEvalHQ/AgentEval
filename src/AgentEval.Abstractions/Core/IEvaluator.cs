// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// The <b>LLM-judge transport</b>: hands an (input, output, criteria) triple to a language model
/// acting as judge and returns its verdict as an <see cref="EvaluationResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a judge contract, not a general scoring contract. Its result type has three members whose
/// documented meaning only a judge can satisfy: <see cref="EvaluationResult.EvaluationFailed"/> ("the
/// judge returned no JSON, malformed JSON, or no recognisable score field"),
/// <see cref="EvaluationResult.InputTokenCount"/> / <see cref="EvaluationResult.OutputTokenCount"/>
/// ("<c>null</c> when the evaluator did not invoke a chat model"), and an
/// <see cref="EvaluationResult.OverallScore"/> that on failure "carries the conventional failure-score
/// fallback but should not be read as a real grade". A deterministic implementation can never truthfully
/// enter the first state, is indistinguishable from a judge whose provider dropped usage reporting in the
/// second, and has no sentinel to emit for the third — so <b>deterministic scoring does not belong here</b>.
/// It belongs on <c>AgentEval.Evals.IEval</c> (typically via <c>AtomicCodeEval</c>).
/// </para>
/// <para>
/// The sanctioned bridge from this transport into the unified eval tree is <c>AgentEval.Evals.AtomicLlmEval</c>,
/// which wraps an <see cref="IEvaluator"/> as an <c>IEval</c> leaf carrying <c>Provenance.Type == "atomic-llm"</c>,
/// a real <c>EstimatedCost</c> from the token counts, and an <c>"error"</c> label — never a pass — when
/// <see cref="EvaluationResult.EvaluationFailed"/> is set. Consumers of this interface directly (e.g. the
/// MAF evaluation harness) must likewise read <see cref="EvaluationResult.EvaluationFailed"/> before the score.
/// </para>
/// <para>Retyped in documentation only by ADR-030 §3.3; no member changed.</para>
/// </remarks>
public interface IEvaluator
{
    /// <summary>
    /// Evaluate an agent response against criteria.
    /// </summary>
    /// <param name="input">The original input/prompt.</param>
    /// <param name="output">The agent's output.</param>
    /// <param name="criteria">Evaluation criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The evaluation result.</returns>
    Task<EvaluationResult> EvaluateAsync(
        string input,
        string output,
        IEnumerable<string> criteria,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an LLM-judge evaluation (the <see cref="IEvaluator"/> transport). Read
/// <see cref="EvaluationFailed"/> before <see cref="OverallScore"/>: when it is set the score is a
/// sentinel, not a grade.
/// </summary>
public class EvaluationResult
{
    /// <summary>Overall score from 0 to 100.</summary>
    public int OverallScore { get; init; }

    /// <summary>Summary of the evaluation.</summary>
    public string Summary { get; init; } = "";

    /// <summary>Suggested improvements.</summary>
    public IReadOnlyList<string> Improvements { get; init; } = [];

    /// <summary>Individual criteria results.</summary>
    public IReadOnlyList<CriterionResult> CriteriaResults { get; init; } = [];

    /// <summary>
    /// True when the evaluation itself failed to produce a usable judgement — e.g. the judge
    /// returned no JSON, malformed JSON, or no recognisable score field. This is an INFRASTRUCTURE
    /// failure, not a low-scoring agent: consumers must distinguish "the eval errored" from "the
    /// agent genuinely scored low" rather than treating the fallback score as a real verdict.
    /// When set, <see cref="OverallScore"/> carries the conventional failure-score fallback but
    /// should not be read as a real grade.
    /// </summary>
    public bool EvaluationFailed { get; init; }

    /// <summary>
    /// Optional input (prompt) token count reported by the underlying chat model.
    /// <c>null</c> when the evaluator did not invoke a chat model or the model did not report usage.
    /// Surfaced via v1.1 task 1.7 so AgentEval.Evals.AtomicLlmEval can populate
    /// <see cref="AgentEval.Evals.EvalProvenance.EstimatedCost"/> from real token usage.
    /// </summary>
    public long? InputTokenCount { get; init; }

    /// <summary>
    /// Optional output (completion) token count reported by the underlying chat model.
    /// <c>null</c> when the evaluator did not invoke a chat model or the model did not report usage.
    /// </summary>
    public long? OutputTokenCount { get; init; }
}

/// <summary>
/// Result for a single evaluation criterion.
/// </summary>
public class CriterionResult
{
    /// <summary>The criterion being evaluated.</summary>
    public string Criterion { get; init; } = "";
    
    /// <summary>Whether the criterion was met.</summary>
    public bool Met { get; init; }
    
    /// <summary>Explanation of the result.</summary>
    public string Explanation { get; init; } = "";
}
