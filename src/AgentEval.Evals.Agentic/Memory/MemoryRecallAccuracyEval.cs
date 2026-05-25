// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Conversation;
using AgentEval.Evals.Agentic.Cost; // T3.1: EvaluatorCostMap doc-cref binding.

namespace AgentEval.Evals.Agentic.Memory;

/// <summary>
/// Evaluates whether an AI agent correctly recalls facts that were established in earlier
/// conversation turns, without confabulating information that was never stated.
/// <para>
/// Reads prior turns from <c>EvalInput.Metadata["conversation_history"]</c> (a
/// <c>IReadOnlyList&lt;ConversationTurn&gt;</c> or any <c>IEnumerable&lt;ConversationTurn&gt;</c>).
/// If the key is absent or the list is empty the eval returns
/// <see cref="EvalResult.Skipped(IEval, string)"/>.
/// </para>
/// <para>
/// <b>Cost tier: HIGH</b> — the full conversation history is embedded in the judge prompt.
/// At 10 turns × ~500 tokens each this evaluator carries ~5,000 input tokens per scenario.
/// See <see cref="EvaluatorCostMap.GetTier(string)"/> with key <c>"memory_recall_accuracy"</c>.
/// </para>
/// </summary>
public sealed class MemoryRecallAccuracyEval : IEval
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
    /// Initialises a new <see cref="MemoryRecallAccuracyEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score memory recall.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.80.</param>
    public MemoryRecallAccuracyEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.80)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "memory_recall_accuracy",
            name: "Memory Recall Accuracy",
            category: "memory",
            version: "1.0.0",
            criteria: new[]
            {
                "The agent correctly recalls facts established in prior conversation turns",
                "The agent does not confabulate facts that were never stated in the conversation history",
                "The agent's response is consistent with the established facts in the conversation history",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.memory_recall_accuracy.v1",
            failureSeverity: "medium");
    }

    /// <inheritdoc/>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        var history = ConversationHistoryHelper.TryGetHistory(input);
        if (history is null || history.Count == 0)
            return EvalResult.Skipped(this,
                $"MemoryRecallAccuracyEval requires EvalInput.Metadata[\"{ConversationHistoryHelper.MetadataKey}\"] with at least 1 prior turn.");

        // Synthesize an enriched query that includes the history for the judge.
        var enriched = new EvalInput(
            Query: $"{ConversationHistoryHelper.FormatTranscript(history)}\n\nCurrent user query: {input.Query}",
            Response: input.Response,
            Metadata: input.Metadata);

        return await _inner.EvaluateAsync(enriched, ct);
    }
}
