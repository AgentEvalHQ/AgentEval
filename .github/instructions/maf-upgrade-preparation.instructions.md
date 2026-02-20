---
applyTo: "MAF/**,MAFvnext/**,src/AgentEval/MAF/**"
description: Instructions for analyzing a new MAF version and producing an upgrade plan BEFORE updating the NuGet package
---

# MAF Upgrade Preparation

## When to Use

This instruction is triggered when the user says any of:
- "prepare for MAF upgrade"
- "diff MAF vs MAFvnext"
- "produce a MAF update plan"
- "assess MAF changes"
- "analyze the new MAF version"

## What This Does

This is **Phase 1** of a two-phase MAF upgrade workflow:

```
Phase 1 (this file):  Diff source → produce plan document
Phase 2 (maf-updates.instructions.md):  Update NuGet → implement plan → run tests
```

The output of Phase 1 is a markdown document (`src/AgentEval/MAF/MAF-Upgrade-Plan.md`) that serves as input to Phase 2.

## Prerequisites

- `/MAF/` contains the **current** MAF source (matching the version pinned in `Directory.Packages.props`)
- `/MAFvnext/` contains the **newer** MAF source to upgrade to
- Both directories are gitignored and local-only
- Do NOT update `Directory.Packages.props` yet — that happens in Phase 2

## Step 1: Read the Current MAF Version

Read `Directory.Packages.props` and note the current pinned version of `Microsoft.Agents.AI`.

Also check if `/MAFvnext/` has a version identifier (in its `Directory.Build.props`, `.csproj` files, or `CHANGELOG.md`).

## Step 2: Identify What to Diff

Only diff MAF source files that AgentEval actually depends on. Do NOT diff the entire MAF codebase.

### From `Microsoft.Agents.AI/` (single-agent adapter surface)

Find and diff the files containing these types:
- `AIAgent` class — `RunAsync()`, `RunStreamingAsync()`, `CreateSessionAsync()`, `Name`
- `AgentSession` class
- `AgentResponse` class — `.Text`, `.Messages`, `.Usage`

### From `Microsoft.Agents.AI.Workflows/` (workflow adapter surface)

| File | Why AgentEval Depends On It |
|------|---------------------------|
| `Workflow.cs` | `ReflectEdges()`, `StartExecutorId`, `DescribeProtocolAsync()` |
| `WorkflowBuilder.cs` | Used in tests for workflow setup |
| `InProcessExecution.cs` | `RunStreamingAsync()` |
| `StreamingRun.cs` | `WatchStreamAsync()`, `TrySendMessageAsync()` |
| `TurnToken.cs` | Used as parameter in event bridge |
| `ExecutorBindingExtensions.cs` | `CreateFuncBinding` (tests) |
| `WorkflowEvent.cs` | Base event type |
| `ExecutorInvokedEvent.cs` | Event bridge maps this |
| `ExecutorCompletedEvent.cs` | Event bridge maps this |
| `ExecutorFailedEvent.cs` | Event bridge maps this |
| `AgentResponseUpdateEvent.cs` | Event bridge maps this |
| `WorkflowOutputEvent.cs` | Event bridge maps this |
| `ChatProtocol.cs` or related | `IsChatProtocol()` extension |

### From `Checkpointing/`

- `EdgeInfo` / `DirectEdgeInfo` / `EdgeKind` types — used by `MAFGraphExtractor`

### Files to SKIP

- Everything in `MAF/docs/`, `MAF/workflow-samples/`, `MAF/dotnet/tests/`
- Packages AgentEval doesn't reference: A2A, AGUI, CopilotStudio, Hosting, Mem0, DevUI, Declarative, etc.

## Step 3: Perform the Diff

For each relevant file, compare:
```
MAF/dotnet/src/.../File.cs      (current)
MAFvnext/dotnet/src/.../File.cs  (next)
```

Look for:
1. **Signature changes** — parameters added/removed/reordered, return types changed
2. **Type renames or namespace moves** — class/interface/enum renamed or relocated
3. **Property changes** — on `AgentResponse`, event types, `EdgeInfo`
4. **Removed APIs** — methods or types deleted entirely
5. **New APIs** — methods or types added that AgentEval could benefit from
6. **Behavioral indicators** — changes to method bodies suggesting different behavior (null vs empty, exception types, event ordering)
7. **Attribute changes** — `[Obsolete]` added, `[EditorBrowsable(Never)]` added
8. **Missing files** — file exists in one directory but not the other (type added or removed)

## Step 4: Map Changes to AgentEval Files

For each change found, identify which AgentEval adapter file(s) are affected:

