// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Static map from evaluator key (e.g. <c>"task_completion"</c>) to its
/// <see cref="EvaluatorCostTier"/>. Used by the <c>--budget-tier</c> CLI filter and by
/// cost-aware reporting.
/// </summary>
/// <remarks>
/// <para>
/// Populated for all plan-05 + plan-06 evaluators. Unknown keys are treated as
/// <see cref="EvaluatorCostTier.Medium"/> by default — see
/// <see cref="GetTier(string)"/>. Consumers shipping their own evaluators can extend
/// the map at runtime via <see cref="Register(string, EvaluatorCostTier)"/>.
/// </para>
/// </remarks>
public static class EvaluatorCostMap
{
    private static readonly Dictionary<string, EvaluatorCostTier> s_tiers = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Plan 05 — System (5) ───────────────────────────────────────────
        ["task_completion"]                 = EvaluatorCostTier.Low,
        ["task_adherence"]                  = EvaluatorCostTier.Medium,    // composite of 5 sub-LLM-judges
        ["intent_identification"]           = EvaluatorCostTier.Low,
        ["intent_resolution"]               = EvaluatorCostTier.Medium,    // composite of 2 sub-LLM-judges
        ["task_navigation_efficiency"]      = EvaluatorCostTier.Low,       // hybrid: AtomicCodeEval (free) + 1 AtomicLlmEval call

        // ── Plan 05 — Process / tool (6) ───────────────────────────────────
        ["tool_selection"]                  = EvaluatorCostTier.Low,
        ["tool_input_accuracy"]             = EvaluatorCostTier.Low,       // hybrid: schema check (free) + 1 LLM call
        ["tool_output_utilization"]         = EvaluatorCostTier.Low,
        ["tool_call_success"]               = EvaluatorCostTier.Trivial,   // deterministic-first; LLM only on free-text fallback
        ["tool_efficiency"]                 = EvaluatorCostTier.Low,
        ["tool_call_accuracy"]              = EvaluatorCostTier.Medium,    // composite of 5 sub-evaluators

        // ── Plan 05 — Quality / RAG (8) ────────────────────────────────────
        ["groundedness"]                    = EvaluatorCostTier.Medium,    // composite of 4 sub-LLM-judges
        ["relevance"]                       = EvaluatorCostTier.Low,
        ["coherence"]                       = EvaluatorCostTier.Low,
        ["fluency"]                         = EvaluatorCostTier.Low,
        ["similarity"]                      = EvaluatorCostTier.Low,
        ["response_completeness"]           = EvaluatorCostTier.Low,
        ["f1_score"]                        = EvaluatorCostTier.Trivial,   // pure code
        ["qa_composite"]                    = EvaluatorCostTier.Medium,    // composite of 7

        // ── Plan 05 — Adjudication / meta (3 + 1 wrapper) ──────────────────
        // AdjudicatedMultiJudgeWrapper itself isn't a leaf — it inherits cost from wrapped panel.
        ["judge_agreement"]                 = EvaluatorCostTier.Trivial,   // pure code over input metadata
        ["calibration_accuracy"]            = EvaluatorCostTier.Trivial,
        ["judge_drift"]                     = EvaluatorCostTier.Trivial,

        // ── Plan 05 — Safety (12) ──────────────────────────────────────────
        ["prohibited_actions"]              = EvaluatorCostTier.Low,       // policy-first; LLM only on ambiguity
        ["sensitive_data_leakage"]          = EvaluatorCostTier.Low,       // regex-first; LLM only on no-match
        ["indirect_attack"]                 = EvaluatorCostTier.Low,
        ["hate_unfairness"]                 = EvaluatorCostTier.Low,
        ["sexual"]                          = EvaluatorCostTier.Low,
        ["violence"]                        = EvaluatorCostTier.Low,
        ["self_harm"]                       = EvaluatorCostTier.Low,
        ["protected_material"]              = EvaluatorCostTier.Low,
        ["code_vulnerability"]              = EvaluatorCostTier.Low,
        ["ungrounded_attributes"]           = EvaluatorCostTier.Low,
        ["system_prompt_leakage"]           = EvaluatorCostTier.Low,       // hybrid: regex-first
        ["unsafe_tool_use"]                 = EvaluatorCostTier.Low,

        // ── Plan 05 — Telemetry (6) ────────────────────────────────────────
        ["latency"]                         = EvaluatorCostTier.Trivial,   // all telemetry is pure code
        ["token_usage"]                     = EvaluatorCostTier.Trivial,
        ["cost"]                            = EvaluatorCostTier.Trivial,
        ["error_rate"]                      = EvaluatorCostTier.Trivial,
        ["retry_rate"]                      = EvaluatorCostTier.Trivial,
        ["tool_latency"]                    = EvaluatorCostTier.Trivial,

