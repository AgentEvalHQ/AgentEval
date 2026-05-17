// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Quality;

/// <summary>
/// Weighted composite of all seven RAG-quality evaluators, producing a single
/// <strong>QA Quality</strong> score for a RAG response.
/// <para>
/// This composite bundles the six LLM-based quality evaluators plus the deterministic
/// <see cref="F1ScoreEval"/> into a single scored result. The weighting reflects the
/// relative diagnostic importance of each dimension for RAG system quality:
/// </para>
/// <list type="table">
///   <listheader><term>Sub-evaluator</term><description>Weight</description></listheader>
///   <item><term><see cref="GroundednessEval"/></term><description>0.30 — factual grounding is the primary quality signal</description></item>
///   <item><term><see cref="ResponseCompletenessEval"/></term><description>0.20 — coverage of expected facts</description></item>
///   <item><term><see cref="RelevanceEval"/></term><description>0.15 — on-topic response</description></item>
///   <item><term><see cref="SimilarityEval"/></term><description>0.15 — semantic match to ground truth</description></item>
///   <item><term><see cref="F1ScoreEval"/></term><description>0.10 — deterministic token-level overlap</description></item>
///   <item><term><see cref="CoherenceEval"/></term><description>0.05 — logical organization</description></item>
///   <item><term><see cref="FluencyEval"/></term><description>0.05 — language quality</description></item>
/// </list>
/// <para>
/// Aggregation: <see cref="WeightedSumAggregation"/>. Pass threshold: 0.70.
/// </para>
/// <para>
/// Two constructors are provided:
/// <list type="bullet">
///   <item>Full DI overload — accepts all seven <see cref="IEval"/> instances for testability
///   and per-evaluator configuration.</item>
///   <item>Convenience overload — accepts a single <see cref="IEvaluator"/> and constructs
///   all sub-evaluators internally with default configurations.</item>
/// </list>
/// </para>
/// </summary>
public sealed class QaCompositeEval : IEval
{
    private readonly CompositeEval _inner;

    /// <inheritdoc/>
    public string Key => _inner.Key;

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public string Category => _inner.Category;

    /// <inheritdoc/>
    public string Version => _inner.Version;

    /// <summary>
    /// Full dependency-injection overload. Each sub-evaluator is supplied independently,
    /// enabling per-evaluator judge configuration, mocking, and unit testing.
    /// </summary>
    /// <param name="groundedness">Groundedness sub-evaluator (weight 0.30).</param>
    /// <param name="responseCompleteness">Response Completeness sub-evaluator (weight 0.20).</param>
    /// <param name="relevance">Relevance sub-evaluator (weight 0.15).</param>
    /// <param name="similarity">Similarity sub-evaluator (weight 0.15).</param>
    /// <param name="f1Score">F1 Score sub-evaluator (weight 0.10).</param>
    /// <param name="coherence">Coherence sub-evaluator (weight 0.05).</param>
    /// <param name="fluency">Fluency sub-evaluator (weight 0.05).</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the aggregate passes. Defaults to 0.70.</param>
    public QaCompositeEval(
        IEval groundedness,
        IEval responseCompleteness,
        IEval relevance,
        IEval similarity,
        IEval f1Score,
        IEval coherence,
        IEval fluency,
        double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(groundedness);
        ArgumentNullException.ThrowIfNull(responseCompleteness);
        ArgumentNullException.ThrowIfNull(relevance);
        ArgumentNullException.ThrowIfNull(similarity);
        ArgumentNullException.ThrowIfNull(f1Score);
        ArgumentNullException.ThrowIfNull(coherence);
        ArgumentNullException.ThrowIfNull(fluency);

        _inner = new CompositeEval(
            key: "qa_composite",
            name: "QA Composite",
            category: "rag",
            version: "1.0.0",
            components: new[]
            {
                new EvalComponent(groundedness,         Weight: 0.30),
                new EvalComponent(responseCompleteness, Weight: 0.20),
                new EvalComponent(relevance,            Weight: 0.15),
                new EvalComponent(similarity,           Weight: 0.15),
                new EvalComponent(f1Score,              Weight: 0.10),
                new EvalComponent(coherence,            Weight: 0.05),
                new EvalComponent(fluency,              Weight: 0.05),
            },
            aggregation: WeightedSumAggregation.Instance,
            threshold: passThreshold);
    }

    /// <summary>
    /// Convenience overload. Instantiates all seven sub-evaluators using the same
    /// <paramref name="judge"/> (F1ScoreEval is deterministic and does not use the judge).
    /// </summary>
    /// <param name="judge">The LLM evaluator shared across the six LLM-based sub-evaluators.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the aggregate passes. Defaults to 0.70.</param>
    public QaCompositeEval(
        IEvaluator judge,
        string? judgeModel = null,
        double passThreshold = 0.70)
        : this(
            groundedness:        new GroundednessEval(judge, judgeModel),
            responseCompleteness: new ResponseCompletenessEval(judge, judgeModel),
            relevance:           new RelevanceEval(judge, judgeModel),
            similarity:          new SimilarityEval(judge, judgeModel),
            f1Score:             new F1ScoreEval(),
            coherence:           new CoherenceEval(judge, judgeModel),
            fluency:             new FluencyEval(judge, judgeModel),
            passThreshold:       passThreshold)
    {
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
