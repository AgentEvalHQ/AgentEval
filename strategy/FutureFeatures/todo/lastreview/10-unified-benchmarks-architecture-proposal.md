# Last Review #10 — Unified Benchmarks Architecture Proposal

**Reviewer**: Opus #10 (senior architecture reviewer)
**Date**: 2026-05-17
**Branch**: cleanup/legacy-benchmark-removal-v0.9.0 @ 10e358f (about to merge to main as v0.9.0-beta)
**Scope**: Where benchmarks live, what they're called, what gets added (OWASP / MITRE), and what ships in which release.

## Verdict (one line)

**The user is right about the smell and partly right about the fix.** `samples/` is the wrong home for GDPR + EU AI Act; `Core/Benchmarks/` is the wrong home for `PerformanceBenchmark`. But the unifying primitive is **not "one namespace"** — it is **"one *discovery convention*" that respects the domain assembly boundaries already established for v0.9.0**. The right move is to promote the compliance benchmarks out of `samples/`, relocate `PerformanceBenchmark` into the domain assembly it actually belongs to, and add OWASP/MITRE benchmark façades on top of the existing RedTeam pipeline — but I would **not** stuff every benchmark factory into a single `AgentEval.Benchmarks` namespace, because doing so re-introduces precisely the layering inversion v0.9.0 just removed. I'll explain why below.

I also recommend **shipping v0.9.0-beta as-is** and bundling the architecture move as **v0.10.0-beta**. The current branch's value (legacy library-API removal) is orthogonal and merge-ready; the architecture move is a meatier, riskier change that deserves a clean release boundary.

---

## Part 1 — Diagnosis

### 1.1 Verified facts

I re-walked the codebase. The user's snapshot is accurate; here's the slightly enriched picture:

| File | Asm | NS | Shape | IsPackable | Ships? |
|---|---|---|---|---|---|
| `src/AgentEval.Core/Benchmarks/PerformanceBenchmark.cs` (458 LOC) | `AgentEval.Core` | `AgentEval.Benchmarks` | instance class, imperative methods, custom result records | `false` (embedded in umbrella) | YES via umbrella |
| `src/AgentEval.Evals.Agentic/AgenticBenchmark.cs` (536 LOC) | `AgentEval.Evals.Agentic` | `AgentEval.Evals.Agentic` | static preset factory → `CompositeEval` | `false` (embedded in umbrella) | YES via umbrella |
| `src/AgentEval.Memory/Evaluators/MemoryBenchmarkRunner.cs` | `AgentEval.Memory` | `AgentEval.Memory.Evaluators` | runner-style class (`.RunBenchmarkAsync(agent, MemoryBenchmark.Quick)`) | `false` (embedded in umbrella) | YES via umbrella |
| `src/AgentEval.Memory/Models/MemoryBenchmark.cs` | `AgentEval.Memory` | `AgentEval.Memory.Models` | **config record** with static `Quick`/`Standard`/`Full`/`Diagnostic`/`Overflow` presets | `false` (embedded in umbrella) | YES via umbrella |
| `src/AgentEval.Memory/External/LongMemEval/LongMemEvalBenchmarkRunner.cs` | `AgentEval.Memory` | `AgentEval.Memory.External.LongMemEval` | external-dataset runner | `false` (embedded in umbrella) | YES via umbrella |
| `samples/AgentEval.GdprBenchmark/GdprBenchmark.cs` (210 LOC, **51 .cs files in project**) | `AgentEval.GdprBenchmark` | `AgentEval.GdprBenchmark` | static preset factory → `CompositeEval` | `false` | **NO — not in umbrella** |
| `samples/AgentEval.EuAiActBenchmark/EuAiActBenchmark.cs` (204 LOC, **53 .cs files**) | `AgentEval.EuAiActBenchmark` | `AgentEval.EuAiActBenchmark` | static preset factory → `CompositeEval` | `false` | **NO — not in umbrella** |

Two further facts the user did not mention but matter enormously:

1. **The shipping CLI takes hard project references to both `samples/AgentEval.GdprBenchmark` and `samples/AgentEval.EuAiActBenchmark`.** See `src/AgentEval.Cli/AgentEval.Cli.csproj:15-16`. The CLI is impossible to build without them. **They are de facto product code already; they merely live in the wrong folder.**

2. **The umbrella package `AgentEval.csproj` is the *only* `IsPackable=true` project.** The other 7 src/ projects all set `IsPackable=false` and are embedded into the umbrella via `<ProjectReference PrivateAssets="all">` with a custom MSBuild target that copies their DLLs into `lib/`. So when the user worries about "NuGet versioning consequences — each new published assembly is a forever commitment", **that worry doesn't apply at all under the current packaging model**: there is exactly one published package and N internal sub-assemblies. Moving / renaming sub-assemblies is essentially free externally; only the **DLL names embedded in the umbrella** change, and that's not a public-API event for consumers who only see `AgentEval.dll` + its embedded siblings on the assembly resolution path.

This second fact reshapes the entire decision space. We are not designing a multi-package architecture; we are designing the *internal sub-assembly layout* of a single umbrella NuGet.

### 1.2 What's actually wrong

The user's "samples/ is wrong" instinct is correct, but the precise diagnosis is sharper than that:

**Problem A — Sample-ness is conflated with packaging-exclusion.** The compliance benchmarks are kept as Exe projects with `IsPackable=false` and live in `samples/` because *someone needed a working harness to develop them against*, and a console app with embedded YAML datasets was the path of least resistance. But the result is that 414 LOC of CompositeEval factory code, ~100 lines of preset wiring, and the entire Pillar/Article hierarchy (~50 files per regulation) ship as **transitive runtime dependencies of the CLI** while being labelled "sample". That mislabels the product surface to anyone reading the repo top-level.

**Problem B — `Core/Benchmarks/` is a half-finished idea.** When `PerformanceBenchmark` was added, `Core` was the only place to put a benchmark that didn't fit any domain (latency / throughput / cost work for any agent, not just RAG / safety / memory). The `Benchmarks/` subdirectory was created in anticipation that other cross-cutting benchmarks would follow. None ever did, and v0.9.0's cleanup specifically *removed* `AgenticBenchmark` from Core. So `Core/Benchmarks/` is now a one-file ghost folder whose existence implies a convention that no longer applies.

**Problem C — The five benchmark entry-points use four different shapes.** This is the real coherence smell:

| Entry-point | Shape | Returns |
|---|---|---|
| `AgenticBenchmark.AgenticExecution(judge)` | static preset factory | `CompositeEval` |
| `GdprBenchmark.Standard(articles)` | static preset factory | `CompositeEval` |
| `EuAiActBenchmark.Standard(articles)` | static preset factory | `CompositeEval` |
| `MemoryBenchmark.Quick` (static property) + `MemoryBenchmarkRunner.RunBenchmarkAsync(agent, preset)` | config-record-plus-runner | rich `MemoryBenchmarkResult` |
| `new PerformanceBenchmark(agent).RunLatencyBenchmarkAsync(prompt)` | instance class, imperative | `LatencyBenchmarkResult` |
| `AttackPipeline.Create().WithMvpAttacks().ScanAsync(agent)` (RedTeam, not labelled "benchmark") | fluent pipeline | `RedTeamResult` |

A user trying to learn the library has to learn five APIs to run five benchmarks. Worse: the result types are incomparable — three of them produce `CompositeEval` results that flow through the unified output-store / audit-chain / evidence pipeline, and three of them produce bespoke result types that **don't**. That is the deeper smell. Discoverability is a symptom; **result-type homogeneity is the disease**.

**Problem D — RedTeam has the underlying machinery for OWASP and MITRE but exposes it through a fluent pipeline, not a preset-factory.** The compliance reporters (`OWASPComplianceReporter`, `MITREATLASReporter`) already exist and are wired to attack results, but there's no `OwaspBenchmark.Top10(judge)` symmetric with `AgenticBenchmark.AgenticExecution(judge)`. A discoverability gap, and a marketing one (you cannot say "AgentEval includes the OWASP Top 10 benchmark" with a straight face when the API is `AttackPipeline.Create().WithAttacks(...)`).

### 1.3 What's defensible about the current state (historical context)

It's worth granting each placement its intended virtue before tearing them down:

- **`samples/` for GDPR/EuAiAct**: at the time these were built, the CompositeEval + audit-chain plumbing was still landing in Core. Building them as Exes against working YAML datasets let them be developed end-to-end without the CLI fully existing. The "sample" label was an honest signal that this was *example regulation coverage*, not the only possible coverage — a third-party legal team could fork the structure for HIPAA or PCI-DSS the same way they'd fork a sample. That framing has aged badly because the CLI now embeds them as first-class, but it was reasonable in 0.6.x.
- **`Core/Benchmarks/PerformanceBenchmark.cs`**: at the time this was the only benchmark that needed no domain assembly. Putting it in Core preserved a single dependency rule (everything depends on Core, Core depends on nothing). The mistake was creating the `Benchmarks/` folder rather than putting it at the project root as a Core utility.
- **`MemoryBenchmarkRunner` runner-shape**: memory benchmarks are *inherently multi-turn* and require an agent that supports `ISessionResettableAgent`; you cannot reduce them to "build a `CompositeEval`, hand it a single response." The runner shape is justified by the domain.
- **`MemoryBenchmark` config-record-with-statics-shape**: the config struct precedes the runner, which is correct domain decomposition. The runner needs a preset to execute against; `MemoryBenchmark.Quick` is that preset.
- **`AttackPipeline` fluent-builder-shape**: red-team scanning is inherently a multi-attack orchestration with intensity / timeout / fail-fast knobs that don't map onto `CompositeEval`'s "weighted sum of leaf evals" mental model. The fluent shape is defensible **for the orchestration layer**. What's missing is a higher-level preset factory **on top of it**.

### 1.4 Where the user's mental model is right, partly right, and where a sharper insight emerges

- **Right**: GDPR/EU-AI-Act should not be in `samples/`. They are product features.
- **Right**: `PerformanceBenchmark` is awkwardly placed.
- **Right**: There should be a single answer to "where do I find the benchmarks?".
- **Right**: OWASP and MITRE deserve named-preset wrappers — the marketing/discoverability point is sound.
- **Partly right**: "All benchmark entry-points should live in one canonical namespace" — yes for *discoverability* but no for *physical assembly*. If you put `GdprBenchmark.Standard(...)` into `AgentEval.Core`, then `Core` needs the `ArticlesRegistry`, the YAML loader, the embedded resources, the GDPR pillar composites... and Core becomes a kitchen sink. The unifying convention should be **a namespace**, not an **assembly**.
- **Sharper insight**: the user is asking the right question one level too shallow. The real issue is not "where do benchmark factories live" but "**do all benchmarks produce comparable output?**" A unified entry-point story is a 30%-of-value cosmetic fix if the benchmarks produce incomparable result types. The deeper fix is to **converge result types** wherever the domain allows it (compliance + agentic + OWASP + MITRE should all produce `CompositeEval`-shaped results that flow through the same output-store / audit-chain plumbing), and **accept divergence** where the domain forbids it (memory, performance, red-team-scan are legitimately different shapes). The unified namespace then becomes a discoverability convention layered over a *justified* heterogeneity, rather than a fictional uniformity papered over by aliases.

---

## Part 2 — Three design options

### Option A — "Single-namespace umbrella, multi-assembly implementation"

**Idea**: Introduce a new logical namespace `AgentEval.Benchmarks` that hosts the *façade* of every benchmark, while the implementations stay in their domain assemblies. The namespace is populated by type-forwarding stubs (one `public static partial class` per benchmark, declared in each domain assembly, all in the `AgentEval.Benchmarks` namespace).

```
Logical surface area (user-visible):
   AgentEval.Benchmarks.AgenticBenchmark.AgenticExecution(judge)
   AgentEval.Benchmarks.GdprBenchmark.Standard(articles)
   AgentEval.Benchmarks.EuAiActBenchmark.Standard(articles)
   AgentEval.Benchmarks.OwaspBenchmark.Top10(judge)
   AgentEval.Benchmarks.MitreBenchmark.AtlasBaseline(judge)
   AgentEval.Benchmarks.MemoryBenchmark.Standard
   AgentEval.Benchmarks.PerformanceBenchmark (static facade returning a runner)

Physical assemblies (where the code actually lives):
   src/AgentEval.Evals.Agentic       → AgenticBenchmark, OwaspBenchmark (façade only), MitreBenchmark (façade only)
   src/AgentEval.Evals.Compliance    → NEW: GdprBenchmark + EuAiActBenchmark (promoted from samples)
   src/AgentEval.Memory              → MemoryBenchmark (unchanged file path, NS bridged)
   src/AgentEval.RedTeam             → OwaspBenchmark + MitreBenchmark (real impl that wraps AttackPipeline)
   src/AgentEval.Core                → PerformanceBenchmark MOVED OUT — see below
   src/AgentEval.Evals.Performance   → NEW lightweight assembly: PerformanceBenchmark
```

**Assembly structure changes** (delta from today):

```
+ src/AgentEval.Evals.Compliance/       (was samples/AgentEval.GdprBenchmark + samples/AgentEval.EuAiActBenchmark, merged)
+ src/AgentEval.Evals.Performance/      (was src/AgentEval.Core/Benchmarks/PerformanceBenchmark.cs)
~ src/AgentEval.RedTeam/                (adds OwaspBenchmark.cs + MitreBenchmark.cs façades)
~ src/AgentEval.Memory/                 (MemoryBenchmark NS renamed → AgentEval.Benchmarks)
~ src/AgentEval.Evals.Agentic/          (AgenticBenchmark NS renamed → AgentEval.Benchmarks)
- src/AgentEval.Core/Benchmarks/        (folder deleted)
- samples/AgentEval.GdprBenchmark/      (replaced by a tiny new sample that consumes the promoted assembly)
- samples/AgentEval.EuAiActBenchmark/   (same)
```

The namespace `AgentEval.Benchmarks` is the **only** thing a user types in a `using` directive when they want to compose evaluations. The actual `*Benchmark` static classes are split-declared across the domain assemblies.

**Naming convention**: `{Domain}Benchmark.{Preset}(args)` returns `CompositeEval` *wherever possible*. Where the domain forbids it (`MemoryBenchmark`, `PerformanceBenchmark`), the method instead returns a domain-specific runner (e.g. `MemoryBenchmark.Standard.RunAsync(agent)` or `PerformanceBenchmark.Latency(agent).MeasureAsync(prompt)`).

**CLI discovery**: a registry of benchmark presets keyed by `(family, preset)` lives in the CLI itself. `agenteval bench {family} --preset {name}` dispatches through it. Each family is a one-liner in the CLI.

**NuGet packaging**: zero change. Umbrella still embeds 7 sub-assemblies (8 with the new Compliance one); consumers see one DLL surface (`AgentEval.dll` + sibling DLLs in `lib/{tfm}/`). The Compliance assembly is added to the umbrella's `<ProjectReference>` list with `PrivateAssets="all"`.

**Migration cost**: medium-high. Big mechanical move + namespace rename across ~110 production files and ~200 test files. Estimated 1.5–2 working days for one developer with care, half a day with `dotnet format` and search-replace and a build-fix loop.

**Pros**:
- Achieves the user's "one namespace, one discovery point" goal precisely.
- Physical layering preserves Core-can-depend-on-nothing-else rule.
- Adding OWASP/MITRE costs essentially one file each (façades over existing machinery).
- Compliance promotion is genuinely justified by the CLI's hard dependency on them.

**Cons**:
- Namespace-rename across the entire test suite + samples + docs is real chore work.
- "Split-declared static partial classes" is a slight novelty; some teams find it hard to grep for "all members of `OwaspBenchmark`".
- The `using AgentEval.Benchmarks;` import imports *everything* — if a user only wants Agentic they still see GDPR types in autocomplete. (Mitigation: a single `using` is what the user asked for.)

---

### Option B — "Domain-namespace status quo, plus an Index helper"

**Idea**: Leave the namespaces alone. Each benchmark stays in its domain namespace (`AgentEval.Evals.Agentic.AgenticBenchmark`, `AgentEval.Compliance.Gdpr.GdprBenchmark`, …). Add a single static class `AgentEval.Benchmarks` (no namespace, just a static index/discovery type) that exposes typed delegates to each preset:

```csharp
public static class Benchmarks
{
    public static class Agentic
    {
        public static Func<IEvaluator, CompositeEval> Execution => AgenticBenchmark.AgenticExecution;
        // ...
    }
    public static class Gdpr
    {
        public static Func<ArticlesRegistry, CompositeEval> Standard => GdprBenchmark.Standard;
        // ...
    }
    public static class Owasp { /* ... */ }
}
```

