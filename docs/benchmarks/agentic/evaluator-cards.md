# Agentic Benchmark — Evaluator Cards

The 60 evaluators shipped across Phases 1–6 are organized below by category, with their key,
implementing class, and (where applicable) the Foundry evaluator they fork from.

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
| `intent_identification` | `IntentIdentificationEval` | AgentEval-original |
| `intent_resolution` | `IntentResolutionEval` | `azureai://built-in/evaluators/intent_resolution` |
| `task_navigation_efficiency` | `TaskNavigationEfficiencyEval` | AgentEval-original (hybrid) |

---

## Process Evaluators (Phase 1 — 6 evaluators)

| Key | Class | Foundry URI |
|-----|-------|-------------|
| `tool_selection` | `ToolSelectionEval` | `azureai://built-in/evaluators/tool_selection` |
| `tool_input_accuracy` | `ToolInputAccuracyEval` | `azureai://built-in/evaluators/tool_input_accuracy` |
| `tool_output_utilization` | `ToolOutputUtilizationEval` | `azureai://built-in/evaluators/tool_output_utilization` |
| `tool_call_success` | `ToolCallSuccessEval` | `azureai://built-in/evaluators/tool_call_success` |
| `tool_efficiency` | `ToolEfficiencyEval` | AgentEval-original |
| `tool_call_accuracy` | `ToolCallAccuracyAggregateEval` | Aggregate — no 1:1 Foundry equivalent |

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
| `f1_score` | `F1ScoreEval` | `azureai://built-in/evaluators/f1_score` |

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

> Detailed per-evaluator scoring rubrics, input contracts, and calibration guidance live in
> the `EvaluatorCard` JSON files. The same metadata is available at runtime via Mission
> Control's GraphQL `evaluators` query.
