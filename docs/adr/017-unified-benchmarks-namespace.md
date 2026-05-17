# ADR-017: Unified Benchmarks Namespace

## Status

**Accepted** (2026-05-17). Implementation scheduled for v0.10.0-beta.

## Date

2026-05-17

## Context

Across releases v0.3.0-beta through v0.9.0-beta, AgentEval grew six distinct benchmark surfaces. Their entry-points landed organically in different namespaces, in different assemblies, with different shapes. Snapshot of the situation at v0.9.0-beta:

| Entry-point | Assembly | Namespace | Shape | Returns |
|---|---|---|---|---|
| `AgenticBenchmark.AgenticExecution(judge)` | `AgentEval.Evals.Agentic` | `AgentEval.Evals.Agentic` | static preset factory | `CompositeEval` |
| `GdprBenchmark.Standard(articles)` | `AgentEval.GdprBenchmark` (in `samples/`) | `AgentEval.GdprBenchmark` | static preset factory | `CompositeEval` |
| `EuAiActBenchmark.Standard(articles)` | `AgentEval.EuAiActBenchmark` (in `samples/`) | `AgentEval.EuAiActBenchmark` | static preset factory | `CompositeEval` |
| `MemoryBenchmark.Quick` + runner | `AgentEval.Memory` | `AgentEval.Memory.Models` + `AgentEval.Memory.Evaluators` | config record + runner | `MemoryBenchmarkResult` (bespoke) |
| `new PerformanceBenchmark(agent).RunLatencyBenchmarkAsync(...)` | `AgentEval.Core` (in `Benchmarks/`) | `AgentEval.Benchmarks` | instance class, imperative | `LatencyBenchmarkResult` (bespoke) |
| `AttackPipeline.Create()....ScanAsync(agent)` | `AgentEval.RedTeam` | `AgentEval.RedTeam` | fluent pipeline | `RedTeamResult` (bespoke) |

Three problems followed from this:

**Problem A — Sample-ness is conflated with packaging-exclusion.** `samples/AgentEval.GdprBenchmark/` and `samples/AgentEval.EuAiActBenchmark/` are referenced as hard `ProjectReference` dependencies by the shipping CLI (`src/AgentEval.Cli/AgentEval.Cli.csproj`). They ship as transitive runtime dependencies of the published umbrella NuGet. They are de facto product code, mislabelled as "samples" because someone needed a console-app harness to develop them against.

**Problem B — `src/AgentEval.Core/Benchmarks/` is a half-finished organisational idea.** `PerformanceBenchmark` was put there when Core was the only place to put a benchmark with no domain assembly. The folder was created in anticipation of cross-cutting benchmarks following — none did, and v0.9.0-beta's cleanup specifically *removed* the legacy `AgenticBenchmark` from Core. The folder is now a one-file ghost folder whose existence implies a convention that no longer applies.

**Problem C — Result-type heterogeneity, not just namespace fragmentation.** Three benchmarks produce `CompositeEval`/`EvalResult` (flowing through the unified output-store / audit-chain / Mission Control rendering). Three produce bespoke result types that don't. A user trying to learn the library has to learn five APIs to run five benchmarks, *and* the results are incomparable downstream. Discoverability is the symptom; result-type homogenisation is the deeper fix.

Two further facts shape the decision space:

1. **The umbrella NuGet is monolithic.** Only `src/AgentEval/AgentEval.csproj` has `IsPackable=true`; the other 7 `src/` projects are embedded into the umbrella via `<ProjectReference PrivateAssets="all"/>`. Moving / renaming sub-assemblies is free externally — there is no multi-package versioning commitment.

2. **The RedTeam assembly already has the underlying machinery for OWASP and MITRE benchmarks** (9 attack types self-classified against `OwaspLlmId` + `MitreAtlasIds`; reporters for OWASP / MITRE ATLAS / ISO 27001 / SOC 2). What's missing is named-preset factory façades. A discoverability gap with marketing implications.

## Decision

Adopt **a single discovery namespace `AgentEval.Benchmarks` for the top-level preset-factory classes**, while internals stay in their domain namespaces. Promote the compliance benchmarks out of `samples/`. Relocate `PerformanceBenchmark` into a dedicated `AgentEval.Evals.Performance` assembly. Add `OwaspBenchmark` and `MitreBenchmark` façades over the existing RedTeam pipeline. Add a `LongMemEvalBenchmark` façade over the existing external runner. Ship this as **v0.10.0-beta** on its own branch, distinct from the v0.9.0-beta legacy-removal release.

