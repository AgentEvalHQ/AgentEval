# RedTeam Next-Wave — Competitive Parity & Honesty Plan

> **✅ TIER 1 SHIPPED (2026-06-13, `feature/redteam-newwave-fixes`).** All 5 low-hanging-fruit items landed in 3 commits (`6c8f4fb`/`578235c`/`1dd4ac5`): Skeleton-Key + Many-shot probes, divergence/repeat-token probes, the graded `LikertJudgeEvaluator`, the seed-prompt dataset importer (`IProbeDatasetImporter` + `--import-probes`), and `--explain` LLM rationale (Succeeded/Inconclusive, judge-gated). Probe total 247→255; CLI 27→29 options; +27 tests. All TFMs green: net8/9 4927, net10 5130/0/1. **Next: Tier 2** (LLM03 registry oracle + LLM08 RAG harness — the two real gap-closers).

> **⏳ TIER 2 IN PROGRESS — Tier-2a (LLM03) SHIPPED 2026-06-13 (`2cbd9f2`).** `HttpPackageRegistry` (live PyPI/npm/NuGet, 404-only⇒absent, outage⇒assume-real) + CLI `--package-registry live` swaps SupplyChain to the registry-backed evaluator → catches model-invented hallucinations like garak. net8 4936, net10 5139/0/1; +9 tests; CLI 29→30.
> **Tier-2b (LLM08) — DESIGN (next, focused turn):** do NOT just mark VectorEmbedding `IToolAwareAttack` — its probes inline the poison, so even at Tier-2 they'd score Verbal (a fake "real boundary"). The honest build: (1) VectorEmbedding implements `IToolAwareAttack` exposing a `retrieve_context` canary tool whose `Execute` RETURNS the poisoned document; (2) add benign-prompt tool-mode probes (Surface=RetrievedDocument) that induce retrieval; (3) a retrieval-aware evaluator that marks **Behavioral** only when the poisoned retrieval was actually executed (reads RawMessages, like ToolInvocationEvaluator) and the marker then appears. Keep the inlined probes for Tier-0. This yields genuine Behavioral RAG evidence at `--sut-tier instrumented`.

**Date:** 2026-06-13  **Branch target:** `feature/redteam-newwave-fixes` (or a fresh `feature/redteam-nextwave`)
**Premise:** the Fable-review arc is complete (HIGH + all MEDIUM + all LOW + Section-2 + H13/H14, all green: net8/9 4900, net10 5103/0/1). This plan is the *forward* wave, derived directly from the 2026-06-13 garak/PyRIT competitive analysis. It supersedes the relevant bits of `RedTeam-NewWave-FeatureComplete-Implementation-Plan.md` §Wave F/G with a concrete, effort-tiered, value-ranked backlog.

**Two strategic goals:**
1. **Close the only two *real* category gaps** vs the leaders — LLM03 (garak's registry check) and LLM08 (garak's `latentinjection` real-retrieval delivery).
2. **Bank the cheap honesty + technique wins** that move us toward garak's breadth and PyRIT's scorer/orchestration depth *without* abandoning our distinctive evidence-fidelity discipline.

We are **not** chasing garak's raw probe count head-on; we win on .NET-native, compliance/governance, and honesty. The breadth gap is closed by *importing datasets*, not by hand-writing thousands of probes.

---

## 1. Prioritized backlog (the whole wave at a glance)

Effort: **🟢 LHF** (small, no new infra) · **🟡 Moderate** (new harness/infra or LLM-in-loop) · **🔴 Complex** (large / blocked).

