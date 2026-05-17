// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Quality;

/// <summary>
/// Evaluates whether an AI response is relevant to the user's query.
/// <para>
/// Wraps an <see cref="AtomicLlmEval"/> configured with the relevance rubric.
/// </para>
/// <para>
/// <b>Secondary metric caveat</b>: Relevance is a necessary but insufficient quality signal.
/// A response can score high on relevance while being factually wrong. Always interpret
/// relevance alongside <see cref="GroundednessEval"/> for a complete quality picture.
/// This caveat is explicit in the prompt rubric so the judge internalises it.
/// </para>
/// <para>
/// <b>Input contract</b>: requires <see cref="EvalInput.Query"/> and
/// <see cref="EvalInput.Response"/>. <see cref="EvalInput.Context"/> is optional but
/// recommended — it allows the judge to assess whether the response is appropriately
/// scoped to the retrieved material.
/// </para>
/// <para>
/// Source: forked from Azure/azure-sdk-for-python (commit main-2026-05-09)
/// sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_relevance/relevance.prompty
/// License: MIT. Modifications: temperature=0, secondary-metric caveat, structured evidence[],
/// label assignment table, severity rubric.
/// </para>
/// </summary>
public sealed class RelevanceEval : IEval
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
    /// Initialises a new <see cref="RelevanceEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score relevance.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.70.</param>
    public RelevanceEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "relevance",
            name: "Relevance",
            category: "rag",
            version: "1.0.0",
            criteria: new[]
            {
                "Response directly addresses the user's query without significant off-topic content",
                "Response covers the scope of the question without notable omissions of explicitly asked information",
                "Response stays on-topic and does not introduce irrelevant tangents",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.relevance.v1",
            failureSeverity: "medium");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
