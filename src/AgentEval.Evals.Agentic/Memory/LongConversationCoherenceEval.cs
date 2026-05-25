// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Conversation;
using AgentEval.Evals.Agentic.Cost; // T3.1: EvaluatorCostMap doc-cref binding.

namespace AgentEval.Evals.Agentic.Memory;

/// <summary>
/// Evaluates whether an AI agent maintains consistency across all turns of a long
/// conversation (10+ turns), detecting contradictions, persona drift, or topic abandonment.
/// <para>
/// Reads prior turns from <c>EvalInput.Metadata["conversation_history"]</c> (a
/// <c>IReadOnlyList&lt;ConversationTurn&gt;</c> or any <c>IEnumerable&lt;ConversationTurn&gt;</c>).
/// If the key is absent or the list is empty the eval returns
/// <see cref="EvalResult.Skipped(IEval, string)"/>.
/// </para>
/// <para>
/// <b>Cost tier: HIGH</b> — the full conversation history is embedded in the judge prompt.
/// At 10+ turns × ~500 tokens each this evaluator carries 5,000+ input tokens per scenario.
/// See <see cref="EvaluatorCostMap.GetTier(string)"/> with key <c>"long_conversation_coherence"</c>.
/// </para>
/// </summary>
public sealed class LongConversationCoherenceEval : IEval
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
    /// Initialises a new <see cref="LongConversationCoherenceEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score long-conversation coherence.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.80.</param>
    public LongConversationCoherenceEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.80)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "long_conversation_coherence",
            name: "Long Conversation Coherence",
            category: "memory",
            version: "1.0.0",
            criteria: new[]
            {
                "The agent does not contradict statements or commitments made in earlier turns",
                "The agent maintains a consistent persona, tone, and style throughout the conversation",
                "The agent does not abandon or ignore topics that remain unresolved from earlier turns",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.long_conversation_coherence.v1",
            failureSeverity: "medium");
    }

    /// <inheritdoc/>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        var history = ConversationHistoryHelper.TryGetHistory(input);
        if (history is null || history.Count == 0)
            return EvalResult.Skipped(this,
                $"LongConversationCoherenceEval requires EvalInput.Metadata[\"{ConversationHistoryHelper.MetadataKey}\"] with at least 1 prior turn.");

        // Synthesize an enriched query that includes the full history for the judge.
        var enriched = new EvalInput(
            Query: $"{ConversationHistoryHelper.FormatTranscript(history)}\n\nCurrent user query: {input.Query}",
            Response: input.Response,
            Metadata: input.Metadata);

        return await _inner.EvaluateAsync(enriched, ct);
    }
}
