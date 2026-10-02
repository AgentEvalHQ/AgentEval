# Agentic Benchmark — Evaluator Cards

The 60 evaluators that have an `EvaluatorCard` are listed below by category, with their key and
implementing class. The Foundry URI column repeats the Azure AI Foundry entry in the card's
`compatibleWith` list. Where the card lists none, the cell says what the class's own source comment
says about its origin; "AgentEval-original" means the class cites no Foundry source.

The assembly also contains the eight Glass Box Diagnostics evaluators that
`AgenticBenchmark.GlassBoxDiagnostics` uses (tool reliability, tool error pattern, safety
intervention, argument sanitization, system-prompt drift, system-prompt injection, truncation
detection, token distribution). They have no card and are not listed here.

The authoritative source for each evaluator's full metadata — score formula, severity, pass
threshold, expected inputs, recommended visualisation, and external compatibility — is the
matching `EvaluatorCard` JSON under `src/AgentEval.Evals.Agentic/EvaluatorCards/<key>.json`.
At runtime, Mission Control loads these into the GraphQL `evaluators` query (see
[Mission Control Getting Started](../../missioncontrol/getting-started.md)), and the
[Portal-Ready Evaluators](../../missioncontrol/portal-ready-evaluators.md) guide documents
the schema for new evaluator authors. Phase 6 evaluators (memory, multi-turn, reasoning,
calibration, UX, adversarial, efficiency) are listed in [Cost Guidance](cost-guidance.md).

---

## System Evaluators (Phase 1 — 5 evaluators)

| Key | Class | Foundry URI |
|-----|-------|-------------|
| `task_completion` | `TaskCompletionEval` | `azureai://built-in/evaluators/task_completion` |
| `task_adherence` | `TaskAdherenceEval` | `azureai://built-in/evaluators/task_adherence` |
| `intent_identification` | `IntentIdentificationEval` | None in the card; the class says it was split out of Foundry's `intent_resolution` |
| `intent_resolution` | `IntentResolutionEval` | `azureai://built-in/evaluators/intent_resolution` |
| `task_navigation_efficiency` | `TaskNavigationEfficiencyEval` | `azureai://built-in/evaluators/task_navigation_efficiency` (hybrid; the class describes its LLM half as having no direct Foundry equivalent) |

---

## Process Evaluators (Phase 1 — 6 evaluators)

| Key | Class | Foundry URI |
|-----|-------|-------------|
| `tool_selection` | `ToolSelectionEval` | `azureai://built-in/evaluators/tool_selection` |
| `tool_input_accuracy` | `ToolInputAccuracyEval` | `azureai://built-in/evaluators/tool_input_accuracy` |
| `tool_output_utilization` | `ToolOutputUtilizationEval` | `azureai://built-in/evaluators/tool_output_utilization` |
| `tool_call_success` | `ToolCallSuccessEval` | None in the card; the class's LLM-fallback reference prompt is AgentEval's own text, modelled on Foundry's `tool_call_success` evaluator concept |
| `tool_efficiency` | `ToolEfficiencyEval` | None in the card; the class's reference prompt is modelled on the efficiency part of Foundry's `tool_call_accuracy` evaluator concept |
| `tool_call_accuracy` | `ToolCallAccuracyAggregateEval` | `azureai://built-in/evaluators/tool_call_accuracy` (aggregate of the five tool evaluators above) |

---

## Quality / RAG Evaluators (Phase 2 — 7 evaluators)

| Key | Class | Foundry URI |
|-----|-------|-------------|
| `groundedness` | `GroundednessEval` | `azureai://built-in/evaluators/groundedness` |
| `relevance` | `RelevanceEval` | `azureai://built-in/evaluators/relevance` |
| `coherence` | `CoherenceEval` | `azureai://built-in/evaluators/coherence` |
| `fluency` | `FluencyEval` | `azureai://built-in/evaluators/fluency` |
| `similarity` | `SimilarityEval` | `azureai://built-in/evaluators/similarity` |
| `response_completeness` | `ResponseCompletenessEval` | `azureai://built-in/evaluators/response_completeness` |
| `f1_score` | `F1ScoreEval` | None in the card (pure-code token overlap; the class lives in `AgentEval.Core`) |

