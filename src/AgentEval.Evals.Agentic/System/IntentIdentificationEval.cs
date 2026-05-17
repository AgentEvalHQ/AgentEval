// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.System;

/// <summary>
/// Evaluates whether an AI agent correctly identified the user's intent.
/// <para>
/// This evaluator addresses the first step of the intent-handling pipeline: did the agent
/// correctly parse what the user wanted? It is intentionally separate from
/// <see cref="IntentResolutionEval"/> which addresses whether the identified intent was
/// then resolved successfully. They can fail independently.
/// </para>
/// <para>
/// Wraps an <see cref="AtomicLlmEval"/> with the intent-identification rubric.
/// </para>
/// <para>
/// Source: split from the Foundry _intent_resolution evaluator per plan-05 §8 and
/// findings-and-suggestions.md §Intent Resolution suggestion. No direct Foundry source file
/// for this sub-evaluator; the criterion decomposition is original AgentEval work.
/// </para>
/// </summary>
public sealed class IntentIdentificationEval : IEval
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
    /// Initialises a new <see cref="IntentIdentificationEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score intent identification.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.70.</param>
    public IntentIdentificationEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "intent_identification",
            name: "Intent Identification",
            category: "system-outcome",
            version: "1.0.0",
            criteria: new[]
            {
                "The agent correctly identified the primary intent of the user's query",
                "The agent correctly identified any secondary or implicit intents present in the query",
                "The agent did not misinterpret the query scope (e.g. did not over-broaden or over-narrow the intent)",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.intent_identification.v1",
            failureSeverity: "medium");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
