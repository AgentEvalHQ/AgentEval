// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Quality;

/// <summary>
/// Evaluates the semantic similarity between an AI response and a ground-truth reference answer.
/// <para>
/// Wraps an <see cref="AtomicLlmEval"/> configured with three similarity criteria.
/// </para>
/// <para>
/// <b>Ground truth</b>: the reference answer is read from <see cref="EvalInput.GroundTruth"/> and sent to the judge after
/// the query. Without one, similarity cannot be measured: the result is <c>skipped</c> (not measured, the reason given)
/// and the judge is not called — never a <c>fail</c>, which would score the agent for a missing input (#203, B12a: the
/// reference was never sent, so the judge improvised a comparison, and since 1.1.0 failed every input).
/// </para>
/// <para>
/// <b>Input contract</b>: requires <see cref="EvalInput.Query"/>,
/// <see cref="EvalInput.Response"/>, and <see cref="EvalInput.GroundTruth"/>.
/// </para>
/// <para>
/// Lineage: AgentEval's own criteria and reference prompt, modelled on the evaluator concept (name,
/// inputs and scoring dimensions) of Azure/azure-sdk-for-python
/// <c>sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_similarity/similarity.prompty</c>.
/// A 2026-10-02 check found no upstream prompt text in the rubric file under
/// <c>Resources/Prompts/</c>, which the judge is sent as its system prompt.
/// </para>
/// </summary>
public sealed class SimilarityEval : IEval
{
    private readonly AtomicLlmEval _inner;

    /// <inheritdoc/>
    public string Key => _inner.Key;

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public string Category => _inner.Category;

    /// <inheritdoc/>
    public string Version => _inner.Version;

    /// <summary>
    /// Initialises a new <see cref="SimilarityEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score semantic similarity.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.70.</param>
    public SimilarityEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "similarity",
            name: "Similarity",
            category: "rag",
            version: "1.2.0",   // 1.2.0: the reference answer reaches the judge; none → not measured (B12a)
            criteria: new[]
            {
                "Response conveys the same key facts and meaning as the ground-truth reference answer",
                "Response does not omit critical information present in the ground truth",
                "Response does not introduce key claims that significantly contradict the ground truth",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.similarity.v1",
            failureSeverity: "medium");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!GroundTruthInput.Has(input))
            return Task.FromResult(EvalResult.Skipped(this,
                "Similarity compares the response with a reference answer, and none was supplied (EvalInput.GroundTruth): not measured."));
        return _inner.EvaluateAsync(GroundTruthInput.Fold(input), ct);
    }
}