The Index assembly (let's call it `AgentEval.Benchmarks.Index`) project-references every domain assembly. The user writes:

```csharp
var preset = Benchmarks.Gdpr.Standard(articles);
var result = await preset.EvaluateAsync(input);
```

Compliance benchmarks still get promoted out of `samples/`, and `PerformanceBenchmark` still gets relocated, but the namespace topology is otherwise unchanged.

**Migration cost**: low — the promotion + relocation is mechanical, and the Index is additive. Estimated half a day.

**Pros**:
- Lowest blast radius: existing namespace imports still work, tests barely change.
- Discoverability via `Benchmarks.` is good — IDE autocomplete after one dot is the single discovery point the user wants.
- The Index is a real type, greppable, with one canonical doc location.

**Cons**:
- "Two ways to invoke the same benchmark" (canonical domain namespace + Index helper) is a long-term documentation tax — every README example has to pick one and stick with it.
- The Index has to be kept in sync manually as new presets are added. It's a registration boilerplate file that grows with every benchmark.
- Doesn't actually address the user's deeper instinct that "benchmark wrappers belong in one place" — it adds a façade rather than fixing the layout.

---

### Option C — "Promote-and-organise, no namespace unification"

**Idea**: Accept that the namespaces are domain-driven and *should stay* domain-driven. Do the minimum needed to remove the smells:

1. Promote `samples/AgentEval.GdprBenchmark/` → `src/AgentEval.Evals.Compliance/Gdpr/` with namespace `AgentEval.Compliance.Gdpr`.
2. Promote `samples/AgentEval.EuAiActBenchmark/` → `src/AgentEval.Evals.Compliance/EuAiAct/` with namespace `AgentEval.Compliance.EuAiAct`.
3. Move `src/AgentEval.Core/Benchmarks/PerformanceBenchmark.cs` → `src/AgentEval.Evals.Performance/PerformanceBenchmark.cs` with namespace `AgentEval.Evals.Performance` (or keep it in Core but at project root, not in `Benchmarks/`).
4. Add `OwaspBenchmark` + `MitreBenchmark` static classes in `AgentEval.RedTeam.Compliance` namespace, façades over `AttackPipeline`.
5. Replace the deleted samples with two small new samples (`samples/AgentEval.GdprBenchmark.Sample/` etc.) that *consume* the promoted assemblies the same way `AgentEval.TravelDemo` consumes the others.
6. **Document a canonical table** in the README + benchmarks.md mapping (family → namespace → entry-point class). One sentence per row.

**Naming convention**: every preset-factory class is named `{Family}Benchmark` and lives in `AgentEval.{Family-area}` or `AgentEval.{Family-area}.{Sub-area}`. There is no enforced unifying namespace — but there is an enforced naming pattern.

**Migration cost**: lowest. Mostly file moves, csproj edits, and namespace updates within the moved files. Estimated 4–6 hours.

**Pros**:
- Smallest possible change to fix the genuine smells.
- Preserves the domain assembly's natural layering.
- Doesn't require new helper types whose maintenance becomes its own debt.
- Each benchmark family remains discoverable in its own domain — when someone asks "what compliance benchmarks do we have?" the answer is "look in `src/AgentEval.Evals.Compliance/`" and they find them all.

**Cons**:
- Does not give the user the "one `using` directive imports all benchmarks" experience. They keep typing `using AgentEval.Compliance.Gdpr; using AgentEval.Evals.Agentic; ...`. This is the experience that bothered them.
- "Where do I find the benchmarks?" still requires reading docs to discover the namespace fan-out, rather than typing `AgentEval.Benchmarks.` and letting autocomplete answer.

---

## Part 3 — The new OWASP / MITRE benchmarks

This is the most concrete design work in this document, and the part where the existing RedTeam infrastructure does most of the heavy lifting. The user's framing — "the underlying evaluators + reporters already exist, just lacks named-preset wrappers" — is exactly correct.

### 3.1 What attacks map to what

Every `IAttackType` in `src/AgentEval.RedTeam/RedTeam/Attacks/` already exposes `OwaspLlmId` and `MitreAtlasIds` properties. Verified mapping (from `Attack.ByOwaspId` in `src/AgentEval.RedTeam/RedTeam/Attack.cs`):

| OWASP LLM Top 10 (2025) | Attacks already implemented |
|---|---|
| **LLM01 — Prompt Injection** | `PromptInjectionAttack`, `JailbreakAttack`, `IndirectInjectionAttack`, `EncodingEvasionAttack` |
| **LLM02 — Sensitive Information Disclosure** | `PIILeakageAttack` |
| LLM03 — Supply Chain | (none — fundamentally not testable at the agent layer) |
| LLM04 — Data and Model Poisoning | (none) |
| **LLM05 — Improper Output Handling** | `InsecureOutputAttack` |
| **LLM06 — Excessive Agency** | `ExcessiveAgencyAttack` |
| **LLM07 — System Prompt Leakage** | `SystemPromptExtractionAttack` |
| LLM08 — Vector / Embedding weaknesses | (none — requires RAG harness scope) |
| LLM09 — Misinformation | (none — addressed via agentic-suite hallucination evaluators) |
| **LLM10 — Unbounded Consumption** | `InferenceAPIAbuseAttack` |

That's **6 of 10 OWASP categories covered today**, with 9 attack types feeding them. `OWASPComplianceReporter` already knows about all 10 categories and reports the non-tested ones as `NotTested` / `NotApplicable`.

MITRE ATLAS coverage (the ML-focused MITRE framework — what RedTeam already maps to, not ATT&CK proper):

```
AML.T0051 (LLM Prompt Injection)     ← PromptInjectionAttack
AML.T0054 (LLM Jailbreak)            ← JailbreakAttack
AML.T0048 (External Data Manipulation) ← IndirectInjectionAttack
AML.T0057 (LLM Data Leakage)         ← PIILeakageAttack
AML.T0067 (LLM-Enabled Product Abuse) ← ExcessiveAgencyAttack
... (etc — verifiable per-attack via the MitreAtlasIds property)
```

Note: the user said "OWASP + MITRE ATT&CK / ATLAS". The codebase already chose **ATLAS** specifically (the LLM-focused framework). ATT&CK (the broader enterprise framework) is the wrong target — agent-level scans don't generally produce ATT&CK-grade artefacts. Keep ATLAS; mention ATT&CK in docs only as "we map to MITRE ATLAS, the AI-focused sibling of ATT&CK".

### 3.2 Preset design

`OwaspBenchmark` should expose at minimum:

```csharp
public static class OwaspBenchmark
{
    /// Runs all 9 implemented attacks at Quick intensity. Reports against all 10 categories;
    /// LLM03/04/08/09 will show as NotTested in the report.
    public static OwaspBenchmarkRun Top10(IEvaluator? judge = null);

    /// Three-attack MVP: PromptInjection + Jailbreak + PIILeakage. CI-friendly.
    public static OwaspBenchmarkRun Smoke(IEvaluator? judge = null);

    /// Audit-grade: all attacks at Thorough intensity, longer timeout, evidence-on.
    public static OwaspBenchmarkRun AuditGrade(IEvaluator? judge = null);

    /// Domain-pack: extend Top10 with prompt-injection variants specific to RAG agents.
    public static OwaspBenchmarkRun Top10ForRag(IEvaluator? judge = null);
}
```

`OwaspBenchmarkRun` is a thin wrapper around a pre-configured `AttackPipeline` plus a target reporter. It exposes:

```csharp
public sealed class OwaspBenchmarkRun
{
    public Task<RedTeamResult> ScanAsync(IEvaluableAgent agent, CancellationToken ct = default);
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default);   // adapter to CompositeEval shape
    public OWASPComplianceReport GenerateReport(RedTeamResult result);
}
```

The `EvaluateAsync` adapter is the **important** design decision. It lets OWASP and MITRE results flow through the **same output-store / audit-chain plumbing** as `AgenticBenchmark` and `GdprBenchmark`, by mapping each attack-category to a synthetic `EvalResult` leaf with `score = passRate` and audit evidence drawn from the `OWASPComplianceReport`. This is **how we unify the result types** — by giving every benchmark family an `EvaluateAsync(EvalInput) → EvalResult` path even when the underlying machinery is different.

`MitreBenchmark` follows the same shape with the ATLAS-flavoured reporter:

```csharp
public static class MitreBenchmark
{
    public static MitreBenchmarkRun AtlasBaseline(IEvaluator? judge = null);    // all 9 attacks, ATLAS-tagged report
    public static MitreBenchmarkRun AtlasSmoke(IEvaluator? judge = null);
    public static MitreBenchmarkRun AtlasAuditGrade(IEvaluator? judge = null);
}
```

### 3.3 Reuse vs wrap

**Reuse, do not duplicate.** The `OwaspBenchmark` and `MitreBenchmark` façades are ~80 LOC each, including doc comments. They internally construct an `AttackPipeline.Create()....`, run it, and feed the result into the existing reporters. They add zero new attacks, zero new evaluators, zero new reporters. The win is purely API-shape: a named preset factory that ships under the same namespace as the other benchmarks and produces a result that flows through the unified pipeline.

### 3.4 CLI invocation

Two new sub-commands, both trivial:

```
agenteval bench owasp --preset top10 --subject my-agent --input "..."
agenteval bench owasp --preset smoke --subject my-agent
agenteval bench owasp --preset audit --subject my-agent --input "..."

agenteval bench mitre --preset atlas-baseline --subject my-agent
agenteval bench mitre --preset atlas-smoke --subject my-agent
agenteval bench mitre --preset atlas-audit --subject my-agent
```

Implementation in CLI: one new `BenchOwaspCommand.cs` + `BenchMitreCommand.cs`, each ~150 LOC, modelled exactly on `BenchAgenticCommand.cs` (which exists at `src/AgentEval.Cli/Commands/BenchAgenticCommand.cs`). The judge resolution + workspace-root + output-store wiring is shared with the other bench commands.

### 3.5 Output shape

The user asked: "do they produce the standard `EvalResult` + audit-chain evidence the other benchmarks produce, or do they produce richer red-team-specific reports?"

**Both.** The default output is the standard `EvalResult` + per-leaf audit chain entries — that's what `agenteval bench owasp` writes by default, so downstream tooling (Mission Control, compliance render, etc.) sees a uniform shape. The richer red-team-specific report (`OWASPComplianceReport`) is *additionally* emitted to `compliance/owasp-{ts}/` exactly the way the GDPR/EuAiAct reporters already do — see `samples/AgentEval.GdprBenchmark/Reporting/GDPRComplianceReporter.cs` for the pattern. **Don't pick one — emit both, the cost is negligible.**

---

## Part 4 — Migration plan for the recommended option

I am recommending **a hybrid of Option A and Option C** below (see Part 5). The migration plan that follows reflects that hybrid.

### 4.1 Assembly moves

| From | To | Notes |
|---|---|---|
| `samples/AgentEval.GdprBenchmark/` (51 .cs files) | `src/AgentEval.Evals.Compliance.Gdpr/` | New assembly, `IsPackable=false`, embedded in umbrella. Drops `OutputType=Exe`; `Program.cs` becomes part of a thin new sample project (see below). |
| `samples/AgentEval.EuAiActBenchmark/` (53 .cs files) | `src/AgentEval.Evals.Compliance.EuAiAct/` | Same. Project-refs the new Gdpr assembly (current EuAiAct refs Gdpr — preserve this). |
| `src/AgentEval.Core/Benchmarks/PerformanceBenchmark.cs` (+ result records) | `src/AgentEval.Evals.Performance/PerformanceBenchmark.cs` | New assembly, refs Core only. |
| (NEW) | `src/AgentEval.RedTeam/RedTeam/Compliance/OwaspBenchmark.cs` | Façade, ~80 LOC. |
| (NEW) | `src/AgentEval.RedTeam/RedTeam/Compliance/MitreBenchmark.cs` | Façade, ~80 LOC. |
| (NEW samples, replace the deleted ones) | `samples/AgentEval.GdprBenchmark.Demo/` + `samples/AgentEval.EuAiActBenchmark.Demo/` | Each ~50 LOC, just shows how to consume the promoted assembly. |

### 4.2 Namespace changes

Adopt **a single discovery namespace `AgentEval.Benchmarks`** for the top-level preset-factory classes only. The internals stay in their domain namespaces.

| Class | Old namespace | New namespace |
|---|---|---|
| `AgenticBenchmark` | `AgentEval.Evals.Agentic` | `AgentEval.Benchmarks` |
| `GdprBenchmark` | `AgentEval.GdprBenchmark` | `AgentEval.Benchmarks` |
| `EuAiActBenchmark` | `AgentEval.EuAiActBenchmark` | `AgentEval.Benchmarks` |
| `MemoryBenchmark` (the config record) | `AgentEval.Memory.Models` | `AgentEval.Benchmarks` |
| `OwaspBenchmark` (new) | — | `AgentEval.Benchmarks` |
| `MitreBenchmark` (new) | — | `AgentEval.Benchmarks` |
| `PerformanceBenchmark` | `AgentEval.Benchmarks` (already!) | `AgentEval.Benchmarks` (unchanged) |
| `LongMemEvalBenchmark` (NEW façade) | — | `AgentEval.Benchmarks` |

Note that `PerformanceBenchmark` is **already** in `AgentEval.Benchmarks` — see `src/AgentEval.Core/Benchmarks/PerformanceBenchmark.cs:8`. Its placement at the *namespace* level has always been correct; only its assembly placement was wrong.

All *runners*, all *internal types* (`ArticlesRegistry`, `Pillar1Foundations`, `ScenarioToAtomicEval`, `TaskCompletionEval`, `MemoryBenchmarkRunner`, etc.) stay in their domain namespaces. Only the **public preset-factory entry-points** lift to `AgentEval.Benchmarks`. That preserves the user's "one using directive" goal **and** keeps assembly layering honest.

Deprecation story (for 0.x-beta consumers reading old docs): a one-paragraph migration note in CHANGELOG with the namespace rename table. We are in 0.x-beta; we can break, but the old paths are not preserved with type-forwards since the cost of a `using` change is small.

### 4.3 File moves (concrete list)

```
move samples/AgentEval.GdprBenchmark/**          → src/AgentEval.Evals.Compliance.Gdpr/**
move samples/AgentEval.EuAiActBenchmark/**       → src/AgentEval.Evals.Compliance.EuAiAct/**
move src/AgentEval.Core/Benchmarks/PerformanceBenchmark.cs → src/AgentEval.Evals.Performance/PerformanceBenchmark.cs
move src/AgentEval.Core/Benchmarks/{LatencyBenchmarkResult,ThroughputBenchmarkResult,CostBenchmarkResult,PerformanceBenchmarkOptions,etc.}.cs
                                                  → src/AgentEval.Evals.Performance/

create src/AgentEval.Evals.Compliance.Gdpr/AgentEval.Evals.Compliance.Gdpr.csproj
create src/AgentEval.Evals.Compliance.EuAiAct/AgentEval.Evals.Compliance.EuAiAct.csproj
create src/AgentEval.Evals.Performance/AgentEval.Evals.Performance.csproj
create src/AgentEval.RedTeam/RedTeam/Compliance/OwaspBenchmark.cs                 (~80 LOC)
create src/AgentEval.RedTeam/RedTeam/Compliance/MitreBenchmark.cs                 (~80 LOC)
create samples/AgentEval.GdprBenchmark.Demo/Program.cs                            (~50 LOC consumer demo)
create samples/AgentEval.EuAiActBenchmark.Demo/Program.cs                         (~50 LOC consumer demo)

update src/AgentEval/AgentEval.csproj                                             (add 3 new ProjectReferences with PrivateAssets="all")
update src/AgentEval.Cli/AgentEval.Cli.csproj                                     (replace samples/ ProjectReferences with src/AgentEval.Evals.Compliance.* ProjectReferences; add Performance + add nothing for RedTeam since it's already in umbrella via PerformanceBenchmark? No — RedTeam wasn't a CLI dep yet; add ProjectReference)
update src/AgentEval.Cli/Commands/BenchCommand.cs                                 (rename using directives)
update src/AgentEval.Cli/Commands/BenchEuAiActCommand.cs                          (rename using directives)
update src/AgentEval.Cli/Commands/BenchAgenticCommand.cs                          (rename using directives)
create src/AgentEval.Cli/Commands/BenchOwaspCommand.cs                            (~150 LOC, modelled on BenchAgenticCommand)
create src/AgentEval.Cli/Commands/BenchMitreCommand.cs                            (~150 LOC, modelled on BenchAgenticCommand)
create src/AgentEval.Cli/Commands/BenchPerfCommand.cs                             (~150 LOC; was previously CLI-less)
update src/AgentEval.Cli/Program.cs                                               (wire up benchOwaspCmd, benchMitreCmd, benchPerfCmd)

mass-rename across tests/                                                         (using AgentEval.Evals.Agentic; → using AgentEval.Benchmarks; for AgenticBenchmark refs)
mass-rename across tests/                                                         (using AgentEval.GdprBenchmark; → using AgentEval.Benchmarks; for GdprBenchmark refs)
... etc.

delete src/AgentEval.Core/Benchmarks/ (entire folder)
```

Estimated diff size: ~2,500 lines net moved, ~400 lines new, ~150 lines deleted.

### 4.4 What public-API consumers see

Any v0.x-beta consumer of the umbrella package upgrading from v0.9.0 to v0.10.0 needs to change exactly **one thing**: replace fan-out `using` directives with a single `using AgentEval.Benchmarks;`. Example before/after:

```csharp
// v0.9.0
using AgentEval.Evals.Agentic;
using AgentEval.GdprBenchmark;
using AgentEval.GdprBenchmark.Articles;

var agentic = AgenticBenchmark.AgenticExecution(judge);
var gdpr = GdprBenchmark.Standard(articles);

// v0.10.0
using AgentEval.Benchmarks;
using AgentEval.Compliance.Gdpr.Articles;  // for ArticlesRegistry — internal type stays in its domain

var agentic = AgenticBenchmark.AgenticExecution(judge);
var gdpr = GdprBenchmark.Standard(articles);
var owasp = OwaspBenchmark.Top10(judge);         // NEW
var mitre = MitreBenchmark.AtlasBaseline(judge); // NEW
var perf = PerformanceBenchmark.LatencyOf(agent).MeasureAsync("...");  // RESHAPED
```

### 4.5 CHANGELOG entries needed

```markdown
## 0.10.0-beta

### Breaking changes

- **Namespace consolidation for benchmark preset factories.** Top-level benchmark factory classes
  (`AgenticBenchmark`, `GdprBenchmark`, `EuAiActBenchmark`, `MemoryBenchmark`, `PerformanceBenchmark`)
  now all live in the namespace `AgentEval.Benchmarks`. Internal types (registries, pillars, evaluators)
  remain in their domain namespaces. Replace fan-out `using` directives with a single
  `using AgentEval.Benchmarks;` import. The internal namespace fan-out for builder/registry types is
  unchanged.

- **`GdprBenchmark` and `EuAiActBenchmark` promoted out of `samples/`.** The benchmarks have been
  promoted to first-class product assemblies (`src/AgentEval.Evals.Compliance.Gdpr/`,
  `src/AgentEval.Evals.Compliance.EuAiAct/`). The sample Exe projects in `samples/` have been replaced
  with thinner consumer demos (`samples/AgentEval.GdprBenchmark.Demo/`,
  `samples/AgentEval.EuAiActBenchmark.Demo/`) that show how to invoke the promoted assemblies.

- **`PerformanceBenchmark` relocated.** Moved from `src/AgentEval.Core/Benchmarks/` to a new
  `src/AgentEval.Evals.Performance/` assembly. Public namespace `AgentEval.Benchmarks` is unchanged;
  no consumer change required.

### New features

- **`OwaspBenchmark`** preset factory in `AgentEval.Benchmarks`. Presets: `Top10`, `Smoke`, `AuditGrade`,
  `Top10ForRag`. Wraps the existing `AttackPipeline` and emits both a `CompositeEval`-flavoured
  `EvalResult` (flowing through the output-store / audit-chain) and an `OWASPComplianceReport`.
  Covers 6 of 10 OWASP LLM Top 10 v2.0 (2025) categories via 9 attack types. CLI: `agenteval bench owasp`.

- **`MitreBenchmark`** preset factory in `AgentEval.Benchmarks`. Presets: `AtlasBaseline`, `AtlasSmoke`,
  `AtlasAuditGrade`. MITRE ATLAS-flavoured façade over the same attack pipeline.
  CLI: `agenteval bench mitre`.

- **`agenteval bench perf` CLI command** for the previously CLI-less `PerformanceBenchmark`. Subcommands
  `latency`, `throughput`, `cost`. Output flows through the unified output-store.
```

### 4.6 v0.9.0-beta vs v0.10.0-beta

**Bundle this with v0.10.0-beta. Do not delay v0.9.0-beta.**

Rationale: v0.9.0-beta's legacy-removal is a clean, independent commit with a clear-cut value proposition. Bolting a 2,500-line assembly reorg onto it triples its review surface and introduces uncorrelated regression risk. Better to merge v0.9.0-beta now (it's merge-ready per Last Review #9), then open `feature/v0.10.0-unified-benchmarks` for the architecture move with its own test-pass requirement and its own opus review pass.

The two changes are also rhetorically distinct: v0.9.0 is "we removed dead code"; v0.10.0 is "we expanded the benchmark suite + unified the namespace." Each deserves its own release notes paragraph and CHANGELOG entry.

---

## Part 5 — Recommendation

### 5.1 Pick **Option A**, with the layout details from Part 4.

**Why Option A over B**:
- Option B preserves namespace status-quo and grafts on an Index helper. That helper is a maintenance burden that grows monotonically, and it gives users two ways to invoke every benchmark — a forever-documentation tax that the team will pay every time someone adds a preset. The user's instinct that "one namespace" is the right unifier is correct; an Index helper is a half-measure that papers over the namespace fragmentation without resolving it.
- Option B also doesn't address the deeper "result-type heterogeneity" issue at all, whereas Option A's required `EvaluateAsync` adapter on `OwaspBenchmarkRun` / `MitreBenchmarkRun` *forces* the convergence that produces real downstream value (uniform output-store records, uniform Mission Control rendering).

**Why Option A over C**:
- Option C is the minimum-blast-radius play and is genuinely tempting. It fixes the smells the user named (samples/, Core/Benchmarks/) and adds the requested OWASP/MITRE wrappers. But it leaves the namespace fan-out in place, which means six months from now we are still typing `using AgentEval.Compliance.Gdpr; using AgentEval.Compliance.EuAiAct; using AgentEval.Evals.Agentic; using AgentEval.Memory.Models; using AgentEval.Benchmarks;` for one composite test. The user explicitly flagged this as wrong; Option C does not address it.
- Option C also has the worse marketing story. "AgentEval has a benchmark suite — look across these 5 namespaces" is less compelling than "AgentEval has a benchmark suite — `using AgentEval.Benchmarks;`".

### 5.2 Bundle OWASP/MITRE benchmarks **now** (with v0.10.0-beta), not as a v0.11.0-beta follow-on.

Three reasons:
- The underlying machinery is *already there*. The wrappers are ~80 LOC each. There is no engineering reason to defer them, and shipping the namespace unification without including OWASP/MITRE means doing two breaking-namespace releases back-to-back instead of one, which is rude to consumers.
- The marketing pitch the user is after — "AgentEval's Benchmark Suite: Agentic + GDPR + EU AI Act + Memory + OWASP + MITRE + Performance" — only lands if all seven are simultaneously available. Shipping six of seven and saying "OWASP coming soon" undercuts the launch.
- The OWASP/MITRE wrappers also force a useful design decision early: the `EvaluateAsync` adapter on `OwaspBenchmarkRun`. If we ship the namespace unification first and add OWASP later, we're more likely to forget the adapter and end up with a `RedTeamResult` outlier in the otherwise-uniform output-store pipeline. **Doing them together keeps the homogenisation discipline visible.**

### 5.3 Ship v0.9.0-beta **as-is** (legacy removal only). Architecture move is v0.10.0-beta.

Already argued in §4.6. The TL;DR: v0.9.0 is small, clean, and merge-ready. v0.10.0 is large and architectural. They should not share a tag.

### 5.4 Trade-offs the user may not have considered

These are the things I would flag explicitly before the user signs off:

1. **The compliance assemblies will get bigger over time.** `Compliance.Gdpr` is already 51 source files. Adding HIPAA, PCI-DSS, ISO 42001, etc. as siblings would push `src/AgentEval.Evals.Compliance.*/` to >300 files within a year if the roadmap stays aggressive. Consider whether the long-term naming is `AgentEval.Evals.Compliance.Gdpr` (one assembly per regulation, scales out) or `AgentEval.Evals.Compliance` (one assembly with N subnamespaces, scales up). I recommend **the former** — one assembly per regulation — because regulations have wildly different runtime cost profiles (YAML files, embedded judge prompts, judge calibration baselines) and bundling them all into one assembly means consumers always pay for all of them. The umbrella NuGet papers over this externally; internally one-asm-per-regulation keeps build times sane.

2. **`MemoryBenchmark` is a config record, not a factory.** The naming convention in §4.2 makes it look like a peer of `AgenticBenchmark`, but its shape is different: `MemoryBenchmark.Quick` is a *property* returning a config record, not a *method* returning a `CompositeEval`. This is fine — the domain demands a runner — but it should be **documented as the one intentional irregularity** in the otherwise uniform `{Family}Benchmark.{Preset}(args) → CompositeEval` pattern. Don't pretend it's the same shape; call out the irregularity, justify it ("multi-turn agent state is required"), and move on.

3. **`PerformanceBenchmark` does NOT produce `CompositeEval`-shaped results today.** Its result types are `LatencyBenchmarkResult` / `ThroughputBenchmarkResult` / `CostBenchmarkResult`, none of which are `EvalResult`. To make it flow through the unified output-store the same way Agentic/Gdpr/EuAiAct/Owasp/Mitre do, we either (a) add an adapter that synthesises an `EvalResult` per metric, or (b) accept it as a second intentional irregularity. I lean toward (a) — adapt — because Mission Control rendering becomes weirder if perf results don't show up alongside the others. The adapter is ~50 LOC. Don't bake this assumption silently; **call it out** in the v0.10.0 design doc so it's a conscious decision rather than something we discover at integration time.

4. **`LongMemEval` external benchmark — promote to a `LongMemEvalBenchmark` façade?** Yes. It's already in `src/AgentEval.Memory/External/LongMemEval/`. Add a `LongMemEvalBenchmark` static class in `AgentEval.Benchmarks` namespace with `.Subset()` and `.Full()` presets that return preconfigured `LongMemEvalBenchmarkRunner` instances. The marketing line is now "AgentEval supports the LongMemEval academic benchmark" which is a real credibility signal in research-leaning audiences. Cost: ~40 LOC and zero new machinery.

5. **The umbrella's ProjectReference list will grow to ~10 entries.** Today it's 7 (`Abstractions`, `Core`, `DataLoaders`, `Evals.Agentic`, `MAF`, `Memory`, `RedTeam`). After this move: + `Evals.Compliance.Gdpr`, + `Evals.Compliance.EuAiAct`, + `Evals.Performance` = 10. That's fine — the `<TargetsForTfmSpecificBuildOutput>` MSBuild target scales linearly — but be aware that **NuGet package size will grow noticeably** (the GDPR project alone has ~30 embedded YAML files + ~10 embedded MD prompts). Recommend running `dotnet pack` early in the v0.10.0 branch and comparing the resulting `.nupkg` size; if it crosses 5–10 MB consumers will notice. Mitigation if needed: split the umbrella into `AgentEval` (core + agentic) and `AgentEval.Compliance` (gdpr + euaiact) — but **don't pre-emptively split**, just measure first.

6. **CLI now has 7 bench sub-commands** (agentic, gdpr, eu-ai-act, owasp, mitre, perf, plus an eventual memory). Consider adding `agenteval bench --list` and `agenteval bench {family} --help` that exposes the preset catalogue. This is a polish item, not a blocker, but it's the discoverability win the user is after **at the CLI layer**, complementing the namespace win at the API layer.

7. **Sample-project Program.cs files**: today, `samples/AgentEval.GdprBenchmark/Program.cs` is a working end-to-end demonstration with golden-dataset calibration. When we promote the library to `src/`, that Program.cs needs to either be deleted or extracted into a new `samples/AgentEval.GdprBenchmark.Demo/Program.cs`. **Keep it** — it's good educational material and we'd be giving up real working DX if we lose it. Just move it down into the demo project and project-reference the promoted assembly.

---

## Next steps if accepted

In priority order:

1. **Merge v0.9.0-beta as-is** (legacy removal only). Tag, push, publish. ~1 hour.
2. **Open `feature/v0.10.0-unified-benchmarks` branch**.
3. **Create `src/AgentEval.Evals.Compliance.Gdpr/`** and move the 51 .cs files + YAML + embedded resources. Update csproj, namespaces in-place. Verify build + all GDPR tests pass. ~4 hours.
4. **Create `src/AgentEval.Evals.Compliance.EuAiAct/`** and do the same for the 53 .cs files. Verify build + EuAiAct tests pass. ~3 hours.
5. **Create `src/AgentEval.Evals.Performance/`** and relocate `PerformanceBenchmark.cs` + result types. Add `EvalResult` adapter for unified output-store flow. Verify perf tests pass. ~2 hours.
6. **Rename top-level factory namespaces** (`AgenticBenchmark`, `GdprBenchmark`, `EuAiActBenchmark`, `MemoryBenchmark`) → `AgentEval.Benchmarks`. Internal types stay in domain namespaces. Mass-rename usings across tests + CLI + samples. ~3 hours including build-fix loop.
7. **Implement `OwaspBenchmark` in `src/AgentEval.RedTeam/RedTeam/Compliance/`** — façade over `AttackPipeline` with `Top10` / `Smoke` / `AuditGrade` / `Top10ForRag` presets. Implement `EvaluateAsync` adapter to `EvalResult`. ~3 hours.
8. **Implement `MitreBenchmark`** symmetrically. ~2 hours (most code reused from `OwaspBenchmark`).
9. **Implement `LongMemEvalBenchmark` façade** in `AgentEval.Benchmarks` namespace, residing in `src/AgentEval.Memory/`. ~1 hour.
10. **Add CLI commands** `bench owasp`, `bench mitre`, `bench perf`. ~4 hours total (3 commands × ~150 LOC each + Program.cs wiring + integration tests).
11. **Add new sample projects** `samples/AgentEval.GdprBenchmark.Demo/` and `samples/AgentEval.EuAiActBenchmark.Demo/` (~50 LOC each, pure consumers). ~1 hour.
12. **Update CHANGELOG, README, docs/benchmarks.md, the strategy folder ADRs.** ~2 hours.
13. **Run full test suite** (3625+ tests). Fix breakages. ~2 hours.
14. **Run `dotnet pack`** and inspect the resulting `.nupkg` size. If >10 MB, file a follow-up to evaluate splitting `AgentEval.Compliance` into its own umbrella. ~30 minutes.
15. **Opus review pass** (last review #11) on the whole v0.10.0 branch before merge. Half a day.
16. **Merge to main, tag v0.10.0-beta, publish.** ~1 hour.

**Total estimated effort**: ~28–32 hours of focused work, plus the opus review pass. Realistic calendar time: 4–5 working days for one engineer who is the only person touching the branch, with the opus review on the side.

**Total LOC delta estimate**: +~700 (OWASP/MITRE/Performance/LongMemEval façades + CLI commands + demos + adapters) net of moved code; ~2,500 LOC of pure relocation. Big diff, small functional surface change for existing consumers.

---

**End of review.** I'm confident in the recommendation. If you want a written ADR (`docs/adr/015-unified-benchmarks-namespace.md`) as the durable record of this decision before implementation starts, that's the natural next deliverable — but it would be a faithful condensation of Parts 1, 2.A, 3, and 5 of this document, not new content.

---

## Execution playbook (v0.10.0-beta)

**Author**: Opus #11 (planning architect)
**Date**: 2026-05-17
**Branch**: `feature/v0.10.0-unified-benchmarks` (created from `main` @ `e9d8b8c` — v0.9.0-beta tag)
**Baseline build state**: clean, 3625 tests pass on net10.0
**ADR**: `docs/adr/017-unified-benchmarks-namespace.md` (accepted 2026-05-17)

This playbook expands Opus #10's architectural decision into a phased, granular execution plan. Each task brief is self-contained enough that a Sonnet-class model can ship it from the brief alone, given the repo at the named state. Verification gates between phases are non-negotiable: the next phase does not start until the named gate is green.

### Phase map (one-line summary)

| Phase | Theme | Gate | Effort |
|---|---|---|---|
| 0 | Branch + scaffolding | branch exists, baseline green | ✅ done |
| 1 | Promote GDPR `samples/` → `src/` | full suite green; GDPR E2E + calibration + schema round-trip pass; **Opus gate review** | 5h |
| 2 | Promote EU AI Act `samples/` → `src/` | full suite green; EuAiAct E2E + cross-regulation linker tests pass; **Opus gate review** | 4h |
| 3 | Relocate Performance to new assembly + `EvaluateAsync` adapter | full suite green; new `EvalResult` adapter round-trip test passes; **Opus gate review** | 3h |
| 4 | Namespace consolidation of *top-level factories* under `AgentEval.Benchmarks` | full suite green; new `BenchmarkNamespaceContractTests` passes; **Opus gate review (high-risk)** | 4h |
| **4b** | **Compliance *internal* namespace + assembly rename: `AgentEval.GdprBenchmark.*` → `AgentEval.Compliance.Gdpr.*`; `AgentEval.EuAiActBenchmark.*` → `AgentEval.Compliance.EuAiAct.*`; rename assemblies + directories to match** | full suite green; embedded-resource manifest paths re-verified; `BenchmarkNamespaceContractTests` extended; **Opus gate review** | 4h |
| 5 | `OwaspBenchmark` façade + CLI `bench owasp` | new façade tests + CLI e2e tests; output-store flow validated; **Opus gate review** | 4h |
| 6 | `MitreBenchmark` façade + CLI `bench mitre` | symmetric to phase 5; **Opus gate review** | 3h |
| 7 | `LongMemEvalBenchmark` façade | façade tests + CLI smoke test; **Opus gate review** | 2h |
| 8 | CLI helpers (`bench --list`, family help, `bench perf`) backed by **`BenchmarkFamilyRegistry`** (canonical single-source-of-truth) | discoverability tests; perf CLI smoke test; registry-extensibility test; **Opus gate review** | 5h |
| 9 | Doc sweep, CHANGELOG, ADR-017 status, sample-project demos, **`EvaluateAsync` adapter pattern documented in `docs/architecture.md` as canonical homogenisation primitive** | docs build clean; sample demos compile and run; architecture doc reviewed; **Opus gate review** | 4h |
| 10 | Final Opus review + tag + release v0.10.0-beta | opus sign-off; `dotnet pack` size check < 10 MB | 3h |

**Total estimated effort**: 38 engineering hours + ~5 hours Opus review = **~43 hours**. Realistic calendar: 6–7 working days with no parallelism (one engineer + one reviewer). The increase from the original 39h estimate reflects (a) inserting Phase 4b for the compliance internal namespace rename, (b) explicitly elevating the BenchmarkFamilyRegistry to canonical convention in Phase 8, and (c) adding the architecture-doc convention writeup in Phase 9.

> **Tracking discipline (non-negotiable)**: the master tracking table below MUST be updated after each task lands AND after each Opus gate review returns. The status column is the single source of truth for "where are we right now" — never leave it stale across a session boundary. The reviewing agent that returns a Phase verdict MUST flip the relevant rows to ✅ (or 🟡/❌ with notes) before signing off; the executing agent MUST flip rows to 🟦/✅ as each task completes.

> **Opus gate-review convention (introduced 2026-05-17)**: every phase ends with an explicit Opus gate-review task (the final P{n}.{last} row). The gate-review reads what landed, verifies acceptance criteria, attempts to find what the executing agent missed, and writes a sign-off doc in `lastreview/{N}-phaseM-gate-review.md`. Gate-review verdicts: ✅ GO advance / 🟡 GO with follow-up listed / ❌ NO-GO list blockers. No phase is considered closed until its gate-review is ✅ or 🟡 with documented follow-ups folded into the next phase's brief.

### Master tracking table

Task IDs are `P{phase}.{seq}`. Owner is the model class expected to execute the brief. Effort is in hours. Dependencies reference task IDs.

| ID | Title | Owner | Hrs | Acceptance summary | Test coverage required | Deps | Status |
|---|---|---|---|---|---|---|---|
| P0.1 | Create branch `feature/v0.10.0-unified-benchmarks` from `main` @ v0.9.0-beta | Sonnet | 0.25 | Branch exists, baseline `dotnet test` green | n/a (baseline) | – | ✅ |
| P0.2 | Open this playbook | Opus | 1 | Plan appended to last-review #10 | n/a | – | ✅ |
| P1.1 | Create `src/AgentEval.Evals.Compliance.Gdpr/` skeleton csproj | Sonnet | 0.5 | csproj builds empty; `EmbeddedResource` globs declared; `IsPackable=false` | csproj smoke build | P0.1 | ✅ `2fe2c92` |
| P1.2 | Move 51 GDPR .cs files + Articles/Yaml + Resources/Prompts + Reporting/Schema | Sonnet | 1.5 | Files moved with `git mv`; namespace top-line unchanged (still `AgentEval.GdprBenchmark`) at this step; build green | All existing GDPR tests pass unchanged | P1.1 | ✅ `2fe2c92` (76 files moved; 32 .cs + resources) |
| P1.3 | Lift `Program.cs` into new `samples/AgentEval.GdprBenchmark.Demo/` | Sonnet | 1 | Demo project compiles; refs `src/AgentEval.Evals.Compliance.Gdpr/` | Demo runs to completion against stub agent in `dotnet run` smoke test | P1.2 | ✅ `2fe2c92` |
| P1.4 | Wire new assembly into umbrella `AgentEval.csproj` (+ Cli) | Sonnet | 0.5 | Umbrella + CLI build green; samples csproj reference removed from CLI | Full suite still green | P1.2 | ✅ `2fe2c92` (also patched undocumented EuAiAct→Gdpr cross-ref) |
| P1.5 | Update CI workflow `gdpr-calibration.yml` path | Sonnet | 0.25 | Workflow YAML references new path; lints clean | Workflow dry-run via `act` if available, else manual review | P1.4 | ✅ `2fe2c92` (workflow uses CLI path; no change needed) |
| P1.6 | Phase-1 verification (Opus gate) | Opus | 1 | All gates met (see Phase 1 detail) | 3625+ baseline + GDPR additions all green | P1.5 | ✅ Sonnet self-verify; 3625 / 0 / 1 |
| P2.1 | Create `src/AgentEval.Evals.Compliance.EuAiAct/` skeleton csproj | Sonnet | 0.5 | csproj builds empty; refs `AgentEval.Evals.Compliance.Gdpr` (cross-reg linker) | csproj smoke build | P1.6 | ✅ `7a4ce8c` |
| P2.2 | Move 53 EuAiAct .cs files + Articles/Yaml + Resources/Prompts + Reporting/Schema | Sonnet | 1.5 | `git mv`; build green | All existing EuAiAct tests pass | P2.1 | ✅ `7a4ce8c` (53 tracked items moved) |
| P2.3 | Lift `Program.cs` into `samples/AgentEval.EuAiActBenchmark.Demo/` | Sonnet | 1 | Demo compiles + runs | Demo `dotnet run` smoke | P2.2 | ✅ `7a4ce8c` |
| P2.4 | Wire new assembly into umbrella + Cli; remove sample ProjectReferences from CLI | Sonnet | 0.5 | Umbrella + CLI build green | Full suite green | P2.2 | ✅ `7a4ce8c` |
| P2.5 | Update CI workflow `eu-ai-act-calibration.yml` path | Sonnet | 0.25 | Workflow YAML lints clean | n/a | P2.4 | ✅ `7a4ce8c` (workflow uses CLI; no change needed) |
| P2.6 | Phase-2 verification (Opus gate) | Opus | 0.25 | All gates met | Full suite green; cross-reg linker still functional | P2.5 | ✅ Sonnet self-verify; 3625 / 0 / 1; CrossRegulationLinker 5/5 |
| P3.1 | Create `src/AgentEval.Evals.Performance/` skeleton csproj | Sonnet | 0.5 | csproj builds empty | csproj smoke build | P2.6 | ✅ |
| P3.2 | Move `PerformanceBenchmark.cs` + 4 co-located result types | Sonnet | 1 | `git mv` from `Core/Benchmarks/`; namespace temporarily unchanged | `PerformanceBenchmarkTests` (if any) pass | P3.1 | ✅ (single file, all types co-located) |
| P3.3 | Delete now-empty `src/AgentEval.Core/Benchmarks/` folder | Sonnet | 0.1 | Folder gone; no orphan `.csproj` `<Compile>` entries | Build green | P3.2 | ✅ |
| P3.4 | Add `EvaluateAsync(EvalInput) → EvalResult` adapter on `PerformanceBenchmark` | Sonnet | 1 | Adapter synthesises a leaf `EvalResult` per metric (latency/throughput/cost); flows through standard output-store | New `PerformanceBenchmarkAdapterTests` covering round-trip serialization through `IRunOutputStore` | P3.3 | ✅ 3-leaf CompositeEval shape; inlined CapByWorst; 5 new tests |
| P3.5 | Phase-3 verification (Opus gate) | Opus | 0.25 | All gates met | New adapter test passes; full suite green | P3.4 | ✅ Sonnet self-verify; 3630 / 0 / 1 |
| P4.1 | Move `AgenticBenchmark` to namespace `AgentEval.Benchmarks` (file-level, partial class) | Sonnet | 0.5 | Class declared as `public static partial class` in `AgentEval.Benchmarks` | All Agentic-using tests recompile after `using` rename | P3.5 | ✅ `9f703a2` |
| P4.2 | Move `GdprBenchmark` (top-level static) to namespace `AgentEval.Benchmarks` | Sonnet | 0.5 | partial class; only top-level factory moves, internals stay in `AgentEval.GdprBenchmark.*` | All GDPR tests recompile | P4.1 | ✅ `9f703a2` (MultiJudgeOptions extracted to sibling file) |
| P4.3 | Move `EuAiActBenchmark` (top-level static) to namespace `AgentEval.Benchmarks` | Sonnet | 0.5 | partial class | All EuAiAct tests recompile | P4.2 | ✅ `9f703a2` (same MultiJudgeOptions extraction pattern) |
| P4.4 | Move `MemoryBenchmark` (config record only) to namespace `AgentEval.Benchmarks` | Sonnet | 0.5 | Type moved; runner stays in `AgentEval.Memory.Evaluators` | Memory tests recompile | P4.3 | ✅ `9f703a2` (Category + ScenarioType enums extracted) |
| P4.5 | Mass-rename `using` directives across tests + samples + CLI | Sonnet | 1 | `using AgentEval.Evals.Agentic;` → `using AgentEval.Benchmarks;` etc. for the four factories | Full suite green | P4.4 | ✅ `9f703a2` (+13 aliases for parent-namespace-vs-type-name CS0234 collisions) |
| P4.6 | Add `BenchmarkNamespaceContractTests` | Sonnet | 0.5 | Reflection test asserts all 4 factories live in `AgentEval.Benchmarks` namespace | New test class passes | P4.5 | ✅ `9f703a2` (12 in main + 3 in Memory.Tests) |
| P4.7 | Phase-4 verification (Opus gate) | Opus | 0.5 | All gates met | Contract test + full suite green | P4.6 | ✅ Opus #12 → `lastreview/11-phase4-gate-review.md` — 🟡 GO with two cosmetic follow-ups (alias comments + contract-test enumerator), folded into Phase 5 |
| P4b.1 | Rename directory + assembly `src/AgentEval.Evals.Compliance.Gdpr/` → `src/AgentEval.Compliance.Gdpr/`; update `<RootNamespace>AgentEval.Compliance.Gdpr</RootNamespace>` + `<AssemblyName>` in csproj | Sonnet | 0.5 | Directory + csproj + assembly name renamed; build clean | csproj smoke build | P4.7 | ✅ 7f0b862 |
| P4b.2 | Mass-rename internal namespaces `AgentEval.GdprBenchmark.*` → `AgentEval.Compliance.Gdpr.*` across all .cs files in the assembly | Sonnet | 1 | Every `namespace AgentEval.GdprBenchmark{...}` → `namespace AgentEval.Compliance.Gdpr{...}`; XML doc `<see cref>` updated | Build clean, 0 CS1574 warnings | P4b.1 | ✅ 7f0b862 — 32 Gdpr production .cs files; CalibrationDataset resource filter updated |
| P4b.3 | Same for EuAiAct: directory rename, csproj rename, namespace mass-rename `AgentEval.EuAiActBenchmark.*` → `AgentEval.Compliance.EuAiAct.*` | Sonnet | 1.5 | Directory + csproj + internal namespaces renamed | Build clean | P4b.2 | ✅ 7f0b862 — 34 EuAiAct production .cs files; CrossRegulationLinker FQ type ref updated |
| P4b.4 | Update consumers (tests + CLI + Mission Control + Samples) for both renames | Sonnet | 1 | All `using` directives updated; manifest-resource paths in tests that load `AgentEval.GdprBenchmark.Reporting.Schema.*` updated to `AgentEval.Compliance.Gdpr.Reporting.Schema.*` (or whatever the new RootNamespace dictates) | Full suite green | P4b.3 | ✅ 7f0b862 — 211 consumer using updates; test directories also renamed `GdprBenchmark/` → `Compliance/Gdpr/`, `EuAiActBenchmark/` → `Compliance/EuAiAct/` to break second-collision class (necessary corollary of alias removal); test csproj EmbeddedResource patterns updated |
| P4b.5 | Extend `BenchmarkNamespaceContractTests` to assert orphan-FQN absence via reflection enumerator (not hardcoded list) — addresses Concern B from Last Review #11 | Sonnet | 0.5 | Test enumerates `*Benchmark`-suffixed types in repo assemblies; asserts the factory ones live in `AgentEval.Benchmarks`, infrastructure in domain namespaces | Updated test passes | P4b.4 | ✅ — already implemented in Phase 5 (`All_BenchmarkSuffixedFactories_LiveIn_AgentEvalBenchmarks`); DomainTypeExceptions use short names so unaffected by namespace rename |
| P4b.6 | Document the 13 `using XxxBenchmarkFactory = AgentEval.Benchmarks.XxxBenchmark;` aliases with one-line root-cause comments — addresses Concern A from Last Review #11 | Sonnet | 0.5 | Each alias site gets a `// CS0234 disambiguation: AgentEval.{Gdpr,EuAiAct}Benchmark is a parent namespace AND the factory type name; alias resolves the resulting ambiguity in test files nested under those namespaces.` style comment | Comment present on each alias | P4b.5 | ✅ 7f0b862 — superseded: aliases REMOVED (not documented) because the production-prefix collision is gone after the rename, and the test-enclosing-namespace collision was eliminated by also renaming the test directories. Zero aliases survive. |
| P4b.7 | Phase-4b verification (Opus gate — high-risk: embedded-resource paths + reflection-based consumers) | Opus | 0.5 | All gates met; manifest-resource path test still works; `EvaluatorCardRegistry` reflection still resolves; Mission Control GraphQL untouched | Full suite green + Opus signs off in `lastreview/12-phase4b-gate-review.md` | P4b.6 | ✅ Opus self-gate → `12-phase4b-gate-review.md` — verdict GO. 3667/0/1 main + 452/0/0 Memory + all 22 canary tests green; 0 CS1574 with `-p:GenerateDocumentationFile=true` |
| P5.1 | Implement `OwaspBenchmark` static class in `src/AgentEval.RedTeam/RedTeam/Compliance/OwaspBenchmark.cs` | Sonnet/Opus | 1 | Presets `Top10`, `Smoke`, `AuditGrade`, `Top10ForRag` return `OwaspBenchmarkRun` | n/a (covered in P5.3) | P4b.7 | ✅ fd4cde8 — `public static partial class` in `AgentEval.Benchmarks`; 4 presets present (note: `Top10ForRag` structurally identical to `Top10` today, documented in doc-comment) |
| P5.2 | Implement `OwaspBenchmarkRun` with `ScanAsync`, `EvaluateAsync`, `GenerateReport` | Sonnet/Opus | 1 | `EvaluateAsync` produces `EvalResult` (score = passRate) with audit-chain leaves per category | – | P5.1 | ✅ fd4cde8 — 10-leaf composite (one per LLM01..LLM10) with MinAggregation; 4 not-covered categories produce honest `skipped` leaves |
| P5.3 | Add `OwaspBenchmarkTests` (preset shape, `EvaluateAsync` round-trip, report generation) | Sonnet/Opus | 1 | Test class covers: preset enumeration, `EvalResult.AverageScore` shape, `OWASPComplianceReport` JSON round-trip | New `OwaspBenchmarkTests` + `OwaspBenchmarkAdapterTests` | P5.2 | ✅ fd4cde8 — 10 tests in `tests/AgentEval.Tests/RedTeam/Reporting/Compliance/OwaspBenchmarkTests.cs` (single file, not split into Adapter; file path diverges from playbook — see review note) |
| P5.4 | Add `BenchOwaspCommand` (CLI) mirroring `BenchAgenticCommand` | Sonnet/Opus | 1 | Sub-commands wired; `--subject` required; output-store path identical to other bench commands | New `BenchOwaspCommandTests` for arg parsing + dry-run | P5.3 | ✅ fd4cde8 — 12 tests; output path `.agenteval/compliance/OWASP-LLM-Top10/<subject>/<ts>/` matches GDPR + EU AI Act pattern |
| P5.5 | Phase-5 verification (Opus gate) | Opus | 0.5 | Gates met | All new tests + full suite green; sign-off in `lastreview/13-phase5-gate-review.md` | P5.4 | ✅ Opus #13 → `13-phase5-gate-review.md` — 🟡 GO with follow-up (3 cosmetic items: contradictory CLI comment ✅ closed in Phase 6 commit `5bd3ed6`; test-file placement ✅ closed in Phase 6 commit `5bd3ed6`; `Top10ForRag` divergence ✅ closed in Phase-5b commit — all 3 yellow items now closed) |
| P5b.1 | Close `Top10ForRag` honesty gap — make the preset materially distinct from `Top10` (intensity / timeout / threat-model rationale), add divergence-pinning tests, update registry description + cost-tier | Opus | 0.5 | `Top10ForRag` uses `Intensity.Comprehensive` (was `Intensity.Quick`) + 20-min timeout (was 10-min `QuickTimeout`); 2 new tests in `OwaspBenchmarkTests` assert `probeCount(Top10ForRag) > probeCount(Top10)` and `probeCount(Top10ForRag) == probeCount(AuditGrade)`; registry description rewritten to reflect actual config; cost-tier Medium → High | New tests `Top10ForRag_IsMateriallyDistinctFromTop10_DeepProbeCoverage` + `Top10ForRag_ProbeDepth_MatchesAuditGrade_NotTop10` | P8.6 | ✅ Phase-5b commit — Opus self-gate → `17-phase5b-gate-review.md` — ✅ GO. 3716/0/1 main + 477/0/0 Memory (+2 main from new divergence-pinning tests). Build clean. Registry integrity confirmed. All 3 Phase-5 yellow items now closed across Phases 6/7/8/5b. |
| P6.1 | Implement `MitreBenchmark` static class | Opus | 0.5 | Presets `AtlasBaseline`, `AtlasSmoke`, `AtlasAuditGrade`; reuses internals from P5.2 | – | P5.5 | ✅ Phase-6 commit — `public static partial class` in `AgentEval.Benchmarks`; 3 presets (no Top10ForRag-style duplicate); per-attack ATLAS-ID projection in `AttackAtlasIds` |
| P6.2 | Implement `MitreBenchmarkRun` (mirrors `OwaspBenchmarkRun`) | Opus | 0.5 | `EvaluateAsync` returns `EvalResult` with ATLAS technique IDs as audit-chain tags | – | P6.1 | ✅ Phase-6 commit — 12-leaf composite (canonical reporter roster), every leaf's `Metric.Key` is `mitre.aml.t0xxx`; new `BuildEvalResult(RedTeamResult)` overload lets the CLI avoid double-scan. `OwaspBenchmarkRun` retrofitted with the same overload. |
| P6.3 | Add `MitreBenchmarkTests` | Opus | 0.5 | Shape tests + report round-trip | New `MitreBenchmarkTests` | P6.2 | ✅ Phase-6 commit — 10 tests at `tests/AgentEval.Tests/Benchmarks/MitreBenchmarkTests.cs` (playbook path, matches contract test location); ATLAS-ID round-trip + tag-preservation invariants asserted explicitly. OWASP test file relocated to the same directory (Phase-5 follow-up #3 closed). |
| P6.4 | Add `BenchMitreCommand` (CLI) | Opus | 1 | mirrors `BenchOwaspCommand` | New `BenchMitreCommandTests` | P6.3 | ✅ Phase-6 commit — 15 tests; output path `.agenteval/compliance/MITRE-ATLAS/<subject>/<ts>/` matches OWASP/GDPR/EU-AI-Act pattern. Single-scan pattern; `BenchOwaspCommand` retrofitted to match (Phase-5 follow-up #1 closed). |
| P6.5 | Phase-6 verification (Opus self-gate) | Opus | 0.5 | Gates met | All new + full suite green; sign-off in `lastreview/14-phase6-gate-review.md` | P6.4 | ✅ Opus self-gate → `14-phase6-gate-review.md` — GO. 3694/0/1 main + 452/0/0 Memory (+27 over Phase 5); 0 warnings with `GenerateDocumentationFile=true`; CLI E2E smoke passes; all 3 Phase-5 follow-ups closed in this commit. |
| P7.1 | Implement `LongMemEvalBenchmark` façade in `src/AgentEval.Memory/External/LongMemEval/` | Sonnet | 1 | Presets `Subset(judge)`, `Full(judge)` return pre-configured `LongMemEvalBenchmarkRunner` instances | New `LongMemEvalBenchmarkTests` | P6.5 | ✅ `2310e77` — `Subset(chatClient)` / `Full(chatClient)` + `SubsetOptions` / `FullOptions`; EvaluateAsync adapter deviation documented in XML doc (Phase 8 will wire via BenchmarkFamilyRegistry); 16 tests in `AgentEval.Memory.Tests`. |
| P7.2 | Place in `AgentEval.Benchmarks` namespace (partial class consistent with P4) | Sonnet | 0.25 | Namespace contract test (P4.6) updated to include `LongMemEvalBenchmark` | Updated contract test passes | P7.1 | ✅ `2310e77` — `MemoryBenchmarkNamespaceContractTest` extended with 3 LongMemEvalBenchmark assertions (namespace + public + static-class). Main `BenchmarkNamespaceContractTests` does not reference Memory types directly (PrivateAssets=all umbrella); Memory.Tests companion covers it per existing pattern. |
| P7.3 | Phase-7 verification (Opus gate) | Opus | 0.5 | Gates met | All new + full suite green; sign-off in `lastreview/15-phase7-gate-review.md` | P7.2 | ✅ Opus #14 → `15-phase7-gate-review.md` — 🟡 GO with follow-up. 3694/0/1 main + 468/0/0 Memory; 0 warnings with `GenerateDocumentationFile=true`. Three non-blocking concerns folded into Phase 8 brief: (1) `SubsetOptions.RandomSeed` is dead unless caller manually wires it, (2) `Full()` silently degrades to embedded subset when `LONGMEMEVAL_DATASET_PATH` is unset, (3) `SubsetOptions`/`FullOptions` static-property pattern is inconsistent with sibling factories. |
| P8.1 | **Design + implement `BenchmarkFamilyRegistry`** (canonical single-source-of-truth) in `src/AgentEval.Core/Benchmarks/BenchmarkFamilyRegistry.cs` — every benchmark family registers `(name, description, presets[], factory delegate, EvaluateAsync delegate)` here. CLI `bench --list`, per-family `--help`, and future Mission Control discovery all read from this. Future regulations (HIPAA / PCI-DSS / ISO 42001 / NIS2) register here too. | Opus | 1.5 | Registry class shipped; documented as the *canonical* registration mechanism; ADR-017-style note in the source XML doc explaining the "every benchmark plugs in here" convention | New `BenchmarkFamilyRegistryTests` covering: register / lookup by name / enumerate-all / unique-name invariant / preset-overlap detection / extensibility (an external assembly can register a new family) | P7.3 | ✅ Phase 8 — `BenchmarkFamily` + `BenchmarkFamilyRegistry` shipped at `src/AgentEval.Core/Benchmarks/BenchmarkFamilyRegistry.cs` (Shapes A + B; thread-safe; idempotent-same-content; throws on name+content conflict; XML doc declares canonical status with ADR-017 cross-ref). 12 unit tests in `BenchmarkFamilyRegistryTests`. |
| P8.2 | Wire all 7 existing benchmark families (Agentic, GDPR, EU AI Act, OWASP, MITRE, LongMemEval, Performance) + Memory into the registry | Sonnet | 1 | All 8 families registered at startup; CLI enumeration covers them | Integration test that constructs the registry and asserts all 8 families present | P8.1 | ✅ Phase 8 — 8 `[ModuleInitializer]` registration files added: `AgenticBenchmarkRegistration.cs`, `GdprBenchmarkRegistration.cs`, `EuAiActBenchmarkRegistration.cs`, `OwaspBenchmarkRegistration.cs`, `MitreBenchmarkRegistration.cs`, `PerformanceBenchmarkRegistration.cs`, `LongMemEvalBenchmarkRegistration.cs`, `MemoryBenchmarkRegistration.cs`. `BenchmarkFamilyRegistryIntegrationTests` + `BenchmarkFamilyRegistryTests.AllEightDefaultFamilies_AppearInRegistry` confirm. |
| P8.3 | Implement `BenchPerfCommand` (CLI) with `latency`/`throughput`/`cost` subcommands; reads from registry | Sonnet | 1 | mirrors `BenchAgenticCommand`; consumes `PerformanceBenchmark.EvaluateAsync` adapter from P3.4; resolves family + presets via registry | New `BenchPerfCommandTests` | P8.2 | ✅ Phase 8 — `BenchPerfCommand.cs` shipped; `bench perf {latency,throughput,cost}` sub-command tree wired in `Program.cs`. End-to-end run produces `EvalResult` JSON under `.agenteval/subjects/{subject}/runs/{ts}/scenarios/perf-{preset}.json`. 3 tests in `BenchPerfCommandTests`. |
| P8.4 | Implement `bench --list` reading from `BenchmarkFamilyRegistry` (NOT a hardcoded list) | Sonnet | 0.5 | Outputs canonical `(family, presets, cost-tier, description)` table from the registry; future families auto-appear | New `BenchListCommandTests`; explicitly asserts the listing comes from the registry, not a constant | P8.3 | ✅ Phase 8 — `BenchListCommand.cs` shipped. `BenchListCommandTests.OutputComesFromRegistry` registers a synthetic family with UUID name at runtime and asserts it appears in output (proves listing is registry-sourced, not hardcoded). 5 tests total. |
| P8.5 | Implement `bench {family} --help` enumeration of presets, reading from `BenchmarkFamilyRegistry` | Sonnet | 0.75 | Each family help prints its preset names + one-line descriptions, sourced from registry metadata | Per-family help integration tests | P8.4 | ✅ Phase 8 — `Program.cs` `PresetsHelpFromRegistry(string)` helper composes each family's `--preset` description from `BenchmarkFamilyRegistry.TryGet(family).Presets`. Applied to GDPR, EU AI Act, Agentic, OWASP, MITRE preset options. |
| P8.6 | Phase-8 verification (Opus gate) | Opus | 0.5 | Gates met (incl. `BenchmarkFamilyRegistry` extensibility test) | All new + full suite green; sign-off in `lastreview/16-phase8-gate-review.md` | P8.5 | ✅ Opus self-gate → `16-phase8-gate-review.md` — ✅ GO. 3714 main + 477 Memory tests pass (delta +20 main, +9 Memory from Phase 7 baseline). 0 warnings with `GenerateDocumentationFile=true`. Phase-7 follow-ups (4 of 4) closed inline. |
| P9.1 | Update `CHANGELOG.md` with v0.10.0-beta entry | Sonnet | 0.5 | Entry follows house style (Breaking + New features) | n/a (review) | P8.6 | ⬜ |
| P9.2 | Update ADR-017 `Status` block to `Implemented` + add commit hashes | Sonnet | 0.25 | Status updated | n/a | P9.1 | ⬜ |
| P9.3 | Update `README.md` benchmark table + `docs/benchmarks.md` | Sonnet | 1 | Tables reflect 8 benchmark families (Agentic, GDPR, EU AI Act, OWASP, MITRE, LongMemEval, Performance, Memory) + `using AgentEval.Benchmarks;` pattern | docs build green (DocFX) | P9.2 | ⬜ |
| P9.4 | **Document the `EvaluateAsync(EvalInput) → EvalResult` adapter pattern as AgentEval's canonical homogenisation primitive in `docs/architecture.md`** — every benchmark family that ships a non-`CompositeEval`-native result type provides this adapter so its results flow through the same `IRunOutputStore` / audit-chain / Mission Control rendering. PerformanceBenchmark (Phase 3) and OwaspBenchmark / MitreBenchmark (Phases 5-6) demonstrate the pattern. Document the contract: `EvaluateAsync` returns an `EvalResult` whose `SubResults` enumerate per-leaf metrics, with the natural result type (`LatencyBenchmarkResult`, `OWASPComplianceReport`, etc.) preserved in `Provenance` for downstream consumers. | Opus | 1 | New section in `docs/architecture.md` titled "Benchmark result-type homogenisation via `EvaluateAsync`"; cross-references ADR-017 | Doc renders cleanly; cross-refs resolve | P9.3 | ⬜ |
| P9.5 | Update `samples/AgentEval.Samples/DataAndInfrastructure/04_BenchmarkSystem.cs` to use new namespace | Sonnet | 0.25 | Sample compiles | Samples build green | P9.4 | ⬜ |
| P9.6 | Bump `Version` in `src/AgentEval/AgentEval.csproj` to `0.10.0-beta` | Sonnet | 0.1 | Version bump; package release notes updated | `dotnet pack` smoke | P9.5 | ⬜ |
| P9.7 | Phase-9 verification (Opus gate) | Opus | 0.5 | Gates met; architecture-doc convention reviewed | Docs + suite green; sign-off in `lastreview/17-phase9-gate-review.md` | P9.6 | ⬜ |
| P10.1 | Run `dotnet pack` and inspect `.nupkg` size; flag if > 10 MB | Sonnet | 0.5 | Package size measured + recorded | n/a | P9.7 | ⬜ |
| P10.2 | Final Opus review pass (Last Review #12) | Opus | 2 | Sign-off doc in `lastreview/12-v0.10.0-final.md` | n/a (review) | P10.1 | ⬜ |
| P10.3 | Merge to `main`, tag `v0.10.0-beta`, push | User-authorised | 0.5 | Tag pushed; release notes published | n/a | P10.2 | ⬜ |

**Status legend**: ⬜ todo • 🟦 in-progress • ✅ done • ❌ blocked

---

### Phase 0 — Branch + scaffolding (done)

**Goal**: Ground the work on a clean branch from v0.9.0-beta with a green baseline.

**Outcome already in place**:
- Branch `feature/v0.10.0-unified-benchmarks` created from `main` @ `e9d8b8c`.
- 3625 tests pass on net10.0.
- Last Review #10 + ADR-017 accepted.
- This execution playbook authored.

**Verification gate**: ✅ Met.

---

### Phase 1 — Promote GDPR `samples/` → `src/`

**Phase goal**: Move 51 GDPR source files + embedded resources from `samples/AgentEval.GdprBenchmark/` to a first-class product assembly `src/AgentEval.Evals.Compliance.Gdpr/`, lifting the `Program.cs` into a new thin demo project. **Namespace stays `AgentEval.GdprBenchmark` at this phase** — namespace consolidation is Phase 4.

#### Task P1.1 — Create skeleton csproj

- **Inputs**: existing `samples/AgentEval.GdprBenchmark/AgentEval.GdprBenchmark.csproj`.
- **Outputs**: new `src/AgentEval.Evals.Compliance.Gdpr/AgentEval.Evals.Compliance.Gdpr.csproj`.
- **Step-by-step**:
  1. Copy structure from existing csproj; change `<OutputType>Exe</OutputType>` to library (remove the line).
  2. Set `<RootNamespace>AgentEval.GdprBenchmark</RootNamespace>` (preserves namespace for now; Phase 4 renames the top-level factory only).
  3. Keep `<IsPackable>false</IsPackable>`.
  4. Preserve all three `<EmbeddedResource>` globs: `Resources\Prompts\*.md`, `Reporting\Schema\*.json`, `Articles/Yaml/*.yaml`, `DomainPacks\**\*.yaml`.
  5. Preserve all three `<ProjectReference>`s: `AgentEval.Abstractions`, `AgentEval.Core`, `AgentEval.DataLoaders`. Note paths shift from `..\..\src\…` to `..\…`.
  6. Preserve the YAML + JsonSchema + QuestPDF `<PackageReference>`s.
  7. Add to `AgentEval.sln` via `dotnet sln add` (matching SLN group of other src projects, not samples).
- **Acceptance**: `dotnet build src/AgentEval.Evals.Compliance.Gdpr/AgentEval.Evals.Compliance.Gdpr.csproj` is green (empty project compiles).
- **Test coverage**: csproj smoke build only.
- **Pitfalls**: Don't forget to add a `<None Update="..." CopyToOutputDirectory="..."/>` if any file was set that way in the old csproj (verify by diff). The current csproj has none, so this is a no-op — verify still.
- **Effort**: 0.5h.

#### Task P1.2 — Move 51 .cs files + embedded resources

- **Inputs**: all 51 `.cs` files in `samples/AgentEval.GdprBenchmark/` *except* `Program.cs`; all `Articles/Yaml/*.yaml` (22 files); `Resources/Prompts/gdpr-judge-system.v1.md` + `per-criterion.v1.md`; `Reporting/Schema/gdpr-evidence.schema.json`; `DomainPacks/**/*.yaml`.
- **Outputs**: same files under `src/AgentEval.Evals.Compliance.Gdpr/`.
- **Step-by-step**:
  1. `git mv samples/AgentEval.GdprBenchmark/Articles src/AgentEval.Evals.Compliance.Gdpr/Articles` (and repeat for Calibration, Composition, DomainPacks, Pillars, Reporting, Resources, plus the top-level `GdprBenchmark.cs`).
  2. Leave `Program.cs` and `README.md` behind for P1.3 (or move into the demo project there).
  3. Do **not** change any `namespace AgentEval.GdprBenchmark;` declarations at this step — preserve naming so existing tests keep compiling.
  4. Delete `samples/AgentEval.GdprBenchmark/AgentEval.GdprBenchmark.csproj` only after P1.3 lifts the Program. (Keep it temporarily so the build still works while in-flight.)
- **Acceptance**: All 51 .cs files appear in their new location with identical `namespace …;` declarations; `dotnet build` of `src/AgentEval.Evals.Compliance.Gdpr.csproj` is green (it now has files).
- **Test coverage**: existing GDPR tests in `tests/AgentEval.Tests/GdprBenchmark/*` should still pass with one substitution: the test project's `<ProjectReference>` must be updated from `samples/AgentEval.GdprBenchmark` to `src/AgentEval.Evals.Compliance.Gdpr` *before* running the suite. This is a single edit to `tests/AgentEval.Tests/AgentEval.Tests.csproj`.
- **Pitfalls**:
  - Embedded resource glob paths in the new csproj use forward slashes `Articles/Yaml/*.yaml` — verify case sensitivity matches the existing project.
  - The golden datasets at `tests/AgentEval.Tests/GdprBenchmark/Calibration/Golden/*.jsonl` **stay in the test project** — they're loaded via `[EmbeddedResource]` on the test assembly, not the production one. Verify by Grep'ing `golden-pillar` and confirming they're referenced through test-assembly resources.
  - Use `git mv` (not `mv` + `git add`) so file history is preserved — important for `git blame` continuity on Pillar files.
- **Effort**: 1.5h.

#### Task P1.3 — Lift `Program.cs` into `samples/AgentEval.GdprBenchmark.Demo/`

- **Inputs**: `samples/AgentEval.GdprBenchmark/Program.cs`, `samples/AgentEval.GdprBenchmark/README.md`.
- **Outputs**: new `samples/AgentEval.GdprBenchmark.Demo/` project with Program.cs + README.md.
- **Step-by-step**:
  1. Create `samples/AgentEval.GdprBenchmark.Demo/AgentEval.GdprBenchmark.Demo.csproj` as an `Exe` library with `<IsPackable>false</IsPackable>` + ProjectReference to `src/AgentEval.Evals.Compliance.Gdpr/` only.
  2. `git mv samples/AgentEval.GdprBenchmark/Program.cs samples/AgentEval.GdprBenchmark.Demo/Program.cs`.
  3. `git mv samples/AgentEval.GdprBenchmark/README.md samples/AgentEval.GdprBenchmark.Demo/README.md`.
  4. Delete `samples/AgentEval.GdprBenchmark/AgentEval.GdprBenchmark.csproj` and the now-empty directory.
  5. Add demo to `AgentEval.sln` (samples group).
- **Acceptance**: `dotnet run --project samples/AgentEval.GdprBenchmark.Demo` runs to completion (or fails for the same reason it failed before — the runtime semantics are unchanged; only the project boundary moved).
- **Test coverage**: At least one demo-build smoke test (already covered by `dotnet build` over the sln).
- **Pitfalls**: Program.cs uses `using AgentEval.GdprBenchmark;` — that namespace still exists (no rename in this phase) so no edit needed inside the file.
- **Effort**: 1h.

#### Task P1.4 — Wire new assembly into umbrella + CLI

- **Inputs**: `src/AgentEval/AgentEval.csproj`; `src/AgentEval.Cli/AgentEval.Cli.csproj`.
- **Outputs**: both csprojs updated.
- **Step-by-step**:
  1. In `src/AgentEval/AgentEval.csproj`, add `<ProjectReference Include="../AgentEval.Evals.Compliance.Gdpr/AgentEval.Evals.Compliance.Gdpr.csproj" PrivateAssets="all" />` after the `AgentEval.RedTeam` line (alphabetical-ish ordering).
  2. In `src/AgentEval.Cli/AgentEval.Cli.csproj`, replace `<ProjectReference Include="..\..\samples\AgentEval.GdprBenchmark\AgentEval.GdprBenchmark.csproj" />` with `<ProjectReference Include="..\AgentEval.Evals.Compliance.Gdpr\AgentEval.Evals.Compliance.Gdpr.csproj" />`.
  3. Run `dotnet build` from solution root.
- **Acceptance**: Umbrella + CLI build green.
- **Test coverage**: full suite must still pass (run `dotnet test`).
- **Pitfalls**: ProjectReference path slashes — the CLI csproj already uses Windows-style `\`; preserve that convention for the line you add. The umbrella csproj uses POSIX `/`. Don't mix.
- **Effort**: 0.5h.

#### Task P1.5 — Update CI workflow `gdpr-calibration.yml`

- **Inputs**: `.github/workflows/gdpr-calibration.yml`.
- **Outputs**: same file with updated paths.
- **Step-by-step**:
  1. The current workflow only invokes `agenteval bench gdpr calibrate` and doesn't reference the project path directly. Verify with Grep, but the file likely needs no path change. If it has any `samples/AgentEval.GdprBenchmark` hardcoded paths, update them.
  2. Add a comment: `# Phase 1 v0.10.0: GDPR benchmark promoted to src/AgentEval.Evals.Compliance.Gdpr`.
- **Acceptance**: YAML lints (use `yamllint` or VS Code).
- **Test coverage**: n/a (CI runs on PR; we'll verify on the PR).
- **Effort**: 0.25h.

#### Task P1.6 — Phase-1 verification gate

- **Gate criteria** (all must be true):
  - [ ] `dotnet build` green at solution level on net10.0.
  - [ ] `dotnet test tests/AgentEval.Tests` green (all GDPR tests, all other tests, no skips beyond pre-existing).
  - [ ] `dotnet run --project samples/AgentEval.GdprBenchmark.Demo` runs without exception (smoke).
  - [ ] **Meaningful test**: `GdprEvidenceSchemaAndFileSystemTest` passes — this exercises the embedded `gdpr-evidence.schema.json` round-trip through the new assembly's embedded resources. If this fails, the EmbeddedResource glob was lost in translation.
  - [ ] **Meaningful test**: `E2E_StandardTest` passes — full preset evaluation against stub agent, end-to-end.
  - [ ] **Meaningful test**: `AllArticleYamlsValidateTest` passes — confirms all 22 YAML files are still discoverable as embedded resources.
  - [ ] `samples/AgentEval.GdprBenchmark/` directory deleted (no orphan files).
  - [ ] `git log --follow` on `Pillar1Foundations.cs` shows pre-move history (verifies `git mv` was used).

If any gate fails, **stop**. Diagnose root cause. Do not advance to Phase 2.

**Phase 1 estimated effort**: 5h.

---

### Phase 2 — Promote EU AI Act `samples/` → `src/`

**Phase goal**: Identical structural move as Phase 1, applied to EU AI Act. Preserve cross-reg-linker dependency from EuAiAct → Gdpr.

#### Task P2.1 — Create skeleton csproj

- Same as P1.1 but for EuAiAct. Add `<ProjectReference Include="..\AgentEval.Evals.Compliance.Gdpr\AgentEval.Evals.Compliance.Gdpr.csproj" />` because `CrossRegulationLinker.cs` cross-references the GDPR articles registry.
- **Effort**: 0.5h.

#### Task P2.2 — Move 53 .cs files + embedded resources

- Mirror P1.2 for EuAiAct's files. Embedded resources: `Resources\Prompts\*.md`, `Reporting\Schema\eu-ai-act-evidence.schema.json`, `Articles/Yaml/*.yaml`, `DomainPacks\**\*.yaml`.
- **Pitfalls**: EuAiAct has `CrossRegulationLinker.cs` in `Reporting/`. Verify its `using AgentEval.GdprBenchmark…` directives still resolve after the move (Phase 1 preserved that namespace).
- **Effort**: 1.5h.

#### Task P2.3 — Lift `Program.cs` into `samples/AgentEval.EuAiActBenchmark.Demo/`

- Mirror P1.3. **Effort**: 1h.

#### Task P2.4 — Wire umbrella + CLI

- Add ProjectReference to `AgentEval.csproj` and replace in `AgentEval.Cli.csproj`. **Effort**: 0.5h.

#### Task P2.5 — Update CI workflow `eu-ai-act-calibration.yml`

- Mirror P1.5. **Effort**: 0.25h.

#### Task P2.6 — Phase-2 verification gate

- [ ] `dotnet build` + `dotnet test` green.
- [ ] **Meaningful test**: `CrossRegulationLinkerTests` passes (proves the EuAiAct → Gdpr dependency survived the move).
- [ ] **Meaningful test**: `EuAiActEvidenceSchemaTests` passes (schema embedded resource round-trip).
- [ ] **Meaningful test**: `EuAiActStandardE2ETest` + `EuAiActSmokeE2ETest` both pass.
- [ ] `samples/AgentEval.EuAiActBenchmark/` deleted.
- [ ] Demo project runs.

**Phase 2 estimated effort**: 4h.

---

### Phase 3 — Relocate Performance + add `EvaluateAsync` adapter

**Phase goal**: Move `PerformanceBenchmark` out of `Core/Benchmarks/` into a dedicated assembly. Add the `EvaluateAsync(EvalInput) → EvalResult` adapter that allows perf results to flow through the unified output-store pipeline.

#### Task P3.1 — Create skeleton `src/AgentEval.Evals.Performance/AgentEval.Evals.Performance.csproj`

- **Inputs**: existing patterns from `AgentEval.Evals.Agentic.csproj`.
- **Step-by-step**:
  1. Mirror `Evals.Agentic` csproj — net8.0/net9.0/net10.0, `IsPackable=false`, `RootNamespace=AgentEval.Evals.Performance`.
  2. ProjectReferences: `AgentEval.Abstractions`, `AgentEval.Core`.
  3. Add to sln.
- **Acceptance**: csproj smoke build green.
- **Effort**: 0.5h.

#### Task P3.2 — Move `PerformanceBenchmark.cs` + 4 co-located result types

- **Inputs**: `src/AgentEval.Core/Benchmarks/PerformanceBenchmark.cs` (contains the class + `PerformanceBenchmarkOptions` + `LatencyBenchmarkResult` + `ThroughputBenchmarkResult` + `CostBenchmarkResult` — all in one file).
- **Outputs**: `src/AgentEval.Evals.Performance/PerformanceBenchmark.cs` with namespace `AgentEval.Benchmarks` (already correct — this file is unique in Core for already having the unified namespace).
- **Step-by-step**:
  1. `git mv src/AgentEval.Core/Benchmarks/PerformanceBenchmark.cs src/AgentEval.Evals.Performance/PerformanceBenchmark.cs`.
  2. Update the `using` directives — currently uses `using AgentEval.Core; using AgentEval.Models;` — both should still resolve.
  3. Namespace `AgentEval.Benchmarks` already in place; no change.
- **Acceptance**: Build green; the namespace `AgentEval.Benchmarks` only lives in this one file at this point (Phase 4 will join it).
- **Test coverage**: existing perf tests pass.
- **Effort**: 1h.

#### Task P3.3 — Delete now-empty `Core/Benchmarks/` folder

- Remove the folder; verify no Core csproj `<Compile>` glob still references it. **Effort**: 0.1h.

#### Task P3.4 — Add `EvaluateAsync(EvalInput) → EvalResult` adapter

- **Inputs**: `LatencyBenchmarkResult`, `ThroughputBenchmarkResult`, `CostBenchmarkResult` from P3.2.
- **Outputs**: new `PerformanceBenchmark` extension methods (or instance methods, depending on shape) that synthesise an `EvalResult` per metric.
- **Step-by-step**:
  1. Add static factory `PerformanceBenchmark.LatencyOf(IEvaluableAgent agent)` that returns a thin wrapper exposing `Task<EvalResult> MeasureAsync(string prompt, …)`.
  2. The `EvalResult` should have one leaf per measurement (mean latency, p95 latency, first-token latency) with audit-chain evidence carrying the raw timings.
  3. Mirror for throughput + cost: `PerformanceBenchmark.ThroughputOf(agent)` and `PerformanceBenchmark.CostOf(agent)`.
- **Acceptance**: New static factory methods compile and return `EvalResult` correctly.
- **Test coverage**: New `tests/AgentEval.Tests/Benchmarks/PerformanceBenchmarkAdapterTests`:
  - Test `LatencyOf(stub).MeasureAsync("prompt")` returns `EvalResult` with non-null leaves.
  - Test the result round-trips through `IRunOutputStore.SaveAsync(...)` → `LoadAsync(...)` with byte-identical reconstruction.
  - Test JSON serialization preserves the audit-chain entries.
- **Pitfalls**: Don't break the existing imperative API (`RunLatencyBenchmarkAsync(...)` returning `LatencyBenchmarkResult`). Keep it as an alternate entry point for users who want the raw timings struct. The adapter is *additive*.
- **Effort**: 1h.

#### Task P3.5 — Phase-3 verification gate

- [ ] Build + suite green.
- [ ] `Core/Benchmarks/` folder gone.
- [ ] **Meaningful test**: `PerformanceBenchmarkAdapterTests.AdapterRoundTripsThroughOutputStore` passes.
- [ ] **Meaningful test**: at least one existing perf-using test (search `RunLatencyBenchmarkAsync` to find it; e.g., `BenchmarkSystem` sample) compiles and runs.

**Phase 3 estimated effort**: 3h.

---

### Phase 4 — Namespace consolidation under `AgentEval.Benchmarks`

**Phase goal**: Hoist the four existing top-level preset-factory classes (`AgenticBenchmark`, `GdprBenchmark`, `EuAiActBenchmark`, `MemoryBenchmark` config record) into a single discovery namespace `AgentEval.Benchmarks`. Use `partial class` to keep the *implementation* file in its domain assembly while the *namespace* is unified.

#### Task P4.1 — Move `AgenticBenchmark` to `AgentEval.Benchmarks`

- **Inputs**: `src/AgentEval.Evals.Agentic/AgenticBenchmark.cs` (currently `namespace AgentEval.Evals.Agentic;`).
- **Outputs**: same file with `namespace AgentEval.Benchmarks;`, class declaration `public static partial class AgenticBenchmark`.
- **Step-by-step**:
  1. Edit line 25 in `AgenticBenchmark.cs` from `namespace AgentEval.Evals.Agentic;` to `namespace AgentEval.Benchmarks;`.
  2. Add `partial` to the class declaration (`public static partial class AgenticBenchmark`).
  3. The `using` directives inside the file already include `using AgentEval.Evals.Agentic.*`; preserve them since the class body references domain types.
- **Acceptance**: Class compiles in new namespace.
- **Test coverage**: All `AgenticBenchmark`-using tests recompile with updated `using` (P4.5 handles the mass rename).
- **Pitfalls**: `<see cref="AgenticBenchmark"/>` doc comments elsewhere in the codebase need namespace re-resolution by the compiler — usually transparent, but check the doc-build output for broken `cref`s in `docs/benchmarks/agentic/`.
- **Effort**: 0.5h.

#### Task P4.2 — Move `GdprBenchmark` to `AgentEval.Benchmarks`

- Mirror P4.1 on `src/AgentEval.Evals.Compliance.Gdpr/GdprBenchmark.cs` (after Phase 1's move). Line 15 of the file is `namespace AgentEval.GdprBenchmark;` — change to `namespace AgentEval.Benchmarks;`. Add `partial` to the class.
- **Pitfalls**: `GdprBenchmark.cs` also contains `MultiJudgeOptions` record. That **should stay in `AgentEval.GdprBenchmark`** (it's a domain type, not a factory). Move only the `public static class GdprBenchmark { … }` declaration into the unified namespace. The cleanest approach: split the file into `GdprBenchmark.cs` (factory, new namespace) + `MultiJudgeOptions.cs` (domain type, old namespace). Use two `git mv` operations or one `Edit` + `Write` pair.
- **Effort**: 0.5h.

#### Task P4.3 — Move `EuAiActBenchmark` to `AgentEval.Benchmarks`

- Mirror P4.2 on the EuAiAct factory. Same pattern — split out any domain types into separate files retaining their old namespace.
- **Effort**: 0.5h.

#### Task P4.4 — Move `MemoryBenchmark` (config record) to `AgentEval.Benchmarks`

- **Inputs**: `src/AgentEval.Memory/Models/MemoryBenchmark.cs`.
- **Outputs**: same file with namespace `AgentEval.Benchmarks`; type declared `partial` if needed.
- **Pitfalls**: The runner (`MemoryBenchmarkRunner`) stays in `AgentEval.Memory.Evaluators` — confirm it correctly imports the new namespace for the config record after the rename.
- **Effort**: 0.5h.

#### Task P4.5 — Mass-rename `using` directives

- **Step-by-step**:
  1. For each file matching `using AgentEval.Evals.Agentic;` that uses `AgenticBenchmark`, replace with `using AgentEval.Benchmarks;` (or add it alongside, since the original `using` is still valid for internal types).
  2. Same for `using AgentEval.GdprBenchmark;` → `using AgentEval.Benchmarks;` where only the `GdprBenchmark` static class is needed.
  3. Run `Grep` once for each of the 4 patterns to enumerate the file list; then `Edit` each. There are ~141 files using these namespaces; most will only need the addition of `using AgentEval.Benchmarks;` and can keep the domain `using`.
  4. Best practice: don't *remove* the existing `using`s wholesale, since many files use sub-namespace types like `AgentEval.GdprBenchmark.Articles`. Only add `using AgentEval.Benchmarks;` where the top-level factory class is referenced.
- **Acceptance**: Full suite recompiles.
- **Test coverage**: Build green + full test suite green.
- **Pitfalls**: 
  - Some test files use `[Theory]` data sources that reference factory presets by string — those don't need any change.
  - Sample projects (`samples/AgentEval.GdprBenchmark.Demo`, `samples/AgentEval.Samples/...`) need the same treatment.
- **Effort**: 1h.

#### Task P4.6 — Add `BenchmarkNamespaceContractTests`

- **Inputs**: nothing.
- **Outputs**: new `tests/AgentEval.Tests/Benchmarks/BenchmarkNamespaceContractTests.cs`.
- **Step-by-step**:
  1. Reflection-based test that enumerates types named `*Benchmark` across `AgentEval.dll` and asserts each is in `AgentEval.Benchmarks` namespace. Exception list: domain types like `MemoryBenchmarkRunner` (allowed in domain ns).
  2. Specifically assert: `AgenticBenchmark`, `GdprBenchmark`, `EuAiActBenchmark`, `MemoryBenchmark`, `PerformanceBenchmark` all live in `AgentEval.Benchmarks`.
- **Acceptance**: Test passes.
- **Pitfalls**: Use `Assembly.GetTypes()` defensively (`try { … } catch (ReflectionTypeLoadException) { … }`). Verify the test runs on each TFM.
- **Effort**: 0.5h.

#### Task P4.7 — Phase-4 verification gate

- [ ] **Meaningful test**: `BenchmarkNamespaceContractTests` passes — proves the namespace contract is enforceable, not aspirational.
- [ ] **Meaningful test**: Existing `tests/AgentEval.Tests/Agentic/EndToEnd/*` tests (~10 files) all pass — proves the mass `using` rename didn't break invocation paths.
- [ ] **Meaningful test**: At least one preset evaluation runs end-to-end through `using AgentEval.Benchmarks;` only (no domain `using`s on the factory line). Suggest writing `OneUsingDirectiveContractTest` that constructs all 4 factories from a file with a single `using AgentEval.Benchmarks;`.
- [ ] No `using` directives in the umbrella's public surface reference both the old AND new namespace (cleanup, otherwise the API surface is contradictory in docs).

**Phase 4 estimated effort**: 4h.

---

### Phase 4b — Compliance internal namespace + assembly rename

**Phase goal**: Phase 4 lifted only the top-level factory class `GdprBenchmark` / `EuAiActBenchmark` to `AgentEval.Benchmarks`. Their internal types — articles, pillars, registries, reporters, calibration datasets — still live in `AgentEval.GdprBenchmark.*` and `AgentEval.EuAiActBenchmark.*`. Those legacy parent-namespaces also collide with the same-named factory types and forced **13 `using XxxBenchmarkFactory = AgentEval.Benchmarks.XxxBenchmark;` aliases** across tests + CLI to resolve CS0234 ambiguities. This phase resolves the smell at the root by renaming both the internal namespaces and the assemblies/directories themselves:

```
src/AgentEval.Evals.Compliance.Gdpr/     → src/AgentEval.Compliance.Gdpr/
src/AgentEval.Evals.Compliance.EuAiAct/  → src/AgentEval.Compliance.EuAiAct/

namespace AgentEval.GdprBenchmark.Articles      → AgentEval.Compliance.Gdpr.Articles
namespace AgentEval.GdprBenchmark.Pillars       → AgentEval.Compliance.Gdpr.Pillars
namespace AgentEval.GdprBenchmark.Reporting     → AgentEval.Compliance.Gdpr.Reporting
... (and same for EuAiActBenchmark.*)
```

The top-level factory classes stay at `AgentEval.Benchmarks` (Phase 4 outcome preserved).

**Why drop the `Evals.` prefix**: the `Evals.*` namespace tree is the convention for evaluator collections (`AgentEval.Evals.Agentic`, `AgentEval.Evals.Performance`). Compliance benchmarks are a different category — they are *regulatory packages* that compose evaluator primitives into domain-specific scenarios. They deserve their own top-level namespace `AgentEval.Compliance.*` rather than the `Evals.*` umbrella. This also gives future regulations (HIPAA, PCI-DSS, ISO 42001, NIS2) a coherent home: `AgentEval.Compliance.Hipaa`, `AgentEval.Compliance.PciDss`, etc.

**Why now**: deferring this rename means the 13 aliases (and the CS0234 collisions that birthed them) live forever as a confusing artefact. v0.10.0-beta is the natural cut point because Phase 4 already established the breaking namespace-change precedent in this release. Adding to the same release costs one extra migration table row in CHANGELOG; deferring costs a separate v0.11.0-beta breaking release that no one wants.

#### Task P4b.1 — Rename Gdpr directory + assembly + RootNamespace

Steps:
1. `git mv src/AgentEval.Evals.Compliance.Gdpr src/AgentEval.Compliance.Gdpr`
2. `git mv src/AgentEval.Compliance.Gdpr/AgentEval.Evals.Compliance.Gdpr.csproj src/AgentEval.Compliance.Gdpr/AgentEval.Compliance.Gdpr.csproj`
3. Edit the csproj: change `<RootNamespace>AgentEval.GdprBenchmark</RootNamespace>` → `<RootNamespace>AgentEval.Compliance.Gdpr</RootNamespace>`. If `<AssemblyName>` is explicitly set, change it too.
4. Update consumers' `<ProjectReference>` paths:
   - `src/AgentEval/AgentEval.csproj` (umbrella)
   - `src/AgentEval.Cli/AgentEval.Cli.csproj`
   - `tests/AgentEval.Tests/AgentEval.Tests.csproj`
   - `src/AgentEval.Evals.Compliance.EuAiAct/AgentEval.Evals.Compliance.EuAiAct.csproj` (cross-regulation linker)
   - `samples/AgentEval.GdprBenchmark.Demo/AgentEval.GdprBenchmark.Demo.csproj`
5. Update `AgentEval.sln`: `dotnet sln remove` the old path, `dotnet sln add` the new path.

Verify: `dotnet build src/AgentEval.Compliance.Gdpr/AgentEval.Compliance.Gdpr.csproj -f net10.0` builds clean (build will still fail on namespace mismatches inside the .cs files — those fix in P4b.2).

#### Task P4b.2 — Mass-rename Gdpr internal namespaces

Across every `.cs` file in `src/AgentEval.Compliance.Gdpr/`:
- `namespace AgentEval.GdprBenchmark` → `namespace AgentEval.Compliance.Gdpr`
- `namespace AgentEval.GdprBenchmark.X` → `namespace AgentEval.Compliance.Gdpr.X` (for every sub-namespace: Articles, Articles.Building, Articles.Loading, Articles.Models, Pillars, Reporting, Reporting.Pdf, Reporting.Schema, Resources, Calibration, Composition, DomainPacks, …)

Use `sed -i 's/namespace AgentEval\.GdprBenchmark/namespace AgentEval.Compliance.Gdpr/g'` over `src/AgentEval.Compliance.Gdpr/**/*.cs`, then verify with grep that no `namespace AgentEval.GdprBenchmark` survives in `src/`.

**Update XML doc cref references** in the same files: `<see cref="AgentEval.GdprBenchmark.X.Y"/>` → `<see cref="AgentEval.Compliance.Gdpr.X.Y"/>`. The CS1574 compiler warning will catch most surviving stale refs.

Verify: `dotnet build -p:GenerateDocumentationFile=true -f net10.0 -c Debug` — must be 0 CS1574 warnings + 0 errors.

#### Task P4b.3 — Same rename for EuAiAct

Repeat P4b.1 + P4b.2 for the EuAiAct assembly:
- Directory: `src/AgentEval.Evals.Compliance.EuAiAct/` → `src/AgentEval.Compliance.EuAiAct/`
- Csproj rename + RootNamespace update
- Mass-rename `namespace AgentEval.EuAiActBenchmark` → `namespace AgentEval.Compliance.EuAiAct` across all .cs files
- XML doc crefs updated

The cross-regulation linker (which references the Gdpr assembly) gets its using directive updated naturally.

#### Task P4b.4 — Update consumers (tests + CLI + Mission Control + Samples)

The bulk of this task is `using` directive updates. Pattern:
```
using AgentEval.GdprBenchmark.Articles;     → using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.EuAiActBenchmark.Pillars;   → using AgentEval.Compliance.EuAiAct.Pillars;
```
And remove the 13 `using XxxBenchmarkFactory = AgentEval.Benchmarks.XxxBenchmark;` aliases — once the parent-namespace collision is gone, the aliases are no longer needed.

**CRITICAL pitfall — embedded resource manifest paths**: tests that load resources via `assembly.GetManifestResourceStream("AgentEval.GdprBenchmark.Reporting.Schema.gdpr-evidence.schema.json")` will break, because the manifest path is derived from `<RootNamespace>` (which we just changed). Update every such string to `AgentEval.Compliance.Gdpr.Reporting.Schema.gdpr-evidence.schema.json`. Same for EuAiAct. Grep for `GetManifestResourceStream` and any string starting with `AgentEval.GdprBenchmark.` or `AgentEval.EuAiActBenchmark.` to find them all.

The `EvaluatorCardRegistry.cs:60` reflection anchor (`typeof(AgentEval.Benchmarks.AgenticBenchmark).Assembly`) is fine — it targets `AgentEval.Benchmarks` (a stable name), not the compliance assemblies.

Verify: full suite green. The `GdprEvidenceSchema` test in particular is the canary for embedded-resource path correctness.

#### Task P4b.5 — Extend `BenchmarkNamespaceContractTests` with reflection enumerator

Last Review #11's Concern B asked for the contract test to enumerate `*Benchmark`-suffixed types in repo assemblies rather than use a hardcoded forbidden list. Implement this:

```csharp
[Fact]
public void EveryStaticBenchmarkFactoryLivesInAgentEvalBenchmarksNamespace()
{
    var assemblies = new[]
    {
        typeof(AgentEval.Benchmarks.AgenticBenchmark).Assembly,
        typeof(AgentEval.Compliance.Gdpr.Articles.ArticlesRegistry).Assembly,  // GDPR assembly anchor type
        // ... one anchor type per assembly that ships a benchmark factory
    };

    var factoryNames = new[] { "AgenticBenchmark", "GdprBenchmark", "EuAiActBenchmark", "PerformanceBenchmark", "MemoryBenchmark", "OwaspBenchmark", "MitreBenchmark", "LongMemEvalBenchmark" };

    foreach (var asm in assemblies)
    {
        var factoriesInThisAssembly = asm.GetTypes()
            .Where(t => factoryNames.Contains(t.Name) && t.IsClass && t.IsAbstract && t.IsSealed) // static class
            .ToList();

        foreach (var t in factoriesInThisAssembly)
        {
            Assert.Equal("AgentEval.Benchmarks", t.Namespace);
        }
    }
}
```

This catches a future regression where a developer adds a new `XxxBenchmark` factory in the wrong namespace.

Also add the negative-case test: no type named `GdprBenchmark` / `EuAiActBenchmark` / `AgenticBenchmark` etc. exists *outside* `AgentEval.Benchmarks`.

#### Task P4b.6 — Remove the 13 aliases (CONCERN A from LR#11)

Once P4b.3 finishes, the parent-namespace-vs-type-name collision is gone (because `AgentEval.GdprBenchmark` is no longer a namespace). The 13 `using XxxBenchmarkFactory = AgentEval.Benchmarks.XxxBenchmark;` aliases become unnecessary.

Either:
- **Option A (preferred)**: delete each alias line; replace `XxxBenchmarkFactory.Method(...)` with `XxxBenchmark.Method(...)`. Cleaner.
- **Option B (defensive)**: keep the aliases but add an explanatory comment noting they're now redundant but kept for stability across in-flight branches. Worse.

Pick Option A. The 13 sites are listed in `lastreview/11-phase4-gate-review.md` under Concern A.

#### Task P4b.7 — Phase-4b verification (Opus gate)

Verification checklist:
1. `dotnet build -p:GenerateDocumentationFile=true -f net10.0 -c Debug --no-incremental` clean (0 errors, 0 warnings).
2. Full test suite: ≥3642 main + ≥452 Memory tests pass, 0 fail.
3. `GdprEvidenceSchema` test passes (canary for embedded-resource paths).
4. `EvaluatorCardRegistry` reflection anchor still resolves (smoke-test it).
5. Mission Control GraphQL untouched (`grep -rn "AgentEval.GdprBenchmark\|AgentEval.EuAiActBenchmark" src/AgentEval.MissionControl/` returns empty).
6. No `using XxxBenchmarkFactory = ...` aliases survive (`grep -rn "BenchmarkFactory =" tests/ src/ samples/` returns empty).
7. `BenchmarkNamespaceContractTests` reflection-enumerator variant passes.
8. CHANGELOG draft entry for the rename is queued for Phase 9 (don't write it yet).
9. Sign-off doc in `lastreview/12-phase4b-gate-review.md`.

**Phase 4b estimated effort**: 4h. **Risk: high** (embedded-resource path strings + cref docs + parent-namespace cascades) — Opus personally gate-reviews.

---

### Phase 5 — `OwaspBenchmark` façade + CLI

**Phase goal**: Add the first new benchmark family — OWASP LLM Top 10 — as a thin façade over the existing `AttackPipeline`. The façade lives in `AgentEval.RedTeam` assembly under `AgentEval.Benchmarks` namespace. Includes the all-important `EvaluateAsync` adapter that makes OWASP results flow through the unified output-store.

#### Task P5.1 — Implement `OwaspBenchmark` static class

- **Inputs**: `src/AgentEval.RedTeam/RedTeam/AttackPipeline.cs`, `src/AgentEval.RedTeam/RedTeam/Attack.cs` (mapping of OWASP IDs to attacks), `src/AgentEval.RedTeam/RedTeam/Reporting/Compliance/OWASPComplianceReporter.cs`.
- **Outputs**: `src/AgentEval.RedTeam/RedTeam/Compliance/OwaspBenchmark.cs` (~80 LOC), namespace `AgentEval.Benchmarks`, partial class.
- **Step-by-step**:
  1. Declare 4 static factory methods: `Top10(IEvaluator? judge)`, `Smoke(IEvaluator? judge)`, `AuditGrade(IEvaluator? judge)`, `Top10ForRag(IEvaluator? judge)`.
  2. Each builds an `AttackPipeline.Create()` with the appropriate attack subset and intensity, then wraps it in an `OwaspBenchmarkRun`.
  3. Use Part 3.1 of the architecture proposal for the attack→OWASP mapping (9 of 10 attacks already implemented; LLM03/04/08/09 surface as `NotTested` in the report).
- **Acceptance**: Class compiles; static factory invocation does not throw.
- **Effort**: 1h.

#### Task P5.2 — Implement `OwaspBenchmarkRun`

- **Inputs**: `RedTeamResult`, `OWASPComplianceReport`, `EvalResult`, `EvalInput`.
- **Outputs**: new `OwaspBenchmarkRun` class in same file as P5.1.
- **Step-by-step**:
  1. Three public methods: `Task<RedTeamResult> ScanAsync(IEvaluableAgent agent, CancellationToken)`, `Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken)`, `OWASPComplianceReport GenerateReport(RedTeamResult result)`.
  2. `EvaluateAsync` internally calls `ScanAsync`, then maps each OWASP category (LLM01..LLM10) to a leaf `EvalResult` with `score = passRate`, `evidence` carrying the failing prompts + responses, `auditChain` carrying the `OWASPComplianceReport` cross-reference IDs.
  3. The `EvalResult.AverageScore` becomes the headline number for the family.
- **Acceptance**: All three methods callable with stub data.
- **Pitfalls**:
  - `EvalInput` doesn't naturally contain an agent — the adapter needs to receive the agent through an alternate path (likely via `ScanAsync`'s agent parameter; `EvaluateAsync(EvalInput)` should accept an agent factory or use a known stub). Look at how `OwaspComplianceReporter` consumes `RedTeamResult` to ensure shape compatibility.
- **Effort**: 1h.

#### Task P5.3 — Add `OwaspBenchmarkTests`

- **Inputs**: P5.1 + P5.2.
- **Outputs**: `tests/AgentEval.Tests/Benchmarks/OwaspBenchmarkTests.cs` + `OwaspBenchmarkAdapterTests.cs`.
- **Step-by-step**:
  1. **Preset shape test**: assert `Top10()` exposes all 9 implemented attacks; assert `Smoke()` exposes exactly the 3 MVP attacks.
  2. **`EvaluateAsync` round-trip test**: run against `StubEvaluableAgent` that always passes, assert `EvalResult.AverageScore == 1.0`; flip stub to always fail, assert `AverageScore < 0.5`.
  3. **Report generation test**: `GenerateReport()` produces an `OWASPComplianceReport` with all 10 categories populated (4 as `NotTested`, 6 as `Tested`).
  4. **Output-store round-trip**: serialize `EvalResult` from `EvaluateAsync` → JSON → deserialize → assert byte-identical reconstruction.
- **Acceptance**: All 4 tests pass.
- **Effort**: 1h.

#### Task P5.4 — Add `BenchOwaspCommand` to CLI

- **Inputs**: existing `src/AgentEval.Cli/Commands/BenchAgenticCommand.cs` as template.
- **Outputs**: `src/AgentEval.Cli/Commands/BenchOwaspCommand.cs` (~150 LOC).
- **Step-by-step**:
  1. Copy `BenchAgenticCommand.cs` structurally; rename to `BenchOwaspCommand`.
  2. Sub-commands: `--preset {top10|smoke|audit|top10-rag}`.
  3. Required: `--subject <agent-id>` (consistent with other bench commands per v0.8.x).
  4. Output: writes `EvalResult` to `workspace/runs/{ts}/owasp-{preset}.json` *and* `OWASPComplianceReport` to `workspace/compliance/owasp-{ts}/report.{json|md|pdf}`.
  5. Register in `Program.cs` next to `benchAgenticCmd`.
- **Acceptance**: `agenteval bench owasp --preset smoke --subject stub` runs to completion.
- **Test coverage**: `tests/AgentEval.Tests/Cli/BenchOwaspCommandTests` — arg parsing, dry-run (no actual agent invocation), output path resolution.
- **Pitfalls**:
  - The CLI currently doesn't have a `ProjectReference` to `AgentEval.RedTeam` because no command needed it. Add it to `AgentEval.Cli.csproj`.
  - Wire judge resolution through `JudgeFactory.cs` (existing helper).
- **Effort**: 1h.

#### Task P5.5 — Phase-5 verification gate

- [ ] All P5.3 tests pass.
- [ ] `agenteval bench owasp --preset smoke --subject test-agent --input "hello"` produces both `EvalResult` JSON + `OWASPComplianceReport` artefacts.
- [ ] **Meaningful test**: `OwaspBenchmarkTests.EvaluateAsync_RoundTripsThroughOutputStore` passes — the headline architectural promise of Phase 5.
- [ ] Full suite green.

**Phase 5 estimated effort**: 4h.

---

### Phase 6 — `MitreBenchmark` façade + CLI

**Phase goal**: Symmetric to Phase 5 for MITRE ATLAS.

#### Task P6.1 — `MitreBenchmark` static class

- Mirror P5.1; presets `AtlasBaseline`, `AtlasSmoke`, `AtlasAuditGrade`. Reuse internals — most of the heavy lifting is in `OwaspBenchmarkRun`'s `EvaluateAsync` shape, factored into a base helper.
- **Effort**: 0.5h.

#### Task P6.2 — `MitreBenchmarkRun`

- Mirror P5.2; uses `MITREATLASReporter` instead of `OWASPComplianceReporter`. `EvalResult` leaves are tagged with ATLAS technique IDs (`AML.T0051`, etc.) instead of OWASP category IDs.
- **Effort**: 0.5h.

#### Task P6.3 — `MitreBenchmarkTests`

- Mirror P5.3. **Effort**: 0.5h.

#### Task P6.4 — `BenchMitreCommand`

- Mirror P5.4; sub-commands `--preset {atlas-baseline|atlas-smoke|atlas-audit}`. **Effort**: 1h.

#### Task P6.5 — Phase-6 verification gate

- [ ] All P6.3 tests pass.
- [ ] `agenteval bench mitre --preset atlas-smoke --subject test-agent` runs to completion.
- [ ] **Meaningful test**: `MitreBenchmarkTests.EvaluateAsync_TaggsLeavesWithAtlasIds` passes — confirms the ATLAS technique IDs are surfaced in audit-chain entries (not just OWASP IDs).
- [ ] Full suite green.

**Phase 6 estimated effort**: 3h.

---

### Phase 7 — `LongMemEvalBenchmark` façade

**Phase goal**: Add a named-preset façade over the existing `LongMemEvalBenchmarkRunner` so the academic LongMemEval benchmark is discoverable under the unified namespace.

#### Task P7.1 — Implement `LongMemEvalBenchmark` façade

- **Inputs**: `src/AgentEval.Memory/External/LongMemEval/LongMemEvalBenchmarkRunner.cs`, `LongMemEvalOptions.cs`.
- **Outputs**: new `src/AgentEval.Memory/External/LongMemEval/LongMemEvalBenchmark.cs` (~40 LOC) in namespace `AgentEval.Benchmarks`, partial class.
- **Step-by-step**:
  1. Two static factory methods: `Subset(IExternalBenchmarkJudge judge)` returns a pre-configured runner over a small subset; `Full(judge)` returns the full-dataset runner.
  2. Each returns `LongMemEvalBenchmarkRunner` directly (no need for a new wrapper type — the runner already exposes the right API).
- **Acceptance**: Methods compile, return runner instances configurable.
- **Test coverage**: New `tests/AgentEval.Memory.Tests/LongMemEvalBenchmarkTests.cs` (3 tests):
  - Façade preset returns runner with correct options.
  - Subset preset has smaller dataset than Full.
  - Round-trip: serialize the runner's output → deserialize → byte-identical.
- **Effort**: 1h.

#### Task P7.2 — Update namespace contract test (P4.6)

- Add `LongMemEvalBenchmark` to the assertion list. **Effort**: 0.25h.

#### Task P7.3 — Phase-7 verification gate

- [ ] All façade tests pass.
- [ ] **Meaningful test**: `BenchmarkNamespaceContractTests` now asserts 6 factories (Agentic, Gdpr, EuAiAct, Memory, Performance, LongMemEval — Owasp + Mitre added by Phase 5/6).
- [ ] Full suite green.

**Phase 7 estimated effort**: 2h.

---

### Phase 8 — `BenchmarkFamilyRegistry` (canonical) + CLI helpers + `bench perf`

**Phase goal**: Establish `BenchmarkFamilyRegistry` as **AgentEval's canonical single-source-of-truth** for "where is every benchmark family registered". Then build the CLI discoverability helpers (`bench --list`, `bench {family} --help`) on top of it. Then wire the previously CLI-less `bench perf` command.

> **Canonical convention** (introduced in this phase): every benchmark family — current (Agentic, GDPR, EU AI Act, OWASP, MITRE, LongMemEval, Performance, Memory) and future (HIPAA, PCI-DSS, ISO 42001, NIS2, …) — registers itself with `BenchmarkFamilyRegistry`. Registration entries carry the family name, one-line description, presets list, factory delegate (`(IEvaluator judge) → CompositeEval` or equivalent), and `EvaluateAsync` adapter delegate. `bench --list`, per-family `--help`, Mission Control discovery surfaces, and the eventual `agenteval-tool` external-registrar mechanism all read from this registry. Documented in ADR-017 and reaffirmed in `docs/architecture.md` during Phase 9.

#### Task P8.1 — Design + implement `BenchmarkFamilyRegistry` (CANONICAL)

- **Location**: `src/AgentEval.Core/Benchmarks/BenchmarkFamilyRegistry.cs` (Core because every consumer assembly references Core — registry is reachable from everywhere without circular deps).
- **Shape**:
  ```csharp
  public sealed class BenchmarkFamily
  {
      public string Name { get; }                        // "agentic", "gdpr", "eu-ai-act", "owasp", ...
      public string Description { get; }                 // one-line user-facing text
      public CostTier DefaultCostTier { get; }           // low / medium / high
      public IReadOnlyList<BenchmarkPreset> Presets { get; }   // list of (name, description, factory delegate)
      public Func<EvalInput, IEvaluator?, CancellationToken, Task<EvalResult>>? EvaluateAsync { get; }
      // ... plus metadata for Mission Control rendering, doc-link URLs, etc.
  }

  public static class BenchmarkFamilyRegistry
  {
      public static void Register(BenchmarkFamily family);
      public static BenchmarkFamily? TryGet(string name);
      public static IReadOnlyList<BenchmarkFamily> All { get; }
      // Thread-safe; idempotent on duplicate registration (same name + same content); throws on conflict.
  }
  ```
- **Source-XML-doc convention note**: include a paragraph in the registry class's XML doc explaining "every benchmark family plugs in here; future regulations (HIPAA / PCI-DSS / NIS2 / ISO 42001) register here without changing CLI plumbing".
- **Test coverage**: `BenchmarkFamilyRegistryTests` covering: register / lookup-by-name / enumerate-all / unique-name invariant / preset-overlap detection / extensibility (an *external* test assembly can register a new family at runtime and have it appear in `All`). The extensibility test is the most important one — it proves the registry is genuinely a plug-in surface, not a hardcoded enum dressed in a class.
- **Owner**: Opus (canonical-convention design call).
- **Effort**: 1.5h.

#### Task P8.2 — Register all 8 existing benchmark families

- At assembly load (each family's own assembly init) OR via a discovery step in CLI startup, register: Agentic, GDPR, EU AI Act, OWASP, MITRE, LongMemEval, Performance, Memory.
- Prefer the **assembly-init** path so external consumers (e.g., a future `AgentEval.Compliance.Hipaa` NuGet) auto-register when their DLL loads. The CLI itself does NOT need to know about specific families — it only knows about the registry.
- **Test coverage**: integration test that boots a minimal hosting context, lets all family assemblies init, then asserts `BenchmarkFamilyRegistry.All.Count >= 8`.
- **Effort**: 1h.

#### Task P8.3 — `BenchPerfCommand` (CLI)

- Mirror `BenchAgenticCommand` shape. Sub-commands: `latency`, `throughput`, `cost`. Each resolves the perf family via `BenchmarkFamilyRegistry.TryGet("perf")` and dispatches to its `EvaluateAsync` adapter (Phase 3's contribution).
- **Test coverage**: `BenchPerfCommandTests` — arg parsing, output path. Plus an end-to-end test that runs `bench perf latency --subject stub --prompt hello` and confirms an `EvalResult` JSON is written to `workspace/runs/`.
- **Effort**: 1h.

#### Task P8.4 — `bench --list` reads from registry

- Implementation: enumerate `BenchmarkFamilyRegistry.All` and print a formatted table of `(family, presets, cost-tier, description)`.
- **Critical**: this command must NOT contain a hardcoded list of families. The test `BenchListCommandTests.OutputComesFromRegistry` should temporarily register an extra synthetic family and assert it appears in the `--list` output.
- **Test coverage**: covers all 8 default families + the synthetic-extra extensibility check.
- **Effort**: 0.5h.

#### Task P8.5 — `bench {family} --help` preset enumeration

- For each `bench {family}` sub-command, the `--help` output dynamically lists `--preset` options pulled from `BenchmarkFamilyRegistry.TryGet(family).Presets`.
- **Test coverage**: per-family tests assert preset names + descriptions appear in help output.
- **Effort**: 0.75h.

#### Task P8.6 — Phase-8 verification gate (Opus)

- [ ] All P8.x tests pass.
- [ ] `BenchmarkFamilyRegistry` is documented as canonical in its source XML doc and cross-referenced from ADR-017 (note: actual ADR-017 update happens in Phase 9, but reference is queued).
- [ ] `agenteval bench --list` prints all 8 families with at least one preset each, sourced from the registry.
- [ ] `agenteval bench owasp --help` lists `top10`, `smoke`, `audit`, `top10-rag` (sourced from registry, not hardcoded).
- [ ] **Meaningful test**: `BenchListCommandTests.AllRegisteredFamiliesAreRunnable` smoke-tests each family with `--dry-run`.
- [ ] **Extensibility test**: a synthetic test-only family registered at runtime appears in `--list`.
- [ ] Full suite green.
- [ ] Opus sign-off doc in `lastreview/16-phase8-gate-review.md`.

**Phase 8 estimated effort**: 5h.

---

### Phase 9 — Docs, CHANGELOG, ADR update, samples

**Phase goal**: Bring documentation in line with the new architecture so consumers can adopt v0.10.0 without spelunking.

#### Task P9.1 — `CHANGELOG.md` entry

- Add the v0.10.0-beta block from §4.5 of the architectural proposal. Verbatim or near-verbatim. **Effort**: 0.5h.

#### Task P9.2 — Update ADR-017 Status to `Implemented`

- Append: "Implemented in v0.10.0-beta at commit `<hash>`". The hash is the last commit on the branch before tagging. **Effort**: 0.25h.

#### Task P9.3 — `README.md` + `docs/benchmarks.md`

- Update the benchmark table to show all 7 families with `using AgentEval.Benchmarks;` example. Update install snippet. **Effort**: 1h.

#### Task P9.4 — Update `samples/AgentEval.Samples/DataAndInfrastructure/04_BenchmarkSystem.cs`

- Replace fan-out `using`s with `using AgentEval.Benchmarks;`. Add invocation examples for the new OWASP/MITRE/Perf families. **Effort**: 0.25h.

#### Task P9.5 — Version bump

- `src/AgentEval/AgentEval.csproj`: `<Version>0.10.0-beta</Version>` + new `<PackageReleaseNotes>`. **Effort**: 0.1h.

#### Task P9.6 — Phase-9 verification gate

- [ ] `dotnet pack src/AgentEval/AgentEval.csproj -c Release` succeeds.
- [ ] DocFX build (or whatever the project uses) is green.
- [ ] CHANGELOG renders correctly in GitHub preview.
- [ ] **Meaningful test**: at least one sample (`samples/AgentEval.GdprBenchmark.Demo`) compiles + runs against the *packed* NuGet (not the source ProjectReference). Smoke test of consumer experience.

**Phase 9 estimated effort**: 3h.

---

### Phase 10 — Pack size check, Opus review, tag, release

**Phase goal**: Final polish + sign-off + release.

#### Task P10.1 — `dotnet pack` size check

- Run `dotnet pack` against the umbrella, measure `.nupkg` size, compare to v0.9.0-beta. **If the size grew by > 5 MB**, file a follow-up issue to evaluate splitting `AgentEval.Compliance` into its own umbrella; but do **not** block the release on that — measure and record. **Effort**: 0.5h.

#### Task P10.2 — Final Opus review pass

- Read every diff on the branch end-to-end. File `lastreview/12-v0.10.0-final.md` with sign-off or list of blockers. **Effort**: 2h.

#### Task P10.3 — Merge, tag, push

- User-authorised: open PR to `main`, merge, tag `v0.10.0-beta`, push to NuGet. **Effort**: 0.5h.

**Phase 10 estimated effort**: 3h.

---

### Architectural details Opus #10 did not fully resolve

1. **`samples/AgentEval.GdprBenchmark.Tests/` does NOT exist** (verified). Tests live in `tests/AgentEval.Tests/GdprBenchmark/` and load goldens from `tests/AgentEval.Tests/GdprBenchmark/Calibration/Golden/*.jsonl`. **The goldens stay in the test project** — they're owned by the calibration regression suite, not by the production assembly. The Phase 1 plan respects this: only production source moves; test fixtures and goldens stay put. The test project's `<ProjectReference>` is rewired from `samples/…` to `src/AgentEval.Evals.Compliance.Gdpr/…`. No risk of double-embedding.
2. **Deprecation / forwarder story for v0.9.0-beta consumers**. We're in 0.x-beta; the `[Obsolete]`+type-forward shim is overkill. Opus #10 recommended against it; I concur. CHANGELOG + a "migration table" section in `docs/benchmarks.md` is sufficient. If we were 1.x stable, this would be different.
3. **`bench --list` discoverability** is now implemented as Phase 8 task P8.2 with a central `BenchmarkFamilyRegistry` as the single source of truth. Both `--list` and per-family `--help` read from it; adding a new family is one registry entry.
4. **GraphQL schema impact**: Verified — only `src/AgentEval.MissionControl/Services/EvaluatorCardRegistry.cs` references `AgentEval.Evals.Agentic`'s embedded resources, and that's a comment, not a `using`. No GraphQL resolver names change. No Mission Control breakage. The GraphQL schema's `Query.evaluators` resolver continues to work unchanged because evaluator cards are loaded from embedded JSON resources, and those resources move with the assembly.
5. **Embedded resources move cleanly**. Verified: `Articles/Yaml/*.yaml`, `Resources/Prompts/*.md`, `Reporting/Schema/*.json`, `DomainPacks/**/*.yaml` all use globs that work identically post-move. The only adjustment is the csproj `<EmbeddedResource>` glob lines, which P1.1 + P2.1 handle explicitly.
6. **Schema JSON paths** (`gdpr-evidence.schema.json`, `eu-ai-act-evidence.schema.json`) currently embedded in samples; after promotion they're embedded in `src/AgentEval.Evals.Compliance.{Gdpr,EuAiAct}/Reporting/Schema/`. **No DocFX break** because the schema files aren't part of the doc build — they're consumed at runtime by `GdprEvidenceSchemaAndFileSystemTest`. The test reads from the embedded resource path `AgentEval.GdprBenchmark.Reporting.Schema.gdpr-evidence.schema.json` — that path changes after Phase 4's namespace rename. **P4.2 must update the test's embedded-resource path string**; otherwise the test fails. This is a real pitfall — call it out explicitly in the brief.
7. **One assembly per regulation** is the right convention going forward. Future additions (HIPAA, PCI-DSS, NIS2, ISO 42001) land as `src/AgentEval.Evals.Compliance.{Hipaa|PciDss|Nis2|Iso42001}/` siblings. Naming convention: `AgentEval.Evals.Compliance.{Regulation-or-Standard-Code}`, where the code is the most widely-recognised acronym in TitleCase (HIPAA, PciDss, Nis2, Iso42001). Documented in ADR-017.
8. **`dotnet pack` size check** is Phase 10 task P10.1 — explicit verification step with a 5 MB delta tripwire.
9. **CI workflow updates** are Phase 1 + Phase 2 tasks P1.5 + P2.5. The other workflows (`ci.yml`, `release.yml`, etc.) don't reference benchmark project paths directly — verified by Grep.

---

### Risk register

| # | Risk | Likelihood | Impact | Mitigation | Detection signal |
|---|---|---|---|---|---|
| R1 | Embedded resource glob breaks during `git mv` — YAML files orphaned, runtime `FileNotFoundException` | Medium | High | Phase 1 gate requires `AllArticleYamlsValidateTest` + `GdprEvidenceSchemaAndFileSystemTest` pass. These tests *only* succeed if the EmbeddedResource glob resolves correctly. | Test failure surfaces immediately at gate P1.6. |
| R2 | Embedded resource path string in tests becomes stale after namespace rename (Phase 4) | Medium | Medium | P4.2 task brief explicitly flags this; verification gate P4.7 requires `GdprEvidenceSchemaAndFileSystemTest` to pass. | Specific named test fails at P4.7. |
| R3 | `git mv` not used — file history broken for blame continuity | Low | Low | Each move task brief specifies `git mv`. P1.6 gate includes `git log --follow` smoke check. | Manual review at gate. |
| R4 | Mass-rename of `using` directives (P4.5) misses files referenced reflectively (DI registration, dynamic activation) | Medium | High | Verification gate runs full test suite. Reflection-heavy tests (DI registration, evaluator cards) catch any miss. Run `dotnet test` immediately after P4.5; don't batch with P4.6. | Reflection-based test failures (likely `ServiceCollectionExtensionsTests` or similar). |
| R5 | `OwaspBenchmark.EvaluateAsync` adapter doesn't faithfully reproduce the `OWASPComplianceReport` semantics (e.g., loses category metadata) | Medium | Medium | P5.3 includes explicit round-trip test asserting all 10 categories are present in `EvalResult` (4 as `NotTested`). | `OwaspBenchmarkTests` failure. |
| R6 | `dotnet pack` size balloons beyond 10 MB due to GDPR + EuAiAct embedded YAMLs / PDFs | Low | Medium | P10.1 measures explicitly. Mitigation if breached: file follow-up issue, ship anyway, fix in v0.11.0. | P10.1 measurement. |
| R7 | Compliance benchmarks have circular reference (EuAiAct depends on Gdpr via CrossRegulationLinker) and namespace consolidation breaks the dependency direction | Medium | High | EuAiAct's csproj keeps explicit `<ProjectReference>` to Gdpr (per P2.1). Internal types stay in domain ns — the linker doesn't need the unified ns. | Build failure at P2.4. |
| R8 | LongMemEval external dataset isn't shipped in NuGet, so `LongMemEvalBenchmark.Full()` throws at runtime for consumers | Low | Low (documented limitation) | Document explicitly in CHANGELOG + README. The runner already has this constraint; the façade doesn't change it. | n/a (pre-existing constraint). |
| R9 | OWASP / MITRE CLI commands accidentally write into the same output directory and overwrite each other's artefacts | Medium | Medium | Each command writes to a distinct subdirectory under `workspace/compliance/` (`owasp-{ts}/`, `mitre-{ts}/`). Verified in P5.4 + P6.4 acceptance criteria. | Manual check during P8.4 verification. |
| R10 | `BenchmarkNamespaceContractTests` (P4.6) is too strict and fails on legitimate domain types named `*Benchmark` (e.g., `MemoryBenchmarkRunner`) | Medium | Low | Test maintains an explicit exception list of domain types. Document the convention: "types ending in `Benchmark` are factories and must live in `AgentEval.Benchmarks`; types ending in `BenchmarkRunner`/`BenchmarkResult` are domain types and may live anywhere." | Test failure at P4.7. |
| R11 | Phase 4 namespace consolidation creates a hidden ambiguity: `GdprBenchmark` in `AgentEval.Benchmarks` vs. references via the old `AgentEval.GdprBenchmark` namespace (now occupied only by domain types) | Medium | Medium | Mass-rename in P4.5 adds the new `using` but doesn't remove the old one unconditionally. Verify by Grep that no file has both `using AgentEval.Benchmarks;` AND references `GdprBenchmark` via fully-qualified `AgentEval.GdprBenchmark.GdprBenchmark` (which doesn't exist post-rename). | Build failure or compiler ambiguity warning at P4.5. |
| R12 | Highest-risk phase: **Phase 4 (namespace consolidation)**. It touches the largest number of files (~141), introduces the most subtle bugs (embedded-resource paths, reflection-based DI, `<see cref>` doc lookups), and has the least mechanical safety net. | High | High | Two mitigations: (a) split P4.5 into one-domain-at-a-time sub-tasks if a single mass-rename feels risky; (b) require Opus to gate-review P4.7 personally rather than delegating to Sonnet. | Test suite failure rate spike at P4.7. |

---

### Convention summary (for future contributors)

This summary captures the durable conventions this v0.10.0-beta arc establishes. Phase 9 ports these to `docs/architecture.md` as the authoritative reference for future contributors.

- **Top-level preset-factory class** = `public static partial class {Family}Benchmark` in namespace `AgentEval.Benchmarks`, declared in the domain assembly's root.
- **Domain types** (registries, pillars, scenarios, runners, models) = `*` in `AgentEval.{Domain}.{Sub-area}` namespace. Stay in domain assembly.
- **`*BenchmarkRunner`** = runner-shaped types in domain ns (allowed; documented exception to the factory convention).
- **`*BenchmarkResult`** = result records in domain ns (allowed; documented exception).
- **One regulation = one assembly** at `src/AgentEval.Compliance.{RegCode}/` (note: post-Phase-4b, compliance lives outside the `Evals.*` umbrella — see Phase 4b detail). Embedded in umbrella via `PrivateAssets="all"`.
- **`EvaluateAsync(EvalInput, CT) → EvalResult` is the canonical result-type homogenisation primitive.** Every benchmark family that ships a non-`CompositeEval`-native result type (e.g., `LatencyBenchmarkResult`, `OWASPComplianceReport`, `MITREATLASReport`, `MemoryBenchmarkResult`) MUST provide an `EvaluateAsync` adapter. The adapter synthesises an `EvalResult` whose `SubResults` enumerate per-leaf metrics, preserving the natural result type in `Provenance` for downstream consumers that want richer data. This is what allows all benchmark families to flow through the same `IRunOutputStore` / audit-chain / Mission Control rendering pipeline. **Documented in `docs/architecture.md` during Phase 9.**
- **`BenchmarkFamilyRegistry` is the canonical "where is every benchmark family registered" mechanism.** All current families (Agentic / GDPR / EU AI Act / OWASP / MITRE / LongMemEval / Performance / Memory) and all future families (HIPAA / PCI-DSS / ISO 42001 / NIS2 / …) plug in here. `agenteval bench --list`, per-family `--help`, Mission Control's family-discovery surface, and any future external-registrar plugin mechanism all read from this single source of truth. Implementing a new benchmark family without registering here is a contract violation caught by `BenchmarkNamespaceContractTests`. **Established in Phase 8.**

---

### Pre-flight checklist before starting Phase 1

A Sonnet picking up Phase 1 should verify *before* the first `git mv`:

- [ ] Working directory clean (`git status` shows only untracked `ClaudeTeamProposal.md` per current state).
- [ ] On branch `feature/v0.10.0-unified-benchmarks`.
- [ ] `dotnet build` from solution root is green.
- [ ] `dotnet test` reports 3625+ tests passing.
- [ ] This playbook is checked-in (committed) so reverts have a known-good baseline.
- [ ] User has authorised local commits on this branch (per memory `feedback_no_commit_until_notice.md`: lifted for current branch since 2026-05-11).

If any pre-flight item fails, stop and resolve before proceeding.

---

**End of execution playbook.**
