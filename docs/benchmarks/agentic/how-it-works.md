# Agentic Benchmark — How It Works (Plain-English Explainer)

This page explains, in everyday language, **what the agentic benchmark does**, how it's built, how we know we can trust it, and why running it on your agent is worth the cost. No prior AI knowledge required.

> **One-line summary**: We give an AI agent a set of carefully-chosen tasks (often with tool calls), then have a second AI — the **judge** — grade the agent's behaviour across many quality dimensions: task completion, tool-call accuracy, RAG groundedness, reasoning, safety, memory, multi-turn coherence, and more. We add up those grades into category-level and overall verdicts.

---

## What this benchmark is — and isn't

**It is** a structured way to ask the question *"is this agent any good?"* across the many dimensions that matter for an autonomous AI agent — beyond just "does it return the right answer". It evaluates behaviour: did it pick the right tools, use them correctly, ground its claims in retrieved evidence, handle multi-turn conversation gracefully, reason soundly, refuse appropriately, and stay within latency and cost budgets?

**It is not** a regulatory compliance attestation (use the GDPR or EU AI Act benchmarks for that), not a production-deployment certification, and not a substitute for domain testing in your specific business setting. A passing score tells you the agent's behaviour is sound on a *general* set of probes — your domain may require more.

---

## The structure, bottom-up

The agentic benchmark is organised by *evaluator* rather than by article. There are around 60 named evaluators grouped into ten categories. Each evaluator answers one specific question about agent behaviour.

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

- **LLM-judge evaluators** — a second AI grades the agent's output against a rubric (e.g., *Task Completion*, *Groundedness*, *Coherence*). The rubric is loaded from a prompt file (`src/AgentEval.Evals.Agentic/.../Prompts/*.prompty` or equivalent). Most rubrics are forked from the public MIT-licensed Azure SDK evaluators with documented modifications.
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

### Layer 4 — Categories (ten broad areas)

Atomic + composite evaluators group into **ten categories** by concern. You don't have to run every evaluator every time; you pick a preset that activates a coherent subset.

| Category | What it covers |
|---|---|
| **System and Process** | Task completion, task adherence, intent identification, intent resolution, navigation efficiency, the five tool-call sub-evaluators |
| **RAG Quality** | Groundedness, relevance, coherence, fluency, similarity, response completeness, F1 score |
| **Judge Quality** | Meta-evaluators (no LLM): judge agreement, calibration accuracy, judge drift — for evaluator-health monitoring |
| **Operational / Telemetry** | Pure-code: latency, token usage, cost, error rate, retry rate, tool latency, stochastic stability |
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

- **NONE** — pure-code evaluators that read trace metadata only (no LLM calls). Examples: Latency, Token Usage, F1 Score, Cost, Error Rate.
- **LOW** — single LLM call per scenario, shorter prompts.
- **MEDIUM** — single LLM call per scenario, longer prompts (multi-criterion grading).
- **HIGH** — multiple LLM calls per scenario (multi-judge consensus, Mode-B per-criterion split).

The `--budget-tier low` flag filters the preset to keep only LOW and NONE tier evaluators. Useful for fast dev-loop iteration when you don't want to pay for the full sweep.

---

## How we know the judges can be trusted — **calibration**

The agentic benchmark uses many judges (one per LLM-graded dimension), each with its own rubric. Each judge gets its own golden dataset and its own calibration pass.

### The golden datasets — reference truth per evaluator

For each LLM-judge evaluator, we hand-labeled a set of scenario+response pairs with the expected verdict and rationale. The datasets live as JSONL files under `tests/AgentEval.Tests/Agentic/Golden/`.

Each dataset is *mixed-class by design* — it contains examples that should pass and examples that should fail. A single-class dataset would let the math collapse into a trivially-perfect-but-meaningless agreement number; mixed datasets force the judge to make real distinctions.

### The calibration run

`agenteval bench agentic calibrate` replays every golden entry through its judge, compares to the human label, and reports two numbers per evaluator category:

- **Accuracy** — fraction of entries where the judge agreed with the human label.
- **Cohen's kappa** — agreement *after subtracting what you'd expect from random chance*.

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

The agentic benchmark publishes calibration coverage **per evaluator category**, and the current release is honest about which categories are fully calibrated and which still rely on synthetic-or-partial coverage. A categorical-coverage gap is **not** a quality problem — it's a coverage problem, and it's tracked publicly. The headline split today:

- **Calibrated** — every evaluator in the category has a hand-labelled golden dataset that runs in `bench agentic calibrate` and meets the calibration gate. These categories produce evidence you can stand behind.
- **Coverage gap** — the evaluator exists, is wired, and produces a verdict at runtime, but its golden dataset is either absent or below target size. The verdict at runtime is still real (the rubric still runs); we just can't tell you with the same confidence how well the judge matches a human on this evaluator.

The full categorisation per evaluator is in [`docs/benchmarks/agentic/evaluator-cards.md`](evaluator-cards.md). The v1.1 plan closes the coverage gap (see [`strategy/FutureFeatures/todo/11-v1.1-implementation-plan.md`](../../../strategy/FutureFeatures/todo/11-v1.1-implementation-plan.md) — task 1.3 "Agentic calibration coverage").

### Calibration quality today

Specific kappa and accuracy values live in the dated baseline report under `strategy/FutureFeatures/calibration-baselines/agentic-calibration-{date}.md`. The qualitative picture by category:

| Category | Calibration quality | Notes |
|---|---|---|
| System and Process | **HIGH** (calibrated subset) | Headline tool-call and task evaluators meet the strict default gate |
| RAG Quality | **HIGH** (calibrated subset) | Groundedness, relevance, completeness meet the gate |
| Judge Quality | **N/A — meta** | Meta-evaluators have no separate judge to calibrate |
| Operational / Telemetry | **N/A — code-only** | No LLM judge to calibrate; deterministic from trace metadata |
| Memory | **MEDIUM** (coverage gap) | Calibration coverage being expanded in v1.1 |
| Multi-turn | **MEDIUM** (coverage gap) | Calibration coverage being expanded in v1.1 |
| Reasoning | **MEDIUM** (coverage gap) | Calibration coverage being expanded in v1.1 |
| Calibration (epistemic) | **MEDIUM** (coverage gap) | Calibration coverage being expanded in v1.1 |
| UX / Communication | **MEDIUM** (coverage gap) | Calibration coverage being expanded in v1.1 |
| Adversarial | **MEDIUM** (coverage gap) | Calibration coverage being expanded in v1.1 |
| Efficiency | **N/A — code-only** | Deterministic from cost and score |

Categories shown as MEDIUM run at runtime and produce verdicts — they just await fuller calibration evidence before we can put HIGH next to them. The honest-scope disclaimer in `getting-started.md` covers this in more detail.

---

## Why this is worth running

1. **Coverage.** No single number tells you whether an agent is good. The benchmark gives you ten orthogonal angles — task completion, tool accuracy, RAG quality, reasoning, memory, safety — and shows where the agent succeeds and where it breaks.
2. **Diagnosability.** Composite evaluators surface sub-scores. A 0.4 on Tool Call Accuracy tells you something failed; the sub-scores tell you *which dimension* — selection, inputs, outputs, execution, or efficiency.
3. **Cost-tiered.** The `--budget-tier low` flag keeps inner-loop runs cheap. Operational evaluators run free (pure-code). Safety and RAG runs reserved for releases.
4. **Forked-from-Foundry.** The LLM-judge prompts trace back to the public Azure SDK Foundry evaluator prompts — same lineage as the Microsoft tooling, with documented improvements (deterministic-first tool-call success, structured failure-type taxonomy, multi-judge consensus, sub-dimension splits).
5. **Calibrated where it matters.** The headline System-and-Process and RAG categories meet the strict calibration gate today. The expansion to full coverage is tracked publicly and scheduled.
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
