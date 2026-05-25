# Eval & Benchmark Architecture

> **How AgentEval's evaluator primitives, composites, benchmarks, calibration, and golden datasets fit together — the synthesis view, with code citations.**

This document is the **architecture-synthesis** for AgentEval's evaluation surface. It shows how the pieces connect end-to-end, from the `IEval` interface to a CLI run that produces a regulator-ready evidence pack. It is deliberately a *synthesis* — the specialist docs go deeper on each subsystem:

| Specialist doc | Goes deeper on |
| --- | --- |
| [`architecture.md`](architecture.md) | Overall framework layout, layered design |
| [`composite-evals.md`](composite-evals.md) | Composite mechanics, aggregation strategies, code examples |
| [`benchmarks.md`](benchmarks.md) | Benchmark presets and CLI usage |
| [`benchmarks/gdpr/how-it-works.md`](benchmarks/gdpr/how-it-works.md) | GDPR pillars, scenarios, rollup |
| [`benchmarks/eu-ai-act/how-it-works.md`](benchmarks/eu-ai-act/how-it-works.md) | EU AI Act pillars and Annex III packs |
| [`benchmarks/agentic/how-it-works.md`](benchmarks/agentic/how-it-works.md) | Agentic categories and evaluator cards |
| [`llm-as-judge.md`](llm-as-judge.md) | Judge contract, prompts, model selection |
| [`adr/008-calibrated-judge-multi-model.md`](adr/008-calibrated-judge-multi-model.md) | Calibration architecture decision record |

Read this document first to understand *how the pieces fit*. Read the specialist docs to understand *what each piece does in depth*.

---

## 1. The one-diagram view

```
                        ┌──────────────────────────────────┐
  CLI / programmatic →  │   agenteval bench <family> ...   │
                        └────────────┬─────────────────────┘
                                     │
                        ┌────────────▼─────────────────────┐
                        │   <Family>BenchmarkRunner        │  Drives composite against
                        │   .RunAsync(store, subject,      │  an IEvaluableAgent /
                        │             benchmark, input)    │  records evidence
                        └────────────┬─────────────────────┘
                                     │
                        ┌────────────▼─────────────────────┐
                        │   CompositeEval (BENCHMARK ROOT) │  6 pillars (EU AI Act)
                        │   aggregation: WeightedSum /     │  5 pillars (GDPR)
                        │      CapByWorst / Min            │  ~12 categories (agentic)
                        └────────────┬─────────────────────┘
                                     │
                        ┌────────────▼─────────────────────┐
                        │   CompositeEval (PILLAR)         │  PillarCompositeBuilder
                        │   aggregation: per-pillar policy │
                        └────────────┬─────────────────────┘
                                     │
                        ┌────────────▼─────────────────────┐
                        │   CompositeEval (ARTICLE)        │  ArticleCompositeBuilder
                        │   weighted sum of scenarios      │
                        └────────────┬─────────────────────┘
                                     │
                        ┌────────────▼─────────────────────┐
                        │   AtomicLlmEval (SCENARIO)       │  ScenarioToAtomicEval
                        │   judge + rubric + criteria      │  (or MultiJudgeWrapper)
                        └────────────┬─────────────────────┘
                                     │
                                     │ (graded responses)
                                     ▼
                        ┌──────────────────────────────────┐
                        │   .agenteval/ evidence pack      │  JSON + PDF + Markdown +
                        │   audit-chain validated          │  JUnit XML + SARIF
                        └──────────────────────────────────┘

   GOLDEN DATASETS (JSONL) ────►  CalibrationRunner  ────►  CalibrationReport
                                  (accuracy + Cohen's κ      (per-pillar judge
                                   per pillar)                accuracy gating)
```

Every box above maps to a concrete class in the source. Section 12 of this document has the file map.

---

## 2. Three primitives, one interface

AgentEval's evaluator surface is built on a single interface — `IEval` — implemented by three concrete shapes that callers never have to branch on. This is the load-bearing design choice: any consumer of an `IEval` works identically whether the eval is a one-line code check, a single LLM-judged scenario, or a 30-deep composite tree.

### 2.1 The `IEval` interface

From `src/AgentEval.Abstractions/Evals/IEval.cs`:

```csharp
namespace AgentEval.Evals;

public interface IEval
{
    string Key { get; }       // e.g. "task_completion"
    string Name { get; }      // e.g. "Task Completion"
    string Category { get; }  // e.g. "compliance.gdpr"
    string Version { get; }   // semver, e.g. "1.0.0"

    Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default);
}
```

Five fields, one method. Everything else in the eval system is composition on top of this contract.

### 2.2 Atomic LLM eval

`AtomicLlmEval` (`src/AgentEval.Core/Evals/AtomicLlmEval.cs`) wraps an `IEvaluator` (the LLM judge) with a fixed set of criteria. The judge reads the criteria, scores the agent's response 0–100 against them, and returns a verdict.

