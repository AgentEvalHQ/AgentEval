# AgentEval.Evals.Agentic

Agent-focused evaluator suite. Framework-neutral. The evaluator prompt files are AgentEval's own text.

## What this package ships

A library of named `IEval` implementations for evaluating AI agents:

- **System evaluators** (Phase 1): `TaskCompletionEval`, `TaskAdherenceEval` (5 sub-dimensions), `IntentIdentificationEval`, `IntentResolutionEval`, `TaskNavigationEfficiencyEval`.
- **Process / tool evaluators** (Phase 1): `ToolSelectionEval`, `ToolInputAccuracyEval` (deterministic + semantic), `ToolOutputUtilizationEval`, `ToolCallSuccessEval` (deterministic-first), `ToolEfficiencyEval`, plus `ToolCallAccuracyAggregateEval` (composite of the 5 sub-evaluators with weighted-sum aggregation).
- **Quality evaluators** (Phase 2): `GroundednessEval` (4 sub-dimensions), `RelevanceEval`, `CoherenceEval`, `FluencyEval`, `SimilarityEval`, `ResponseCompletenessEval`, `QaCompositeEval`, plus `F1ScoreEval` (re-exported from `AgentEval.Core`).
- **Adjudication** (Phase 3): `AdjudicatedMultiJudgeWrapper`, plus the meta-evaluators `JudgeAgreementEval`, `CalibrationAccuracyEval`, `JudgeDriftEval`.
- **Safety evaluators** (Phase 4): hybrid policy-as-code + LLM judges for prohibited actions, sensitive data leakage, indirect attack, hate/sexual/violence/self-harm, protected materials, code vulnerability, system-prompt leakage, unsafe tool use.
- **Telemetry evaluators** (Phase 5): pure-code metrics from trace metadata.

Plus benchmark presets in `AgenticBenchmark.cs` (project root):

- `AgenticBenchmark.AgenticExecution()` — overall agent quality
- `AgenticBenchmark.ToolCallAccuracy()` — tool-focused diagnostic
- `AgenticBenchmark.RagQuality()` — RAG-specific
- `AgenticBenchmark.Safety()` — safety/security focused
- `AgenticBenchmark.JudgeQuality()` — meta-evaluation
- `AgenticBenchmark.Telemetry()` — pure-code operational metrics
- `AgenticBenchmark.StochasticStability()` — run-to-run variance check

## Prompt provenance

The evaluator prompt files under `Resources/Prompts/` are AgentEval's own text, under AgentEval's MIT license. About half are modelled on the evaluator concepts (name, inputs and scoring dimensions) of the Azure AI Evaluation SDK (`azure-sdk-for-python`); a comparison against every upstream version of the cited prompty files found no reproduced prompt text. Each file's header records its lineage and how it differs from the upstream evaluator. The files are not yet sent to the judge: today the judge grades each evaluator's own criteria under a generic system prompt (see the 0.42.0-beta CHANGELOG, "Corrected").

## Why a separate project (not a sample)

Agentic evaluators are **building blocks** — every consumer assembles their own benchmark from these primitives. Compliance benchmarks (`samples/AgentEval.GdprBenchmark`, `samples/AgentEval.EuAiActBenchmark`) live in `samples/` because their scenario *content* is regulation-specific. Agentic evaluators live in `src/` because they are reusable infrastructure.