### What the public surface looks like after this ADR

```csharp
using AgentEval.Benchmarks;

var agentic   = AgenticBenchmark.AgenticExecution(judge);
var gdpr      = GdprBenchmark.Standard(articles);
var euAiAct   = EuAiActBenchmark.Standard(articles);
var owasp     = OwaspBenchmark.Top10(judge);                    // NEW
var mitre     = MitreBenchmark.AtlasBaseline(judge);            // NEW
var memory    = MemoryBenchmark.Quick;                          // unchanged shape, NS rehosted
var perf      = PerformanceBenchmark.LatencyOf(agent);          // reshaped to fit unified pattern
var longMem   = LongMemEvalBenchmark.Subset(judge);             // NEW façade over existing runner
```

**One `using` directive** for benchmark discovery. The factories are `static partial class` declarations split across the domain assemblies that own their implementations — physical layering preserved, logical layering unified.

### What stays in domain namespaces

All *runners*, all *internal types* (`ArticlesRegistry`, `Pillar1Foundations`, `ScenarioToAtomicEval`, `TaskCompletionEval`, `MemoryBenchmarkRunner`, `AttackPipeline`, …) stay in their domain namespaces (`AgentEval.Compliance.Gdpr.Articles`, `AgentEval.Evals.Agentic.Process`, `AgentEval.RedTeam`, …). Only the **public preset-factory entry-points** lift to `AgentEval.Benchmarks`.

### Result-type homogenisation

OWASP / MITRE / Performance runners get a new `EvaluateAsync(EvalInput) → EvalResult` adapter method so they flow through the same output-store / audit-chain plumbing as the other benchmarks. The bespoke result types (`OWASPComplianceReport`, `MITREATLASReport`, `LatencyBenchmarkResult`) remain available as **additional** output — emitted alongside the standard `EvalResult`, not as a replacement. Mission Control rendering becomes uniform across all benchmark families.

### Assembly layout after v0.10.0-beta

```
src/AgentEval                              (umbrella; only IsPackable=true)
src/AgentEval.Abstractions
src/AgentEval.Core
src/AgentEval.DataLoaders
src/AgentEval.Evals.Agentic                (existing — AgenticBenchmark NS rehosted)
src/AgentEval.Evals.Compliance.Gdpr        (NEW — promoted from samples/AgentEval.GdprBenchmark)
src/AgentEval.Evals.Compliance.EuAiAct     (NEW — promoted from samples/AgentEval.EuAiActBenchmark)
src/AgentEval.Evals.Performance            (NEW — promoted from src/AgentEval.Core/Benchmarks/)
src/AgentEval.MAF
src/AgentEval.Memory                       (existing — MemoryBenchmark NS rehosted)
src/AgentEval.RedTeam                      (existing — OwaspBenchmark + MitreBenchmark façades added)

samples/AgentEval.TravelDemo               (unchanged)
samples/AgentEval.TravelDemo.Evals         (unchanged)
samples/AgentEval.Samples                  (unchanged)
samples/AgentEval.GdprBenchmark.Demo       (NEW — ~50 LOC consumer of promoted assembly)
samples/AgentEval.EuAiActBenchmark.Demo    (NEW — ~50 LOC consumer of promoted assembly)
```

The compliance benchmarks live in **separate assemblies per regulation** (not one fat `AgentEval.Evals.Compliance`). Rationale: regulations have wildly different runtime cost profiles (embedded YAML, judge prompts, calibration baselines). One assembly per regulation scales out to HIPAA / PCI-DSS / ISO 42001 / SOC 2 / NIS2 over the v1.1+ roadmap without forcing consumers to download all of them.

## Rationale

### Why one namespace, not one assembly

The user's instinct of "all benchmarks in one place" is correct **for discovery** but wrong for **physical co-location**. Placing all factory implementations in one assembly (e.g., a new `AgentEval.Benchmarks` library) creates two problems:

