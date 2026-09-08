// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Adversarial;
using AgentEval.Evals.Agentic.Calibration;
using AgentEval.Evals.Agentic.Process;
using AgentEval.Evals.Agentic.Quality;
using AgentEval.Evals.Agentic.Reasoning;
using AgentEval.Evals.Agentic.Safety;
using AgentEval.Evals.Agentic.System;
using AgentEval.Evals.Agentic.UX;

namespace AgentEval.Evals.Agentic;

/// <summary>
/// Registers the 40 calibration-dispatchable agentic evaluators with
/// <see cref="EvalRegistry.Shared"/> (ADR-031 C1, built to the corrected
/// <c>Key → Func&lt;IEvaluator?, string?, IEval&gt;</c> signature — see
/// <see cref="EvalRegistration"/> and <c>MEASUREMENT_STATUS</c> §67.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where this table came from.</b> It was a hand-authored 40-entry
/// <c>Dictionary&lt;string, IEval&gt;</c> inside <c>BenchAgenticCalibrateCommand.RunCoreAsync</c>,
/// 89 lines of construction in a CLI command file, invisible to anything that wanted to ask "what
/// evaluator keys exist?" without running the calibration command. C1's first deliverable was
/// deleting it; this file is where the entries went. <b>The keys, the eval types, the constructor
/// arguments and the case-insensitive matching are carried over unchanged</b> — the only difference
/// is that construction is deferred until a judge exists.
/// </para>
/// <para>
/// <b>Path A' (v1.1) scope, unchanged.</b> 40 dispatched entries across 9 categories: system (5),
/// process (6), ux (3), adversarial (5), reasoning (2 — the 3 trace-dependent ones are carved out),
/// calibration (2), memory (0 — fully carved), quality (6 — <c>f1_score</c> carved), safety (11).
/// The 20-key carve-out list and its per-bucket rationale stay in
/// <c>BenchAgenticCalibrateCommand.s_carveOutKeys</c>, which is where the calibration report reads
/// them from.
/// </para>
/// <para>
/// <b>Stub-argument note (carried over verbatim).</b> These evaluators receive only the judge and
/// the judge-model identifier — additional constructor arguments (custom regex patterns,
/// <c>IContentSafetyClient</c>, <c>IPolicyResolver</c>, …) are left at their defaults, so the table
/// reflects "calibrate this evaluator's LLM judge", not the full production wiring.
/// <c>ProhibitedActionsEval</c> is the one Safety eval that cannot satisfy this contract (it
/// requires an <c>IPolicyResolver</c> and a subject id) and is therefore not dispatched at all.
/// </para>
/// <para>
/// <b>Registration is both automatic and explicit.</b> <see cref="Register"/> carries
/// <c>[ModuleInitializer]</c> so any consumer of this assembly gets the table for free, and it is
/// also <c>public</c> so a caller that must not depend on module-initialiser timing can invoke it
/// directly. It is idempotent, so calling it after the module initializer has already run is a
/// no-op — a property the registry's conflict/idempotence path is tested for.
/// </para>
/// </remarks>
public static class AgenticEvalRegistration
{
    /// <summary>
    /// The number of evaluator keys this registrar contributes. Pinned as a constant so a dropped
    /// or duplicated entry is a test failure rather than a silently smaller calibration run.
    /// </summary>
    public const int DispatchedEvaluatorCount = 40;

    // CA2255: ModuleInitializer is the canonical auto-registration mechanism in this codebase
    // (ADR-017 Convention 3; see AgenticBenchmarkRegistration for the benchmark-family twin).
    // Library use here is intentional — every consumer of AgentEval.Evals.Agentic.dll should find
    // the evaluator keys present in EvalRegistry.Shared without an explicit call.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register() => RegisterInto(EvalRegistry.Shared);