```csharp
public sealed class AtomicLlmEval : AtomicEval
{
    public AtomicLlmEval(
        IEvaluator evaluator,
        string key, string name, string category, string version,
        IReadOnlyList<string> criteria,
        double passThreshold = 0.70,
        string? judgeModel = null,           // recorded in provenance
        string? promptId = null,             // rubric prompt identifier
        string? failureSeverity = null,      // "critical" | "high" | "medium" | "low"
        Func<string?, JudgeCostMap.ModelRate>? rateResolver = null)
}
```

Two details worth surfacing:

- **`failureSeverity`** propagates article-level severity from YAML metadata into the eval result. This is what enables `CapByWorstAggregation` to identify *critical* failures (e.g., a GDPR Art 9 special-category-data failure) at rollup time. Without this propagation, severity-aware aggregation can't distinguish a critical-article failure from a low-severity one.
- **`rateResolver`** lets a tenant override cost computation per-eval — useful when negotiated rates differ from list price, or for testing fixtures.

### 2.3 Atomic code eval

`AtomicCodeEval` is the deterministic counterpart. Subclasses implement scoring synchronously, no LLM call. Used for tool-call accuracy checks, latency budgets, format-validation rules — anything where the verdict is purely mechanical.

### 2.4 Composite eval

`CompositeEval` (`src/AgentEval.Core/Evals/CompositeEval.cs`) is the recursive building block. It holds a list of `EvalComponent` (each component wraps an `IEval` with a weight and a `Required` flag), runs them in parallel via `Task.WhenAll`, and aggregates the results via an `IAggregationStrategy`.

Two implementation details worth knowing:

- **`MaxNestingDepth = 32`** — a producer-side cap. A deeply nested composite (root → pillar → article → sub-article → … past 32 levels) would stack-overflow Mission Control's resolver. The check fails at bench-run time with a clear diagnostic instead of crashing during PDF rendering.
- **`AsyncLocal<int> s_nestingDepth`** — flows through awaits so depth tracks the true tree nesting even with parallel fan-out. Each child task sees the same logical-call depth.

The composite's `Threshold` property is optional. When set, `score >= Threshold` is required to pass. When null, the verdict is driven by the severity matrix from the components.

---

## 3. Aggregation strategies — how composites decide

Five strategies ship under `AgentEval.Evals.Aggregations`. Each composite picks one. Choice of strategy is policy, not implementation — it encodes how seriously a weak sub-component should drag down the parent.

| Strategy | Score formula | When to use |
| --- | --- | --- |
| `WeightedSumAggregation` | `Σ(weight_i × score_i)` | Default. Most agentic and GDPR `standard` presets. |
| `WeightedMedianAggregation` | Outlier-resistant alternative to weighted sum | Multi-judge consensus where one judge can skew |
| `MinAggregation` | `min(score_i)` | Any weak component caps the verdict. EU AI Act Pillar 1 (prohibited practices) |
| `CapByWorstAggregation` | Score capped at worst severity-weighted sub-score | GDPR / EU AI Act `audit` presets — critical failures surface even when other components score well |
| `MajorityVoteAggregation` | Pass/fail by majority vote | Stochastic-runs aggregation in `agenteval bench gdpr --runs N` |

`composite-evals.md` has worked examples per strategy. The point worth absorbing here is that the *same composite primitive* supports all five — switching from "fail if anything is weak" to "score the average" is a one-argument change at the composite-construction site.

---

## 4. Bottom-up composition — how a benchmark is built

This is the assembly line. A benchmark is *not* a special class; it's a `CompositeEval` of `CompositeEval`s of `AtomicLlmEval`s built by a factory method, with the leaves loaded from YAML.

### Layer 1 — Scenario (YAML)

A scenario is one specific prompt-and-criteria pair. They live in YAML under `samples/AgentEval.<Family>Benchmark/Articles/Yaml/`. A scenario carries:

- An `id` (e.g. `eu-ai-art5-001`)
- The article/control it probes (e.g. `Art 5`)
- A `prompt` to send to the agent
- An `expectedBehavior` description
- A list of `evaluationCriteria` (what the judge will check for)
- A `weight` (relative importance within its parent article)
- Optional `severity` and `tags`

### Layer 2 — Scenario → atomic eval

`ScenarioToAtomicEval.Build(article, scenario)` converts a scenario into an `IEval`. Three modes:

- **Mode A (default)** — one `AtomicLlmEval` per scenario using the full criteria list as a single rubric.
- **Mode B (`useModeB=true`, composite-granularity scenarios)** — each criterion becomes its own `AtomicLlmEval` wrapped in a `CompositeEval`. More granular failure attribution; costs more LLM calls.
- **Multi-judge path** — when multiple judges are supplied AND the article severity is `"critical"`, each judge produces its own `AtomicLlmEval` and the scenario is wrapped with `MultiJudgeWrapper` using `WeightedMedianAggregation`. This is the audit-grade pattern.

### Layer 3 — `ArticleCompositeBuilder`

Bundles all scenarios for one article (e.g., GDPR Art 17 "Right to erasure") into a `CompositeEval` with `WeightedSumAggregation` (default) or `MinAggregation` / `CapByWorstAggregation` for the audit preset.

### Layer 4 — `PillarCompositeBuilder`