- That assembly would need to reference every domain assembly transitively, creating a dependency tree inversion.
- Each preset factory needs domain types (`ArticlesRegistry`, `IAttackType`, `MemoryBenchmarkRunner`, …) that can't sensibly live in one shared assembly without bundling all the domain implementations together. That defeats the modularity v0.9.0-beta just established.

A namespace, by contrast, is a logical organisation that doesn't constrain physical layering. C# allows `public static partial class Benchmarks` declarations to span multiple assemblies (or sibling top-level static classes that all share a namespace). The user gets **one `using` directive**; the codebase keeps domain-driven assembly boundaries.

### Why promote compliance assemblies out of `samples/`

Three independent reasons converge:

1. **CLI dependency reality.** The shipping CLI takes hard `ProjectReference`s to both compliance projects. They ship to consumers via the umbrella NuGet whether labelled "sample" or not. The label is fictional.

2. **Marketing-credibility reality.** "AgentEval ships compliance benchmarks for GDPR and the EU AI Act" reads quite differently from "AgentEval has GDPR and EU AI Act sample projects you might fork". The first is a product claim; the second is a hobbyist signal. The compliance benchmarks have professional-grade calibration baselines (cf. `strategy/FutureFeatures/calibration-baselines/`), regulator-grade article YAML, signed PDF reports, and audit-chain evidence files. They are products.

3. **Future-roadmap reality.** HIPAA, PCI-DSS, ISO 42001, NIS2, the UK AI Bill — each is a candidate sibling. Building them as `samples/AgentEval.HipaaBenchmark/` etc. perpetuates the wrong category; building them as `src/AgentEval.Evals.Compliance.Hipaa/` etc. names them what they are.

### Why dedicated `AgentEval.Evals.Performance` assembly

`PerformanceBenchmark` is API-incompatible with the new preset-factory shape (instance class, imperative methods, custom result records). Leaving it in `src/AgentEval.Core/Benchmarks/` perpetuates the half-finished organisational idea from Problem B. Moving it to `src/AgentEval.Evals.Performance/` gives it a clean home alongside other `Evals.*` siblings and keeps Core focused on framework primitives. The 50-LOC `EvaluateAsync` adapter that brings it into the unified output-store flow lives in the same assembly.

### Why bundle OWASP and MITRE in v0.10.0

Three reasons:

- The underlying machinery already exists. The façades are ~80 LOC each. There is no engineering reason to defer.
- Shipping the namespace unification without OWASP / MITRE means doing two back-to-back breaking-namespace releases instead of one — discourteous to consumers.
- The "AgentEval Benchmark Suite" marketing pitch (Agentic + GDPR + EU AI Act + Memory + OWASP + MITRE + LongMemEval + Performance) only lands as a complete package. Shipping six of eight and saying "OWASP coming soon" undercuts the launch.

The OWASP/MITRE addition also **forces** an important design decision early: the `EvaluateAsync(EvalInput) → EvalResult` adapter. Without it, OWASP/MITRE results would be a `RedTeamResult` outlier in the otherwise-uniform output-store pipeline. Bundling the work keeps the homogenisation discipline visible.

### Why v0.10.0-beta, not v0.9.0-beta

v0.9.0-beta is a small, clean, independent release (removal of the legacy library-API `AgenticBenchmark`). Bolting a 2,500-LOC assembly reorg onto it triples its review surface and introduces uncorrelated regression risk. The two changes are also rhetorically distinct: v0.9.0 is "we removed dead code"; v0.10.0 is "we expanded the benchmark suite + unified the namespace". Each deserves its own release notes paragraph and CHANGELOG entry.

## Alternatives considered

### Option B — Domain-namespace status quo plus an `Index` helper

Keep each benchmark in its domain namespace. Add a single static class (`AgentEval.Benchmarks.Index`) that exposes typed delegates to each preset. The Index assembly project-references every domain assembly.

**Rejected**: creates two ways to invoke every benchmark (the canonical domain namespace and the Index helper), forcing every README example to pick one and stick with it — a long-term documentation tax. The Index is also boilerplate that grows with every new preset added. Doesn't address the deeper "one place to find benchmarks" instinct; layers a façade over the existing fragmentation rather than fixing it.

### Option C — Promote-and-organise, no namespace unification

Promote compliance out of `samples/`, relocate `PerformanceBenchmark`, add OWASP / MITRE façades — but keep domain-driven namespaces (`AgentEval.Compliance.Gdpr`, `AgentEval.Compliance.EuAiAct`, `AgentEval.Evals.Agentic`, etc.).

