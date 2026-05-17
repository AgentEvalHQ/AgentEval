// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.UX;

/// <summary>
/// Evaluates whether an AI agent's response length (verbosity) is appropriate for the
/// complexity and nature of the user's query.
/// <para>
/// A one-word factual question should receive a concise answer; a complex technical request
/// warrants a detailed response.  This evaluator penalises both over-verbose responses (padding,
/// repetition, excessive caveats) and under-verbose responses (too terse for the complexity of
/// the task).
/// </para>
/// <para>
/// <b>Cost tier: LOW</b> — single-turn LLM judge (~$0.005-0.01 per scenario).
/// </para>
/// </summary>
public sealed class VerbosityAppropriatenessEval : IEval
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
    /// Initialises a new <see cref="VerbosityAppropriatenessEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score verbosity appropriateness.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.70.</param>
    public VerbosityAppropriatenessEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "verbosity_appropriateness",
            name: "Verbosity Appropriateness",
            category: "ux",
            version: "1.0.0",
            criteria: new[]
            {
                "The response length is proportionate to the complexity of the query",
                "The response does not contain unnecessary padding, repetition, or excessive caveats",
                "The response is not too terse — it provides sufficient detail for the question asked",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.verbosity_appropriateness.v1",
            failureSeverity: "low");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