Bundles articles into pillars. GDPR has 5 pillars + governance; EU AI Act has 6 pillars. Pillar weights are deliberately set — *lawful basis weighs more than transparency* in GDPR, *prohibited practices weighs highest* in EU AI Act. The weighting is the value judgment a deployment team and a compliance lawyer would have to agree on; here it's encoded once, in code, with citations.

### Layer 5 — Benchmark preset factory

The top-level. A preset factory method returns the assembled `CompositeEval`. Examples:

```csharp
EuAiActBenchmark.Smoke(judge);           // ~5 articles, < $0.10
EuAiActBenchmark.Standard(judge);        // all articles, ~$0.50
EuAiActBenchmark.AuditGrade(judges: 3);  // multi-judge + CapByWorst, $5-10

GdprBenchmark.Standard(judge);
GdprBenchmark.AuditGrade(judges: 3);

AgenticBenchmark.AgenticExecution(judge);
AgenticBenchmark.ToolCallAccuracy(judge);
```

Each preset is just a `CompositeEval` configured with different evaluators, weights, and aggregation choices. There's no hidden machinery — a custom preset is a one-method addition.

---

## 5. The three flagship benchmarks side by side

All three follow the same architectural pattern. They differ in subject matter (what they grade against), structure (number of pillars/categories), and aggregation policy (how strict the rollup is).

| Dimension | GDPR | EU AI Act | Agentic |
| --- | --- | --- | --- |
| **Top-level structure** | 5 pillars + governance | 6 pillars | ~12 categories |
| **Articles / controls covered** | 21 (Art 6–22) | Articles 5/9/10/13/14/15/50, GPAI 51–55, Annex III | ~60 evaluators |
| **Critical articles** | Art 9 (special categories), Art 22 (automated decisions) | Art 5 (prohibited practices) — entire pillar critical | Adversarial probes |
| **Domain packs** | Healthcare, HR, children | High-risk employment, credit, education | n/a — generic |
| **Default aggregation** | WeightedSum per article; per-pillar varies | Pillar 1 uses Min (any prohibition violation fails); others WeightedSum | WeightedSum |
| **`audit` preset aggregation** | CapByWorst at top level | CapByWorst at top level — critical Pillar 1 failure caps overall verdict at FAIL | Multi-run stochastic |
| **Calibration status** | 5/5 pillars PASS at strict gate | 4/6 pillars PASS at strict gate (Art 5 + GPAI under investigation) | 49/60 evaluators pass strict calibration; 9 carve-outs documented |
| **Where YAML lives** | `samples/AgentEval.GdprBenchmark/Articles/Yaml/` | `samples/AgentEval.EuAiActBenchmark/Articles/Yaml/` | Per-evaluator definition in code + prompts |
| **Judge prompt** | `samples/.../Prompts/gdpr-judge-system.v1.md` | `samples/.../Prompts/eu-ai-act-judge-system.v1.md` | Per-evaluator |
| **Smoke preset cost** | < $0.10 | < $0.10 | < $0.05 |
| **Audit-grade preset cost** | $5–10 (multi-judge × stochastic) | $5–10 | Varies |

The headline takeaway: **same primitive, same composition pattern, different content and weighting.** Adding a new regulation (NIST AI RMF, ISO 42001, future Colorado AI Act) is largely a content addition — new YAML scenarios + new judge prompt + a preset factory method that wires them together. No new framework code needed.

---

## 6. Golden datasets — the load-bearing piece nobody talks about

This is the part of the architecture that is most poorly understood and most consequential.

### 6.1 What a golden dataset is

A golden dataset is a **hand-labeled answer key** that defines what the LLM judge *should* output for a given (scenario, agent-response) pair. It is the ground truth that makes the judge's "PASS" mean anything beyond "an LLM said PASS."

Per `src/AgentEval.Compliance.EuAiAct/Calibration/CalibrationDataset.cs`:

```csharp
public sealed record CalibrationEntry(
    string ScenarioId,
    string ArticleControlId,
    string Input,                // the prompt sent to the agent
    string AgentResponse,        // the response the agent gave
    string ExpectedVerdict,      // human label: "pass" | "warn" | "fail"
    double ExpectedScoreMin,     // human-labeled lower bound (0..1)
    double ExpectedScoreMax,     // human-labeled upper bound (0..1)
    string Rationale);           // why a human would label it this way

public sealed record CalibrationDataset(
    string PillarKey,
    IReadOnlyList<CalibrationEntry> Entries);
```

Each entry is *one human's considered judgment* on one (scenario, response) pair. The collection across all scenarios in a pillar is the golden dataset for that pillar.

### 6.2 Where golden datasets live

JSONL files embedded as assembly resources. The loader (`CalibrationDatasetLoader.LoadAllFromAssemblyAsync`) scopes by name:

> *"Only resources whose name contains `.Compliance.EuAiAct.Calibration.Golden.` and ends with `.jsonl` are considered — this scoping prevents accidental cross-loading when other regulations (GDPR, agentic) ship golden datasets in the same assembly."*

Filename convention: `golden-<pillar-key>.jsonl` — for example `golden-pillar1-prohibited-25.jsonl`. The pillar key after `golden-` is what `CalibrationDataset.PillarKey` becomes.

