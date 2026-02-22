# Analysis: Microsoft Agent Framework (MAF) Integration in AgentEval

**Date:** February 17, 2026 (originally); updated February 2026 for MAF RC1  
**Scope:** Full review of MAF NuGet package usage, abstraction quality, interface isolation, and versioning strategy  
**MAF Version:** See `Directory.Packages.props` for current pinned versions  
**See also:** [ADR-013](../../docs/adr/013-maf-rc1-upgrade.md) for the preview → RC1 upgrade decision and breaking changes

> **Note:** Version numbers cited in this document (e.g., `1.0.0-rc1`, `10.3.0`) reflect the state at time of writing. Always check `Directory.Packages.props` for the current pinned versions.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [MAF NuGet Package Usage](#2-maf-nuget-package-usage)
3. [Code Isolation Analysis](#3-code-isolation-analysis)
4. [Interface Abstraction Quality](#4-interface-abstraction-quality)
5. [MAF Surface Area & Coupling Points](#5-maf-surface-area--coupling-points)
6. [Versioning Strategy Analysis](#6-versioning-strategy-analysis)
7. [Breaking Change Mitigation](#7-breaking-change-mitigation)
8. [Decision: Single Package, Track Latest MAF, Independent Versioning](#8-decision-single-package-track-latest-maf-independent-versioning)
9. [Detailed Plan: Extracting `AgentEval.MAF` into a Separate Package](#9-detailed-plan-extracting-agentevalMaf-into-a-separate-package)

---

## 1. Executive Summary

The current MAF integration is **well-architected at the code level**. All MAF-dependent code is confined to the `src/AgentEval/MAF/` directory (7 files), and all core interfaces (`IEvaluableAgent`, `IStreamableAgent`, `IEvaluationHarness`, `IWorkflowEvaluableAgent`) are framework-agnostic. No MAF types leak into Core, Models, Metrics, or Assertions.

However, there is one **structural concern**: the MAF NuGet packages are referenced at the main library project level (`AgentEval.csproj`), meaning all consumers transitively pull `Microsoft.Agents.AI` and `Microsoft.Agents.AI.Workflows` even if they never use the MAF adapters.

The recommended path forward is a **single-version tracking strategy** (no NuGet multi-targeting for MAF) with a potential future extraction into a separate `AgentEval.MAF` package.

---

## 2. MAF NuGet Package Usage

### 2.1 Package References by Project

| Project | MAF Packages | Purpose |
|---------|-------------|---------|
| `src/AgentEval/AgentEval.csproj` | `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Workflows` | MAF adapter implementations |
| `tests/AgentEval.Tests.csproj` | `Microsoft.Agents.AI.Workflows` | Tests for workflow adapters |
| `samples/AgentEval.Samples.csproj` | `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows` | Sample code demonstrating MAF agent creation |
| `samples/AgentEval.NuGetConsumer.csproj` | `Microsoft.Agents.AI` | NuGet consumer sample |

### 2.2 Version Pinning (Directory.Packages.props)

```xml
<PackageVersion Include="Microsoft.Agents.AI" Version="1.0.0-rc1" />
<PackageVersion Include="Microsoft.Agents.AI.OpenAI" Version="1.0.0-rc1" />
<PackageVersion Include="Microsoft.Agents.AI.Workflows" Version="1.0.0-rc1" />
```

All three packages are centrally managed and locked to the same RC1 version, which is correct for consistency. The version string `1.0.0-rc1` indicates this is a release candidate — the API surface is near-final but breaking changes may still occur before GA.

### 2.3 Microsoft.Extensions.AI (Shared Dependency)

A critical distinction: `Microsoft.Extensions.AI` (version `10.3.0`) provides foundational types used throughout AgentEval **and** by MAF:
- `IChatClient` — used by metrics, evaluators, and `FakeChatClient`
- `ChatMessage`, `TextContent`, `FunctionCallContent`, `FunctionResultContent` — used by `ToolUsageExtractor` and MAF adapters
- `UsageContent` — used for token tracking

This is a **shared dependency**, not a MAF-specific one. The `ToolUsageExtractor` in `Core/` uses `FunctionCallContent` and `FunctionResultContent` from `Microsoft.Extensions.AI`, not from MAF.

---

## 3. Code Isolation Analysis

### 3.1 MAF Directory Contents (7 files in `src/AgentEval/MAF/`)

| File | LOC | MAF Types Used | Purpose |
|------|-----|---------------|---------|
| `MAFAgentAdapter.cs` | 122 | `AIAgent`, `AgentSession`, `AgentResponse` | Adapts single MAF agent → `IStreamableAgent` |
| `MAFIdentifiableAgentAdapter.cs` | 120 | `AIAgent`, `AgentSession`, `AgentResponse` | Same + `IModelIdentifiable` for comparison |
| `MAFEvaluationHarness.cs` | 630 | None (only Core types) | Full evaluation harness implementing `IStreamingEvaluationHarness` |
| `MAFWorkflowAdapter.cs` | 400 | `Workflow` (via static factory `FromMAFWorkflow`) | Adapts MAF Workflow → `IWorkflowEvaluableAgent` |
| `MAFWorkflowEventBridge.cs` | 280 | `Workflow`, `StreamingRun`, `InProcessExecution`, `WorkflowEvent` subclasses, `TurnToken`, etc. | Bridges MAF events → AgentEval events |
| `MAFGraphExtractor.cs` | 172 | `Workflow`, `EdgeKind`, `EdgeInfo`, `DirectEdgeInfo` (Checkpointing) | Extracts graph from MAF Workflow |
| `WorkflowEvaluationHarness.cs` | 340 | None (only Core types) | Workflow test harness using `IWorkflowEvaluableAgent` |

### 3.2 Leakage Assessment

| Layer | MAF Type Leakage | Verdict |
|-------|-----------------|---------|
| `src/AgentEval/Core/` | **None** | ✅ Clean |
| `src/AgentEval/Models/` | **None** | ✅ Clean |
| `src/AgentEval/Metrics/` | **None** | ✅ Clean |
| `src/AgentEval/Assertions/` | **None** | ✅ Clean |
| `src/AgentEval/Comparison/` | **Comment-only** (XML doc example mentions `MAFAgentAdapter`) | ✅ Clean |
| `src/AgentEval/Tracing/` | **Comment-only** (XML doc example mentions `MafWorkflowAdapter`) | ✅ Clean |
| `src/AgentEval/DependencyInjection/` | **None** | ✅ Clean |
| `tests/AgentEval.Tests/MAF/` | **Expected** (9 MAF test files, properly contained) | ✅ Clean |
| `samples/` | **Expected** (samples create `AIAgent` instances directly) | ✅ Expected |

**Conclusion:** MAF types are properly isolated at the code level. No `using Microsoft.Agents.AI` directive exists outside `src/AgentEval/MAF/` in the library source.

### 3.3 Structural Concern: Project-Level Package Reference

The main `AgentEval.csproj` includes:
```xml
<PackageReference Include="Microsoft.Agents.AI" />
<PackageReference Include="Microsoft.Agents.AI.Workflows" />
```

This means **any consumer** of the AgentEval NuGet package transitively pulls `Microsoft.Agents.AI` and its dependency chain, even if they:
- Only use metrics with `IChatClient`
- Only use fluent assertions with `FakeChatClient`
- Target a non-MAF agent framework

This is the **primary architectural gap** in the current design.

---

## 4. Interface Abstraction Quality

### 4.1 Core Interfaces (Framework-Agnostic)

The abstraction layer is well-designed and follows Interface Segregation Principle:

```
IEvaluableAgent                    ← Minimal: Name + InvokeAsync(prompt)
├── IStreamableAgent              ← Adds: InvokeStreamingAsync(prompt)
└── IWorkflowEvaluableAgent       ← Adds: ExecuteWorkflowAsync, ExecutorIds, WorkflowType

IEvaluationHarness                ← RunEvaluationAsync(agent, testCase)
└── IStreamingEvaluationHarness   ← Adds: RunEvaluationStreamingAsync(...)

IModelIdentifiable                ← ModelId + ModelDisplayName (for comparison)
```

These interfaces are in `AgentEval.Core` and `AgentEval.Comparison`, with **zero references to MAF types**. Any agent framework can be adapted by implementing these interfaces.

### 4.2 Adapter Pattern

The MAF adapters correctly implement the Adapter pattern:

- `MAFAgentAdapter` : `IStreamableAgent` — wraps `AIAgent`, translates `AgentResponse` fields → `AgentEval.AgentResponse`
- `MAFIdentifiableAgentAdapter` : `IStreamableAgent, IModelIdentifiable` — wraps `AIAgent` + model identity
- `MAFWorkflowAdapter` : `IWorkflowEvaluableAgent` — wraps `Workflow` via event bridging

### 4.3 Event Translation Layer

The `MAFWorkflowEventBridge` and `MAFWorkflowAdapter` demonstrate a well-designed translation pattern:

```
MAF Events (class hierarchy)        AgentEval Events (records)
─────────────────────────────       ──────────────────────────
ExecutorInvokedEvent           →    (state tracking)
AgentResponseUpdateEvent       →    ExecutorOutputEvent, ExecutorToolCallEvent
ExecutorCompletedEvent         →    ExecutorOutputEvent
ExecutorFailedEvent            →    ExecutorErrorEvent
WorkflowOutputEvent            →    WorkflowCompleteEvent
```

AgentEval's event types (`WorkflowEvent`, `ExecutorOutputEvent`, etc.) are defined as records in `MAFWorkflowAdapter.cs`. These are AgentEval-owned types, not MAF types — but they're defined in the `AgentEval.MAF` namespace. They are used by `IWorkflowEvaluableAgent.ExecuteWorkflowAsync()` which returns `WorkflowExecutionResult` in `AgentEval.Models`.

**Note:** The `WorkflowEvent` record hierarchy is consumed by `MAFWorkflowAdapter` but defined in the same file. Since `IWorkflowEvaluableAgent` (in Core) doesn't reference these events directly (it returns `WorkflowExecutionResult`), this is acceptable.

### 4.4 Harness Abstraction

`MAFEvaluationHarness` implements `IStreamingEvaluationHarness` and `WorkflowEvaluationHarness` is a concrete class. Notably:

- `MAFEvaluationHarness` — Despite its name, this class has **zero direct MAF dependencies**. It works entirely through `IEvaluableAgent` and `IStreamableAgent`. It could be renamed to remove the MAF prefix.
- `WorkflowEvaluationHarness` — Also has **zero direct MAF dependencies**. Works through `IWorkflowEvaluableAgent`.

Both harnesses are in the `AgentEval.MAF` namespace but don't use MAF types — they could be moved to `AgentEval.Core` without any code changes.

---

## 5. MAF Surface Area & Coupling Points

### 5.1 MAF API Surface Used

These are the specific MAF types and methods the codebase depends on:

**From `Microsoft.Agents.AI`:**
| Type/Member | Used In | Breaking Change Risk |
|------------|---------|---------------------|
| `AIAgent` (class) | `MAFAgentAdapter`, `MAFIdentifiableAgentAdapter` | Medium — constructor/factory changes |
| `AIAgent.Name` | Adapters | Low |
| `AIAgent.RunAsync()` | Adapters | High — signature/return type changes |
| `AIAgent.RunStreamingAsync()` | Adapters | High — signature/return type changes |
| `AIAgent.CreateSessionAsync()` | Adapters | Medium |
| `AgentSession` | Adapters | Low — used as opaque handle |
| `AgentResponse.Text` | Adapters | Medium |
| `AgentResponse.Messages` | Adapters | Medium |
| `AgentResponse.Usage` | Adapters | Medium |

**From `Microsoft.Agents.AI.Workflows`:**
| Type/Member | Used In | Breaking Change Risk |
|------------|---------|---------------------|
| `Workflow` | EventBridge, GraphExtractor, WorkflowAdapter | High |
| `Workflow.ReflectEdges()` | GraphExtractor | High — internal API surface |
| `Workflow.StartExecutorId` | GraphExtractor | Medium |
| `Workflow.DescribeProtocolAsync()` | EventBridge | Medium |
| `InProcessExecution.RunStreamingAsync()` | EventBridge | High |
| `StreamingRun.WatchStreamAsync()` | EventBridge | High |
| `StreamingRun.TrySendMessageAsync()` | EventBridge | High |
| `TurnToken` | EventBridge | Medium |
| `WorkflowEvent` hierarchy (6+ subtypes) | EventBridge | High — event shape changes |
| `EdgeKind`, `EdgeInfo`, `DirectEdgeInfo` | GraphExtractor | High |
| `ChatProtocolExtensions.IsChatProtocol()` | EventBridge | Medium |

**From `Microsoft.Agents.AI.Workflows.Checkpointing`:**
| Type/Member | Used In | Breaking Change Risk |
|------------|---------|---------------------|
| `EdgeInfo.Kind`, `.Connection` | GraphExtractor | High |
| `DirectEdgeInfo.HasCondition` | GraphExtractor | Medium |
| `.Connection.SinkIds`, `.SourceIds` | GraphExtractor | High |

### 5.2 High-Risk Coupling Points

1. **`MAFWorkflowEventBridge`** — This is the most tightly coupled file. It depends on 10+ MAF workflow types and their specific event hierarchy. Any restructuring of MAF's event model would require significant changes here.

2. **`MAFGraphExtractor`** — Depends on `Workflow.ReflectEdges()` and `Checkpointing.EdgeInfo` internals. The `Checkpointing` namespace suggests this may be a less-stable API.

3. **`MAFAgentAdapter.InvokeStreamingAsync()`** — Depends on the streaming content model (`update.Contents` → pattern match on `TextContent`, `FunctionCallContent`, etc.). These types are from `Microsoft.Extensions.AI`, which is more stable.

---

## 6. Versioning Strategy Analysis

### 6.1 Option A: NuGet Multi-Targeting for Different MAF Versions

**How it would work:**
```xml
<!-- AgentEval.csproj -->
<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>

<!-- Conditional MAF references -->
<ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
    <PackageReference Include="Microsoft.Agents.AI" Version="1.0.0-rc1" />
</ItemGroup>
<ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="Microsoft.Agents.AI" Version="2.0.0" />
</ItemGroup>
```

With `#if` preprocessor directives for API differences:
```csharp
#if NET8_0
    var response = await _agent.RunAsync(prompt, thread);
#elif NET10_0
    var response = await _agent.RunAsync(new RunOptions(prompt), thread);
#endif
```

**Pros:**
- Single NuGet package supports multiple MAF versions
- Consumers on older frameworks get matching MAF version

**Cons:**
- **TFM and MAF version are orthogonal** — MAF version ≠ .NET TFM. A consumer on `net10.0` might want MAF 1.x. This approach conflates two independent axes.
- Preprocessor directives create hard-to-maintain code paths
- Each `#if` branch needs separate testing
- TFM multi-targeting is for .NET runtime differences, not NuGet dependency versioning
- Exponential complexity: 3 TFMs × N MAF versions = 3N build configurations

**Verdict: Not recommended.** The TFM dimension is wrong for expressing MAF version compatibility. TFM multi-targeting should remain for .NET runtime differences (which it already does: net8.0/net9.0/net10.0).

### 6.2 Option B: Separate NuGet Packages per MAF Version

**How it would work:**
```
AgentEval                    ← Core library, no MAF dependency
AgentEval.MAF.v1             ← MAF 1.0.0-rc1 adapters
AgentEval.MAF.v2             ← MAF 2.0.0 adapters (future)
```

**Pros:**
- Clean separation — core consumers don't pull MAF
- Each MAF version package has its own adapter code
- Consumers pick the MAF version they need

**Cons:**
- Multiple packages to maintain
- Combinatorial testing (each MAF package × 3 TFMs)
- Solution-level complexity grows
- Two MAF packages can't coexist in the same project

**Verdict: Over-engineering for current state.** MAF is in RC (release candidate). Maintaining separate packages for pre-GA versions adds cost with no benefit.

### 6.3 Option C: Single Package, Track Latest MAF (Recommended)

**How it would work:**
- AgentEval tracks the latest MAF release (RC or stable)
- Each AgentEval release states which MAF version it supports
- When MAF makes breaking changes, AgentEval releases a new version
- Optionally extract MAF code to `AgentEval.MAF` package later

**Pros:**
- Simple — one codebase, one version matrix
- Matches MAF's own release cadence (they're also pre-release)
- Minimizes maintenance overhead
- Adapter pattern already isolates MAF — breaking changes are contained

**Cons:**
- Consumers on older MAF versions must stay on older AgentEval versions
- No simultaneous support for multiple MAF versions

**Verdict: Best fit for current project stage and MAF maturity.**

### 6.4 Option D: Extract `AgentEval.MAF` Package (Future Enhancement)

**How it would work (when MAF reaches 1.0 stable):**
```
AgentEval                    ← Core library: interfaces, metrics, assertions, tracing
                                No Microsoft.Agents.AI dependency
AgentEval.MAF                ← MAF adapters: MAFAgentAdapter, MAFWorkflowAdapter, etc.
                                References AgentEval + Microsoft.Agents.AI
```

**Extraction steps:**
1. Move `src/AgentEval/MAF/*.cs` → new `src/AgentEval.MAF/` project
2. Move `tests/AgentEval.Tests/MAF/` → new `tests/AgentEval.MAF.Tests/`
3. Remove `Microsoft.Agents.AI` and `Microsoft.Agents.AI.Workflows` from `AgentEval.csproj`
4. Add project reference from `AgentEval.MAF` → `AgentEval`
5. Move `MAFEvaluationHarness` and `WorkflowEvaluationHarness` to Core (they have no MAF deps)

**Impact assessment:**
- `MAFEvaluationHarness` — No code changes needed (no MAF imports). Just move + rename namespace.
- `WorkflowEvaluationHarness` — Same as above. No MAF imports.
- `WorkflowEvent` records — Currently in `MAFWorkflowAdapter.cs`. Would move to `AgentEval.MAF` or to `AgentEval.Models` (they're framework-agnostic records).
- Core consumers get a smaller dependency footprint.
- MAF consumers add one extra package reference.

**Verdict: The right move once MAF stabilizes.** Defer until MAF reaches 1.0 or AgentEval reaches GA.

---

## 7. Breaking Change Mitigation

### 7.1 Current Protection

The codebase already has strong protection against MAF breaking changes:

1. **Adapter Pattern** — MAF types are wrapped behind `IEvaluableAgent`/`IStreamableAgent`/`IWorkflowEvaluableAgent`. Breaking changes in MAF are absorbed by the adapter layer.
2. **Contained Impact** — Only 4 out of 7 MAF files actually import MAF types. The harnesses are framework-agnostic.
3. **Event Translation** — `MAFWorkflowEventBridge` translates MAF's event hierarchy to AgentEval's own records. MAF event changes affect only the bridge.

### 7.2 Automated Version Monitoring — How It Works

#### What is Dependabot?

**Dependabot** is a GitHub-native service (built into every GitHub repository — no installation needed) that automatically monitors your NuGet package dependencies for new versions. It works by:

1. **Scanning** — Dependabot reads your `Directory.Packages.props` (or `.csproj` files) daily (configurable) to find all package references and their pinned versions.
2. **Checking NuGet feeds** — It queries `nuget.org` (or configured private feeds) to see if newer versions of those packages exist.
3. **Creating PRs** — When it finds a newer version of `Microsoft.Agents.AI` (e.g., `1.0.0-rc2` or `1.0.0`), it automatically creates a pull request that bumps the version in `Directory.Packages.props` from the old pinned version to the new one.
4. **CI runs on the PR** — Your GitHub Actions workflow runs `dotnet restore`, `dotnet build`, and `dotnet test` on the PR. If everything passes, the new MAF version is API-compatible. If it fails, there's a breaking change.

**Configuration** (add to `.github/dependabot.yml`):
```yaml
version: 2
updates:
  - package-ecosystem: "nuget"
    directory: "/"
    schedule:
      interval: "weekly"         # Check weekly for new MAF releases
    allow:
      - dependency-name: "Microsoft.Agents.*"   # Only monitor MAF packages
      - dependency-name: "Microsoft.Extensions.AI*"
    commit-message:
      prefix: "deps"
    labels:
      - "dependencies"
      - "maf-update"
```

#### What is Renovate?

**Renovate** is an open-source alternative to Dependabot (by Mend, formerly WhiteSource). It does the same thing — monitors dependencies and auto-creates PRs — but with more advanced features:
- Groups related packages into a single PR (all `Microsoft.Agents.*` bumped together)
- Supports auto-merge when CI passes
- Works with GitHub, GitLab, Bitbucket, Azure DevOps

For a GitHub-hosted repo, **Dependabot is the simpler choice** since it requires zero installation.

#### How does it detect API breaks?

The detection is simple: **if the code doesn't compile, the API broke.** The flow is:

```
1. Dependabot updates Directory.Packages.props:
     Microsoft.Agents.AI: 1.0.0-rc1 → 1.0.0-rc2

2. CI pipeline runs on the PR:
     dotnet restore --force     ← Forces re-download of new package version
     dotnet build               ← Compiles against new MAF API
     dotnet test                ← Runs all tests including MAF integration tests

3. Outcomes:
     ✅ Build passes + Tests pass  → API compatible → merge the PR
     ❌ Build fails                → Compile error → breaking API change in MAF
     ⚠️ Build passes + Tests fail  → Behavioral change (API same, behavior different)
```

**Why `dotnet restore --force`?** Normally, `dotnet restore` uses cached packages. The `--force` flag ensures it re-downloads from NuGet even if a cached version exists, guaranteeing you test against the exact new version.

**Why are we "pinned" but still updating?** Being "pinned" means we don't use floating version ranges like `1.0.0-*` (which would silently pull new versions). Instead, we pin to an exact version (`1.0.0-rc1`) and only update via an **explicit PR** that we review and test. The pin is the default; the update is deliberate.

#### CI Integration

```yaml
# .github/workflows/maf-update-check.yml (optional: proactive check)
- name: Check for MAF updates
  run: |
    dotnet outdated --include Microsoft.Agents
```

#### Version Compatibility Documentation

Maintain a one-line compatibility note in each release's notes (see Section 8.3):
```
AgentEval 0.2.1-beta → Compatible with MAF 1.0.0-rc1
AgentEval 0.3.0-beta → Compatible with MAF 1.0.0-rc2 (future)
AgentEval 1.0.0      → Compatible with MAF 1.0.0
```

### 7.3 Handling Specific Breaking Change Scenarios

| Scenario | Files Affected | Mitigation |
|----------|---------------|------------|
| `AIAgent.RunAsync()` signature change | `MAFAgentAdapter`, `MAFIdentifiableAgentAdapter` | Update adapter, same `IEvaluableAgent` contract |
| `AgentResponse` property renames | `MAFAgentAdapter` | Map new property names in adapter |
| Workflow event hierarchy restructuring | `MAFWorkflowEventBridge` | Rewrite bridge, `WorkflowEvent` records unchanged |
| `ReflectEdges()` removed or changed | `MAFGraphExtractor` | Adapt to new reflection API or use alternative |
| `InProcessExecution` API change | `MAFWorkflowEventBridge` | Update execution method, event translation unchanged |
| `EdgeKind`/`EdgeInfo` restructuring | `MAFGraphExtractor` | Adapt `TranslateEdge()` method |
| `Microsoft.Extensions.AI` breaking change | Broad (affects `ToolUsageExtractor`, adapters) | Higher impact — affects core library too |

### 7.4 Testing Strategy for MAF Updates

#### Current Test Coverage (9 files in `tests/AgentEval.Tests/MAF/`)

The tests split into two clear categories based on whether they instantiate real MAF types:

**Tests that USE real MAF types** (import `Microsoft.Agents.AI.Workflows`):
| Test File | MAF Types Used | What It Tests |
|-----------|---------------|---------------|
| `MAFWorkflowEventBridgeTests` | `WorkflowBuilder`, `ExecutorBindingExtensions`, `Workflow` | Event translation from real MAF workflows |
| `MAFGraphExtractorTests` | `WorkflowBuilder`, `ExecutorBindingExtensions`, `Workflow` | Graph extraction from real MAF workflows |
| `MAFWorkflowAdapterFromMAFWorkflowTests` | `WorkflowBuilder`, `ExecutorBindingExtensions`, `Workflow` | `FromMAFWorkflow()` factory wiring |

**Tests that do NOT use real MAF types** (pure AgentEval types only):
| Test File | What It Tests |
|-----------|---------------|
| `MAFEvaluationHarnessTests` | Harness workflows via `IEvaluableAgent` |
| `MAFWorkflowAdapterTests` | Adapter event processing via `FromSteps()` factory |
| `MAFWorkflowAdapterEdgeTests` | Edge traversal via custom event generators |
| `WorkflowEvaluationHarnessTests` | Workflow test execution via `IWorkflowEvaluableAgent` |
| `WorkflowToolTrackingTests` | Tool call tracking, assertions, computed properties |
| `MicrosoftEvaluatorAdapterTests` | Evaluator adapter factory methods |

#### Tagging Strategy: `[Trait("Category", "MAFIntegration")]`

Tag the 3 test classes that instantiate real MAF types with `[Trait("Category", "MAFIntegration")]` at the **class level**:

```csharp
// In MAFWorkflowEventBridgeTests.cs, MAFGraphExtractorTests.cs, MAFWorkflowAdapterFromMAFWorkflowTests.cs:
[Trait("Category", "MAFIntegration")]
public class MAFWorkflowEventBridgeTests
{
    // ... existing tests unchanged
}
```

**Running tagged tests selectively:**

```powershell
# Run ONLY MAF integration tests (during MAF version upgrade):
dotnet test --filter "Category=MAFIntegration"

# Run everything EXCEPT MAF integration tests (quick feedback loop):
dotnet test --filter "Category!=MAFIntegration"

# Run all tests (normal CI):
dotnet test
```

**Why class-level, not per-test?** All tests in these 3 classes use real MAF types — every test in the class exercises MAF API surface. Class-level `[Trait]` avoids decorating 20+ individual tests.

#### Missing Test Coverage — What to Add

The current suite has a gap: **no tests for `MAFAgentAdapter` or `MAFIdentifiableAgentAdapter`**. These are the adapters that wrap `AIAgent` (the single-agent scenario), and they use `AIAgent.RunAsync()`, `RunStreamingAsync()`, `CreateSessionAsync()`, and `AgentResponse`.

Recommended additions:

| Test File to Create | What to Test | MAF Types Needed |
|---------------------|-------------|------------------|
| `MAFAgentAdapterTests.cs` | `InvokeAsync` returns correct `AgentResponse`, token usage extraction, `ResetSession()`, `CreateSessionAsync()`, `Name` delegation | Requires mocking `AIAgent` or using a test double |
| `MAFIdentifiableAgentAdapterTests.cs` | Same as above + `ModelId`/`ModelDisplayName` from `IModelIdentifiable` | Same |

**Challenge:** `AIAgent` may not be easily mockable (it's a concrete class, not an interface). Options:
1. If MAF provides a test harness or virtual methods → use those
2. If not → test through integration tests with `CreateFuncBinding` (similar to existing workflow tests)
3. Alternatively, test at the `IStreamableAgent` contract level with a simple wrapper

Additional coverage to consider:

| Area | Current Coverage | Gap |
|------|-----------------|-----|
| `MAFAgentAdapter.InvokeAsync()` | None | Need basic invocation test |
| `MAFAgentAdapter.InvokeStreamingAsync()` | None | Need streaming content type handling |
| Token usage extraction (`AgentResponse.Usage`) | None | Need test with/without usage data |
| Error handling in adapters | Partial (workflow only) | Need adapter-level error scenarios |
| `MAFWorkflowEventBridge` with complex workflows | Basic (single/sequential) | Could add parallel, conditional, error-during-stream |

---

## 8. Decision: Single Package, Track Latest MAF, Independent Versioning

### 8.1 The Chosen Strategy

**Option C — Single Package, Track Latest MAF** is the chosen path.

AgentEval ships as a single NuGet package (`AgentEval`) that includes the MAF adapters and tracks the latest MAF version. No package split. **AgentEval uses independent versioning** — its version number reflects its own release cadence, not MAF's.

### 8.2 Why Not Split into `AgentEval` + `AgentEval.MAF`?

The extraction detailed in Section 9 is technically clean — the code isolation already supports it. But it adds **real complexity for a theoretical benefit:**

| Extraction Cost | Reality Check |
|----------------|---------------|
| Two packages to publish, version, and test | Doubles the release surface area |
| Lockstep versioning between `AgentEval` and `AgentEval.MAF` | If they always release together anyway, they're one package pretending to be two |
| Consumers must remember to add both references | Extra friction for every new user; guaranteed support questions |
| Independent release cadence sounds good in theory | In practice, MAF adapter changes usually accompany metrics/harness changes — they co-evolve |
| Version range dependency (`[1.0.0, 2.0.0)`) | Introduces "diamond dependency" risks; consumer hits version conflict at worst time |
| "Opens adapter ecosystem" for SemanticKernel, AutoGen | Speculative — no .NET competing framework exists at meaningful scale today |
| CI/CD pipeline complexity | Two pack targets, two NuGet pushes, two release notes, two changelogs |

**The fundamental insight:** If there's only one .NET agentic framework that matters (MAF), a second package doesn't simplify — it complicates. The adapter code is 4 files. The transitive dependency cost of `Microsoft.Agents.AI` is trivial compared to what any real AI application already pulls (Azure.AI.OpenAI, Microsoft.Extensions.AI, etc.).

### 8.3 Versioning Strategy: Independent Versioning + Compatibility Note

AgentEval follows **independent SemVer versioning**. Each release includes a one-line compatibility note stating which MAF version was tested against.

**Why NOT version-match with MAF:**

| Problem | Example |
|---------|---------|
| **Independent evolution** | AgentEval adds 5 new metrics — deserves a bump. MAF didn't change. Version-matching would either skip AgentEval versions or assign meaningless MAF versions. |
| **MAF patches** | MAF ships a patch `1.0.1` with zero API changes. AgentEval must release too, with no actual changes, just to keep numbers aligned. |
| **Pre-release strings** | MAF uses `1.0.0-rc1` (release candidate). If MAF used date-encoded previews, AgentEval couldn't mirror that format without losing its own SemVer meaning. |
| **SemVer meaning loss** | If AgentEval is `2.3.0` because MAF is `2.3.0`, does a consumer know if AgentEval had breaking changes? Only if they check MAF's changelog — defeating the purpose. |
| **Precedent** | Entity Framework Core initially matched .NET versions, then stopped (EF Core 7 → 8 → 9 don't match .NET versions). The coupling was unsustainable. |

**What we do instead:**

Each AgentEval release note includes one line:

```
AgentEval 0.2.1-beta — Compatible with MAF 1.0.0-rc1
AgentEval 0.3.0-beta — Compatible with MAF 1.0.0-rc2 (future)
AgentEval 1.0.0      — Compatible with MAF 1.0.0
AgentEval 1.1.0      — Compatible with MAF 1.0.0 (no MAF changes)
AgentEval 1.2.0      — Compatible with MAF 1.1.0
```

The `Directory.Packages.props` file is the **source of truth** for which MAF version AgentEval targets. The compatibility note in release notes is human-readable documentation of that fact.

### 8.4 How to Stay Current with MAF Releases

**Automated monitoring** (explained in detail in Section 7.2):
1. **Dependabot** — Auto-creates PRs when `Microsoft.Agents.AI.*` packages publish new versions on NuGet.
2. **CI verification** — The PR triggers `dotnet restore --force && dotnet build && dotnet test` to detect API breaks immediately.
3. **Pin exact versions** — Already done via `Directory.Packages.props`. Never use floating version ranges for MAF.

**Update procedure when a new MAF release ships:**
1. Dependabot creates PR bumping versions in `Directory.Packages.props`
2. CI builds — if it passes, the API is compatible → merge
3. CI fails → breaking change detected:
   - Identify which adapter files need updating (usually 2-4 files in `src/AgentEval/MAF/`)
   - Update adapters to match new MAF API
   - Core interfaces (`IEvaluableAgent`, etc.) remain unchanged
   - Bump AgentEval version per SemVer (minor if adapters changed, major if public API changed)
4. Release includes one-line compatibility note: "Compatible with MAF X.Y.Z"

**Pre-update source diff (recommended for major MAF changes):**

Before updating the NuGet package, the MAF source can be diffed to produce a proactive migration plan:

1. Current MAF source lives in `/MAF/` (gitignored, matches the version in `Directory.Packages.props`)
2. Copy the new MAF source to `/MAFvnext/` (also gitignored)
3. An agent follows `.github/instructions/maf-upgrade-preparation.instructions.md` to:
   - Diff only the ~20 MAF files containing types AgentEval depends on (not all 80+ files)
   - Categorize changes: breaking / behavioral / additive / irrelevant
   - Map each change to the specific AgentEval adapter file(s) affected
   - Produce a concrete adjustment plan with code snippets
4. Review and approve the plan, then update `Directory.Packages.props` and apply the changes

This approach catches **behavioral changes** (same API signature, different semantics) and **new capabilities** that a simple build pass/fail cannot detect. It's especially valuable for major MAF version bumps where many APIs change simultaneously.

### 8.5 Handling MAF Breaking Changes

The adapter pattern already provides the right isolation. When MAF breaks its API:

| What Changes in MAF | What Changes in AgentEval | What Stays the Same |
|---------------------|---------------------------|---------------------|
| `AIAgent.RunAsync()` signature | `MAFAgentAdapter.InvokeAsync()` internally | `IEvaluableAgent.InvokeAsync()` contract |
| Workflow event hierarchy | `MAFWorkflowEventBridge` translation | `WorkflowEvent` records, `WorkflowExecutionResult` |
| `ReflectEdges()` API | `MAFGraphExtractor.ExtractGraph()` | `WorkflowGraphSnapshot` model |
| Streaming content model | `MAFAgentAdapter.InvokeStreamingAsync()` | `IStreamableAgent` contract, `AgentResponseChunk` |

**Key fact:** Every MAF breaking change is absorbed by 2-4 files in `src/AgentEval/MAF/`. The 1,000+ tests across Core, Metrics, Assertions, and Models don't need to change. The adapter layer works.

See `.github/instructions/maf-updates.instructions.md` for agent-readable step-by-step instructions on detecting and adapting to MAF breaking changes.

### 8.6 What About Non-MAF Consumers?

If a consumer wants to use AgentEval with a non-MAF agent:

1. They implement `IEvaluableAgent` (3 members: `Name`, `InvokeAsync`) — trivial
2. They get the full evaluation toolkit: metrics, assertions, stochastic runner, tracing
3. They pull `Microsoft.Agents.AI` transitively — a minor overhead in any AI application context

If this becomes a real problem (someone complains), **then** extract. Not before.

### 8.7 When to Reconsider the Package Split

Revisit the `AgentEval.MAF` extraction (documented in Section 9) **only if:**

1. **A competing .NET agent framework reaches meaningful adoption** — e.g., Semantic Kernel ships an Agent abstraction that is widely used and incompatible with MAF. Today this doesn't exist.
2. **Consumer reports a real version conflict** — The MAF transitive dependency causes a diamond dependency problem in a real-world scenario.
3. **Microsoft decouples MAF from .NET release cadence** — If MAF starts shipping independently (like Entity Framework did), the release cadence divergence becomes harder to track.

Until one of these triggers fires, the single package with independent versioning is the right choice.

### 8.8 Concrete Actions (Now)

1. **Keep MAF code in `src/AgentEval/MAF/`** — The isolation at the directory level is excellent. Don't move files.
2. **Keep the single `AgentEval.csproj`** — Don't create `AgentEval.MAF.csproj`.
3. **Add Dependabot for MAF packages** — Automated PRs when MAF updates (see Section 7.2 for details).
4. **Include one-line compatibility note in release notes** — e.g., "Compatible with MAF 1.0.0-rc1".
5. **Tag MAF-dependent tests** — Add `[Trait("Category", "MAFIntegration")]` to the 3 test classes that instantiate real MAF types (see Section 7.4 for details). Run with `dotnet test --filter "Category=MAFIntegration"` during MAF upgrades.
6. **Add `MAFAgentAdapterTests`** — Currently no tests for `MAFAgentAdapter` or `MAFIdentifiableAgentAdapter` (see Section 7.4 gap analysis).
7. **Create agent instructions for MAF updates** — `.github/instructions/maf-updates.instructions.md` with step-by-step procedures for detecting and adapting to MAF breaking changes.
8. **Optionally rename harnesses** — `MAFEvaluationHarness` and `WorkflowEvaluationHarness` have zero MAF dependencies. Renaming to `DefaultEvaluationHarness` / `DefaultWorkflowEvaluationHarness` improves clarity, but is low priority.

### What NOT to Do

- **Don't match AgentEval versions to MAF versions** — Independent SemVer with a one-line compatibility note is clearer and more sustainable.
- **Don't split packages prematurely** — Complexity with no current consumer demand.
- **Don't multi-target MAF versions via TFM** — TFMs are for .NET runtime versions, not dependency versions.
- **Don't maintain parallel MAF version branches** — Git branch per MAF version creates maintenance hell.
- **Don't abstract MAF behind another interface layer** — The adapter pattern is already the right abstraction.
- **Don't vendor MAF source code** — The `MAF/` directory at repo root is reference documentation, not vendored code.

---

## Appendix: File-Level Dependency Map

```
src/AgentEval/
├── Core/
│   ├── IEvaluableAgent.cs          ← No MAF deps (Microsoft.Extensions.AI only)
│   ├── IEvaluationHarness.cs       ← No MAF deps
│   ├── IWorkflowEvaluableAgent.cs  ← No MAF deps
│   ├── ToolUsageExtractor.cs       ← No MAF deps (Microsoft.Extensions.AI only)
│   └── ...
├── Models/                         ← No MAF deps
├── Metrics/                        ← No MAF deps
├── Assertions/                     ← No MAF deps
├── Comparison/
│   └── IModelIdentifiable.cs       ← No MAF deps
├── MAF/
│   ├── MAFAgentAdapter.cs          ← Microsoft.Agents.AI (AIAgent, AgentSession)
│   ├── MAFIdentifiableAgentAdapter.cs ← Microsoft.Agents.AI (AIAgent, AgentSession)
│   ├── MAFEvaluationHarness.cs     ← NO MAF deps (AgentEval.Core only)
│   ├── MAFWorkflowAdapter.cs       ← Microsoft.Agents.AI.Workflows (Workflow, via factory)
│   ├── MAFWorkflowEventBridge.cs   ← Microsoft.Agents.AI.Workflows (heavy usage)
│   ├── MAFGraphExtractor.cs        ← Microsoft.Agents.AI.Workflows + Checkpointing
│   └── WorkflowEvaluationHarness.cs ← NO MAF deps (AgentEval.Core only)
└── AgentEval.csproj                ← Package refs: Microsoft.Agents.AI, .AI.Workflows
```

**Files with actual MAF compile-time dependency: 4 out of 7** in the MAF directory.  
**Files with zero MAF dependency: 3 out of 7** (harnesses + could be in Core).

---

## 9. Detailed Plan: Extracting `AgentEval.MAF` into a Separate Package

### 9.1 Why Extract?

Today, `AgentEval.csproj` has top-level `<PackageReference>` entries for `Microsoft.Agents.AI` and `Microsoft.Agents.AI.Workflows`. This means **every consumer** of the AgentEval NuGet package transitively pulls the entire MAF dependency tree, even if they:

- Only use `FaithfulnessMetric` with a plain `IChatClient`
- Only use `ToolUsageAssertions` against pre-recorded `ToolUsageReport` data
- Only use `FakeChatClient` for offline testing
- Want to integrate a non-MAF framework (Semantic Kernel, AutoGen, custom)

Extraction creates two NuGet packages:

| Package | Contains | Dependencies |
|---------|----------|-------------|
| `AgentEval` | Core interfaces, metrics, assertions, models, tracing, DI, exporters | `Microsoft.Extensions.AI` (no MAF) |
| `AgentEval.MAF` | MAF adapters, event bridge, graph extractor | `AgentEval` + `Microsoft.Agents.AI` + `Microsoft.Agents.AI.Workflows` |

Consumers who use MAF add both packages. Consumers who don't use MAF add only `AgentEval`.

### 9.2 What Moves Where

#### Files that MOVE to `AgentEval.MAF` (4 files — actual MAF dependencies)

| File | Namespace Change | Why |
|------|-----------------|-----|
| `MAFAgentAdapter.cs` | `AgentEval.MAF` (unchanged) | Imports `Microsoft.Agents.AI` |
| `MAFIdentifiableAgentAdapter.cs` | `AgentEval.MAF` (unchanged) | Imports `Microsoft.Agents.AI` |
| `MAFWorkflowEventBridge.cs` | `AgentEval.MAF` (unchanged) | Imports `Microsoft.Agents.AI.Workflows` |
| `MAFGraphExtractor.cs` | `AgentEval.MAF` (unchanged) | Imports `Microsoft.Agents.AI.Workflows.Checkpointing` |

#### Files that STAY in `AgentEval` core (3 files — zero MAF dependencies)

| File | Current Namespace | New Namespace | New Location | Why |
|------|-------------------|---------------|-------------|-----|
| `MAFEvaluationHarness.cs` | `AgentEval.MAF` | `AgentEval.Core` | `src/AgentEval/Core/` | Zero MAF imports — works through `IEvaluableAgent`/`IStreamableAgent` |
| `WorkflowEvaluationHarness.cs` | `AgentEval.MAF` | `AgentEval.Core` | `src/AgentEval/Core/` | Zero MAF imports — works through `IWorkflowEvaluableAgent` |
| `MAFWorkflowAdapter.cs` | `AgentEval.MAF` | Split (see below) | Split | Mixed — core adapter logic is framework-agnostic, but `FromMAFWorkflow()` factory imports MAF |

#### Special Case: `MAFWorkflowAdapter.cs` (must be split)

This file contains two distinct concerns:

1. **Framework-agnostic workflow adapter** — The `MAFWorkflowAdapter` class takes a `Func<string, CancellationToken, IAsyncEnumerable<WorkflowEvent>>` and processes AgentEval's own `WorkflowEvent` records. No MAF imports needed. Also includes `FromSteps()`, `FromConditionalSteps()`, and `WithGraph()` factory methods for testing.

2. **MAF factory method** — The `FromMAFWorkflow()` static method takes a `MAFWorkflows.Workflow` and wires it to `MAFWorkflowEventBridge`. This is the only part that imports MAF.

3. **`WorkflowEvent` record hierarchy** — `WorkflowEvent`, `ExecutorOutputEvent`, `ExecutorToolCallEvent`, `ExecutorErrorEvent`, `WorkflowCompleteEvent`, `EdgeTraversedEvent`, `RoutingDecisionEvent`, `ParallelBranchStartEvent`, `ParallelBranchEndEvent`. These are AgentEval-owned types with zero MAF dependencies.

**Splitting approach:**

| What | Moves To | Why |
|------|----------|-----|
| `WorkflowEvent` records | `AgentEval` core (e.g., `src/AgentEval/Models/WorkflowEvents.cs`) | Framework-agnostic event types used by `IWorkflowEvaluableAgent` pipeline |
| `MAFWorkflowAdapter` class (minus `FromMAFWorkflow`) | `AgentEval` core (e.g., `src/AgentEval/Core/WorkflowAdapter.cs`) | Rename to `WorkflowAdapter` — accepts any event source via delegate |
| `FromMAFWorkflow()` factory | `AgentEval.MAF` (e.g., as extension method or separate `MAFWorkflowAdapterFactory.cs`) | Only part that imports MAF |

### 9.3 Step-by-Step Implementation

#### Phase 1: Pre-extraction preparations (in current single-project structure)

These steps can happen **before** creating the new project, making extraction cleaner:

1. **Move `WorkflowEvent` records** from bottom of `MAFWorkflowAdapter.cs` to `src/AgentEval/Models/WorkflowEvents.cs` with namespace `AgentEval.Models`.

2. **Rename harnesses** — `MAFEvaluationHarness` → `DefaultEvaluationHarness`, `WorkflowEvaluationHarness` → `DefaultWorkflowEvaluationHarness`. Move to `src/AgentEval/Core/`. Add `[Obsolete("Use DefaultEvaluationHarness")]` type-forwarding aliases temporarily.

3. **Extract `FromMAFWorkflow()`** from `MAFWorkflowAdapter` into a separate static class `MAFWorkflowFactory` in the `MAF/` directory.

4. **Rename `MAFWorkflowAdapter`** to `WorkflowAdapter` and move to `src/AgentEval/Core/`. It has no MAF dependency once `FromMAFWorkflow()` is extracted.

After Phase 1, the `src/AgentEval/MAF/` directory contains only truly MAF-dependent files:
```
src/AgentEval/MAF/
├── MAFAgentAdapter.cs              ← Imports Microsoft.Agents.AI
├── MAFIdentifiableAgentAdapter.cs  ← Imports Microsoft.Agents.AI
├── MAFWorkflowEventBridge.cs       ← Imports Microsoft.Agents.AI.Workflows
├── MAFGraphExtractor.cs            ← Imports Microsoft.Agents.AI.Workflows
└── MAFWorkflowFactory.cs           ← Imports Microsoft.Agents.AI.Workflows (extracted from adapter)
```

#### Phase 2: Create `AgentEval.MAF` project

**Step 2a: Create the project file**

```
src/AgentEval.MAF/AgentEval.MAF.csproj
```

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    
    <PackageId>AgentEval.MAF</PackageId>
    <Version>0.2.1-beta</Version>
    <Description>Microsoft Agent Framework (MAF) adapters for AgentEval. 
Provides MAFAgentAdapter, workflow event bridge, and graph extraction 
for evaluating MAF agents with AgentEval's metrics and assertions.</Description>
    <PackageTags>ai;agent;evaluation;maf;microsoft-agent-framework</PackageTags>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
    
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  </PropertyGroup>

  <ItemGroup>
    <!-- Core AgentEval library -->
    <ProjectReference Include="..\AgentEval\AgentEval.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- MAF-specific dependencies (only this package pulls them) -->
    <PackageReference Include="Microsoft.Agents.AI" />
    <PackageReference Include="Microsoft.Agents.AI.Workflows" />
    <!-- SourceLink -->
    <PackageReference Include="Microsoft.SourceLink.GitHub" PrivateAssets="All" />
  </ItemGroup>

</Project>
```

**Step 2b: Move the 5 MAF files**

```
src/AgentEval/MAF/MAFAgentAdapter.cs              → src/AgentEval.MAF/MAFAgentAdapter.cs
src/AgentEval/MAF/MAFIdentifiableAgentAdapter.cs   → src/AgentEval.MAF/MAFIdentifiableAgentAdapter.cs
src/AgentEval/MAF/MAFWorkflowEventBridge.cs        → src/AgentEval.MAF/MAFWorkflowEventBridge.cs
src/AgentEval/MAF/MAFGraphExtractor.cs             → src/AgentEval.MAF/MAFGraphExtractor.cs
src/AgentEval/MAF/MAFWorkflowFactory.cs            → src/AgentEval.MAF/MAFWorkflowFactory.cs
```

Namespace stays `AgentEval.MAF` — no change needed.

**Step 2c: Remove MAF package references from core**

From `src/AgentEval/AgentEval.csproj`, remove:
```xml
<!-- REMOVE these two lines -->
<PackageReference Include="Microsoft.Agents.AI" />
<PackageReference Include="Microsoft.Agents.AI.Workflows" />
```

`Microsoft.Extensions.AI` stays — it's the shared abstraction layer, not MAF-specific.

**Step 2d: Delete the now-empty `src/AgentEval/MAF/` directory**

After moving all files out.

**Step 2e: Update the solution file**

Add the new project to `AgentEval.sln` under the `src` solution folder.

#### Phase 3: Update test project

Create `tests/AgentEval.MAF.Tests/` or keep MAF tests in `AgentEval.Tests` with a project reference to `AgentEval.MAF`:

**Option A (simpler):** Keep tests in existing project, add reference:
```xml
<!-- tests/AgentEval.Tests.csproj -->
<ProjectReference Include="..\..\src\AgentEval.MAF\AgentEval.MAF.csproj" />
```

**Option B (cleaner):** Create `tests/AgentEval.MAF.Tests/`:
```
tests/AgentEval.MAF.Tests/
├── AgentEval.MAF.Tests.csproj
├── MAFAgentAdapterTests.cs          (moved from Tests/MAF/)
├── MAFEvaluationHarnessTests.cs     (stays in main tests — no MAF dep)
├── MAFWorkflowAdapterTests.cs       (moved from Tests/MAF/)
├── MAFWorkflowEventBridgeTests.cs   (moved from Tests/MAF/)
├── MAFGraphExtractorTests.cs        (moved from Tests/MAF/)
└── ...
```

**Recommendation:** Option A for now. Option B when the test count grows.

#### Phase 4: Update samples

Samples that use MAF add the project reference:
```xml
<!-- samples/AgentEval.Samples/AgentEval.Samples.csproj -->
<ProjectReference Include="..\..\src\AgentEval.MAF\AgentEval.MAF.csproj" />
```

No code changes needed — `using AgentEval.MAF;` still resolves.

#### Phase 5: Optional DI extension

Add a convenience DI registration in the `AgentEval.MAF` package:

```csharp
// In AgentEval.MAF/DependencyInjection/AgentEvalMAFServiceCollectionExtensions.cs
namespace AgentEval.DependencyInjection;

public static class AgentEvalMAFServiceCollectionExtensions
{
    public static IServiceCollection AddAgentEvalMAF(this IServiceCollection services)
    {
        // Register MAF-specific services (adapter factories, etc.)
        return services;
    }
}
```

### 9.4 Resulting Architecture

```
BEFORE (current):
─────────────────
AgentEval.nupkg
├── AgentEval.Core (interfaces, no MAF)
├── AgentEval.Models (no MAF)
├── AgentEval.Metrics (no MAF)
├── AgentEval.Assertions (no MAF)
├── AgentEval.MAF ← ⚠️ pulls Microsoft.Agents.AI for ALL consumers
└── ...

AFTER (extracted):
──────────────────
AgentEval.nupkg                          AgentEval.MAF.nupkg
├── AgentEval.Core                       ├── MAFAgentAdapter
│   ├── IEvaluableAgent                  ├── MAFIdentifiableAgentAdapter
│   ├── IStreamableAgent                 ├── MAFWorkflowEventBridge
│   ├── IWorkflowEvaluableAgent          ├── MAFGraphExtractor
│   ├── DefaultEvaluationHarness         ├── MAFWorkflowFactory
│   ├── DefaultWorkflowEvaluationHarness │
│   └── WorkflowAdapter                 └── Dependencies:
├── AgentEval.Models                         ├── AgentEval (project ref)
│   └── WorkflowEvents.cs (records)          ├── Microsoft.Agents.AI
├── AgentEval.Metrics                        └── Microsoft.Agents.AI.Workflows
├── AgentEval.Assertions
└── ...
    NO Microsoft.Agents.AI dependency
```

### 9.5 Consumer Experience

**Before extraction:**
```xml
<!-- Consumer only using metrics — still pulls MAF -->
<PackageReference Include="AgentEval" Version="0.3.0" />
<!-- Transitively gets: Microsoft.Agents.AI, Microsoft.Agents.AI.Workflows -->
```

**After extraction — core-only consumer:**
```xml
<!-- Clean: no MAF pulled -->
<PackageReference Include="AgentEval" Version="1.0.0" />
```

**After extraction — MAF consumer:**
```xml
<!-- MAF consumer adds both -->
<PackageReference Include="AgentEval" Version="1.0.0" />
<PackageReference Include="AgentEval.MAF" Version="1.0.0" />
```

**Consumer code changes: NONE.** The `using AgentEval.MAF;` directive, `MAFAgentAdapter`, `MAFEvaluationHarness` — all resolve identically. The only change is adding one extra `<PackageReference>` line.

### 9.6 Versioning After Extraction

Both packages should be versioned in lockstep initially:

```
AgentEval 1.0.0 + AgentEval.MAF 1.0.0 → MAF 1.0.0
AgentEval 1.1.0 + AgentEval.MAF 1.1.0 → MAF 1.0.1 (patch)
AgentEval 1.2.0 + AgentEval.MAF 1.2.0 → MAF 2.0.0 (breaking)
```

`AgentEval.MAF` should declare a version range dependency on `AgentEval`:
```xml
<PackageReference Include="AgentEval" Version="[1.0.0, 2.0.0)" />
```

This allows `AgentEval` core to release patches independently while ensuring `AgentEval.MAF` stays compatible within the major version.

### 9.7 Pros and Cons

#### Pros

| Benefit | Impact |
|---------|--------|
| **Smaller dependency footprint** | Core consumers don't pull `Microsoft.Agents.AI` (~12+ transitive packages). Faster restore, smaller publish, fewer version conflicts. |
| **Independent release cadence** | `AgentEval.MAF` can update for MAF breaking changes without forcing a new `AgentEval` core release. Core metrics/assertions evolve independently. |
| **Opens adapter ecosystem** | Clear pattern for `AgentEval.SemanticKernel`, `AgentEval.AutoGen`, `AgentEval.LangChain4j`. Community can contribute framework bindings without modifying core. |
| **Cleaner architecture** | Enforces the already-existing abstraction at the package level, not just by convention. Compile-time guarantee that core never references MAF. |
| **Better for enterprise adoption** | Organizations with strict dependency policies won't be blocked by an unwanted transitive MAF dependency. |
| **Follows .NET ecosystem patterns** | Mirrors `Microsoft.Extensions.AI.OpenAI` (separate from `Microsoft.Extensions.AI`), `Serilog.Sinks.Console` (separate from `Serilog`), `MediatR.Extensions.Microsoft.DependencyInjection` (separate from `MediatR`). |

#### Cons

| Cost | Mitigation |
|------|------------|
| **Two packages to publish** | Automate with a single CI pipeline that packs both. Same `dotnet pack` command, two outputs. |
| **Lockstep versioning overhead** | Use `Directory.Build.props` with a shared `<Version>` property. Both packages always release together initially. |
| **Consumer adds one more `<PackageReference>`** | One line in `.csproj`. Documented clearly in getting-started guide. |
| **Solution complexity** | One more `.csproj` file. Standard for any multi-package .NET repo. |
| **Samples/docs need updating** | One-time cost. `using AgentEval.MAF;` doesn't change — just add the project/package reference. |
| **Initial refactoring effort** | ~2-4 hours. Most work is Phase 1 (moving/renaming). The code is already isolated. |
| **Breaking change for existing NuGet consumers** | Mitigate with a major version bump (0.x → 1.0) and clear migration guide: "Add `AgentEval.MAF` package reference." |

### 9.8 When to Do This

**Trigger conditions (any one):**

1. **MAF reaches 1.0 stable** — MAF is currently at RC1. Extracting now means maintaining a separate package against a near-final but potentially shifting target. Once MAF reaches GA, the extraction becomes lower-risk.

2. **AgentEval reaches GA (1.0)** — A major version bump is the natural time to restructure packages without breaking semver promises.

3. **A non-MAF adapter is requested** — If someone wants `AgentEval.SemanticKernel`, the extraction becomes immediately necessary to avoid forcing SK consumers to pull MAF.

4. **MAF dependency causes real conflicts** — If a consumer reports version conflicts between AgentEval's MAF transitive deps and their own, extract immediately.

**Do NOT extract if:**
- MAF is still in RC churn (current state: RC1, GA expected soon)
- No consumers have complained about the transitive dependency
- The team is resource-constrained and working on higher-priority features

### 9.9 Migration Guide (for existing consumers)

When the extraction happens, publish this alongside the release notes:

```markdown
## Migrating to AgentEval 1.0 (Package Split)

Starting with v1.0, MAF adapters are in a separate package.

### If you use MAFAgentAdapter, MAFEvaluationHarness, or MAFWorkflowAdapter:

Add the new package:
    dotnet add package AgentEval.MAF

Your `using AgentEval.MAF;` statements continue to work unchanged.

### If you only use metrics, assertions, or FakeChatClient:

No changes needed. Your dependency footprint is now smaller.

### Renamed types:
- `MAFEvaluationHarness` → `DefaultEvaluationHarness` (in `AgentEval.Core`)
- `WorkflowEvaluationHarness` → `DefaultWorkflowEvaluationHarness` (in `AgentEval.Core`)
- Old names still work via `[Obsolete]` aliases in `AgentEval.MAF` for one major version.
```
