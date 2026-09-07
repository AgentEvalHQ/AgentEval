# ADR-032: Benchmarks are definitions; runs bind subjects; scores are meta

- **Status:** **Accepted (2026-09-07).** The owner answered §6's Q-A — **inside the rule** — and
  Wave 2 was then BUILT rather than merely funded: `BenchmarkDefinition` / `AdmittedCheck` /
  `BenchmarkArm` / `CheckObservation` / `BenchmarkRun` (`c27bb45e`), `BenchmarkScore` (`821b41cd`),
  `BenchmarkRunner` + the `Metadata` refusal (`b340e829`), the `EvalJoin/02` sample (`e74dcc78`) and
  one in-repo benchmark converted (`375da551`). ADR-030's Q4(ii), Q5 and Q6 are answered too — see
  §6's answer table for what each answer refused, **in code and not only in prose**.
  <br/>⚠ Accepted describes the contract, not the stop rule: Q6 is *yes on the principle, staged in
  execution*, so `BenchmarkRunner` still applies **no** floor to any verdict.
  <br/>_Superseded status, kept because the reasoning still holds: **Proposed.** Proposed is a gate,
  not a placeholder (the ADR-026 / ADR-030 precedent). Accepting this document funds Waves 0 and 1 of
  §3.3 … Wave 2, the benchmark contract itself, is gated on one question only the owner can answer
  (§6, Q-A), and this document does not answer it._
- **Date:** 2026-09-07, written against `d563fd9d` on `joslat/digitec-galaxus`, tree clean
  (`git status --short` → 0 lines). No build, test or paid run was executed for this document. The
  test totals (net10 10,071/0/2; net9 and net8 9,853/0/1) are **carried** from the join wave's
  record; every other number below is a command run on that tree, and the command is beside it.
  Line references into ADR-030 and ADR-031 are to those files **as of `d563fd9d`**, before the
  pointer lines their §11 / §12 amendments (written with this ADR) insert.
- **Decision in one line:** **Aggregation stops demanding an `IEval` per weight (deleting four
  throwing stubs and one private copy); the floor-admission door refuses a composite and a double
  admission; the projection stops trusting a timeline derived from a blind report; three provenance
  labels stop naming a judge that graded nothing; and a deterministic benchmark is a *definition*
  (cases plus atomic checks, each admitted with its own floor), *run* by binding one arm to one run
  directory, and *scored* in the meta lane — reusing AE-04's join, `IOutputStore` and
  `agenteval compare` unchanged, with no registry entry, no CLI verb and no new package.**