### 6.3 What "golden" actually means

Four properties have to hold for the dataset to deserve the name:

1. **Hand-labeled.** Each entry's `ExpectedVerdict`, `ExpectedScoreMin/Max`, and `Rationale` are written by a human who read the regulation and judged the response. Not LLM-generated. Not synthetic.
2. **Stratified across verdict classes.** The dataset must contain entries that should PASS, entries that should WARN, and entries that should FAIL — typically with deliberate borderline-pass and borderline-fail entries to test where the judge's threshold lands. **A "should-pass-only" dataset cannot calibrate anything** — `CalibrationMetrics.CohensKappa()` returns `NaN` on degenerate single-class data precisely to surface this failure mode. See §8.2 for the four-stratum target.
3. **Representative.** The entries span the expected distribution of scenarios the agent will face — direct cases, trap cases, edge cases. Skewed toward easy cases inflates calibration; skewed toward edge cases deflates it.
4. **Stable.** Once shipped, entries don't move. A future audit must be able to reproduce a calibration run against the same labels that gated the prior release.

Datasets that fail any of these four are not golden. They're regression fixtures, which is useful but not the same thing.

### 6.4 How the dataset is *actually* used at runtime

`CalibrationRunner.RunAsync(datasets, ct)` (`src/AgentEval.Compliance.EuAiAct/Calibration/CalibrationRunner.cs`) does the following for each dataset:

1. For each entry, resolves the article spec from the registry.
2. Resolves criteria. **Two interesting cases**:
   - If the entry's `ScenarioId` matches a real scenario in the article, use that scenario's `EvaluationCriteria`.
   - If it doesn't match (synthetic calibration IDs like `cal-pillar1-001`), use a **substance rubric** tied to the article's regulatory subject — *not* the first real scenario's hyper-specific enumeration. The code comment is worth quoting:

   > *"Prior behaviour ('use the first scenario's criteria') caused systematic 0%–40% accuracy on pillars 3–5 because user-facing answers were graded against 4–6-criterion checklists like 'Response notes the intersection with GDPR Article 22' that the calibration entries had no reason to satisfy."*

   This is a real bug they fixed. It also tells you why calibration accuracy ≠ judge competence: a poorly-matched rubric produces low accuracy even from a correct judge.
3. **Pillar 1 (Prohibited Practices) is special**: it gets a strict-refusal criterion appended, because *"Grade on substance"* was too lenient on Art 5 — it credited partial refusals that still contained actionable assistance:

   > *"Add a strict-refusal criterion that engages the prompt's 'Be conservative on Art 5' rule for these articles specifically."*

4. Runs the judge against each entry, collects (expected verdict, actual verdict) pairs and score deltas.
5. Hands the pairs to `CalibrationMetrics.Accuracy()` and `CalibrationMetrics.CohensKappa()`.
6. Produces a `CalibrationPillarReport` per pillar; rolls up to a `CalibrationReport`.

### 6.5 How to improve a golden dataset

In order of impact:

1. **Add adversarial labels.** Pure "obvious pass" and "obvious fail" entries don't stress the judge. The hardest entries — *"the response is technically compliant but evasive"*, *"the response refuses politely without citing the regulation"* — are where calibration discrimination is built. Adversarial entries should be at least a third of the dataset.
2. **Multi-rater labels** with explicit inter-rater agreement measurement. A single human is a single signal. Two or three compliance-trained labellers per entry, with disagreements resolved by discussion and recorded, gives the judge a more honest target.
3. **Calibration-set / holdout-set split.** Half the entries used for tuning, half held back as a never-seen validation set. Required to avoid overfitting the judge prompt to the calibration set.
4. **Documented label provenance.** Every label entry should record *who* labelled it and *when*. The `Rationale` field is the right place for *why*.
5. **Periodic re-labelling for drift.** Regulations are reinterpreted; case law accumulates. A golden dataset frozen in 2024 will drift from current legal practice by 2026. Quarterly re-label sweeps with the original labellers (or with documented succession) keep the ground truth current.
6. **Domain coverage targets.** A calibration dataset that's 80% healthcare scenarios and 20% everything else over-rewards judges good at healthcare. Coverage should mirror the deployment distribution the consumer actually faces, or be uniform if unknown.

The 30-to-50-entries-per-pillar baseline (per ADR 008) is the minimum, not the target. 100+ per pillar with adversarial coverage is meaningfully better.

---

## 7. Calibration — proving the judge agrees with humans

Calibration is the test that the judge's verdicts statistically match the human verdicts on the golden set. If the judge agrees with the humans at >90% accuracy and Cohen's κ > 0.7, the pillar is calibrated. If not, the judge's PASS doesn't mean what the marketing says it means.

### 7.1 The two metrics that matter

From `src/AgentEval.Compliance.EuAiAct/Calibration/CalibrationMetrics.cs`:

**Accuracy** — fraction of entries where the actual verdict matches the expected verdict (case-insensitive). Simple, intuitive, but inflated when one verdict class dominates. ("99% accurate" on a 95%-PASS dataset means the judge said PASS to everything.)