    /// <summary>
    /// Registers the 40 entries into <paramref name="registry"/>. Exposed separately from
    /// <see cref="Register"/> so a test can populate an isolated registry rather than the shared
    /// one.
    /// </summary>
    /// <param name="registry">Target registry. Required.</param>
    public static void RegisterInto(IEvalRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var owner = typeof(AgenticEvalRegistration).Assembly.GetName().Name;

        void Add<TEval>(string key, Func<IEvaluator?, string?, IEval> factory) where TEval : IEval
            => registry.Register(new EvalRegistration(key, typeof(TEval), factory, owner));

        // ── System evaluators (5) ────────────────────────────────────────────
        Add<TaskCompletionEval>("task_completion", (j, m) => new TaskCompletionEval(j!, judgeModel: m));
        Add<TaskAdherenceEval>("task_adherence", (j, m) => new TaskAdherenceEval(j!, judgeModel: m));
        Add<IntentIdentificationEval>("intent_identification", (j, m) => new IntentIdentificationEval(j!, judgeModel: m));
        Add<IntentResolutionEval>("intent_resolution", (j, m) => new IntentResolutionEval(j!, judgeModel: m));
        Add<TaskNavigationEfficiencyEval>("task_navigation_efficiency", (j, m) => new TaskNavigationEfficiencyEval(j!, judgeModel: m));

        // ── Process evaluators (6) ───────────────────────────────────────────
        Add<ToolSelectionEval>("tool_selection", (j, m) => new ToolSelectionEval(j!, judgeModel: m));
        Add<ToolInputAccuracyEval>("tool_input_accuracy", (j, m) => new ToolInputAccuracyEval(j!, judgeModel: m));
        Add<ToolOutputUtilizationEval>("tool_output_utilization", (j, m) => new ToolOutputUtilizationEval(j!, judgeModel: m));
        Add<ToolCallSuccessEval>("tool_call_success", (j, m) => new ToolCallSuccessEval(j!, judgeModel: m));
        Add<ToolEfficiencyEval>("tool_efficiency", (j, m) => new ToolEfficiencyEval(j!, judgeModel: m));
        // The aggregate uses the convenience single-judge overload.
        Add<ToolCallAccuracyAggregateEval>("tool_call_accuracy", (j, m) => new ToolCallAccuracyAggregateEval(j!, judgeModel: m));

        // ── UX evaluators (3) ────────────────────────────────────────────────
        Add<VerbosityAppropriatenessEval>("verbosity_appropriateness", (j, m) => new VerbosityAppropriatenessEval(j!, judgeModel: m));
        Add<ToneAppropriatenessEval>("tone_appropriateness", (j, m) => new ToneAppropriatenessEval(j!, judgeModel: m));
        Add<RefusalQualityEval>("refusal_quality", (j, m) => new RefusalQualityEval(j!, judgeModel: m));

        // ── Adversarial-resistance evaluators (5) ────────────────────────────
        // `prompt_leak` and `escalation_resistance` are calibration-only keys: prompt_leak
        // dispatches to SystemPromptLeakageEval (same operational concept, shorter table key);
        // escalation_resistance dispatches to JailbreakResistanceEval as a functional alias
        // (privilege-escalation prompts are jailbreak variants). Two keys naming one type is
        // legal — the registry keys on the string, and content-equality is per key.
        Add<DirectInjectionEval>("direct_injection", (j, m) => new DirectInjectionEval(j!, judgeModel: m));
        Add<PersonaAttackEval>("persona_attack", (j, m) => new PersonaAttackEval(j!, judgeModel: m));
        Add<JailbreakResistanceEval>("jailbreak_resistance", (j, m) => new JailbreakResistanceEval(j!, judgeModel: m));
        Add<SystemPromptLeakageEval>("prompt_leak", (j, m) => new SystemPromptLeakageEval(j!, judgeModel: m));
        Add<JailbreakResistanceEval>("escalation_resistance", (j, m) => new JailbreakResistanceEval(j!, judgeModel: m));

        // ── Reasoning evaluators (2 — Path A' carved out the 3 trace-dependent) ──
        Add<ReasoningCorrectnessEval>("reasoning_correctness", (j, m) => new ReasoningCorrectnessEval(j!, judgeModel: m));
        Add<GoalDecompositionQualityEval>("goal_decomposition_quality", (j, m) => new GoalDecompositionQualityEval(j!, judgeModel: m));

        // ── Confidence-calibration evaluators (2) ────────────────────────────
        Add<ConfidenceCalibrationEval>("confidence_calibration", (j, m) => new ConfidenceCalibrationEval(j!, judgeModel: m));
        Add<UncertaintyAcknowledgmentEval>("uncertainty_acknowledgment", (j, m) => new UncertaintyAcknowledgmentEval(j!, judgeModel: m));

        // ── Memory / multi-turn evaluators (0 — Path A' fully carved) ────────

        // ── Quality / RAG evaluators (6 — Path A' carved out f1_score) ───────
        Add<GroundednessEval>("groundedness", (j, m) => new GroundednessEval(j!, judgeModel: m));
        Add<RelevanceEval>("relevance", (j, m) => new RelevanceEval(j!, judgeModel: m));
        Add<CoherenceEval>("coherence", (j, m) => new CoherenceEval(j!, judgeModel: m));
        Add<FluencyEval>("fluency", (j, m) => new FluencyEval(j!, judgeModel: m));
        Add<SimilarityEval>("similarity", (j, m) => new SimilarityEval(j!, judgeModel: m));
        Add<ResponseCompletenessEval>("response_completeness", (j, m) => new ResponseCompletenessEval(j!, judgeModel: m));

        // ── Safety / content-classifier evaluators (11) ──────────────────────
        // contentSafetyClient stays null on hate/self-harm/sexual/violence: calibration runs use
        // only the LLM-judge path, not the Azure Content Safety API (cost + auth).
        Add<SystemPromptLeakageEval>("system_prompt_leakage", (j, m) => new SystemPromptLeakageEval(j!, judgeModel: m));
        Add<IndirectAttackEval>("indirect_attack", (j, m) => new IndirectAttackEval(j!, judgeModel: m));
        Add<UnsafeToolUseEval>("unsafe_tool_use", (j, m) => new UnsafeToolUseEval(j!, judgeModel: m));
        Add<HateUnfairnessEval>("hate_unfairness", (j, m) => new HateUnfairnessEval(j!, contentSafetyClient: null, judgeModel: m));
        Add<SelfHarmEval>("self_harm", (j, m) => new SelfHarmEval(j!, contentSafetyClient: null, judgeModel: m));
        Add<SexualEval>("sexual", (j, m) => new SexualEval(j!, contentSafetyClient: null, judgeModel: m));
        Add<ViolenceEval>("violence", (j, m) => new ViolenceEval(j!, contentSafetyClient: null, judgeModel: m));
        Add<CodeVulnerabilityEval>("code_vulnerability", (j, m) => new CodeVulnerabilityEval(j!, judgeModel: m));
        Add<UngroundedAttributesEval>("ungrounded_attributes", (j, m) => new UngroundedAttributesEval(j!, judgeModel: m));
        Add<SensitiveDataLeakageEval>("sensitive_data_leakage", (j, m) => new SensitiveDataLeakageEval(j!, judgeModel: m));
        Add<ProtectedMaterialEval>("protected_material", (j, m) => new ProtectedMaterialEval(j!, judgeModel: m));
    }
}
