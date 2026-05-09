# Agentic Benchmark — Cost Guidance

This document describes the cost-tier classification for every evaluator in the AgentEval agentic benchmark suite, explains how to use the `--budget-tier` CLI flag to control evaluation spend, and provides estimated costs per preset and use-case scenario.

---

## Cost-Tier Definitions

The `EvaluatorCostTier` enum (in `AgentEval.Abstractions/Evals/EvaluatorCostTier.cs`) defines five tiers:

| Tier | Enum value | Description | Approx. cost per scenario |
|------|-----------|-------------|--------------------------|
| **TRIVIAL** | `EvaluatorCostTier.Trivial` | Pure-code computation — no LLM invocation, no API cost. Examples: F1Score, all Telemetry evaluators, JudgeQuality evaluators, CostQualityEfficiency. | ~$0.000 |
| **LOW** | `EvaluatorCostTier.Low` | Single short prompt to the judge model — one turn, minimal context. Examples: Groundedness, TaskCompletion, VerbosityAppropriateness, DirectInjection. | ~$0.005–$0.010 |
| **MEDIUM** | `EvaluatorCostTier.Medium` | Single prompt with moderate context (tool call results, prior turn, plan text). Examples: TurnCoherence, IntermediateStepHallucination, SelfCorrectionQuality. | ~$0.010–$0.050 |
| **HIGH** | `EvaluatorCostTier.High` | Single prompt with large context (full conversation history at 10+ turns). Examples: MemoryRecallAccuracy, LongConversationCoherence, GoalTracking. | ~$0.050–$0.200 |

> Cost estimates assume GPT-4o class judge (approximately $0.0025/1K input tokens, $0.010/1K output tokens). Actual costs depend on your model, provider pricing, and prompt length.

---

## Per-Evaluator Cost-Tier Table

### Phase 1 — System + Process

| Evaluator key | Cost tier | Notes |
|---------------|-----------|-------|
| `task_completion` | LOW | Single-turn LLM judge |
| `task_adherence` | MEDIUM | Composite of **5 sub-LLM judges** (goal/rule/procedural/presentation/authorization) |
| `intent_identification` | LOW | Single-turn LLM judge |
| `intent_resolution` | MEDIUM | Composite of **2 sub-LLM judges** (intent_identified + intent_resolved) |
| `task_navigation_efficiency` | LOW | Hybrid: deterministic edit-distance (free) + 1 LLM judge call |
| `tool_selection` | LOW | Single-turn LLM judge |
| `tool_input_accuracy` | LOW | Hybrid: schema check (free) + 1 LLM call |
| `tool_output_utilization` | LOW | Single-turn LLM judge |
| `tool_call_success` | TRIVIAL | Deterministic-first; LLM fallback only when status field absent |
| `tool_efficiency` | LOW | Single-turn LLM judge |
| `tool_call_accuracy` | MEDIUM | Composite of **5 sub-evaluators** — total LLM calls multiply |

### Phase 2 — RAG / Quality

| Evaluator key | Cost tier | Notes |
|---------------|-----------|-------|
| `groundedness` | MEDIUM | Composite of **4 sub-LLM judges** (claim_support / claim_contradicted / citation_accuracy / evidence_coverage) |
| `relevance` | LOW | Single-turn LLM judge |
| `coherence` | LOW | Single-turn LLM judge |
| `fluency` | LOW | Single-turn LLM judge |
| `similarity` | LOW | Single-turn LLM judge |
| `response_completeness` | LOW | Single-turn LLM judge |
| `f1_score` | TRIVIAL | Pure-code token overlap |
| `qa_composite` | MEDIUM | Composite of all 7 above — total LLM calls compound |

### Phase 3 — Judge Quality (Meta)

| Evaluator key | Cost tier | Notes |
|---------------|-----------|-------|
| `judge_agreement` | TRIVIAL | Pure-code Cohen's kappa computation |
| `calibration_accuracy` | TRIVIAL | Pure-code accuracy computation |
| `judge_drift` | TRIVIAL | Pure-code max-delta computation |

### Phase 4 — Safety

