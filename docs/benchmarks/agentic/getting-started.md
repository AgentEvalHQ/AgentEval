# Agentic Benchmark — Getting Started

## Scope, Limitations and Honest Framing

> **Disclaimer**: This evaluation result reflects an AI agent's behavior on the configured benchmark scenarios. It is not a compliance attestation, certification, or production-ready quality guarantee. Use it as one input alongside human review, monitoring, and domain testing.

The Agentic Benchmark is a behavioral evaluation framework for AI agents. It measures whether an agent demonstrates sound task-completion, tool-use, retrieval-augmented generation quality, judge health, and operational behavior across a set of configurable evaluation scenarios. It does not certify an agent for production deployment, regulatory compliance, or fitness for a specific business domain.

### Audiences and defensible claims

| Audience | What a passing run supports |
|----------|-----------------------------|
| Developer | "The agent's behavior passes the agentic benchmark on these scenarios — a starting signal for quality." |
| AI lead | "Our agent passes the standard agentic evaluation preset; one of several inputs into our quality program." |
| Product manager | "The agent has been screened with AgentEval's agentic benchmark." — not "is production-certified". |
| Operations | "Telemetry evaluators confirm the agent meets latency and cost budgets on the evaluated scenarios." |

---

## What the Benchmark Validates

The benchmark organizes its evaluators into ten categories. Each category is independently runnable via a preset factory, or they can be combined into custom composites.

### System and Process (Phase 1)

Covers the agent's end-to-end task execution and tool-use behavior:

- **Task Completion** — whether the agent completes the assigned task end-to-end, including verification of tool-claimed outcomes and actionability of the final response.
- **Task Adherence** — whether the agent respects goals, rules, procedural constraints, presentation requirements, and authorization boundaries across five sub-dimensions.
- **Intent Identification** — whether the agent correctly identifies the user's primary intent, secondary or implicit intents, and scope.
- **Intent Resolution** — whether an identified intent is actually resolved in the response.
- **Task Navigation Efficiency** — how efficiently the agent navigates toward the goal (hybrid: deterministic edit-distance on tool call sequences + LLM-assessed path quality).
- **Tool Selection** — whether the agent selects the correct tools given available options.
- **Tool Input Accuracy** — whether tool inputs match required parameters and provide appropriate values (hybrid: schema-check + LLM assessment).
- **Tool Output Utilization** — whether the agent correctly uses tool results in its reasoning and final response.
- **Tool Call Success** — whether all tool calls executed successfully (deterministic-first: reads structured status fields before falling back to LLM).
- **Tool Efficiency** — whether the agent avoided redundant or wasteful tool calls.
- **Tool Call Accuracy Aggregate** — a composite of the five tool sub-evaluators with canonical weights.

Each evaluator's prompt file (`Resources/Prompts/...`) carries a header documenting its public MIT-licensed source (Azure SDK for Python `_evaluators/...prompty` files), pinned commit SHA at fork time, and the modifications applied.

### RAG Quality (Phase 2)

Covers retrieval-augmented generation quality:

- **Groundedness** — whether agent claims are supported by context, across four sub-dimensions (claim support, claim contradiction, citation accuracy, evidence coverage).
- **Relevance** — whether the response is on-topic with respect to the query.
- **Coherence** — logical organization and consistency of the response.
- **Fluency** — grammatical and linguistic quality.
- **Similarity** — semantic match to a ground-truth reference.
- **Response Completeness** — whether the response covers all expected facts, distinguishing critical from optional gaps.
- **F1 Score** — deterministic token-level overlap between response and ground truth.

### Judge Quality (Phase 3)

Meta-evaluators for evaluator health monitoring (no LLM invocation):

- **Judge Agreement** — Cohen's kappa across a panel of judge results for the same input.
- **Calibration Accuracy** — fraction of judge verdicts matching hand-labeled expected verdicts.
- **Judge Drift** — maximum score delta between two run snapshots on the same input.

### Operational / Telemetry (Phase 5)

Pure-code evaluators that read trace metadata — no LLM invocation:

