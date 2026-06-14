# MAF 1.10.0 Upgrade — Implementation Plan

> Repository-wide upgrade of Microsoft Agent Framework (MAF) from **1.3.0 → 1.10.0**.
> Generated with the **maf-doctor (maf-autopilot)** MCP toolchain. Baseline captured 2026-06-14.

---

## 1. Executive summary

| Item | Value |
|---|---|
| Current MAF version | **1.3.0** (`Microsoft.Agents.AI`, `.OpenAI`, `.Workflows`, `.Workflows.Generators`) |
| Target MAF version | **1.10.0** |
| Current `Microsoft.Extensions.AI` | `10.5.0` → **must bump to ≥ 10.6.0** |
| Target TFMs | `net8.0; net9.0; net10.0` — all supported by 1.10.0 (.NET ≥ 8.0) ✅ |
| maf-doctor health grade (baseline) | **B** — 0 errors, 0 warnings, 281 unbounded-cost `RunAsync` sites (pre-existing, non-blocking) |
| Migration-risk score (heuristic) | **HARD (37)** — but see §3: the high-impact breaking surfaces are **not used** in source |
| Realistic effort | Low–Medium — a centralised package bump for the core (Group A) **plus a CPM-consolidation pass that attaches the inline-pinned sample projects to central package management** (Group B, §4); the published-`AgentEval`-package consumers (incl. `NuGetConsumer`) are **left untouched** until a new package ships (§4.1) |
| Projects referencing MAF | **10** (all in `AgentEval.sln`): 6 CPM-managed + 4 inline-pinned — full inventory in §4 |

**Key finding:** the migration-risk heuristic rates this *HARD* because the 1.4→1.10 registry
contains many removed/changed symbols. A targeted scan of `src/`, `samples/`, and `tests/`
shows **none of those high-impact surfaces are referenced** (see §3). The practical work is a
version bump plus a clean build to surface any residual `CS0246`/`CS0618`.

---

## 2. Baseline (captured via maf-doctor)

- `MafDoctor` → grade **B**. 3 `[MessageHandler]` methods inspected, **0 silent-starvation risks**, **0 anti-pattern errors**, **0 prompt-lint issues**.
- `MafScoreMigrationRisk` → **HARD (37)**: heuristic breakdown `cs0246=14`, `cs0117=4`, `cs0618=1` (registry-projected, not confirmed against actual call sites).
- `MafCompatibility 1.10.0` → .NET ≥ 8.0, `Microsoft.Extensions.AI` ≥ 10.6.0, `…Workflows.Generators` 1.10.0, identity = `ManagedIdentityCredential`.
- The 281 unbounded-cost sites are an **independent, pre-existing** finding (missing `MaxOutputTokens`) — not introduced by this upgrade. Track separately; do **not** block the upgrade on them.

### 1.10.0 documented breaking changes (from `MafCompatibility`)
- Skill `Content` / `Resources` / `Scripts` properties **removed**.
- `ToolApprovalAgent` constructor now takes `ToolApprovalAgentOptions?` (was `JsonSerializerOptions?`).
- `SubAgentsProvider` / `SubTaskInfo` / `SubTaskStatus` **removed**.
- `GroupChatWorkflowBuilder` / `MagenticWorkflowBuilder` `.WithName` / `.WithDescription` **removed**.

---

## 3. Impact analysis — what this repo actually uses

A source scan (`src/`, `samples/`, `tests/`) for the breaking symbols was run. Results:

| Breaking surface (registry IDs) | Used in repo? | Action |
|---|---|---|
| Skill `Content`/`Resources`/`Scripts`, `ScriptDirectories`/`ResourceDirectories` (MAF110-AGENT/OPTIONS) | ❌ No | None |
| `ToolApprovalAgent` ctor / `UseToolApproval` (MAF110-TOOL-001, MAF110-EXTENSIO-001) | ❌ No | None |
| `SubAgentsProvider` / `SubTaskInfo` / `SubTaskStatus` (MAF110-PROVIDER/SUB) | ❌ No | None |
| `GroupChatWorkflowBuilder`/`MagenticWorkflowBuilder` `.WithName`/`.WithDescription` (MAF110-BUILDER) | ❌ No | None |
| `AgentSkill.RunAsync` / `Invoke` / `BeginInvoke` (MAF140-AGENT-001..004) | ❌ No (`AIFunctionArguments`/`AgentSkill` absent) | None |
| `TodoProvider.GetAllTodos` / `GetRemainingTodos` (MAF150-PROVIDER-001/002) | ❌ No | None |
| `WorkflowEvaluationExtensions.EvaluateAsync` (MAF161-EXTENSIO-001) | ❌ No | None (additive param if adopted later) |
| A2A (`RegisterA2AAgent`/`MapA2A`), `StreamAsync`, `GetNewThread`, `AddFanInBarrierEdge` arg order (MAF130-*) | ✅ Already migrated to 1.3.0 idioms | Verify only |