        // ── Plan 05 — Stability (1) ────────────────────────────────────────
        ["stochastic_stability"]            = EvaluatorCostTier.Trivial,   // pure code

        // ── Plan 06 — Memory (2) ───────────────────────────────────────────
        ["memory_recall_accuracy"]          = EvaluatorCostTier.High,      // full conversation history in judge prompt
        ["long_conversation_coherence"]     = EvaluatorCostTier.High,

        // ── Plan 06 — Multi-turn (3) ───────────────────────────────────────
        ["turn_coherence"]                  = EvaluatorCostTier.Medium,    // previous turn only — not full history
        ["clarification_appropriateness"]   = EvaluatorCostTier.Low,
        ["goal_tracking"]                   = EvaluatorCostTier.High,      // full history

        // ── Plan 06 — Reasoning (4) ────────────────────────────────────────
        ["plan_formulation_quality"]        = EvaluatorCostTier.Medium,
        ["goal_decomposition_quality"]      = EvaluatorCostTier.Low,
        ["reasoning_correctness"]           = EvaluatorCostTier.Medium,
        ["intermediate_step_hallucination"] = EvaluatorCostTier.Medium,    // cross-checks claims against tool calls

        // ── Plan 06 — Confidence calibration (3) ───────────────────────────
        ["confidence_calibration"]          = EvaluatorCostTier.Low,
        ["uncertainty_acknowledgment"]      = EvaluatorCostTier.Low,
        ["self_correction_quality"]         = EvaluatorCostTier.Medium,    // needs original + correction turn

        // ── Plan 06 — Direct adversarial (3) ───────────────────────────────
        ["direct_injection"]                = EvaluatorCostTier.Low,
        ["persona_attack"]                  = EvaluatorCostTier.Low,
        ["jailbreak_resistance"]            = EvaluatorCostTier.Medium,    // runs N known patterns per scenario

        // ── Plan 06 — UX (3) ───────────────────────────────────────────────
        ["verbosity_appropriateness"]       = EvaluatorCostTier.Low,
        ["tone_appropriateness"]            = EvaluatorCostTier.Low,
        ["refusal_quality"]                 = EvaluatorCostTier.Low,

        // ── Plan 06 — Cross-cutting (1) ────────────────────────────────────
        ["cost_quality_efficiency"]         = EvaluatorCostTier.Trivial,
    };

    /// <summary>
    /// Returns the cost tier for <paramref name="evaluatorKey"/>. Unknown keys default to
    /// <see cref="EvaluatorCostTier.Medium"/> (a conservative middle ground that filters
    /// neither too aggressively nor too permissively).
    /// </summary>
    /// <param name="evaluatorKey">Evaluator key, e.g. <c>"task_completion"</c>.</param>
    /// <returns>The cost tier; defaults to <see cref="EvaluatorCostTier.Medium"/> for unknown keys.</returns>
    public static EvaluatorCostTier GetTier(string evaluatorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluatorKey);
        return s_tiers.TryGetValue(evaluatorKey, out var tier) ? tier : EvaluatorCostTier.Medium;
    }

    /// <summary>
    /// Registers a custom evaluator's cost tier. Useful for downstream consumers shipping
    /// their own evaluators that should participate in the <c>--budget-tier</c> filter.
    /// </summary>
    /// <param name="evaluatorKey">Evaluator key, e.g. <c>"my_custom_eval"</c>.</param>
    /// <param name="tier">The cost tier to register.</param>
    public static void Register(string evaluatorKey, EvaluatorCostTier tier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluatorKey);
        s_tiers[evaluatorKey] = tier;
    }

    /// <summary>True if <paramref name="evaluatorTier"/> is at or below <paramref name="budgetTier"/>.</summary>
    /// <remarks>
    /// Used by the <c>--budget-tier</c> filter: an evaluator is included when its tier is
    /// less-than-or-equal to the configured budget tier (so <c>--budget-tier medium</c>
    /// includes Trivial + Low + Medium evaluators but excludes High).
    /// </remarks>
    public static bool IsWithinBudget(EvaluatorCostTier evaluatorTier, EvaluatorCostTier budgetTier) =>
        (int)evaluatorTier <= (int)budgetTier;

    /// <summary>
    /// True if <paramref name="evaluatorKey"/> has an explicit registration (vs. falling
    /// back to <see cref="EvaluatorCostTier.Medium"/> via <see cref="GetTier"/>'s default).
    /// </summary>
    /// <remarks>
    /// Used by Mission Control's <c>Query.runCostBreakdown</c> resolver to distinguish
    /// "this evaluator's cost rolls into Medium because we know it's medium" from
    /// "we don't know what tier this evaluator is, surface its cost separately as
    /// `unknownKeyCost`". Plan-08 MC1.4.3.
    /// </remarks>
    public static bool IsRegistered(string evaluatorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluatorKey);
        return s_tiers.ContainsKey(evaluatorKey);
    }
}