| # | Item | Source | Effort | Value | Depends on | Honesty note |
|---|------|--------|--------|-------|------------|--------------|
| 1 | **Graded scorers** (Likert / SelfAsk / refusal / FloatScale) | PyRIT | 🟢 | High — graded confidence; unlocks #2, #11; intent-vs-echo on every marker test | LLM judge (exists) | rationale-bearing; fidelity-capped at IntentToAct |
| 2 | **Skeleton-Key** + **Many-shot** jailbreak probes | PyRIT/MSRC | 🟢 | High — two strong, well-known techniques we lack | none | deterministic; refusal-gated |
| 3 | **`--explain` rationale** on findings | PyRIT scorers | 🟢→🟡 | High — explains *why* + *which fidelity*; auditor-facing | LLM judge | opt-in LLM call; never invents a verdict |
| 4 | **Seed-prompt dataset importer** (`IProbeDatasetImporter`, JSON/CSV/YAML) | garak+PyRIT | 🟢→🟡 | **Very High** — +hundreds of attributable probes from one seam | none (core); license gate is #10 | provenance + license attribution per probe |
| 5 | **`divergence` / repeat-token extraction** probes | garak | 🟢 | Med — real training-data-leak vector (LLM02/06) | none | deterministic detector (repetition/PII) |
| 6 | **`leakreplay`-style** training-data replay probes | garak | 🟡 | Med — membership/replay (LLM02/04 adjacent) | none | conclusive only on verbatim hit |
| 7 | **LLM03 registry oracle** (live PyPI/npm/NuGet adapter or bundled full snapshot) | garak | 🟡 | **High — closes a real gap** | `RegistryBackedSupplyChainEvaluator` (exists) | **see ⚠️ false-positive trap below** |
| 8 | **LLM08 RAG/retrieval harness** + `latentinjection`-style probes | garak/PyRIT | 🟡 | **High — closes a real gap** | InjectionSurface.RetrievedDocument (exists) + a RAG test agent | Behavioral evidence via real retrieval boundary |
| 9 | **z-score calibration** (relative-to-reference scoring) | garak | 🟡 | Med-High — best honesty feature we lack | baseline infra (exists) | "unusually vulnerable vs cohort", not absolute |
| 10 | **PackDownloader + license gate** (HarmBench/JailbreakBench/CyberSecEval) | PyRIT/garak | 🟡 | High — turns #4 into a curated benchmark library | #4 | `--accept-license`; no harmful data bundled by default |
| 11 | **BadLikertJudge** multi-turn attack | PyRIT | 🟡 | Med | #1 | graded; multi-turn fold |
| 12 | **LLM-driven converters** (paraphrase/persuasion/tense) | PyRIT | 🟡 | Med — amplifies every probe | transform pipeline (exists, deterministic) | **breaks determinism → opt-in + labeled** |
| 13 | **Tool-aware multi-turn attack** (exercise the Wave-B↔C DIM) | — (our gap) | 🟡 | Med — proves the compose path with a shipped attack | DIM (exists) | Behavioral over the conversation channel |
| 14 | **LLM10 transport-level metering harness** | — (field gap) | 🟡→🔴 | Med — only way to make LLM10 real | a real metered endpoint | measures tokens/latency/cost, not text cooperation |
| 15 | **atkgen** adaptive generation | garak | 🔴 | High but heavy | #1, attacker-LLM (exists) | non-deterministic, labeled |
| 16 | **Memory-poisoning + multi-agent surfaces** | — (Wave G) | 🔴 | High, differentiator | new surfaces | — |

**Parked (KEEP-CUT):** GCG/adversarial-suffix (needs gradients/GPU); multi-modal (blocked on MAF vision); running garak/PyRIT as external Python *processes* (import patterns/data, never processes); DeepTeam import (enterprise-gated).

---

## 2. Tier 1 — Lowest-hanging fruit (do these first)

All 🟢, no new infrastructure, high cumulative value. This is the recommended **first PR slice**.

