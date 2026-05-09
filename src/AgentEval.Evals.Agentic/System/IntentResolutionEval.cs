// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.System;

/// <summary>
/// Evaluates whether an AI agent both identified and resolved the user's intent.
/// <para>
/// Implemented as a <see cref="CompositeEval"/> with two equal-weight sub-dimensions:
/// <c>intent_identified</c> (did the agent correctly understand what the user wanted?) and
/// <c>intent_resolved</c> (did the agent then successfully fulfil that intent?).
/// </para>
/// <para>
/// The two dimensions can fail independently, which provides diagnosability:
/// a correctly-identified intent that the agent failed to resolve is a different
/// fix from an intent that was never correctly understood.
/// </para>
/// <para>
/// Source: forked from Azure/azure-sdk-for-python
/// sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_intent_resolution/intent_resolution.prompty
/// License: MIT. Modifications: split into two independent sub-dimensions; structured evidence output.
/// </para>
/// </summary>
public sealed class IntentResolutionEval : IEval
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
    /// Initialises a new <see cref="IntentResolutionEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score each sub-dimension.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the composite passes. Defaults to 0.70.</param>
    public IntentResolutionEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(judge);

        var components = new[]
        {
            new EvalComponent(
                Eval: new AtomicLlmEval(
                    evaluator: judge,
                    key: "intent_identified",
                    name: "Intent Identified",
                    category: "system-outcome",
                    version: "1.0.0",
                    criteria: new[]
                    {
                        "The agent's response demonstrates that it correctly understood the user's primary intent",
                        "The agent did not misinterpret or conflate the user's request with a different goal",
                    },
                    passThreshold: passThreshold,
                    judgeModel: judgeModel,
                    promptId: "agenteval.intent_resolution.v1",
                    failureSeverity: "medium"),
                Weight: 0.50),

            new EvalComponent(
                Eval: new AtomicLlmEval(
                    evaluator: judge,
                    key: "intent_resolved",
                    name: "Intent Resolved",
                    category: "system-outcome",
                    version: "1.0.0",
                    criteria: new[]
                    {
                        "The agent's response fully resolves the identified intent",
                        "The resolution is complete — the user does not need to ask a follow-up to get the answer or action they requested",
                        "The resolution is correct — it matches what the user actually wanted, not a related but different outcome",
                    },
                    passThreshold: passThreshold,
                    judgeModel: judgeModel,
                    promptId: "agenteval.intent_resolution.v1",
                    failureSeverity: "medium"),
                Weight: 0.50),
        };

        _inner = new CompositeEval(
            key: "intent_resolution",
            name: "Intent Resolution",
            category: "system-outcome",
            version: "1.0.0",
            components: components,
            aggregation: WeightedSumAggregation.Instance,
            threshold: passThreshold);
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