- **Latency** — end-to-end agent latency at P99 vs. configurable threshold.
- **Token Usage** — total token consumption vs. configurable budget.
- **Cost** — estimated monetary cost per run vs. configurable USD budget.
- **Error Rate** — fraction of calls that resulted in errors (pass: error rate <= 5%).
- **Retry Rate** — fraction of calls that triggered retries (pass: retry rate <= 10%).
- **Tool Latency** — worst per-tool mean latency vs. configurable per-tool budget.
- **Stochastic Stability** — run-to-run score consistency across N independent runs (success rate + score variance inverse + failure-mode consistency).

### Memory (Phase 6)

Evaluators for long-term and short-term information recall:

- **Memory Recall Accuracy** — whether the agent correctly recalls facts established in prior conversation turns without confabulation.
- **Long Conversation Coherence** — whether the agent maintains consistency across 10+ turns, detecting contradictions, persona drift, or topic abandonment.

### Multi-Turn (Phase 6)

Evaluators for multi-turn conversation quality:

- **Turn Coherence** — whether the agent's current response coherently addresses the immediately preceding turn.
- **Goal Tracking** — whether the agent maintains the user's original goal across multiple turns despite distractors.
- **Clarification Appropriateness** — whether the agent asks appropriate clarifying questions when the user's query is ambiguous (not too many, not too few).

### Reasoning (Phase 6)

Evaluators for reasoning chain quality and hallucination detection:

- **Reasoning Correctness** — whether the agent's reasoning chain is logically sound and arrives at correct conclusions.
- **Goal Decomposition Quality** — whether the agent decomposes complex goals into well-scoped, independently addressable sub-goals.
- **Plan Formulation Quality** — whether the agent produced a sound, well-structured plan before executing a task.
- **Intermediate Step Hallucination** — whether the agent fabricated intermediate facts, tool results, or conclusions not grounded in actual tool outputs or context.

### Calibration (Phase 6)

Evaluators for self-awareness and epistemic quality:

- **Confidence Calibration** — whether the agent's expressed confidence matches the correctness of its claims.
- **Uncertainty Acknowledgment** — whether the agent appropriately acknowledges when it cannot know something with certainty.
- **Self-Correction Quality** — whether the agent accepts valid corrections gracefully and produces more accurate corrected responses.

### UX / Communication (Phase 6)

Evaluators for user-facing communication quality:

- **Verbosity Appropriateness** — whether the response length is appropriate for the complexity of the query.
- **Tone Appropriateness** — whether the communication tone is appropriate for the user, context, and domain.
- **Refusal Quality** — when the agent declines a request, whether the refusal is specific, non-preachy, and offers constructive alternatives.

### Adversarial (Phase 6)

Evaluators for resistance to direct adversarial attacks:

- **Direct Prompt Injection** — whether the agent resisted adversarial instructions embedded in the user message attempting to override system constraints.
- **Persona Attack Resistance** — whether the agent resisted attempts to substitute its identity with an adversarial persona.
- **Jailbreak Resistance** — aggregate resistance across the full combined pattern library (direct-injection + persona-attack vectors).

### Efficiency (Phase 6)

Pure-code evaluator for cost-quality trade-off:

- **Cost-Quality Efficiency** — score-per-dollar ratio normalized against a reference efficiency point; signals when a benchmark run is less cost-efficient than expected.

---

## What the Benchmark Does NOT Validate

- **Regulatory compliance** — the agentic benchmark is not a GDPR, EU AI Act, or other regulatory attestation. Use `agenteval bench gdpr` or `agenteval bench eu-ai-act` for compliance-oriented evaluation.
- **Domain-specific correctness** — factual accuracy in a specific business domain requires domain-authored ground truth and separate validation.
- **Production performance under load** — telemetry evaluators use trace data from test runs; they do not measure production-scale latency or cost.
- **Security and adversarial robustness** — Safety evaluators (Phase 4) cover some attack vectors, but a production security posture requires penetration testing, red-team exercises, and architectural review.
- **Model-level guarantees** — the benchmark evaluates agent dialog behavior, not the underlying model's training data, fine-tuning quality, or provider-level obligations.
- **Certification or audit evidence** — results are evaluation artifacts, not compliance evidence. They do not carry the audit chain enforced by the GDPR and EU AI Act benchmarks.

---

## Prerequisites

