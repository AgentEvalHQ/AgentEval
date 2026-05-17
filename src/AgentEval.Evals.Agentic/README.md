# AgentEval.Evals.Agentic

Agent-focused evaluator suite. Framework-neutral, prompts forked from public MIT-licensed sources and improved.

## What this package ships

A library of named `IEval` implementations for evaluating AI agents:

- **System evaluators** (Phase 1): `TaskCompletionEval`, `TaskAdherenceEval` (5 sub-dimensions), `IntentIdentificationEval`, `IntentResolutionEval`, `TaskNavigationEfficiencyEval`.
- **Process / tool evaluators** (Phase 1): `ToolSelectionEval`, `ToolInputAccuracyEval` (deterministic + semantic), `ToolOutputUtilizationEval`, `ToolCallSuccessEval` (deterministic-first), `ToolEfficiencyEval`, plus `ToolCallAccuracyAggregateEval` (composite of the 5 sub-evaluators with weighted-sum aggregation).
- **Quality evaluators** (Phase 2): `GroundednessEval` (4 sub-dimensions), `RelevanceEval`, `CoherenceEval`, `FluencyEval`, `SimilarityEval`, `ResponseCompletenessEval`, `QaCompositeEval`, plus `F1ScoreEval` (re-exported from `AgentEval.Core`).
- **Adjudication** (Phase 3): `AdjudicatedMultiJudgeWrapper`, plus the meta-evaluators `JudgeAgreementEval`, `CalibrationAccuracyEval`, `JudgeDriftEval`.
- **Safety evaluators** (Phase 4): hybrid policy-as-code + LLM judges for prohibited actions, sensitive data leakage, indirect attack, hate/sexual/violence/self-harm, protected materials, code vulnerability, system-prompt leakage, unsafe tool use.
- **Telemetry evaluators** (Phase 5): pure-code metrics from trace metadata.

Plus benchmark presets in `Composition/AgenticBenchmark.cs`:

- `AgenticBenchmark.AgenticExecution()` — overall agent quality
- `AgenticBenchmark.ToolCallAccuracy()` — tool-focused diagnostic
- `AgenticBenchmark.RagQuality()` — RAG-specific
- `AgenticBenchmark.Safety()` — safety/security focused
- `AgenticBenchmark.JudgeQuality()` — meta-evaluation
- `AgenticBenchmark.Telemetry()` — pure-code operational metrics
- `AgenticBenchmark.StochasticStability()` — run-to-run variance check

## Prompt provenance

Evaluator prompts are forked from public MIT-licensed sources (`azure-sdk-for-python` evaluator prompty files) and improved per the AgentEval envelope (`temperature: 0`, structured `evidence[]` instead of chain-of-thought, severity rubric, sub-dimensions where applicable, deterministic-first paths for hybrid evaluators). Each prompt file's header carries the source URL, pinned commit SHA, and the list of modifications applied — that's the credit-where-credit-is-due story per the MIT license.

The upstream-feedback summary documenting the improvements lives at [`strategy/FutureFeatures/todo/findings-and-suggestions.md`](../../strategy/FutureFeatures/todo/findings-and-suggestions.md). It's positioned as friendly contribution back to the upstream maintainers, not as a coupling layer in this codebase.

## Why a separate project (not a sample)

Agentic evaluators are **building blocks** — every consumer assembles their own benchmark from these primitives. Compliance benchmarks (`samples/AgentEval.GdprBenchmark`, `samples/AgentEval.EuAiActBenchmark`) live in `samples/` because their scenario *content* is regulation-specific. Agentic evaluators live in `src/` because they are reusable infrastructure.

## Implementation plan

[`strategy/FutureFeatures/todo/05-AgentEval-Foundry-Evals-Local.md`](../../strategy/FutureFeatures/todo/05-AgentEval-Foundry-Evals-Local.md)