**Surfaces the repo does use (stable across 1.x):** `RunStreamingAsync` (workflows), `WatchStreamAsync`,
`WorkflowBuilder`, `AIAgent`, `ChatClientAgent`, `AgentSession` / `CreateSessionAsync` /
`SerializeSessionAsync`. These are not in the 1.4→1.10 breaking set. The `[MessageHandler]` fan-out
handlers already pass `MafValidateFanOut` (0 starvation risks).

> Conclusion: expected residual compiler breakage is **minimal**. The plan still uses a per-step
> build gate so any surprise from a transitive dependency is caught immediately.

---

## 4. Project inventory — every project that references `Microsoft.Agents.AI*`

> ⚠️ This repo is **not** uniformly CPM-managed today. Four sample projects set
> `ManagePackageVersionsCentrally=false` and pin MAF versions **inline**, so the
> `Directory.Packages.props` bump does **not** reach them as-is.
> **This migration consolidates them into Central Package Management (CPM)** — see §4.2 —
> so that, afterwards, `Directory.Packages.props` is the single source of truth for every
> upgradeable project. All projects below ARE part of `AgentEval.sln` (the "not in sln"
> csproj comments are stale).

### Group A — CPM-managed (bumped centrally via `Directory.Packages.props`)

| Project | TFMs | MAF reference | Notes |
|---|---|---|---|
| [src/AgentEval/AgentEval.csproj](src/AgentEval/AgentEval.csproj) | net8/9/10 | direct: `Microsoft.Agents.AI`, `.Workflows` | Umbrella NuGet package |
| [src/AgentEval.MAF/AgentEval.MAF.csproj](src/AgentEval.MAF/AgentEval.MAF.csproj) | net8/9/10 | direct: `Microsoft.Agents.AI`, `.Workflows` | Core MAF integration |
| [src/AgentEval.Cli/AgentEval.Cli.csproj](src/AgentEval.Cli/AgentEval.Cli.csproj) | **net8/10** (no net9) | transitive via `AgentEval.MAF` project ref | ✅ **Canonical home — lives in THIS repo** at `src/AgentEval.Cli`. It is the source for the `AgentEval.Cli` NuGet package; the external `AgentEvalHQ/AgentEval.Cli` repo is being **retired**. Packed as the `agenteval` dotnet tool |
| [samples/AgentEval.Samples/AgentEval.Samples.csproj](samples/AgentEval.Samples/AgentEval.Samples.csproj) | net10 | direct: `Microsoft.Agents.AI.OpenAI`, `.Workflows`, `.Workflows.Generators` | Uses src project refs |
| [tests/AgentEval.Tests/AgentEval.Tests.csproj](tests/AgentEval.Tests/AgentEval.Tests.csproj) | net8/9/10 | transitive via src project refs | Inherits the central bump automatically |
| [tests/AgentEval.Memory.Tests/AgentEval.Memory.Tests.csproj](tests/AgentEval.Memory.Tests/AgentEval.Memory.Tests.csproj) | net8/9/10 | transitive via src project refs | Inherits the central bump automatically |

→ **Single edit** in `Directory.Packages.props` covers all of Group A: bump the 4 `Microsoft.Agents.AI*` packages → `1.10.0`, bump `Microsoft.Extensions.AI*` → `≥ 10.6.0`, and re-validate the `OpenTelemetry.Api` security pin (GHSA-g94r-2vxg-569j).

### Group B — CPM **disabled**, versions pinned **inline** (consolidated into CPM by this migration)

Split by whether they can be upgraded now:

#### B1 — Consolidate into CPM and upgrade **now**

| Project | TFM | Inline MAF pins | Consumes `AgentEval` NuGet? | Action |
|---|---|---|---|---|
| [samples/AgentEval.TravelDemo/AgentEval.TravelDemo.csproj](samples/AgentEval.TravelDemo/AgentEval.TravelDemo.csproj) | net10 | `Microsoft.Agents.AI` 1.3.0, `.Workflows` 1.3.0 | ❌ No (pure MAF) | ✅ Attach to CPM (§4.2) → auto-bumps to 1.10.0 |

#### B2 — Do **NOT** touch yet 🔒 (gated on a republished `AgentEval` package built on 1.10.0)