- .NET 10.0.x SDK (or 8.x / 9.x).
- An initialized `.agenteval` workspace in your repository root.
- Optional but recommended: Azure OpenAI resource with a deployed GPT-4o model (see Configuration below). Without Azure OpenAI credentials, the CLI falls back to a stub judge that produces deterministic placeholder scores; stub-mode results are not meaningful for quality evaluation.

---

## Quick Start

```bash
# Initialize the .agenteval workspace if not already done
agenteval init --name MySolution

# Run the Agentic Execution preset (task completion, adherence, intent, tool accuracy, navigation)
agenteval bench agentic --preset agentic-execution --subject MyTravelAgent

# Run the RAG Quality preset
agenteval bench agentic --preset rag-quality --subject MyTravelAgent

# Run the Judge Quality preset (no LLM required)
agenteval bench agentic --preset judge-quality

# Run the Safety preset (12-evaluator safety/security composite)
agenteval bench agentic --preset safety --subject MyTravelAgent

# Run the Conversational Quality preset (memory + multi-turn)
agenteval bench agentic --preset conversational --subject MyTravelAgent

# Run the Reasoning Quality preset
agenteval bench agentic --preset reasoning --subject MyTravelAgent

# Run with cost-aware filtering — keep only LOW-tier evaluators for fast dev-loop iteration
agenteval bench agentic --preset conversational --subject MyTravelAgent --budget-tier low

# Run calibration and write a calibration report
agenteval bench agentic calibrate

# Re-render an existing report without LLM cost
agenteval render --benchmark agentic --subject MyTravelAgent
```

---

## Preset Reference

Each preset is a `static CompositeEval` factory in `AgenticBenchmark` (`src/AgentEval.Evals.Agentic/Composition/AgenticBenchmark.cs`).

| Preset | CLI name | Components | Pass threshold | Intended use |
|--------|----------|------------|----------------|--------------|
| `AgenticExecution` | `agentic-execution` | TaskCompletion 0.25, TaskAdherence 0.20, ToolCallAccuracy 0.20, IntentResolution 0.15, TaskNavEfficiency 0.10, IntentIdentification 0.10 | 0.85 | Standard agent quality gate |
| `ToolCallAccuracy` | `tool-call-accuracy` | ToolCallAccuracyAggregateEval 1.0 (5 sub-dims) | 0.80 | Focused tool-call diagnostic |
| `RagQuality` | `rag-quality` | Groundedness 0.30, ResponseCompleteness 0.20, Relevance 0.15, Similarity 0.15, F1Score 0.10, Coherence 0.05, Fluency 0.05 | 0.70 | RAG pipeline quality |
| `JudgeQuality` | `judge-quality` | JudgeAgreement 0.40, CalibrationAccuracy 0.40, JudgeDrift 0.20 | 0.75 | Evaluator health monitoring |
| `Safety` | `safety` | ProhibitedActions 0.20, IndirectAttack 0.10, Hate/Sexual/Violence/SelfHarm 0.08 each, SensitiveDataLeakage 0.10, ProtectedMaterial/CodeVulnerability/SystemPromptLeakage/UnsafeToolUse 0.06 each, UngroundedAttributes 0.04 | 0.90 | Safety/security gate |
| `Telemetry` | `telemetry` | Latency 0.25, ErrorRate 0.25, TokenUsage 0.20, Cost 0.15, RetryRate 0.10, ToolLatency 0.05 | 0.80 | Operational health monitoring |
| `StochasticStability` | `stochastic-stability` | StochasticStabilityEval 1.0 | 0.80 | Run-to-run consistency verification |
| `Conversational` | `conversational` | MemoryRecall 0.25, LongConvCoherence 0.25, TurnCoherence 0.20, GoalTracking 0.20, ClarificationAppropriateness 0.10 | 0.80 | Memory + multi-turn quality |
| `Reasoning` | `reasoning` | ReasoningCorrectness 0.30, IntermediateStepHallucination 0.25, PlanFormulationQuality 0.25, GoalDecompositionQuality 0.20 | 0.80 | Reasoning chain quality |
| `UserExperience` | `user-experience` | ToneAppropriateness 0.30, VerbosityAppropriateness 0.25, RefusalQuality 0.20, ConfidenceCalibration 0.15, UncertaintyAcknowledgment 0.10 | 0.80 | UX and communication quality |
| `AdversarialDirect` | `adversarial-direct` | DirectInjection 0.40, PersonaAttack 0.30, JailbreakResistance 0.30 | 0.95 | Direct adversarial resistance gate |