| AgentEval File | MAF APIs It Uses |
|---------------|-----------------|
| `src/AgentEval/MAF/MAFAgentAdapter.cs` | `AIAgent.RunAsync()`, `RunStreamingAsync()`, `CreateSessionAsync()`, `AgentResponse.Text/Messages/Usage` |
| `src/AgentEval/MAF/MAFIdentifiableAgentAdapter.cs` | Same as above |
| `src/AgentEval/MAF/MAFWorkflowEventBridge.cs` | `Workflow`, `InProcessExecution.RunStreamingAsync()`, `StreamingRun`, `TurnToken`, all `*Event` types, `ChatProtocol` |
| `src/AgentEval/MAF/MAFGraphExtractor.cs` | `Workflow.ReflectEdges()`, `EdgeKind`, `EdgeInfo`, `DirectEdgeInfo`, `Checkpointing` |
| `src/AgentEval/MAF/MAFWorkflowAdapter.cs` | `Workflow` (only in `FromMAFWorkflow()` factory) |

Also check test files for test-only MAF APIs:
| Test File | MAF APIs It Uses |
|-----------|-----------------|
| `tests/AgentEval.Tests/MAF/MAFWorkflowEventBridgeTests.cs` | `WorkflowBuilder`, `ExecutorBindingExtensions` |
| `tests/AgentEval.Tests/MAF/MAFGraphExtractorTests.cs` | `WorkflowBuilder`, `ExecutorBindingExtensions` |
| `tests/AgentEval.Tests/MAF/MAFWorkflowAdapterFromMAFWorkflowTests.cs` | `WorkflowBuilder`, `ExecutorBindingExtensions` |

## Step 5: Produce the Plan Document

Create the file `src/AgentEval/MAF/MAF-Upgrade-Plan.md` with this structure:

```markdown
# MAF Upgrade Plan: <current-version> → <new-version>

**Date:** <today>
**Status:** Pending review
**Prepared by:** AI agent (source diff analysis)

## Summary

One-paragraph overview: how many breaking changes, behavioral changes, new APIs found.
Overall risk assessment: Low / Medium / High.

## 1. Breaking Changes (compile errors expected)

For each breaking change:

### 1.1 <Short description>
- **MAF change:** <what changed — old signature → new signature>
- **Affected AgentEval file(s):** <file path and line number(s)>
- **Fix:**
  ```csharp
  // Before (current):
  <current code>

  // After (fixed):
  <new code>
  ```
- **Risk:** Low / Medium / High
- **Tests that verify this:** <test name or "needs new test">

## 2. Behavioral Changes (compiles but may fail tests)

For each behavioral change:

### 2.1 <Short description>
- **MAF change:** <what changed semantically>
- **Affected AgentEval file(s):** <file path>
- **Existing test coverage:** Yes / No / Partial
- **Suggested test addition:** <if needed>

## 3. Deprecations (compiles with warnings)

For each deprecation:
- **What's deprecated:** <API name>
- **Replacement:** <new API>
- **Migrate now?** Yes (simple change) / No (defer to next upgrade)

## 4. New APIs Worth Adopting

For each opportunity:
- **New API:** <name and signature>
- **What it does:** <description>
- **AgentEval benefit:** <why we might want it>
- **Adopt now?** Yes / No (defer)

## 5. No Impact

Changes in MAF areas AgentEval doesn't use (listed for completeness).

## 6. Recommended Update Sequence

1. Update `Directory.Packages.props` to `<new-version>`
2. <ordered list of file edits, grouped by dependency>
3. Run `dotnet build` to verify compilation
4. Run `dotnet test --filter "Category=MAFIntegration"`
5. Run `dotnet test` for full suite
6. Update compatibility note in release

## 7. Estimated Effort

- Files to modify: <count>
- Lines to change: ~<estimate>
- New tests needed: <count>
- Risk of regressions: Low / Medium / High
```

## Step 6: Present to User

After creating the plan document:

1. Give the user a concise summary (not the full document): how many breaking changes, which files affected, overall risk
2. Tell the user the plan is at `src/AgentEval/MAF/MAF-Upgrade-Plan.md`
3. Ask: "Ready to proceed with the upgrade? I'll follow `maf-updates.instructions.md` and use this plan."

## What Happens Next

When the user approves:
1. Follow `.github/instructions/maf-updates.instructions.md`
2. The plan document serves as pre-analyzed context — the agent already knows what will break and how to fix it
3. After successful upgrade, update the plan document status to "Completed" and note the date
4. Rename `/MAFvnext/` content to `/MAF/` (or ask the user to do so)

## Important Rules

- Do NOT update `Directory.Packages.props` during preparation — that's Phase 2
- Do NOT modify any AgentEval source code during preparation — only analyze and plan
- Do NOT diff MAF packages AgentEval doesn't reference
- Focus on public API surface — internal implementation changes are irrelevant unless they affect behavior of APIs we call
- The plan document is a working artifact, not permanent documentation — it can be deleted after the upgrade is complete