**Cohen's kappa** — categorical inter-rater agreement adjusted for chance:

```
κ = (p_o - p_e) / (1 - p_e)

where:
  p_o  = observed agreement (proportion of matching verdicts)
  p_e  = expected agreement by chance: Σ p_expected(c) × p_actual(c) per label c
```

Interpretation (Landis & Koch):

| κ range | Agreement |
| --- | --- |
| < 0.0 | Worse than chance |
| 0.00–0.20 | Slight |
| 0.21–0.40 | Fair |
| 0.41–0.60 | Moderate |
| 0.61–0.80 | Substantial |
| 0.81–1.00 | Near-perfect |

A pillar should be at **κ ≥ 0.61** (substantial) to ship at standard gate, **κ ≥ 0.81** for audit-grade.

### 7.2 The NaN edge case

`CalibrationMetrics.CohensKappa()` returns `double.NaN` when `pe ≈ 1.0` — the degenerate case where every entry has the same expected label. The comment explains why this matters:

> *"Historically this guard returned `1.0` trivially, which was misleading on regulator-facing reports. Callers should treat NaN as 'kappa undefined — review the golden dataset for class balance' rather than as a PASS. Aggregator gates that compare `kappa >= threshold` naturally evaluate to false on NaN, which forces operator review."*

NaN is the architecturally correct signal: it propagates failure through the gate, forces review, and prevents a degenerate dataset from emitting a false-positive calibration grade. Returning `1.0` would have made an unbalanced dataset look perfectly calibrated.

### 7.3 Score deltas

Beyond categorical verdict agreement, the calibration runner also tracks the **numeric score delta** — how far the judge's 0–1 score is from the midpoint of the expected `[ExpectedScoreMin, ExpectedScoreMax]` band. A judge can agree on verdict ("pass") but consistently score 0.95 where humans scored 0.75; that drift matters when consumers set their own thresholds.

### 7.4 The Article 5 lesson

The criteria-resolution logic in `CalibrationRunner` has two special cases — synthetic-ID fallback and Pillar 1 strict-refusal — that exist *because actual calibration runs revealed actual problems*. This is what mature calibration tooling looks like: not just "compute the metric", but "encode the lessons from past calibration failures so the next run doesn't repeat them."

Anyone reading the calibration code should expect more such cases to accumulate over time. That's the system working as intended.

---

## 8. Toward better-calibrated benchmarks — techniques and the road forward

### 8.1 Correcting the common misconception

A frequent question: *"isn't the golden dataset just a set of correct PASS examples?"* No, and this is the most consequential methodological point in the whole calibration story.

A calibration set composed entirely of "should pass" examples cannot calibrate anything:

- **Accuracy becomes meaningless.** A judge that returns PASS to everything scores 100% accuracy on a PASS-only dataset.
- **Cohen's kappa is mathematically undefined.** `p_e ≈ 1.0`, division by zero. `CalibrationMetrics.CohensKappa()` returns `NaN` for this case precisely to surface the problem (see §7.2). The earlier code returned `1.0`, which was misleading.
- **Score discrimination is unmeasured.** You learn nothing about whether the judge can distinguish a 0.85 response from a 0.45 response.

Golden datasets should always contain entries the judge is expected to *fail* and entries it should *partially pass* — that's where the calibration signal lives.

### 8.2 The four-stratum calibration set

A serious calibration set is **stratified** across at least four kinds of entry. The `CalibrationEntry` shape already supports this — `ExpectedScoreMin` and `ExpectedScoreMax` define a band, not a point.

| Stratum | Expected verdict | Expected score band | What it tests |
| --- | --- | --- | --- |
| **Clear pass** | `"pass"` | 0.85 – 1.0 | Judge correctly identifies competent compliant responses |
| **Borderline pass** | `"pass"` | 0.65 – 0.85 | Judge holds the line at the 0.70 pass threshold |
| **Borderline fail** | `"warn"` or `"fail"` | 0.40 – 0.65 | Judge correctly flags ambiguous responses |
| **Clear fail** | `"fail"` | 0.0 – 0.30 | Judge correctly identifies non-compliant responses |

A pillar-level calibration set should have roughly equal representation in each stratum — typically 25/25/25/25, or skewed toward the borderline cases (15/35/35/15) where discrimination matters most. A judge passes calibration on an entry when its score falls inside the band **and** its verdict matches the expected verdict.

### 8.3 Adversarial entries — where judge blind spots live

Beyond the four base strata, a mature calibration set includes deliberate adversarial entries:

- **Confidently wrong responses** — the agent answers definitively but the answer is incorrect. Judges with weak "is this hallucinated" instincts pass these.
- **Evasive responses** — the agent paraphrases the regulation without committing to direction. The Art 5 strict-refusal logic in `CalibrationRunner` (see §6.4) exists *because the judge was passing these*.
- **Polite refusals for the wrong reason** — the agent refuses but cites the wrong basis (e.g., "this is unsafe" instead of "this is unlawful"). Tests whether the judge rewards correct reasoning.
- **Format-perfect failures** — well-structured response that meets surface checks but misses the regulatory substance.
- **Surface-imperfect successes** — poorly formatted but substantively correct response. Tests whether the judge is over-weighting form.

