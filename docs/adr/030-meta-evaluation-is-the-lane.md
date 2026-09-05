# ADR-030: Meta-evaluation is the lane. Contract unification is not.

- **Status:** **Accepted (2026-09-05).** Ratification is what funds §8: Slices 1 and 2 are now
  funded. Slice 0 was **already executed** before ratification (see the ratification note) — the gate
  it was meant to sit behind did not hold, and saying so is part of accepting the document. Status
  `Proposed` was a gate, not a placeholder (the ADR-026 precedent); it has now been cleared, and
  everything in "Deferred until a second team asks" (§8) remains unfunded.
- **How it got here, which is the useful part:** a four-lane design was produced (contract
  unification, meta-evaluation, harness bridge, assertions) and put to an adversarial review. The
  review returned **RETHINK the unification half, SHIP a much smaller meta-evaluation slice**, and it
  was right on both counts. Two of the design's load-bearing mechanisms — the base class that made
  the chance-floor defect "unrepresentable", and the guard that made "inapplicable cannot be a pass"
  structural — **do not work as specified, and one of them does not compile.** This ADR is the
  design after those rulings were applied, not the design that was reviewed. §7 records what was
  refuted, including by this document's own earlier draft.
- **Dates:** authored **2026-09-04** (`a396c5b4`); **ratified 2026-09-05**, with §4.2's guard
  corrected and §3.3's input claim corrected on the way in — see §0.0. Supersedes
  `ADR-030-DRAFT_NonLlm_MetaEvaluation.md` (deleted on write).
- **Decision in one line:** **Adopt meta-evaluation — chance floors, exact tests, paired
  significance, unit-of-analysis, applicability — as AgentEval's differentiating concern, defined
  over a framework-neutral observation tuple rather than over `EvalResult`; keep `IEval` as the one
  evaluation contract by adaptation and not by migration, and say plainly that the reason is
  migration cost rather than architectural superiority; and reject a deterministic assertion
  catalogue permanently, by name.**
- **Relates to:** [ADR-022](022-grading-by-decomposition-composite-sub-evaluators.md)
  (the composite tree this builds on), [ADR-028](028-typedmemeval-acceptance-on-discrimination.md)
  (accept on discrimination; chance floors derive per arm), [ADR-025](025-gatekeeper-runtime-fail-closed-enforcement.md)
  (fail closed rather than silently green-light).

---

## 0.0 RATIFICATION NOTE — 2026-09-05

**Ratified by:** the repository owner (`joslat`), on branch `joslat/digitec-galaxus` at `b57496f2`.
**Reviewed against the tree, not against the brief.** Four checks were re-run this session; three
confirmed the decision, one corrected a supporting sentence, and one changed what ratification means.

