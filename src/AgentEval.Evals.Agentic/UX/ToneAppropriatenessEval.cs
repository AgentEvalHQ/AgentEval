// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.UX;

/// <summary>
/// Evaluates whether an AI agent's tone matches the user's emotional register and context.
/// <para>
/// A high score means the agent is neither preachy, nor overly formal/robotic, nor
/// excessively empathetic when the situation does not call for it.  The agent should mirror
/// the user's tone — casual for casual queries, professional for business contexts,
/// empathetic for sensitive topics.
/// </para>
/// <para>
/// <b>Cost tier: LOW</b> — single-turn LLM judge (~$0.005-0.01 per scenario).
/// </para>
/// </summary>
public sealed class ToneAppropriatenessEval : IEval
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
    /// Initialises a new <see cref="ToneAppropriatenessEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score tone appropriateness.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.75.</param>
    public ToneAppropriatenessEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.75)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "tone_appropriateness",
            name: "Tone Appropriateness",
            category: "ux",
            version: "1.0.0",
            criteria: new[]
            {
                "The agent's tone matches the user's emotional register and context",
                "The agent is not preachy, lecturing, or moralising when the user has not asked for ethical guidance",
                "The agent is not overly robotic, formal, or detached in casual conversational contexts",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.tone_appropriateness.v1",
            failureSeverity: "low");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
