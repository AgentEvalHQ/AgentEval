# Agentic Benchmark — How It Works (Plain-English Explainer)

This page explains, in everyday language, **what the agentic benchmark does**, how it's built, how we know we can trust it, and why running it on your agent is worth the cost. No prior AI knowledge required.

> **One-line summary**: We give an AI agent a set of carefully-chosen tasks (often with tool calls), then have a second AI — the **judge** — grade the agent's behaviour across many quality dimensions: task completion, tool-call accuracy, RAG groundedness, reasoning, safety, memory, multi-turn coherence, and more. We add up those grades into category-level and overall verdicts.

---

## What this benchmark is — and isn't

**It is** a structured way to ask the question *"is this agent any good?"* across the many dimensions that matter for an autonomous AI agent — beyond just "does it return the right answer". It evaluates behaviour: did it pick the right tools, use them correctly, ground its claims in retrieved evidence, handle multi-turn conversation gracefully, reason soundly, refuse appropriately, and stay within latency and cost budgets?

**It is not** a regulatory compliance attestation (use the GDPR or EU AI Act benchmarks for that), not a production-deployment certification, and not a substitute for domain testing in your specific business setting. A passing score tells you the agent's behaviour is sound on a *general* set of probes — your domain may require more.

---

## The structure, bottom-up

The agentic benchmark is organised by *evaluator* rather than by article. There are around 60 named evaluators grouped into roughly a dozen categories. Each evaluator answers one specific question about agent behaviour.

```
                ┌─────────────────────────────────────┐
        TOP →   │  Overall verdict per preset         │
                ├─────────────────────────────────────┤
                │  Composite evaluators per category  │
                │   (e.g. Tool Call Accuracy =       │
                │    5 sub-dimensions weighted)       │
                ├─────────────────────────────────────┤
                │  Atomic evaluators                  │
                │   (one grade for one dimension)     │
                ├─────────────────────────────────────┤
       BOTTOM → │  Scenarios + agent traces           │
                │   (task inputs + agent's runtime    │
                │    record of tool calls, responses) │
                └─────────────────────────────────────┘
```

### Layer 1 — Scenarios and traces (the ground floor)

A **scenario** is a task we give the agent. Some scenarios are simple ("answer this question"); others involve tool calls, multi-turn conversation, or retrieved documents. The agent runs the task and produces a **trace** — a structured record of the user input, the system prompt, the agent's response, any tool calls and their inputs/outputs, latency, and token usage.

The trace is what the evaluators look at. Some evaluators read the response only; others read the tool-call sequence; the operational ones read just the timing and cost metadata.

### Layer 2 — Atomic evaluators (one dimension, one grade)

Each atomic evaluator answers one focused question. There are three kinds:

- **LLM-judge evaluators** — a second AI grades the agent's output against a short list of criteria defined in each evaluator (e.g., *Task Completion*, *Groundedness*, *Coherence*), with the evaluator's rubric file under `src/AgentEval.Evals.Agentic/Resources/Prompts/<category>/*.v1.md`, written by AgentEval, as its system prompt. About half of those files are modelled on the concept of an Azure AI Evaluation SDK evaluator — its name, inputs and scoring dimensions, not its wording — and the rest have no upstream prompt to be modelled on (see [Prompt Provenance](getting-started.md#prompt-provenance)). The verdict is the band of the judge's score in the rubric's own table, and a reply off the rubric's scale is an error — see [What the judge is sent](getting-started.md#what-the-judge-is-sent).
- **Code-only evaluators** — pure C# code reads the trace and computes a score (e.g., *Latency*, *Cost*, *Token Usage*, *Error Rate*, *F1 Score*). No LLM call, no LLM cost.
- **Hybrid evaluators** — deterministic check first, LLM fallback only when needed (e.g., *Tool Call Success* reads structured status fields if present, falls back to LLM only on free-text result strings).

Each atomic evaluator produces a score (0..1), a verdict, a severity, and a rationale.

### Layer 3 — Composite evaluators (named groupings)

Several atomic evaluators bundle into a **composite** that produces one rolled-up score. The headline example is **Tool Call Accuracy**, which weighs five sub-dimensions:

```
ToolCallAccuracy = 0.25 × ToolSelection
                 + 0.25 × ToolInputAccuracy
                 + 0.20 × ToolOutputUtilization
                 + 0.15 × ToolCallSuccess
                 + 0.15 × ToolEfficiency
```

The composite score is useful as a single number, but the individual sub-scores tell you *which* dimension dragged the verdict down — "your tool selection is fine but your tool inputs are wrong" is far more actionable than just "tool calls scored 0.4".

### Layer 4 — Categories (the broad areas)

Atomic + composite evaluators group into **categories** by concern. You don't have to run every evaluator every time; you pick a preset that activates a coherent subset.

| Category | What it covers |
|---|---|
| **System and Process** | Task completion, task adherence, intent identification, intent resolution, navigation efficiency, the five tool-call sub-evaluators |
| **RAG Quality** | Groundedness, relevance, coherence, fluency, similarity, response completeness, F1 score |
| **Judge Quality** | Meta-evaluators (no LLM): judge agreement, calibration accuracy, judge drift — for evaluator-health monitoring |
| **Operational / Telemetry** | Pure-code: latency, token usage, cost, error rate, retry rate, tool latency, stochastic stability |
| **Safety** | Prohibited actions, indirect prompt injection, hate / sexual / violence / self-harm, sensitive-data leakage, protected material, code vulnerability, system-prompt leakage, unsafe tool use, ungrounded attributes |
| **Memory** | Memory recall accuracy, long-conversation coherence |
| **Multi-turn** | Turn coherence, goal tracking, clarification appropriateness |
| **Reasoning** | Reasoning correctness, goal decomposition, plan formulation, intermediate-step hallucination |
| **Calibration (epistemic)** | Confidence calibration, uncertainty acknowledgment, self-correction quality |
| **UX / Communication** | Verbosity appropriateness, tone appropriateness, refusal quality |
| **Adversarial** | Direct prompt injection, persona attack, jailbreak resistance |
| **Efficiency** | Cost-quality efficiency (score-per-dollar ratio) |

### Layer 5 — Presets and overall verdict

A **preset** is a named bundle of evaluators with pre-set weights. Common presets:

- `agentic-execution` — task completion, adherence, tool accuracy, intent, navigation
- `rag-quality` — groundedness-led RAG composite
- `safety` — adversarial + refusal + jailbreak
- `conversational` — memory + multi-turn
- `reasoning` — reasoning correctness, decomposition, plan formulation, hallucination

Each preset has its own pass threshold (e.g., agentic-execution requires 0.85; RAG quality requires 0.70 because RAG is more variable). A preset's score rolls up to PASS / WARN / FAIL by the same composite mechanic the other benchmarks use.

---

## Cost tiers — picking which evaluators to run

Not every evaluator costs the same. The benchmark tags each evaluator with a **cost tier**:

- **TRIVIAL** — pure-code evaluators that read trace metadata only (no LLM calls). Examples: Latency, Token Usage, F1 Score, Cost, Error Rate.
- **LOW** — single LLM call per scenario, shorter prompts.
- **MEDIUM** — single LLM call per scenario, longer prompts (multi-criterion grading).
- **HIGH** — multiple LLM calls per scenario (multi-judge consensus, Mode-B per-criterion split).

The `--budget-tier low` flag filters the preset to keep only LOW and TRIVIAL tier evaluators. Useful for fast dev-loop iteration when you don't want to pay for the full sweep.

---

## How we know the judges can be trusted — **calibration**

The agentic benchmark uses many judges (one per LLM-graded dimension), each with its own criteria list and its own rubric as the system prompt (since 0.44; through 0.43 they shared a generic judge system prompt). A judge's `needs_review` verdict is a warn, which agrees with neither gold label. Each evaluator dispatched for calibration has its own golden dataset; some are carved out (see below).

### The golden datasets — reference truth per evaluator

For most LLM-judge evaluators, we hand-labeled a set of scenario+response pairs with the expected verdict and rationale. The datasets live as JSONL files under `tests/AgentEval.Tests/Agentic/Calibration/Golden/`.

Each dataset is *mixed-class by design* — it contains examples that should pass and examples that should fail. A single-class dataset would let the math collapse into a trivially-perfect-but-meaningless agreement number; mixed datasets force the judge to make real distinctions.

### The calibration run

`agenteval bench agentic calibrate` replays every golden entry through its judge, compares to the human label, and reports two numbers per evaluator category:

- **Accuracy** — fraction of entries where the judge agreed with the human label.
- **Cohen's kappa** — agreement *after subtracting what you'd expect from random chance*.

The report's header also names the judge provider and the judge model or deployment that produced those numbers, because they describe that judge and no other.

### How to read kappa (no math degree needed)

Kappa answers *"did the judge understand the task, or just guess?"*

| Kappa band | Plain-English meaning |
|---|---|
| **1.0** | Perfect agreement |
| **≥ 0.85** | Near-perfect — comparable to two human experts |
| **0.70 – 0.85** | Strong — the default requirement for benchmark categories |
| **0.40 – 0.70** | Moderate — well above guessing, room to improve |
| **0.20 – 0.40** | Fair — the judge gets it sometimes |
| **near 0** | No better than flipping a coin |

The default gate is *accuracy ≥ 85%* AND *kappa ≥ 0.70* per category, with zero evaluation failures.

### The honest-scope disclaimer

Calibration coverage is reported **per evaluator category**, and it is partial. A coverage gap is **not** a quality verdict on the evaluator — it means there is no measurement of how well its judge agrees with a human. Each evaluator is in one of two states:

- **Dispatched** — the evaluator has hand-labelled golden entries, and `bench agentic calibrate` runs them through the judge and gates the category on the result.
- **Carved out** — the evaluator exists, is wired, and produces a verdict at runtime, but `calibrate` skips it: it is pure code, a meta-evaluator, or it needs conversation history or a reasoning trace that a single-turn golden entry cannot carry. The verdict at runtime is still real (its criteria are still graded); there is just no measurement of how often its judge matches a human.

The dispatched evaluators are registered in `src/AgentEval.Evals.Agentic/AgenticEvalRegistration.cs`; the carved-out ones, each with its reason, are in `s_carveOutKeys` and `s_notCalibratableOnTheseGoldens` in `src/AgentEval.Cli/Commands/BenchAgenticCalibrateCommand.cs`. The table below summarises both by category.

### Calibration quality today

The project's latest figures, for one judge model on one day, are in [Calibration results](../calibration-results.md). The qualitative picture by category, with the gate each category is held to (`s_categoryOverrides` in `src/AgentEval.Cli/Commands/BenchAgenticCalibrateCommand.cs`; the default is 0.85 / 0.70):

| Category | Calibration status | Notes |
|---|---|---|
| System and Process | **Dispatched, relaxed gates** | All five system evaluators and four of the six process evaluators run in `calibrate`; Tool Input Accuracy and Tool Call Accuracy are left out by key, because the golden cases carry no tool definitions — their schema check cannot run, so they withhold every pass and only their fail predictions could be scored. Process is gated at 0.85 / 0.65 and system at 0.70 / 0.45 |
| RAG Quality | **Dispatched, relaxed gate** | Six of the seven run in `calibrate`; F1 Score is pure code and is carved out. Gated at 0.65 / 0.40 |
| Judge Quality | **N/A — meta** | Meta-evaluators have no separate judge to calibrate |
| Operational / Telemetry | **N/A — code-only** | No LLM judge to calibrate; deterministic from trace metadata |
| Safety | **Dispatched, relaxed gate** | Ten of the twelve run in `calibrate`; Prohibited Actions needs a policy resolver and a subject id, and Unsafe Tool Use needs tool calls the golden cases do not carry, so neither is dispatched. Gated at 0.80 / 0.60. See the content-filter note below |
| Memory | **Not calibrated (carved out)** | Memory Recall Accuracy and Long Conversation Coherence grade recall of earlier turns; a golden entry is single-turn, so `calibrate` skips them and reports the category as SKIP |
| Multi-turn | **Not calibrated (carved out)** | Same reason as Memory; `calibrate` files these three under its `memory` category |
| Reasoning | **Partly dispatched, relaxed gate** | Reasoning Correctness and Goal Decomposition run in `calibrate`; Plan Formulation and Intermediate-Step Hallucination need the agent's reasoning trace and are carved out. Gated at 0.70 / 0.40 |
| Calibration (epistemic) | **Partly dispatched, relaxed gate** | Confidence Calibration and Uncertainty Acknowledgment run in `calibrate`; Self-Correction Quality needs a reasoning trace and is carved out. Gated at 0.75 / 0.55 |
| UX / Communication | **Dispatched, default gate** | All three run in `calibrate` against the default 0.85 / 0.70 |
| Adversarial | **Dispatched, default gate** | All three run in `calibrate`, plus two calibration-only keys (`prompt_leak`, graded by System Prompt Leakage, and `escalation_resistance`, graded by Jailbreak Resistance); default 0.85 / 0.70. See the content-filter note below |
| Efficiency | **N/A — code-only** | Deterministic from cost and score |

Every category in this table runs at runtime and produces verdicts, whether or not `calibrate` covers it. A category reads **INCOMPLETE** (and fails the gate) when one of its dispatched evaluators was not measured on every record: that evaluator is left out of the scoring whole — scoring only its measured records would score a sample selected by its own verdicts. Six of the eight categories `calibrate` scores are held to relaxed gates rather than the 0.85 / 0.70 default. Before the memory and multi-turn evaluators were carved out, the judge scored 14.3% accuracy on their 21 single-turn entries — below chance — which is why they are skipped rather than gated (the measurement is recorded in the `s_carveOutKeys` remarks).

**Content-filter note.** An evaluation that throws (for example, a judge call the provider rejects) counts as an evaluation failure, which makes its category report INFRA-FAIL and fails the command. The calibrate command's own notes record that on Azure OpenAI the provider's content filter has blocked judge calls on the harmful-content goldens in the Safety and Adversarial categories.

---

## Why this is worth running

1. **Coverage.** No single number tells you whether an agent is good. The benchmark gives you many orthogonal angles — task completion, tool accuracy, RAG quality, reasoning, memory, safety — and shows where the agent succeeds and where it breaks.
2. **Diagnosability.** Composite evaluators surface sub-scores. A 0.4 on Tool Call Accuracy tells you something failed; the sub-scores tell you *which dimension* — selection, inputs, outputs, execution, or efficiency.
3. **Cost-tiered.** The `--budget-tier low` flag keeps inner-loop runs cheap. Operational evaluators run free (pure-code). Safety and RAG runs reserved for releases.
4. **Familiar evaluator concepts.** More than a third of the evaluator cards name a corresponding Azure AI Foundry evaluator, and about half of the rubric files under `Resources/Prompts/` are modelled on one. The rubric text is AgentEval's own, with its differences from the upstream evaluator listed in each file's header, and it is what the judge is sent, beside each evaluator's own criteria. Deterministic-first tool-call success and the sub-dimension splits are implemented in code.
5. **Calibration built in.** Golden datasets ship for the dispatched evaluators and `calibrate` measures the judge against them; six of the eight categories it scores are held to relaxed per-category gates (see the table above).
6. **Open.** Every evaluator card, prompt file, and golden entry is in the repo.

---

## What this benchmark is not

- **Not a regulatory benchmark.** For GDPR or EU AI Act, run the matching compliance benchmark — those carry audit-chain-validated evidence files; this one does not.
- **Not a production-load proxy.** Operational evaluators read trace data from your test runs, not from production at scale.
- **Not exhaustive.** Domain-specific factual accuracy still requires domain-authored ground truth and separate validation.
- **Not certified.** Results are evaluation artifacts, not compliance attestations.

---

## Where to look next

- [`getting-started.md`](getting-started.md) — how to run it.
- [`cost-guidance.md`](cost-guidance.md) — per-evaluator cost classification.
- [`evaluator-cards.md`](evaluator-cards.md) — the canonical per-evaluator reference.
- [`../../composite-evals.md`](../../composite-evals.md) — the underlying composition primitives.
- [`../../cli.md`](../../cli.md) — full CLI reference for `bench agentic`.
