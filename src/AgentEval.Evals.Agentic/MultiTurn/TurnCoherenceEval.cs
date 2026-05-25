// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Conversation;
using AgentEval.Evals.Agentic.Cost; // T3.1: EvaluatorCostMap doc-cref binding.

namespace AgentEval.Evals.Agentic.MultiTurn;

/// <summary>
/// Evaluates whether the agent's current response coherently addresses the immediately
/// preceding turn rather than ignoring it or pivoting without acknowledgement.
/// <para>
/// Reads the <b>last turn only</b> from <c>EvalInput.Metadata["conversation_history"]</c>
/// (a <c>IReadOnlyList&lt;ConversationTurn&gt;</c> or any <c>IEnumerable&lt;ConversationTurn&gt;</c>).
/// Only the final turn is extracted and injected as context, keeping the judge prompt small.
/// If the key is absent or the list is empty the eval returns
/// <see cref="EvalResult.Skipped(IEval, string)"/>.
/// </para>
/// <para>
/// <b>Cost tier: MEDIUM</b> — only the previous turn is included in the judge prompt
/// (not the full history), giving a small context footprint.
/// See <see cref="EvaluatorCostMap.GetTier(string)"/> with key <c>"turn_coherence"</c>.
/// </para>
/// </summary>
public sealed class TurnCoherenceEval : IEval
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
    /// Initialises a new <see cref="TurnCoherenceEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score turn coherence.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.85.</param>
    public TurnCoherenceEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.85)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "turn_coherence",
            name: "Turn Coherence",
            category: "multi-turn",
            version: "1.0.0",
            criteria: new[]
            {
                "The response directly addresses or acknowledges the content of the immediately preceding turn",
                "The response does not ignore, contradict, or pivot away from the previous turn without explanation",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.turn_coherence.v1",
            failureSeverity: "medium");
    }

    /// <inheritdoc/>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        var history = ConversationHistoryHelper.TryGetHistory(input);
        if (history is null || history.Count == 0)
            return EvalResult.Skipped(this,
                $"TurnCoherenceEval requires EvalInput.Metadata[\"{ConversationHistoryHelper.MetadataKey}\"] with at least 1 prior turn.");

        // Only the immediately-preceding turn is needed (MEDIUM cost tier — small context).
        var enriched = new EvalInput(
            Query: $"{ConversationHistoryHelper.FormatPreviousTurn(history[^1])}\n\nCurrent user query: {input.Query}",
            Response: input.Response,
            Metadata: input.Metadata);

        return await _inner.EvaluateAsync(enriched, ct);
    }
}