| Evaluator key | Cost tier | Notes |
|---------------|-----------|-------|
| `prohibited_actions` | LOW | Hybrid deterministic policy-as-code + LLM fallback |
| `sensitive_data_leakage` | LOW | Hybrid regex scan + LLM fallback |
| `indirect_attack` | LOW | Single-turn LLM judge (XPIA) |
| `hate_unfairness` | LOW | Hybrid content-safety client + LLM fallback |
| `sexual` | LOW | Hybrid content-safety client + LLM fallback |
| `violence` | LOW | Hybrid content-safety client + LLM fallback |
| `self_harm` | LOW | Hybrid content-safety client + LLM fallback |
| `protected_material` | LOW | Single-turn LLM judge |
| `code_vulnerability` | LOW | Single-turn LLM judge |
| `ungrounded_attributes` | LOW | Single-turn LLM judge |
| `system_prompt_leakage` | LOW | Hybrid pattern scan + LLM fallback |
| `unsafe_tool_use` | LOW | Deterministic short-circuit when no tool calls; LLM otherwise |

### Phase 5 — Telemetry + Stochastic Stability

| Evaluator key | Cost tier | Notes |
|---------------|-----------|-------|
| `latency` | TRIVIAL | Pure-code telemetry check |
| `token_usage` | TRIVIAL | Pure-code telemetry check |
| `cost` | TRIVIAL | Pure-code telemetry check |
| `error_rate` | TRIVIAL | Pure-code telemetry check |
| `retry_rate` | TRIVIAL | Pure-code telemetry check |
| `tool_latency` | TRIVIAL | Pure-code telemetry check |
| `stochastic_stability` | TRIVIAL | Pure-code statistical analysis over N prior runs |

### Phase 6 — Memory

| Evaluator key | Cost tier | Notes |
|---------------|-----------|-------|
| `memory_recall_accuracy` | HIGH | Full conversation history (10+ turns) injected into judge prompt |
| `long_conversation_coherence` | HIGH | Full conversation history (10+ turns) injected into judge prompt |

### Phase 6 — Multi-Turn

| Evaluator key | Cost tier | Notes |
|---------------|-----------|-------|
| `turn_coherence` | MEDIUM | Previous turn only (small context) |
| `goal_tracking` | HIGH | Full conversation history required |
| `clarification_appropriateness` | LOW | Single-turn; optional history |

### Phase 6 — Reasoning

| Evaluator key | Cost tier | Notes |
|---------------|-----------|-------|
| `reasoning_correctness` | MEDIUM | Single LLM call with reasoning trace context |
| `goal_decomposition_quality` | LOW | Single LLM call over goal + response |
| `plan_formulation_quality` | MEDIUM | Single LLM call over plan text + query |
| `intermediate_step_hallucination` | MEDIUM | LLM cross-check of claims vs. tool call results |

### Phase 6 — Calibration

| Evaluator key | Cost tier | Notes |
|---------------|-----------|-------|
| `confidence_calibration` | LOW | Single-turn LLM judge |
| `uncertainty_acknowledgment` | LOW | Single-turn LLM judge |
| `self_correction_quality` | MEDIUM | Original exchange + correction turn in judge context |

### Phase 6 — Adversarial

| Evaluator key | Cost tier | Notes |
|---------------|-----------|-------|
| `direct_injection` | LOW | Deterministic pattern scan (zero LLM cost); LLM only on match or nuanced case |
| `persona_attack` | LOW | Deterministic template scan (zero LLM cost); LLM only on match or nuanced case |
| `jailbreak_resistance` | MEDIUM | Scans combined library; up to N LLM calls per matched pattern |

### Phase 6 — UX

| Evaluator key | Cost tier | Notes |
|---------------|-----------|-------|
| `verbosity_appropriateness` | LOW | Single-turn LLM judge |
| `tone_appropriateness` | LOW | Single-turn LLM judge |
| `refusal_quality` | LOW | Single-turn LLM judge; fast-pass when not a refusal |

### Phase 6 — Efficiency