| Project | TFM | Inline MAF pins | Consumes `AgentEval` NuGet? | Why deferred |
|---|---|---|---|---|
| [samples/AgentEval.NuGetConsumer/AgentEval.NuGetConsumer.csproj](samples/AgentEval.NuGetConsumer/AgentEval.NuGetConsumer.csproj) | net10 | `Microsoft.Agents.AI` 1.3.0, `Extensions.AI` 10.5.0 | ✅ `AgentEval` 0.8.1-beta | **Explicitly left untouched** — external-consumer simulation; bump only after republish (§4.1) |
| [samples/AgentEval.NuGetConsumer.Tests/AgentEval.NuGetConsumer.Tests.csproj](samples/AgentEval.NuGetConsumer.Tests/AgentEval.NuGetConsumer.Tests.csproj) | net10 | transitive via NuGetConsumer | ✅ `AgentEval` 0.10.1-beta | Tests the above — left untouched with it |
| [samples/AgentEval.TravelDemo.Evals/AgentEval.TravelDemo.Evals.csproj](samples/AgentEval.TravelDemo.Evals/AgentEval.TravelDemo.Evals.csproj) | net10 | `Microsoft.Agents.AI` 1.3.0, `.Workflows` 1.3.0 | ✅ `AgentEval` 0.10.1-beta | Same published-package binding constraint (§4.1) |

### 4.1 Published-NuGet sequencing constraint 🔒

The three **B2** projects consume the **published `AgentEval` NuGet package** (0.8.1 / 0.10.1-beta),
which was **built against MAF 1.3.0**. Forcing `Microsoft.Agents.AI` to 1.10.0 in these projects
while they still reference a 1.3.0-built `AgentEval` package risks runtime/binding mismatches and
defeats their purpose (validating the *published* downstream surface).

**Decision (per maintainer):** `AgentEval.NuGetConsumer` and `AgentEval.NuGetConsumer.Tests` are
**left untouched** in this migration — they keep CPM disabled and stay on MAF 1.3.0 (which preserves
their role as a real external-consumer simulation). They are upgraded **only after** a new
`AgentEval` package built on MAF 1.10.0 is published. `AgentEval.TravelDemo.Evals` carries the same
constraint and is deferred alongside them.

`AgentEval.TravelDemo` (B1) is pure MAF (no `AgentEval` dependency) → consolidate + bump in lockstep
with the core.

### 4.2 CPM consolidation (do this as part of the upgrade)

Rather than leaving inline pins to drift, **attach the upgradeable inline projects to Central
Package Management**. For each B1 project (and, later, each B2 project once unblocked):

1. In the csproj, **remove** `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>`.
2. **Strip the `Version="..."` attribute** from every `<PackageReference>` so versions resolve from `Directory.Packages.props`.
3. **Add any missing `<PackageVersion>` entries** to `Directory.Packages.props` for packages those projects use that aren't centrally declared yet — audit list: `Microsoft.Extensions.AI` / `.OpenAI` (already present at 10.x), `System.Memory.Data` (10.0.3), `Azure.AI.OpenAI` (2.8.0-beta.1, present), `OpenTelemetry.Api` (1.15.3, present), and the `AgentEval` package version itself (for B2, when unblocked).
4. `dotnet restore` and confirm no `NU1008` (inline version with CPM enabled) or `NU1605` (downgrade) warnings.

After consolidation, the MAF version lives in **exactly one place** (`Directory.Packages.props`) for
every project except the intentionally-isolated B2 external-consumer simulations (until they too are
folded in post-republish).

> Note: `AgentEval.NuGetConsumer*` may *intentionally* remain CPM-disabled even after republish, to
> keep simulating how a real external consumer pins versions. Decide at that point.

---

## 5. Step-by-step plan

Migrate **one minor at a time** with a build + test gate between each. The maf-doctor regression
plan identifies **1.4.0** and **1.5.0** as the only steps that introduce registry-tracked changes;
1.6–1.10 are additive for this repo.

### Phase 0 — Preparation
- [ ] Create rollback branch: `git switch -c chore/maf-1.10.0-upgrade` (keep `main` clean).
- [ ] Confirm green baseline: `dotnet build` + `dotnet test` on 1.3.0.
- [ ] Re-run `MafDoctor` and save the grade-B baseline for comparison.

### Phase 1 — Bump to 1.4.0 (registry: MAF140-AGENT-001..004)
- [ ] In `Directory.Packages.props`, set the 4 `Microsoft.Agents.AI*` packages → `1.4.0`.
- [ ] `dotnet build`. Run `MafRunCs0618Hunt` on `AgentEval.sln` for compiler ground truth.
- [ ] For any hit, apply the registry fix (`MafApiSafety <symbol>` / `MafRegistryLookup <id>`). None expected (no `AgentSkill` usage).
- [ ] `dotnet test` (×3 TFMs). Gate must be green before proceeding.