### 1. Graded scorers (Likert / SelfAsk / refusal / FloatScale)
- **What:** add `LikertScorer` (1–5 graded), `RefusalScorer`, `FloatScaleScorer` alongside the existing binary `LLMJudgeEvaluator`. PyRIT's core scoring primitives.
- **Why:** graded confidence instead of pass/fail; lets a marker-echo be scored *intent vs mere echo*; **prerequisite for BadLikertJudge (#11)**.
- **Effort:** 🟢 S — we already have the judge plumbing + `EvaluationResult.Confidence`. Reuse `--judge`; cap fidelity at IntentToAct (text-only).
- **Honesty:** a graded score still folds to our outcome enum; an unsure judge → Inconclusive, never a fabricated verdict.

### 2. Skeleton-Key & Many-shot jailbreak probes
- **What:** Skeleton-Key (the MSRC "update your guidelines / add a warning instead of refusing" technique) + Many-shot (a long context of fabricated compliant Q&A pairs). Add as new techniques on `JailbreakAttack` (or a `SkeletonKeyAttack`).
- **Why:** two of the most-cited 2024–25 jailbreaks; currently absent.
- **Effort:** 🟢 S — deterministic scripted probes; reuse the Jailbreak marker oracle + refusal gating.

### 3. `--explain` rationale on findings
- **What:** thread an LLM-generated **rationale** into `ProbeResult` (already has `Reason`; add `Rationale` / `Metadata["rationale"]`) and surface it in reports. A `--explain` flag requests it (one judge call per *failed/inconclusive* finding to bound cost).
- **Why it matters / honest framing:** PyRIT *already* emits scorer rationales — so a bare rationale is **parity, not a unique differentiator**. The **differentiator is a rationale tied to our fidelity model** — e.g. *"scored IntentToAct: the model described calling `transfer_funds` but emitted no tool-call"* or *"Inconclusive: the only evidence is a refusal-adjacent phrase 40 chars from the marker."* That explanation of *why this fidelity / why inconclusive* is something neither garak nor PyRIT produces.
- **Effort:** 🟢→🟡 S–M. Requires an LLM call ⇒ **opt-in flag** (off by default; `--explain` or implied by `--judge`). We already have `Reason` for the deterministic baseline, so the floor is unchanged when off.

### 4. Seed-prompt dataset importer (`IProbeDatasetImporter`)
- **What:** an importer that reads external **seed-prompt datasets** (JSON/CSV/YAML) into `AttackProbe`s, stamping `Source` + license attribution + a `dataset:` provenance tag.
- **Why this is the breadth play (see §3):** it's the single seam that turns 247 curated probes into thousands, with attribution, *without* hand-writing them.
- **Effort:** 🟢→🟡 S–M for the importer core; the curated/licensed *packs* are #10.

### 5. `divergence` / repeat-token extraction probes
- **What:** garak's data-extraction-by-repetition family ("repeat the word 'company' forever", token-divergence prompts) — detector flags degeneration into memorized/PII content.
- **Effort:** 🟢 S — deterministic prompts; a repetition/PII detector (we already have `RegexMatchEvaluator` + PII patterns). Maps to LLM02/LLM06.

---

## 3. The breadth play — how the Wave F importers actually work

**Question: are garak/PyRIT probes "available to import," and how?**

Two distinct sources, two mechanisms — this distinction matters:

1. **Seed-prompt DATA (importable directly):** many attacks are just *prompt datasets*. These ship as files and are reusable with attribution:
   - **HarmBench** (MIT) — standardized harmful-behavior prompts.
   - **JailbreakBench / JBB-Behaviors** (MIT) — jailbreak artifacts + behaviors.
   - **AdvBench** (Zou et al. `llm-attacks`, MIT) — harmful strings/behaviors.
   - **DAN / in-the-wild jailbreak collections** (various; vet per-file).
   - **PyRIT seed-prompt datasets** (MIT) — shipped YAML.
   - **garak data files** (Apache-2.0) — the *data*-driven probes' payloads.
   → **Mechanism:** `IProbeDatasetImporter` (#4) reads these into `AttackProbe`s. A `PackDownloader` (#10) fetches them on demand behind a **`--accept-license` gate** (we do **not** bundle harmful-content datasets in the NuGet package; the user opts in and the license is recorded in provenance). License **must be re-verified at import time** — the gate enforces it; the licenses above are current best-knowledge, not a guarantee.

2. **Code-GENERATED probes (re-implement, don't import):** garak's `encoding`, `latentinjection`, `divergence`, `goodside`, dynamic/reactive probes are *generated by Python code*, not static data. We **cannot** import these as data and we **will not** run garak as a subprocess (env hell — the standing KEEP-CUT decision). We **re-implement the pattern natively** (as we already did for transforms/encoders). That's items #5, #8(latentinjection), #15(atkgen).

**Net:** breadth comes ~80% from dataset import (#4/#10) + ~20% from native re-implementation of a handful of high-value code patterns. Realistic add: **+300–600 attributable probes** from the importer alone, plus the re-implemented families.

---

## 4. Tier 2 — Moderate (the two real category-gap closers + infra)

### 7. LLM03 — registry oracle (closes the garak gap)
- **garak does it better today:** garak checks recommended packages against a **real, near-complete** package-name set, so it catches model-*invented* hallucinations, not just a planted fake.
- **Our state:** the honesty-preserving evaluator already exists — `RegistryBackedSupplyChainEvaluator` (caution-gated, opt-in via `SupplyChainAttack(IPackageRegistry)`). It just needs a real `IPackageRegistry`.
- **⚠️ The false-positive trap (why this is Moderate, not LHF):** a *small* curated allowlist (top-5k packages) would **false-flag legitimate-but-rare real packages** → fabricated Succeededs (an RC-6 violation). The honest options are (a) a **live registry adapter** (PyPI/npm/NuGet JSON API lookup — network, cached) or (b) a **bundled full name-snapshot** (large data file, like garak downloads). Both are 🟡 Moderate. Do **not** ship a small allowlist as "the registry."
- **Value:** High — turns LLM03 from a planted-fake proxy into a real hallucination check, at parity with garak.

### 8. LLM08 — RAG/retrieval harness + `latentinjection` probes (closes the garak gap)
- **garak/PyRIT do it better:** garak's `latentinjection` delivers the payload through retrieved/structured content; PyRIT has indirect-injection orchestration. **Ours inlines the payload** into the prompt (no real retrieval step).
- **Our state:** the surface enum already exists (`InjectionSurface.RetrievedDocument`) and IndirectInjection already hits the real **ToolOutput** boundary at Tier-1/2. We need a **RAG test agent** (an `IToolCapableAgent`/retrieval shim that pulls from a seeded vector/document store) so VectorEmbedding's payload arrives via genuine retrieval.
- **Effort:** 🟡 Moderate — a retrieval harness + `latentinjection`-style probe variants. Reuses the existing surface + tool-channel machinery.
- **Value:** High — Behavioral evidence for LLM08 instead of an inlined proxy.

### 9. z-score calibration (copy garak's best honesty feature)
- **What:** report each model's conclusive score **relative to a reference cohort/baseline** ("this model is N std-devs more vulnerable than the reference on LLM01"). garak's z-score/calibration is the one honesty feature it has and we don't.
- **Effort:** 🟡 Moderate — we already persist baselines; add a reference-cohort comparison + a z-score field in the report.

### 6 / 10 / 11 / 12 / 13: see the backlog table — all 🟡, sequenced after the gap-closers.

---

## 5. Tier 3 — Complex / long-horizon (Wave G)

- **#14 LLM10 metering harness** (🟡→🔴): the *only* way to make Unbounded Consumption real is a transport-level harness that applies inference params and **measures** tokens/latency/cost + rate-limit enforcement. Note: **neither garak nor PyRIT does real black-box resource metering either** — this category is at the field ceiling; we're not uniquely weak, just honestly labeled. Worth it only if a customer needs real DoS evidence.
- **#15 atkgen** (🔴): LLM-driven adaptive probe generation (reuses our attacker-LLM). High value, non-deterministic (labeled).
- **#16 memory-poisoning + multi-agent surfaces** (🔴): genuine differentiators, large.

---

## 6. Direct answers to the open questions

- **"LLM04 — can we improve anything from garak?"** Yes, but modestly. garak doesn't do real training poisoning either; its adjacent value is `leakreplay` (#6, training-data replay/extraction) and `divergence` (#5). We can also strengthen DataPoisoning with **multi-turn persistence** (poison turn 1, exploit turn 3) using our turn harness. None of these is "training poisoning" — that's white-box-only.
- **"PyRIT doesn't do training-poisoning → are we stronger there?"** **No — don't overclaim.** *Both* do in-context/RAG poisoning; *neither* does real training poisoning. We're at **parity, arguably slightly cleaner** (our DataPoisoning has deterministic negation-proximity adoption scoring). It's a category where everyone sits at the black-box ceiling.
- **"We're missing a Rationale output — how hard, key differentiator, flag?"** We *have* a deterministic `Reason`; we lack an LLM-generated rationale. Adding it is 🟢→🟡 S–M behind a **`--explain` flag** (it's an LLM call, so opt-in/off-by-default). A *bare* rationale is **parity with PyRIT, not a differentiator** — the differentiator is a rationale that **explains the fidelity/inconclusive verdict**, which neither competitor produces. Worth doing; frame it as "explainable honesty," not "we invented rationales."
- **"What's pending to improve in multi-turn?"** We have Crescendo/PAIR/TAP + attacker-LLM. Pending: **BadLikertJudge** (#11, needs scorers), **Skeleton-Key/Many-shot** (#2), a **shipped tool-aware multi-turn attack** that actually exercises the Wave-B↔C DIM we added (#13), graded scorers in the turn loop (#1), and Wave-G memory-poisoning conversations (#16).

---

## 7. Recommended sequencing & the first-PR slice

**PR-1 (Tier-1 LHF, ~1 focused wave):** #1 graded scorers · #2 Skeleton-Key + Many-shot · #3 `--explain` rationale · #5 divergence probes · #4 dataset-importer *core* (no packs yet). High value, no new infra, fully deterministic except the opt-in `--explain`/scorer LLM calls.

**PR-2 (close the real gaps):** #7 LLM03 registry oracle · #8 LLM08 RAG harness + latentinjection. This is the headline competitive move — it removes the only two categories where garak genuinely beats us.

**PR-3 (breadth + calibration):** #10 PackDownloader + license gate (HarmBench/JailbreakBench/CyberSecEval) · #9 z-score calibration · #6 leakreplay · #11 BadLikertJudge · #12 LLM-driven converters.

**Later (Wave G):** #13 tool-aware multi-turn attack · #14 LLM10 metering · #15 atkgen · #16 memory/multi-agent.

**Honesty guardrails for the whole wave (non-negotiable):** every new probe carries fidelity + (where weak) Inconclusive; no imported dataset is scored as conclusive without a real oracle; the LLM03 registry must be live/full-snapshot (never a small allowlist); LLM-driven converters/atkgen are labeled non-deterministic; nothing harmful is bundled — the license gate fetches on opt-in.