| Check | Result |
|---|---|
| Does the flagship harness reach the good contract, from a consumer's side? | **Confirmed, and harder than §2.2 states.** `samples/Galaxus.RecommendationAgent.Evals` makes **0** references to `IEval` and **59** to `MAFEvaluationHarness`. A consumer with zero `IEval` references cannot be served by anything defined over `EvalResult`. This is the evidence that makes §4.1 (the neutral `Observation` tuple, BCL-only) load-bearing rather than stylistic: it is the only part of this design that a zero-`IEval` consumer can adopt without migrating first. |
| Is `IEval`'s input gap as large as §3.3 claims? | **No — corrected in §3.3.** `EvalInput` already carries `ToolCalls`, `ToolDefinitions` and `ExpectedActions`. The real gap against `ToolCallRecord` is **three fields** (`WasExecuted`, `ApprovalState`, `Order`) plus one `ToolUsageReport → EvalInput` projection. The bridge is cheaper than the document implied. It stays deferred anyway, because §6.2's reasons for deferring it are **breaking-change** reasons (gates defaulting on; `TestResult.Score` exactness dead by construction), not input-model-cost reasons, and those survive the correction untouched. |
| Is the §4.2 guard sound? | **No — folded in this ratification.** The D2 fix (guard on `EvalResult`'s primary constructor) is itself bypassable by a `with` clone and sits on the wrong type. Replaced by the backing-field + validating-init-accessor pattern already shipped twice in this repo. See §4.2 and the new §7.1 row **D2b**. |
| Are §2.3's five defects still shipped and live? | **No. All five are fixed**, in `a396c5b4` — the same commit that added this ADR. |

**What ratification actually funds, stated as a diff and not as an intent.**

- **Slice 0 is DONE, not funded.** All seven items landed in `a396c5b4` ("three-valued
  `AssertionResult`, assertion→result wiring, ADR-030 Slice 0"), which is also the commit that
  created this file. §2.3 and §8 Slice 0 therefore describe a tree state that **no longer exists**;
  they are kept verbatim as the evidence that motivated the decision, and this note is the correction
  that stops them being read as current. Verified fixed this session: `CompositeEval` all-skipped now
  reports `label:"skipped"` (`CompositeEval.cs:149-187`); `MAFEvaluationHarness` now reads
  `EvaluationFailed` (`:635`, `:657`); `ToolInputAccuracyEval`'s two flattering `Build(1.0, true,
  "none")` sites now skip (`:124`, `:219`); `ToolUsageExtractor` sees `ToolApprovalRequestContent`
  (5 sites); `MicrosoftEvaluatorAdapter` is `: IMetric, IEval` — the dual-target that §9 Q3
  recommended; `EvalResultPersistence` populates `ScenarioResult.Assertions` from
  `AgentEvalScope.Current`.
- **The ADR's own gate did not hold.** "Nothing in §8 is funded until this document is ratified" was
  violated in the commit that wrote the sentence. Recorded rather than quietly dropped: a gate that
  is authored and cleared in one commit is not a gate, and this is the second document in this repo
  where the artifact under review supplied an input to its own approval.
- **Slices 1 and 2 are what ratification funds**, and neither exists in `src/` — verified this
  session: no `MeasurementState`, no `AgentEval.Evals.Meta`, no `ExactTests`, no `EvalInput.CaseId`.
  The stop rule (Slice 2.6) is therefore still ahead of us, not behind us.
- **The "Deferred until a second team asks" bucket stays unfunded**, including all three items the
  review found actually wrong (D1, D8, D10).

**Open questions closed by this ratification:** Q1 **yes** (the honest framing is wanted; the
demotion stands). Q3 **dual-target**, already implemented that way in `a396c5b4`. Q7 **normative** —
§3.1's exclusion list is normative text in this ADR and a PR adding `contains` is closed with a link
to it. **Still open and still gating their own slices:** Q2 (packaging), Q4 (schema v1.1 timing —
gates Slice 1.4), Q5 (controls — unfunded, which is the answer unless it is revisited), Q6 (the stop
rule — recommend it actually stops), Q8 (four unverified items; the §5.2 line delta remains an
attribution estimate and must not be quoted as a measurement).

**Not ratified:** nothing in §8's deferred bucket, and no line-count claim in §5. §5.1's
"−2,791 (13.7%)" and §5.2's "~−974" are projections of a migration nobody has performed.

---

## 0. READING ORDER, AND THE ONE THING TO TAKE IF YOU READ NOTHING ELSE

The valuable half of this ADR was **§8 Slice 0** — bug fixes, no new public types, every defect
shipped and live in `src/` when this was written. **They are now fixed** (`a396c5b4`; see §0.0), so
the valuable half of this ADR *as a plan* is now **Slices 1 and 2**: applicability, and the
BCL-only statistics module. Everything after those is progressively more speculative and
progressively less funded, and §2.3 should be read as the case that was made, not as a description
of the tree.

If the reader takes one sentence: *AgentEval's differentiator is not that it can score a string
deterministically — every competitor can. It is that it can tell you whether your eval, judge or
code, could have failed at all. Nobody ships that. That is the lane, and it is about 700 lines.*

---

## 1. CONTEXT AND THE QUESTION ASKED

Two questions arrived together.

**(a) The architectural one, verbatim:** *"what abstractions should we add in AgentEval to have all
work together inside our evaluation suite by extending — so we do not have to build another
evaluation harness framework but extend and use AgentEval. That is the goal and the nicest thing to
do architecturally. Also follows DRY, CLEAN, SOLID and PRAGMATIC."*

**(b) The scope one:** *does non-LLM (deterministic) evaluation belong in AgentEval as a first-class
concern, and do the non-deterministic (judge) evaluators have a reason to be?*

The forcing evidence is the Galaxus sample: **20,348 lines of C#** (measured this session; 55 `.cs`
files under `samples/Galaxus.RecommendationAgent.Evals/`, excluding `obj/` and `bin/`) of which the
large majority is grading machinery the sample had to build because the library could not reach it.
A second consumer, `AgentEval.TravelDemo.Evals` (2,433 lines), built a smaller copy of the same
machinery and got it wrong.

The brief for this ADR asserted "~17,400 lines". That undercounts by ~2,900 because Evals 05–09 grew
after the figure was taken. **20,348 is the measured number; use it.**

---

## 2. THE EVIDENCE

Every number in this section was produced by reading the tree this session. Where a claim is
inherited rather than measured, it says so.

### 2.1 AgentEval has SIX evaluation contracts, not four

The brief said four. Counted in `src/`:

| # | Contract | Location | Implementations | Result model |
|---|---|---|---|---|
| 1 | `IEval` | `src/AgentEval.Abstractions/Evals/IEval.cs`, ns `AgentEval.Evals` | **79 files** declare it | `EvalResult` (nests, versioned schema, provenance) |
| 2 | `IEvaluator` | `src/AgentEval.Abstractions/Core/IEvaluator.cs`, ns `AgentEval.Core` | 2 real (`ChatClientEvaluator`, `CalibratedEvaluator`) + 2 stubs; **203 references** | `AgentEval.Core.EvaluationResult` |
| 3 | `IProbeEvaluator` | `src/AgentEval.RedTeam/RedTeam/Core/IProbeEvaluator.cs` | 30 | `AgentEval.RedTeam.EvaluationResult` (`readonly record struct`) |
| 4 | Assertions | `src/AgentEval.Core/Assertions/*` (16 files, 103+ fluent methods) | n/a | **throws** `AgentEvalAssertionException` — no result object |
| 5 | **`IMetric`** and its four sub-interfaces | `src/AgentEval.Abstractions/Core/IMetric.cs` | **26 concrete classes** (`IAgenticMetric` 6, `IRAGMetric` 9, `ISafetyMetric` 3+, `IMemoryMetric` 5, `IMetric` direct 1) | `MetricResult` (+ input `EvaluationContext`) |
| 6 | `IEvaluationHarness` | `src/AgentEval.Abstractions/Core/IEvaluationHarness.cs` | MAF + workflow + others | `TestResult` |

**Correction to the design lanes, which disagreed with each other.** The contract-unification lane
counted 21 `IMetric` implementations; the adversarial review counted 7. Both are wrong. **26** is the
count of concrete classes implementing `IMetric` or one of its four derived interfaces
(`grep "class X : I*Metric"`, excluding interfaces and registries). This matters: it is the
difference between "fold `IMetric`, it is small" and "do not fold `IMetric`, it is a 26-class
migration". §3.3 takes the second.

`IMetric` is also the contract the brief missed entirely, and it is load-bearing:
`AgentEvalBuilder` (`src/AgentEval.Core/Core/AgentEvalBuilder.cs:87,96,105`) exposes
`AddMetric`/`AddMetrics` and **no `AddEval`**. The public "build a suite" entry point knows contract
#5 and does not know contract #1, where all 79 evaluators live. Grep for `IEnumerable<IEval>` or
`IReadOnlyList<IEval>` across `src/`: **zero hits.** There is no runner over a *set* of evals at
all — `CompositeEval` composes them into one tree, but nothing runs a suite of them and persists it.

### 2.2 The flagship harness cannot reach the good contract

`src/AgentEval.MAF/MAF/MAFEvaluationHarness.cs` (759 lines) contains **no reference to `IEval`**.
Its complete deterministic capability, verbatim from `:174-189` and duplicated byte-for-byte at
`:390-401`:

```csharp
// No AI criteria - check for non-empty response and ExpectedOutputContains
result.Passed = !string.IsNullOrWhiteSpace(response.Text);
if (result.Passed && !string.IsNullOrEmpty(testCase.ExpectedOutputContains))
    result.Passed = response.Text.Contains(testCase.ExpectedOutputContains, StringComparison.OrdinalIgnoreCase);
result.Score = result.Passed ? 100 : 0;
```

Non-empty plus one case-insensitive substring, scored binary. Tool usage is captured, costed,
timelined and printed — and **consulted by no verdict**. The Galaxus sample says so in as many
words at `Eval01_CatalogueIntegrity.cs:29-32`: *"`TestResult.Passed` is ignored. With no criteria
the harness sets it to 'the agent produced non-empty text', which would score a refusal as a pass."*

**That is why the sample grew 20,348 lines.** It is the single most important fact in this ADR.

### 2.3 Five defects that are shipped, live, and flattering

> **⚠️ HISTORICAL AS OF 2026-09-05. All five are FIXED**, in `a396c5b4` — the same commit that added
> this ADR. Kept verbatim because they are the evidence the decision was made on, and because a
> defect table rewritten after the fix stops being a record of what was actually wrong. Do **not**
> cite any row here as a current property of `src/`. §0.0 carries the per-item verification.

Each verified by reading the file at the time of writing. Each fails in the direction that makes an
agent look better than it is — which is the direction that survives review.

| # | Defect | Location | Why it is flattering |
|---|---|---|---|
| D-a | A composite where **every leaf skipped** returns `label:"pass", passed:true, score:0.0` | `CompositeEval.cs:148-163` + `MinAggregation.cs:33` (`if (nonSkipped.Count == 0) return (0, "none")`; the `Threshold==null` path reads only severity, and `SeverityRollup.Max(empty)` is `"none"` → `"pass"`) | A green verdict from an instrument that measured nothing. This is the silent-`{}` shape from the standing gate self-examination rule. |
| D-b | A judge that **failed to produce a verdict** can certify an agent | `ChatClientEvaluator.cs:179,185,201` return `OverallScore = DefaultFailureScore = 50` with `EvaluationFailed = true`; `MAFEvaluationHarness.cs:168` does `result.Passed = evaluation.OverallScore >= testCase.PassingScore` and **never reads `EvaluationFailed`** (grep: 0 hits in that file) | Any `PassingScore <= 50` passes on a judge parse failure. |
| D-c | Three **perfect scores on absent input** | `ToolInputAccuracyEval.cs:116` (no tool calls), `:129` (no tool definitions), `:214` (`totalCalls == 0`) — all `return Build(1.0, true, "none")`; line 129 ships evidence reading *"schema validation skipped"* **beside a 1.0** | Supply no `ToolDefinitions` and this eval reports perfect, forever. |
| D-d | `TestCase.ExpectedTools` is written by every loader and read by **no** `MAFEvaluationHarness` path | `CsvDatasetLoader.cs:200`, `JsonDatasetLoader.cs:175`, `JsonlDatasetLoader.cs:122`, `YamlDatasetLoader.cs:124` write it. `DatasetTestCaseExtensions.cs:79` **documents behaviour that does not exist**: *"For tool-call accuracy evaluation, use the ToolUsage metrics which compare against ExpectedTools."* | A dataset declaring expected tools passes on any non-empty string. |
| D-e | An **approval-gated tool call is invisible**, so `NeverCallTool` has a chance floor of 1.0 | `ToolUsageExtractor.Extract` matches `FunctionCallContent` only. MAF answers an approval-required call with a `ToolApprovalRequestContent` carrying the `FunctionCallContent` in its `.ToolCall` property and **no** `FunctionCallContent` of its own | `NeverCallTool("PlaceOrder")` passes whatever the agent does — zero information — and its permission partner is unpassable, so the pair scores 0.5 for every agent forever. |

**Naming correction, resolved this session.** The brief names the MAF type
`FunctionApprovalRequestContent`. That type appears **nowhere** in this tree. The type actually used
is **`ToolApprovalRequestContent`** (with `.ToolCall is FunctionCallContent`), in 20 places across
`src/AgentEval.MAF/Gatekeeper/`, `samples/AgentEval.Samples/Gatekeeper/` and the Galaxus adapter,
plus `ToolApprovalResponseContent` for the decision. The maf-doctor registry (MAF 1.3.0, 63 entries)
reports no known issue for either name, so the tree is the authority and the brief is wrong.

Also verified absent, by grep over `src/` excluding `obj/`: **chance floor / random baseline /
degenerate policy → 0 hits. Sign test / binomial / p-value / McNemar / Wilcoxon → 0 hits.** The
library has no significance test of any kind. `DistributionStatistics` computes confidence intervals
that decide nothing.

`ScenarioResult.Assertions` is hard-coded `Array.Empty<AssertionResult>()` at both and only its
construction sites (`EvalResultPersistence.cs:70`, `DirectoryExporter.cs:187`). Assertion outcomes
appear in **no artefact AgentEval writes**. They exist as stack traces.

### 2.4 The ecosystem, and the one gap in it

Surveyed: OpenAI Evals, DeepEval, Ragas, promptfoo, LangSmith, Braintrust, Inspect, HELM,
`Microsoft.Extensions.AI.Evaluation`.

- **Deterministic assertions are a solved, crowded problem.** promptfoo owns the catalogue outright
  (~30 assertion types: `equals`, `regex`, `is-json` with schema, `levenshtein`, `f-score`, `bleu`,
  `rouge-n`, `latency`, `cost`, a full trajectory family). `M.E.AI.Evaluation.NLP` ships
  BLEU/GLEU/F1 on the **same** `IEvaluator` as its LLM-graded evaluators with `chatConfiguration`
  nullable — the cleanest unification in the field, in a package this repo **already references**
  (`Directory.Packages.props:41`, `Microsoft.Extensions.AI.Evaluation.Quality 10.7.0`).
- **Meta-evaluation is shipped by nobody.** Not one of the nine ships a chance floor, a negative
  control, or paired significance as machinery. The single artefact in the whole field is a
  hand-rolled `RandomBaselineSolver` inside two OpenAI Evals directories — a per-eval convention,
  computed by no harness, carried by no report model, required by nothing. Inspect leads on
  *variance* (bootstrap stderr, Wilson CIs, clustered stderr) and still answers only "how much does
  the score move", never "could this eval have produced a bad score at all".

**The evidence, folded in-repo on 2026-09-05.** This table previously lived in
`strategy/Galaxus/Ecosystem_ChanceFloor_Comparison.md`, which is gitignored and therefore was not
readable from this repository — a citation nobody could check. That file is deleted; its evidence is
here. ⚠️ **The sweep itself is still the one performed on 2026-09-04 and was not re-run** (Q8): treat
every row as of that date, not as of today.

| Framework | Evidence for "no chance-floor / negative-control machinery" |
|---|---|
| **HELM** | Metrics docs enumerate ~50 metrics in 7 categories; no random baseline, chance level or null model. Code search `"random baseline" repo:stanford-crfm/helm` → **0 hits** |
| **Inspect (UK AISI)** | Metrics page lists `accuracy, mean, var, std, stderr, bootstrap_stderr, ci, ci_wilson, frequency, categorical, krippendorff_alpha` — no baseline/chance metric. `"random baseline"`, `"negative control"` → **0**. The nearest artefact is a **test double, not a control**: `mockllm.py`, *"Always returns default_output"*, explicitly **no randomness** |
| **DeepEval** | `"random baseline"` → **0**; nothing in the metric docs |
| **Ragas** | Metrics overview has no mention of baselines or chance |
| **promptfoo** | `"chance level"`, `"negative control"` → **0**. `"random_baseline"` → 1 hit: a **blog post about RLVR**, not a feature |
| **Braintrust / autoevals** | `"random baseline"`, `"chance level"`, `"negative control"`, `"random_baseline"` → **all 0**. It *does* ship a "baseline experiment" (`base_experiment`) — but that is **a previous run of your own system**, not a degenerate policy |
| **LangSmith / openevals** | Evaluation-concepts doc covers human / code / LLM-judge / pairwise; **no** baseline, chance, variance or significance |
| **M.E.AI.Evaluation** | Namespace type list and libraries doc contain **no** baseline or chance concept |
| **OpenAI Evals — the one exception** | `RandomBaselineSolver`, verbatim: *"A solver that randomly answers `yes` or `no` for any input. We view this baseline as equivalent to randomly guessing."* It exists in `already_said_that/solvers.py` and `track_the_stat/solvers.py` and is registered as a runnable "model" in two YAMLs. **The shape of the exception is the finding: a per-eval, hand-rolled convention inside individual eval directories, not a framework capability.** No harness computes it, no report model carries it, nothing requires it |

**Statistical rigour, for calibration of the claim:** Inspect is in a different league and it is honest to
say so — `var`, `std`, `stderr`, `bootstrap_stderr` (1000 resamples), `ci`, `ci_wilson`,
`krippendorff_alpha`, and **clustered standard errors** via `stderr(cluster=...)`. Everyone else stops
at repetition (Braintrust `trialCount`, LangSmith `num_repetitions`, promptfoo `--repeat`) with **no
significance testing found in any of them**. **No framework in the set ships paired significance
testing** — no paired bootstrap, no McNemar, no paired t-test — though four ship pairwise *judging*.
Outside the set, EleutherAI's `lm-evaluation-harness` reports bootstrap stderr per task, which puts it
in Inspect's tier.

**Uncertainty flags carried forward, because a 0-hit is not a proof:** GitHub code search indexes the
default branch and can miss things, and the API rate-limited the sweep mid-run. Confidence is **high**
for HELM / Inspect / autoevals / DeepEval (0 hits *plus* corroborating doc reads), **medium** for Ragas
and langsmith-sdk (those queries were rate-limited; doc reads only). "ndcg" was not exhaustively
grepped — treat MRR/NDCG as *not documented* rather than *absent*.

**The differentiating point, stated plainly:** every framework here answers *"what score did the system
get?"*. Several answer *"how much does that score move between runs?"*. **None of them answers "could
this eval have produced a bad score at all?"**

### 2.5 The live-run proof — real money, $6.34, today

This is the part that is not a code-reading argument.

- **Eval 01 live: the agent scored 8/14; the best constant policy scored 10/14.** The agent was
  **below its own floor**. No analytic floor would have said so — only actually running an
  input-blind policy did. The chance-floor instrument is the only thing that revealed it.
- **3 of the agent's 6 failures were one false positive.** `"wahl"` — German for both *election* and
  *choice* — sat in the special-category blocklist and tripped on every German-language persona.
  **Invisible offline.** It became visible the moment a floor and a paired comparison existed,
  because it made the agent lose cases a constant policy won.
- **Demo 2: 6 of 7 model calls timed out at 60s.** The reported finding *"the loop bought nothing"*
  came from **timeouts**, not from the reviewer. The eval measured deterministic fallbacks and did
  not say so loudly enough. This is `MeasurementState.NotMeasured` (an operational finding) being
  reported as if it were a measurement.
- **`DetectOptOutBackstop` is the only place in the entire suite that reads a tool RESULT** rather
  than its arguments. It is covered by no control and reports its own blindness as *"never
  exercised"* — an instrument that cannot fail, un-flagged.

Two more, recorded by the sample's own comments as measured-and-fixed, both in the flattering
direction:

- `RuleOfThreeUpperBound` was called with a **clean-case count** and printed *"95% upper bound
  34.8%"* beside an **observed defect rate of 50%**. A bound below its own observation is not a
  bound.
- The constant-policy ceiling was **typed as 8** in a comment and **measured at 10**; the refuser was
  typed 8 and measured 5. A hand-typed baseline is a baseline someone chose.

### 2.6 One qualification this ADR owes its own claim — we are not quite at zero

*Added 2026-09-05, folded from `strategy/Galaxus/AgentEval_NonLlm_Inventory.md` before that file was
deleted. §2.4 says meta-evaluation is shipped by nobody. Inside this repository that is very nearly
true, and the exception has to be named or §3 is arguing against a strawman of our own making.*

**`GateCalibrationHarness` — `AgentEval.Guardrails.Judges` — already does part of it.** It computes a
full confusion matrix (TP/TN/FP/FN), decisive accuracy, a **dangerous-error (FN) count**, FPR, and κ
against gold; promotion is **fail-closed**, requiring `PromotionCriteriaConfigured && SufficientData`;
and it carries an optional **deterministic-baseline comparison**:

```csharp
beatsBaseline = judgeAccuracy > baselineAccuracy && fn <= bfn;
```

**That is the only place in this library that asks "does the judge beat a deterministic detector?"** —
which is a meta-evaluation question, not a scoring question. Three things keep it from refuting §2.4
and all three are the point:

1. It is a **judge**-scoring instrument. It answers *"is this judge good?"*, never *"could this eval
   have failed?"* — no chance floor, no applicability, no unit-of-analysis collapse.
2. It emits a `CalibrationReport`, **never an `EvalResult`**, and is wired to nothing else. It is a
   seventh result shape in all but name, which is exactly what §3.2 forbids the meta lane from
   becoming.
3. Its baseline is *supplied*, not *derived*. `baselineAccuracy` is whatever the caller hands it.

**Consequence, binding on Slice 2.** `AgentEval.Evals.Meta` **must reconcile with this harness or
state in writing why not.** Shipping a second, unconnected meta-evaluation instrument beside it is the
fork this ADR exists to refuse, and it would be a fork in the one place §3.2 says a fork is worst.
`FloorComparison`/`PairedEvalComparer` and `beatsBaseline` are answering neighbouring questions; if the
former cannot express the latter, that is a finding about the design, not about the harness.

**Two smaller corrections from the same inventory, recorded so nobody re-derives them wrong:**

- **`PerformanceBenchmark` has the `IEval` signature and is not an `IEval`.** It exposes
  `EvaluateAsync(EvalInput, CancellationToken) → EvalResult` and builds a real three-leaf
  `CapByWorst` composite, but is declared `public class PerformanceBenchmark` with no interface, and
  carries a `SyntheticEval : IEval` **stub whose only method throws**, present purely to satisfy an
  `EvalComponent` constructor. `OwaspBenchmarkRun` / `NistBenchmarkRun` / `MitreBenchmarkRun` use the
  identical duck-typed + throwing-stub pattern. That is four real measurement families that cannot be
  composed — the §2.2 reachability gap one layer further down, and **not counted in §5's line delta**.
- **`CalibratedEvaluator` (§2.1, contract #2) is a naming trap.** It is not statistical calibration; it
  is multi-LLM consensus voting (`judges.Select(j => (IEvaluator)new ChatClientEvaluator(j.Client))`).
  **It adds cost, not determinism.** One of contract #2's "2 real implementations" is this.

---

## 3. THE DECISION

### 3.1 The lane: meta-evaluation, YES. Deterministic assertion catalogue, NO — permanently.

**Adopt as first-class:** chance floors, exact small-sample tests, paired comparison with
unit-of-analysis collapse, and the applicability distinction (measured / not-applicable /
not-measured).

Three reasons, in order of strength:

1. **Nobody ships it** (§2.4), and it is the half of Composite Judges we have been missing. A code
   eval fails loudly. A judge's failure mode is *a plausible number that would have appeared
   anyway* — which is exactly what a floor and a control detect.
2. **Our own history is the evidence.** 78.2% of V3 probe answers were empty and counted as passes.
   69 unearned V2 passes from a bar the artefact supplied. Procedural V6 printed 77/80 that was
   flattering because an absent chance floor is not a zero floor. Every one is a meta-evaluation
   failure and the library has no machinery that would have caught any of them.
3. **It is small.** The funded slices are ~700 production lines.

**Positioning:** *AgentEval is the library that tells you whether your eval — judge or code — could
have failed.*

#### The exclusion list. Normative. By name. Permanently.

AgentEval will not ship, and any PR adding one is closed with a link to this subsection:

- a string-matching assertion catalogue — `equals`, `contains`, `starts-with`, `regex`, `is-json`,
  `levenshtein`, or a schema-validation assertion type;
- BLEU, ROUGE, GLEU, METEOR, chrF, or any n-gram overlap metric;
- exact-match scoring as a shipped eval;
- NDCG, MRR, recall@k as *new* work (the two that already exist are
  `src/AgentEval.Core/Metrics/Retrieval/MRRMetric.cs` and `RecallAtKMetric.cs` — **named here on
  2026-09-05 because the inventory that fed this ADR wrongly claimed neither existed**; they stay
  where they are and they are not
  extended);
- an assertion DSL, YAML-configured or otherwise;
- **a deterministic `IEvaluator` implementation** (see §3.3).

`AtomicCodeEval` is public and a `contains` eval is 30 lines. For NLP metrics, depend on
`Microsoft.Extensions.AI.Evaluation.NLP`. The realistic failure mode here is not "we added a chance
floor"; it is that having added a floor, `contains` looks free, then `regex`, then a schema
validator, then a DSL — and eighteen months later we are a worse promptfoo maintained by fewer
people. The list is the mitigation.

### 3.2 The load-bearing rule

> **Meta-evaluation never implements `IEval`.**
> A floor is a property *of a comparison*. A control is a *run of* an eval. A comparison is a
> *function of* results. Nothing in `AgentEval.Evals.Meta` returns `EvalResult` as its own verdict.

This is what prevents a seventh result model, and the seventh would be the one holding pass/fail
authority — the worst possible place for a fork. It is not expressible in the type system, so it is
enforced by an architecture test (§4.6).

**Ruling applied (was `EvalScore.ChanceFloor`, now deleted).** The draft said *"a floor is a FIELD ON
A SCORE"*. The review refuted it and the refutation holds:

1. **A composite has a `Score` and cannot have a floor.** The node consumers actually read — the
   root of the tree, the row in `results.jsonl` — would carry `chanceFloor: null` forever. The field
   would be null precisely where it is wanted.
2. **The number without its derivation is unusable.** `0.44` means nothing without `k`, `poolSize`,
   `Kind` and the derivation sentence. Those go into `Details.Dimensions` and `Details.Evidence`
   regardless — so `Score.ChanceFloor` would be a duplicate of data that already has a home, and
   duplicated data drifts.
3. **It buys the design's only avoidable schema break.** `Details.Dimensions` is
   `additionalProperties`-open and already has a reserved-key convention (`_lifted.*` in
   `EvalResultPersistence.cs:46-56`). `chance_floor` fits with **zero** schema change.

So: floors live in `Details.Dimensions["chance_floor*"]` plus one
`EvalEvidence("chance-floor", kind, derivation)`. The typed floor object lives at the **suite**
level, in `FloorComparison`, where the derivation, the interval and the pool are all in scope
together — which is where the exact test needs them anyway.

### 3.3 Which contract survives, and what happens to the other five

**`IEval` survives.** And the honest reason must be stated first, because the review is right that
dressing it up as a design preference will cost the document its credibility with the first reviewer
who knows `M.E.AI.Evaluation`:

> **`IEval` survives because it already has 79 implementations, a JSON schema, an evaluator-card
> registry, a persistence layer, a CLI and MissionControl. This ADR is not choosing a contract; it
> is declining to migrate 79 evaluators. That is a legitimate reason and it is the actual one.**

The secondary reasons are real but are claims about AgentEval's current assets, not comparative wins:

- It is the only contract whose result can express **"not applicable"** —
  `EvalResult.Skipped(eval, reason)` produces `label:"skipped"`, and four of the five aggregation
  strategies already exclude it from the denominator (`WeightedSumAggregation.cs:34`,
  `MinAggregation.cs:33`, `WeightedMedianAggregation.cs:35`, `MajorityVoteAggregation.cs:37`).
  *(Inconsistency to log: `CapByWorstAggregation.cs:37-40` filters `"skipped"` but not `"error"`. It
  is safe today only because error leaves carry severity `"none"`. Unintended; fix in Slice 1.)*
- It carries **how-was-this-produced as data** (`EvalProvenance.Type`, `JudgeModel`, `PromptHash`,
  `TokensUsed`, `EstimatedCost`, `CacheHit`).
- It **nests** (`EvalDetails.SubResults`, depth-capped at 32). A suite is a tree.

**The honest caveat that shapes everything else:** `IEval` wins on output, composition, provenance
and applicability. It **loses on input** — but by less than this document originally claimed, and the
correction is recorded rather than absorbed.

> **Corrected at ratification (2026-09-05).** The sentence here read *"`IMetric`'s
> `EvaluationContext` already carries `ToolUsage`, `Timeline`, `Performance` and `ExpectedTools`;
> `EvalInput` carries none of them."* **The second clause is false.** `EvalInput` already carries
> `ToolCalls` (`IReadOnlyList<ToolCall>`, with a chronological-ordering contract), `ToolDefinitions`
> and `ExpectedActions` — verified at `src/AgentEval.Abstractions/Evals/EvalInput.cs`. What it
> genuinely lacks, measured against `ToolCallRecord`
> (`src/AgentEval.Abstractions/Models/ToolCallRecord.cs`), is **three fields on `EvalInput.ToolCall`
> — `WasExecuted`, `ApprovalState`, `Order`** — plus a `ToolUsageReport → EvalInput` projection.
> `Timeline` and `Performance` remain genuinely absent.
>
> **This makes the bridge cheaper than the document implied, and it does not reopen the decision.**
> The bridge is deferred in §8 for the reasons in §6.2 — two gates that default ON and flip
> dataset-driven consumers pass→fail on a minor, and a `TestResult.Score` exactness guarantee that is
> dead by construction. Those are breaking-change reasons. They are unaffected by how few fields the
> input gap is, and they are why the bridge is deferred rather than cheap-and-therefore-funded. What
> the correction does change: **"`EvalInput` cannot carry tool data" must not be cited as a reason
> for anything**, by this document or by any document depending on it.

The residual input gap is still *why* 26 structural metrics were written against `IMetric`:
`EvaluationContext` hands a metric a whole `ToolUsageReport` with timing, ordering and execution
state already joined, and `EvalInput` hands it three plain fields per call.

| Contract | Disposition | Funded when |
|---|---|---|
| **`IEval`** | Survives. **Interface untouched — no member added, ever, in this programme.** | — |
| **`IEvaluator`** | **Stays, retyped in DOCUMENTATION ONLY** as the LLM-judge transport. Already bridged by the shipped `AtomicLlmEval`. See below. | Slice 0 (doc) |
| **`IMetric`** (26 impls) | **Stays. Not folded.** The one genuine deletion available is retargeting `MicrosoftEvaluatorAdapter` from `IMetric` to `IEval` — 1 class, ~40 lines — so first-party M.E.AI evaluators stop entering through the second-class contract. | Slice 0 |
| **`IProbeEvaluator`** (30) | Stays. Struct stays (5 fields, zero heap allocation; `EvalResult` is a floor of 5 allocations, and a campaign evaluates tens of thousands of pairs). `ProbeEval` adapter **deferred**. | Deferred |
| **Assertions** (16 files) | Stay throwing. `AssertionRecorder`/`AssertionEval` **deferred**. Slice 0 stops the lie in `ScenarioResult.Assertions`. | Deferred |
| **`IEvaluationHarness`** | Bug fixes only in the funded slices. `EvaluationOptions.Evals` and `MAFEvaluationHarnessOptions` **deferred**. | Deferred |

**Why `IEvaluator` must not be widened — the answer to "should judge evaluators exist?"**

Yes, and widening `IEvaluator` to cover deterministic scoring would manufacture a Liskov violation
with a named victim in this codebase. `EvaluationResult` has three members whose *documented*
contract only a judge can satisfy:

- `EvaluationFailed` — documented at `IEvaluator.cs:44-52` as *"the judge returned no JSON,
  malformed JSON, or no recognisable score field… an INFRASTRUCTURE failure"*. A deterministic
  implementation can never truthfully enter that state, and `AtomicLlmEval` **branches on it**
  (sets `value=0, passed=false, label="error"` and replaces all evidence). A deterministic subtype
  makes a branch the caller depends on provably dead.
- `InputTokenCount` / `OutputTokenCount` — documented *"`null` when the evaluator did not invoke a
  chat model"*. A deterministic substitute is therefore **indistinguishable from a judge whose
  provider dropped usage reporting**, and silently zeroes the cost rollup.
- `OverallScore` on failure *"carries the conventional failure-score fallback but should not be read
  as a real grade"* — a sentinel only a judge produces.

So `IEvaluator` is correctly typed as an **LLM-judge transport**. The fix is a doc change, not a code
change: amend the summary from *"Provides AI-powered response evaluation"* to say plainly that it is
the judge transport, that deterministic scoring belongs on `IEval`/`AtomicCodeEval`, and that
`AtomicLlmEval` is the sanctioned bridge — **which already ships.**

And the judge earns its place precisely where a code leaf cannot reach: *was the refusal
appropriate*, *is the justification faithful to the retrieved evidence*. After the bridge (deferred)
it is just a leaf carrying `Provenance.Type = "atomic-llm"`, a real `EstimatedCost` and an `"error"`
label when it fails to speak. It does not need a seventh contract; it needs to stop being the *only*
contract the flagship harness can reach.

### 3.4 What this decision explicitly is NOT

**It is not a unification.** Counted honestly:

| | before | after (full design) | after (funded slices) |
|---|---|---|---|
| Evaluation contracts | 6 | 6 | 6 |
| Verdict-bearing result models | 9 | 9 | 9 |
| Adapters | 2 | 7 | 2 (1 retargeted) |
| New non-verdict record types | ~0 | ~20 | ~6 |

Not one contract is deprecated; not one implementation migrates. **A unification that deletes no
contract, migrates no implementation and deprecates no API is not a unification — it is a
compatibility layer with a unification's budget.** The review said this and it is correct. The
headline is therefore demoted: this ADR is *meta-evaluation plus bug fixes*, and the word
"unification" does not appear in its title.

---

## 4. THE ABSTRACTIONS

Namespaces, signatures, and where each lands. Everything here is additive; every existing call site
keeps compiling. Where a change is **not** non-breaking, §6.2 names it.

### 4.1 The neutral observation tuple — the single most important design ruling

Chance floors, controls, sign tests and rep collapse operate on a five-tuple and nothing more.
Binding them to `EvalResult` was the draft's mistake, and it costs the ADR its best card: the meta
layer is the **only** part of this work with no prior art in any surveyed framework. Defined over a
neutral tuple it is a ~600-line module that AgentEval, `Microsoft.Extensions.AI.Evaluation`, and a
future Python port can all consume — and that could be **contributed upstream** as
`Microsoft.Extensions.AI.Evaluation.Meta`, which is a far better outcome for this project than owning
a private copy. Defined over `EvalResult` it is an AgentEval internal nobody else can adopt.

```csharp
namespace AgentEval.Evals.Meta;   // BCL-only. No reference to AgentEval.Abstractions.

/// <summary>One collapsed observation: a case, an arm, a number, and whether that number is real.</summary>
/// <remarks>
/// The unit of analysis is the CASE, not the rep. Everything in this namespace consumes
/// <see cref="Observation"/> and nothing else, so the whole module is testable without
/// constructing an eval tree, and portable to any framework that can produce a tuple.
/// </remarks>
public readonly record struct Observation(
    string CaseId,
    string ArmId,
    double Value,
    MeasurementState State);
```

Adapters `EvalResult → Observation`, `MetricResult → Observation`, and
`Microsoft.Extensions.AI.Evaluation.EvaluationResult → Observation` live in `AgentEval.Core` and are
one-way, per §3.2.

`MeasurementState` is the one type that must live in **both** worlds (it is a field on `EvalScore`
and on `Observation`). It is a BCL-only enum; declare it in `AgentEval.Evals.Meta` and have
`AgentEval.Abstractions` reference the meta project, which is the bottom of the dependency graph.
*(`AgentEval.Abstractions.csproj` has **zero** `PackageReference` entries and `IsPackable=false` —
verified — so it is already BCL-only and this ordering is available. Packaging consequences are
an open question, §9 Q2.)*

### 4.2 Applicability — the third state

The draft proposed `bool? Applicable`. **Rejected:** a tri-state `bool?` is exactly the silent-`{}`
shape — `null` reads as "nobody set it" and the first consumer writes `score.Applicable ?? true`.
The default must be a real, named value.

```csharp
namespace AgentEval.Evals.Meta;

public enum MeasurementState
{
    /// <summary>A real measurement. Default, so every existing call site is unchanged.</summary>
    Measured = 0,

    /// <summary>
    /// The CASE could not test the thing: empty gold, no distractor, no tool definitions supplied,
    /// a chance floor of 1.0. A CORPUS/DESIGN finding — fix the cases.
    /// <para>
    /// Never a pass and never a zero. Excluded from means, counted in its own column.
    /// <b>"The agent answered nothing" is NOT this state — that is a FAIL.</b>
    /// Applicability is a property of the CASE, never of the ANSWER.
    /// </para>
    /// </summary>
    NotApplicable = 1,

    /// <summary>
    /// The INSTRUMENT did not run: skipped, timed out, errored, budget-filtered. An OPERATIONAL
    /// finding — fix the run. Distinct from NotApplicable because they have different owners and
    /// different fixes; pooling them hides which one you have. Demo 2's six 60-second timeouts are
    /// this state, and reporting them as measurements is what produced a wrong finding.
    /// </summary>
    NotMeasured = 2,
}
```

It lands **on `EvalScore` as a non-positional init-only property** — the `EvalDetails.Summary`
precedent, so positional construction and deconstruction are unaffected. The full declaration is
below, after the guard ruling, because the guard is what determines how the property is declared.

**Where the guard goes, and why it took three attempts.** The invariant is one sentence — *a score
that is not a measurement can never be `Passed`* — and it has now been placed wrongly twice. Both
wrong placements are recorded, because the second one was this document's own fix for the first.

**Attempt 1 (the draft, and the meta lane): a throwing initializer on `Measurement` itself.**
Refuted twice over, and both refutations are decisive:

1. **It does not compile.** `Value`/`Threshold`/`Confidence` could self-validate in an initializer
   because they are *positional parameters* — the initializer reads the primary-constructor
   parameter. `Measurement` is not a parameter, so `Measurement is …` inside its own initializer is
   **CS0236**.
2. **Even if it compiled it could never fire.** A non-positional init-only property is set by an
   object initializer or `with`, which runs **after** the property initializer. The initializer
   would always observe `default` = `Measured`. The one invariant the entire applicability design
   rests on would have had no enforcement point.

**Attempt 2 (recorded as D2's fix): move the guard to `EvalResult`'s primary constructor.**
**Also insufficient — established by a compiled probe at ratification, and this is the correction
being folded in.** Two independent failures:

1. **A `with` clone skips it.** `EvalResult.Score`'s *initializer* runs on the constructor path only.
   `result with { Score = someNotApplicableScoreThatClaimsPassed }` invokes the **init accessor**,
   and an auto-property's accessor validates nothing. This is bit-for-bit the shape of **AE-01**
   (`AssertionResult.Passed`) and **AE-08** (`EvalScore.Value`) — both already found and fixed in
   this repo, both by the same reasoning, and the guard was written a third time in the form both
   fixes exist to forbid.
2. **It is on the wrong type.** The invariant's two operands, `Measurement` and `Passed`, both live
   on `EvalScore`. `EvalScore` is public and is read directly by all five aggregation strategies;
   guarding at `EvalResult` leaves every un-wrapped `EvalScore` unguarded and makes "was it wrapped
   yet?" load-bearing.

**Attempt 3, and the one to implement: a private backing field plus a validating `init` accessor on
each operand.** This is the pattern the repo has shipped **twice**; copy it, do not invent a third:

- `src/AgentEval.Abstractions/Output/AssertionResult.cs` — `Passed` declared explicitly with
  `private readonly bool _passed` and a validating `init`, so a `with { Passed = true }` copy of an
  `Inconclusive` result is refused (AE-01).
- `src/AgentEval.Abstractions/Evals/EvalScore.cs` — `Value` / `Threshold` / `Confidence` on
  `private readonly` fields with validating `init` accessors, so `with { Value = double.NaN }` is
  refused (AE-08).

Mirror their tests too: `tests/AgentEval.Tests/Assertions/AssertionResultWithExpressionTests.cs`
and `tests/AgentEval.Tests/Evals/EvalScoreWithExpressionTests.cs`.

```csharp
// src/AgentEval.Abstractions/Evals/EvalScore.cs
// The 7 positional parameters are UNTOUCHED. Passed is REDECLARED (it stays positional; only its
// storage and its accessor change) because it is one of the invariant's two operands and a `with`
// can set it from either side.
public sealed record EvalScore(
    double Value, int? Ordinal, string Label, bool Passed,
    double? Threshold, string Severity, double? Confidence)
{
    // ── existing AE-08 fields, unchanged ────────────────────────────────────────────────────────
    private readonly double _value = EnsureFinite(Value, nameof(Value));
    private readonly double? _threshold = EnsureFiniteOrNull(Threshold, nameof(Threshold));
    private readonly double? _confidence = EnsureFiniteOrNull(Confidence, nameof(Confidence));

    // ── new. Declared BEFORE _passed so the ctor path reads a settled Measurement. ──────────────
    // Measured is default(MeasurementState) = 0, so on the constructor path the pair is always
    // (Measured, Passed) and there is nothing to validate; Measurement can only become non-Measured
    // via an object initializer or a `with`, both of which run AFTER the field initialisers.
    private readonly MeasurementState _measurement = MeasurementState.Measured;
    private readonly bool _passed = Passed;

    /// <summary>
    /// Whether the eval decided and the thing held. Positional, so every existing call site and
    /// every persisted artifact is unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when set to <see langword="true"/> on a copy whose <see cref="Measurement"/> is not
    /// <see cref="MeasurementState.Measured"/>. The primary constructor cannot reach that state —
    /// <see cref="Measurement"/> is still <c>Measured</c> while the field initialisers run — so the
    /// constructor path assigns directly and only the copy path validates.
    /// </exception>
    public bool Passed
    {
        get => _passed;
        init
        {
            EnsureDecidable(_measurement, value, nameof(Passed));
            _passed = value;
        }
    }

    /// <summary>
    /// Whether this score is a real measurement. Non-positional and init-only, so positional
    /// construction and deconstruction are unaffected and the default is <c>Measured</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when set to a non-measured state on a score whose <see cref="Passed"/> is
    /// <see langword="true"/> — on an object initializer, a <c>with</c> copy, and deserialisation
    /// alike, since all three route through this accessor.
    /// </exception>
    public MeasurementState Measurement
    {
        get => _measurement;
        init
        {
            EnsureDecidable(value, _passed, nameof(Measurement));
            _measurement = value;
        }
    }

    /// <summary>The only sanctioned way to build a not-applicable score.</summary>
    /// <remarks>The REASON does not live here — it goes to <c>EvalDetails.Summary</c> (and
    /// <c>Recommendations</c>), per D13. A bare <c>n/a</c> with no reason is the blank cell the
    /// rendering rule exists to forbid.</remarks>
    public static EvalScore NotApplicable(string severity = "none") =>
        new(0.0, null, "inapplicable", false, null, severity, null)
        { Measurement = MeasurementState.NotApplicable };

    // One predicate, both accessors. The guard is on the PAIR, so it cannot be satisfied by
    // arriving at the bad state from the other side.
    private static void EnsureDecidable(MeasurementState measurement, bool passed, string member)
    {
        if (measurement is not MeasurementState.Measured && passed)
        {
            throw new ArgumentException(
                $"A score whose Measurement is '{measurement}' cannot be Passed. The case had " +
                "nothing to fire against, or the instrument did not run; undecidable is not a pass. " +
                $"Use {nameof(EvalScore)}.{nameof(NotApplicable)}(...) or EvalResult.Skipped(...).",
                member);   // the member the caller actually assigned, so the ParamName is testable
        }
    }
}
```

**Why this closes the hole the constructor guard left open**, spelled out because the failure is
order-dependent and a reviewer will want to check it:

- The record's compiler-generated **copy constructor copies every field first**, then the `with`
  block's init accessors run in source order. So each accessor sees the *other* operand's copied
  value, and **both orderings are caught**: `score with { Passed = true, Measurement = NotApplicable }`
  throws on the second assignment, and `score with { Measurement = NotApplicable, Passed = true }`
  throws on the second assignment too.
- `EvalScore.NotApplicable() with { Passed = true }` — the exact bypass that defeated attempt 2 —
  throws.
- **Deserialisation is covered and fails closed.** `System.Text.Json` fills positional members
  through the constructor and init-only members after, so an artifact carrying
  `{"passed": true, "measurement": "NotApplicable"}` throws when `Measurement` is set. That is the
  ADR-025 direction: refuse the artifact rather than load a pass that was never earned.
- **`EvalResult` needs no guard at all.** A bad `EvalScore` is now unconstructable, so pass/fail
  authority stops depending on whether the score happened to be wrapped yet.

**All of the above was compiled and run**, not reasoned about: a 16-assertion probe on `net8.0`
exercised every path in this subsection — both `with` orderings, the object-initializer path,
positional deconstruction, the surviving AE-08 `NaN` guard, a legal round-trip, and the hostile
artifact. All 16 hold. Two results worth carrying into the implementation:

- **`Measurement` participates in record equality** (it is a field), so
  `EvalScore.NotApplicable() != new EvalScore(0.0, null, "inapplicable", false, null, "none", null)`.
  That is correct — an inapplicable score and a fabricated look-alike are different facts — but any
  test or cache keyed on `EvalScore` equality will see new inequalities the day Slice 1 lands.
- **The escape hatch, declared rather than discovered later.**
  `notApplicableScore with { Measurement = MeasurementState.Measured, Passed = true }` **succeeds**.
  It is a deliberate two-step re-declaration — the author must assert *both* "this is a real
  measurement" *and* "it passed" in the same expression — and there is no way to forbid it without
  making `Measurement` immutable after construction, which would break the legal
  `NotMeasured`-on-a-timeout path. It fails in the flattering direction, so: the architecture test
  that bans `Measurement = …` outside `EvalScore.NotApplicable()` must also flag
  `Measurement = MeasurementState.Measured` appearing in any `with` block, and that grep is the only
  thing standing between this hatch and someone using it to clear an inconvenient `n/a`.

**Residual, declared:** the guard binds `Measurement` to `Passed`, not to `Label`. A score with
`Label = "inapplicable"` and `Measurement = Measured` is still spellable. That asymmetry is
deliberate — `Label` is a free string that historical artifacts round-trip through, and a second
guard on it would reject documents that are merely old rather than wrong. The canonical mapping
table below is normative for producers; `CountsTowardAggregate()` reads **both**, so a mislabelled
score cannot leak into an aggregate through the label alone.

An **architecture test** still bans `Measurement = MeasurementState.NotApplicable` in object
initializers outside `EvalScore.NotApplicable()`, but it is now a style rule, not the enforcement
point — the accessor is.

The assertions lane proposed the same invariant as `AssertionResult.Applicability` and would have
had the same non-enforcement bug. It no longer can: `AssertionResult` **shipped** in `a396c5b4` with
the three-valued `AssertionOutcome` and the correct pattern (AE-01), and it is now the precedent this
subsection copies rather than a lane waiting on this ruling.

Canonical mapping, normative:

| `Measurement` | `Label` | `Passed` | `Value` | In the mean? | In the denominator? |
|---|---|---|---|---|---|
| `Measured` | `pass`/`fail`/`warn` | real | real | yes | yes |
| `NotApplicable` | `inapplicable` | **always false** | 0.0, never read | **no** | **no — own column** |
| `NotMeasured` | `skipped`/`error` | false | 0.0, never read | no (already) | no |

**One predicate, one home.** All five aggregation strategies today repeat
`r.Score.Label is not ("skipped" or "error")` in five files with two different comment vintages.
Route them through one extension so the new neutral state is added once:

```csharp
namespace AgentEval.Evals;

public static class EvalScoreExtensions
{
    /// <summary>The SINGLE authority on whether a score contributes to an aggregate.</summary>
    public static bool CountsTowardAggregate(this EvalScore score) =>
        score.Measurement == MeasurementState.Measured
        && score.Label is not ("skipped" or "error" or "inapplicable");
}
```

This also fixes the `CapByWorstAggregation` asymmetry noted in §3.3 as a side effect.

**And every mean must print its denominator:**

```csharp
namespace AgentEval.Evals.Meta;

/// <summary>What went into a number. A mean over 3 of 12 and a mean over 12 of 12 are different
/// facts and must not render identically.</summary>
public sealed record ObservationCensus(int Measured, int NotApplicable, int NotMeasured)
{
    public int Total => Measured + NotApplicable + NotMeasured;

    /// <summary>Nothing was measurable. The aggregate is VOID — not perfect, not zero.</summary>
    public bool Void => Measured == 0 && Total > 0;

    /// <summary>Extreme values are wiring faults until proven otherwise, in BOTH directions.
    /// NotApplicable == 0 across a suite is as suspicious as == Total: an inapplicability ledger
    /// that reads clean is usually a ledger nothing writes to.</summary>
    public bool ExtremeAndUnexamined => Total > 0 && (NotApplicable == 0 || NotApplicable == Total);
}
```

Rendering rules, normative for every renderer:

- `NotApplicable` renders **`n/a` plus the reason** — never `0.00`, never a blank cell, never a dash.
- Every mean prints its denominator: **`0.62 (8 of 12 measured, 3 n/a, 1 not measured)`**. A bare
  `0.62` is not renderable.
- `Census.Void` renders **`VOID — nothing measurable`**, not `0.00`.

**Blocking sub-defect (D13), must be fixed with this:** `EvalResult.Skipped` puts its reason in
`Details.Recommendations`, not `Details.Summary` (verified: `EvalResult.cs:20`,
`Details: new(null, null, new[] { reason }, null, null)`). Any renderer reading `Summary` prints a
bare `n/a` with no reason — the blank cell the rule exists to forbid. Write the reason to both.

### 4.3 Chance floors — four static functions and a plain record

No `IChanceFloor` interface, no `FloorGatedCodeEval`, no `PoolMismatch`. See §7 for why all three
were cut.

```csharp
namespace AgentEval.Evals.Meta;   // BCL-only

/// <summary>Whether a chance floor exists, and if not, why not. Never collapses to a number.</summary>
public enum FloorState { Derived = 0, NotDerivable = 1 }

/// <summary>
/// What an arm that understands nothing scores. Derived from the corpus and the arm's DECLARED
/// budget; it never sees a measurement.
/// </summary>
/// <remarks>
/// <b>An absent floor is not a zero floor.</b> <see cref="Value"/> THROWS when the floor is not
/// derived, so a caller cannot average an absence into a mean. A chance floor ABSENT is not zero —
/// that is how a metric gets condemned at p = 0.70.
/// <para>
/// <b>An estimated floor carries its own uncertainty.</b> Analytic floors leave
/// <see cref="IntervalHigh"/> null. Empirical floors carry a Clopper-Pearson upper bound, and
/// comparisons must clear THAT, not <see cref="Value"/> — comparing an observed rate to a point
/// estimate computed from the same corpus is the co-moving-operands failure.
/// </para>
/// </remarks>
public sealed record ChanceFloor(
    string Kind,                 // "hypergeometric-at-least-one" | "hypergeometric-avoids-all"
                                 // | "uniform-choice" | "prior-rate" | "empirical-policy" | "not-derivable"
    FloorState State,
    double RawValue,
    double? IntervalHigh,        // null when exact
    int Draws,
    int PoolSize,
    string Derivation)           // one sentence naming the pool, the favourable set and k. Never empty.
{
    public double Value => State is FloorState.Derived
        ? RawValue
        : throw new InvalidOperationException($"Chance floor not derived: {Derivation}");

    /// <summary>The number a comparison must clear: the interval's upper bound when estimated.</summary>
    public double ComparisonBar => IntervalHigh ?? Value;

    // ── factories; there is no public ctor path that skips Derivation ────────────────────────
    public static ChanceFloor AtLeastOneHit(int poolSize, int favourable, int draws);
    public static ChanceFloor AvoidsAll(int poolSize, int forbidden, int draws);
    public static ChanceFloor UniformChoice(int alternatives);
    public static ChanceFloor PriorRate(int positives, int total);

    /// <summary>
    /// A floor MEASURED by running an input-blind policy. <paramref name="policiesConsidered"/> is
    /// MANDATORY: "the best constant policy" is a MAXIMUM over a family, and a maximum selected on
    /// the same corpus the agent is scored on is optimistically biased. With more than one policy
    /// considered, <paramref name="heldOutFrom"/> must name the split the constant was chosen on,
    /// or the call throws. The Galaxus ceiling was TYPED as 8 and MEASURED at 10 — selection over a
    /// family on the scored corpus is how you get 10.
    /// </summary>
    public static ChanceFloor Empirical(int successes, int trials,
                                        int policiesConsidered, string? heldOutFrom = null);

    /// <summary>Not derivable. <paramref name="reason"/> is mandatory and prints where the floor would have.</summary>
    public static ChanceFloor NotDerivable(string reason);
}
```

**`k` comes from the arm's DECLARED budget, never from its observed output.** This is the recorded
Galaxus defect: a deliberately implausible two-product dry-run stub read **above its own floor on 3
of 12 personas** while a real arm at the identical 0.333 read below at k=12 — *the arm sized its own
null*. Fixed-k is not the answer either (Nadia's floor is 0.129 @ k=1, 0.491 @ k=5, 0.655 @ k=8;
scoring a 12-item arm against a 5-item arm's floor is wrong the other way). The fix is **provenance
on k**, and it is a convention plus a review checklist, not a base class:

```csharp
/// <param name="DeclaredDrawBudget">k — from a prompt constraint, a tool schema maxItems, a config key.</param>
/// <param name="BudgetSource">Where the number came from. A k with no provenance is a k someone tuned.</param>
public sealed record ArmProfile(string ArmId, int DeclaredDrawBudget, string BudgetSource);
```

An arm that **exceeds** its declared budget is a control condition, not a floor question. Silently
re-deriving at the larger observed k is the defect.

**A floor must be TESTED, not point-compared.** A per-case `value > floor` is a disposition; the
suite runs the exact test:

```csharp
public sealed record FloorComparison(
    string ArmId, int Successes, int Trials,
    double FloorUsed,            // ChanceFloor.ComparisonBar — the interval bound when estimated
    bool FloorWasEstimated,
    double PValue,               // one-sided binomial upper tail against FloorUsed
    (double Low, double High) ObservedRate,
    int NotApplicableCases,
    double MinimumAttainableP)
{
    /// <summary>No observation at this n could have reached alpha.</summary>
    public bool UnderpoweredByConstruction => MinimumAttainableP > 0.05;

    /// <summary>A DIRECTION, never a verdict.</summary>
    public bool AboveFloor => PValue <= 0.05 && !UnderpoweredByConstruction;
}

public static FloorComparison CompareToFloor(IReadOnlyList<Observation> obs, ChanceFloor floor);
```

### 4.4 Exact tests — the best-shaped thing in this document

Five pure static functions. No ceremony, trivially testable, deterministic across machines.

```csharp
namespace AgentEval.Evals.Meta;   // BCL-only

public static class ExactTests
{
    /// <summary>
    /// Exact two-sided binomial p at p = 0.5: 2 * P(X >= max(wins, n-wins)), clamped to 1.
    /// Returns 1.0 when <paramref name="nonTied"/> is 0 — every case tied is "no detectable
    /// difference", never a win.
    /// </summary>
    /// <remarks>
    /// Accumulated in LOG space (exp(logC(n,i) - n*ln2)). The naive form divides a sum of binomial
    /// coefficients by Math.Pow(2, n), which is +Infinity past n ~ 1030 and returns NaN — silently,
    /// in the direction of "no result". The sample ships THREE independent binomial-coefficient
    /// implementations; this replaces all three.
    /// </remarks>
    public static double TwoSidedSignP(int wins, int nonTied);

    /// <summary>
    /// The smallest two-sided p this n could EVER produce: min(1, 2 * 0.5^n). Computed from the
    /// NON-TIED count, because that is the n the exact test runs on — using the full paired count
    /// understates it, since discarding ties costs power.
    /// </summary>
    public static double MinimumAttainableP(int nonTied);

    /// <summary>One-sided binomial tail against a FLOOR. This — not <c>rate &gt; floor</c> — is what
    /// "beats chance" means. Pass an estimated floor's upper bound.</summary>
    public static double BinomialTailP(int successes, int trials, double floor);

    /// <summary>Exact (Clopper-Pearson) interval by bisecting the exact tail sums, log-space pmf.</summary>
    public static (double Low, double High) ClopperPearson(int successes, int trials, double alpha = 0.05);

    /// <summary>
    /// The one-sided upper bound given <paramref name="events"/> events in <paramref name="trials"/>:
    /// 1 - alpha^(1/n), the exact form of the "rule of three".
    /// </summary>
    /// <remarks>
    /// <b>There is deliberately no overload taking only <paramref name="trials"/>.</b> The rule holds
    /// ONLY at zero events; the recorded bug was calling it with a CLEAN-CASE count and printing a
    /// "95% upper bound of 34.8%" beside an OBSERVED defect rate of 50%. A bound below its own
    /// observation is not a bound, and it failed in the flattering direction. Requiring the event
    /// count makes the misuse unspellable: when events > 0 the result is NotApplicable and carries
    /// the Clopper-Pearson interval around the observed rate instead.
    /// </remarks>
    public static ZeroEventBound ZeroEventUpperBound(int events, int trials, double alpha = 0.05);
}

public sealed record ZeroEventBound(
    bool IsApplicable, double? UpperBound, (double Low, double High)? ObservedRateInterval, string Reason);
```

**No new `ConfidenceInterval` type.** `ClopperPearson` returns a named tuple. The shipped
`AgentEval.Comparison.ConfidenceInterval(Lower, Upper, Level)`
(`src/AgentEval.Abstractions/Comparison/StochasticResult.cs:228`) stays the one interval type in the
library; renderers wrap the tuple. The meta lane's proposed `(Low, High, Alpha, Method)` would have
been a **binary and source break** on a shipped positional record, contradicting its own
non-breaking premise. If `Method` is wanted for rendering, add it to the shipped record as a
non-positional init-only property.

### 4.5 Paired comparison and the unit of analysis

```csharp
namespace AgentEval.Evals.Meta;   // BCL-only

/// <summary>
/// How N repetitions of the same (case, arm) become ONE observation.
/// </summary>
/// <remarks>
/// <b>The unit of analysis is the case, not the rep.</b> Treating 3 reps of 12 cases as 36
/// independent observations adds no information — the reps share a case, a prompt and a corpus row —
/// but it shrinks every standard error by sqrt(3) and every p-value with it. The collapse is
/// MANDATORY and its strategy is DECLARED, because different strategies encode different claims and
/// the flattering one must be visible in the code.
/// </remarks>
public enum RepCollapse
{
    Mean = 0,
    Median = 1,       // robust to one pathological rep (a timeout, a truncated stream)
    All = 2,          // "it does this every time" — a reliability claim
    Majority = 3,

    /// <summary>Any rep passed. <b>The flattering strategy</b> — it measures best-of-N, which is a
    /// different claim, and it rises with N for free. Permitted, always labelled "best-of-N".</summary>
    Any = 4,
}

public sealed record ObservationUnit(int Cases, int TotalReps, double MeanRepsPerCase, RepCollapse Strategy)
{
    /// <summary>sqrt(mean reps per case) — the factor by which standard errors would have been
    /// understated had reps been counted as independent. A number on the page, not a paragraph in a
    /// design doc.</summary>
    public double PseudoReplicationInflation => Math.Sqrt(MeanRepsPerCase);
}

public sealed record PairedComparison(
    string Reference, string Challenger,
    int Wins, int Losses, int Ties,
    double PValue,
    double MinimumAttainableP,
    double MeanDelta,
    ObservationCensus Census,
    ObservationUnit Unit,
    string? RuleHash)            // the pre-registered rule that was in force, stamped for diffing
{
    public int EffectiveN => Wins + Losses;
    public bool UnderpoweredByConstruction => MinimumAttainableP > 0.05;

    /// <summary>A DIRECTION, NOT A RESULT. Must never gate a build.</summary>
    public bool ChallengerLeads => Wins > Losses;
}

public sealed class PairedEvalComparer
{
    public PairedEvalComparer(RepCollapse collapse);

    public void Record(Observation observation);        // reps accumulate; collapsed at Compare time
    public void DeclareAbsent(string armId, string reason);   // declared-absent RENDERS; missing does not
    public PairedComparison Compare(string reference, string challenger);
}
```

Rules baked in, each with a recorded reason:

- **Ties discarded and counted**; `PValue = 1.0` when everything ties.
- **`MinimumAttainableP` from the non-tied n**, so an underpowered comparison says so instead of
  quoting a p as though it could have been significant.
- **Reps collapse before pairing.** There is no code path that pairs raw reps.
- **A `NotApplicable` observation on either side excludes the pair** and increments
  `Census.NotApplicable`. It is never a loss and never a tie.
- **Bootstrap is not shipped.** The sample suppresses it below n=6 for a good reason (*"a percentile
  bootstrap over three deltas resamples three numbers and reports their spread, which is not a
  confidence interval for a population"*); at the n's an eval suite works at, Clopper-Pearson is
  exact and the bootstrap adds a seed to record and nothing else.

**On pre-registration.** `PreRegisteredRule` with a `RegisteredAt` timestamp and a
`RegisteredAfterData` verdict is **cut**. It detects only within-process ordering; the failure it
claims to prevent — a rule written after the author saw the numbers — happens between runs, in an
editor, and is invisible to it. A gate that catches the accident and not the incentive provides
false assurance. What survives is the part that works: **a `RuleHash` stamped into the persisted
artefact**, so a rule change is visible in a diff across runs, which is the only place it is
actually detectable.

### 4.6 The rule, enforced

```csharp
[Fact]
public void MetaTypes_NeverImplement_IEval()
{
    var offenders = typeof(Observation).Assembly.GetTypes()
        .Concat(typeof(EvalResult).Assembly.GetTypes())
        .Where(t => t.Namespace?.StartsWith("AgentEval.Evals.Meta", StringComparison.Ordinal) == true)
        .Where(t => typeof(IEval).IsAssignableFrom(t))
        .ToList();

    // A floor is a property OF a comparison. A control is a RUN OF an eval. A comparison is a
    // FUNCTION OF results. The moment one returns EvalResult as its OWN verdict, AgentEval has a
    // seventh result model and it is the one holding pass/fail authority.
    Assert.Empty(offenders);
}
```

### 4.7 Case identity — two properties, without which half of this is unimplementable

Verified: `EvalInput` has **no** `Id`. `TestCase` has `Name`, not an id — and the Galaxus harness
sets `Name = $"{c.Id} — {c.Group}"`, i.e. **a display string used as a join key**. Yet
`Observation.CaseId`, `PairedEvalComparer.Record`, `FloorComparison` and any shuffled-gold control
all require stable per-case identity.

```csharp
// src/AgentEval.Abstractions/Evals/EvalInput.cs   — non-positional init-only, additive
public string? CaseId { get; init; }

// src/AgentEval.Abstractions/Models/EvaluationModels.cs — TestCase is a plain class
public string? Id { get; set; }
```

---

## 5. THE GALAXUS MIGRATION — THE WORKED PROOF, AND THE LINE DELTA THAT DOES NOT FLATTER

### 5.1 Under the FULL four-lane design: the sample shrinks 13.7% and the library grows more

| area | before | after | Δ |
|---|---|---|---|
| `Graders/` | 2,219 | 1,520 | −699 |
| `Evals/` | 11,345 | 9,867 | −1,478 |
| root infrastructure | 2,275 | 1,945 | −330 |
| `Controls/` | 1,229 | 1,094 | −135 |
| `Adapters/` | 709 | 560 | −149 |
| `Cases/` + `Loop/` | 2,571 | 2,571 | 0 |
| **total** | **20,348** | **17,557** | **−2,791 (13.7%)** |

Against an estimated library growth of **~3,800 production lines (~7,300 with tests)**.

**Say it plainly: on line count alone, the full design loses.** A 13.7% shrink of one sample against
a 2.2% growth of a 170,619-line library is a weak trade if the second consumer never arrives, and
machinery with one consumer rots in about six months.

### 5.2 Under the FUNDED slices only: roughly break-even, plus five live bug fixes

Attributing the migration lane's per-cause table to Slices 0–2 (an **estimate**, derived by
attribution rather than measured by doing the migration — labelled as such):

| cause | Δ sample | in slice |
|---|---|---|
| statistics engine (sign test, Clopper-Pearson, rule-of-three, `MinimumAttainableP`, 3× binomial coefficient) | −248 | 2 |
| `IntegrityRunReport`'s statistics half | ~−90 | 2 |
| chance-floor combinatorics | −55 | 2 |
| applicability discipline (63 `double.IsNaN` guards, 55 `NaN` literals, 4 duplicate `Format(double)`) | −180 | 1 |
| approval-visibility adapter deleted | −149 | 0 |
| Eval09's per-criterion paired report | ~−190 | 2 |
| Eval08 binomial duplication | ~−30 | 2 |
| rep collapse | −32 | 2 |
| **estimated total** | **~−974 (≈4.8%)** | |

Against **~700 production lines** of library. **That is roughly break-even on lines, and it buys
five shipped defects fixed.** It is the honest trade and it is the one being proposed.

### 5.3 The argument that actually carries — the deleted lines are the wrong ones

Every defect the sample's own comments record as measured-and-fixed sits inside the deleted lines,
and ten of the twelve failed in the flattering direction:

| recorded defect | absorbed by |
|---|---|
| rule-of-three printed a **34.8% bound beside a 50% observation** | `ExactTests.ZeroEventUpperBound(events, trials)` — misuse unspellable |
| `tripped = d1==0 && d5>0 && (d3>0 \|\| d4>0)` — a dead D3 detector still prints a tick | deferred (controls); the *rule* is recorded here |
| constant-policy ceiling **typed 8, measured 10**; refuser typed 8, measured 5 | `ChanceFloor.Empirical(..., policiesConsidered)` |
| fixed-k floor: Nadia 0.129 @ k=1, 0.491 @ k=5, 0.655 @ k=8 | `ArmProfile.DeclaredDrawBudget` + `BudgetSource` |
| mean-vs-mean gate **passed** an arm scoring 0.000/1.000/1.000 while below floor on 2 of 3 | `FloorComparison` per case |
| a control criterion **clamped to 1.000** — a bar nothing could fail | deferred (controls) |
| score computed **before** `result.HasError` — an errored turn averaged in as 0.000 | `MeasurementState.NotMeasured` |
| `signTests[0]` positional index silently re-pointed a **gate** at a different comparison | `Compare(reference, challenger)` **by name** |
| `EvaluationFailed` discarded → judge score 50 read as a grade whenever `PassingScore <= 50` | Slice 0 item 2, **not optional** |
| `NeverCallTool("PlaceOrder")` chance floor **1.0** | Slice 0 item 5 |
| **"wahl"** (German: *election* AND *choice*) in the blocklist, tripping de-language personas | found **only** because a floor and a paired comparison existed |

And the duplication *inside a single sample*, counted rather than asserted: **3 independent binomial
coefficient implementations**; **6 chance-floor declarations with 0 shared type** (one of which,
`TrajectoryCase.ChanceFloor`, is a **`string`** — prose, not a number); **7 independent spellings of
`MeasurementState`**; **4 hand-rolled "both directions" controls**; **5 cost/token rollups**;
**4 assertion-to-result shims**.

`AgentEval.TravelDemo.Evals` is consumer #2, it already exists, and it is already wrong in the exact
way this design forbids: `EvalPrinter.cs:577-578` prints **`HYPOTHESIS CONFIRMED`** on
`workflow.LlmScore > agent.LlmScore || workflow.CriteriaMetCount > agent.CriteriaMetCount` — an OR of
two one-sided comparisons with no test of any kind. (Two more `HYPOTHESIS CONFIRMED` sites exist in
the same sample, at `EvalPrinter.cs:676` and `Eval01_TravelAgentEvals.cs:198`; they were not audited.)

**Sell it as "the machinery every consumer gets wrong stops being writable", not as "the sample gets
much smaller". If it is sold on line count it will disappoint.**

### 5.4 The one worked eval

`Eval01_CatalogueIntegrity.cs` is 599 lines and shrinks to ~507 (−92, 15%). The 507 that remain are
catalogue lookups, six defect classes, fourteen authored cases, persona prompts and console panels —
**Galaxus**. What leaves is AgentEval. Specifically:

- `RunFluentAssertions` (41 lines) returns a **`string?`**, produces **no record on a pass**, and
  returns `null` when the trace is null — making every prohibition **vacuously true**. Under the
  deferred assertions lane it becomes a scored result with a declared floor; under the funded slices
  it at minimum stops being invisible, because Slice 0 makes the extractor see approval-gated calls.
- `PrintDerivedFloors` (29 lines) is **nine hand-typed prose floors**, one of which carries the
  comment *"an earlier version of this line said 8, which was wrong by two in the flattering
  direction"*. It becomes 7 lines rendered from `ChanceFloor.Derivation`.
- `ApprovalAwareAgentAdapter` (149 lines) is **deleted entirely** — and that deletion is the check
  that Slice 0 item 5 landed at the right altitude.

---

## 6. CONSEQUENCES

### 6.1 What gets better

- Five shipped, flattering defects stop being shipped (§2.3).
- "Undecidable" becomes expressible. Today the only things that compile are `0.0` (reads as a
  genuine zero and averages as one) and `Skipped` (reads as "not run"). Both are wrong, and the
  `0.0` path fails in the *un*flattering direction, which makes it harder to notice and easier to
  defend as conservative. It is not conservative; it is wrong.
- The library gains the ability to say **"this eval could not have failed"** — which no competitor
  ships and which our own release history repeatedly needed.
- First-party M.E.AI evaluators stop entering through the second-class contract.

### 6.2 What gets worse, and the honest breaking-change list

**The non-breaking claim in the design lanes is false in three specific ways. All three are handled
by the funded plan; naming them is not optional.**

1. **Two proposed gates default ON.** `EnforceExpectedTools = true` and `OutputCheckPolicy.Always`
   would flip every dataset-driven consumer from pass to fail on a *minor* version:
   `AgentEval.Cli/Commands/EvalCommand.cs:284-285`, `samples/AgentEval.NuGetConsumer.Tests/*`
   (7 harness sites), `samples/AgentEval.Samples/*` (12+ sites), TravelDemo, PartnerDesk.
   *"Pass → fail only"* is not a safety argument — it is the definition of a breaking change for a
   test harness. **Ruling: both default OFF for one minor release, with a one-line warning printed
   when `ExpectedTools`/`ExpectedOutputContains` is present and unenforced. Flip at the next major.**
   Both are in the deferred bucket regardless.
2. **`TestResult.Score` would stop being binary on the deterministic path**, and the proposed
   exactness guarantee is dead by construction: the single-judge passthrough fires only when
   `real.Count == 1`, but the non-empty leaf never skips and output checks default on, so
   `real.Count >= 2` **always** and every judge score round-trips `87 → 0.87 → 86.999…`.
   `samples/AgentEval.NuGetConsumer.Tests/ResponseValidationTests.cs:53` asserts `Score >= 70` and is
   directly exposed. Deferred with the bridge; when it lands, compute `Score` from the judge leaf
   whenever a judge leaf exists, independent of leaf count.
3. **Schema v1 has `additionalProperties: false` on `score` and a closed `label` enum** — verified in
   `src/AgentEval.DataLoaders/Output/Schema/v1/eval-result.schema.json`. Adding `measurement` and
   `"inapplicable"` **is** a schema change. Enforcement is soft on the write path
   (`FileSystemOutputStore.cs:352-380` logs and writes anyway) and strict on `agenteval doctor`'s
   read path. **Every historical run's `ScenarioResult` content hash changes at this boundary**, and
   cross-version comparison in MissionControl silently splits unless the release note carries a
   byte-level prediction. **Ship exactly one schema change in this entire programme.** That is why
   `EvalScore.ChanceFloor` was cut (§3.2) — the budget is spent on `measurement`, which genuinely
   cannot be expressed any other way.

   *(Related: the contract lane says `provenance.type` is a closed enum and `"atomic-probe"` must not
   be invented; the assertions lane proposes `"atomic-code.assertions"` into the same closed enum and
   calls it additive. **Two lanes shipping opposite rules for one field is how a schema rots.**
   Ruling: the enum stays closed; sub-kind is carried in `Category`.)*

**Other honest negatives:**

- **Nothing is deleted.** 6 contracts before, 6 after. The count moves to 5 only if `IMetric` is
  eventually folded, and that is a **26-class** migration, not a 7-class one. Not funded.
- **Dashboards that count passes will see the number drop** when `NotApplicable` lands. Direction:
  pass → not-a-pass. Declared, and it is the correction, not a regression.
- **`MicrosoftEvaluatorAdapter`'s retarget is a breaking change** for anyone consuming it as
  `IMetric`. See §9 Q3.
- **A wrong floor is worse than no floor**, because it launders a pass into evidence. The sample
  proves the mechanism: a per-arm floor let a *deliberately implausible* stub read above it on 3 of
  12 personas, and the printed claim *"a stub that presents the same two products for every persona
  cannot beat a random draw"* was refuted **by the same run that printed it**.
- **A three-way split on where structural context lives** must be resolved before any code. The
  contract lane adds typed `EvalInput.Workflow`; the bridge lane routes it through
  `Metadata["__workflowExecution__"]`; the assertions lane states flatly that no slot exists.
  **Three lanes, three answers, one field.** In six months there will be evals reading each, and a
  run where the typed property is null because the producer filled the metadata key.
  **Ruling: typed properties win** (Eval07 reads `exec.Steps`, `exec.Graph.TraversedEdges` and
  `exec.RoutingDecisions` in one method — that is the evidence), **one lane owns `EvalInput`**, and
  the bridge stops proposing metadata keys for payloads that have properties. Not funded now; the
  ruling is recorded so it cannot drift.
- **Approvals: the bridge's shape wins over the contract lane's.** `ToolCallRecord.ApprovalState`
  joined to the call beats a parallel `EvalInput.ApprovalEvents` list re-paired by `Order` — Eval06's
  `CountApprovalGatedCalls` filters `!c.WasExecuted && CommitToolNames.Contains(c.Name)`, which needs
  the state on the call.
- **A control asserting a quantity nobody measures is the element-missing gate shape.** The proposed
  `NoModelNoCostControl` asserts `tokens_reported == 0` for arms with no judge model — but
  `DiscoveryModelCall` discards `response.Usage`, so `TestResult.Performance` reports **zero tokens
  for a turn that made seven model calls**. The control would pass vacuously on every arm.
  **The library cannot currently measure spend**, which is why the sample built
  `Eval09TokenLedger` + `MeteredChatClient`. No lane proposes fixing it. Recorded as a gap.

### 6.3 What gets harder

- Every renderer must print a denominator. That is a rule with no enforcement mechanism today,
  because **the library ships HTML and PDF renderers and no console renderer** — `EvalPrinter` is
  892 lines of the sample. Every consumer will re-implement "print the denominator" and half will
  forget.
- One more concept for contributors: `Measured` / `NotApplicable` / `NotMeasured` is a distinction
  people get wrong, and the wrong answer ("the agent said nothing, so mark it n/a") is the exact
  defect this exists to prevent.
- The meta module being BCL-only means adapters, and adapters mean a place for the two worlds to
  drift.

---

## 7. THE COUNTER-ARGUMENT, AT FULL STRENGTH — AND IT WINS HALF

> **"Don't build this. Depend on `Microsoft.Extensions.AI.Evaluation`. You already reference it."**

Stated properly, because it is the strongest objection and the ADR is worse if it strawmans it.

**Where M.E.AI wins outright.** Its `IEvaluator.EvaluateAsync(messages, response, chatConfiguration,
additionalContext, ct)` with `chatConfiguration` **nullable** is a *better-shaped* version of exactly
what this design is reaching for: one interface, deterministic and judged side by side;
`EvaluationMetric` subtypes (`NumericMetric` / `BooleanMetric` / `StringMetric` /
**`MetricWithNoValue`**); `EvaluationMetricInterpretation(Rating, Failed, Reason)`; `Diagnostics`
with severities; a metadata bag. **`MetricWithNoValue` + `EvaluationRating.Unknown` already express
most of `MeasurementState`.** It ships a result store, a response cache, a report generator and a
CLI. It is first-party, versioned by Microsoft, and this repo **already depends on it**
(`Directory.Packages.props:41`).

**So why not just depend on it? The honest answers, in order:**

1. **`IEval` already exists with 79 implementations, a JSON schema, an evaluator-card registry, a
   persistence layer, a CLI and MissionControl.** This ADR is not choosing a contract; it is
   declining to migrate 79 evaluators. **That is the real reason and it is stated as the real
   reason.** The four-point case for `IEval` (applicability, provenance, persistence, nesting) is
   accurate about AgentEval's current assets and is mostly *not* a comparison with M.E.AI.
2. **Composition.** M.E.AI has no `CompositeEval` / `IAggregationStrategy` /
   `EvalComponent(Required, Weight)` tree. For compliance work (`AgentEval.Compliance.Gdpr`,
   `.EuAiAct` — required-article rollup with severity propagation) that tree is load-bearing and
   would have to be rebuilt on top of M.E.AI anyway.
3. **The input model is a narrower win for M.E.AI than it first looks.** Its input is a chat
   transcript plus `EvaluationContext` subclasses. A workflow graph, routing decisions, approval
   events and retrieval provenance would ride in as context subclasses — a *typed* bag, better than
   AgentEval's `Metadata["__x__"]` string keys, but still the same architecture the contract lane
   proposed to fix with typed `EvalInput` properties.

**Two concessions, both recorded as rulings:**

- **The contract-unification half of this ADR spends a large budget to arrive at a contract that is
  not better than one already in `Directory.Packages.props`.** If this were greenfield, M.E.AI wins.
  That is why the unification half is **demoted from the headline to "bridge + bug fixes"** and why
  the word does not appear in the title.
- **`MicrosoftEvaluatorAdapter : IMetric` is a live bug against this ADR's own thesis** — first-party
  evaluators enter through the contract the design treats as second-class. Retargeting it to `IEval`
  is ~40 lines and is the single highest-leverage item in the programme for *"make everything work
  together"*. It is Slice 0 item 4.

**Where AgentEval genuinely wins, and it is the whole reason to proceed:** M.E.AI ships **no** chance
floors, **no** negative controls, **no** paired significance, **no** unit-of-analysis rule, and no
applicability-versus-silence distinction. Neither does anyone else. **That is the ADR.** And it is
precisely the part §4.1 insists must not be welded to `EvalResult` — because if it stays portable it
can be contributed upstream as `Microsoft.Extensions.AI.Evaluation.Meta`, which is a better outcome
for this project than owning a private copy.

**Verdict: the objection does not kill the ADR. It kills the ADR's unification half as a headline,
and it makes the neutral-tuple ruling (§4.1) non-negotiable.**

### 7.1 What the adversarial review refuted, and what was done about it

Recorded so the reversals are visible rather than quietly absorbed. Fifteen findings from the
adversarial review, of which **eleven changed the design**; plus **D2b**, added at ratification,
which refuted D2's own fix. Sixteen rows, twelve reversals.

| # | Finding | Ruling taken |
|---|---|---|
| D1 | `FloorGatedCodeEval` **does not prevent the defect it exists to prevent** — `FloorDerivationContext.Input` is a full `EvalInput`, so `int k = ctx.Input.ToolCalls!.Count(...)` spells the exact Galaxus defect inside `DeriveFloor`. Worse than the convention it replaces, because a reviewer would trust it. | **CUT.** Not in any funded slice. Rebuild only with a *redacted* context (`Query`, `GroundTruth`, `Context`, `ToolDefinitions`, `ExpectedActions`, `ArmProfile` — nothing the arm produced), and only if a second consumer asks. Until then: convention + review checklist, same protection, 200 fewer lines. |
| D2 | The `NotApplicable ⇒ !Passed` guard **does not compile** (CS0236, self-reference in a non-positional initializer) **and could never fire** (property initializers run before object initializers). | **FIXED, then the fix was itself refuted — see D2b.** The original ruling moved the guard to `EvalResult`'s primary constructor. **That ruling is SUPERSEDED**; the diagnosis was right and the placement was wrong. |
| **D2b** | **D2's own fix is insufficient.** The guard on `EvalResult`'s primary constructor is an *initializer*, which runs on the constructor path only: `result with { Score = aNotApplicableScoreClaimingPassed }` invokes the init accessor and an auto-property's accessor validates nothing. It is also on the wrong type — both operands of the invariant live on `EvalScore`, which is public and read directly by all five aggregation strategies, so an un-wrapped `EvalScore` was never covered. **Found at ratification by a compiled probe**, not by review. Same class as **AE-01** (`AssertionResult.Passed`) and **AE-08** (`EvalScore.Value`): both already found and fixed in this repo by the same reasoning, and the guard was written a third time in the form both fixes exist to forbid. | **FIXED (2026-09-05)** — the guard is now a private backing field plus a validating `init` accessor on **both** `EvalScore.Passed` and `EvalScore.Measurement`, so the clone path validates too. Full code and the 16-assertion probe result in §4.2. `EvalResult` carries no guard at all. **Standing rule this makes explicit: in this repo, a record invariant enforced by a property *initializer* is not enforced. Only an `init` accessor over a backing field is.** |
| D3 | Two verdicts in one artifact, free to disagree: the composite's `Threshold==null` path reads only severity, so an all-skipped composite is `passed:true, value:0.0` **today**. | **Slice 0 item 1** fixes the shipped bug. The bridge's competing verdict is deferred; when it lands, the harness reads the tree's verdict rather than recomputing. |
| D4 | The non-breaking claim is false in three ways (gates on by default; `Score` exactness dead by construction; schema `additionalProperties:false`). | **ACCEPTED** — §6.2, all three named, gates deferred and defaulted off when they land. |
| D5 | The meta layer is welded to `EvalResult`, foreclosing its only strong strategic option. | **ACCEPTED, non-negotiable** — §4.1, neutral `Observation` tuple, BCL-only. |
| D6 | A floor is **not** a field on a score (composites can't carry one; the number without derivation is unusable; it buys the only avoidable schema break). | **ACCEPTED** — `EvalScore.ChanceFloor` cut (§3.2). |
| D7 | `PoolMismatch` is the gate-self-examination shape *inside the design that exists to prevent it* — two free strings compared for equality, written by the same author ten lines apart; only a typo can trip it, and a typo **rejects a correct floor** while two genuinely different pools carelessly labelled alike match silently and flatteringly. | **CUT** — `PoolIdentity` and `FloorState.PoolMismatch` removed from §4.3. |
| D8 | `SealedRun<T>` + gating `Unwitnessed` will be switched off within a month; the first `NumbersDespiteVoid("CI needs a number")` makes the audit grep a permanent hit everyone scrolls past — **worse than nothing**, because the artefact still looks disciplined. | **CUT.** Voiding is reserved for a gating control that *ran and failed to trip* (a positive observation, never an absence), carried as a `MetaVerdict` field plus a non-zero exit code. |
| D9 | The empirical floor is a **selected maximum with no correction** — the ADR's own co-moving-operands failure one level up. | **FIXED** — `ChanceFloor.Empirical(..., policiesConsidered, heldOutFrom)`, throws when >1 policy without a held-out split (§4.3). |
| D10 | `PreRegisteredRule.RegisteredAt` detects only within-process ordering; the failure happens in an editor between runs. | **CUT** — `RuleHash` stamped in the artefact survives (§4.5). |
| D11 | **No case identity anywhere.** `EvalInput` has no `Id`; the sample joins on a formatted display string. Half the meta API is unimplementable without it. | **FIXED** — §4.7, Slice 1. |
| D12 | Controls conflate *perturbing a recording* with *running a different arm*; `ScrambledInputControl` needs an agent and the signature has none, so it is silently degenerate. | **ACCEPTED**, deferred with the controls. When built: `IObservationControl` (no agent) and `IArmControl` (re-runs) are two interfaces. |
| D13 | `EvalResult.Skipped` puts its reason in `Recommendations`, not `Summary` — so the mandated *"n/a plus the reason"* renders as a bare `n/a`. | **FIXED** — §4.2, Slice 1. |
| D14 | `ConfidenceInterval` already exists and does not match; the proposed shape is a binary+source break. | **FIXED** — no new interval type; `ClopperPearson` returns a tuple (§4.4). |
| D15 | ISP/LSP analysis is correct. Two residuals: `MetricEval.NormaliseKey` turns a **display name** into a persisted join key (rename a metric and every historical comparison silently splits); `AggregateWithCensus` as a default interface method is not callable through a concrete type. | **ACCEPTED** — require an explicit key; add the member to all five aggregations rather than using a DIM. Both deferred with their lanes. |

**Also refuted, by this document's own earlier draft:** the draft claimed *"No schema break;
`eval-result.schema.json` v1 already permits all four."* That is true for `threshold`/`dimensions`/
`evidence`/`provenance.type` and **false** for the new score properties and the new label. The
correction is §6.2 item 3.

---

## 8. IMPLEMENTATION PLAN

Ranked by (value × cheapness). **Slices 0–2 are what is proposed for funding. Everything after is
deferred until a second team asks**, and the three items the review found actually wrong (D1, D8,
D10) are all in the deferred bucket.

### Slice 0 — bug fixes. ~1 week. Zero new public types, no ADR needed, no schema change.

> **✅ DONE — shipped in `a396c5b4` (2026-09-04 21:29), before this ADR was ratified.** All seven
> items landed, and 0.4 landed as the **dual-target** `MicrosoftEvaluatorAdapter : IMetric, IEval`
> that §9 Q3 recommended. The table below is retained as the acceptance record; the "fails today"
> notes were true when written and are not any more. **The gate this slice was meant to sit behind —
> "nothing in §8 is funded until this document is ratified" — was authored and cleared in one
> commit.** That is recorded in §0.0 rather than dropped.

Each item fixed a defect that was **shipped and live**.

| # | Change | Acceptance criterion | The test that would prove it |
|---|---|---|---|
| 0.1 | `CompositeEval` all-skipped → `label:"skipped"`, not `"pass"` with `score 0.0` (`CompositeEval.cs:148-163`, `MinAggregation.cs:33`) | A composite whose every leaf is skipped reports `passed:false, label:"skipped"` | `AllLeavesSkipped_DoesNotReportPass()` — build a 3-leaf composite of `EvalResult.Skipped`, assert `label == "skipped"`. **Fails today.** |
| 0.2 | `MAFEvaluationHarness` must not pass on `EvaluationResult.EvaluationFailed`. **Non-optional, no opt-out.** | A judge returning `EvaluationFailed=true, OverallScore=50` yields `Passed=false` at any `PassingScore` | `JudgeParseFailure_NeverPasses()` — stub an `IEvaluator` returning the `ChatClientEvaluator` failure shape, run with `PassingScore=40`, assert `!Passed`. **Fails today.** |
| 0.3 | `ToolInputAccuracyEval`'s three `Build(1.0, true, "none")` (lines 116, 129, 214) → `EvalResult.Skipped(...)`. Version 1.0.0 → 2.0.0 | No `ToolDefinitions` yields `label:"skipped"`, not 1.0 | `NoToolDefinitions_DoesNotScorePerfect()`. **Fails today**, and line 129 currently ships evidence saying "schema validation skipped" beside a 1.0. |
| 0.4 | Retarget `MicrosoftEvaluatorAdapter` from `IMetric` to `IEval` (~40 lines) | A first-party M.E.AI quality evaluator produces an `EvalResult` with `Provenance.Type == "atomic-llm"` and a real `EstimatedCost` | `MicrosoftEvaluator_ProducesEvalResult()` |
| 0.5 | `MafToolUsageExtractor` reading `ToolApprovalRequestContent.ToolCall` (+ `ToolApprovalResponseContent` → `ToolCallRecord.ApprovalState`), **opt-in**, plus a **warning logged whenever the default extractor sees and drops one** | An approval-gated `PlaceOrder` appears with `WasExecuted=false, ApprovalState=Requested`; the default extractor logs rather than silently dropping | `ApprovalGatedCall_IsVisible()`. Opt-in because this is the one change that can turn a red test **green** (`NeverCallTool` can now fail, `MustCallTool` can now pass) — do not change anyone's numbers silently; make the blindness loud instead. |
| 0.6 | `IEvaluator` doc: "AI-powered response evaluation" → **LLM-judge transport**, with the `AtomicLlmEval` bridge named | Doc only | n/a |
| 0.7 | Stop the lie in `ScenarioResult.Assertions` — either populate it or delete the field. An always-`Array.Empty` slot in a persisted artefact is worse than an absent one. | The field is honest | `PersistedScenario_AssertionsAreNotFabricated()` |

### Slice 1 — applicability. ~1 week. **Exactly one schema change, ever.**

| # | Change | Acceptance criterion | Test |
|---|---|---|---|
| 1.1 | `MeasurementState` enum + `EvalScore.Measurement` init-only + `EvalScore.NotApplicable()` factory, **guard as a backing field + validating `init` accessor on both `EvalScore.Passed` and `EvalScore.Measurement`** — the AE-01/AE-08 pattern, §4.2 as folded 2026-09-05. **No guard on `EvalResult`.** | `EvalScore.NotApplicable() with { Passed = true }` throws, **and so does** `measuredPassingScore with { Measurement = MeasurementState.NotApplicable }` — the invariant is on the pair, so neither side can reach the bad state | `NotApplicableScore_CannotBePassed()` + a `…WithExpressionTests` file mirroring `EvalScoreWithExpressionTests.cs`, covering **both** `with` orderings, the object-initializer path, and a hostile `{"passed":true,"measurement":"NotApplicable"}` artifact failing deserialisation closed |
| 1.2 | `EvalScoreExtensions.CountsTowardAggregate()`; all five aggregations route through it (fixes the `CapByWorst` asymmetry) | One predicate, five call sites | `InapplicableLeaf_DoesNotEqual_ZeroScoredLeaf()` — a composite `{0.8, 0.8, n/a}` scores **0.80**; `{0.8, 0.8, 0.0}` scores **0.533**; assert not equal |
| 1.3 | `ObservationCensus` + the rule that no mean renders without its denominator | `0.62 (8 of 12 measured, 3 n/a, 1 not measured)`; `Census.Void` renders `VOID`, never `0.00` | `VoidAggregate_DoesNotRenderAsZero()` |
| 1.4 | Schema **v1.1**: `score.measurement`, `label` enum gains `"inapplicable"`. **Not `chanceFloor`.** `$id` bumped; `ContentHasher` canonical converter updated | v1.1 documents validate; the release note carries the **byte-level prediction** that every historical `ScenarioResult` content hash changes at this boundary | `SchemaV1_1_AcceptsInapplicable()` + a golden-hash test pinning the new value |
| 1.5 | `EvalResult.Skipped` writes its reason to `Details.Summary` as well as `Recommendations` (D13) | A skipped leaf renders `n/a` **with** its reason | `SkippedResult_ExposesReasonInSummary()` |
| 1.6 | `EvalInput.CaseId` + `TestCase.Id` (D11) | Both present, both nullable, both additive | compile-only + one round-trip test |

### Slice 2 — statistics. ~1 week. BCL-only, zero coupling.

| # | Change | Acceptance criterion | Test |
|---|---|---|---|
| 2.1 | `AgentEval.Evals.Meta` over `Observation`: `ExactTests` (5 functions), `RepCollapse` + `PairedEvalComparer`, `ChanceFloor` (5 factories + `NotDerivable`), `ObservationCensus`, `ObservationUnit` | The namespace references nothing outside the BCL; the architecture test in §4.6 passes | `MetaNamespace_HasNoNonBclDependencies()` + `MetaTypes_NeverImplement_IEval()` |
| 2.2 | Adapters `EvalResult → Observation`, `MetricResult → Observation`, `M.E.AI EvaluationResult → Observation`, in `AgentEval.Core` | One-way, per §3.2 | `Adapters_AreOneWay()` (reflection: no `Observation → EvalResult`) |
| 2.3 | `ExactTests` correctness | `TwoSidedSignP(8, 18)` matches R's `binom.test`; log-space form returns a finite p at n = 4,000 where the naive form returns NaN | `SignP_MatchesReference()` + `SignP_SurvivesLargeN()` |
| 2.4 | `ZeroEventUpperBound` misuse is unspellable | `ZeroEventUpperBound(events: 7, trials: 14)` returns `IsApplicable=false` with the observed-rate interval, **not** a bound below its own observation | `RuleOfThree_RefusesNonZeroEvents()` — this is the recorded 34.8%-vs-50% defect |
| 2.5 | `ChanceFloor.Empirical` refuses an uncorrected selected maximum (D9) | `Empirical(10, 14, policiesConsidered: 4)` with no `heldOutFrom` **throws** | `SelectedFloor_RequiresHeldOutSplit()` |
| 2.6 | **The stop rule.** Retrofit **exactly one** Galaxus eval — Eval 02, which has the mean-vs-mean floor gate that passed an arm scoring 0.000/1.000/1.000 (mean 0.667 > floor 0.462) while below floor on 2 of 3 | The retrofit **deletes** the hand-rolled sign test and the per-persona floor loop outright | If it does not, **the programme stops there.** That is the acceptance criterion. |

### Deferred until a second team asks

`FloorGatedCodeEval` (D1 — rebuild only with a redacted context), `INegativeControl` /
`ControlSuite` / `WitnessLedger` (D8, D12), `SealedRun<T>`, `PreRegisteredRule` (D10),
`MetricEval`, `ProbeEval`, `AssertionRecorder` / `AssertionEval`, `MAFEvaluationHarnessOptions`
and `EvaluationOptions.Evals`, typed `EvalInput.ToolUsage`/`.Workflow`/`.RetrievedDocuments`, and
all three CLI flags (`--require-controls`, `--require-floors`, `--strict-unit` — three flags for a
feature with zero adopters; ship none until one team asks).

Every one is defensible. None is urgent. Three of them are wrong as specified.

**The one thing worth saying about the deferred controls:** their highest-value first application is
`AgentEval.Compliance.*`, which is 100% `AtomicLlmEval` and **has never been shown able to fail**.
And the rule that must travel with them: **if the control suite finds nothing there, that is a wiring
fault in the control suite, not a clean bill of health** — extreme values are wiring faults until
proven otherwise — and the programme stops until the controls are shown able to fail.

---

## 9. OPEN QUESTIONS — RATIFICATION REQUIRED

Nothing in §8 starts before these are answered.

**Q1 — Is the demotion accepted?** This ADR says the unification half loses to
`Microsoft.Extensions.AI.Evaluation` on the merits and survives only on migration cost (§7). That is
a weaker claim than the brief's framing and it is deliberately weaker. **Confirm the honest framing
is wanted over the flattering one.**

**Q2 — Packaging of `AgentEval.Evals.Meta`.** §4.1 requires it to be BCL-only and portable so it can
be contributed upstream. Options: **(a)** a new BCL-only project `AgentEval.Meta` at the bottom of
the dependency graph, which `AgentEval.Abstractions` references — clean, but `AgentEval.Abstractions`
is `IsPackable=false` and **I have not traced how it reaches consumers**, so the packaging
consequence is unverified; **(b)** a namespace inside `AgentEval.Core`, giving up portability and the
upstream option. Recommend (a). **Needs a packaging check before it is chosen.**

**Q3 — `MicrosoftEvaluatorAdapter` retarget (Slice 0.4).** Hard break (`IMetric` → `IEval`), or keep
both with the `IMetric` path `[Obsolete]` for one minor? Recommend dual-target with `[Obsolete]`;
it costs ~20 lines and removes the only breaking change from Slice 0.

**Q4 — Schema v1.1 timing (Slice 1.4).** Accept the historical-content-hash break now, or hold
applicability until a v2 that batches other changes? Recommend now: the defect it fixes is live, and
batching means the break lands anyway but later and larger. **The release note must carry the
byte-level prediction either way.**

**Q5 — Slice 3 (negative controls): funded, or genuinely deferred?** The review's position is that
machinery with one consumer rots in six months and controls are the most ceremony-heavy part. My
position is that the `(d3 > 0 || d4 > 0)` defect — a dead detector printing a tick behind an OR — is
the single most valuable thing the controls prevent, and it is unwritable only if the API exists.
**These conflict. The user decides.**

**Q6 — The stop rule (Slice 2.6).** If the Eval 02 retrofit does not delete the hand-rolled sign
test and the per-persona floor loop, does the programme actually stop, or does it continue with a
recorded finding? Recommend it actually stops. **A stop rule nobody will honour is worse than no stop
rule.**

**Q7 — Does the exclusion list (§3.1) go into `docs/adr/030-*.md` as normative text**, so a PR adding
`contains` can be closed with a link, or does it stay advisory in `strategy/`? Recommend normative.

**Q8 — Unverified items to check before executing**, not decisions but they gate execution:
- the ecosystem sweep (§2.4) was **not re-run this session** — its per-framework evidence was folded
  into §2.4 in-repo on 2026-09-05, so the claim is now *checkable*, but it is still not *re-checked*;
- the funded-slice line delta (§5.2, ~−974) is an **attribution estimate, not a measurement**;
- `AgentEval.Abstractions`' packaging path (Q2);
- the `~3,800` full-design library-growth figure is the migration lane's estimate at this repo's
  density and was not independently derived.

---

## 10. WHAT WOULD CHANGE THIS DECISION

1. **`M.E.AI.Evaluation` (or Inspect, the only framework in the same tier statistically) ships chance
   floors or negative-control machinery.** The differentiation evaporates and we adopt theirs rather
   than compete. Today neither ships either.
2. **The Eval 02 retrofit does not delete the hand-rolled statistics** (Slice 2.6). Then the
   abstraction is wrong, not the sample.
3. **A real consumer needs `contains`/`regex`/schema gating at volume** and will not subclass
   `AtomicCodeEval`. Even then the answer is to depend on `Microsoft.Extensions.AI.Evaluation.NLP`
   and document the pattern — not to build the catalogue.
4. **The controls, when eventually run against `AgentEval.Compliance.*`, find nothing.** That is a
   wiring fault in the control suite, not a clean bill of health, and the programme stops until the
   controls are shown able to fail.