---

## Cost-Aware Execution

The `--budget-tier` flag filters out evaluators whose cost tier exceeds the specified budget and renormalizes the remaining weights to sum to 1.0. This allows you to run a cheaper subset of a preset during development and switch to the full preset for release gates.

### Budget tiers

| Tier | CLI value | Approx. cost per scenario | Intended use |
|------|-----------|--------------------------|--------------|
| TRIVIAL | `trivial` | ~$0 | Pure-code only (no LLM calls) |
| LOW | `low` | ~$0.005–0.01 | Single-turn LLM evaluators |
| MEDIUM | `medium` | ~$0.01–0.05 | Multi-context LLM evaluators |
| HIGH | `high` | ~$0.05–0.20 | Full-history LLM evaluators |
| (all) | `all` | uncapped | Full preset — default behavior |

### Usage

```bash
# Dev-loop iteration — only LOW and below (fast, cheap)
agenteval bench agentic --preset conversational --subject MyAgent --budget-tier low

# PR build — MEDIUM and below (balance speed and coverage)
agenteval bench agentic --preset conversational --subject MyAgent --budget-tier medium

# Release gate — all evaluators (full coverage, no filtering)
agenteval bench agentic --preset conversational --subject MyAgent
```

When filtering removes some components, the CLI prints: `Budget-tier filter 'low': kept N of M components (removed K above-budget evaluators).`

When filtering would remove **all** components, the command exits with an error. Use a higher budget tier or switch to a more appropriate preset.

For a full per-evaluator cost-tier table and cost estimation guidance, see [Cost Guidance](cost-guidance.md).

---

## Output

Each run writes to `.agenteval/benchmarks/agentic/{subject}/{timestamp}/`. The timestamp format is `yyyy-MM-dd_HH-mm-ss`.

```
.agenteval/benchmarks/agentic/MyTravelAgent/2026-05-09_10-15-00/
├── agentic-result.json    # AgenticBenchmarkResult: composite tree, summary, critical findings,
│                          #   recommendations, disclaimer, attestation
├── report.md              # PR-friendly markdown report
└── report.pdf             # PDF report (QuestPDF)
```

### `agentic-result.json`

The benchmark result document. Contains:

- `compositeTree` — the full recursive `EvalResult` tree, one node per component and sub-component.
- `summary` — per-category scores, pass/fail/warn status, and overall verdict (`PASS`, `WARN`, or `FAIL`).
- `criticalFindings` — list of evaluators that scored below threshold at `high` or `medium` severity.
- `recommendations` — one recommendation string per critical finding.
- `disclaimer` — the verbatim disclaimer text from the Scope section above.
- `attestation` — `{ "agentEvalVersion": "...", "judgeMode": "...", "promptVersions": { ... } }`.

Validated against `agentic-result.schema.json` before writing. If validation fails, the write is refused and an error is reported to stderr.

### `report.md`

A markdown report suitable for attaching to a pull request or GitHub release. Sections: executive summary, per-category table, per-evaluator results, critical findings, recommendations, and disclaimer.

### `report.pdf`

A PDF report for team review. Sections: cover page (with mandatory disclaimer banner), executive summary, per-category results, per-evaluator results, methodology note, and disclaimer. Generated using QuestPDF.

---

## Configuration

Set the following environment variables before running to use a real LLM judge:

```
AZURE_OPENAI_ENDPOINT=https://<your-resource>.openai.azure.com/
AZURE_OPENAI_API_KEY=<your-key>
AZURE_OPENAI_DEPLOYMENT=<your-gpt-4o-deployment>
```

If any of these variables are unset, the CLI engages stub mode automatically. Stub mode prints a warning to stderr and returns deterministic placeholder scores. Stub-mode results should not be used for quality evaluation or decision-making.

Pure-code evaluators (Telemetry, StochasticStability, JudgeQuality, TaskNavigationEfficiency deterministic path, ToolCallSuccess deterministic path) do not require Azure OpenAI and produce meaningful scores in stub mode.

---

## Calibration

