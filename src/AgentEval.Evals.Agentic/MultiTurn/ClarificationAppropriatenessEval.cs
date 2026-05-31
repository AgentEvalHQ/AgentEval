// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Conversation;
using AgentEval.Evals.Agentic.Cost; // T3.1: EvaluatorCostMap doc-cref binding.

namespace AgentEval.Evals.Agentic.MultiTurn;

/// <summary>
/// Evaluates whether the agent asks appropriate clarifying questions when the user's query
/// is ambiguous: not too many questions, not too few, and targeting the right ambiguities.
/// <para>
/// This evaluator can operate in single-turn mode (query + response alone) or with history.
/// <c>EvalInput.Metadata["conversation_history"]</c> is optional; when present the full
/// history is included in the judge prompt for richer context.
/// </para>
/// <para>
/// <b>Cost tier: LOW</b> — single-turn judgment of the clarification quality; history is
/// optional and small when provided.
/// See <see cref="EvaluatorCostMap.GetTier(string)"/> with key <c>"clarification_appropriateness"</c>.
/// </para>
/// </summary>
public sealed class ClarificationAppropriatenessEval : IEval
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
    /// Initialises a new <see cref="ClarificationAppropriatenessEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score clarification appropriateness.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.75.</param>
    public ClarificationAppropriatenessEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.75)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "clarification_appropriateness",
            name: "Clarification Appropriateness",
            category: "multi-turn",
            version: "1.0.0",
            criteria: new[]
            {
                "When the query is ambiguous, the agent asks exactly the clarifying questions needed — not more, not fewer",
                "The clarifying questions target the specific ambiguity that would most change the agent's response",
                "When the query is clear, the agent does not ask unnecessary clarifying questions",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.clarification_appropriateness.v1",
            failureSeverity: "low");
    }

    /// <inheritdoc/>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        // History is optional for this evaluator — fall back to single-turn evaluation if absent.
        var history = ConversationHistoryHelper.TryGetHistory(input);

        // Preserve every other EvalInput field via WithEnrichedQuery (BUG-57).
        var evalInput = history is { Count: > 0 }
            ? ConversationHistoryHelper.WithEnrichedQuery(
                input, ConversationHistoryHelper.FormatTranscript(history))
            : input;

        return await _inner.EvaluateAsync(evalInput, ct);
    }
}