**Rejected**: the minimum-blast-radius fix that addresses Problems A and B but not the user's discoverability goal. Six months from now we are still typing five `using` directives for one composite test. Doesn't establish "AgentEval has a Benchmark Suite" as a single mental model.

### Option D — Single fat `AgentEval.Benchmarks` library

Create a new top-level `AgentEval.Benchmarks` library that contains every preset factory directly. Reference all domain assemblies from it.

**Rejected**: creates dependency-tree inversion. Forces the library to know about every domain (compliance, agentic, memory, red-team, performance) as direct project references. Defeats the modularity v0.9.0-beta just established. Internal compliance type movement (a Pillar refactor in GDPR, say) would now need a coordinated bump to the central benchmarks library. Wrong fan-in pattern.

## Consequences

### Positive

- One `using AgentEval.Benchmarks;` import for benchmark discovery — matches the user's mental model.
- Compliance benchmarks promoted from `samples/` to product surface, matching their de facto status.
- `PerformanceBenchmark` gets a sensible home and a unified output shape.
- OWASP and MITRE benchmarks become first-class named-preset wrappers, completing the "AgentEval Benchmark Suite" marketing claim.
- LongMemEval gets a façade — "AgentEval supports the LongMemEval academic benchmark" becomes a real credibility signal.
- Result-type homogenisation via `EvaluateAsync` adapters on the formerly-bespoke benchmarks (OWASP, MITRE, Performance) — Mission Control rendering becomes uniform.
- Internal assembly layering preserved; Core can still depend on nothing else.
- Future compliance benchmarks (HIPAA, PCI-DSS, ISO 42001, NIS2) have a clear home pattern.

### Negative

- ~2,500 LOC of mechanical relocation (file moves, namespace updates) + ~700 LOC of new façade / adapter / CLI work.
- Mass test-namespace rename across ~200 test files.
- Breaking namespace change for any v0.9.0-beta consumer of the affected types — documented in CHANGELOG with a migration table. (Acceptable in 0.x-beta per semver.)
- Umbrella `<ProjectReference>` count grows from 7 to ~10. Build time impact: negligible (linear). NuGet package size impact: needs measurement via `dotnet pack` — if the resulting `.nupkg` crosses ~10 MB, a follow-up may split out an `AgentEval.Compliance` umbrella.
- CLI grows to 7 `bench` subcommands. Discoverability mitigation: add `agenteval bench --list` and `agenteval bench {family} --help` enumeration.

### Neutral

- "Split-declared static partial classes" — some teams dislike this pattern because "grep for all members of `OwaspBenchmark`" requires more than one search. Mitigated by IDE features that aggregate partials.
- The umbrella NuGet remains monolithic — the user can still get every benchmark from one package install. If a future need for fine-grained packaging emerges, it's straightforward to flip individual sub-assemblies to `IsPackable=true`.

## Implementation note

The detailed migration plan, file-move list, and step-by-step implementation order live in [`strategy/FutureFeatures/todo/lastreview/10-unified-benchmarks-architecture-proposal.md`](../../strategy/FutureFeatures/todo/lastreview/10-unified-benchmarks-architecture-proposal.md), §4 ("Migration plan") and §"Next steps if accepted". This ADR captures the decision and rationale; the proposal doc captures the execution plan.

Estimated effort: ~28-32 hours of focused engineering, plus a final Opus pre-merge review pass. Realistic calendar time: 4-5 working days on `feature/v0.10.0-unified-benchmarks`.

## References

- `strategy/FutureFeatures/todo/lastreview/10-unified-benchmarks-architecture-proposal.md` — full architectural proposal with three options weighed, OWASP/MITRE attack-mapping table, and step-by-step migration plan.
- `strategy/FutureFeatures/todo/lastreview/09-v0.9.0-cleanup-review.md` — the prior review that surfaced this need by removing the legacy library-API `AgenticBenchmark`.
- `CHANGELOG.md` — the v0.9.0-beta removal entry establishes the precedent for breaking namespace changes in the 0.x-beta channel.
- ADR-009 (Superseded) — the original benchmark strategy decision, now superseded by the v0.9.0-beta legacy removal and this ADR.
