// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Quality;

/// <summary>
/// Evaluates the semantic similarity between an AI response and a ground-truth reference answer.
/// <para>
/// Wraps an <see cref="AtomicLlmEval"/> configured with the similarity rubric.
/// </para>
/// <para>
/// <b>Ground-truth resolution</b>: ground truth is read from <see cref="EvalInput.GroundTruth"/>
/// (the dedicated field in <see cref="EvalInput"/>). If that field is null or empty,
/// the evaluator will score 0.0 with a <c>fail</c> label and an evidence note explaining
/// that similarity cannot be computed without a reference answer. Ground truth can also
/// be provided via the constructor for batch scenarios where all inputs share the same
/// reference — though per-input <see cref="EvalInput.GroundTruth"/> takes precedence.
/// </para>
/// <para>
/// <b>Input contract</b>: requires <see cref="EvalInput.Query"/>,
/// <see cref="EvalInput.Response"/>, and <see cref="EvalInput.GroundTruth"/>.
/// </para>
/// <para>
/// Source: forked from Azure/azure-sdk-for-python (commit main-2026-05-09)
/// sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_similarity/similarity.prompty
/// License: MIT. Modifications: temperature=0, structured evidence[], label assignment table,
/// explicit missing-ground-truth handling, severity=medium.
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
            version: "1.0.0",
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
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
