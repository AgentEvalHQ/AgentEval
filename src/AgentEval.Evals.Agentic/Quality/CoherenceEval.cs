// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Quality;

/// <summary>
/// Evaluates the logical organization, internal consistency, and natural flow of an AI response.
/// <para>
/// Wraps an <see cref="AtomicLlmEval"/> with four criteria; the first names a 5-point ordinal
/// scale (1=incoherent, 2=poor, 3=moderate, 4=mostly coherent, 5=highly coherent). The score is the judge's overall score normalised to <c>[0,1]</c>; no separate
/// ordinal is emitted.
/// </para>
/// <para>
/// The ordinal-plus-score envelope (normalised score = ordinal / 5.0) is specified in the reference
/// prompt file, which is not yet sent to the judge.
/// </para>
/// <para>
/// <b>Input contract</b>: requires <see cref="EvalInput.Query"/> and
/// <see cref="EvalInput.Response"/>.
/// </para>
/// <para>
/// Source: forked from Azure/azure-sdk-for-python (commit &lt;TBD-foundry-sha&gt; see CHANGELOG T3.7)
/// sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_coherence/coherence.prompty
/// License: MIT. Modifications: temperature=0, 5-point ordinal normalized to 0..1,
/// structured evidence[], both ordinal and score in output, label table, severity=low.
/// These modifications are in the reference prompt file under <c>Resources/Prompts/</c>, which is not
/// yet sent to the judge; the judge call sets no temperature.
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
