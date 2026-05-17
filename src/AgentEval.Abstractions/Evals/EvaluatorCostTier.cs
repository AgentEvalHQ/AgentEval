// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Per-scenario cost tier for an evaluator, used by the <c>--budget-tier</c> CLI filter
/// and by cost-aware reporting.
/// </summary>
/// <remarks>
/// <para>
/// Cost figures below are estimates per scenario assuming a GPT-4-class judge. Smaller or
/// cheaper models would shift the tiers; the canonical reference model is GPT-4-class for
/// v1 of the cost framework.
/// </para>
/// <list type="table">
///   <listheader><term>Tier</term><description>Per-scenario cost</description></listheader>
///   <item><term><see cref="Trivial"/></term><description>$0 — pure code (no LLM): F1Score, telemetry, StochasticStability, meta-evaluators.</description></item>
///   <item><term><see cref="Low"/></term><description>~$0.005-$0.01 — single-turn LLM judge (TaskCompletion, Relevance, content-safety, most direct adversarial).</description></item>
///   <item><term><see cref="Medium"/></term><description>~$0.01-$0.05 — LLM judge + small history (2-3 turns), or composite of 2-3 sub-LLM-judges (TaskAdherence with 5 sub-dims, TurnCoherence).</description></item>
///   <item><term><see cref="High"/></term><description>~$0.05-$0.25 — LLM judge + full multi-turn history (10+ turns) OR multi-judge consensus (MemoryRecall, LongConversationCoherence, GoalTracking).</description></item>
/// </list>
/// </remarks>
public enum EvaluatorCostTier
{
    /// <summary>Pure code; no LLM call. ~$0 per scenario.</summary>
    Trivial = 0,

    /// <summary>Single-turn LLM judge. ~$0.005-0.01 per scenario.</summary>
    Low = 1,

    /// <summary>LLM judge + small history (2-3 turns) or multi-sub-dim composite. ~$0.01-0.05 per scenario.</summary>
    Medium = 2,

    /// <summary>LLM judge + full multi-turn history or multi-judge consensus. ~$0.05-0.25 per scenario.</summary>
    High = 3,
}
