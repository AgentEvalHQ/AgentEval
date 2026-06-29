// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Provides AI-powered response evaluation.
/// </summary>
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
/// Result of an AI-powered evaluation.
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
