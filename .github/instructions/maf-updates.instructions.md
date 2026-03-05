---
applyTo: "src/AgentEval.MAF/**,tests/AgentEval.Tests/MAF/**,Directory.Packages.props"
description: Instructions for detecting and adapting to Microsoft Agent Framework (MAF) breaking changes
---

# MAF Update & Breaking Change Instructions

These instructions apply when updating the Microsoft Agent Framework (MAF) NuGet dependency in AgentEval.

## Two-Phase Upgrade Workflow

MAF upgrades follow two phases:

```
Phase 1 (OPTIONAL):  maf-upgrade-preparation.instructions.md
                     Diff MAF source → produce plan document
                     Output: MAF/MAF-Upgrade-Plan.md

Phase 2 (THIS FILE): Update NuGet → fix breaks → run tests
                     Input: plan document (if available) OR compile errors
```

Phase 1 is recommended for major MAF version changes. For minor/patch updates or Dependabot PRs, start directly at Step 0 below.

## Step 0: Check for an Existing Upgrade Plan

Before starting, check if `MAF/MAF-Upgrade-Plan.md` exists.

- **If it exists:** Read it first. It contains pre-analyzed breaking changes, behavioral changes, and concrete fix code snippets produced by Phase 1. Use it as a guide — you already know what will break and how to fix it.
- **If it doesn't exist:** Proceed normally with build-error-driven detection (Step 1).

## Where MAF Versions Are Pinned

All MAF package versions are pinned in a single file:

```
Directory.Packages.props
```

Look for these three entries:
```xml
<PackageVersion Include="Microsoft.Agents.AI" Version="..." />
<PackageVersion Include="Microsoft.Agents.AI.OpenAI" Version="..." />
<PackageVersion Include="Microsoft.Agents.AI.Workflows" Version="..." />
```

All three MUST be updated to the same version simultaneously.

Also update the Microsoft.Extensions.AI packages if the MAF release requires a newer version:
```xml
<PackageVersion Include="Microsoft.Extensions.AI" Version="..." />
<PackageVersion Include="Microsoft.Extensions.AI.OpenAI" Version="..." />
<PackageVersion Include="Microsoft.Extensions.AI.Evaluation.Quality" Version="..." />
```

**Current versions — always check `Directory.Packages.props` for the actual pinned versions.**

**Which projects reference which MAF packages:**
- `src/AgentEval.MAF/AgentEval.MAF.csproj` → `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Workflows` (compile-time dependency)
- `src/AgentEval/AgentEval.csproj` (umbrella) → `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Workflows` (re-declared for NuGet consumers because `PrivateAssets="all"` suppresses transitive propagation from sub-projects)
- `samples/AgentEval.Samples/AgentEval.Samples.csproj` → `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`

## Step 1: Update the NuGet Version and Detect Breaking Changes

When a Dependabot PR arrives (or when manually bumping MAF versions):

1. Update the three `<PackageVersion>` entries in `Directory.Packages.props` to the new MAF version
2. Run:
   ```powershell
   dotnet restore --force
   dotnet build
   ```
3. Interpret results:
   - **Build succeeds** → No API breaking change. Proceed to Step 3 (run tests).
   - **Build fails with compile errors** → Breaking change detected. Proceed to Step 2.
   - **If an upgrade plan exists** → Compare compile errors against the plan. The plan should have predicted every failure. If there are surprises, investigate.

## Step 2: Fix MAF Breaking Changes

MAF-dependent code is confined to exactly these files in `src/AgentEval.MAF/MAF/`:

| File | MAF APIs Used | Most Likely to Break |
|------|--------------|---------------------|
| `MAFAgentAdapter.cs` | `AIAgent`, `RunAsync()`, `RunStreamingAsync()`, `CreateSessionAsync()`, `AgentResponse` | Method signatures, response shape |
| `MAFIdentifiableAgentAdapter.cs` | Same as above + `IModelIdentifiable` | Same as above |
| `MAFWorkflowEventBridge.cs` | `Workflow`, `InProcessExecution`, `StreamingRun`, `WorkflowEvent` subclasses, `TurnToken` | Event hierarchy restructuring |
| `MAFGraphExtractor.cs` | `Workflow.ReflectEdges()`, `EdgeKind`, `EdgeInfo`, `DirectEdgeInfo`, `Checkpointing` APIs | Reflection/edge API surface |
| `MAFWorkflowAdapter.cs` | `Workflow` (only in `FromMAFWorkflow()` static factory) | Factory wiring |

### Fix procedure:

1. Read the compile errors — they will point to the exact file and line
2. Check MAF release notes or changelog for the API change
3. Update ONLY the affected adapter file(s) to match the new MAF API
4. **Do NOT change** any of these (they have zero MAF dependencies):
   - Core interfaces in `src/AgentEval.Abstractions/` (`IEvaluableAgent`, `IStreamableAgent`, `IWorkflowEvaluableAgent`)
   - `MAFEvaluationHarness.cs` (despite the name, no MAF imports)
   - `WorkflowEvaluationHarness.cs` (no MAF imports)
   - Anything in `src/AgentEval.Core/` (metrics, assertions, comparison, tracing)
   - Anything in `src/AgentEval.DataLoaders/` (data loading, exporters)
   - Anything in `src/AgentEval.RedTeam/` (security scanning)

### Common breaking change patterns and how to fix:

**`AIAgent.RunAsync()` / `RunStreamingAsync()` signature changed:**
- Fix in `MAFAgentAdapter.InvokeAsync()` and `MAFIdentifiableAgentAdapter.InvokeAsync()`
- Map new parameters/return type to existing `AgentResponse` shape
- The `IEvaluableAgent.InvokeAsync()` contract stays unchanged

**`AgentResponse` properties renamed or restructured:**
- Fix in `MAFAgentAdapter.InvokeAsync()` — map new property names
- Key properties: `.Text`, `.Messages`, `.Usage`

**`AgentSession` / `CreateSessionAsync()` changed:**
- Fix in both adapters — session lifecycle is managed internally
- Note: `CreateSessionAsync()` is async (changed from sync `GetNewThread()` in preview)

**Agent configuration model changed (e.g., `ChatClientAgentOptions`):**
- Fix in samples that create `ChatClientAgent` instances
- Check if properties moved (like `Instructions` moved to `ChatOptions.Instructions` in RC1)
- Also update code examples in documentation (`docs/*.md`)

**Workflow event types changed:**
- Fix in `MAFWorkflowEventBridge.cs` — update the event pattern matching
- Map new MAF event types to existing AgentEval `WorkflowEvent` records
- Watch for event hierarchy changes (e.g., `AgentResponseUpdateEvent` extends `WorkflowOutputEvent` in RC1)
- AgentEval's own event records (`ExecutorOutputEvent`, `ExecutorToolCallEvent`, etc.) stay unchanged

**`Workflow.ReflectEdges()` API changed:**
- Fix in `MAFGraphExtractor.ExtractGraph()`
- Map new edge model to existing `WorkflowGraphSnapshot`

**`WorkflowBuilder` API changed (e.g., `AddFanInEdge` → `AddFanInBarrierEdge`):**
- Fix in test files that build workflows for testing
- No impact on production code (builder is only used in tests)

**Streaming content model changed (`update.Contents`):**
- Fix in `MAFAgentAdapter.InvokeStreamingAsync()`
- Note: Content types (`TextContent`, `FunctionCallContent`, etc.) come from `Microsoft.Extensions.AI`, not MAF. If these change, the impact is broader.

## Step 3: Run Tests

After fixing compile errors (or if there were none):

```powershell
# Run MAF integration tests first (fastest feedback for MAF-specific issues):
dotnet test --filter "Category=MAFIntegration"

# Then run all tests to confirm nothing else broke:
dotnet test
```

### Test files that exercise real MAF objects (tagged with `[Trait("Category", "MAFIntegration")]`):
- `tests/AgentEval.Tests/MAF/MAFWorkflowEventBridgeTests.cs`
- `tests/AgentEval.Tests/MAF/MAFGraphExtractorTests.cs`
- `tests/AgentEval.Tests/MAF/MAFWorkflowAdapterFromMAFWorkflowTests.cs`

### Test files that do NOT use real MAF objects (if these fail, the issue is in AgentEval logic, not MAF):
- `MAFEvaluationHarnessTests.cs`
- `MAFWorkflowAdapterTests.cs`
- `MAFWorkflowAdapterEdgeTests.cs`
- `WorkflowEvaluationHarnessTests.cs`
- `WorkflowToolTrackingTests.cs`
- `MicrosoftEvaluatorAdapterTests.cs`
- `ChatClientAdapterStreamingIntegrationTests.cs`

## Step 4: Update Compatibility Note

After successfully updating, add a one-line compatibility note to the release:

```
AgentEval X.Y.Z — Compatible with MAF <new-version>
```

## What NOT to Change During a MAF Update

- **Core interfaces** — `IEvaluableAgent`, `IStreamableAgent`, `IWorkflowEvaluableAgent` are the public API contract. MAF changes should NEVER require changes to these interfaces. If they do, something is architecturally wrong.
- **Models** — `AgentResponse`, `WorkflowExecutionResult`, `WorkflowEvent` records are AgentEval-owned types. Adapters translate MAF types into these; the models themselves don't change.
- **Metrics, Assertions, Tracing** — These work through the interfaces, never with MAF types directly.
- **Harnesses** — `MAFEvaluationHarness` and `WorkflowEvaluationHarness` have zero MAF imports despite their names.