| Evaluator key | Cost tier | Notes |
|---------------|-----------|-------|
| `cost_quality_efficiency` | TRIVIAL | Pure-code formula — no LLM call |

---

## Recommended Budget Per Use Case

### Dev-loop iteration (`--budget-tier low`)

Use during active development for fast feedback without incurring significant API costs.

- Keeps: all TRIVIAL + LOW tier evaluators
- Removes: MEDIUM and HIGH tier evaluators
- **Conversational preset result after `low` filter**: 1 component retained (`clarification_appropriateness`). Weight renormalised to 1.0.
- **Recommended presets for dev-loop**: `agentic-execution`, `rag-quality`, `safety`, `user-experience`

```bash
agenteval bench agentic --preset agentic-execution --subject MyAgent --budget-tier low
agenteval bench agentic --preset user-experience --subject MyAgent --budget-tier low
```

### PR build gate (`--budget-tier medium`)

Use in CI for pull request validation — balances speed and coverage.

- Keeps: all TRIVIAL + LOW + MEDIUM tier evaluators
- Removes: HIGH tier evaluators (full-history memory evaluators)
- **Conversational preset result after `medium` filter**: 4 components retained (all except MemoryRecallAccuracy and LongConversationCoherence).

```bash
agenteval bench agentic --preset conversational --subject MyAgent --budget-tier medium
agenteval bench agentic --preset reasoning --subject MyAgent --budget-tier medium
```

### Release-gate audit (no `--budget-tier` flag, defaults to `all`)

Use for full coverage at release time or for scheduled quality audits.

- All evaluators run regardless of cost tier.
- No filtering applied.

```bash
agenteval bench agentic --preset conversational --subject MyAgent
agenteval bench agentic --preset adversarial-direct --subject MyAgent
```

---

## Estimated Full-Calibration Costs Per Preset

Estimates assume a GPT-4o-class judge, 10 scenarios per evaluator, and average prompt lengths typical for each tier. Costs are approximate.

| Preset | Components | Tiers | Est. cost per run (10 scenarios) |
|--------|------------|-------|----------------------------------|
| `agentic-execution` | 6 | LOW×6 | ~$0.30–$0.60 |
| `tool-call-accuracy` | 5 (via aggregate) | LOW×5 | ~$0.25–$0.50 |
| `rag-quality` | 7 | TRIVIAL×1, LOW×6 | ~$0.30–$0.60 |
| `judge-quality` | 3 | TRIVIAL×3 | ~$0.00 |
| `safety` | 12 | TRIVIAL×2, LOW×10 | ~$0.50–$1.00 |
| `telemetry` | 6 | TRIVIAL×6 | ~$0.00 |
| `stochastic-stability` | 1 | TRIVIAL×1 | ~$0.00 |
| `conversational` | 5 | HIGH×3, MEDIUM×1, LOW×1 | ~$1.50–$3.00 |
| `reasoning` | 4 | MEDIUM×4 | ~$0.40–$2.00 |
| `user-experience` | 5 | LOW×5 | ~$0.25–$0.50 |
| `adversarial-direct` | 3 | MEDIUM×1, LOW×2 | ~$0.15–$0.60 |

> The `conversational` preset is the most expensive due to its two HIGH-tier evaluators (MemoryRecallAccuracy, LongConversationCoherence, GoalTracking) which embed full conversation histories in every judge prompt. For dev-loop use, apply `--budget-tier medium` or `--budget-tier low`.

---

## Implementation Reference

- **Enum**: `AgentEval.Evals.EvaluatorCostTier` in `src/AgentEval.Abstractions/Evals/`
- **Cost map**: `AgentEval.Evals.EvaluatorCostMap` (static dictionary, 46+ entries)
- **Filter logic**: `AgentEval.Evals.Agentic.Composition.CostFilteredCompositeBuilder.FilterByBudget`
- **CLI integration**: `--budget-tier` flag on `agenteval bench agentic`

The `EvaluatorCostMap.IsWithinBudget(tier, budget)` method returns `true` when `tier <= budget`. Unknown evaluator keys default to `Medium` (conservative).
