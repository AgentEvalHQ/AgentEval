# GDPR Benchmark — How It Works (Plain-English Explainer)

This page explains, in everyday language, **what the GDPR benchmark actually does**, how it's built, how we know we can trust it, and why running it on your agent is worth the cost. No prior AI knowledge required.

> **One-line summary**: We ask an AI agent a set of carefully-chosen questions about personal-data scenarios, then have a second AI — the **judge** — grade the answers against the GDPR. We add up those grades through several layers to produce a single verdict: **PASS**, **WARN**, or **FAIL**.

---

## What this benchmark is — and isn't

**It is** a fast, repeatable way to check whether an AI agent gives sensible, regulation-aware answers about personal data. Think of it as a *driving test for an AI agent's data-protection behavior*: it doesn't prove the agent is safe in every possible situation, but it catches the obvious failure modes before they reach production.

**It is not** a legal compliance attestation. Real GDPR compliance also depends on encryption, breach-notification processes, DPIA paperwork, international-transfer mechanisms, and many other things that live outside any dialog. A passing score means *the agent's spoken behavior holds up under examination*, nothing more.

---

## The structure, bottom-up

The benchmark is a tower. The ground floor is one scenario; the top floor is the overall verdict. Each layer takes the layer below it and rolls it up.

```
                ┌─────────────────────────────┐
        TOP →   │  Overall verdict (PASS/...)  │
                ├─────────────────────────────┤
                │  5 Pillars (broad topics)   │
                ├─────────────────────────────┤
                │  Articles (specific rules)  │
                ├─────────────────────────────┤
                │  Atomic evaluators (1 grade)│
                ├─────────────────────────────┤
       BOTTOM → │  Scenarios (1 question)     │
                └─────────────────────────────┘
```

### Layer 1 — Scenarios (the ground floor)

A **scenario** is one specific situation that we put the agent through. For example: *"A user asks the agent to delete all their personal data. The user also has unpaid invoices."* The scenario carries a prompt for the agent, an expected behavior in plain language, and tags marking it as sensitive or domain-specific.

Each GDPR article has a handful of scenarios that probe the article from several angles — *the direct case*, *the trap case* ("can you delete the unpaid-invoice data?"), and *the edge case*. They live as YAML files under `samples/AgentEval.GdprBenchmark/Articles/Yaml/`.

### Layer 2 — Atomic evaluators (one grade per scenario)

For each scenario, we ask the agent to respond, then we ask **the judge** — a second AI — to grade the response. The judge is itself an LLM (typically GPT-4o-class), but it works from a *fixed grading rubric* loaded from a prompt file (`samples/AgentEval.GdprBenchmark/Resources/Prompts/gdpr-judge-system.v1.md`). The rubric tells the judge what to look for, how to cite the regulation, and how to score (pass / warn / fail with a 0–1 number).

This grade-by-rubric pattern is the **atomic evaluator**. It produces a score, a verdict (pass/warn/fail), a severity (low/medium/high/critical), a rationale, and an audit-trail entry.

### Layer 3 — Articles (composite of several scenarios)

The scenarios for one GDPR article (say *Art 17 — Right to erasure*) are bundled into a **composite evaluator**. The composite rolls its scenarios up into a single article-level score using an aggregation rule:

- **Weighted sum** (the default): scenarios contribute proportionally to their `weight` field. A scenario the dataset author marked "this is the central probe" weighs more than a passing edge case.
- **Min**: the article scores the *worst* of its scenarios. Used where a single bad answer should drag the article down — e.g., child-consent rules.
- **CapByWorst** (audit mode): like min, but only triggered when a high-severity failure is present. Lets one critical bad answer cap the whole verdict at FAIL while still showing the other scenario scores.

### Layer 4 — Pillars (broad topical groupings)

Articles roll up into **pillars** by topic. The GDPR benchmark has five:

| Pillar | What it covers |
|---|---|
| 1 — Foundations | The principles in Art 5 (lawfulness, purpose, minimisation, accuracy, storage, integrity) plus Art 6 (lawful basis), Art 7 (consent), Art 8 (children), Art 9 (special categories) |
| 2 — Lawful basis | Specifically the legal-basis articles, given outsized attention |
| 3 — Subject rights | Arts 15–22 — access, rectification, erasure, restriction, portability, objection, automated decision-making |
| 4 — Transparency | Arts 13–14 — what you must tell the data subject and when |
| 5 — Privacy by design | Arts 25, 32 — design-time and security obligations |

Each pillar uses the same weighted-sum-or-min-or-cap rollup. **Pillar weights** are deliberately set: lawful basis weighs more than transparency in the overall score because a foundational-principles failure has worse consequences than a wording failure.

### Layer 5 — Overall verdict

The pillars roll up into one final verdict for the benchmark run:

| Verdict | What it means |
|---|---|
| **PASS** | Every article scored at or above its pass threshold and no critical-severity failures. |
| **WARN** | At least one article was in the warn band, but no critical failures. Investigation recommended. |
| **FAIL** | At least one article failed at high or critical severity, or CapByWorst applied. |

Two GDPR articles are marked **Critical** by design: **Art 9** (special-category data) and **Art 22** (automated decision-making). A failure on either is escalated to critical severity regardless of the raw score, because the legal risk of getting these wrong dwarfs the rest.

---

## Presets — the same machine, different settings

Running the benchmark involves picking a **preset** that selects which articles to include and which aggregation rules to apply. They span a fast inner-loop check to a full release-gate sweep:

| Preset | When to use | Cost band |
|---|---|---|
| `smoke` | On every commit / PR | LOW |
| `standard` | Sprint reviews, team QA | MEDIUM |
| `audit` | DPO review, release sign-off (adds CapByWorst + optional multi-judge) | HIGH |
| `healthcare`, `hr`, `childrens` | Vertical-specific extensions on top of `standard` | LOW (each) |

Presets compose with a `+` — e.g., `--preset standard+healthcare`. Weights renormalize automatically.

---

## How we know the judge can be trusted — **calibration**

The benchmark's results are only as good as the judge that produces them. **Calibration** is how we keep the judge honest.

### The golden datasets — our reference truth

For each pillar, we hand-labeled a set of scenario+response pairs as **pass** or **fail**, each carrying a rationale that cites the specific GDPR article (and sub-article) that justifies the label. These live as JSONL files under `tests/AgentEval.Tests/GdprBenchmark/Calibration/Golden/`.

Crucially, the golden datasets contain **both kinds of examples**: clearly correct answers (`pass`) and clearly wrong answers (`fail`). A single-class dataset (all-pass or all-fail) would make the math collapse — we'd never know if the judge was lazily agreeing with whichever label dominated. Mixed datasets force the judge to make real distinctions.

### The calibration run

When we run `agenteval bench gdpr calibrate`, the runner:

1. Replays every golden entry through the judge.
2. Compares the judge's verdict to the human label.
3. Reports two numbers per pillar:
   - **Accuracy** — the share of entries where the judge agreed with the human label.
   - **Cohen's kappa** — agreement *after subtracting what you'd expect from random chance*.

### How to read kappa (no math degree needed)

Cohen's kappa is the single most useful number in inter-rater agreement. It answers the question *"did the judge actually understand the task, or did it just guess and get lucky?"*

| Kappa band | Plain-English meaning |
|---|---|
| **1.0** | Perfect agreement |
| **≥ 0.85** | Near-perfect — comparable to two human experts agreeing |
| **0.70 – 0.85** | Strong — what we require by default for compliance pillars |
| **0.40 – 0.70** | Moderate — well above guessing, but room to improve |
| **0.20 – 0.40** | Fair — the judge gets it sometimes |
| **near 0** | No better than flipping a coin |

The **default gate** is *accuracy ≥ 85%* AND *kappa ≥ 0.70* per pillar, with **zero evaluation failures** (no judge crashes). Some pillars carry a documented relaxed gate because of small-N stochasticity or known regulatory ambiguity — every relaxation comes with a written investigation path to retire it.

### The release gate

A pillar that fails any threshold blocks the release PR. The CI workflow (`.github/workflows/gdpr-calibration.yml`) runs calibration on every release-branch PR. We only ship a benchmark version when **every pillar** passes calibration.

### Calibration quality today

Rather than print numbers that change with every refinement, here's the qualitative picture across the five pillars. Specific kappa and accuracy values live in the dated baseline report under `strategy/FutureFeatures/calibration-baselines/gdpr-calibration-{date}.md`.

| Pillar | Calibration quality | Notes |
|---|---|---|
| 1 — Foundations | **HIGH** | Strict default gate met |
| 2 — Lawful basis | **HIGH** | Strict default gate met |
| 3 — Subject rights | **HIGH** | Strict default gate met |
| 4 — Transparency | **HIGH** | Strict default gate met |
| 5 — Privacy by design | **HIGH** | Strict default gate met |

If any pillar were to drop into MEDIUM or LOW, the corresponding finding would land in the consolidated tracker (`strategy/FutureFeatures/todo/12-6plan-review-findings-and-fixes.md`) with a fix path before the next release.

---

## Why this is worth running

1. **Speed.** A smoke run finishes in seconds. A standard run finishes in a few minutes. You get fast feedback on whether your agent is starting to drift on a regulation that takes years to learn manually.
2. **Repeatability.** The same scenarios run on every release. You can see trends over time, not just one-shot snapshots.
3. **Defensible evidence.** Each run produces a JSON evidence file, a markdown report, and a PDF — all sealed with an audit-chain hash that `agenteval doctor` can re-verify. You can hand the evidence to a DPO, attach it to a PR, or store it for an audit.
4. **Regulator-grade reasoning.** The judge's rubric is built from the actual EU regulation text. Citations in the report point to the specific article and sub-article that justify each verdict.
5. **Calibrated quality.** We don't ship a benchmark version until its judge agrees with hand-labeled human experts at near-expert levels. Bench you can trust isn't free — it takes the work above.
6. **Open and inspectable.** Every prompt, every scenario, every golden entry is in the repo. You can read why the judge graded the way it did, and disagree if you want.

---

## What this benchmark is not

- **Not a legal opinion.** A passing run doesn't mean a regulator will agree. Always loop in a qualified DPO.
- **Not a guarantee for production.** New scenarios outside the tested set may surface failures the benchmark doesn't catch.
- **Not the only signal.** Treat it as one input among many: code review, red-teaming, customer feedback, incident response data.
- **Not exhaustive.** v1 covers the dialog-observable subset of GDPR. Process obligations (DPIAs, breach notification timelines, international-transfer paperwork) live outside any dialog benchmark by definition.

---

## Where to look next

- [`getting-started.md`](getting-started.md) — how to actually run it.
- [`../../composite-evals.md`](../../composite-evals.md) — the underlying composition primitives.
- [`../../cli.md`](../../cli.md) — full CLI reference for `bench gdpr` and `bench gdpr calibrate`.
- [`../../agenteval-workspace.md`](../../agenteval-workspace.md) — where evidence is written and how `agenteval doctor` validates it.
