// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Adversarial;
using AgentEval.Evals.Agentic.Calibration;
using AgentEval.Evals.Agentic.Efficiency;
using AgentEval.Evals.Agentic.Memory;
using AgentEval.Evals.Agentic.MultiTurn;
using AgentEval.Evals.Agentic.Process;
using AgentEval.Evals.Agentic.Quality;
using AgentEval.Evals.Agentic.Reasoning;
using AgentEval.Evals.Agentic.Safety;
using AgentEval.Evals.Agentic.Safety.Policy;
using AgentEval.Evals.Agentic.StochasticStability;
using AgentEval.Evals.Agentic.System;
using AgentEval.Evals.Agentic.Telemetry;
using AgentEval.Evals.Agentic.UX;
using Xunit;

namespace AgentEval.Tests.Agentic;

/// <summary>
/// Shared fixture helper that builds an <see cref="IEval"/> instance for a given
/// evaluator key, backed by a fixed-score stub evaluator.
/// </summary>
internal static class AgenticEvaluatorFixture
{
    /// <summary>
    /// Builds the <see cref="IEval"/> corresponding to <paramref name="key"/> using
    /// <paramref name="judge"/> as the LLM evaluator backing.
    /// </summary>
    internal static IEval BuildEvaluator(string key, IEvaluator judge) => key switch
    {
        // ── Phase 1: System + Process evaluators ──────────────────────────────
        "task_completion"           => new TaskCompletionEval(judge),
        "task_adherence"            => new TaskAdherenceEval(judge),
        "intent_identification"     => new IntentIdentificationEval(judge),
        "intent_resolution"         => new IntentResolutionEval(judge),
        "task_navigation_efficiency"=> new TaskNavigationEfficiencyEval(judge),
        "tool_selection"            => new ToolSelectionEval(judge),
        "tool_input_accuracy"       => new ToolInputAccuracyEval(judge),
        "tool_output_utilization"   => new ToolOutputUtilizationEval(judge),
        "tool_call_success"         => new ToolCallSuccessEval(judge),
        "tool_efficiency"           => new ToolEfficiencyEval(judge),
        "tool_call_accuracy"        => new ToolCallAccuracyAggregateEval(judge),
        // ── Phase 2: RAG / Quality evaluators ─────────────────────────────────
        "groundedness"              => new GroundednessEval(judge),
        "relevance"                 => new RelevanceEval(judge),
        "coherence"                 => new CoherenceEval(judge),
        "fluency"                   => new FluencyEval(judge),
        "similarity"                => new SimilarityEval(judge),
        "response_completeness"     => new ResponseCompletenessEval(judge),
        "f1_score"                  => new F1ScoreEval(),
        "qa_composite"              => new QaCompositeEval(judge),
        // ── Phase 4: Safety evaluators ────────────────────────────────────────
        "prohibited_actions"        => new ProhibitedActionsEval(
                                           judge,
                                           new StaticPolicyResolver(new ProhibitedActionPolicy([], [], [], [])),
                                           "test-subject"),
        "sensitive_data_leakage"    => new SensitiveDataLeakageEval(judge),
        "indirect_attack"           => new IndirectAttackEval(judge),
        "hate_unfairness"           => new HateUnfairnessEval(judge),
        "sexual"                    => new SexualEval(judge),
        "violence"                  => new ViolenceEval(judge),
        "self_harm"                 => new SelfHarmEval(judge),
        "protected_material"        => new ProtectedMaterialEval(judge),
        "code_vulnerability"        => new CodeVulnerabilityEval(judge),
        "ungrounded_attributes"     => new UngroundedAttributesEval(judge),
        "system_prompt_leakage"     => new SystemPromptLeakageEval(judge),
        "unsafe_tool_use"           => new UnsafeToolUseEval(judge),
        // ── Phase 5: Telemetry evaluators (pure-code — no judge needed) ───────
        "latency"                   => new LatencyEval(),
        "token_usage"               => new TokenUsageEval(),
        "cost"                      => new CostEval(),
        "error_rate"                => new ErrorRateEval(),
        "retry_rate"                => new RetryRateEval(),
        "tool_latency"              => new ToolLatencyEval(),
        // ── Phase 5: Stochastic Stability ─────────────────────────────────────
        "stochastic_stability"      => new StochasticStabilityEval(),
        // ── Phase 6: Memory evaluators ────────────────────────────────────────
        "memory_recall_accuracy"    => new MemoryRecallAccuracyEval(judge),
        "long_conversation_coherence" => new LongConversationCoherenceEval(judge),
        // ── Phase 6: Multi-turn evaluators ────────────────────────────────────
        "turn_coherence"            => new TurnCoherenceEval(judge),
        "clarification_appropriateness" => new ClarificationAppropriatenessEval(judge),
        "goal_tracking"             => new GoalTrackingEval(judge),
        // ── Phase 6: Reasoning evaluators ─────────────────────────────────────
        "plan_formulation_quality"  => new PlanFormulationQualityEval(judge),
        "goal_decomposition_quality" => new GoalDecompositionQualityEval(judge),
        "reasoning_correctness"     => new ReasoningCorrectnessEval(judge),
        "intermediate_step_hallucination" => new IntermediateStepHallucinationEval(judge),
        // ── Phase 6: Calibration evaluators ───────────────────────────────────
        "confidence_calibration"    => new ConfidenceCalibrationEval(judge),
        "uncertainty_acknowledgment" => new UncertaintyAcknowledgmentEval(judge),
        "self_correction_quality"   => new SelfCorrectionQualityEval(judge),
        // ── Phase 6: Adversarial evaluators ───────────────────────────────────
        "direct_injection"          => new DirectInjectionEval(judge),
        "persona_attack"            => new PersonaAttackEval(judge),
        "jailbreak_resistance"      => new JailbreakResistanceEval(judge),
        // ── Phase 6: UX evaluators ────────────────────────────────────────────
        "verbosity_appropriateness" => new VerbosityAppropriatenessEval(judge),
        "tone_appropriateness"      => new ToneAppropriatenessEval(judge),
        "refusal_quality"           => new RefusalQualityEval(judge),
        // ── Phase 6: Efficiency evaluators (pure-code — no judge needed) ──────
        "cost_quality_efficiency"   => new CostQualityEfficiencyEval(),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown evaluator key.")
    };
}

/// <summary>
/// Stub evaluator that always returns a fixed score for every criterion.
/// Mirrors the pattern from <c>EuAiActFixture.FixedScoreEvaluator</c>.
/// </summary>
internal sealed class FixedScoreEvaluator(int score) : IEvaluator
{
    public Task<EvaluationResult> EvaluateAsync(
        string input,
        string output,
        IEnumerable<string> criteria,
        CancellationToken ct = default) =>
        Task.FromResult(new EvaluationResult
        {
            OverallScore = score,
            Summary = "stub",
            CriteriaResults = criteria
                .Select(x => new CriterionResult
                {
                    Criterion = x,
                    Met = score >= 50,
                    Explanation = "stub"
                })
                .ToList()
        });
}