- **Relates to:** [ADR-030](030-meta-evaluation-is-the-lane.md) (§3.2 the rule; §3.3 the six
  contracts; §11 the amendment recording the join and the census this builds on);
  [ADR-031](031-eval-packs-ship-reduced.md) (V1/V2 — why there is no definition hash; S5 — the
  comparer this reuses; §12 amendment); [ADR-017](017-unified-benchmarks-namespace.md) (Convention 2,
  which this supersedes; Convention 3, which this leaves as a catalog);
  [ADR-028](028-typedmemeval-acceptance-on-discrimination.md) (floors derive per arm, never from the
  arm's output).
- **Why a new ADR rather than an amendment:** it supersedes ADR-017 Convention 2 and rules on the fate
  of the four duck-typed benchmark families, which ADR-030 §3.4 lists as what that ADR is *not*. An
  Accepted ADR's correction table cannot host a decision the owner has not yet gated.
- **How it got here:** one synthesis over nine adversarial critiques — three design angles (minimal,
  clean, consumer-first) read by three independent readers — produced fourteen fatal objections. Each
  is listed in §4.2 with what was done about it. The design that survived is **smaller than any of
  the three that went in**: no contract merges, no runner collapse, no delegate wrapper, no registry
  hook, no definition hash, no packaging change. The word is *layering*, not *unification*.

---

## 0. IF YOU READ NOTHING ELSE

1. The tree already has one verdict contract (`IEval`), one subject layer (`IEvaluationHarness`, whose
   only implementer is `MAFEvaluationHarness`), and one meta layer (`AgentEval.Evals.Meta`, never a
   leaf). They are **layers**, and nothing in this document merges any of them.
2. The four benchmark families that pretend to be `IEval`s do so to satisfy an aggregation contract
   that reads **one `double`** off each component. Fix the contract and the pretence goes with it —
   four throwing stubs and one private copy, deleted, zero existing tests touched.
3. A benchmark is **data** (cases + atomic checks, each with its own floor), a **run** binds a subject
   to that data once and writes one run directory per arm, and the **score** is a meta-lane report
   that gates nothing — until the owner says a floor gates something, which is Q6 and is not answered
   here.

---

## 1. CONTEXT

### 1.1 The layers, measured

| Layer | Contract | Where | Measured on `d563fd9d` |
|---|---|---|---|
| verdict | `IEval` (`Key`, `Name`, `Category`, `Version`, `EvaluateAsync`) | `src/AgentEval.Abstractions/Evals/IEval.cs:11-23` | **79** files declare it — the strict grep `FloorAdmittedEval.cs:28` publishes (`grep -rlE "^\s*(public\|internal\|private\|protected)?\s*(sealed\s+\|abstract\s+\|static\s+\|partial\s+)*(class\|record\|struct)\b[^=]*[:,]\s*IEval\b" src --include=*.cs \| grep -v /obj/ \| wc -l`); a looser regex gives 80 |
| subject | `IEvaluationHarness.RunEvaluationAsync(IEvaluableAgent, TestCase, EvaluationOptions?, ct)` | `src/AgentEval.Abstractions/Core/IEvaluationHarness.cs:22-26` | **1** class reaches it: `MAFEvaluationHarness : IStreamingEvaluationHarness, IBatchEvaluationHarness` (`src/AgentEval.MAF/MAF/MAFEvaluationHarness.cs:16`; both sub-interfaces extend it, `IEvaluationHarness.cs:32`, `:52`). It takes `IEvaluator?` (`:18`, `:42`) and cannot run an `IEval` |
| meta | `AgentEval.Evals.Meta` | `src/AgentEval.Abstractions/Evals/Meta/` — 7 files (`ChanceFloor`, `ExactTests`, `MeasurementState`, `Observation`, `ObservationCensus`, `PairedEvalComparer`, `RepCollapse`) | never a leaf, enforced by namespace (`tests/AgentEval.Tests/Evals/Meta/MetaLaneArchitectureTests.cs:24`) and by reflection over every assembly (`tests/AgentEval.Tests/Evals/FloorAdmittedEvalTests.cs:336`) |
| join (AE-04) | `TestRunEvalProjection.ToEvalInput(this TestCase, TestResult)`; `AgentEvalBuilder.AddEval(IEval, ChanceFloor)`; `FloorAdmittedEval : IEval` | `src/AgentEval.Abstractions/Evals/TestRunEvalProjection.cs:152`; `src/AgentEval.Core/Core/AgentEvalBuilder.cs:151` (runner at `:356`, `Evals` at `:404`, `EvaluateEvalsAsync` at `:422`); `src/AgentEval.Abstractions/Evals/FloorAdmittedEval.cs:70` | `ChanceFloor` in **7** src files; intersection with the 79 = **1**, the door (`comm -12` of the two sorted lists) |
| judge | `IEvaluator` | `src/AgentEval.Abstractions/Core/IEvaluator.cs:8` ("the LLM-judge transport"); judge-only states at `:14-16` | stays (§4.1) |
| red-team | `IProbeEvaluator` | `src/AgentEval.RedTeam/RedTeam/Core/IProbeEvaluator.cs` | stays (§4.1) |
| metric | `IMetric` | `src/AgentEval.Abstractions/Core/IMetric.cs` | stays; the one-way bridge already exists (`src/AgentEval.Core/Evals/ObservationAdapters.cs:72`, `ToObservation(this MetricResult, …)`), and `MicrosoftEvaluatorAdapter : IMetric, IEval` (`src/AgentEval.Core/Adapters/MicrosoftEvaluatorAdapter.cs:40`) already dual-targets |

The join shipped in `10a94755` (2026-09-07 13:01) and `9078cab9` (14:19). The goal named it
`TestResult → EvalInput`; the types refuted it — `TestResult` carries no query — so the projection
takes the pair. `CaseId` is `TestCase.Id` and nothing else (`TestRunEvalProjection.cs:186`, rule at
`:60-65`).

### 1.2 The benchmark lane, and why four families duck-type

**Twelve** families register with `BenchmarkFamilyRegistry` — which lives in
`src/AgentEval.Core/Benchmarks/BenchmarkFamilyRegistry.cs`, not in the CLI. Counted from
`grep -rn 'evaluateAsync:\|runnerFactory:' src --include=*.cs | grep -v BenchmarkFamilyRegistry.cs`:
**4** registrations supply an `evaluateAsync:` delegate (Performance, MITRE, NIST, OWASP), **8** pass
`evaluateAsync: null`, **9** set `runnerFactory:`. The registry's ctor throws without a
`CompositeFactory` or a `RunnerFactory`+`RunnerType` (`:205-217`); `CompositeFactory` is
`Func<string, IEvaluator?, CompositeEval>?` (`:144`). The registry's `EvaluateAsync` delegate has
**one** caller in the tree, `src/AgentEval.Cli/Commands/BenchPerfCommand.cs:143`.

The four that supply a delegate are the four duck-typed classes:

| Class | `EvaluateAsync(EvalInput, ct) → EvalResult` at | base type | stub |
|---|---|---|---|
| `PerformanceBenchmark` | `src/AgentEval.Evals.Performance/PerformanceBenchmark.cs:470` | **none** (`:14`, `public class PerformanceBenchmark`) | `SyntheticEval : IEval` at `:748`, throws at `:761` |
| `OwaspBenchmarkRun` | `src/AgentEval.RedTeam/RedTeam/Compliance/OwaspBenchmarkRun.cs:128` | not `IEval` | `OwaspSyntheticEval : IEval` at `:326`, throws at `:342` |
| `NistBenchmarkRun` | `…/NistBenchmarkRun.cs:79` | not `IEval` | `NistSyntheticEval : IEval` at `:238`, throws at `:245` |
| `MitreBenchmarkRun` | `…/MitreBenchmarkRun.cs:132` | not `IEval` | `MitreSyntheticEval : IEval` at `:358`, throws at `:374` |

**The stubs have one root cause, and it is one line.** `IAggregationStrategy.Aggregate(results,
IReadOnlyList<EvalComponent>)` (`src/AgentEval.Abstractions/Evals/IAggregationStrategy.cs:14`) pairs
results with components by index, and the five strategies read **only `.Weight`**:
`grep -rn '\.Eval\b' src/AgentEval.Core/Evals/Aggregations/` → **0**; `'\.Required\b'` → **0**;
`'\.Weight\b'` → `WeightedMedianAggregation.cs:35-36,42`, `WeightedSumAggregation.cs:35-37`. The only
production caller is `CompositeEval.cs:117`. Each stub exists — its own comment says so — "to satisfy
`EvalComponent` constructor requirements". `PerformanceBenchmark` additionally carries a **private**
`CapByWorstAggregate` (`:717`) whose comment says it exists to avoid a Core dependency;
`src/AgentEval.Evals.Performance/AgentEval.Evals.Performance.csproj:12` references Core.

**What the ducks actually lack is a subject slot, not `: IEval`.** `EvalInput` carries a transcript;
these classes *drive* an agent. So the agent is smuggled through `EvalInput.Metadata["agent"]` —
**4** reads in `src/` (`PerformanceBenchmarkRegistration.cs:58`, `OwaspBenchmarkRun.cs:137`,
`NistBenchmarkRun.cs:84`, `MitreBenchmarkRun.cs:141`) plus **1** setter (`BenchPerfCommand.cs:119`),
and **13** sites in `tests/` (`MitreBenchmarkTests.cs` 6, `OwaspBenchmarkTests.cs` 5,
`NistBenchmarkTests.cs` 2). When the key is absent the result is a *skipped composite*
(`OwaspBenchmarkRun.cs:137-142` → `BuildSkippedComposite`, `:278`). The in-tree precedent for binding
a subject at construction, typed, is `AgentScenarioEval(IEvaluableAgent agent, string scenarioInput,
IEval inner)` (`src/AgentEval.Core/Evals/AgentScenarioEval.cs:39`; referenced in 7 src files).

**ADR-017 Convention 2 is unimplementable as written.** It requires the adapter to preserve "the
natural result type … in `Provenance`". `EvalProvenance` is a seven-field positional record with no
object slot — see the literal at `NistBenchmarkRun.cs:159`: `new("composite", …, null, null, null,
0.0, false)`. No family does what the convention says; the contract tests pin the `EvaluateAsync`
shape (ADR-017 §Verification), not the provenance slot.

### 1.3 Three flattering defects on the shipped join, found by the readers

**(a) The projection treats a timeline as a second recorder; on the real harness it is not.**
`TestRunEvalProjection.ProjectToolCalls` (`:253-277`) nulls a `ToolUsage` report whose
`DroppedApprovalRequestCount > 0` (`:257`) — correct — and then **falls through to `TestResult.Timeline`**
(`:264`) as though it were an independent record. The only producer of a timeline in `src/` is the
harness, and it builds the timeline **from the same report**: `MAFEvaluationHarness.cs:129` →
`PopulateTimelineFromToolUsage(timeline, result.ToolUsage)` (`:599`), attached at `:217`/`:246`
(`grep -rn '\.Timeline = ' src --include=*.cs` → 4 sites, all in that file). Under the default
`IncludeApprovalGatedToolCalls = false` (`IEvaluationHarness.cs:90`), a run whose only calls were
approval-gated yields a dropping report → `null` → an *empty* derived timeline → **`[]`, a measured
zero** on an absence question; with mixed calls, the visible ones return as the whole record.
`NamedSkuNotPresentedEval.cs:33-37` promises the opposite. The test at
`tests/AgentEval.Tests/Evals/TestRunEvalProjectionTests.cs:391`
(`ADroppingReportFallsThroughToATimeline_WhichIsADifferentRecorder`) pins the false premise. Neither
shipped sample reaches it (`tests/AgentEval.Tests/Evals/EvalJoinEndToEndTests.cs` uses no
approval-gated tool — `grep -c 'approval\|Approval'` → 0); the library contract does.
*Residual, not fixed here:* `RunEvaluationStreamingAsync` (`:257-460`) never records a drop count
(`sed -n '257,460p' … | grep -c 'DroppedApprovalRequestCount\|TrackTools'` → 0) and attaches its
timeline unconditionally at `:394` and `:453`; 0 callers in `src/` or `samples/`.

**(b) The door admits a door, and a composite.** `grep -n 'is FloorAdmittedEval\|SubResults'
src/AgentEval.Abstractions/Evals/FloorAdmittedEval.cs` → 0. `Admit` refuses null, an underived floor
and a bar outside `[0,1]` (`:128-160`); `Annotate` refuses a result that already carries a floor
(`:190-197`). It does not refuse `Admit(alreadyAdmittedDoor, otherFloor)` and it does not refuse a
`CompositeEval` root — and the three composite runners persist **leaves** and a root summary only
(`src/AgentEval.Compliance.Gdpr/Articles/GdprBenchmarkRunner.cs:71-74`, `:86`), so a root floor would
land on nothing in `results.jsonl` while satisfying the census.

**(c) Three provenance labels name a judge that graded nothing.** `JudgeModel: Judge is null ? null
: "owasp-judge-passthrough"` (`OwaspBenchmarkRun.cs:269`; `NistBenchmarkRun.cs:159`;
`MitreBenchmarkRun.cs:282`). `OwaspBenchmark.cs:84-88` documents the `judge` parameter as "a no-op
getter"; `BenchOwaspCommand.cs:85-90` resolves a judge on every CLI run anyway. `RunComparison`
gates the `judge` axis on presence (`src/AgentEval.Core/Output/RunComparison.cs:330`), so every
CLI red-team row on disk reads as *judged*. Three files under `.agenteval/` carry the literal (two
`owasp-smoke.json` scenario files in two run directories, one `report.html`) — a directory that is
**gitignored** (`.gitignore:453`), so those files exist only on the machine that produced them.

### 1.4 Reachability, packaging, release — nothing external can be tested yet

| Sample | `AtomicCodeEval` files | `.AddEval(` call sites |
|---|---|---|
| `samples/AgentEval.Samples` (`EvalJoin/01_EvalWithChanceFloor.cs`) | 1 | 1 |
| `samples/Galaxus.RecommendationAgent.Evals` (`NamedSkuNotPresentedEval.cs`, Eval 04's fifth check) | 1 | 1 |
| `samples/AgentEval.TravelDemo.Evals` | 0 | 0 |
| `samples/AgentEval.MafEvalLightPath`, `samples/AgentEval.MafEvalFoundryAlongsideLocal` | 0 | 0 |

(`grep -rl '\bAtomicCodeEval\b' <dir> --include=*.cs | wc -l`; `grep -rn '\.AddEval(' <dir> --include=*.cs | wc -l`.)

- **Packaging.** `AgentEval.Abstractions` and `AgentEval.Core` are `IsPackable=false`
  (`AgentEval.Abstractions.csproj:4`, `AgentEval.Core.csproj:5`) and ship *inside* the umbrella:
  `grep -c '<ProjectReference Include' src/AgentEval/AgentEval.csproj` → **13** (ADR-030 Q2 traced the
  route: `PrivateAssets="all"` + `IncludeSubProjectDlls`).
- **Release.** `Directory.Build.props:42` reads `0.34.0-beta`; the `v0.34.0-beta` tag is `40647d24` =
  `origin/main`; `git rev-list --count origin/main..HEAD` → **175**; `CHANGELOG.md:8` `[Unreleased]` is
  empty; `grep -c 'agenteval compare\|Incomparable\|exit 13' CHANGELOG.md` → **0**; `docs/*.md`
  outside `adr/` mention `AddEval`/`ToEvalInput`/`FloorAdmittedEval` in **0** files. **The join is in no
  published package.**
- **The one external consumer.** The GatekeeperDemo repository (`b3858c4`) pins
  `<PackageVersion Include="AgentEval" Version="0.28.0-beta" />` (`Directory.Packages.props:7`) through
  one `PackageReference` (`src/AgentEval.PartnerDeskDemo/AgentEval.PartnerDeskDemo.csproj:16`). Its
  `using AgentEval.*` lines: `MAF.Gatekeeper` 7, `Tracing` 2, `Testing` 1, `Guardrails.Gates` 1
  (`grep -rhoE 'using AgentEval\.[A-Za-z.]+;' --include=*.cs src | sort | uniq -c`). Its
  `PartnerDeskDemo.Evals` project names `IEval`, `IEvaluator`, `IMetric`, `EvalInput`, `TestResult` in
  **0** files; `RunMetrics.cs` declares **9** `required bool` members; its `ConcealmentJudge` is on
  `IChatClient` (`ConcealmentJudge.cs:42-45`). It is a parallel harness, and this ADR promises it no
  deletion (§2, D12).
- **The extensibility story in `docs/architecture.md` is refuted by the CLI.** `:1011` says "there are
  no hardcoded family lists anywhere"; `:1027` says "No changes to `src/AgentEval.Cli/` are required".
  `grep -c 'new Command(' src/AgentEval.Cli/Program.cs` → **27**; `BenchListCommand.AnchorAssemblies()`
  (`src/AgentEval.Cli/Commands/BenchListCommand.cs:79-89`) hard-codes nine assemblies; the only
  `Assembly.LoadFrom` in `src/` is `CalibrationGoldenAssembly.cs:45`, not a benchmark path. A
  consumer's family gets neither a `bench` subcommand nor a `--list` row unless it is already
  in-process.

### 1.5 The rule this design must not cross, and where it lives

*"The one thing that must not happen: AE-04 before AE-06"* is the owner's local plan's §2.4
(`strategy/Galaxus/MASTER_PLAN.md:1048` — gitignored, **not readable from this repository**; recorded
here by quotation because `grep -c 'AE-04' docs/adr/030-*.md` → 0 before ADR-030 §11). Its Phase 6
rows (`:1697-1707`) list 6.1 (`AddEval`) and 6.2 (the projection) — the two that shipped as AE-04 —
and 6.3 (*the harness runs an `IEval`*), which did not; `:688` places **all** of Phase 6 under the
user's decision, gated on Q6. The join honoured the rule's letter (no eval enters `AgentEvalRunner`
without a floor; 79 ∩ 7 = 1) and its bulk-wiring clause fully; its outcome clause ("no front-door
verdict without a bar") is honoured by recording, not by applying — `FloorComparison.Compute` has
**0** callers in `src/` outside its own file, and the only "read against the floor" is a console line
in the sample (`01_EvalWithChanceFloor.cs:192-195`). Whether *iterating* 6.1+6.2 over a definition's
cases is inside that rule is the owner's reading of the owner's rule, and it is §6 Q-A.

---

## 2. THE DECISION

### D1 — Layering, not merging. What is homogenised now, later, and never

**Now (Waves 0–1; one existing-test edit in total):**

| What | Into | Deletes | Loses |
|---|---|---|---|
| `IAggregationStrategy`'s need for an `IEval` per weight | `public static (double Score, string Severity) AggregateWeights(IReadOnlyList<EvalResult> results, IReadOnlyList<double> weights)` on each of `CapByWorst`/`MajorityVote`/`Min`/`WeightedMedian`/`WeightedSum`; the instance `Aggregate(results, components)` keeps its null and 1:1 checks and forwards `components.Select(c => c.Weight)` | `PerformanceBenchmark.SyntheticEval` (`:748`), `OwaspSyntheticEval` (`:326`), `MitreSyntheticEval` (`:358`), `NistSyntheticEval` (`:238`), perf's private `CapByWorstAggregate` (`:717-745`) | Nothing observable: no strategy reads `.Eval` or `.Required`. Perf inherits Core's `CountsTowardAggregate` rule — byte-identical on every input perf produces (`grep -c '"error"\|"inapplicable"' PerformanceBenchmark.cs` → 0), declared as a rule change |
| The projection's false premise (§1.3a) | `ProjectToolCalls`: a dropping report ⇒ `null`, **regardless of `Timeline`** | the premise of `TestRunEvalProjectionTests.cs:391` (renamed, assertion flipped to `Null` — the one test edit) | Nothing: no producer attaches a timeline from a different recorder. The streaming residual is declared in the projection's remarks, not fixed |
| Three phantom judge labels (§1.3c) | `JudgeModel: null` at `OwaspBenchmarkRun.cs:269`, `NistBenchmarkRun.cs:159`, `MitreBenchmarkRun.cs:282` | three lies | Disclosure in §3.2. The `IEvaluator? judge` ctor parameters stay (public API) until Wave 4 |
| The door's blind spots (§1.3b) | `Admit` throws on `eval is FloorAdmittedEval`; `Annotate` throws when the wrapped result has `SubResults` | — | Nothing: 0 tests admit a composite or a door |
| Five-positional-record boilerplate for the undecidable verdict | `protected EvalResult NotApplicable(string reason, EvalEvidence? evidence = null)` on `AtomicCodeEval` — provenance `"atomic-code"`, `Score = EvalScore.NotApplicable()` (`EvalScore.cs:163-164`), reason written to **both** `Summary` and `Recommendations` (ADR-030 D13, the `EvalResult.Skipped` precedent at `EvalResult.cs:18-30`) | — | Nothing; the record path stays. Named `NotApplicable`, not `Undecidable`: both shipped consumers declare `private EvalResult Undecidable(string)` (`01_EvalWithChanceFloor.cs:294`, `NamedSkuNotPresentedEval.cs:180`) and a base member of that name is CS0108 |
| Projection rules unreachable without a `TestResult` | `TestRunEvalProjection.ToToolCall(ToolCallRecord)` = today's private `FromRecord` (`:279`) made public | — | Nothing. String factories would lose the `notExecuted \| failure \| recorded result` composition (`:291-299`); taking the record keeps it |

**Later, and what would have to be true first:** promoting `AggregateWeights` onto
`IAggregationStrategy` (one test fake edit: `CompositeEvalsServiceExtensionsTests.cs:14`); splitting
the four ducks into a harness half plus admitted checks (13 test sites + ADR-017 Convention 2
superseded — Wave 4); `MetricEval` (ADR-030 §8: when a second team asks; none has);
`AgentEvalCompositeEvaluator` (`src/AgentEval.MAF/Evaluators/AgentEvalCompositeEvaluator.cs:38`,
ctor `(IEval composite)` at `:44`, its own two-field `EvalInput` at `:74`, 9 referencing files) taking
a floor — that is Q6, because a floor recorded and unapplied at the MAF door is the same shape as at
`AgentEvalRunner`.

**Never, with the code reason (settled by ADR-030 §3.3 and re-verified by every reader):**
`IEvaluator` (`IEvaluator.cs:14-16`: `EvaluationFailed` and the token counts are judge-only states a
deterministic implementation can never truthfully enter); `IProbeEvaluator` (a three-valued `Outcome`
the combinators branch on, inverted polarity as a type, `IAttackType` ownership); `IMetric`
(`MetricResult` has no undecidable state; the observation bridge already exists); harness-vs-eval
(layers — `ToEvalInput` is the seam, and the four ducks are what merging them looks like); the meta
lane as a leaf (a sibling floor bypasses `Annotate`'s self-supplied guard at `FloorAdmittedEval.cs:190-197`,
`WeightedSum` would average the instrument into the subject, `ObservationCensus` would count itself);
`BenchmarkFamily` as an execution contract (D11); multi-turn attacker campaigns (no `TestCase` shape);
the CLI as a consumer's entry (it cannot construct their agent).

### D2 — The benchmark contract

```csharp
namespace AgentEval.Benchmarks;   // AgentEval.Core, ships inside the umbrella. Type names end in Definition/Check/Arm/
                                  // Runner/Run/Score — never "Benchmark" — so BenchmarkNamespaceContractTests
                                  // (EndsWith("Benchmark"), tests/…/BenchmarkNamespaceContractTests.cs:131) does not
                                  // enumerate them. AdmittedCheck is a record, not an IEval, so FloorAdmittedEvalTests'
                                  // census (:336) still finds exactly the door.

// ── DEFINITION — data. No subject, no judge, no store. ────────────────────────────────────────

/// One check = one ATOMIC IEval + the floor it is admitted with. Admission is the existing door:
/// FloorAdmittedEval.Admit refuses null, a floor with no derivation, a bar outside [0,1]
/// (FloorAdmittedEval.cs:128-160) and — after D4 — a double admission and a composite.
/// The floor is a statement about the ARM (one p for the binomial tail, ChanceFloor.cs:325-326),
/// derived from the definition's corpus and the arm's declared draw budget (ADR-030 §4.3 ArmProfile),
/// never from the eval or the result.
public sealed record AdmittedCheck(IEval Eval, ChanceFloor Floor)
{
    public FloorAdmittedEval Admit() => FloorAdmittedEval.Admit(Eval, Floor);
}

public sealed record BenchmarkDefinition(
    string Key,                              // human identity; rides in the manifest's Harness string (interim, D3)
    string Version,                          // bump when Cases or Checks change; a bump is a re-baseline
                                             // (ADR-031 §1.3's rules, re-homed here — the version is documentation,
                                             // the per-scenario facts `compare` gates on are the mechanism)
    IReadOnlyList<TestCase> Cases,           // existing type (EvaluationModels.cs:30 `Id`). Id REQUIRED and unique —
                                             // the case is the unit of analysis (ADR-030 §4.5; D11: CaseId is TestCase.Id)
    IReadOnlyList<AdmittedCheck> Checks)     // atomic only; keys unique (AgentEvalBuilder.AddEval already refuses a duplicate)
{
    // ctor: Cases non-empty; every Id non-blank and unique; Checks non-empty. Nothing else —
    // no content hash (ADR-031 V2; nothing reads one), no Controls slot (Q5).
}

// ── RUN — the only place the subject lives. ONE run per (arm, rep). ───────────────────────────

/// An arm is a name and a way to observe one case. The subject is bound HERE, typed, once — never in
/// EvalInput.Metadata (the four ducks' TryGetValue("agent") is what this replaces).
public sealed record BenchmarkArm(string ArmId, Func<TestCase, CancellationToken, Task<EvalInput>> Observe)
{
    /// MAF-hosted: the harness drives the agent, AE-04's projection makes the EvalInput. Options are
    /// the harness's own defaults unless the caller says otherwise — IncludeApprovalGatedToolCalls
    /// (IEvaluationHarness.cs:90) is the one switch that turns a null ToolCalls into a record, and it
    /// stays reachable; hiding it would make every check on a gated agent undecidable with the fix
    /// out of the caller's reach.
    public static BenchmarkArm FromHarness(
        string armId, IEvaluationHarness harness, IEvaluableAgent agent, EvaluationOptions? options = null)
        => new(armId, async (c, ct) => c.ToEvalInput(await harness.RunEvaluationAsync(agent, c, options, ct)));

    /// A consumer with its own runner supplies its own Observe. Its contract is the projection's,
    /// documented not exported: ToolCalls == null means no recorder or a blind one; [] means a complete
    /// recorder saw nothing; TestRunEvalProjection.ToToolCall(record) gives it the same failure /
    /// not-executed markers the shipped evals filter on (ToolErrorResultPrefix :104,
    /// ToolNotExecutedResultPrefix :134). Data records may travel in Metadata; an agent, a client or a
    /// delegate may not (D10).
    public static BenchmarkArm From(string armId, Func<TestCase, CancellationToken, Task<EvalInput>> observe) => new(armId, observe);
}

public sealed class BenchmarkRunner
{
    public BenchmarkRunner(IOutputStore store, SubjectIdentity subject);

    /// definition × ONE arm → ONE run directory. Reps are further calls sharing parentInvocationId.
    public async Task<BenchmarkRun> RunAsync(
        BenchmarkDefinition definition, BenchmarkArm arm, string? parentInvocationId = null, CancellationToken ct = default)
    {
        // 1 · Admit every check BEFORE any case is observed. The door is the only path in:
        //     AgentEvalBuilder.AddEval(IEval, ChanceFloor) (AgentEvalBuilder.cs:151) has no floorless overload.
        var builder = new AgentEvalBuilder();
        foreach (var check in definition.Checks) builder.AddEval(check.Eval, check.Floor);
        AgentEvalRunner runner = await builder.BuildAsync(ct);

        // 2 · Manifest. Kind "benchmark" is in the closed enum (manifest.schema.json:44). The definition's
        //     identity rides in the Harness string for a human reader; no tool reads it and no schema changes
        //     (a typed field is Q4(ii): manifest.schema.json is additionalProperties:false at 7 sites).
        var manifest = await _store.StartRunAsync(_subject, new RunContext(
            EvalProject: definition.Key, EvalProjectPath: "",
            Harness: $"BenchmarkRunner/{definition.Key}@{definition.Version}",
            Seed: null, ParentInvocationId: parentInvocationId, Kind: "benchmark"), ct);   // IOutputStore.cs:86-92

        var observations = new List<CheckObservation>();
        try
        {
            foreach (var c in definition.Cases)
            {
                // 3 · Observe (the subject layer), then stamp identity from the case, never from a name.
                var input = (await arm.Observe(c, ct)) with { CaseId = c.Id };

                // 4 · Grade through the admitted evals. Each result's root carries Dimensions["chance_floor"] +
                //     Evidence("chance-floor", …) — ADR-030 §3.2's convention, read back by ComparabilityOf
                //     (EvalResultPersistence.cs:148).
                var results = await runner.EvaluateEvalsAsync(input, ct);

                for (int i = 0; i < results.Count; i++)
                {
                    var check = definition.Checks[i]; var r = results[i];
                    // 5 · One row per (case, check). The id is unique within the run (RunComparison.cs:287-291
                    //     refuses a duplicate) and identical across runs of the same definition, so
                    //     `agenteval compare` pairs baseline-arm rows with candidate-arm rows by construction.
                    //     StimulusHash from c.Input (ADR-031 S2).
                    await _store.WriteScenarioResultAsync(manifest.Run.RunId,
                        EvalResultPersistence.ToScenarioResult(r, $"{c.Id}·{check.Eval.Key}", check.Eval.Name,
                            input: c.Input, subjectModel: input.SubjectModel), ct);          // :57-63
                    observations.Add(new CheckObservation(check.Eval.Key, r.ToObservation(c.Id!, arm.ArmId), r));
                }
            }
        }
        finally
        {
            // 6 · Complete the run even when observe or a check threw: no orphan manifest.
            await _store.CompleteRunAsync(manifest, BuildSummary(observations, manifest.Run.RunId), ct);
        }
        return new BenchmarkRun(manifest.Run.RunId, arm.ArmId, definition, observations);
    }

    /// The existing pass/fail semantics, no floor applied, over the schema's closed buckets
    /// (summary.schema.json:11-22; RunStats(Total, Passed, Failed, Warnings, Skipped) at RunSummary.cs:23):
    ///   passed   = Measured ∧ Passed;  warnings = label "warn";  failed = Measured ∧ ¬Passed ∧ label ≠ "warn";
    ///   skipped  = ¬Measured  (NotApplicable and NotMeasured share the one bucket the schema has — finer is Q4(ii)).
    ///   verdict  = FAIL if failed > 0 · PENDING if nothing was measured ("no verdict"; NOT ADR-031 S4's VOID: no
    ///              exit code, no control ledger — Q5) · WARN if warnings > 0 · else PASS.
    /// This is the stance the door and `compare` already take — the floor is recorded, never applied (Q6) —
    /// and the rule AgenticBenchmarkRunner.cs:102-111 already uses.
    private static RunSummary BuildSummary(IReadOnlyList<CheckObservation> observations, string runId);
}

public sealed record CheckObservation(string CheckKey, Observation Observation, EvalResult Result);
public sealed record BenchmarkRun(string RunId, string ArmId, BenchmarkDefinition Definition, IReadOnlyList<CheckObservation> Observations);

// ── SCORE — meta lane only. Nothing here is an IEval, returns an EvalResult, or writes Passed. Reports facts. ──

public static class BenchmarkScore
{
    /// One arm vs its floor, per check. Pass ALL runs (reps) of that arm. Reps are collapsed per case with
    /// ObservationUnit.Collapse(repValues, collapse, passAt) (RepCollapse.cs:76) to the 0/1 outcome
    /// FloorComparison.Compute requires (ChanceFloor.cs:345-353 throws otherwise). A floor at 1.0 yields
    /// PValue NaN (ExactTests.cs:114) and AboveFloor false (ChanceFloor.cs:291) — undecidable, never a pass.
    public static IReadOnlyList<(string CheckKey, FloorComparison Comparison)> AgainstFloor(
        IReadOnlyList<BenchmarkRun> runsOfOneArm, RepCollapse collapse = RepCollapse.All, double passAt = 1.0);

    /// Reference arm vs challenger, per check, the case as unit: new PairedEvalComparer(collapse, passAt)
    /// (PairedEvalComparer.cs:108), Record(...) every observation of both arms (:119), Compare(reference, challenger).
    public static IReadOnlyList<(string CheckKey, PairedComparison Comparison)> AgainstReference(
        IReadOnlyList<BenchmarkRun> reference, IReadOnlyList<BenchmarkRun> challenger,
        RepCollapse collapse = RepCollapse.All, double passAt = 1.0);

    /// Census per (check, arm). Void ⇒ nothing measured (ObservationCensus.cs:30). A fact; the verdict enum is the owner's (Q5).
    public static IReadOnlyList<(string CheckKey, ObservationCensus Census)> Census(IReadOnlyList<BenchmarkRun> runsOfOneArm);
}
```

**On disk, unchanged.** `agenteval compare --baseline <run of arm A, rep 1> --candidate <run of arm B,
rep 1>` pairs on `case·check`, gates on `evalKey / evalVersion / effectiveBar / judge / judge.modelId /
judge.rubricDigest / stimulus` (`RunComparison.cs:155`, `:322-339`), and warns when no usable floor was
recorded (`CompareCommand.cs:245`). Zero CLI changes; no new verb; no registry entry — `bench --list`
will not show a consumer's definition, because the CLI cannot construct the consumer's agent (§1.4).

**New public surface, total:** 2 records (`AdmittedCheck`, `BenchmarkDefinition`), 1 record
(`BenchmarkArm`) + 2 factories, 1 class (`BenchmarkRunner`), 2 records (`BenchmarkRun`,
`CheckObservation`), 1 static class (`BenchmarkScore`). It deletes nothing in its own wave; its
deletions (four duck `EvaluateAsync`s, four registry delegates, five `Metadata["agent"]` sites) are
Wave 4 and gated. That is the parallel-path risk the minimal angle named about itself, bounded here by
the runner being the **only** new execution path and by its build being gated (§3.3) rather than assumed.

### D3 — One run per (arm, rep); scenario id `{case.Id}·{check.Key}`

Forced by the store and the comparer: `FileSystemOutputStore.cs:355` writes
`_layout.ScenarioFile(subject, runId, result.Id)` = `{Sanitize(id)}.json` (`FileSystemLayout.cs:51`),
so arms × reps in one run overwrite by id; `RunComparison.Index` throws on a duplicate id
(`:287-291`) and pairs by id across runs and never sees an arm. One run per (arm, rep) makes the
pairing fall out of the id scheme. Reps share `RunContext.ParentInvocationId`; the paired comparison
over reps is in memory (`PairedEvalComparer.Record` accumulates reps, `:119`).

### D4 — The door refuses a composite and a double admission

`Admit` throws on `eval is FloorAdmittedEval`. `Annotate` throws when the wrapped result has
`SubResults`. `Admit` is in `AgentEval.Abstractions` and cannot name `CompositeEval`
(`src/AgentEval.Core/Evals/CompositeEval.cs:12`), so the structural check on the *result* is the one
available. This is what keeps ADR-030 §3.2's reason 1 ("a composite has a `Score` and cannot have a
floor") **true**: the door will not write the root floor that reason says cannot exist. Atomic checks
only; each with its own floor; no composite root can carry floorless leaves in under one `NotDerivable`.

### D5 — The projection: a dropping report ⇒ `null`, timeline or no timeline; `ToToolCall` public

§1.3a. Costs the one existing-test edit (§5.2). The contract a consumer's `Observe` must honour is
documented on `BenchmarkArm.From`: `null` = no recorder or a blind one; `[]` = a complete recorder saw
nothing; `ToToolCall(record)` for the two prefixes. The streaming residual is declared in the
projection's remarks and left to the harness (it is harness work — the extractor must learn to count).

### D6 — `AggregateWeights(results, weights)` statics on the five sealed strategies; stubs deleted

D1's first row. A **distinct name** rather than an overload of `Aggregate`, so the 9 test files that
call `.Aggregate(` (`grep -rl '\.Aggregate(' tests --include=*.cs | wc -l` → 9) face no
overload-resolution question, and so the single test fake (`CompositeEvalsServiceExtensionsTests.cs:14`)
keeps compiling — an abstract interface addition would break it, and a default forwarding *toward* the
weights overload could not delete the stubs. Interface promotion is Wave 4.

### D7 — `JudgeModel: null` at the three red-team roots, with the four-part disclosure (§3.2)

### D8 — `AtomicCodeEval.NotApplicable(reason, evidence?)`

D1's fifth row. It removes the five-positional-record noise
(`new EvalResult(Metric, Score, Details, Provenance, EvaluatedAt)`, `EvalResult.cs:8`), not the
subclass where a consumer's real branches live: `NamedSkuNotPresentedEval.cs:118` (the error-prefix
filter) and `:132-138` (presented-nothing ⇒ `NotApplicable`; the avoidance floor at k = 0 is 1.000 —
the one-way-direction argument for reading applicability off the output is the comment at `:123-131`).
There is no `CodeCheck.Of` / delegate eval (§4.2 F-L): ADR-030 §3.1 names the slope.

### D9 — "Deterministic" is by shape plus a recorded fact, never by a runtime provenance check

A definition's rule members take no `IEvaluator` (by type), **and** every persisted row records
`Judge == null` (`ComparabilityOf`, `EvalResultPersistence.cs:148`) — the axis `compare` already gates
on (`RunComparison.cs:330`). No allowlist over `Provenance.Type` literals: the tree spells the
deterministic kind as `"atomic-code"` in most places and as `"code"` at `PerformanceBenchmark.cs:605,
:641, :693`, and a positive allowlist would refuse the family it exists to host. A rule that smuggles
a judge and reports `JudgeModel: null` is an author's lie no type can catch; said so.

### D10 — `EvalInput.Metadata` may carry data records and never an execution-bearing object

The rule `EvalInput.TraceMetadataKey` (`EvalInput.cs:51`) already follows. An agent, a chat client or a
delegate in `Metadata` is the ducks' pattern, and it is what D2's typed `BenchmarkArm.Observe` replaces.

### D11 — `BenchmarkFamily` is a catalog, not an execution contract

No `Register(BenchmarkDefinition)`, no `FromDefinition`: the registry ctor demands a
`CompositeFactory` or a `RunnerFactory` (`BenchmarkFamilyRegistry.cs:205-217`), so a definition-only
family would need a throwing factory — the stub shape one layer up. No `DelegateEval` over
`BenchmarkFamily.EvaluateAsync` either: it would wrap nothing for 8 of 12 families and wire the
`Metadata["agent"]` smuggle to the front door for the other 4 (§4.2 F-D). ADR-017 Convention 3 stands
as *"where every family is listed"*; Convention 2 is superseded by this ADR (its `EvaluateAsync(EvalInput)`
adapter becomes one of two entries — the other is D2 — and its provenance clause is recorded as never
implemented, §1.2).

### D12 — External use: a release first; no packaging change; docs; nothing deleted in the consumer

1. **Cut `v0.35.0-beta` first** (Wave 0). Open the PR (none exists: `gh pr list --head
   joslat/digitec-galaxus --state all` → `[]`, carried from the external reader); fill
   `CHANGELOG [Unreleased]` **from `git diff v0.34.0-beta..HEAD` — the diff, never the intent** —
   including AE-04 (`ToEvalInput`, `AddEval(IEval, ChanceFloor)`, `FloorAdmittedEval`),
   `agenteval compare` + `ExitCodes.Incomparable = 13` (`ExitCodes.cs:159`), `StimulusHash`,
   `MeasurementState` + the `"inapplicable"` schema widening (`e34d9614`), the binary-incompatible
   optional-parameter append on `EvalResultPersistence.ToScenarioResult` (`:57-63`), the `[Obsolete]`
   on `AgentEval.Testing.AssertionResult`, and Wave 0's three behaviour changes with their §3.2
   disclosures; bump `Directory.Build.props:42`; mark the release pre-release; run the pre-tag probe.
   **Nothing external is testable before this.**
2. **No packaging change.** ADR-030 Q2 ruled (a) and said a second `PackageId` "is a different
   decision"; consumers see one package and Abstractions/Core types are already on their compile
   surface. No ADR-033.
3. **Additive public API** — Wave 1: `AtomicCodeEval.NotApplicable`, `TestRunEvalProjection.ToToolCall`,
   the five `AggregateWeights` statics. Wave 2 (gated): `AgentEval.Benchmarks.*` of D2.
4. **Docs.** Add `docs/deterministic-evals.md` (the door; the projection contract — `null` vs `[]`,
   the two prefixes; the floor; the undecidable verdict); a paragraph in `src/AgentEval/README.md`;
   and **retract** `docs/architecture.md:1011` and `:1027` (§1.4).
5. **A consumer with no `TestResult` — what it can honestly do.** GatekeeperDemo, after the cut: bump
   the pin (nothing in Waves 0–2 touches `MAF.Gatekeeper`, `Tracing`, `Testing` or `Guardrails.Gates`);
   write **one** `BenchmarkArm.From(...)` projecting its `PhaseOutcome` to an `EvalInput` (query = the
   phase prompt, response = the answer text, tool calls = proposals and executed reads/sends via
   `ToToolCall`, gate facts as *data* in `Metadata`); and admit, with a roster-derived floor, the
   checks that are functions of the tool record — the executed-bulk-read, executed-external-send,
   exfiltrated and attempted booleans (4 of its 9). **What it keeps:** the five booleans that read
   Gatekeeper facts the harness asserted rather than the transcript; the concealment judge
   (`IChatClient`, not an `AtomicCodeEval`); its own interval and report types until it chooses the
   meta lane. "Never exfiltrates" floors at ceiling for a refuse-all policy — recorded honestly as
   undecidable-against-chance and still a measured zero. **This ADR promises no deletion in that
   repository.**
6. **The acceptance subject for D2** is the shipped sample re-expressed as a definition
   (`samples/AgentEval.Samples/EvalJoin/02_DeterministicBenchmark.cs`), built against the **package**
   after Wave 3's cut — a sample that compiles only against the tree proves nothing about external use.
   "VitrineDemo" occurs in **0** files in either repository and is not a subject.
7. **A cross-repo `ProjectReference` is an acceptable *dated* interim only**, targeting the umbrella
   `src/AgentEval/AgentEval.csproj` (never the `IsPackable=false` children), with the AgentEval commit
   SHA recorded by the consumer itself — `AgentEvalVersion` reads `0.34.0.0` for every one of the 175
   branch commits.

### D13 — Named holes, deliberately not filled

| Not carried | Why | What would have to be true |
|---|---|---|
| A definition content hash | Nothing reads one (`grep -c 'Harness\|EvalProject' RunComparison.cs CompareCommand.cs` → 0, 0); ADR-031 V2: one hash over prose kills itself; the version is documentation, the per-scenario facts are the mechanism | A reader that gates on the hash exists first (ADR-031 V7's rule) — and Q4(ii), because it is a manifest field |
| A `Controls` slot, `VOID`, exit 12, a control ledger | Q5. Giving the slot a shape pre-empts the design half of an unfunded lane | Q5 answered |
| Per-case floors | `FloorComparison.Compute` takes one floor per arm (`ChanceFloor.cs:325-326`); `Observation` has no floor slot; `ExactTests` has no Poisson-binomial tail | A per-case exact test lands in the meta lane with its own calibration record |
| Judged definitions (the memory lane) | `IJudgedBenchmarkDefinition` is a name, not a type; LongMemEval/TypedMemEval are stateful runners with no `DatasetTestCase` per case | A memory family asks — ADR-030's "second team" condition, one lane over |
| A family port | Trace-fidelity says it cannot take the shape (`TraceFidelityBenchmarkRegistration.cs:38`: "two-trace input doesn't map onto a single EvalInput"); the red-team trio floors at ceiling for a refuse-all policy (`BinomialTailP` returns NaN for `floor >= 1.0`, `ExactTests.cs:114`; `AboveFloor` is then false, never a pass — the correct outcome for a check a blind policy cannot fail) | Wave 4(e), after the duck split |
| Composite rules at the door | D4; ADR-030 §3.2 reason 1 | Never on the evidence: the runners persist leaves, so a root floor lands on nothing |
| A `bench` subcommand or `--list` row for a consumer's definition | §1.4; D11 | Registry redesign plus assembly loading — retracted from `architecture.md` instead |

---

## 3. CONSEQUENCES

### 3.1 What gets better

- Three flattering defects on the shipped join stop shipping: a measured `[]` where the recorder was
  blind (§1.3a); a root floor on a composite that the census counts and the runners never persist
  (§1.3b); a `judge` axis that reads *judged* on rows a judge never saw (§1.3c).
- Four `IEval` stubs whose only method throws, and one private aggregation copy, are deleted
  (`grep -rn SyntheticEval src tests` → 0 after Wave 1). The reachability gap ADR-030 §2.6 calls "one
  layer down" is closed at its root cause rather than at the registry.
- A consumer can express a deterministic benchmark as data, run it against two arms, and get two run
  directories that `agenteval compare` pairs by construction — with the case as the unit, the floor
  on every row, and the undecidable verdict expressible (D8). Nothing in that path invents a result
  model, a verb, a schema field or a package.
- ADR-030 §3.2's rule is sharpened to its load-bearing form (*never a leaf*) and gets the refusal that
  keeps its first reason true.

### 3.2 What gets worse, and the disclosures owed

Three behaviour changes ship in Wave 0. Each carries the four-part correction format the project holds
itself to (superseded → corrected · direction · blast radius · falsifiable prediction).

| | Projection timeline | `JudgeModel: null` ×3 | Perf's cap rule |
|---|---|---|---|
| **Superseded** | a dropping `ToolUsage` report falls through to `Timeline` and returns the derived timeline as the record (`TestRunEvalProjection.cs:264`) | `"owasp-judge-passthrough"` / `"nist-…"` / `"mitre-…"` whenever a judge object was supplied (`OwaspBenchmarkRun.cs:269`, `NistBenchmarkRun.cs:159`, `MitreBenchmarkRun.cs:282`) — a judge that graded nothing (`OwaspBenchmark.cs:84-88`) | a private `CapByWorstAggregate` (`PerformanceBenchmark.cs:717-745`) that filters by label only |
| **Corrected** | a dropping report ⇒ `ToolCalls == null`, timeline or no timeline | `null` | Core's `CapByWorstAggregation.AggregateWeights`, which routes through `CountsTowardAggregate` |
| **Direction** | the old value was **flattering**: `[]` (a measured zero on an absence question) or a partial record presented as complete | the old value was **flattering**: rows read as judged | none observable: `grep -c '"error"\|"inapplicable"' PerformanceBenchmark.cs` → 0, so the extra exclusions never fire on perf's inputs — declared as a rule change |
| **Blast radius** | any harness-fed check on an agent with approval-gated tools under the default options; neither shipped sample (0 approval-gated tools in `EvalJoinEndToEndTests.cs`) | every historical CLI red-team run (`BenchOwaspCommand.cs:85-90` always resolves a judge) reads **Incomparable, exit 13**, on the `judge` axis against a post-fix run; 3 files under `.agenteval/` carry the literal today | none |
| **Falsifiable** | after the change, `TestRunEvalProjectionTests.cs:391` is the **only** red test in the suite | `agenteval compare` of `.agenteval/subjects/agents/TestSubject/runs/2026-05-24_08-48-17_6ea30ed8` against any post-fix OWASP run exits **13** with axis `judge: present/none` — on the machine that holds that gitignored run (§1.3c). Portable form for a fresh clone: the per-family test persists the root through `EvalResultPersistence.ToScenarioResult` and asserts `Comparability.Judge is null`; `RunComparisonTests.cs:153` (`JudgeOnOneSideOnly_IsIncomparable`) already pins that a judged row against an unjudged one is Incomparable | perf's three-leaf composite score is byte-identical before and after on every `PerformanceBenchmarkAdapterTests` input |

Other honest negatives:

- **A parallel path exists for one wave.** Between Wave 2 and Wave 4 the four ducks and D2's runner
  both execute benchmarks. Bounded: the runner is the only *new* path, Wave 4 is the named deletion,
  and no consumer is pointed at the ducks in the meantime.
- **`PENDING` for a run that measured nothing is "no verdict", and it is not VOID.** A reader who
  wants an inadmissible run to be loud gets a summary verdict the schema already has and no exit
  code. That is Q5's design half, deliberately not pre-empted.
- **The definition's identity rides in a string** (`RunContext.Harness`) until Q4(ii) adds a typed
  field. A human can read it; no tool does; nothing gates on it.
- **`NotApplicable` and `NotMeasured` share the `skipped` bucket** in `RunStats`. A finer bucket is
  Q4(ii).
- **The streaming harness path is still blind to approval-gated drops.** Declared, not fixed.
- **One existing test is edited** (§5.2). The project's standing rule is that no existing test file
  is edited; this is the one grant this ADR asks for, with the alternative named.

### 3.3 Sequencing — waves, what forces each, and its gate

| Wave | Content | Forced by | Gate |
|---|---|---|---|
| **0 — fix, then cut** | D5 (projection ⇒ `null`, + the one test edit); D4 (`Admit`/`Annotate` refusals); D7 (`JudgeModel: null` ×3 with §3.2's disclosure); CHANGELOG from the tag diff; props bump; PR; pre-release flag; pre-tag probe; **cut `v0.35.0-beta`** | *Flattering defects first* (the local plan's second ordering rule, `MASTER_PLAN.md:1050`); nothing external is testable before a package carries the join; the projection's contract must not be documented while its only producer feeds it a flattering path | **One existing-test edit** (§5.2). If refused: cut anyway with the defect **declared** in the notes, as the project did with five declared defects in `v0.31.0-beta` |
| **1 — deletions and helpers, zero test edits** | D6 (`AggregateWeights` ×5; delete 4 stubs + perf's copy); D8; `ToToolCall` public; `docs/deterministic-evals.md`, README paragraph, `architecture.md` retraction; ADR-030 §11 / ADR-031 §12 (written with this ADR) | Wave 2's per-check rows need aggregation without an `IEval` per leaf; this ADR cannot cite an ADR whose evidence section says `AddEval` does not exist | none |
| **2 — the contract** | D2/D3 (`AgentEval.Benchmarks.*`); `samples/AgentEval.Samples/EvalJoin/02_DeterministicBenchmark.cs`; this ADR → Accepted | Wave 1; the join already present | **Owner: Q-A** (§6) |
| **3 — second cut** | `v0.36.0-beta`; GatekeeperDemo pin bump + optionally one arm and up to four honest checks (D12 item 5) | a consumer migrates against a version, never a branch | none beyond Wave 2 |
| **4 — gated deletions** | (a) split the four ducks: harness half + admitted checks; retire `TryGetValue("agent")` ×4 + `BenchPerfCommand.cs:119`, the four registry delegates, `BuildSkippedComposite`; promote `AggregateWeights` onto `IAggregationStrategy` · (b) `AgentEvalCompositeEvaluator` takes a floor · (c) `MetricEval` · (d) per-case rows visible on disk as inapplicable · (e) family ports (perf latency/cost; red-team per-probe) | each edits existing tests or answers a reserved question | (a) test edits: `MitreBenchmarkTests` 6 sites, `OwaspBenchmarkTests` 5, `NistBenchmarkTests` 2, `PerformanceBenchmarkAdapterTests`, `TraceFidelityTests.cs:168,183` (`Assert.Null(family.EvaluateAsync)`), the one aggregation fake; ADR-017 Convention 2 formally superseded · (b) Q6 · (c) a second team · (d) Q4(ii) · (e) after (a) |

### 3.4 Where the three design angles disagreed, and what was decided

Condensed; each row has a reason a reviewer can re-run.

| Question | Decision | Checkable reason |
|---|---|---|
| Aggregation change shape (statics / retype the interface / additive interface overload) | statics, distinct name, interface later | `grep -rn ': IAggregationStrategy' tests` → one fake; `.Aggregate(` in 9 test files |
| Timeline fix location (harness / projection) | projection | the harness's timeline has four readers (`TraceArtifactManager`, two samples, `NuGetConsumer/Demos.cs`) that want executed-call timelines; the projection is where the false premise lives |
| Collapse the three `*BenchmarkRunner`s | **no** | `ComparabilityFactsTests.cs:580-591` and `StimulusHashTests.cs:264-275` open the three files **by path**, assert `File.Exists`, and assert literals in each body; the runners are 124/124/127 lines with a normalised diff of **2** lines (GDPR↔EU AI Act) and **27** (GDPR↔Agentic; `sed -E 's/Gdpr|EuAiAct|Agentic/X/g'` on each, then `diff | grep -c '^[<>]'` — the second figure moves by a few lines with the normalisation) — the collapse deleted ~180 lines and nothing else |
| Undecidable helper name | `NotApplicable` | CS0108 against both consumers' `Undecidable` |
| Definition hash / fingerprint | none | D13 row 1 |
| Where the floor lives | one per check (= per check, per arm) | `Compute` takes one floor per arm; per-case applicability is `NotApplicable` per row |
| Composite rules at the door | refuse | D4 |
| Determinism enforcement | shape + `Judge == null` | D9 |
| Subject slot | `BenchmarkArm(ArmId, Observe)` + `FromHarness(…, options?)` | `IncludeApprovalGatedToolCalls` must stay reachable |
| Arms × reps per run | one run per (arm, rep) | D3 |
| Registry hook | none | D11 |
| Packaging | none | ADR-030 Q2 |
| Public projection helper | `ToToolCall(ToolCallRecord)` only | D1's last row |
| First subject through the contract | the shipped sample as `EvalJoin/02` | trace-fidelity refuses the shape; VitrineDemo does not exist |

---

## 4. ALTERNATIVES CONSIDERED

### 4.1 Merging any of the contracts — rejected, each with the state it would lose

| Fold | What the code would lose | Where |
|---|---|---|
| `IEvaluator` into `IEval` (or widen `IEvaluator`) | `EvaluationFailed` and the token counts are documented judge-only states; a deterministic implementer makes a branch the caller depends on dead | `IEvaluator.cs:14-16`, `:28-29`, `:77`, `:85` |
| `IProbeEvaluator` into `IEval` | the three-valued `Outcome` that combinators branch on; inverted polarity as a type; `IAttackType.GetEvaluator()` ownership; a zero-allocation struct on a path that evaluates tens of thousands of pairs | `src/AgentEval.RedTeam/RedTeam/Core/` |
| `IEval` as `IMetric` | `MetricResult` has no undecidable state — it is the only result model without one | `src/AgentEval.Abstractions/Core/IMetric.cs` |
| harness into eval | they are layers; `ToEvalInput` is the seam; the four ducks *are* the merged shape and §1.2 is what it costs | `TestRunEvalProjection.cs:152` |
| a generic `IEvaluate<TIn,TOut>` | nothing binds on it; it names the six contracts without deleting one | — |

### 4.2 The fourteen fatal objections, and what was done with each

| # | Objection | Verified on the tree | Disposition |
|---|---|---|---|
| F-A | One floor per definition stamped on every per-case row as `Derived` — rows carry a bar that is not that row's bar | `FloorComparison.Compute(observations, armId, floor)` takes **one** floor per arm (`ChanceFloor.cs:325-326`); `Observation` has no floor slot; no Poisson-binomial in `ExactTests` | **Resolved by stating the unit.** The floor is an arm-level statement (the binomial tail tests the arm's success count over cases against one p); it is derived per check; per-case applicability is `NotApplicable` per row. Per-case floors are D13 |
| F-B | A harness-side timeline fix destroys executed-call timelines for four readers | producer `MAFEvaluationHarness.cs:129 → :599`; readers in `TraceArtifactManager`, two samples, the NuGet consumer demo | **Fix moved to the projection** (D5). Costs one test edit (§5.2) |
| F-C | Collapsing the runners into forwarders cannot satisfy "0 test files touched" | `ComparabilityFactsTests.cs:580-591`, `StimulusHashTests.cs:264-275` scan the three files by path | **Collapse dropped** |
| F-D | `DelegateEval` over `BenchmarkFamily.EvaluateAsync` wraps nothing for 8 of 12 and wires the agent-smuggle to the front door for 4 | `evaluateAsync: null` ×8; the 4 read `Metadata["agent"]` and return a skipped composite when absent | **Dropped** (D11) |
| F-E | `BenchmarkScore.AgainstChance` cannot execute: per-case floors have nowhere to go and `Compute` throws on continuous values | `ChanceFloor.cs:345-353`; `ObservationAdapters.ToObservation(EvalResult)` (`:48`) maps `Score.Value` uncollapsed; `Compute` has 0 callers in `src/` | **Resolved by construction:** atomic checks; reps collapsed with `ObservationUnit.Collapse` (`RepCollapse.cs:76`) *before* `Compute`; collapse and `passAt` are explicit parameters |
| F-F | Q6 mischaracterised, then presumed: building the runner before Slice 2.6 answers Q6 by construction | ADR-030 `:1627`; the local plan's `:688` and Phase 6 rows | **The runner's build is gated on the owner** (§6 Q-A). The contract composes the harness externally and leaves `TestResult.Score` untouched, so it is not 6.3 — but whether iterating 6.1+6.2 over cases is inside the rule is the owner's reading of the owner's rule |
| F-G | Trace-fidelity as the first port is the one family that says it cannot take the shape | `TraceFidelityBenchmarkRegistration.cs:38` | **No family port scheduled**; first subject is `EvalJoin/02` |
| F-H | OWASP/MITRE/NIST assigned to the deterministic contract while carrying a judge; refuse-all floor = 1.0 ⇒ NaN | `ExactTests.cs:114`; `AboveFloor` (`ChanceFloor.cs:291`) is then false; `Admit` accepts a bar of 1.0 | **Not a defect of the contract; recorded** (D13 row 5). The red-team families are not ported; the phantom label is removed so their rows stop reading as judged |
| F-I | A positive provenance allowlist (`"atomic-code"` only) refuses the families it exists to host | perf spells it `"code"` at `:605,:641,:693` | **No runtime provenance police** (D9) |
| F-J | Arms × reps in one run overwrite by scenario id; `compare` refuses duplicates and never sees arms | `FileSystemOutputStore.cs:355`, `FileSystemLayout.cs:51`, `RunComparison.cs:287-291` | **One run per (arm, rep)** (D3) |
| F-K | A run writer that "decides nothing" is unsatisfiable: `CompleteRunAsync` needs a verdict from a closed enum, and the shipped runners count `inapplicable` as failed | `summary.schema.json:11` (`PASS\|FAIL\|WARN\|PENDING`), `:13-22` (closed stats); `AgenticBenchmarkRunner.cs:102-111`; `EvalScore.NotApplicable()` has label `"inapplicable"`, `Passed=false` (`EvalScore.cs:163-164`) | **The runner writes the existing pass/fail semantics with no floor applied** (D2's `BuildSummary`); `PENDING` = "no verdict", not VOID; Q6 untouched |
| F-L | A ≤20-lines consumer acceptance rewards the repository's most-repeated defect; the worked example drops two branches | `NamedSkuNotPresentedEval.cs:118`, `:128-136` | **`CodeCheck.Of` dropped; line count is not an acceptance criterion**; D8 keeps the subclass |
| F-M | Routing the composite runners through the door means `NotDerivable("legacy")` on ~79 evals or a floorless path | 79 ∩ 7 = 1 | **Nothing is routed through anything** (F-C); atomic only (D4) |
| F-N | Violates the no-existing-test-edit rule (for Waves 2/4) | ADR-030 `:1594-1602` | **All test edits enumerated and gated** (§5.2, §3.3 Wave 4) |
| F-O | A consumer's twelve booleans cannot be twelve `Func<EvalInput,…>` checks; it did not ask | `RunMetrics.cs`: 9 `required bool`; `ConcealmentJudge` on `IChatClient`; 0 eval types named | **The migration story is withdrawn**; D12 item 5 states what it can honestly do and what it keeps |

### 4.3 Not stolen, and why

`DelegateEval` (F-D); a definition `ContentHash` (V2); ADR-033 on packaging (Q2 answered it); the
trace-fidelity port (F-G); `CodeCheck.Of` and the `Undecidable` name (F-L, CS0108); a `Fingerprint` in
`RunContext.Harness` (nothing reads it); arms × reps in one run (F-J); a line-count acceptance (F-L);
the consumer deletion story (F-O); a runtime provenance allowlist (F-I).

---

## 5. ACCEPTANCE CRITERIA — commands a reviewer can run

### 5.1 Waves 0–1, before funding

1. **Projection.** After D5, `dotnet test` on any TFM shows **exactly one** red test:
   `TestRunEvalProjectionTests.cs:391`. Prediction: `EvalJoinEndToEndTests.cs` stays green (0
   approval-gated tools) and `ChatClientAdapterStreamingIntegrationTests` stays green (the streaming
   report never says "dropping").
2. **Aggregation.** After D6, `grep -rn SyntheticEval src tests` → **0**, and the 9 test files that
   call `.Aggregate(` compile unchanged (`grep -rl '\.Aggregate(' tests --include=*.cs | wc -l` → 9,
   same set).
3. **Judge labels.** After D7, `agenteval compare --baseline
   .agenteval/subjects/agents/TestSubject/runs/2026-05-24_08-48-17_6ea30ed8 --candidate <any post-fix
   OWASP run>` exits **13** with axis `judge: present/none` — **only on the machine holding that run**:
   `.agenteval/` is gitignored (`.gitignore:453`) and a pre-fix run needs credentials to re-make. Elsewhere:
   `grep -rn --include=*.cs 'judge-passthrough' src` → 0, and the per-family test asserts
   `Comparability.Judge is null` on the persisted root (`RunComparisonTests.cs:153` pins the consequence).
4. **Census invariant.** After Wave 1, the strict `IEval` grep reads **75** — each of the four
   `*SyntheticEval` stubs was its file's *only* strict `: IEval` declaration (`grep -nE '<the regex at
   FloorAdmittedEval.cs:28>' PerformanceBenchmark.cs OwaspBenchmarkRun.cs NistBenchmarkRun.cs
   MitreBenchmarkRun.cs` → one line each, `:748`, `:326`, `:238`, `:358`), so D6 removes four files from the
   declaring set. An earlier revision of this item said "still 79"; it had not accounted for the stubs.
   `ChanceFloor` files still **7**, intersection still **1** (the door); `FloorAdmittedEvalTests.cs:336`
   still green — it asserts the intersection by reflection, not the count.
5. **Door.** `FloorAdmittedEval.Admit(FloorAdmittedEval.Admit(e, f1), f2)` throws; annotating a result
   with `SubResults` throws; no existing test goes red (0 tests admit a composite or a door today).
6. **Release.** `git tag --list 'v0.35*'` → one tag; the nuget package at that version, opened,
   contains `TestRunEvalProjection`, `FloorAdmittedEval`, `ChanceFloor`, `MeasurementState`,
   `StimulusHash` in `AgentEval.Abstractions.dll` (the `0.34.0-beta` package contains none of them —
   carried from the external reader).

### 5.2 The existing-test grant list — the whole of it

| Wave | File | Why | If refused |
|---|---|---|---|
| 0 | `tests/AgentEval.Tests/Evals/TestRunEvalProjectionTests.cs:391` — rename `ADroppingReportFallsThroughToATimeline_WhichIsADifferentRecorder`, flip its assertion to `Null` | it pins a premise no producer satisfies (`grep -rn '\.Timeline = ' src` → 4 harness sites, all from the same `ToolUsage`); the defect is flattering on a safety question | ship with the defect declared in the release notes; document the projection's contract only after the fix |
| 0 (comment only) | `tests/AgentEval.Tests/Evals/EvalScoreMeasurementWithExpressionTests.cs:211` — the *comment* ("which no shipped producer can currently make it") goes stale once Core has a `NotApplicable` producer; the assertion is untouched | honesty of the record | leave the stale comment and note it in the CHANGELOG |

Everything else in Waves 0–3 edits **no** existing test file. Wave 4's list is in §3.3.

### 5.3 Wave 2, after the owner's Q-A

7. A definition with two arms produces two run directories whose scenario files are byte-pairable by
   name; `agenteval compare` on them exits **0 or 13** and never throws on a duplicate id.
8. `grep -rn 'Metadata\[.agent.\]\|TryGetValue("agent"' src/AgentEval.Core/Benchmarks/` → 0 in the
   new namespace; `grep -rn ': IEval' src/AgentEval.Core/Benchmarks/` → 0 (no new `IEval`;
   `BenchmarkNamespaceContractTests` enumerates nothing new).
9. `samples/AgentEval.Samples/EvalJoin/02_DeterministicBenchmark.cs` builds against the **package**
   (`PackageReference`, not `ProjectReference`) at the Wave-3 version.

---

## 6. OPEN QUESTIONS — the owner's; named as gates, not resolved here

| # | Question | What it blocks in this ADR | Evidence, sharpened not answered |
|---|---|---|---|
| **Q-A** | Is iterating AE-04's join (6.1 + 6.2) over a definition's cases **inside** the local plan's "AE-04 before AE-06" rule (`MASTER_PLAN.md:1048`, §2.4), given that Slice 2.6 (AE-06's stop rule) is unbuilt and gated on Q6? | **Wave 2 entire** (D2, D3, the `EvalJoin/02` sample, this ADR's move to Accepted) | The runner admits every check through the same door (79 ∩ 7 = 1 is unchanged by it); it composes the harness externally and leaves `TestResult.Score` untouched, so it is not 6.3; it applies no floor to any verdict. The rule's bulk-wiring clause is honoured by construction; its outcome clause is honoured the way the door honours it — recorded, unapplied. Whether that is "inside" is a reading of the owner's own rule |
| **Q4(ii)** (ADR-030) | Write `measurement` unconditionally and bump `$id` | a typed definition identity and rep index on the manifest (`additionalProperties:false` at `manifest.schema.json:6,12,22,36,54,64,73`); a finer `RunStats` bucket than `skipped`; on-disk visibility of `NotApplicable` rows without reading `label` | Nothing here changes a historical content hash: `NotApplicable` writes `measurement` only when non-default, as the two shipped consumers already do |
| **Q5** (ADR-030) | Fund negative controls? | any `Controls` slot on `BenchmarkDefinition` (deliberately absent); `VOID`, exit 12 (`ExitCodes.cs:153-154`), a control ledger; whether the join wave's local ablations become durable controls | `PENDING` for a run with nothing measured is "no verdict", not VOID; `grep -rn controlLedger src` → 1 hit, the reservation comment |
| **Q6** (ADR-030) | Does the stop rule bind — does any `FloorComparison` gate a verdict? | whether `BenchmarkScore` ever gates (today it reports); Slice 2.6's acceptance; Wave 4(b) (`AgentEvalCompositeEvaluator` taking a floor) | `SignTestAtEqualK` is live at `PairedCoverageReport.cs:463` with **11** call sites (`Eval02_LatentInterestCoverage.cs` ×4, `Eval09_HypothesisComparison.cs` ×4, `NegativeControls.cs:1845,:2897,:3320`), so 2.6's precondition is two deletions, not one (ADR-030 §11.2 row 11). `IsUsableAsABar && Passed` with no comparison is now countable and, on this tree, is every admitted pass |
| **Q8** (ADR-030) | Quotation of the four UNKNOWN figures | nothing here quotes them | unchanged |

### ✅ §6 ANSWERED 2026-09-07 — every question in this table now has the owner's answer

| # | Answer | Effect on this ADR |
|---|---|---|
| **Q-A** | **INSIDE the rule.** | Wave 2 is funded: D2, D3, `BenchmarkRunner`, the `EvalJoin/02` sample, and this ADR's move to Accepted |
| **Q4(ii)** | **Defer.** Keep the conditional writer; no `$id` bump | the summary keeps the schema's single `skipped` bucket for both `NotApplicable` and `NotMeasured`, and says so where it does it |
| **Q5** | **Defer the API; take the arm.** No `INegativeControl` | `BenchmarkDefinition` keeps **no** `Controls` slot; the runner writes **no** `VOID` and claims **no** exit 12. A degraded `BenchmarkArm.From` IS the control, scored by `AgainstReference` |
| **Q6** | **Yes on the principle, staged in execution** | Phase 7.2 unblocked. Until Slice 2.6 lands, `BenchmarkRunner` applies **no** floor to any verdict — floors are recorded and unapplied |
| **Q8** | unchanged — a standing quotation obligation, not a gate | nothing here quotes the four |

**Why Q-A is "inside", recorded so a future reader can check the reasoning rather than the verdict.**
The rule exists to stop floorless evals being bulk-wired at the primary entry point. The runner
**cannot reach that state**: an `AdmittedCheck` becomes runnable only through
`FloorAdmittedEval.Admit`, so the census intersection (`IEval` declarers ∩ files naming
`ChanceFloor`) is unchanged by it and stays at **1** — verified after the runner shipped, not
predicted. It composes the harness externally, leaves `TestResult.Score` untouched (so it is not
6.3), and applies no floor to any verdict. The bulk-wiring clause is honoured **by construction**;
the outcome clause is honoured the way the door already honours it — recorded, unapplied.

⚠ **What this answer does NOT license.** It does not make the runner a place where a floor gates
anything (that is Q6, and its execution is staged). It does not add a controls slot (Q5). It does
not add a manifest field (Q4(ii)). Each of those is refused in code, not only in prose, and §5.3's
acceptance greps are what check it.


---

## 7. WHAT WOULD CHANGE THIS DECISION

1. **The owner reads Q-A as "outside the rule"** — then Wave 2 waits for Slice 2.6, and Waves 0–1
   stand on their own (they are fixes and deletions, not the contract).
2. **A second team asks for a judged definition** — then `IJudgedBenchmarkDefinition` gets a type, and
   D9's "deterministic by shape" acquires a sibling with a judge fingerprint on the row.
3. **A consumer needs a `bench` row for its definition** — then the registry's ctor invariant
   (`BenchmarkFamilyRegistry.cs:205-217`) is the thing to redesign, not this contract; D11 would be
   reopened.
4. **The projection change turns more than one existing test red** (§5.1 item 1) — then a producer
   this document did not find attaches a timeline from a different recorder, and D5 must be re-examined
   before it ships.
5. **`grep -rn SyntheticEval src tests` is non-zero after D6** — then a stub had a reader this
   document did not find, and D6's "loses nothing" was wrong.