---

## Judge Quality Meta-Evaluators (Phase 3 — 3 evaluators)

| Key | Class | Notes |
|-----|-------|-------|
| `judge_agreement` | `JudgeAgreementEval` | Cohen's kappa across judge panel |
| `calibration_accuracy` | `CalibrationAccuracyEval` | Accuracy vs hand-labeled verdicts |
| `judge_drift` | `JudgeDriftEval` | Score delta between two run snapshots |

---

## Telemetry Evaluators (Phase 5 — 6 evaluators)

| Key | Class | Score formula |
|-----|-------|---------------|
| `latency` | `LatencyEval` | Linear on P99 vs threshold; high severity above threshold |
| `token_usage` | `TokenUsageEval` | Linear on tokens vs budget |
| `cost` | `CostEval` | Linear on USD vs budget |
| `error_rate` | `ErrorRateEval` | `1 - (errors / totalCalls)` |
| `retry_rate` | `RetryRateEval` | `1 - (retries / totalCalls)` |
| `tool_latency` | `ToolLatencyEval` | Linear on worst-tool mean latency vs per-tool budget |

---

## Stochastic Stability (Phase 5 — 1 evaluator)

| Key | Class | Score formula |
|-----|-------|---------------|
| `stochastic_stability` | `StochasticStabilityEval` | Weighted sum: success_rate 0.50 + variance_inverse 0.30 + failure_mode_consistency 0.20 |

---

## UX Evaluators (Phase 6 — 3 evaluators)

| Key | Class | Foundry URI |
|-----|-------|-------------|
| `verbosity_appropriateness` | `VerbosityAppropriatenessEval` | AgentEval-original |
| `tone_appropriateness` | `ToneAppropriatenessEval` | AgentEval-original |
| `refusal_quality` | `RefusalQualityEval` | AgentEval-original |

---

## Adversarial-Resistance Evaluators (Phase 6 — 3 evaluators)

| Key | Class | Foundry URI |
|-----|-------|-------------|
| `direct_injection` | `DirectInjectionEval` | AgentEval-original (mirrors OWASP LLM01) |
| `persona_attack` | `PersonaAttackEval` | AgentEval-original |
| `jailbreak_resistance` | `JailbreakResistanceEval` | AgentEval-original (mirrors OWASP LLM01) |

The calibration runner also accepts two adversarial keys that have no card and no class of their
own. `AgenticEvalRegistration` maps them onto existing evaluators so their golden entries can be
graded:

| Calibration-only key | Evaluated by |
|----------------------|--------------|
| `prompt_leak` | `SystemPromptLeakageEval` (the `system_prompt_leakage` evaluator below) |
| `escalation_resistance` | `JailbreakResistanceEval` (privilege-escalation prompts treated as jailbreak variants) |

---

## Reasoning Evaluators (Phase 6 — 5 evaluators)

| Key | Class | Foundry URI |
|-----|-------|-------------|
| `reasoning_correctness` | `ReasoningCorrectnessEval` | AgentEval-original |
| `intermediate_step_hallucination` | `IntermediateStepHallucinationEval` | AgentEval-original |
| `plan_formulation_quality` | `PlanFormulationQualityEval` | AgentEval-original |
| `goal_decomposition_quality` | `GoalDecompositionQualityEval` | AgentEval-original |
| `self_correction_quality` | `SelfCorrectionQualityEval` | AgentEval-original. Its card and namespace put it in the calibration category; the calibration runner files it under reasoning |

`intermediate_step_hallucination`, `plan_formulation_quality` and `self_correction_quality` need the
agent's reasoning trace, which a single-turn `CalibrationEntry` cannot carry, so
`agenteval bench agentic calibrate` skips them (`BenchAgenticCalibrateCommand.s_carveOutKeys`).

---

## Calibration Meta Evaluators (Phase 6 — 2 evaluators)