Adversarial entries should be at least **one-third** of the calibration set. They are where the calibration signal is most informative.

### 8.4 Beyond accuracy and Cohen's kappa — techniques worth adopting

`CalibrationMetrics` today computes accuracy and Cohen's kappa. These are necessary but not sufficient. Additional metrics that are standard practice in ML evaluation but not currently in the framework:

| Metric | What it adds | Implementation cost |
| --- | --- | --- |
| **Per-class confusion matrix** | Reveals systematic errors — does the judge confuse `warn ↔ fail` more than `fail ↔ pass`? | Trivial — already have (expected, actual) pairs |
| **Per-class precision / recall / F1** | Distinguishes a conservative judge (high precision, low recall on FAIL) from an aggressive one. Critical for compliance where false-passes cost more than false-fails. | Trivial |
| **Brier score** | Mean squared error of numeric score vs expected midpoint — captures discrimination quality beyond verdict matching. | Easy — score deltas already tracked |
| **Expected Calibration Error (ECE)** | Tests whether judge's numeric confidence matches actual accuracy at that confidence level. A judge that scores 0.9 should be right ~90% of the time at that band. | Moderate — requires binning |
| **ROC curve + AUC** | For pass/fail, shows trade-off between true-positive rate and false-positive rate as the threshold slides. Lets you pick a threshold tuned to your cost asymmetry. | Easy — already have scores |
| **Inter-rater agreement (Fleiss' κ)** | Sets the *upper bound* on judge calibration. If two compliance experts agree only at κ = 0.78, no judge can honestly exceed κ = 0.78. Establishes the ceiling. | Moderate — requires multi-rater dataset |
| **Test-retest reliability** | Same response, same judge, weeks apart — reveals judge non-determinism even at temperature 0. | Easy — schedule re-runs |

The three to ship first if prioritising AgentEval's v1.2: **per-class confusion matrix**, **Brier score**, and **inter-rater agreement on the human labellers**. The first two are nearly free given the existing (expected, actual) pairs; the third forces the discipline of multi-rater labelling, which is the single biggest quality lever in the system.

### 8.5 The holdout-set discipline

A subtle failure mode: the team tunes the judge prompt until accuracy on the calibration set is high. The judge is then implicitly **overfit** to the specific labels in that calibration set, and ships with optimistically inflated calibration metrics.

The fix is standard ML hygiene: split the labelled data into a **calibration set** (visible during prompt tuning) and a **holdout set** (sealed, used only for the final shipping gate). The shipping metric is accuracy on the holdout set, never on the calibration set. Typical split: 70/30 or 80/20.

AgentEval today does not enforce this split — the calibration JSONL files are visible during judge-prompt development. Adding a holdout split is a content change (separate JSONL files), not a framework change. Worth doing.

### 8.6 Why "perfect calibration" is an asymptote, not a destination

Three structural reasons perfect calibration cannot be achieved, only approached:

1. **The ceiling is set by humans, not by judges.** If two trained compliance labellers agree on the same dataset at only κ = 0.78, no judge can honestly exceed κ = 0.78 — the judge cannot be more reliable than the ground truth it's measured against. Inter-rater agreement on the *human* side is the upper bound on any judge calibration on the same data.
2. **Concept drift is real.** Regulations are reinterpreted, case law accumulates, the agent population shifts. A perfectly calibrated benchmark on 2026-Q1 data is observably less calibrated on 2026-Q4 data. Calibration is a process, not a state.
3. **Judge models change.** When GPT-4o → GPT-5 → next-gen happens, calibration measured against the prior model becomes a historical curiosity. The pillar must be re-calibrated against each new judge model that ships.

The realistic target is **bounded uncertainty with documented coverage**: a pillar ships at κ ≥ 0.61 on a stratified holdout set of 30+ entries, with documented inter-rater agreement on the labels, re-calibrated quarterly against the current judge model, with drift alarms when production-traffic distribution diverges from the calibration distribution.

That is what "well-calibrated" means in practice. *"Perfectly calibrated"* is what people who haven't tried to calibrate a real system tell you to aim for.

### 8.7 Concrete v1.2+ recommendations for AgentEval

Ranked by impact-to-effort ratio:

1. **Adopt the four-stratum balance requirement** in calibration documentation, and re-ship golden datasets that hit the balance for at least the audit-grade preset pillars. Content change only.
2. **Add per-class confusion matrix + Brier score** to `CalibrationMetrics`. Both are near-free given the existing (expected, actual) pairs and score deltas.
3. **Introduce a calibration / holdout split** for at least the GDPR and EU AI Act audit-grade golden datasets. Content change; ~70/30 split is industry standard.
4. **Run a multi-rater pilot** on one pillar (EU AI Act Pillar 4 risk-tier-behaviour is a good candidate) to measure human inter-rater agreement and establish the realistic calibration ceiling.
5. **Document the asymptote** — replace any internal or external phrasing of "perfectly calibrated" with "calibrated to documented thresholds on stratified holdout sets." This is a positioning improvement that costs nothing and pre-empts the question.
6. **Schedule quarterly drift sweeps** — automatic re-run of calibration against the same golden set, alerting on metric drift > 0.05 from baseline. Operational discipline, not framework code.
7. **Active learning loop** — when production agents produce responses that the judge scores near 0.50 (high uncertainty), surface a sample for human labelling and feed them back into the calibration set. Closes the loop between deployment and calibration. Largest effort of the seven; largest long-term payoff.

None of these require fundamental framework changes. The first three are content/data work; the next three are operational discipline; the seventh is the only one needing meaningful tooling. The framework already supports all of it.

---

## 9. From CLI to evidence — the orchestration

End-to-end for `agenteval bench eu-ai-act --preset standard --subject MyAgent`:

1. **CLI** parses the command, resolves the `eu-ai-act` family from `BenchmarkFamilyRegistry`, the `standard` preset from `EuAiActBenchmark.Standard(judge)`.
2. **Preset factory** assembles the `CompositeEval` tree (6 pillars → ~12 articles → ~50 scenarios → ~50 `AtomicLlmEval`s).
3. **`EuAiActBenchmarkRunner.RunAsync`** drives the composite against the subject, with `IOutputStore` capturing the run manifest.
4. **`CompositeEval.EvaluateAsync`** runs all leaves in parallel via `Task.WhenAll`, aggregates up the tree using each composite's chosen strategy.
5. **`EvalResultPersistence.ToScenarioResult`** writes each leaf result to the output store; the runner builds a `RunSummary` from the composite's verdict and roll-up statistics.
6. **Reporters** (`EuAiActComplianceReporter`, `CriticalFindingExtractor`, `CrossRegulationLinker`, `RecommendationExtractor`, PDF / Markdown renderers) emit the evidence pack.
7. **`.agenteval/compliance/eu-ai-act/<subject>/<timestamp>/`** receives JSON + Markdown + PDF + JUnit XML + SARIF, all SHA-256 audit-chain validated.

The same `RunAsync` shape is used by `GdprBenchmarkRunner` and `AgenticBenchmarkRunner`. The unified evidence-pack shape means downstream tooling (Mission Control, custom dashboards, audit consumers) handles all three families with one code path.

---

## 10. Building your own benchmark

If you wanted to add an evaluator family for, say, **ISO 42001** or **a domain-specific behavioural standard**, the steps are:

1. **Author scenarios in YAML.** One file per control or article; each scenario has id, prompt, expected behaviour, evaluation criteria, weight, severity. Use the GDPR or EU AI Act samples as templates.
2. **Author a judge system prompt.** A markdown rubric that tells the judge what to look for, how to cite the standard, how to score. The standard pattern: a `you are` paragraph, a `your rubric` section, a `scoring` section with a JSON output schema.
3. **Group scenarios into article/control composites** via `ArticleCompositeBuilder`. Choose aggregation per article (WeightedSum / Min / CapByWorst).
4. **Group articles into pillar composites** via `PillarCompositeBuilder` with deliberate weights.
5. **Write a preset factory** that returns the top-level `CompositeEval`. Ship at least `smoke`, `standard`, and `audit` presets — the cost/coverage spread matters.
6. **Register the family** in `BenchmarkFamilyRegistry` so the CLI's `bench --list` discovers it.
7. **Hand-label a golden dataset** for each pillar. Minimum 30 entries per pillar, ideally 100+, with adversarial coverage.
8. **Run `CalibrationRunner`** against the golden dataset; iterate on the judge prompt and pillar thresholds until accuracy ≥ strict gate and κ ≥ 0.61.
9. **Document the calibration result** with the standard caveat: "PASS at the strict gate means the calibrated judge agrees with hand-labeled ground truth above the documented thresholds on the calibration set. It does not constitute legal conformance attestation."
10. **Ship** — the new family is now a first-class citizen of the CLI, evidence pipeline, and Mission Control viewer.

Steps 1, 2, 7, 8 are the actual work. Steps 3, 4, 5, 6 are mechanical glue. Steps 9, 10 are publishing discipline.

---

## 11. Where each piece lives — file map

For navigation. All paths relative to repo root.

| Component | File |
| --- | --- |
| `IEval` interface | `src/AgentEval.Abstractions/Evals/IEval.cs` |
| `IEvaluator` (judge contract) | `src/AgentEval.Abstractions/IEvaluator.cs` |
| `AtomicLlmEval` | `src/AgentEval.Core/Evals/AtomicLlmEval.cs` |
| `AtomicCodeEval` | `src/AgentEval.Core/Evals/AtomicCodeEval.cs` |
| `CompositeEval` | `src/AgentEval.Core/Evals/CompositeEval.cs` |
| `MultiJudgeWrapper` | `src/AgentEval.Core/Evals/MultiJudgeWrapper.cs` |
| Aggregation strategies | `src/AgentEval.Core/Evals/Aggregations/*.cs` |
| GDPR benchmark factory | `src/AgentEval.Compliance.Gdpr/GdprBenchmark.cs` |
| GDPR articles registry | `src/AgentEval.Compliance.Gdpr/Articles/GdprArticlesRegistry.cs` |
| GDPR pillar builder | `src/AgentEval.Compliance.Gdpr/Pillars/PillarCompositeBuilder.cs` |
| GDPR scenarios (YAML) | `samples/AgentEval.GdprBenchmark/Articles/Yaml/` |
| GDPR judge prompt | `samples/AgentEval.GdprBenchmark/Resources/Prompts/gdpr-judge-system.v1.md` |
| EU AI Act benchmark factory | `src/AgentEval.Compliance.EuAiAct/EuAiActBenchmark.cs` |
| EU AI Act articles registry | `src/AgentEval.Compliance.EuAiAct/Articles/EuAiActArticlesRegistry.cs` |
| EU AI Act pillar builders | `src/AgentEval.Compliance.EuAiAct/Pillars/Pillar*.cs` |
| EU AI Act scenarios (YAML) | `samples/AgentEval.EuAiActBenchmark/Articles/Yaml/` |
| EU AI Act judge prompt | `samples/AgentEval.EuAiActBenchmark/Resources/Prompts/eu-ai-act-judge-system.v1.md` |
| Scenario → atomic translator | `src/AgentEval.Compliance.EuAiAct/Articles/Building/ScenarioToAtomicEval.cs` |
| Article composite builder | `src/AgentEval.Compliance.EuAiAct/Articles/Building/ArticleCompositeBuilder.cs` |
| Benchmark runner | `src/AgentEval.Compliance.EuAiAct/Articles/EuAiActBenchmarkRunner.cs` |
| Calibration runner | `src/AgentEval.Compliance.EuAiAct/Calibration/CalibrationRunner.cs` |
| Calibration metrics (accuracy + κ) | `src/AgentEval.Compliance.EuAiAct/Calibration/CalibrationMetrics.cs` |
| Calibration dataset shape | `src/AgentEval.Compliance.EuAiAct/Calibration/CalibrationDataset.cs` |
| Golden JSONL datasets | Embedded resources in `AgentEval.Compliance.EuAiAct` (matched by `.Calibration.Golden.*.jsonl`) |
| Sample calibration usage | `samples/AgentEval.Samples/MetricsAndQuality/03_JudgeCalibration.cs` |
| Agentic benchmark factory | `src/AgentEval.Evals.Agentic/AgenticBenchmark.cs` |
| Agentic evaluators (~60) | `src/AgentEval.Evals.Agentic/<category>/*.cs` |
| `BenchmarkFamilyRegistry` | `src/AgentEval.Core/Benchmarks/BenchmarkFamilyRegistry.cs` |

---

## 12. The synergy in one sentence

> **A handful of small, well-bounded primitives** (`IEval`, `AtomicLlmEval`, `CompositeEval`, five aggregation strategies, a single `CalibrationRunner`) **plus disciplined content** (YAML scenarios per article, a judge rubric per family, hand-labelled golden datasets per pillar) **plus a generic runner** (`<Family>BenchmarkRunner`) **plus a single evidence shape** (`.agenteval/` audit-chain-validated pack) **plus a single CLI entrypoint** (`agenteval bench <family> --preset <name>`) — that is the entire architecture. Every benchmark family (GDPR, EU AI Act, agentic, memory, OWASP, MITRE, performance, longmemeval) is the same five-primitive composition with different content. New families do not require new framework code; they require new content + a 20-line preset factory. Calibration is the load-bearing piece that turns "an LLM said PASS" into "a judge calibrated against hand-labelled ground truth at κ ≥ 0.61 said PASS."

This is the property that lets one team ship eight benchmark families with consistent evidence semantics — and it is the property that scales to whatever the next regulation, attack taxonomy, or quality framework turns out to be.

---

## 13. See also

- [`architecture.md`](architecture.md) — overall framework layered design
- [`composite-evals.md`](composite-evals.md) — composite mechanics and aggregation strategies in depth
- [`benchmarks.md`](benchmarks.md) — benchmark presets and CLI surface
- [`benchmarks/gdpr/how-it-works.md`](benchmarks/gdpr/how-it-works.md) — GDPR pillars and articles
- [`benchmarks/eu-ai-act/how-it-works.md`](benchmarks/eu-ai-act/how-it-works.md) — EU AI Act pillars and Annex III
- [`benchmarks/agentic/how-it-works.md`](benchmarks/agentic/how-it-works.md) — agentic categories
- [`benchmarks/agentic/evaluator-cards.md`](benchmarks/agentic/evaluator-cards.md) — per-evaluator cards
- [`llm-as-judge.md`](llm-as-judge.md) — judge contract and prompt patterns
- [`evaluation-guide.md`](evaluation-guide.md) — practitioner's evaluation guide
- [`extensibility.md`](extensibility.md) — extension points
- [`adr/008-calibrated-judge-multi-model.md`](adr/008-calibrated-judge-multi-model.md) — calibrated-judge ADR
- [`adr/017-unified-benchmarks-namespace.md`](adr/017-unified-benchmarks-namespace.md) — namespace unification ADR
