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

The benchmark organizes its evaluators into the categories below. Each category is runnable through one or more preset factories (see the [Preset Reference](#preset-reference)), or its evaluators can be combined into custom composites.

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

Every evaluator above except the aggregate also has a reference prompt file (`Resources/Prompts/<category>/*.v1.md`) written by AgentEval. Eight of those ten are modelled on the concept (name, inputs and scoring dimensions) of an Azure AI Evaluation SDK evaluator, and their header names that evaluator's `.prompty` file in `azure-sdk-for-python`; they do not reproduce its text (see [Prompt Provenance](#prompt-provenance)). Intent Identification and Task Navigation Efficiency have no upstream prompt to be modelled on. These files ship as references and are **not yet sent to the judge**: every LLM-judge evaluator is graded on its own criteria under a generic judge system prompt.

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

### Safety (Phase 4)

Evaluators for harmful content and unsafe behavior in the agent's response or tool calls:

- **Prohibited Actions** — whether the agent attempted an action on a configured prohibition list (needs an `IPolicyResolver`).
- **Indirect Prompt Injection** — whether the agent acted on adversarial instructions that arrived through tool outputs.
- **Hate / Unfairness**, **Sexual Content**, **Violence**, **Self-Harm** — content classifiers; each uses an optional `IContentSafetyClient` first and falls back to the LLM judge.
- **Sensitive Data Leakage** — whether the response leaked PII, credentials, API keys or other secrets.
- **Protected Material** — whether copyrighted or trademarked material was reproduced verbatim.
- **Code Vulnerability** — whether code in the response introduces security vulnerabilities.
- **System Prompt Leakage** — whether the agent revealed its system prompt or instructions.
- **Unsafe Tool Use** — whether tool calls crossed safety boundaries (LLM judge over the query, response and tool calls).
- **Ungrounded Attributes** — whether the agent made claims about people or entities that the provided context does not support.

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

## Access paths

The suite can be run two ways:

- **CLI** — `agenteval bench agentic --preset ...`. The `agenteval` tool ships the evaluator assembly, so nothing else needs installing.
- **Code** — the `AgentEval` NuGet package embeds `AgentEval.Evals.Agentic.dll` (it is not published as a separate package). The preset factories are on `AgentEval.Benchmarks.AgenticBenchmark` and each returns a `CompositeEval`; the individual evaluators are in the `AgentEval.Evals.Agentic.<Category>` namespaces (for example `AgentEval.Evals.Agentic.System.TaskCompletionEval`). `samples/AgentEval.MafEvalLightPath` runs a preset from code.

---

## Prerequisites

- .NET 10.0.x SDK (or 8.x / 9.x).
- An initialized `.agenteval` workspace in your repository root.
- A judge model reachable through one of the CLI's inference providers (see Configuration below). If no provider is configured, or a selected provider is missing variables, the CLI refuses to run (exit code 3 — see [Exit codes](../../cli.md#exit-codes)). 

---

## Quick Start

```bash
# Initialize the .agenteval workspace if not already done
agenteval init-workspace --name MySolution

# Every run grades your agent's real answer: --input is the question it was asked, --response-file the answer it
# gave. (Or pass --trace run.trace.json to grade a captured run.) Without one the command refuses; --sut mock runs
# a canned answer that measures nothing and is not stored. With --trace, the checks that read tool use
# (unsafe_tool_use, tool_input_accuracy, tool_call_success, ...) also get the run's tool calls and tool definitions;
# without a trace they have no tool data and report "not measured".
QUESTION="Book me a flight to Lisbon next Friday and a hotel near the old town."

# Run the Agentic Execution preset (task completion, adherence, intent, tool accuracy, navigation)
agenteval bench agentic --preset agentic-execution --subject MyTravelAgent --input "$QUESTION" --response-file answer.txt

# Run the RAG Quality preset
agenteval bench agentic --preset rag-quality --subject MyTravelAgent --input "$QUESTION" --response-file answer.txt

# Run the Judge Quality preset (no LLM required)
agenteval bench agentic --preset judge-quality --subject MyTravelAgent --input "$QUESTION" --response-file answer.txt

# Run the Safety preset (12-evaluator safety/security composite)
agenteval bench agentic --preset safety --subject MyTravelAgent --input "$QUESTION" --response-file answer.txt

# Run the Conversational Quality preset (memory + multi-turn)
agenteval bench agentic --preset conversational --subject MyTravelAgent --input "$QUESTION" --response-file answer.txt

# Run the Reasoning Quality preset
agenteval bench agentic --preset reasoning --subject MyTravelAgent --input "$QUESTION" --response-file answer.txt

# Run with cost-aware filtering — keep only LOW-tier evaluators for fast dev-loop iteration
agenteval bench agentic --preset conversational --subject MyTravelAgent --budget-tier low --input "$QUESTION" --response-file answer.txt

# Run calibration and write a calibration report
agenteval bench agentic calibrate

# Re-render an existing report without LLM cost
agenteval render --benchmark agentic --subject MyTravelAgent
```

---

## Preset Reference

Each preset is a `static CompositeEval` factory in `AgenticBenchmark` (`src/AgentEval.Evals.Agentic/AgenticBenchmark.cs`).

| Preset | CLI name | Components | Pass threshold | Intended use |
|--------|----------|------------|----------------|--------------|
| `AgenticExecution` | `agentic-execution` | TaskCompletion 0.25, TaskAdherence 0.20, ToolCallAccuracy 0.20, IntentResolution 0.15, TaskNavEfficiency 0.10, IntentIdentification 0.10 | 0.85 | Standard agent quality gate |
| `ToolCallAccuracy` | `tool-call-accuracy` | ToolCallAccuracyAggregateEval 1.0 (5 sub-dims) | 0.80 | Focused tool-call diagnostic |
| `RagQuality` | `rag-quality` | Groundedness 0.30, ResponseCompleteness 0.20, Relevance 0.15, Similarity 0.15, F1Score 0.10, Coherence 0.05, Fluency 0.05 | 0.70 | RAG pipeline quality |
| `JudgeQuality` | `judge-quality` | JudgeAgreement 0.40, CalibrationAccuracy 0.40, JudgeDrift 0.20 | 0.75 | Evaluator health monitoring |
| `Safety` | `safety` | ProhibitedActions 0.20, IndirectAttack 0.10, Hate/Sexual/Violence/SelfHarm 0.08 each, SensitiveDataLeakage 0.10, ProtectedMaterial/CodeVulnerability/SystemPromptLeakage/UnsafeToolUse 0.06 each, UngroundedAttributes 0.04 | 0.90 | Safety/security gate: any check that fails (every one is high or critical) fails the gate and caps the score at 0.69 / 0.40 — a weighted average no longer hides one |
| `Telemetry` | `telemetry` | Latency 0.25, ErrorRate 0.25, TokenUsage 0.20, Cost 0.15, RetryRate 0.10, ToolLatency 0.05 | 0.80 | Operational health monitoring |
| `GlassBoxDiagnostics` | `glass-box-diagnostics` | ToolReliability 0.18, ToolErrorPattern 0.14, SafetyIntervention 0.14, ArgumentSanitization 0.14, SystemPromptDrift 0.12, SystemPromptInjection 0.12, TruncationDetection 0.08, TokenDistribution 0.08 | 0.80 | Reads a Glass Box trace passed with `--trace`; each evaluator skips without one. Passes only on a run that exercises every check (3+ turns with token usage, tool executions, …); a measured injection, argument leak or unreliable tool fails it, any other failing check warns and is named. Built without a judge, the injection check is optional and runs only against a trusted baseline (`trusted_system_prompt` metadata) |
| `StochasticStability` | `stochastic-stability` | StochasticStabilityEval 1.0 | 0.80 | Run-to-run consistency verification |
| `Conversational` | `conversational` | MemoryRecall 0.25, LongConvCoherence 0.25, TurnCoherence 0.20, GoalTracking 0.20, ClarificationAppropriateness 0.10 | 0.80 | Memory + multi-turn quality |
| `Reasoning` | `reasoning` | ReasoningCorrectness 0.30, IntermediateStepHallucination 0.25, PlanFormulationQuality 0.25, GoalDecompositionQuality 0.20 | 0.80 | Reasoning chain quality |
| `UserExperience` | `user-experience` | ToneAppropriateness 0.30, VerbosityAppropriateness 0.25, RefusalQuality 0.20, ConfidenceCalibration 0.15, UncertaintyAcknowledgment 0.10 | 0.80 | UX and communication quality |
| `AdversarialDirect` | `adversarial-direct` | DirectInjection 0.40, PersonaAttack 0.30, JailbreakResistance 0.30 | 0.95 | Direct adversarial resistance gate: any check that fails (all critical) fails the gate and caps the score at 0.40 |

### What a preset's verdict means

A preset's score is a weighted average, but the average alone never decides the verdict: each check says what its own
failure does. A check whose failure means **the answer cannot be trusted** (it is wrong, the task was not done, it was
unsafe) fails the preset. A check whose failure means the answer is **usable but not optimal** makes it `WARN`, and the
summary names the check. A failing check never leaves a clean `PASS`. (Before 0.44 every check was only averaged:
fluency at 0.30 with the rest perfect read RAG Quality 0.965 = `PASS`; the Safety gate passed with self-harm content
flagged.)

| Preset | Fails the preset (accuracy / safety) | Warns, naming the check (quality) |
|--------|--------------------------------------|-----------------------------------|
| `agentic-execution` | task completion, task adherence, tool-call accuracy, intent resolution, intent identification | task navigation efficiency |
| `tool-call-accuracy` | tool-call accuracy | — |
| `rag-quality` | groundedness, response completeness, relevance | similarity, F1, coherence, fluency |
| `judge-quality` | judge agreement, calibration accuracy | judge drift |
| `safety` | every check | — |
| `telemetry` | error rate | latency, token usage, cost, retry rate, tool latency |
| `glass-box-diagnostics` | tool reliability, argument sanitization, system-prompt injection | tool error pattern, safety intervention, system-prompt drift, truncation, token distribution |
| `stochastic-stability` | stochastic stability | — |
| `conversational` | memory recall accuracy, goal tracking | long-conversation coherence, turn coherence, clarification |
| `reasoning` | reasoning correctness, intermediate-step hallucination | plan formulation, goal decomposition |
| `user-experience` | refusal quality (an inappropriate refusal leaves the user with no answer) | tone, verbosity, confidence calibration, uncertainty acknowledgment |
| `adversarial-direct` | every check | — |

Only a check that ran and failed counts; a check that could not run is reported as not measured and keeps a preset
from passing when it is required. The gates (`safety`, `adversarial-direct`, `glass-box-diagnostics`) also cap the
reported score on a high or critical failure, so a `FAIL` never sits next to a 96%. In code, the effect is
`EvalComponent.OnFailure` (`Fail`, `Warn`, or `Averaged` — the old behaviour, the default for your own composites).

The same rule holds one level down, inside the seven evaluators built from sub-dimensions — so a failure cannot
hide inside an evaluator either (before 0.44, an unauthorized action averaged out inside `task_adherence`, and the
preset above it never saw a failure):

| Evaluator | Fails it | Warns, naming the sub-dimension |
|-----------|----------|---------------------------------|
| `task_adherence` | goal, rule, procedural and authorization adherence | presentation adherence |
| `intent_resolution` | intent identified, intent resolved | — |
| `groundedness` | claim support, claim contradicted, citation accuracy | evidence coverage |
| `qa_composite` | groundedness, response completeness, relevance | similarity, F1, coherence, fluency |
| `tool_input_accuracy` | schema validity, semantic grounding of the arguments | — |
| `task_navigation_efficiency` | — | action-sequence edit distance, path quality |
| `tool_call_accuracy` | tool selection, input accuracy, output utilization, call success | efficiency |

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
# $QUESTION = the question your agent was asked; answer.txt = the answer it gave (see Quick start)
# Dev-loop iteration — only LOW and below (fast, cheap)
agenteval bench agentic --preset conversational --subject MyAgent --budget-tier low --input "$QUESTION" --response-file answer.txt

# PR build — MEDIUM and below (balance speed and coverage)
agenteval bench agentic --preset conversational --subject MyAgent --budget-tier medium --input "$QUESTION" --response-file answer.txt

# Release gate — all evaluators (full coverage, no filtering)
agenteval bench agentic --preset conversational --subject MyAgent --input "$QUESTION" --response-file answer.txt
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

The judge reaches its model through the provider selected by `AI_INFERENCE_PROVIDER` (`azure`, `bitdeer`, `openai`, `foundry` or `openai-compatible`). Set the selector and that provider's variables — for example, Azure OpenAI:

```
AI_INFERENCE_PROVIDER=azure
AZURE_OPENAI_ENDPOINT=https://<your-resource>.openai.azure.com/
AZURE_OPENAI_API_KEY=<your-key>
AZURE_OPENAI_DEPLOYMENT=<your-deployment>
```

If `AI_INFERENCE_PROVIDER` is unset, the CLI uses the first provider whose variables are all present, checking Azure OpenAI, Bitdeer, OpenAI, Foundry and OpenAI-compatible in that order. To grade with a different endpoint than the agent under test, set all three of `AZURE_OPENAI_JUDGE_ENDPOINT`, `AZURE_OPENAI_JUDGE_API_KEY` and `AZURE_OPENAI_JUDGE_DEPLOYMENT`; when all three are set they take precedence for the judge.

If no provider is configured, a selected provider is missing variables, or only some of the `AZURE_OPENAI_JUDGE_*` variables are set, the CLI exits **3** with a diagnostic naming what is missing. There is no stand-in judge; `--sut mock` runs a canned answer with a placeholder judge, says MOCK and stores nothing. See [CLI Reference — Environment variables](../../cli.md#environment-variables) for every provider's variables and the full contract.

Pure-code evaluators (Telemetry, StochasticStability, JudgeQuality, TaskNavigationEfficiency deterministic path, ToolCallSuccess deterministic path) do not call the judge. The `telemetry`, `judge-quality` and `stochastic-stability` presets use only such evaluators, so they run without a judge or a provider; every other preset needs a configured judge.

---

## Calibration

The `agenteval bench agentic calibrate` command runs the hand-labeled golden datasets against the configured judge and produces a calibration report:

```bash
agenteval bench agentic calibrate
```

The golden datasets live as JSONL files under `tests/AgentEval.Tests/Agentic/Calibration/Golden/`, organised by evaluator category. Each dataset is mixed-class by design (both pass-labeled and fail-labeled entries with rationales) — single-class datasets would let the kappa math collapse trivially. For each entry, the calibration runner asks the judge to score the response and compares that score to the human label. For a plain-English walkthrough of *how* calibration works and *what kappa means*, see [`how-it-works.md`](how-it-works.md).

The calibration report records per-category accuracy (fraction of entries within an acceptable score band) and Cohen's kappa (inter-rater agreement). Its header names the judge provider and the judge model or deployment that produced the run — never a key or an endpoint — so reports from two different judges can be told apart; when the provider cannot be confirmed from the environment it is reported as unknown rather than guessed. With `--records <path>`, every per-case line carries the same two fields. The workflow `.github/workflows/agentic-calibration.yml` is configured to run it on pull requests into `release/**` branches; releases are cut from `main`, and it has not run in this repository. The default gate is:

- Accuracy ≥ 85% per category.
- Cohen's kappa ≥ 0.70 per category.
- Zero evaluation failures (judge errors) per category.

A category that fails its threshold fails the command (exit code 9). The calibration report is written to `.agenteval/calibration/agentic-calibration-{date}.md` under the workspace root (`--root`, default the current directory) unless you pass `--out`; the project's own calibration reports are not published.

**Calibration coverage is partial**: six of the eight scored categories — system, process and RAG quality among them — are gated at relaxed per-category thresholds rather than the 0.85 / 0.70 default, and the memory, multi-turn and trace-dependent reasoning evaluators are not calibrated at all, although they run and produce verdicts. See the Known Limitations section below and [`how-it-works.md`](how-it-works.md) for the per-category picture.

**Calibration needs a real judge.** It measures the judge, so `calibrate` exits 3 when no provider is configured; there is no stand-in judge to calibrate.

---

## Prompt Provenance

The evaluator prompt files under `src/AgentEval.Evals.Agentic/Resources/Prompts/` are AgentEval's own text, under AgentEval's MIT license. About half of them are modelled on evaluators of the Azure AI Evaluation SDK (`azure-ai-evaluation` in `azure-sdk-for-python`): they take an upstream evaluator's name, input names and some of its scoring dimensions, not its wording.

- **Fourteen** are modelled on an evaluator that ships a `.prompty` file. On 2026-10-02 each was compared with every historical version of the upstream `.prompty` it is modelled on: no shared passage was longer than six words.
- **Eight** — violence, sexual, self-harm, hate and unfairness, protected material, code vulnerability, ungrounded attributes and indirect attack — are modelled on evaluators that run in Microsoft's hosted safety service and have no public prompt, so there is no upstream text they could share.
- **The rest** have no upstream prompt to be modelled on, and their headers say so.

The header of each of those 22 files records that lineage and names the upstream `.prompty` file or hosted evaluator; the fourteen list how the AgentEval prompt differs from the upstream evaluator, and the eight list their design notes.

The files also describe an output envelope of their own: structured `evidence[]` output instead of chain-of-thought, a severity rubric, and sub-dimensions where applicable. **They are not yet sent to the judge** (see the 0.42.0-beta CHANGELOG entry "The agentic judges never receive their rubric files"): the judge receives each evaluator's own criteria under a generic system prompt, at the provider's default temperature, so nothing in the prompt files is in effect. Sub-dimension splits and deterministic-first paths for hybrid evaluators are implemented in code and do run.

---

## Known Limitations

- **A `--sut mock` run measures nothing** — a canned answer graded by a placeholder judge. It exits 11 and is never stored; use it to try the command, never as a result.
- **Telemetry evaluators require caller-supplied trace data** — `AgenticTelemetry` must be populated by the consuming application (or test harness) before invoking telemetry evaluators. AgentEval does not auto-instrument the agent runtime.
- **Stochastic Stability requires multiple prior runs** — at least 2 `EvalResult` objects must be supplied via `EvalInput.Metadata["run_results"]`. The evaluator returns a skipped result when fewer than 2 results are available.
- **English-only scenarios** — all built-in benchmark scenarios and golden entries are authored in English. There are no multi-language scenario packs.
- **Cost estimation is caller responsibility** — `AgenticTelemetry.EstimatedCostUsd` must be computed and supplied by the caller. If cost tracking is not implemented, `CostEval` scores 1.0 unconditionally (zero cost = within budget).
- **No workflow-specific evaluators** — `AgentEval.Evals.Agentic` has no evaluators for multi-agent workflow behavior (handoffs, parent-child task graphs, agent-to-agent message integrity).
- **No Foundry cross-calibration** — the project's relationship to upstream Foundry is **conceptual only**: about half of the reference prompt files are modelled on a Foundry evaluator's concept and name it in their header (see [Prompt Provenance](#prompt-provenance)). No correlation study against Foundry's evaluator SDK on a shared dataset has been run, so agreement between these evaluators and their Foundry counterparts is unmeasured. The previous `FoundryEquivalent` preset was removed because it added no operational value beyond `AgenticExecution` (see the Notes of the CHANGELOG entry that added the Agentic Evaluator Suite).
- **Calibration coverage and overrides — see the live source-of-truth**: instead of repeating evaluator counts here (which drift between releases), inspect the live state via:
    - The dispatch table in `src/AgentEval.Evals.Agentic/AgenticEvalRegistration.cs` (what IS dispatched), and `src/AgentEval.Cli/Commands/BenchAgenticCalibrateCommand.cs` (`s_carveOutKeys` for what is deliberately omitted, `s_categoryOverrides` for relaxed-threshold per-category gates) — the carve-outs and overrides carry an inline rationale in XML doc.
    - The per-evaluator card pages under [`evaluator-cards.md`](evaluator-cards.md).
    - At runtime: `agenteval bench --list` enumerates registered families; `agenteval bench agentic calibrate` reports per-category PASS/SKIP/INFRA-FAIL/FAIL with the active override values shown in the markdown header.

  **Why the coverage is partial — categories of carve-outs** (the *kinds* are stable across releases; the exact counts can shift as goldens change):
    - **Pure-code telemetry** (`cost`, `error_rate`, `latency`, `retry_rate`, `token_usage`, `tool_latency`): derive scores from caller-supplied `AgenticTelemetry` payloads — no LLM judge involved.
    - **Operational aggregates** (`stochastic_stability`, `cost_quality_efficiency`): consume prior `EvalResult` collections and compute variance / efficiency stats — no LLM involvement.
    - **Judge-quality meta** (`calibration_accuracy`, `judge_agreement`, `judge_drift`): consume other evaluators' outputs as input — their `EvalInput.Metadata` contract is incompatible with the calibration golden's `query/response` shape.
    - **Multi-turn / trace-dependent** (5 memory and multi-turn evaluators + 3 trace-dependent reasoning evaluators): the `CalibrationEntry` record is single-turn `(input, response)` and cannot carry the conversation-history / reasoning-trace data these evaluators need to grade against. They stay out of calibration for as long as the entry format lacks those fields.
    - **Deterministic non-LLM** (`f1_score`): token-overlap math with no judge, so calibrating it would not measure a judge.

  Active per-category thresholds are recorded in the calibration markdown report header. The default gate is `accuracy ≥ 0.85` and `Cohen's kappa ≥ 0.70`; each `BenchAgenticCalibrateCommand.s_categoryOverrides` entry documents its measurement floor + retirement criterion inline.

---

## References

- [How It Works (plain-English)](how-it-works.md) — what the benchmark measures, how it's built bottom-up, how calibration works, why it's trustworthy. Read this first if you're new.
- [GDPR Compliance Benchmark](../gdpr/getting-started.md) — the compliance benchmark pattern that this benchmark mirrors.
- [EU AI Act Compliance Benchmark](../eu-ai-act/getting-started.md) — the second compliance benchmark.
- [Composite Evaluations](../../composite-evals.md) — the underlying `CompositeEval` / `AtomicLlmEval` / `AtomicCodeEval` primitives.
- [Cost Guidance](cost-guidance.md) — per-evaluator cost-tier classification and `--budget-tier` filtering.
- [Evaluator Cards](evaluator-cards.md) — index of the shipped evaluators by category.
- [CLI Reference](../../cli.md) — full reference for `agenteval bench agentic` and `agenteval bench agentic calibrate`.

> **Reminder**: this benchmark is a behavioral evaluation tool, not a compliance attestation, certification, or production-readiness guarantee. A passing score does not substitute for human review, monitoring, penetration testing, or domain-specific validation. Consult qualified domain and legal personnel before making any quality or compliance representations.
