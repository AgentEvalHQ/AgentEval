// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Quality;

/// <summary>
/// Evaluates the logical organization, internal consistency, and natural flow of an AI response.
/// <para>
/// Wraps an <see cref="AtomicLlmEval"/> using a 5-point ordinal scale.
/// Both the 0..1 normalized <c>score</c> and the integer <c>ordinal</c> (1–5) are
/// emitted in <see cref="EvalResult.Details"/> metadata (per findings-and-suggestions §2
/// universal envelope: always emit both ordinal and normalized score).
/// </para>
/// <para>
/// Scale: 1=incoherent, 2=poor, 3=moderate, 4=mostly coherent, 5=highly coherent.
/// Normalized score = ordinal / 5.0.
/// </para>
/// <para>
/// <b>Input contract</b>: requires <see cref="EvalInput.Query"/> and
/// <see cref="EvalInput.Response"/>.
/// </para>
/// <para>
/// Source: forked from Azure/azure-sdk-for-python (commit <TBD-foundry-sha> see CHANGELOG T3.7)
/// sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_coherence/coherence.prompty
/// License: MIT. Modifications: temperature=0, 5-point ordinal normalized to 0..1,
/// structured evidence[], both ordinal and score in output, label table, severity=low.
/// </para>
/// </summary>
public sealed class CoherenceEval : IEval
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
    /// Initialises a new <see cref="CoherenceEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score coherence.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">
    /// Score fraction (0..1) at or above which the eval passes.
    /// Defaults to 0.60, corresponding to ordinal ≥ 3 (moderately coherent).
    /// </param>
    public CoherenceEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.60)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "coherence",
            name: "Coherence",
            category: "rag",
            version: "1.0.0",
            criteria: new[]
            {
                "Response is logically organized with a clear structure (ordinal 1–5: 5=highly coherent, 1=incoherent)",
                "Ideas flow naturally with appropriate transitions between sentences and paragraphs",
                "Response does not contradict itself internally",
                "Sentences and paragraphs are well-connected and the response is easy to follow",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.coherence.v1",
            // Coherence failures are low-severity (presentation quality, not safety or factual accuracy)
            failureSeverity: "low");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