### Phase 2 — Bump to 1.5.0 (registry: MAF150-PROVIDER-001/002)
- [ ] Set the 4 MAF packages → `1.5.0`.
- [ ] `dotnet build` + `MafRunCs0618Hunt`. `TodoProvider` not used → expect clean.
- [ ] `dotnet test`. Green gate.

### Phase 3 — Bump core (Group A) to 1.10.0 (additive for this repo)
- [ ] In `Directory.Packages.props`: set the 4 `Microsoft.Agents.AI*` packages → `1.10.0`.
- [ ] Bump `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `Microsoft.Extensions.AI.Evaluation.Quality` → `≥ 10.6.0` (match latest compatible).
- [ ] `dotnet restore` → inspect the transitive tree; re-confirm `OpenTelemetry.Api` pin (1.15.3) still resolves the GHSA-g94r-2vxg-569j advisory. Adjust the pin if MAF 1.10.0 pulls a different transitive baseline.
- [ ] Re-check `Azure.AI.OpenAI 2.8.0-beta.1` compatibility with the new MEAI surface (used by `AgentEval.Samples` factories and `AgentEval.Cli`'s judge).
- [ ] `dotnet build` + `MafRunCs0618Hunt AgentEval.sln`. Resolve any residual `CS0246`/`CS0618` via the registry.
- [ ] Confirm **`AgentEval.Cli`** (net8/net10) builds — it pulls MAF transitively through `AgentEval.MAF`.
- [ ] `dotnet test` (×3 TFMs). Green gate.

### Phase 3b — Consolidate inline-pinned samples into CPM (Group B)
- [ ] **`AgentEval.TravelDemo`** (B1, pure MAF): attach to CPM per §4.2 (remove `ManagePackageVersionsCentrally=false`, strip inline `Version=`); first add any missing `<PackageVersion>` entries to `Directory.Packages.props`. It then resolves MAF 1.10.0 + `Microsoft.Extensions.AI*` ≥ 10.6.0 centrally. `dotnet restore` (expect no `NU1008`/`NU1605`), build, then run `Demo02_TripPlannerWorkflow`.
- [ ] **`AgentEval.NuGetConsumer`, `AgentEval.NuGetConsumer.Tests`** (B2): 🔒 **do NOT touch.** Leave CPM-disabled on MAF 1.3.0 — they validate the published external-consumer surface. Revisit only after the new `AgentEval` package (built on 1.10.0) is published.
- [ ] **`AgentEval.TravelDemo.Evals`** (B2): 🔒 defer with the same constraint (§4.1). When the new `AgentEval` package ships, bump its `AgentEval` ref and consolidate it into CPM per §4.2.

### Phase 4 — Verification & hardening
- [ ] `MafDoctor` → expect grade **B** or better (no new errors/starvation).
- [ ] `MafScanAntiPatterns` and `MafValidateFanOut` → confirm 0 starvation risks on the 3 `[MessageHandler]` methods.
- [ ] `MafSimulateWorkflow` → confirm workflow topology still completes (Mermaid verdict ✅).
- [ ] `MafAutoFixAll --dry-run` → apply only purely-additive/safe rewrites (e.g. `sealed` on executors) if surfaced; review each diff.
- [ ] Run the samples that touch MAF (e.g. TravelDemo `Demo02_TripPlannerWorkflow`) to validate runtime behaviour.
- [ ] Update `CHANGELOG.md` and any `docs/adr/0xx-maf-*.md` with the 1.10.0 bump.

---

## 6. Risk register & rollback

| Risk | Likelihood | Mitigation |
|---|---|---|
| Transitive `Microsoft.Extensions.AI` / OpenTelemetry version conflict | Medium | Per-step `dotnet restore` + dependency-tree review; keep the explicit `OpenTelemetry.Api` security pin |
| `Azure.AI.OpenAI 2.8.0-beta.1` API drift vs new MEAI | Low–Medium | Build the samples/factories; pin remains central in CPM |
| **Group B samples missed** (were CPM disabled, inline pins) | Low | §4.2 CPM-consolidation folds B1 into central management; `NU1008` restore check catches stragglers |
| **B2 consuming a 1.3.0-built `AgentEval` package** | **Medium** | §4.1 — `NuGetConsumer*` left untouched + `TravelDemo.Evals` deferred until a 1.10.0-based `AgentEval` package ships |
| Hidden `CS0246`/`CS0618` not in source scan | Low | `MafRunCs0618Hunt` after every bump is the authoritative gate |
| Workflow fan-in regression | Low | `MafValidateFanOut` + `MafSimulateWorkflow` in Phase 4 |
| `EnforceCodeStyleInBuild` surfaces new analyzer warnings | Low | `TreatWarningsAsErrors=false` → non-fatal; triage separately |

**Rollback:** all changes are confined to `Directory.Packages.props` on a dedicated branch.
Revert the branch (or the single props edit) to return to a known-good 1.3.0 state. No source
rewrites are anticipated, keeping rollback trivial.

---

## 7. maf-doctor command quick-reference

```text
MafDoctor .                                  # health grade + top fixes
MafScoreMigrationRisk .                       # EASY/MEDIUM/HARD pre-flight
MafCompatibility 1.10.0                        # runtime/SDK requirements
MafGenerateRegressionPlan 1.3.0 1.10.0         # ordered per-version roadmap
MafRunCs0618Hunt AgentEval.sln                 # compiler ground-truth gate (run each step)
MafApiSafety <symbol> / MafRegistryLookup <id> # exact fix recipe per finding
MafAutoFixAll --dry-run .                       # preview safe deterministic rewrites
MafValidateFanOut . / MafSimulateWorkflow .     # workflow topology verification
MafScanAntiPatterns . (format: sarif)           # CI/GHAS ingestion
```

---

## Appendix A — maf-doctor toolchain inventory

The maf-autopilot ("MAF Doctor") MCP server is installed and exposes the following (version-aware
via `applies_to_codebases` registry markers).

**Diagnose / score**
- `MafDoctor` — aggregate A/B/C/F health grade + top 3 fixes.
- `MafScanAntiPatterns` — security/concurrency/observability/identity/topology rules (markdown or SARIF).
- `MafScoreMigrationRisk` — EASY/MEDIUM/HARD with weighted breakdown.
- `MafValidateFanOut` — flags silent fan-in starvation (`[MessageHandler]` non-message return types).
- `MafSimulateWorkflow` — static topology proof + Mermaid diagram.
- `MafEstimateCost` — unbounded `RunAsync`/`RunStreamingAsync` (missing `MaxOutputTokens`) audit.
- `MafLintAgentPrompt` — prompt bloat / missing refusals / injection-risk concatenation.
- `MafRunCs0618Hunt` — shells `dotnet build`, joins `CS0618`/`CS0246` to the registry.

**Upgrade / migrate**
- `MafCompatibility` — per-version runtime/SDK requirements.
- `MafMigrationPath` — guide sections for a version range.
- `MafGenerateRegressionPlan` — ordered per-version roadmap + Mermaid.
- `MafPreUpgradeDryRun` — diff impact preview (requires `dotnet-inspect` on PATH).
- `MafDiffPackage` — public-API diff between two NuGet versions.

**Registry / symbol lookup**
- `MafRegistryList` — all obsolete-API registry entry IDs.
- `MafRegistryLookup` — full fix recipe by entry ID.
- `MafApiSafety` — SAFE/UNSAFE verdict for a single symbol.

**Fix / rewrite**
- `MafAutoFixAll` — apply every supported rewriter in dependency-safe order (supports `--dry-run`).
- `MafAutoFix` — apply a single rewriter rule.
- `MafBeforeAfter` — preview rewriter diffs without writing.

**Scaffold / explain / collaborate**
- `MafNewAgent` — `ChatClientAgent` + hermetic xUnit smoke test.
- `MafNewExecutor` — `sealed Executor` with fan-out-safe `[MessageHandler]` + regression test.
- `MafExplain` — line-by-line annotation of a MAF snippet.
- `MafTour` — guided codebase walkthrough.
- `MafEstimateCost`, `MafAuditPullRequest` (PR-scoped scan), `MafDraftIssue` (file a tracked issue).

**Specialist agents:** `@maf-best-practice-reviewer`, `@maf-auditor`, `@maf-migration`, `@maf-incident-responder`.

### What you can do with it
Run a single health grade across the repo, prove workflows won't starve, audit token cost,
lint agent prompts, score upgrade risk before starting, generate an ordered version-by-version
migration roadmap, look up the exact fix for any obsolete/broken MAF symbol, apply tested
deterministic rewrites (with dry-run preview), scaffold correct-by-construction agents/executors,
and scope all of the above to a pull request for CI — all kept current with MAF's per-minor
breaking changes via the curated registry, so it supersedes stale training data.