| Key | Class | Notes |
|-----|-------|-------|
| `confidence_calibration` | `ConfidenceCalibrationEval` | Expected to be noisy; the calibration category is gated at a relaxed 0.75 / 0.55 (`s_categoryOverrides`) |
| `uncertainty_acknowledgment` | `UncertaintyAcknowledgmentEval` | Expected to be noisy; the calibration category is gated at a relaxed 0.75 / 0.55 (`s_categoryOverrides`) |

---

## Memory / Multi-Turn Evaluators (Phase 6 — 5 evaluators)

Note: these evaluators grade recall of earlier conversation turns. A `CalibrationEntry` is
single-turn and has no conversation history, so `agenteval bench agentic calibrate` skips all five
(`BenchAgenticCalibrateCommand.s_carveOutKeys`).

| Key | Class | Foundry URI |
|-----|-------|-------------|
| `memory_recall_accuracy` | `MemoryRecallAccuracyEval` | AgentEval-original |
| `turn_coherence` | `TurnCoherenceEval` | AgentEval-original |
| `goal_tracking` | `GoalTrackingEval` | AgentEval-original |
| `clarification_appropriateness` | `ClarificationAppropriatenessEval` | AgentEval-original |
| `long_conversation_coherence` | `LongConversationCoherenceEval` | AgentEval-original |

---

## Safety / Content-Classifier Evaluators (Phase 4 — 12 evaluators)

All twelve are reported under the `safety` calibration category (`CalibrationDataset.DeriveCategory`).
Four of them — `hate_unfairness`, `self_harm`, `sexual` and `violence` — accept an optional
`IContentSafetyClient` and use it before the LLM judge when one is supplied; the calibration runner
passes none. `prohibited_actions` needs an `IPolicyResolver` and a subject id, so the calibration
runner does not dispatch it.

| Key | Class | Foundry URI |
|-----|-------|-------------|
| `hate_unfairness` | `HateUnfairnessEval` | `azureai://built-in/evaluators/hate_unfairness` |
| `self_harm` | `SelfHarmEval` | `azureai://built-in/evaluators/self_harm` |
| `sexual` | `SexualEval` | `azureai://built-in/evaluators/sexual` |
| `violence` | `ViolenceEval` | `azureai://built-in/evaluators/violence` |
| `code_vulnerability` | `CodeVulnerabilityEval` | `azureai://built-in/evaluators/code_vulnerability` |
| `ungrounded_attributes` | `UngroundedAttributesEval` | `azureai://built-in/evaluators/ungrounded_attributes` |
| `sensitive_data_leakage` | `SensitiveDataLeakageEval` | None in the card; the class calls it analogous to Foundry's `sensitive_data_leakage` concept, with an AgentEval-original regex path |
| `protected_material` | `ProtectedMaterialEval` | `azureai://built-in/evaluators/protected_material` |
| `unsafe_tool_use` | `UnsafeToolUseEval` | AgentEval-original |
| `indirect_attack` | `IndirectAttackEval` | `azureai://built-in/evaluators/indirect_attack` |
| `system_prompt_leakage` | `SystemPromptLeakageEval` | AgentEval-original |
| `prohibited_actions` | `ProhibitedActionsEval` | AgentEval-original |

---

## Cost / Quality Efficiency (Phase 6 — 1 evaluator)

| Key | Class | Score formula |
|-----|-------|---------------|
| `cost_quality_efficiency` | `CostQualityEfficiencyEval` | `clamp((scenario_score / max(cost_usd, 0.001)) / 5.0, 0, 1)`; default pass threshold 0.50 |

---

## QA Composite (Phase 2 — 1 evaluator)

| Key | Class | Notes |
|-----|-------|-------|
| `qa_composite` | `QaCompositeEval` | Weighted sum of the seven RAG evaluators: groundedness 0.30, response completeness 0.20, relevance 0.15, similarity 0.15, F1 0.10, coherence 0.05, fluency 0.05; pass threshold 0.70. The card lists `azureai://built-in/evaluators/qa` |

---

> Per-evaluator descriptions, input contracts, default thresholds and Foundry cross-references live in
> the `EvaluatorCard` JSON files (60 total under `src/AgentEval.Evals.Agentic/EvaluatorCards/`).
> The same metadata is available at runtime via Mission Control's GraphQL `evaluators` query.