The `agenteval bench agentic calibrate` command runs the hand-labeled golden dataset against the configured judge and produces a calibration report:

```bash
agenteval bench agentic calibrate
```

The golden dataset contains hand-labeled scenario/response pairs distributed across category-specific files under `tests/AgentEval.Tests/Agentic/Calibration/Golden/` — three Phase 1–2 files of 20 entries each (system / process / quality), plus Phase 6 files for adversarial, confidence calibration, memory + multi-turn, reasoning, and UX. For each entry, the calibration runner asks the judge to score the response and compares that score to the human label.

The calibration report records per-category accuracy (fraction of entries within an acceptable score band) and Cohen's kappa (inter-rater agreement). The CI workflow `.github/workflows/agentic-calibration.yml` gates release branches on:

- Accuracy >= 0.85 per category.
- Cohen's kappa >= 0.70 per category.
- Zero evaluation failures (judge errors) per category.

A category that fails any threshold blocks the release PR. Golden dataset files are located under `tests/AgentEval.Tests/Agentic/Calibration/Golden/` as JSONL files.

The calibration report is written to `docs/benchmarks/agentic/calibration-{date}.md`.

**Caveat**: calibration results are only meaningful when a real LLM judge is wired. Running calibration against the stub judge produces placeholder metrics because the stub always returns deterministic scores regardless of content.

---

## Prompt Provenance

Evaluator prompts are forked from public MIT-licensed sources (the `azure-sdk-for-python` evaluator `.prompty` files) and improved per the AgentEval envelope: `temperature: 0` for reproducibility, structured `evidence[]` output instead of chain-of-thought, severity rubric, sub-dimensions where applicable, and deterministic-first paths for hybrid evaluators.

Each prompt file's header carries the source URL, pinned commit SHA at fork time, and the list of modifications applied — that's the credit-where-credit-is-due story per the MIT license.

---

## Known Limitations

- **Multi-judge x Mode-B mutual exclusivity** — when both multi-judge (3 judges for high-severity evaluators) and per-criterion decomposition (Mode-B) are configured for the same evaluator, multi-judge takes precedence and Mode-B is silently skipped. This is an accepted v1 cost trade-off, documented inline in the relevant evaluator source files. A full fix is tracked as a Phase 11+ enhancement.
- **Stub-mode scores are not meaningful** — the stub judge always returns a configurable fixed score regardless of content. Do not use stub-mode results for quality gates, compliance purposes, or decision-making.
- **Telemetry evaluators require caller-supplied trace data** — `AgenticTelemetry` must be populated by the consuming application (or test harness) before invoking telemetry evaluators. AgentEval does not auto-instrument the agent runtime.
- **Stochastic Stability requires multiple prior runs** — at least 2 `EvalResult` objects must be supplied via `EvalInput.Metadata["run_results"]`. The evaluator returns a skipped result when fewer than 2 results are available.
- **English-only scenarios** — all built-in benchmark scenarios are authored in English. Multi-language scenario packs are deferred.
- **Cost estimation is caller responsibility** — `AgenticTelemetry.EstimatedCostUsd` must be computed and supplied by the caller. If cost tracking is not implemented, `CostEval` scores 1.0 unconditionally (zero cost = within budget).

---

## References

- [GDPR Compliance Benchmark](../gdpr/getting-started.md) — the original reference benchmark whose pattern the agentic benchmark mirrors.
- [EU AI Act Compliance Benchmark](../eu-ai-act/getting-started.md) — the second reference benchmark.
- [Composite Evaluations](../../composite-evals.md) — the underlying `CompositeEval` / `AtomicLlmEval` / `AtomicCodeEval` primitives.
- [Cost Guidance](cost-guidance.md) — per-evaluator cost-tier classification and `--budget-tier` filtering.
- [Evaluator Cards](evaluator-cards.md) — index of the 60 shipped evaluators by category.
- [CLI Reference](../../cli.md) — full reference for `agenteval bench agentic` and `agenteval bench agentic calibrate`.

> **Reminder**: this benchmark is a behavioral evaluation tool, not a compliance attestation, certification, or production-readiness guarantee. A passing score does not substitute for human review, monitoring, penetration testing, or domain-specific validation. Consult qualified domain and legal personnel before making any quality or compliance representations.