## Versioning Rules

- AgentEval uses **independent SemVer** — version numbers reflect AgentEval's own changes, NOT MAF's version
- A MAF update that requires adapter changes → bump AgentEval **minor** version
- A MAF update that forces changes to core interfaces (shouldn't happen) → bump AgentEval **major** version
- A MAF update with zero code changes → bump AgentEval **patch** version (if releasing for compatibility note only)
- Do NOT try to match AgentEval version numbers to MAF version numbers

## Architecture Reference (Post-Modularization)

AgentEval is modularized into 6 sub-projects (see ADR-016). MAF dependencies are **compile-time isolated** in a separate project:

```
src/
├── AgentEval.Abstractions/   ← Interfaces, models — zero external deps
│   ├── Core/                 ← IEvaluableAgent, IStreamableAgent, IMetric, etc.
│   ├── Comparison/           ← IModelIdentifiable, IAgentFactory, etc.
│   └── Models/               ← TestCase, TestResult, PerformanceMetrics, etc.
├── AgentEval.Core/           ← NO MAF deps — metrics, assertions, comparison, tracing
├── AgentEval.DataLoaders/    ← NO MAF deps — dataset loading, 6 export formats
├── AgentEval.MAF/            ← ALL MAF deps confined here (4 of 7 files with imports)
│   └── MAF/
│       ├── MAFAgentAdapter.cs              ← Microsoft.Agents.AI
│       ├── MAFIdentifiableAgentAdapter.cs  ← Microsoft.Agents.AI
│       ├── MAFWorkflowEventBridge.cs       ← Microsoft.Agents.AI.Workflows (heaviest)
│       ├── MAFGraphExtractor.cs            ← Microsoft.Agents.AI.Workflows + Checkpointing
│       ├── MAFWorkflowAdapter.cs           ← Microsoft.Agents.AI.Workflows (factory only)
│       ├── MAFEvaluationHarness.cs         ← NO MAF deps (works through interfaces)
│       └── WorkflowEvaluationHarness.cs    ← NO MAF deps (works through interfaces)
├── AgentEval.RedTeam/        ← NO MAF deps — security scanning
├── AgentEval/                ← Umbrella NuGet (embeds all 5 DLLs via PrivateAssets="all")
│   └── AgentEval.csproj      ← Re-declares MAF package refs for NuGet consumers
└── AgentEval.Cli/            ← CLI tool (separate NuGet)
    └── Commands/EvalCommand.cs  ← uses AgentEval.MAF (MAFEvaluationHarness)
```

Only 4 out of 7 files in `src/AgentEval.MAF/MAF/` actually import MAF types. The other 3 are framework-agnostic and work entirely through `IEvaluableAgent`/`IStreamableAgent`/`IWorkflowEvaluableAgent`.

**Key isolation property:** A `using Microsoft.Agents.AI` in Core, Abstractions, DataLoaders, or RedTeam would fail to compile — isolation is enforced by project boundaries, not convention.

## Step 5: Clean Up After Upgrade

After a successful upgrade:

1. If `MAF/MAF-Upgrade-Plan.md` exists, update its status to "Completed" with the date, or delete it
2. If `/MAFVnext/` exists, move its contents to `/MAF/` (so `/MAF/` reflects the new current version), then remove `/MAFVnext/`
3. Update documentation:
   - Check all code examples in `docs/*.md` for stale API patterns (especially `ChatClientAgentOptions` configuration)
   - Update `.github/instructions/` files to reflect new API names
   - Add or update the ADR if the upgrade involved significant breaking changes
   - Update `CHANGELOG.md` with the upgrade entry
4. Commit all changes with message: `deps: Update MAF to <new-version>`

### Lessons Learned from the Preview → RC1 Upgrade

The RC1 upgrade revealed that `ChatClientAgentOptions.Instructions` was removed (moved to `ChatOptions.Instructions`). This affected:
- 14 sample files (26 occurrences)
- 5+ documentation files with code examples
- Instruction files with mock agent examples

**Takeaway:** Always check sample files and documentation code examples, not just `src/` and `tests/`. Use `grep` to search for any API name that changed.

## Related Instructions

- **Pre-upgrade analysis:** `.github/instructions/maf-upgrade-preparation.instructions.md` — Diff MAF source before updating NuGet
- **Detailed architecture:** `strategy/MAF-Integration-Analysis.md` — Full MAF integration analysis, versioning decisions, extraction contingency plan
