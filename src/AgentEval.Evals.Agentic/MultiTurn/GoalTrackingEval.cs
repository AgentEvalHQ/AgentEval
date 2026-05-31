// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Conversation;
using AgentEval.Evals.Agentic.Cost; // T3.1: EvaluatorCostMap doc-cref binding.

namespace AgentEval.Evals.Agentic.MultiTurn;

/// <summary>
/// Evaluates whether the agent maintains the user's original goal across multiple turns,
/// even when distractor topics, off-topic questions, or redirect attempts arrive.
/// <para>
/// Reads prior turns from <c>EvalInput.Metadata["conversation_history"]</c> (a
/// <c>IReadOnlyList&lt;ConversationTurn&gt;</c> or any <c>IEnumerable&lt;ConversationTurn&gt;</c>).
/// The full history is embedded in the judge prompt so the judge can identify the original
/// goal stated in turn 1 and check whether it remains on track in the current response.
/// If the key is absent or the list is empty the eval returns
/// <see cref="EvalResult.Skipped(IEval, string)"/>.
/// </para>
/// <para>
/// <b>Cost tier: HIGH</b> — the full conversation history is embedded in the judge prompt.
/// At 10+ turns × ~500 tokens each this evaluator carries 5,000+ input tokens per scenario.
/// See <see cref="EvaluatorCostMap.GetTier(string)"/> with key <c>"goal_tracking"</c>.
/// </para>
/// </summary>
public sealed class GoalTrackingEval : IEval
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
    /// Initialises a new <see cref="GoalTrackingEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score goal tracking.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.80.</param>
    public GoalTrackingEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.80)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "goal_tracking",
            name: "Goal Tracking",
            category: "multi-turn",
            version: "1.0.0",
            criteria: new[]
            {
                "The agent's response remains aligned with the original goal established in the first user turn",
                "The agent does not abandon the original goal when distractor topics or side-questions arrive",
                "When the agent temporarily addresses a distractor, it returns to the original goal afterwards",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.goal_tracking.v1",
            failureSeverity: "medium");
    }

    /// <inheritdoc/>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        var history = ConversationHistoryHelper.TryGetHistory(input);
        if (history is null || history.Count == 0)
            return EvalResult.Skipped(this,
                $"GoalTrackingEval requires EvalInput.Metadata[\"{ConversationHistoryHelper.MetadataKey}\"] with at least 1 prior turn.");

        // Embed the full history so the judge can identify the original goal from turn 1 —
        // preserving every other EvalInput field via WithEnrichedQuery (BUG-57).
        var enriched = ConversationHistoryHelper.WithEnrichedQuery(
            input, ConversationHistoryHelper.FormatTranscript(history));

        return await _inner.EvaluateAsync(enriched, ct);
    }
}
