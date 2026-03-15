# AgentEval Memory Evaluation — Implementation Plan 🎸

> **Date:** 2026-03-06  
> **Author:** AgentEval Engineering  
> **Status:** APPROVED — Ready to Rock  
> **Prerequisites:** MAF RC3 source analysis, Wes alignment call, RC3 feature analysis  
> **Rock Anthem:** "Memory remains... but does your agent remember?" 🤘

---

## Implementation Progress Tracker

| Name | Description | % Done | Reviewed | Quality | Notes |
|------|-------------|--------|----------|---------|-------|
| **Core Models & Abstractions** | MemoryTestScenario, MemoryQuery, MemoryFact, MemoryEvaluationResult | 100% | ✅ | EXCELLENT | Complete: All models with factory methods, validation, temporal support |
| **F01: MemoryTestRunner** | Core orchestration engine for memory evaluation | 100% | ✅ | EXCELLENT | Complete: 3-phase orchestration with comprehensive error handling and performance tracking |  
| **F02: MemoryJudge** | LLM-based fact verification and scoring | 100% | ✅ | EXCELLENT | Complete: Structured LLM prompting with JSON parsing and robust fallback mechanisms |
| **F03: CanRemember Extensions** | One-liner memory testing API | 100% | ✅ | EXCELLENT | Complete: DI extensions re-enabled, CanRememberExtensions fully functional |
| **F04: Memory Fluent Assertions** | Fluent assertion chains for memory results | 100% | ✅ | EXCELLENT | Complete: Perfect AgentEval integration with structured exceptions and rich error messages |
| **F05: Temporal Memory Evaluation** | Time-travel queries and temporal reasoning | 100% | ✅ | EXCELLENT | Complete: Advanced time-travel queries and fact evolution scenarios with proper temporal metadata |
| **F07: Memory Scenario Library** | Built-in memory testing scenarios | 100% | ✅ | EXCELLENT | Complete: Comprehensive scenario collection including basic, chatty, cross-session, and temporal patterns |
| **F09: Cross-Session Memory Tests** | Session boundary testing with ISessionResettableAgent | 100% | ✅ | EXCELLENT | Complete: ICrossSessionEvaluator + CrossSessionEvaluator + CrossSessionResult models + DI registration + unit tests |
| **F10: Memory Reducer Evaluation** | Information compression fidelity testing | 100% | ✅ | EXCELLENT | Complete: IReducerEvaluator + ReducerEvaluator + ReducerEvaluationResult models + DI registration + unit tests |
| **F18: Memory Benchmark Suite** | Performance benchmarking and analysis | 100% | ✅ | EXCELLENT | Complete: IMemoryBenchmarkRunner + MemoryBenchmarkRunner + MemoryBenchmark presets (Quick/Standard/Full) + MemoryBenchmarkResult with grading + DI registration + unit tests |
| **F26: Chatty Conversation Scenarios** | Noise-resilient memory testing | 100% | ✅ | EXCELLENT | Complete: ChattyConversationScenarios with buried facts, topic changes, emotional distractors |
| **F27: Memory Reach-Back Testing** | Conversation depth and degradation analysis | 100% | ✅ | EXCELLENT | Complete: IReachBackEvaluator + ReachBackEvaluator + ReachBackResult/DepthResult models + DI registration + unit tests |
| **IMetric Implementations** | 5 memory-specific metrics (llm_*, code_*, embed_*) | 100% | ✅ | EXCELLENT | Complete: 5 metrics with proper AgentEval naming conventions, LLM integration, and cost estimation |
| **Framework Adapters** | MEAI and MAF session reset adapters | 100% | ✅ | COMPLETE | **DONE.** Moved `ISessionResettableAgent` to `AgentEval.Abstractions/Core/` (namespace `AgentEval.Core`). `ChatClientAgentAdapter` now implements `ISessionResettableAgent` (delegates to `ClearHistory()`). `MAFAgentAdapter` now implements `ISessionResettableAgent` (method already existed, just added interface declaration). Old interface deleted from Memory project. All 7,905 tests pass, all 5 samples verified. |
| **Test Infrastructure** | MockMemoryAgent, FakeChatClient patterns, unit tests | 100% | ✅ | EXCELLENT | 116 tests per TFM (348 total). Covers engine, models, scenarios, temporal, DI, all 4 evaluator implementations (ReachBack, Reducer, CrossSession, BenchmarkRunner), evaluator result models, and skipped-category handling. Remaining: integration tests with real agents |
| **DI Registration** | Service registration and extensions | 100% | ✅ | EXCELLENT | Complete: All 5 registration methods working, all scenario/metric/service registrations verified |
| **Sample Applications** | 5 sample apps demonstrating memory features | 100% | ✅ | EXCELLENT | 5 samples (28-32) complete in AgentEval.Samples: Basics, Benchmark Suite, Scenarios/Evaluators, DI Registration, Cross-Session Persistence. All verified and running. |

---

## Framework Adapters — Deep Analysis (March 8, 2026)

> **✅ STATUS: IMPLEMENTED (March 8, 2026)**
> All steps completed. `ISessionResettableAgent` moved to `AgentEval.Abstractions/Core/` with namespace `AgentEval.Core`. Both `ChatClientAgentAdapter` and `MAFAgentAdapter` now implement the interface. Build: 0 warnings, 0 errors. Tests: 7,905/7,905 passing (348 Memory + 7,557 Core × 3 TFMs). All 5 samples (28-32) verified.

### The Problem Statement

The `ISessionResettableAgent` interface enables cross-session memory evaluation — planting facts, resetting the session, then testing whether the agent recalls them. This is one of the most valuable memory evaluation capabilities: it tests **persistent memory** vs. **ephemeral context**.

**The gap:** The interface exists in `AgentEval.Memory` but the two primary adapters that every AgentEval user relies on do NOT implement it. This means cross-session benchmarks **silently skip** for the vast majority of users.

### Current State — Code-Level Analysis

#### 1. `ISessionResettableAgent` (AgentEval.Memory)

```
File:      src/AgentEval.Memory/ISessionResettableAgent.cs
Namespace: AgentEval.Memory
Extends:   nothing (standalone interface)
```

```csharp
public interface ISessionResettableAgent
{
    Task ResetSessionAsync(CancellationToken cancellationToken = default);
}
```

**Design note:** The plan (Section 4.5, 9.4) originally specified `ISessionResettableAgent : IEvaluableAgent`. The actual implementation is a standalone interface (no inheritance). This is actually the **better design** — it follows Interface Segregation Principle (ISP). An agent can implement both `IEvaluableAgent` AND `ISessionResettableAgent` without being forced into an inheritance hierarchy. The evaluators check via `agent is ISessionResettableAgent` pattern matching, which works perfectly with composition.

#### 2. `ChatClientAgentAdapter` (AgentEval.Core)

```
File:      src/AgentEval.Core/Core/ChatClientAgentAdapter.cs
Namespace: AgentEval.Core
Implements: IStreamableAgent (which extends IEvaluableAgent)
```

**Session reset capability:** YES — has `ClearHistory()` public method that clears `_conversationHistory`

```csharp
public void ClearHistory()
{
    _conversationHistory.Clear();
}
```

**What `ResetSessionAsync` would do:**
```csharp
public Task ResetSessionAsync(CancellationToken cancellationToken = default)
{
    ClearHistory();
    return Task.CompletedTask;
}
```

**Critical subtlety:** `ChatClientAgentAdapter` has a constructor parameter `bool includeHistory = false`. When `includeHistory` is `false`, the adapter is **stateless** — it doesn't accumulate messages. In this mode, `ClearHistory()` is a no-op (the list is always empty). For memory testing, the adapter MUST be created with `includeHistory: true` to maintain conversation state across calls. This is a **documentation/guidance concern**, not a code concern — `ResetSessionAsync` can still safely call `ClearHistory()` regardless.

#### 3. `MAFAgentAdapter` (AgentEval.MAF)

```
File:      src/AgentEval.MAF/MAF/MAFAgentAdapter.cs
Namespace: AgentEval.MAF
Implements: IStreamableAgent (which extends IEvaluableAgent)
```

**Session reset capability:** YES — has `ResetSessionAsync()` with the EXACT same signature as the interface

```csharp
public async Task ResetSessionAsync(CancellationToken cancellationToken = default)
{
    _session = await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
}
```

**This method already exists and does exactly what the interface requires.** Adding `: ISessionResettableAgent` to the class declaration is the ONLY change needed. Zero new code.

#### 4. `TraceReplayingAgent` (AgentEval.Core)

```
File:      src/AgentEval.Core/Tracing/TraceReplayingAgent.cs
Namespace: AgentEval.Tracing
Implements: IEvaluableAgent, IStreamableAgent
```

**Session reset capability:** Has `_currentIndex` field that tracks replay position. A `ResetSessionAsync` would reset this to 0. Not yet implemented but straightforward.

**Lower priority** — trace replay is about deterministic evaluation, not typically cross-session testing. Listed in the original plan but can be deferred.

### Dependency Graph — The Core Constraint

```
AgentEval.Abstractions  ← Root. No project references. All core interfaces live here.
    │                      IEvaluableAgent, IStreamableAgent, IMetric, etc.
    │
    ├── AgentEval.Core     ← References Abstractions only
    │   │                     ChatClientAgentAdapter, TraceReplayingAgent
    │   │
    │   ├── AgentEval.MAF  ← References Abstractions + Core
    │   │                     MAFAgentAdapter
    │   │
    │   ├── AgentEval.Memory ← References Abstractions + Core
    │   │                       ISessionResettableAgent (CURRENTLY HERE)
    │   │                       CrossSessionEvaluator, MemoryBenchmarkRunner, etc.
    │   │
    │   ├── AgentEval.DataLoaders ← References Abstractions + Core
    │   │
    │   └── AgentEval.RedTeam ← References Abstractions + Core
    │
    └── AgentEval (umbrella) ← References ALL sub-projects
```

**The constraint:**
- `ISessionResettableAgent` currently lives in `AgentEval.Memory`
- `ChatClientAgentAdapter` lives in `AgentEval.Core`
- `MAFAgentAdapter` lives in `AgentEval.MAF`
- Neither Core nor MAF reference Memory → **they CANNOT see `ISessionResettableAgent`**
- Adding a reference from Core/MAF → Memory would create an **architectural violation** (downstream depends on sibling)

**This is why the adapters don't implement the interface.** It's not an oversight — it's a dependency direction constraint.

### Solution Options — Full Analysis

#### Option A: Move `ISessionResettableAgent` to `AgentEval.Abstractions`

**THE RECOMMENDED SOLUTION**

```
AgentEval.Abstractions/Core/
    ├── IEvaluableAgent.cs
    ├── IStreamableAgent.cs        (extends IEvaluableAgent)
    ├── ISessionResettableAgent.cs  ← MOVED HERE
    └── ...
```

**Why this is correct architecturally:**
- Session reset is a **general agent capability**, not memory-specific. Any agent that manages conversation state benefits from a reset mechanism — not just for memory testing but also for:
  - Conversation isolation in test suites
  - Stateful agent testing (fresh state per test)
  - Workflow evaluation with clean-slate requirements
  - Benchmark runs that need independent iterations
- It follows the existing pattern: `IEvaluableAgent` (core), `IStreamableAgent` (streaming capability), `ISessionResettableAgent` (reset capability) — all optional capability interfaces in Abstractions
- Zero new dependencies. Abstractions has no project references.

**Changes required:**

| File | Change | Effort |
|------|--------|--------|
| `src/AgentEval.Abstractions/Core/ISessionResettableAgent.cs` | **Create** — move interface here, change namespace to `AgentEval.Core` | New file |
| `src/AgentEval.Memory/ISessionResettableAgent.cs` | **Delete** — no longer needed | Delete |
| `src/AgentEval.Core/Core/ChatClientAgentAdapter.cs` | Add `: ISessionResettableAgent` + `ResetSessionAsync()` method (3 lines) | Minimal |
| `src/AgentEval.MAF/MAF/MAFAgentAdapter.cs` | Add `: ISessionResettableAgent` to class declaration (method already exists) | 1 line |
| `src/AgentEval.Memory/Evaluators/CrossSessionEvaluator.cs` | Update `using` — remove `AgentEval.Memory`, namespace already resolved via `AgentEval.Core` | 1 line or auto |
| `src/AgentEval.Memory/Evaluators/MemoryBenchmarkRunner.cs` | Same `using` update | 1 line or auto |
| `src/AgentEval.Memory/Extensions/CanRememberExtensions.cs` | Same `using` update | 1 line or auto |
| All test files referencing `ISessionResettableAgent` | Same `using` update | Trivial |
| Sample32_MemoryCrossSession.cs | Remove `using AgentEval.Memory` if only used for this interface | Trivial |

**Namespace consideration:**
Both Abstractions and Memory use `RootNamespace=AgentEval`. The existing interface is in namespace `AgentEval.Memory`. After moving to Abstractions, it would naturally be `AgentEval.Core` (matching `IEvaluableAgent`). This is a **namespace change** that affects all consuming code.

**Alternative:** Keep namespace as `AgentEval.Memory` even inside Abstractions to avoid any breaking change. But this would be misleading — an interface in Abstractions shouldn't use a domain sub-namespace to avoid a chicken-and-egg situation. The clean approach is `AgentEval.Core` namespace.

**Binary compatibility impact:**
- Moving the interface changes its assembly location (from `AgentEval.Memory.dll` to `AgentEval.Abstractions.dll`).
- Since we're pre-1.0 (alpha/beta), this is acceptable and expected.
- The NuGet umbrella package ships all DLLs together — consumers referencing `AgentEval` get both DLLs.

#### Option B: Add a reference from Core/MAF → Memory

**NOT RECOMMENDED**

Would create circular-ish dependency concerns and violate the layered architecture. Core should not depend on a domain feature module.

#### Option C: Define a duplicate/shadow interface in Abstractions

**NOT RECOMMENDED**

Two `ISessionResettableAgent` interfaces in different assemblies would cause type-identity issues and confuse consumers.

#### Option D: Use a shared interface from a third-party or conventions (duck typing)

**NOT RECOMMENDED**

C# doesn't support duck typing. Source generators or runtime emit would add unnecessary complexity.

### Impact Analysis

**Without the fix (current state):**
```
User creates agent with ChatClientAgentAdapter or MAFAgentAdapter
    → Runs MemoryBenchmark.Full
    → Cross-Session category: SKIPPED (⚠️)
    → ReducerFidelity: ✅ (doesn't need session reset)
    → Benchmark score renormalized without Cross-Session
    → User never sees cross-session evaluation
    → Misleading — benchmark says "Full" but actually runs 7/8 categories
```

**With the fix:**
```
User creates agent with ChatClientAgentAdapter(includeHistory: true) or MAFAgentAdapter
    → Runs MemoryBenchmark.Full
    → Cross-Session category: ✅ EVALUATED
    → All 8/8 categories produce scores
    → True "Full" benchmark — no silent skipping
```

**Quantified impact:** The Cross-Session category carries **15% weight** in the Full benchmark. Skipping it means the other 7 categories get inflated weights through renormalization, hiding the fact that a whole dimension of memory quality goes unmeasured.

### `ChatClientAgentAdapter` — Additional Guidance Needed

The `includeHistory` flag creates a subtlety that warrants documentation:

```csharp
// ❌ WRONG for memory testing — stateless, no conversation accumulation
var adapter = new ChatClientAgentAdapter(chatClient, "MyAgent");

// ✅ CORRECT for memory testing — stateful, maintains conversation across calls
var adapter = new ChatClientAgentAdapter(chatClient, "MyAgent", includeHistory: true);
```

When `includeHistory: false` (the default), the adapter sends only the current prompt each call. Facts told in previous turns are lost because they're not re-sent. `ResetSessionAsync` would technically work (clears empty list) but is meaningless.

**Recommendation:** Add a `/// <remarks>` section to `ChatClientAgentAdapter` noting that `includeHistory: true` is required for memory evaluation. Optionally, `ResetSessionAsync` could log a warning if `_includeHistory` is `false`:

```csharp
public Task ResetSessionAsync(CancellationToken cancellationToken = default)
{
    if (!_includeHistory)
        _logger?.LogWarning("ResetSessionAsync called but includeHistory is false. " +
            "Memory evaluation requires includeHistory: true for meaningful results.");
    ClearHistory();
    return Task.CompletedTask;
}
```

### `MAFAgentAdapter` — Ready As-Is

The MAF adapter's `ResetSessionAsync` already does exactly the right thing:
1. Calls `_agent.CreateSessionAsync()` which creates a fresh `AgentSession`
2. Assigns the new session to `_session`
3. Next `InvokeAsync` call uses the fresh session — clean slate for conversation
4. MAF's persistence layer (if configured) retains long-term memory independently of session

**No behavioral changes needed.** Just add `: ISessionResettableAgent` to the class declaration.

### `TraceReplayingAgent` — Future Enhancement

The trace replayer could support session reset by resetting `_currentIndex = 0`, effectively replaying from the beginning. This is useful for:
- Testing cross-session scenarios with deterministic replay
- CI pipelines that test session reset behavior without LLM calls

**Effort:** Small (5-10 lines). **Priority:** Low — defer to a separate PR.

### Implementation Steps (Ordered)

```
Step 1: Create src/AgentEval.Abstractions/Core/ISessionResettableAgent.cs
        ├── namespace AgentEval.Core
        ├── Copy interface definition
        └── Preserve XML docs

Step 2: Delete src/AgentEval.Memory/ISessionResettableAgent.cs

Step 3: Update ChatClientAgentAdapter
        ├── Add : ISessionResettableAgent to class declaration
        └── Add ResetSessionAsync() method (calls ClearHistory())

Step 4: Update MAFAgentAdapter
        └── Add : ISessionResettableAgent to class declaration
            (method ResetSessionAsync already exists with correct signature)

Step 5: Fix usings across AgentEval.Memory
        ├── CrossSessionEvaluator.cs — verify using AgentEval.Core resolves
        ├── MemoryBenchmarkRunner.cs — same
        ├── CanRememberExtensions.cs — same
        └── Any files with "using AgentEval.Memory" for ISessionResettableAgent

Step 6: Fix test usings
        ├── CrossSessionEvaluatorTests.cs
        ├── MemoryBenchmarkRunnerTests.cs
        └── Sample32_MemoryCrossSession.cs

Step 7: Add/update unit tests
        ├── ChatClientAgentAdapterTests — verify ResetSessionAsync clears history
        ├── MAFAgentAdapterTests — verify ResetSessionAsync creates new session
        └── Verify existing memory tests still pass with namespace change

Step 8: Build + test full solution (0 warnings, 0 errors, all TFMs green)
```

### Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Namespace change breaks consumer code | Low (pre-1.0) | Low | Umbrella NuGet includes all DLLs; using `AgentEval.Core` is natural |
| `includeHistory: false` confusion | Medium | Low | Document in remarks + optional warn log |
| MAFAgentAdapter session semantics differ from test expectations | Low | Medium | MAF session reset is well-defined — `CreateSessionAsync()` is clean |
| Breaking existing tests | Low | Low | Namespace change is the only change; update `using` statements |

### Effort Estimate

| Component | Lines Changed | Risk |
|-----------|--------------|------|
| New file in Abstractions | ~20 lines | None |
| Delete file from Memory | -22 lines | None |
| ChatClientAgentAdapter | +8 lines (interface + method) | Trivial |
| MAFAgentAdapter | +1 line (interface declaration) | Trivial |
| Using statement updates | ~10 files, 1 line each | Trivial |
| New tests | ~20-30 lines | Trivial |
| **TOTAL** | ~80 lines net | **Very Low** |

### Verdict

**Do it. This is the highest-impact, lowest-risk improvement remaining in the memory feature set.** It unblocks cross-session evaluation for 100% of standard adapter users. The architectural decision (move to Abstractions) is clean and consistent with existing patterns. The implementation is almost entirely additive — no existing behavior changes.

### Implementation Log (March 8, 2026)

All 8 steps from the implementation plan above were executed successfully:

| Step | Action | Result |
|------|--------|--------|
| 1 | Created `src/AgentEval.Abstractions/Core/ISessionResettableAgent.cs` | ✅ Namespace `AgentEval.Core`, enhanced XML docs with `<remarks>`, `<see cref>` links |
| 2 | Deleted `src/AgentEval.Memory/ISessionResettableAgent.cs` | ✅ Removed |
| 3 | Updated `ChatClientAgentAdapter` | ✅ Added `: ISessionResettableAgent` + `ResetSessionAsync()` method (delegates to `ClearHistory()`) |
| 4 | Updated `MAFAgentAdapter` | ✅ Added `: ISessionResettableAgent` to class declaration (method already existed) |
| 5 | Fixed usings in Memory project | ✅ All files already had `using AgentEval.Core;` — no changes needed |
| 6 | Fixed test/sample usings | ✅ Removed unused `using AgentEval.Memory;` from Sample32 |
| 7 | Build verification | ✅ `dotnet build` — 0 warnings, 0 errors |
| 8 | Full test run | ✅ 7,905 tests passing (116×3 Memory + 2,519×3 Core) |

**Samples verified:** All 5 memory samples (28-32) run successfully. Sample 32 (Cross-Session) correctly exercises the `ISessionResettableAgent` flow.

**Key insight:** No using statement changes were needed in the Memory project because all consuming files already had `using AgentEval.Core;`. The only cleanup was removing `using AgentEval.Memory;` from Sample32 (which was only there for the old interface location).

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [The 12 MUST HAVE Features — Simple Explanations](#2-the-12-must-have-features--simple-explanations)
3. [Architecture Overview — The Full Stack](#3-architecture-overview--the-full-stack)
4. [**CRITICAL: Framework Compatibility Analysis (MEAI vs MAF)**](#4-critical-framework-compatibility-analysis-meai-vs-maf)
5. [Core Base Features — The Foundation](#5-core-base-features--the-foundation)
6. [Project Structure — AgentEval.Memory](#6-project-structure--agentevalmemory)
7. [Feature-by-Feature Implementation Plan](#7-feature-by-feature-implementation-plan)
8. [Abstractions & Shared Contracts](#8-abstractions--shared-contracts)
9. [Core Adjustments Needed](#9-core-adjustments-needed)
10. [Pareto Analysis — 80/20 for Each Feature](#10-pareto-analysis--8020-for-each-feature)
11. [Implementation Phases & Order](#11-implementation-phases--order)
12. [The Grand Architecture — How It All Fits Together](#12-the-grand-architecture--how-it-all-fits-together)
13. [F08 & F09 — Full Feature Implementation Plans](#13-f08--f09--full-feature-implementation-plans)
14. [**CRITICAL MISSING INTEGRATIONS** — Source Code Analysis](#14-critical-missing-integrations--source-code-analysis)
15. [Feature Classification: MAF vs AgentEval](#15-feature-classification-maf-vs-agentevalframe)
16. [Feature Comparison Table — Market Analysis](#16-feature-comparison-table--market-analysis)
17. [**RECOMMENDATION: Double Down on Agentic Evaluation**](#17-recommendation-double-down-on-agentic-evaluation)
18. [Unit Test Structure & Implementation Order](#18-unit-test-structure--implementation-order)
19. [Performance Framework & Benchmarking Strategy](#19-performance-framework--benchmarking-strategy)
20. [Sample Applications & Developer Experience](#20-sample-applications--developer-experience)

---

## 1. Executive Summary

AgentEval Memory Evaluation is a new module (`AgentEval.Memory`) that enables developers to **test and benchmark how well their AI agents remember**. It is **framework-universal** — it works with ANY `IEvaluableAgent`: Microsoft.Extensions.AI (MEAI) agents via `ChatClientAgentAdapter`, MAF agents via `MAFAgentAdapter`, OpenAI-compatible endpoints, or any custom agent implementation. **Zero coupling to any specific framework or memory provider.**

**12 MUST HAVE features (S-Tier):**
F01 Core Engine, F02 LLM-as-Judge, F03 CanRememberAsync, F04 Fluent Assertions, F05 Temporal Evaluation, F07 Scenario Library, **F08 Scope Misconfiguration Detection**, **F09 Cross-Session Persistence**, F10 Chat Reducer Evaluation, F18 Memory Benchmark Suite, F26 Chatty Conversations, F27 Reach-Back Depth Testing.

**Critical architectural decision (see Section 4):**
- 11 of 12 features are **UNIVERSAL** — work with ANY agent → `AgentEval.Memory`
- 1 feature (F08) is **MAF-specific** — needs MAF scope types → `AgentEval.MAF`
- Cross-session/reach-back testing needs new `ISessionResettableAgent` interface

**Design principles:**
- Test through interfaces, NEVER depend on concrete implementations
- **Framework-universal by default** — MEAI, MAF, OpenAI, custom — all supported
- Reuse existing AgentEval patterns (metrics, assertions, DI, exporters)
- 80/20 Pareto — each feature delivers maximum value with minimum complexity
- SOLID, DRY, KISS — no over-engineering, no premature abstraction

**One-liner taste test:**
```csharp
// The dream API — is your agent a goldfish or an elephant?
var canRemember = await agent.CanRememberAsync(
    tell: "I'm allergic to peanuts",
    ask: "What food allergies should you know about?",
    expect: "peanuts"
);

// The full evaluation — memory health check
var result = await runner.RunMemoryBenchmarkAsync(agent, MemoryBenchmark.Standard);
result.Should().HaveOverallScoreAbove(80);
```

---

## 2. The 12 MUST HAVE Features — Simple Explanations

### F01: Core Engine — The Foundation 🏗️

**What:** The data models, test runner, and result types that everything else builds on.

```
┌──────────────────────────────────────────────────────────────┐
│                    F01: CORE ENGINE                           │
│                                                              │
│   MemoryTestScenario          MemoryTestRunner               │
│   ┌──────────────────┐       ┌────────────────────────┐     │
│   │ Name              │       │                        │     │
│   │ Steps[] ─────────────────►│  1. Create session      │     │
│   │   "I'm José"      │       │  2. Run scenario steps  │     │
│   │   "I live in CPH"  │       │  3. Execute queries     │     │
│   │ Queries[] ────────────────►│  4. Judge responses     │     │
│   │   "Where do I live?"│       │  5. Build result        │     │
│   │   expect: "CPH"    │       │                        │     │
│   └──────────────────┘       └──────────┬─────────────┘     │
│                                          │                   │
│                               MemoryEvaluationResult         │
│                               ┌──────────────────────┐      │
│                               │ OverallScore: 85     │      │
│                               │ RetentionScore: 90   │      │
│                               │ TemporalScore: 70    │      │
│                               │ FactResults[]        │      │
│                               │ Duration: 2.3s       │      │
│                               └──────────────────────┘      │
└──────────────────────────────────────────────────────────────┘
```

---

### F02: LLM-as-Judge for Memory 🧠

**What:** Uses an LLM to judge whether the agent's response contains expected facts and doesn't contain forbidden facts.

```
Agent Response                  LLM Judge
┌───────────────────┐          ┌─────────────────────────────┐
│ "I remember you   │          │ Expected: ["Copenhagen"]    │
│  mentioned living  │────────►│ Forbidden: ["Stockholm"]    │
│  in Copenhagen,   │          │                             │
│  Denmark."        │          │ Verdict:                    │
└───────────────────┘          │  ✅ "Copenhagen" FOUND      │
                               │  ✅ "Stockholm" NOT found   │
                               │  Score: 100/100             │
                               └─────────────────────────────┘
```

---

### F03: CanRememberAsync One-Liner 🎯

**What:** The simplest possible API — one line to test memory.

```csharp
// That's it. One line. Does your agent remember?
bool remembers = await agent.CanRememberAsync(
    tell: "My birthday is March 15",
    ask:  "When is my birthday?",
    expect: "March 15"
);
```

---

### F04: Fluent Memory Assertions ✅

**What:** Beautiful assertion chains that match AgentEval's existing style.

```csharp
result.Should()
    .HavePassed()
    .HaveRetentionScoreAbove(90, because: "basic facts must be retained")
    .HaveTemporalScoreAbove(70)
    .HaveNoForbiddenFacts()
    .HaveFactRecalled("Copenhagen")
    .HaveFactRecalled("peanut allergy");
```

---

### F05: Temporal Memory Evaluation ⏰

**What:** Tests whether the agent handles facts that change over time.

```
TIMELINE OF FACTS
==================

  2025-03 ─────── "I drive a Ferrari" ──────────────────────────
                                        │
  2025-11 ─────── "I sold the Ferrari, ─┘  ── "I ride a Honda" ─
                    bought a Honda"              │
                                                 │
  2026-02 ─────── "I got another Ferrari" ──     │
                                             │   │
                                             ▼   ▼
  QUERY: "What vehicles do I own NOW?"
  EXPECT: ["Ferrari", "Honda"]    <── Both should be recalled
  
  QUERY: "What did I drive in October 2025?"
  EXPECT: ["Ferrari"]  FORBID: ["Honda"]    <── Time-travel query
```

---

### F07: Built-in Scenario Library 📚

**What:** Pre-built test scenarios so users don't start from scratch.

```
BUILT-IN SCENARIOS
==================

MemoryScenarios.BasicRetention      → "Remember 5 facts, recall all 5"
MemoryScenarios.FactUpdate          → "Fact changes, recall latest version"
MemoryScenarios.TemporalReasoning   → "Facts with timestamps, time-travel queries"
MemoryScenarios.MultiTopic          → "Facts across 5 different topics"
MemoryScenarios.Contradiction       → "Conflicting facts, which wins?"
MemoryScenarios.ImplicitFacts       → "'Going to gym at 8' → remembers schedule"
MemoryScenarios.ChattyConversation  → "Facts buried in chit-chat noise"
MemoryScenarios.ReachBack           → "Fact at turn 1, noise for 25 turns, recall"
MemoryScenarios.SafetyCritical      → "Allergies, medications — MUST remember"
MemoryScenarios.ProfileBuilding     → "Name, age, city over multiple turns"
MemoryScenarios.ReducerStress       → "Long conversation, test reducer impact"
MemoryScenarios.CrossSession        → "Remember across session boundaries"
```

---

### F10: Chat Reducer Evaluation 🔧

**What:** Measures what information the IChatReducer loses when compressing conversation history. Nobody measures this properly — this is a massive industry blind spot.

```
WHAT THE REDUCER DOES TO YOUR MEMORY
======================================

Before Reducer (20 messages, ~4000 tokens):
┌────────────────────────────────────────────┐
│ Turn 1: "I'm allergic to peanuts"    ←KEY  │
│ Turn 2: "Nice weather today"               │
│ Turn 3: "Tell me a joke"                   │
│ Turn 4: "Ha! That's funny"                 │
│ Turn 5: "My meeting is at 3pm"       ←KEY  │
│ Turn 6: "What's the capital of France?"    │
│ ... (turns 7-16: chit-chat)                │
│ Turn 17: "I prefer email over Slack" ←KEY  │
│ Turn 18: "Interesting"                     │
│ Turn 19: "Thanks for the help"             │
│ Turn 20: "See you tomorrow"                │
└────────────────────────────────────────────┘
              │
              ▼  MessageCountingChatReducer(keep: 5)
              
After Reducer (5 messages, ~1000 tokens):
┌────────────────────────────────────────────┐
│ Turn 16: "That's a good point"             │
│ Turn 17: "I prefer email over Slack" ←KEY  │
│ Turn 18: "Interesting"                     │
│ Turn 19: "Thanks for the help"             │
│ Turn 20: "See you tomorrow"                │
└────────────────────────────────────────────┘

LOST: ❌ "allergic to peanuts" (SAFETY CRITICAL!)
LOST: ❌ "meeting at 3pm" (IMPORTANT)
KEPT: ✅ "prefer email" (nice to have)

Reducer Fidelity Score: 33% (1/3 key facts retained)
Critical Fact Loss: YES — safety-critical info lost!
```

---

### F18: Memory Benchmark Suite 🏆

**What:** A standardized battery of tests that gives a holistic "memory quality score" for any agent. Like a synthetic benchmark for GPUs, but for agent memory.

```
MEMORY BENCHMARK RESULTS
=========================

┌─────────────────────────────────────────────────────────────┐
│                  MEMORY BENCHMARK v1.0                        │
│                  Agent: "CustomerSupportBot"                 │
├──────────────────────┬──────────┬───────────────────────────┤
│ Category             │ Score    │ Grade                     │
├──────────────────────┼──────────┼───────────────────────────┤
│ Basic Retention      │  95/100  │ ⭐⭐⭐⭐⭐ Excellent           │
│ Temporal Reasoning   │  62/100  │ ⭐⭐⭐   Needs Work          │
│ Noise Resilience     │  78/100  │ ⭐⭐⭐⭐  Good                │
│ Reach-Back Depth     │  45/100  │ ⭐⭐    Poor (max depth: 15) │
│ Contradiction Handle │  70/100  │ ⭐⭐⭐⭐  Good                │
│ Reducer Fidelity     │  55/100  │ ⭐⭐⭐   Needs Work          │
│ Cross-Session        │  88/100  │ ⭐⭐⭐⭐⭐ Excellent           │
│ Update Handling      │  80/100  │ ⭐⭐⭐⭐  Good                │
├──────────────────────┼──────────┼───────────────────────────┤
│ OVERALL              │  72/100  │ ⭐⭐⭐⭐  GOOD                │
└──────────────────────┴──────────┴───────────────────────────┘
│ Recommendation: Improve temporal reasoning and reach-back.   │
│ Consider a semantic memory provider with longer context.     │
└──────────────────────────────────────────────────────────────┘
```

---

### F26: Chatty Conversation Scenarios 💬

**What:** Test scenarios where important facts are buried in 80% noise — realistic conversations with small talk, pleasantries, and topic changes.

```
CHATTY SCENARIO EXAMPLE
=========================

Turn  1: "Hey! How's it going?"              ← noise
Turn  2: "Pretty good, thanks for asking"     ← noise  
Turn  3: "By the way, I'm allergic to        ← ⚡ FACT (buried!)
          peanuts"
Turn  4: "Awesome! What else is new?"         ← noise
Turn  5: "Not much, just chilling"            ← noise
Turn  6: "That's cool! I like your style"     ← noise
Turn  7: "Thanks! Oh, my meeting is at 3pm"   ← ⚡ FACT (buried!)
Turn  8: "Got it! How about that game?"       ← noise
Turn  9: "It was amazing! Garcia scored!"     ← noise
Turn 10: "Haha nice! Tell me more"            ← noise
  ...
Turn 30: "Can you suggest a restaurant        ← QUERY
          for dinner tonight?"

MUST recall: peanut allergy (from turn 3, buried under 27 noise turns!)
Signal-to-noise ratio: 2 facts / 28 noise turns = 7%
```

---

### F27: Reach-Back Depth Testing 🎸 (The Deep Dive)

**What:** Measures exactly HOW FAR BACK your agent can recall facts through layers of noise. The "sonar depth test" for memory.

```
REACH-BACK DEPTH TEST
======================

    FACT planted ──►  N turns of noise  ──► QUERY
    at depth 0       (variable depth)       "Do you recall?"
    
    Depth   Result
    ─────   ──────
       5    ✅ Recalled perfectly
      10    ✅ Recalled perfectly  
      25    ✅ Recalled with minor details missing
      50    ⚠️ Partially recalled (key detail lost)
     100    ❌ Completely forgotten
    
    ┌────────────────────────────────────────────────┐
    │  DEGRADATION CURVE                              │
    │                                                 │
    │ 100%│ ■ ■ ■                                     │
    │  80%│         ■                                 │
    │  60%│                                           │
    │  40%│           ■                               │
    │  20%│                                           │
    │   0%│             ■                             │
    │     └───┬───┬───┬───┬───                        │
    │         5  10  25  50 100  ← noise depth        │
    │                                                 │
    │  Max Reliable Depth: 25 turns                   │
    │  Failure Point: 50 turns                        │
    └────────────────────────────────────────────────┘
```

---

## 3. Architecture Overview — The Full Stack

```
┌────────────────────────────────────────────────────────────────────────────────┐
│                    AgentEval Memory Evaluation — Full Architecture              │
│                                                                                │
│  ┌── USER-FACING API ──────────────────────────────────────────────────────┐   │
│  │                                                                         │   │
│  │  agent.CanRememberAsync()     result.Should().HavePassed()             │   │
│  │  runner.RunMemoryTestAsync()  runner.RunMemoryBenchmarkAsync()          │   │
│  │  agent.EvaluateReachBackAsync()  agent.EvaluateReducerAsync()          │   │
│  │                                                                         │   │
│  └─────────────────────────┬───────────────────────────────────────────────┘   │
│                             │                                                  │
│  ┌── SCENARIOS & BENCHMARKS ┴──────────────────────────────────────────────┐   │
│  │                                                                         │   │
│  │  MemoryScenarios.BasicRetention    MemoryBenchmark.Standard             │   │
│  │  MemoryScenarios.Temporal          MemoryBenchmark.Quick                │   │
│  │  MemoryScenarios.ChattyConversation                                    │   │
│  │  MemoryScenarios.ReachBack         Custom scenarios                    │   │
│  │                                                                         │   │
│  └─────────────────────────┬───────────────────────────────────────────────┘   │
│                             │                                                  │
│  ┌── EVALUATION ENGINE ────┴───────────────────────────────────────────────┐   │
│  │                                                                         │   │
│  │  ┌─────────────────┐  ┌──────────────────┐  ┌──────────────────────┐   │   │
│  │  │ MemoryTestRunner │  │ MemoryJudge      │  │ MemoryMetrics        │   │   │
│  │  │                  │  │ (LLM-as-Judge)   │  │                      │   │   │
│  │  │ Runs scenarios   │  │                  │  │ llm_memory_retention │   │   │
│  │  │ against agents   │  │ Evaluates if     │  │ llm_memory_temporal  │   │   │
│  │  │ Collects results │  │ response contains│  │ code_memory_reducer  │   │   │
│  │  │ Coordinates flow │  │ expected facts   │  │ code_memory_reachback│   │   │
│  │  └─────────────────┘  └──────────────────┘  └──────────────────────┘   │   │
│  │                                                                         │   │
│  │  ┌────────────────────────┐  ┌────────────────────────────────────┐    │   │
│  │  │ ReachBackEvaluator     │  │ ReducerEvaluator                   │    │   │
│  │  │ Parametric depth test  │  │ Measures info loss from IChatReducer│    │   │
│  │  └────────────────────────┘  └────────────────────────────────────┘    │   │
│  │                                                                         │   │
│  └─────────────────────────┬───────────────────────────────────────────────┘   │
│                             │                                                  │
│  ┌── MODELS & RESULTS ─────┴──────────────────────────────────────────────┐   │
│  │                                                                         │   │
│  │  MemoryTestScenario    MemoryEvaluationResult    MemoryBenchmarkResult  │   │
│  │  MemoryQuery           MemoryFactResult          ReachBackResult        │   │
│  │  MemoryFact            ReducerEvaluationResult   NoiseGenerator         │   │
│  │                                                                         │   │
│  └─────────────────────────┬───────────────────────────────────────────────┘   │
│                             │                                                  │
│  ┌── ASSERTIONS ───────────┴──────────────────────────────────────────────┐   │
│  │                                                                         │   │
│  │  MemoryAssertions.Should()                                             │   │
│  │    .HavePassed()                .HaveRetentionScoreAbove(90)           │   │
│  │    .HaveTemporalScoreAbove(70)  .HaveNoForbiddenFacts()               │   │
│  │    .HaveFactRecalled("X")       .HaveReachBackDepthAtLeast(25)        │   │
│  │    .HaveReducerFidelityAbove(80)                                      │   │
│  │                                                                         │   │
│  └─────────────────────────────────────────────────────────────────────────┘   │
│                                                                                │
│  ┌── INFRASTRUCTURE (in AgentEval.Abstractions & AgentEval.Core) ──────────┐  │
│  │                                                                          │  │
│  │  IEvaluableAgent  |  IEvaluator  |  IChatClient  |  DI Registration     │  │
│  │  FakeChatClient   |  MetricResult |  TestResult   |  Existing Exporters  │  │
│  │                                                                          │  │
│  └──────────────────────────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────────────────────────┘
```

---

## 4. CRITICAL: Framework Compatibility Analysis (MEAI vs MAF)

> **This section answers the fundamental question: Is memory evaluation only for MAF agents? (SPOILER: NO!)**

### 4.1 The Answer: Memory Evaluation is UNIVERSAL 🌍

After analyzing the existing AgentEval codebase, the answer is clear: **memory evaluation works with ANY agent framework**, not just MAF. Here's why:

The **core memory test** is purely behavioral:
```
1. TELL the agent a fact    →  agent.InvokeAsync("I'm allergic to peanuts")
2. (Optional noise/delay)   →  agent.InvokeAsync("Nice weather today")
3. ASK if it remembers       →  agent.InvokeAsync("What allergies should you know about?")
4. JUDGE the response        →  Does "peanuts" appear in the response?
```

This works with **ANY `IEvaluableAgent`** that maintains conversation state. The memory evaluation module doesn't care HOW the agent remembers — it only cares WHETHER it remembers.

### 4.2 Existing Agent Adapters Already Support This

AgentEval already has TWO agent adapters that maintain conversation state:

```
EXISTING ADAPTERS — BOTH SUPPORT MEMORY TESTING
=================================================

┌─────────────────────────────────────────────────────────────────────────────────┐
│                                                                                 │
│  MEAI (Universal)                          MAF (Framework-Specific)             │
│                                                                                 │
│  ChatClientAgentAdapter                    MAFAgentAdapter                      │
│  (in AgentEval.Core)                       (in AgentEval.MAF)                  │
│                                                                                 │
│  ┌──────────────────────────┐              ┌──────────────────────────┐        │
│  │ Wraps: IChatClient       │              │ Wraps: AIAgent           │        │
│  │ History: includeHistory  │              │ Session: AgentSession    │        │
│  │         = true           │              │          (auto-created)  │        │
│  │                          │              │                          │        │
│  │ ClearHistory() ──────────│──────────────│── ResetSessionAsync()    │        │
│  │                          │              │                          │        │
│  │ Works with:              │              │ Works with:              │        │
│  │  • Azure OpenAI          │              │  • MAF AIAgent           │        │
│  │  • OpenAI                │              │  • Any MAF agent with    │        │
│  │  • Anthropic             │              │    memory providers      │        │
│  │  • Any IChatClient       │              │  • MAF workflows         │        │
│  │  • Ollama                │              │                          │        │
│  └──────────────────────────┘              └──────────────────────────┘        │
│         │                                          │                           │
│         │            BOTH implement                 │                           │
│         │            IEvaluableAgent                │                           │
│         └──────────────┬───────────────────────────┘                           │
│                        │                                                       │
│                        ▼                                                       │
│              ┌─────────────────────────┐                                       │
│              │   IEvaluableAgent       │                                       │
│              │  .InvokeAsync(prompt)   │  ← Memory evaluation uses ONLY this   │
│              └─────────────────────────┘                                       │
│                                                                                 │
│  MEMORY EVALUATION MODULE SITS HERE — FRAMEWORK AGNOSTIC                       │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
```

**Key code evidence:**

```csharp
// ChatClientAgentAdapter (AgentEval.Core) — MEAI universal adapter
public ChatClientAgentAdapter(
    IChatClient chatClient,
    string name = "ChatClientAgent",
    string? systemPrompt = null,
    ChatOptions? chatOptions = null,
    bool includeHistory = false)      // ← Set to TRUE for memory testing!

public void ClearHistory()             // ← Session reset capability!
```

```csharp
// MAFAgentAdapter (AgentEval.MAF) — MAF-specific adapter  
public MAFAgentAdapter(AIAgent agent, AgentSession? session = null)

public async Task ResetSessionAsync(CancellationToken ct)    // ← Session reset!
public async Task<AgentSession> CreateSessionAsync(CancellationToken ct)
```

### 4.3 Feature-by-Feature: What's Universal vs. MAF-Specific

```
FRAMEWORK COMPATIBILITY MATRIX
================================

Feature    Description                    Universal?  MAF-Only?  Where?
───────    ───────────────────────────    ──────────  ─────────  ──────────────────
F01        Core Engine                    ✅          -          AgentEval.Memory
F02        LLM-as-Judge                   ✅          -          AgentEval.Memory
F03        CanRememberAsync               ✅          -          AgentEval.Memory
F04        Fluent Assertions              ✅          -          AgentEval.Memory
F05        Temporal Evaluation            ✅          -          AgentEval.Memory
F07        Scenario Library               ✅          -          AgentEval.Memory
F08        Scope Misconfig Detection      -           ✅         AgentEval.MAF
F09        Cross-Session Persistence      ✅*         -          AgentEval.Memory
F10        Reducer Evaluation             ✅          -          AgentEval.Memory
F18        Benchmark Suite                ✅          -          AgentEval.Memory
F26        Chatty Conversations           ✅          -          AgentEval.Memory
F27        Reach-Back Depth               ✅*         -          AgentEval.Memory

* = Requires ISessionResettableAgent for full capability (graceful degradation without it)

SCORE: 11/12 features are UNIVERSAL  =  91.7% framework-agnostic! 🎸
```

### 4.4 The One MAF-Only Feature: F08 Scope Misconfiguration

F08 needs to inspect MAF-specific types (`StorageScope`, `SearchScope`, `AIContextProvider` configuration). These types don't exist outside MAF. This feature goes in `AgentEval.MAF/Memory/`:

```csharp
// This is MAF-specific — needs StorageScope, SearchScope from Microsoft.Agents.AI
// Lives in AgentEval.MAF, NOT in AgentEval.Memory
public class ScopeMisconfigurationDetector
{
    public ScopeMisconfigurationResult Analyze(AIAgent agent) { ... }
}
```

### 4.5 The Session Reset Problem — And The Solution

Two features need the ability to **reset the conversation session** while keeping persistent memory intact:
- **F09** (Cross-Session): Tell facts in session 1 → reset → query in session 2
- **F27** (Reach-Back): Each depth level needs a fresh session

**The problem:** `IEvaluableAgent` has no session management — it's stateless by design.

**The solution:** A new optional interface `ISessionResettableAgent`:

```csharp
// NEW: In AgentEval.Abstractions
// Optional interface — agents that can reset their session for multi-session testing
public interface ISessionResettableAgent : IEvaluableAgent
{
    /// <summary>
    /// Resets the conversation session, creating a fresh context.
    /// Persistent memory (long-term store, vector DB, etc.) should survive the reset.
    /// Only conversational context (chat history) is cleared.
    /// </summary>
    Task ResetSessionAsync(CancellationToken cancellationToken = default);
}
```

**Who implements it:**
```
┌─────────────────────────────────────────────────────────────────┐
│                                                                 │
│  ChatClientAgentAdapter                                         │
│  ├── Already has: ClearHistory()                               │
│  └── NEW: implements ISessionResettableAgent                    │
│        ResetSessionAsync() → calls ClearHistory()               │
│                                                                 │
│  MAFAgentAdapter                                                │
│  ├── Already has: ResetSessionAsync(), CreateSessionAsync()    │
│  └── NEW: implements ISessionResettableAgent                    │
│        ResetSessionAsync() → calls _agent.CreateSessionAsync() │
│                                                                 │
│  TraceReplayingAgent                                           │
│  └── NEW: implements ISessionResettableAgent (resets replay)   │
│                                                                 │
│  Custom agents from users                                       │
│  └── Can optionally implement ISessionResettableAgent           │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

**Graceful degradation:** When the agent does NOT implement `ISessionResettableAgent`:
```csharp
// In MemoryTestRunner — cross-session test
if (agent is ISessionResettableAgent resettable)
{
    await resettable.ResetSessionAsync(ct);  // Clean reset
    // Continue with cross-session query...
}
else
{
    // Graceful degradation: skip cross-session tests, 
    // log warning: "Agent does not support session reset. 
    // Cross-session tests will be skipped. Implement ISessionResettableAgent 
    // to enable cross-session memory evaluation."
}
```

### 4.6 How Each Framework Benefits

```
MEMORY EVALUATION BY FRAMEWORK
================================

┌─────────────────────────────┬─────────────────────────────────────────────────┐
│ Framework                   │ What They Get                                   │
├─────────────────────────────┼─────────────────────────────────────────────────┤
│                             │                                                 │
│ MEAI (IChatClient)          │ ✅ All behavioral tests (F01-F07, F10, F18-F27) │
│ via ChatClientAgentAdapter  │ ✅ Cross-session (if includeHistory=true+reset) │
│                             │ ✅ CanRememberAsync one-liner                   │
│                             │ ✅ Full benchmark suite                         │
│                             │ ❌ No scope misconfig detection (no scopes)     │
│                             │                                                 │
│ MAF (AIAgent)               │ ✅ ALL 12 features — full evaluation suite      │
│ via MAFAgentAdapter         │ ✅ F08 scope misconfig detection (MAF-specific) │
│                             │ ✅ Cross-session with real session management   │
│                             │ ✅ Full benchmark suite                         │
│                             │ ✅ Tests memory providers end-to-end            │
│                             │                                                 │
│ OpenAI-compatible endpoint  │ ✅ All behavioral tests via ChatClientAdapter   │
│ via IChatClient             │ ✅ CanRememberAsync one-liner                   │
│                             │ ⚠️ Limited cross-session (depends on impl)     │
│                             │                                                 │
│ Custom IEvaluableAgent      │ ✅ All behavioral tests out of the box          │
│                             │ ✅ Optional: implement ISessionResettableAgent  │
│                             │    for cross-session and reach-back evaluation  │
│                             │                                                 │
└─────────────────────────────┴─────────────────────────────────────────────────┘
```

### 4.7 The Architecture Decision — Code Goes Where?

```
WHERE DOES THE CODE LIVE?
==========================

AgentEval.Abstractions/Memory/        ← Interfaces + Models (zero dependencies)
  • IMemoryTestRunner, IMemoryJudge, ISessionResettableAgent
  • MemoryTestScenario, MemoryEvaluationResult, etc.

AgentEval.Memory/ (NEW)               ← Universal implementation (depends on Core)
  • MemoryTestRunner, MemoryJudge, MemoryAssertions
  • 11 of 12 features
  • Works with ANY IEvaluableAgent
  • NO MAF REFERENCES ANYWHERE

AgentEval.Core/                        ← Extend existing adapter
  • ChatClientAgentAdapter NOW ALSO implements ISessionResettableAgent

AgentEval.MAF/                         ← MAF-specific extensions
  • MAFAgentAdapter NOW ALSO implements ISessionResettableAgent
  • NEW: AgentEval.MAF/Memory/ScopeMisconfigurationDetector.cs (F08)
  • That's it — just F08 lives here

AgentEval/ (umbrella)                  ← Adds Memory project reference
  • <ProjectReference Include="../AgentEval.Memory/..." PrivateAssets="all" />
  • AddAgentEvalAll() → includes AddAgentEvalMemory()
```

### 4.8 Developer Experience by Framework

```csharp
// ═══════════════════════════════════════════════════════════════
// MEAI Agent (any IChatClient — Azure OpenAI, OpenAI, Anthropic, Ollama...)
// ═══════════════════════════════════════════════════════════════

var chatClient = new AzureOpenAIClient(endpoint, credential)
    .GetChatClient("gpt-4o")
    .AsIChatClient();

// Wrap in adapter with history enabled (critical for memory testing!)
var agent = new ChatClientAgentAdapter(chatClient, includeHistory: true);

// Test memory — works out of the box!
var remembers = await agent.CanRememberAsync(
    tell: "I'm allergic to peanuts",
    ask: "What food allergies do I have?",
    expect: "peanuts"
);

// Full benchmark — also works!
var result = await runner.RunMemoryBenchmarkAsync(agent, MemoryBenchmark.Standard);
result.Should().HaveOverallScoreAbove(70);


// ═══════════════════════════════════════════════════════════════
// MAF Agent (with memory providers)
// ═══════════════════════════════════════════════════════════════

var mafAgent = new MyMAFAgentWithMem0Provider();
var adapter = new MAFAgentAdapter(mafAgent);

// Same API — identical developer experience!
var remembers = await adapter.CanRememberAsync(
    tell: "I'm allergic to peanuts",
    ask: "What food allergies do I have?",
    expect: "peanuts"
);

// Full benchmark — identical!
var result = await runner.RunMemoryBenchmarkAsync(adapter, MemoryBenchmark.Standard);

// BONUS: MAF-specific scope check
var scopeCheck = new ScopeMisconfigurationDetector();
var scopeResult = scopeCheck.Analyze(mafAgent);
scopeResult.Should().HaveNoMisconfigurations();


// ═══════════════════════════════════════════════════════════════
// Custom Agent (any framework, any implementation)
// ═══════════════════════════════════════════════════════════════

public class MyCustomAgent : IEvaluableAgent, ISessionResettableAgent
{
    private readonly MyMemoryStore _store;
    private List<string> _chatHistory = new();
    
    public string Name => "CustomAgent";
    
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct)
    {
        _chatHistory.Add(prompt);
        var context = await _store.SearchAsync(prompt);  // Your memory system
        var response = await _llm.ChatAsync(prompt, context, _chatHistory);
        _chatHistory.Add(response);
        return new AgentResponse { Text = response };
    }
    
    public Task ResetSessionAsync(CancellationToken ct)
    {
        _chatHistory.Clear();  // Clear chat, keep memory store
        return Task.CompletedTask;
    }
}

// Same API — works perfectly!
var result = await runner.RunMemoryBenchmarkAsync(myCustomAgent, MemoryBenchmark.Standard);
```

### 4.9 What Does Microsoft.Extensions.AI Give Us for Memory?

**MEAI provides the building blocks, not the memory management:**

```
MEAI CONTRIBUTION TO MEMORY EVALUATION
========================================

What MEAI provides:
  ✅ IChatClient — the LLM interface (used by MemoryJudge for fact-checking)
  ✅ ChatMessage — conversation message model
  ✅ ChatOptions — model configuration
  ✅ FunctionCallContent — tool call tracking
  ✅ UsageContent — token tracking

What MEAI does NOT provide:
  ❌ Memory management (no built-in memory system)
  ❌ Session management (no session concept)
  ❌ RAG/retrieval (not in MEAI itself)
  ❌ Context window management (no reducer)

That gap is exactly where agents add memory:
  • MAF adds: AIContextProvider, ChatHistoryProvider, IChatReducer
  • Semantic Kernel adds: Memory plugins, planners
  • LangChain adds: Memory chains, retrievers
  • Custom code adds: Vector stores, embeddings, summarization

AgentEval.Memory tests the RESULT of all of these — framework-agnostically.
```

**Bottom line:** MEAI is the foundation (IChatClient for judge, ChatClientAgentAdapter for testing), but memory is an agent-level concern that lives ABOVE MEAI. AgentEval.Memory evaluates the agent's memory behavior regardless of how it was implemented.

---

## 5. Core Base Features — The Foundation

Before implementing any of the 12 MUST HAVE features, we need these **core building blocks**. These are not user-facing features — they're the plumbing that makes everything work.

### 5.1 Core Contracts (in AgentEval.Abstractions)

```
CORE CONTRACTS — What goes in Abstractions
============================================

┌─────────────────────────────────────────────────────────┐
│ AgentEval.Abstractions/Memory/                          │
│                                                         │
│ Interfaces:                                             │
│ ├── IMemoryTestRunner        Run memory test scenarios  │
│ ├── IMemoryJudge             LLM-based fact verification│
│ ├── IMemoryBenchmark         Benchmark suite contract   │
│ ├── IReducerEvaluator        Reducer info-loss analysis │
│ ├── IReachBackEvaluator      Depth testing contract     │
│ ├── INoiseGenerator          Noise generation contract  │
│ │                                                       │
│ Models:                                                 │
│ ├── MemoryTestScenario       Test scenario definition   │
│ ├── MemoryQuery              Query with expected facts  │
│ ├── MemoryFact               A fact to remember/verify  │
│ ├── MemoryStep               A conversation step        │
│ ├── MemoryEvaluationResult   Result of scenario run     │
│ ├── MemoryFactResult         Per-fact pass/fail result  │
│ ├── MemoryBenchmarkResult    Full benchmark output      │
│ ├── ReachBackResult          Depth test result          │
│ ├── ReachBackDepthResult     Per-depth measurement      │
│ ├── ReducerEvaluationResult  Reducer fidelity results   │
│ └── MemoryScoreCategory      Enum of score dimensions   │
└─────────────────────────────────────────────────────────┘
```

### 5.2 Core Implementations (in AgentEval.Memory)

```
CORE IMPLEMENTATIONS — What goes in Memory module
===================================================

┌─────────────────────────────────────────────────────────┐
│ AgentEval.Memory/                                       │
│                                                         │
│ Engine:                                                 │
│ ├── MemoryTestRunner          Orchestrates test runs    │
│ ├── MemoryJudge               LLM fact-checking impl   │
│ │                                                       │
│ Evaluators:                                             │
│ ├── ReachBackEvaluator        Depth testing impl        │
│ ├── ReducerEvaluator          Reducer fidelity impl     │
│ │                                                       │
│ Metrics:                                                │
│ ├── MemoryRetentionMetric     llm_ basic retention      │
│ ├── MemoryTemporalMetric      llm_ temporal reasoning   │
│ ├── MemoryReachBackMetric     code_ reach-back depth    │
│ ├── MemoryReducerMetric       code_ reducer fidelity    │
│ │                                                       │
│ Scenarios:                                              │
│ ├── MemoryScenarios           Built-in scenario library │
│ ├── NoiseGenerators           Chit-chat noise generators│
│ │                                                       │
│ Benchmark:                                              │
│ ├── MemoryBenchmarkRunner     Full benchmark execution  │
│ ├── MemoryBenchmark           Benchmark definitions     │
│ │                                                       │
│ Assertions:                                             │
│ ├── MemoryAssertions          Fluent assertion chains   │
│ ├── MemoryAssertionExceptions Rich error types          │
│ │                                                       │
│ Extensions:                                             │
│ ├── CanRememberExtensions     One-liner extension methods│
│ │                                                       │
│ DependencyInjection:                                    │
│ └── MemoryServiceExtensions   services.AddAgentEvalMemory()│
└─────────────────────────────────────────────────────────┘
```

### 5.3 Why These Core Features MUST Exist First

Every MUST HAVE feature depends on these core types. Here's the dependency map:

```
CORE DEPENDENCY MAP
====================

MemoryTestScenario ◄──── F01, F05, F07, F10, F18, F26, F27
MemoryTestRunner   ◄──── F01, F03, F05, F07, F10, F18, F26, F27
MemoryJudge        ◄──── F02, F03, F05, F07, F18, F26, F27
MemoryEvalResult   ◄──── F01, F04, F05, F07, F10, F18, F26, F27
MemoryAssertions   ◄──── F04
INoiseGenerator    ◄──── F26, F27
ReachBackEvaluator ◄──── F27
ReducerEvaluator   ◄──── F10
BenchmarkRunner    ◄──── F18

IMPLEMENTATION ORDER:
  1. Models (MemoryTestScenario, MemoryQuery, MemoryFact, results)
  2. MemoryJudge (LLM fact-checking)
  3. MemoryTestRunner (orchestration)
  4. MemoryAssertions (fluent API)
  5. CanRememberExtensions (one-liner)
  6. INoiseGenerator + NoiseGenerators (noise generation)
  7. MemoryScenarios (built-in library)
  8. ReachBackEvaluator + ReducerEvaluator (specialized evaluators)
  9. BenchmarkRunner (ties everything together)
```

---

## 6. Project Structure — AgentEval.Memory

### 6.1 New Project: `src/AgentEval.Memory/`

Following the established pattern (like `AgentEval.RedTeam/`, `AgentEval.DataLoaders/`):

```
src/AgentEval.Memory/
├── AgentEval.Memory.csproj
│
├── DependencyInjection/
│   └── MemoryServiceCollectionExtensions.cs      # services.AddAgentEvalMemory()
│
├── Memory/
│   ├── Engine/
│   │   ├── MemoryTestRunner.cs                   # F01: Core orchestrator
│   │   └── MemoryJudge.cs                        # F02: LLM-as-Judge
│   │
│   ├── Evaluators/
│   │   ├── ReachBackEvaluator.cs                 # F27: Depth testing
│   │   ├── ReducerEvaluator.cs                   # F10: Reducer fidelity
│   │   └── CrossSessionEvaluator.cs              # F09: Cross-session persistence
│   │
│   ├── Metrics/
│   │   ├── MemoryRetentionMetric.cs              # llm_memory_retention
│   │   ├── MemoryTemporalMetric.cs               # llm_memory_temporal
│   │   ├── MemoryReachBackMetric.cs              # code_memory_reachback
│   │   ├── MemoryReducerFidelityMetric.cs        # code_memory_reducer_fidelity
│   │   └── MemoryNoiseResilienceMetric.cs        # llm_memory_noise_resilience
│   │
│   ├── Scenarios/
│   │   ├── MemoryScenarios.cs                    # F07: Static scenario library
│   │   ├── TemporalScenarios.cs                  # F05: Temporal scenarios
│   │   ├── ChattyScenarios.cs                    # F26: Chatty conversation scenarios
│   │   └── NoiseGenerators.cs                    # Noise generation for F26/F27
│   │
│   ├── Benchmark/
│   │   ├── MemoryBenchmarkRunner.cs              # F18: Benchmark orchestrator
│   │   └── MemoryBenchmark.cs                    # Benchmark definitions (Quick/Standard/Full)
│   │
│   ├── Assertions/
│   │   ├── MemoryAssertions.cs                   # F04: Fluent assertions
│   │   └── MemoryAssertionExceptions.cs          # Rich error messages
│   │
│   └── Extensions/
│       └── CanRememberExtensions.cs              # F03: One-liner API
│
└── README.md
```

### 6.2 New Abstractions: `src/AgentEval.Abstractions/Memory/`

```
src/AgentEval.Abstractions/Memory/
├── IMemoryTestRunner.cs              # Core runner interface
├── IMemoryJudge.cs                   # LLM judge interface
├── IMemoryBenchmark.cs               # Benchmark contract
├── IReducerEvaluator.cs              # Reducer evaluation contract
├── IReachBackEvaluator.cs            # Reach-back testing contract
├── ICrossSessionEvaluator.cs         # Cross-session testing contract
├── INoiseGenerator.cs                # Noise generation contract
├── ISessionResettableAgent.cs        # NEW: Session reset for multi-session testing
│
├── Models/
│   ├── MemoryTestScenario.cs         # Scenario definition
│   ├── MemoryStep.cs                 # Conversation step (tell)
│   ├── MemoryQuery.cs                # Query with expected/forbidden facts
│   ├── MemoryFact.cs                 # A fact (text + metadata)
│   ├── MemoryEvaluationResult.cs     # Scenario result
│   ├── MemoryFactResult.cs           # Per-fact result
│   ├── MemoryBenchmarkResult.cs      # Benchmark result
│   ├── ReachBackResult.cs            # Reach-back result
│   ├── ReachBackDepthResult.cs       # Per-depth measurement
│   ├── ReducerEvaluationResult.cs    # Reducer fidelity result
│   ├── CrossSessionResult.cs         # Cross-session result
│   ├── CrossSessionScenario.cs       # Cross-session scenario definition
│   ├── MemoryScoreCategory.cs        # Score dimension enum
│   └── MemoryBenchmarkOptions.cs     # Configuration options
│
└── Extensions/
    └── IMemoryEvaluableAgent.cs      # Optional: richer agent contract for memory
```

### 6.3 The .csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <IsPackable>false</IsPackable>
    <RootNamespace>AgentEval</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="AgentEval" />
    <InternalsVisibleTo Include="AgentEval.Tests" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../AgentEval.Abstractions/AgentEval.Abstractions.csproj" />
    <ProjectReference Include="../AgentEval.Core/AgentEval.Core.csproj" />
  </ItemGroup>

  <!-- Explicit MEAI dependency for IChatClient (MemoryJudge) — matches Core project pattern -->
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.AI" />
  </ItemGroup>
</Project>
```

### 6.4 Integration with Umbrella Package

The umbrella `AgentEval.csproj` adds memory as a sub-project:

```xml
<ProjectReference Include="../AgentEval.Memory/AgentEval.Memory.csproj" PrivateAssets="all" />
```

DI convenience:
```csharp
// In umbrella's DI extensions — AddAgentEvalAll() updated with Memory registration
// NOTE: Existing method already has configure parameter — preserved here
public static IServiceCollection AddAgentEvalAll(
    this IServiceCollection services,
    Action<AgentEvalServiceOptions>? configure = null)
{
    services.AddAgentEval(configure);          // Core
    services.AddAgentEvalDataLoaders();        // DataLoaders
    services.AddAgentEvalRedTeam();            // RedTeam
    services.AddAgentEvalMemory(configure);    // Memory (NEW)
    return services;
}
```

---

## 7. Feature-by-Feature Implementation Plan

### F01: Core Engine 🏗️

**What to build:**
- `MemoryTestScenario` — the test definition model
- `MemoryTestRunner` — the orchestrator that runs scenarios against agents
- `MemoryEvaluationResult` — the result model

**Key design decision:** The runner works with `IEvaluableAgent`, not with MAF types. This means ANY agent (MAF, custom, mock) can be tested.

```csharp
// MemoryTestScenario — the data model
public class MemoryTestScenario
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<MemoryStep> Steps { get; init; }
    public required IReadOnlyList<MemoryQuery> Queries { get; init; }
    public MemoryScenarioOptions? Options { get; init; }
}

public class MemoryStep
{
    public required string Content { get; init; }
    public string? Timestamp { get; init; }          // For temporal scenarios
    public TimeSpan? DelayAfter { get; init; }        // For async memory providers (e.g., Foundry)
}

public class MemoryQuery
{
    public required string Question { get; init; }
    public IReadOnlyList<string>? ExpectedFacts { get; init; }
    public IReadOnlyList<string>? ForbiddenFacts { get; init; }
    public int? AfterStep { get; init; }              // Run after which step?
}

// IMemoryTestRunner — the contract
public interface IMemoryTestRunner
{
    Task<MemoryEvaluationResult> RunAsync(
        IEvaluableAgent agent,
        MemoryTestScenario scenario,
        CancellationToken ct = default);
}

// MemoryEvaluationResult — the output
public class MemoryEvaluationResult
{
    public required string ScenarioName { get; init; }
    public required bool Passed { get; init; }
    public required double OverallScore { get; init; }       // 0-100
    public required double RetentionScore { get; init; }     // 0-100
    public double? TemporalScore { get; init; }              // 0-100, if temporal
    public required IReadOnlyList<MemoryFactResult> FactResults { get; init; }
    public required TimeSpan Duration { get; init; }
}
```

**80/20:** Focus on the happy path. Scenario → Runner → Result. No fancy parallel execution, no streaming. Just serial turn execution + LLM-judge evaluation.

---

### F02: LLM-as-Judge for Memory 🧠

**What to build:**
- `IMemoryJudge` — interface for fact verification
- `MemoryJudge` — implementation using IChatClient

**Key design:** Reuses AgentEval's existing `IEvaluator` / `IChatClient` pattern. The judge receives the agent's response + expected/forbidden facts and returns a score.

```csharp
public interface IMemoryJudge
{
    Task<MemoryFactResult> JudgeAsync(
        string agentResponse,
        MemoryQuery query,
        CancellationToken ct = default);
}

// Implementation uses IChatClient — injected via DI
public class MemoryJudge(IChatClient chatClient) : IMemoryJudge
{
    public async Task<MemoryFactResult> JudgeAsync(
        string agentResponse, MemoryQuery query, CancellationToken ct)
    {
        // Prompt: "Given response X, does it contain facts Y? Forbidden Z?"
        // Returns: structured JSON { found: [...], missing: [...], forbidden_found: [...], score: N }
    }
}
```

**80/20:** Use a simple prompt template with structured output. Don't over-engineer prompt chains or multi-hop verification. One LLM call per query.

---

### F03: CanRememberAsync One-Liner 🎯

**What to build:**
- Extension method on `IEvaluableAgent`

```csharp
public static class CanRememberExtensions
{
    /// <summary>
    /// The simplest memory test: tell the agent a fact, then ask if it remembers.
    /// </summary>
    public static async Task<bool> CanRememberAsync(
        this IEvaluableAgent agent,
        string tell,
        string ask,
        string expect,
        CancellationToken ct = default)
    {
        // 1. Tell the agent the fact
        await agent.InvokeAsync(tell, ct);
        
        // 2. Ask the agent about it
        var response = await agent.InvokeAsync(ask, ct);
        
        // 3. Check if response contains expected fact
        // Simple: case-insensitive contains check
        // Falls back to LLM-judge for semantic matching if DI is available
        return response.Text.Contains(expect, StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Overload with LLM-based semantic matching for fuzzy fact verification.
    /// </summary>
    public static async Task<MemoryFactResult> CanRememberAsync(
        this IEvaluableAgent agent,
        string tell,
        string ask,
        string expect,
        IMemoryJudge judge,
        CancellationToken ct = default)
    {
        await agent.InvokeAsync(tell, ct);
        var response = await agent.InvokeAsync(ask, ct);
        return await judge.JudgeAsync(response.Text, 
            new MemoryQuery { Question = ask, ExpectedFacts = [expect] }, ct);
    }
}
```

**80/20:** The basic overload uses string matching (free, fast, no LLM needed). The advanced overload uses the MemoryJudge for semantic matching. Both work. Start with string matching.

---

### F04: Fluent Memory Assertions ✅

**What to build:**
- `MemoryAssertions` class following our existing assertion pattern
- `MemoryAssertionException` with Expected/Actual/Suggestions structure

```csharp
public static class MemoryAssertionsExtensions
{
    public static MemoryAssertions Should(this MemoryEvaluationResult result)
        => new(result);
}

public class MemoryAssertions
{
    private readonly MemoryEvaluationResult _result;

    [StackTraceHidden]
    public MemoryAssertions HavePassed(string? because = null)
    {
        if (!_result.Passed)
            AgentEvalScope.FailWith(new MemoryAssertionException(/*...*/));
        return this;
    }

    [StackTraceHidden]
    public MemoryAssertions HaveRetentionScoreAbove(double threshold, string? because = null)
    {
        if (_result.RetentionScore < threshold)
            AgentEvalScope.FailWith(/*...*/);
        return this;
    }

    [StackTraceHidden]
    public MemoryAssertions HaveTemporalScoreAbove(double threshold, string? because = null) { /*...*/ }

    [StackTraceHidden]
    public MemoryAssertions HaveFactRecalled(string fact, string? because = null) { /*...*/ }
    
    [StackTraceHidden]
    public MemoryAssertions HaveNoForbiddenFacts(string? because = null) { /*...*/ }

    [StackTraceHidden]
    public MemoryAssertions HaveReachBackDepthAtLeast(int turns, string? because = null) { /*...*/ }
    
    [StackTraceHidden]
    public MemoryAssertions HaveReducerFidelityAbove(double threshold, string? because = null) { /*...*/ }
}
```

**80/20:** Start with 6-8 core assertions. Don't build 30 assertion methods day one. Add them as features demand them.

---

### F05: Temporal Memory Evaluation ⏰

**What to build:**
- `TemporalScenarios` — pre-built temporal test scenarios
- Temporal scoring logic in `MemoryTestRunner`
- `TemporalScore` property on `MemoryEvaluationResult`

```csharp
// Temporal scenario builder
public static class TemporalScenarios
{
    public static MemoryTestScenario VehicleOwnership() => new()
    {
        Name = "Vehicle Ownership Timeline",
        Steps =
        [
            new() { Content = "I just bought a red Ferrari!", Timestamp = "2025-03" },
            new() { Content = "I sold the Ferrari and bought a Honda CBR.", Timestamp = "2025-11" },
        ],
        Queries =
        [
            new() { 
                Question = "What vehicle do I have?", 
                ExpectedFacts = ["Honda CBR"], 
                ForbiddenFacts = ["Ferrari"],
                AfterStep = 1  // After step index 1 (sold Ferrari)
            },
            new() {
                Question = "In March 2025, what did I drive?",
                ExpectedFacts = ["Ferrari"],
                ForbiddenFacts = ["Honda"],
                AfterStep = 1  // Time-travel query from step 1
            },
        ]
    };
}
```

**80/20:** Temporal evaluation is really just `MemoryTestScenario` with `Timestamp` on steps and `AfterStep` on queries. The runner needs to insert queries at the right point in the conversation. No complex temporal reasoning engine — let the LLM-judge evaluate the responses.

---

### F07: Built-in Scenario Library 📚

**What to build:**
- `MemoryScenarios` — static class with 12+ pre-built scenarios

```csharp
public static class MemoryScenarios
{
    // BASIC
    public static MemoryTestScenario BasicRetention(int factCount = 5) => /*...*/;
    public static MemoryTestScenario ProfileBuilding() => /*...*/;
    public static MemoryTestScenario ImplicitFacts() => /*...*/;
    
    // TEMPORAL
    public static MemoryTestScenario FactUpdate() => /*...*/;
    public static MemoryTestScenario TemporalReasoning() => /*...*/;
    public static MemoryTestScenario Contradiction() => /*...*/;
    
    // STRESS
    public static MemoryTestScenario ChattyConversation(
        int factCount = 3, int noiseToFactRatio = 10) => /*...*/;
    public static MemoryTestScenario ReachBack(int depth = 25) => /*...*/;
    public static MemoryTestScenario SafetyCritical() => /*...*/;
    
    // REDUCER
    public static MemoryTestScenario ReducerStress(int turnCount = 30) => /*...*/;
    
    // CROSS-SESSION (placeholder for A-tier F09)
    public static MemoryTestScenario CrossSession() => /*...*/;
    
    // MULTI-TOPIC
    public static MemoryTestScenario MultiTopic(int topicCount = 5) => /*...*/;
}
```

**80/20:** Start with 12 scenarios that cover the 80% use case. Each scenario is a static method returning a `MemoryTestScenario`. Users can customize by modifying or composing scenarios. Don't build a scenario DSL or generator — just good, hand-crafted scenarios.

---

### F10: Chat Reducer Evaluation 🔧

**What to build:**
- `IReducerEvaluator` — interface for measuring reducer information loss
- `ReducerEvaluator` — implementation

```csharp
public interface IReducerEvaluator
{
    Task<ReducerEvaluationResult> EvaluateAsync(
        IEvaluableAgent agentWithReducer,
        MemoryTestScenario scenario,
        CancellationToken ct = default);
}

public class ReducerEvaluationResult
{
    public required double FidelityScore { get; init; }          // 0-100: % facts surviving reduction
    public required int TotalFacts { get; init; }
    public required int FactsRetained { get; init; }
    public required int FactsLost { get; init; }
    public required IReadOnlyList<string> LostFacts { get; init; }
    public required IReadOnlyList<string> RetainedFacts { get; init; }
    public bool CriticalFactLost { get; init; }                   // Safety-critical fact lost?
    public int MessagesBeforeReduction { get; init; }
    public int MessagesAfterReduction { get; init; }
}
```

**How it works:**
1. Run scenario steps (seed facts through conversation)
2. Let the reducer do its work (automatic after each turn)
3. Query each fact — which ones survived?
4. Score: `retained / total = fidelity`
5. Flag: any safety-critical facts lost?

**80/20:** Focus on the measurement. Don't try to evaluate different reducer implementations or suggest optimal settings. Just measure: "you put in 10 facts, the reducer kept 6, fidelity = 60%." That alone is groundbreaking.

---

### F18: Memory Benchmark Suite 🏆

**What to build:**
- `IMemoryBenchmark` — benchmark contract
- `MemoryBenchmarkRunner` — runs all benchmark categories
- `MemoryBenchmark.Standard` / `MemoryBenchmark.Quick` — preset benchmark suites
- `MemoryBenchmarkResult` — comprehensive result

```csharp
public class MemoryBenchmark
{
    public required string Name { get; init; }
    public required IReadOnlyList<MemoryTestScenario> Scenarios { get; init; }
    public required IReadOnlyList<MemoryScoreCategory> Categories { get; init; }

    // Preset benchmarks
    public static MemoryBenchmark Quick => new()
    {
        Name = "Quick Memory Benchmark",
        Scenarios = 
        [
            MemoryScenarios.BasicRetention(factCount: 3),
            MemoryScenarios.FactUpdate(),
            MemoryScenarios.ChattyConversation(factCount: 2, noiseToFactRatio: 5),
        ],
        Categories = [BasicRetention, TemporalReasoning, NoiseResilience]
    };

    public static MemoryBenchmark Standard => new()
    {
        Name = "Standard Memory Benchmark",
        Scenarios =
        [
            MemoryScenarios.BasicRetention(factCount: 5),
            MemoryScenarios.FactUpdate(),
            MemoryScenarios.TemporalReasoning(),
            MemoryScenarios.ChattyConversation(factCount: 3, noiseToFactRatio: 10),
            MemoryScenarios.ReachBack(depth: 25),
            MemoryScenarios.Contradiction(),
            MemoryScenarios.SafetyCritical(),
            MemoryScenarios.MultiTopic(topicCount: 5),
        ],
        Categories = [BasicRetention, TemporalReasoning, NoiseResilience,
                      ReachBack, ContradictionHandling, SafetyCritical, UpdateHandling]
    };
}
```

**80/20:** The benchmark is just a curated list of scenarios from the scenario library (F07) + a runner that aggregates results by category. The value is in the curation and presentation, not complex algorithms.

---

### F26: Chatty Conversation Scenarios 💬

**What to build:**
- Chatty scenario generators in `ChattyScenarios`
- `NoiseGenerators` — reusable noise turn generators
- Configurable noise-to-signal ratio

```csharp
public static class NoiseGenerators
{
    // Built-in noise generators for chatty conversations
    public static INoiseGenerator ChitChat => /*...*/;       // "Nice!", "Tell me more", etc.
    public static INoiseGenerator SmallTalk => /*...*/;       // Weather, sports, casual topics
    public static INoiseGenerator Digressions => /*...*/;     // Topic changes, tangents
    public static INoiseGenerator Emotional => /*...*/;       // "That's amazing!", "So sorry to hear"
}

public interface INoiseGenerator
{
    IReadOnlyList<string> Generate(int count, Random? random = null);
}

// Usage in scenario builder
public static class ChattyScenarios
{
    public static MemoryTestScenario BuriedFacts(
        IReadOnlyList<MemoryFact> facts,
        int noisePerFact = 10,
        INoiseGenerator? noise = null)
    {
        noise ??= NoiseGenerators.ChitChat;
        var steps = new List<MemoryStep>();
        
        foreach (var fact in facts)
        {
            // Add noise turns before the fact
            foreach (var noiseTurn in noise.Generate(noisePerFact))
                steps.Add(new MemoryStep { Content = noiseTurn });
            
            // Add the actual fact
            steps.Add(new MemoryStep { Content = fact.Content });
        }
        
        // Add trailing noise
        foreach (var noiseTurn in noise.Generate(noisePerFact))
            steps.Add(new MemoryStep { Content = noiseTurn });
        
        // Queries: ask about each fact
        var queries = facts.Select(f => new MemoryQuery
        {
            Question = f.QueryTemplate ?? $"What do you remember about {f.Topic}?",
            ExpectedFacts = [f.Content]
        }).ToList();
        
        return new MemoryTestScenario
        {
            Name = $"Chatty Conversation ({facts.Count} facts, {noisePerFact}:1 noise ratio)",
            Steps = steps,
            Queries = queries
        };
    }
}
```

**80/20:** The noise generators are just lists of predefined strings, randomly shuffled. No LLM-generated noise (that's F20, not in scope). 50 pre-written chit-chat lines is plenty for realistic noise generation.

---

### F27: Reach-Back Depth Testing 🎸

**What to build:**
- `IReachBackEvaluator` — interface
- `ReachBackEvaluator` — implementation with parametric depth testing
- `ReachBackResult` — result with degradation curve

```csharp
public interface IReachBackEvaluator
{
    Task<ReachBackResult> EvaluateAsync(
        IEvaluableAgent agent,
        MemoryFact fact,
        string query,
        ReachBackOptions options,
        CancellationToken ct = default);
}

public class ReachBackOptions
{
    public int[] TestDepths { get; init; } = [5, 10, 25, 50, 100];
    public INoiseGenerator? NoiseGenerator { get; init; }
    public double SuccessThreshold { get; init; } = 0.8;
}

public class ReachBackResult
{
    public required int MaxReliableDepth { get; init; }     // Last depth with full recall
    public required int FailurePoint { get; init; }          // First depth with complete failure
    public required IReadOnlyList<ReachBackDepthResult> DepthResults { get; init; }
    public required MemoryFact TestedFact { get; init; }
}

public class ReachBackDepthResult
{
    public required int Depth { get; init; }
    public required double Score { get; init; }       // 0-100
    public required bool Passed { get; init; }
    public required string AgentResponse { get; init; }
}
```

**How the evaluator works:**
```
For each depth in TestDepths:
  1. Create fresh agent session
  2. Send the fact message
  3. Send `depth` noise messages
  4. Send the query message
  5. Judge: does the response contain the fact?
  6. Record: depth → score

Return: sorted results, max reliable depth, failure point
```

**80/20:** Test 5 preset depths [5, 10, 25, 50, 100]. Don't do binary search for the exact failure point — the 5 preset levels give enough signal. Each depth requires a fresh session (new agent invocation), so we keep the number small for cost/time reasons.

---

## 8. Abstractions & Shared Contracts

### What Goes in `AgentEval.Abstractions`

Only **interfaces**, **models**, and **enums** that are shared across modules:

```csharp
// Interfaces (in AgentEval.Abstractions/Memory/)
public interface IMemoryTestRunner { ... }
public interface IMemoryJudge { ... }
public interface IMemoryBenchmark { ... }
public interface IReducerEvaluator { ... }
public interface IReachBackEvaluator { ... }
public interface ICrossSessionEvaluator { ... }
public interface INoiseGenerator { ... }
public interface ISessionResettableAgent : IEvaluableAgent { ... }  // NEW!

// Models (in AgentEval.Abstractions/Memory/Models/)
public class MemoryTestScenario { ... }
public class MemoryStep { ... }
public class MemoryQuery { ... }
public class MemoryFact { ... }
public class MemoryEvaluationResult { ... }
public class MemoryFactResult { ... }
public class MemoryBenchmarkResult { ... }
public class ReachBackResult { ... }
public class ReducerEvaluationResult { ... }
public class CrossSessionResult { ... }
public class CrossSessionScenario { ... }

// Enums
public enum MemoryScoreCategory
{
    BasicRetention,
    TemporalReasoning,
    NoiseResilience,
    ReachBack,
    ContradictionHandling,
    UpdateHandling,
    SafetyCritical,
    ReducerFidelity,
    CrossSession,
    ScopeMisconfiguration    // MAF-only, scored from AgentEval.MAF
}
```

### What Stays in `AgentEval.Memory`

Everything that has **implementation logic** — the runner, judge, evaluators (including cross-session), scenarios, assertions, metrics, DI registration.

### Why This Split?

1. **Other modules can reference the models.** DataLoaders could export `MemoryBenchmarkResult`. RedTeam could use `MemoryTestScenario` for security scenarios.
2. **Users can implement their own `IMemoryJudge`** without depending on `AgentEval.Memory`.
3. **`ISessionResettableAgent`** can be implemented by ANY adapter (Core, MAF, custom) without depending on Memory.
4. **Follows the existing pattern** — `IMetric` is in Abstractions, `FaithfulnessMetric` is in Core.

---

## 9. Core Adjustments Needed

### 9.1 IEvaluableAgent — No Changes Needed ✅

`IEvaluableAgent.InvokeAsync(string prompt, CancellationToken ct)` is sufficient. Memory evaluation uses sequential `InvokeAsync` calls within a conversation. The agent manages its own session state internally.

### 9.2 MetricResult — No Changes Needed ✅

`MetricResult` with `Score`, `Explanation`, `SubMetrics` already handles our needs. Memory metrics return `MetricResult` like any other metric.

### 9.3 AgentEvalServiceCollectionExtensions — Extended

```csharp
// Add to umbrella convenience method
public static IServiceCollection AddAgentEvalAll(this IServiceCollection services)
{
    services.AddAgentEval();
    services.AddAgentEvalDataLoaders();
    services.AddAgentEvalRedTeam();
    services.AddAgentEvalMemory();     // NEW
    return services;
}
```

### 9.4 IEvaluableAgent — Conversation Statefulness + ISessionResettableAgent

The current `IEvaluableAgent` interface is stateless per call. For memory evaluation, we need two capabilities:

**Within a session: Agent manages state internally (SELECTED)**
The agent implementation (e.g., `MAFAgentAdapter`, `ChatClientAgentAdapter`) maintains its session across `InvokeAsync` calls. Both adapters already do this.

**Across sessions: NEW `ISessionResettableAgent` interface (SELECTED)**
```csharp
// NEW interface — see Section 4.5 for full design rationale
public interface ISessionResettableAgent : IEvaluableAgent
{
    Task ResetSessionAsync(CancellationToken cancellationToken = default);
}
```

**Decision: Both approaches, layered.**
- **Within a session:** `IEvaluableAgent.InvokeAsync()` calls accumulate state naturally.
- **Across sessions:** `ISessionResettableAgent.ResetSessionAsync()` clears conversation context while preserving persistent memory.
- **Graceful degradation:** When agent doesn't implement `ISessionResettableAgent`, cross-session tests are skipped with a helpful warning message.

Both `ChatClientAgentAdapter` (MEAI) and `MAFAgentAdapter` (MAF) already have the reset capability — they just need to implement the new interface. See Section 9.6 for the minimal code changes.

### 9.5 New IMemoryMetric — Extends IMetric

> **NOTE:** See also Section 4.5 for the new `ISessionResettableAgent` interface
> and Section 9.6 for the required updates to existing adapters.

```csharp
/// <summary>
/// Marker interface for memory-specific metrics.
/// Follows the IRAGMetric / IAgenticMetric pattern.
/// Note: LLM vs code-computed is already conveyed by the naming prefix (llm_ / code_).
/// </summary>
public interface IMemoryMetric : IMetric
{
    /// <summary>
    /// Whether this metric requires the agent to implement ISessionResettableAgent
    /// for cross-session evaluation capabilities.
    /// </summary>
    bool RequiresSessionReset { get; }
}
```

This follows the `IRAGMetric` / `IAgenticMetric` pattern. Domain-specific properties
(`RequiresContext`, `RequiresGroundTruth`, `RequiresToolUsage`) are preferred over generic
`RequiresLLM` — the naming prefix already encodes cost tier (KISS principle).

### 9.6 NEW: Existing Adapter Updates for ISessionResettableAgent

These are minimal, non-breaking changes to existing adapters:

```csharp
// ChatClientAgentAdapter — add interface implementation
// File: src/AgentEval.Core/Core/ChatClientAgentAdapter.cs
public class ChatClientAgentAdapter : IStreamableAgent, ISessionResettableAgent  // ← add interface
{
    // Existing ClearHistory() method already does the right thing
    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        ClearHistory();  // Already exists!
        return Task.CompletedTask;
    }
}

// MAFAgentAdapter — add interface implementation  
// File: src/AgentEval.MAF/MAF/MAFAgentAdapter.cs
public class MAFAgentAdapter : IStreamableAgent, ISessionResettableAgent  // ← add interface
{
    // Existing ResetSessionAsync already has the right signature!
    // Just need to ensure it implements the interface.
    // The method already exists: creates new AgentSession via _agent.CreateSessionAsync()
}
```

**Impact:** Both changes are additive (new interface on existing classes). Zero breaking changes. Existing code continues to work. Memory evaluation features just light up automatically.

---

## 10. Pareto Analysis — 80/20 for Each Feature

### The Golden Rule

For each feature, focus on the **minimum implementation that delivers maximum insight**. We can always add depth later. Better to ship 10 features at 80% than 3 features at 100%.

| Feature | 80% Value Implementation | Deferred 20% |
|---------|-------------------------|---------------|
| **F01** Core Engine | Scenario model + serial runner + result model | Parallel execution, streaming, retry |
| **F02** LLM-as-Judge | Single prompt, JSON response, one LLM call per query | Multi-hop verification, confidence calibration |
| **F03** CanRemember | String.Contains check + LLM judge overload | Semantic similarity fallback, fuzzy matching |
| **F04** Assertions | 8 core assertion methods with AgentEvalScope | Chained complex assertions, custom predicates |
| **F05** Temporal | Timestamp on steps, AfterStep on queries, temporal score | Time-travel queries, temporal reasoning engine |
| **F07** Scenarios | 12 hand-crafted static scenarios | Dynamic generation, parameterized templates |
| **F08** Scope Misconfig | Static analysis of MAF scope settings | Deep provider chain analysis, remediation auto-fix |
| **F09** Cross-Session | Reset session → query facts → check recall | Cross-provider session, distributed session stores |
| **F10** Reducer | Run scenario → query all facts → count retention | Token counting, per-token fidelity, optimal budget recommendation |
| **F18** Benchmark | Aggregate scores from scenario library by category | Historical tracking, CI integration, HTML reports |
| **F26** Chatty | 50 pre-written noise messages, shuffled as noise | LLM-generated noise, personality-specific chat styles |
| **F27** Reach-Back | 5 preset depths, fresh session per depth | Binary search for exact failure point, multi-fact interleaving |

---

## 11. Implementation Phases & Order

### Phase 0: Foundation (Core Engine + Judge + Models)

```
PHASE 0: THE FOUNDATION
=========================

Build order:
  1. Models (Abstractions): MemoryTestScenario, MemoryStep, MemoryQuery,
     MemoryFact, MemoryEvaluationResult, MemoryFactResult
  2. ISessionResettableAgent (Abstractions) — NEW interface
  3. IMemoryJudge (Abstractions) + MemoryJudge (Memory)
  4. IMemoryTestRunner (Abstractions) + MemoryTestRunner (Memory)
  5. CanRememberExtensions (Memory)
  6. MemoryAssertions (Memory) — basic set
  7. DI Registration: AddAgentEvalMemory()
  8. ChatClientAgentAdapter: implement ISessionResettableAgent (Core)
  9. MAFAgentAdapter: implement ISessionResettableAgent (MAF)
  10. Unit tests with FakeChatClient

Features delivered: F01, F02, F03, F04 (partial)
Tests needed: ~25 unit tests using FakeChatClient
```

### Phase 1: Temporal + Scenarios + Noise

```
PHASE 1: SCENARIO POWER
=========================

Build order:
  1. INoiseGenerator (Abstractions) + NoiseGenerators (Memory)
  2. TemporalScenarios (Memory)
  3. ChattyScenarios (Memory)
  4. MemoryScenarios — full library (Memory)
  5. Temporal scoring logic in MemoryTestRunner
  6. Additional assertions for temporal + noise

Features delivered: F05, F07, F26
Tests needed: ~15 unit tests
```

### Phase 2: Reach-Back + Reducer + Cross-Session + Scope + Benchmark

```
PHASE 2: ADVANCED EVALUATION
==============================

Build order:
  1. IReachBackEvaluator + ReachBackEvaluator + ReachBackResult
  2. IReducerEvaluator + ReducerEvaluator + ReducerEvaluationResult
  3. Cross-session test scenarios + runner support (F09)
  4. ScopeMisconfigurationDetector in AgentEval.MAF (F08)
  5. Memory metrics: MemoryRetentionMetric, MemoryTemporalMetric,
     MemoryReachBackMetric, MemoryReducerFidelityMetric,
     code_memory_scope_misconfig (MAF)
  6. IMemoryBenchmark + MemoryBenchmarkRunner + MemoryBenchmarks
  7. Full assertion set (including cross-session + scope assertions)
  8. Benchmark presentation (can reuse existing exporters)

Features delivered: F08, F09, F10, F18, F27
Tests needed: ~25 unit tests
```

### Phase 3: Integration + Samples + Polish

```
PHASE 3: SHIP IT! 🎸
=====================

Build order:
  1. Integrate into umbrella package (AgentEval.csproj)
  2. Update AddAgentEvalAll()
  3. Create samples: Sample_MemoryEvaluation_HelloWorld,
     Sample_MemoryBenchmark, Sample_ReachBackTest
  4. Documentation
  5. Integration tests (with real LLM, optional)

Features delivered: Polish on all 12 features
Tests needed: ~10 integration tests (use FakeChatClient for CI)
```

---

## 12. The Grand Architecture — How It All Fits Together 🎸

This is the complete architectural view showing how every component connects:

```
┌────────────────────────────────────────────────────────────────────────────────────┐
│                                                                                    │
│                     ╔═══════════════════════════════════════╗                       │
│                     ║   AgentEval Memory Evaluation Module  ║                       │
│                     ║   "Does your agent remember?"        ║                       │
│                     ╚═══════════════════════════════════════╝                       │
│                                                                                    │
│  ╔════════════════════════════════════════════════════════════════════════════════╗  │
│  ║  LAYER 1: USER-FACING API (How developers interact)                          ║  │
│  ║                                                                               ║  │
│  ║  ┌─────────────────────────┐  ┌──────────────────────────────────────────┐   ║  │
│  ║  │ ONE-LINERS (F03)        │  │ FLUENT ASSERTIONS (F04)                  │   ║  │
│  ║  │                         │  │                                          │   ║  │
│  ║  │ agent.CanRememberAsync  │  │ result.Should()                         │   ║  │
│  ║  │  (tell, ask, expect)    │  │   .HavePassed()                         │   ║  │
│  ║  │                         │  │   .HaveRetentionScoreAbove(90)          │   ║  │
│  ║  │                         │  │   .HaveTemporalScoreAbove(70)           │   ║  │
│  ║  │                         │  │   .HaveReachBackDepthAtLeast(25)        │   ║  │
│  ║  └────────────┬────────────┘  └────────────────┬─────────────────────────┘   ║  │
│  ║               │                                 │                             ║  │
│  ╚═══════════════╪═════════════════════════════════╪═════════════════════════════╝  │
│                  │                                 │                                │
│  ╔═══════════════╪═════════════════════════════════╪═════════════════════════════╗  │
│  ║  LAYER 2: SCENARIOS & BENCHMARKS (What to test)                              ║  │
│  ║               │                                 │                             ║  │
│  ║  ┌────────────▼────────────────────────────────────────────────────────────┐  ║  │
│  ║  │ SCENARIO LIBRARY (F07)                                                  │  ║  │
│  ║  │                                                                         │  ║  │
│  ║  │ MemoryScenarios.BasicRetention    TemporalScenarios.VehicleOwnership   │  ║  │
│  ║  │ MemoryScenarios.FactUpdate        TemporalScenarios.MedicalHistory     │  ║  │
│  ║  │ MemoryScenarios.MultiTopic        ChattyScenarios.BuriedFacts          │  ║  │
│  ║  │ MemoryScenarios.SafetyCritical    ChattyScenarios.SmallTalkHeavy       │  ║  │
│  ║  └────────────┬────────────────────────────────────────────────────────────┘  ║  │
│  ║               │                                                               ║  │
│  ║  ┌────────────▼──────────────┐  ┌──────────────────────────────────────────┐  ║  │
│  ║  │ NOISE GENERATORS (F26)    │  │ BENCHMARKS (F18)                         │  ║  │
│  ║  │                           │  │                                          │  ║  │
│  ║  │ NoiseGenerators.ChitChat  │  │ MemoryBenchmark.Quick   (3 scenarios)   │  ║  │
│  ║  │ NoiseGenerators.SmallTalk │  │ MemoryBenchmark.Standard (8 scenarios)  │  ║  │
│  ║  │ NoiseGenerators.Digress   │  │ MemoryBenchmark.Full    (15 scenarios)  │  ║  │
│  ║  └────────────┬──────────────┘  └─────────────────┬────────────────────────┘  ║  │
│  ║               │                                    │                           ║  │
│  ╚═══════════════╪════════════════════════════════════╪═══════════════════════════╝  │
│                  │                                    │                              │
│  ╔═══════════════╪════════════════════════════════════╪═══════════════════════════╗  │
│  ║  LAYER 3: EVALUATION ENGINE (How we evaluate)                                 ║  │
│  ║               │                                    │                           ║  │
│  ║  ┌────────────▼────────────┐  ┌───────────────────▼────────────────────────┐  ║  │
│  ║  │ MEMORY TEST RUNNER (F01)│  │ MEMORY JUDGE (F02)                         │  ║  │
│  ║  │                         │  │                                            │  ║  │
│  ║  │ 1. Create agent session │  │ "Given this response, does it contain      │  ║  │
│  ║  │ 2. Execute steps        │  │  the expected facts and NOT contain        │  ║  │
│  ║  │ 3. At query points:     │──│  the forbidden facts?"                     │  ║  │
│  ║  │    ask → judge → score  │  │                                            │  ║  │
│  ║  │ 4. Aggregate results    │  │  Uses: IChatClient (via DI)               │  ║  │
│  ║  │                         │  │  returns: MemoryFactResult                 │  ║  │
│  ║  └─────────────────────────┘  └────────────────────────────────────────────┘  ║  │
│  ║                                                                               ║  │
│  ║  ┌─────────────────────────┐  ┌────────────────────────────────────────────┐  ║  │
│  ║  │ REACH-BACK EVAL (F27)   │  │ REDUCER EVALUATOR (F10)                    │  ║  │
│  ║  │                         │  │                                            │  ║  │
│  ║  │ For each depth level:   │  │ 1. Run scenario (seed facts)              │  ║  │
│  ║  │  1. Fresh session       │  │ 2. Reducer compresses history              │  ║  │
│  ║  │  2. Plant fact          │  │ 3. Query each fact                         │  ║  │
│  ║  │  3. N noise turns       │  │ 4. Score: retained / total = fidelity     │  ║  │
│  ║  │  4. Query fact          │  │ 5. Flag critical losses                   │  ║  │
│  ║  │  5. Judge response      │  │                                            │  ║  │
│  ║  │                         │  │ Key insight: measures what the reducer     │  ║  │
│  ║  │ Result: degradation     │  │ DESTROYS, not what it keeps               │  ║  │
│  ║  │ curve + max depth       │  │                                            │  ║  │
│  ║  └─────────────────────────┘  └────────────────────────────────────────────┘  ║  │
│  ║                                                                               ║  │
│  ║  ┌────────────────────────────────────────────────────────────────────────┐   ║  │
│  ║  │ BENCHMARK RUNNER (F18)                                                 │   ║  │
│  ║  │                                                                        │   ║  │
│  ║  │  Uses: MemoryTestRunner + MemoryJudge + ReachBackEvaluator +           │   ║  │
│  ║  │        ReducerEvaluator + all scenarios                                │   ║  │
│  ║  │                                                                        │   ║  │
│  ║  │  Aggregates results by category → MemoryBenchmarkResult                │   ║  │
│  ║  │  Categories: Retention, Temporal, Noise, ReachBack, Reducer, etc.      │   ║  │
│  ║  └────────────────────────────────────────────────────────────────────────┘   ║  │
│  ║                                                                               ║  │
│  ╚═══════════════════════════════════════════════════════════════════════════════╝  │
│                                                                                    │
│  ╔═══════════════════════════════════════════════════════════════════════════════╗  │
│  ║  LAYER 4: METRICS (IMetric implementations, registered in DI)                ║  │
│  ║                                                                               ║  │
│  ║  llm_memory_retention      code_memory_reachback      code_memory_reducer     ║  │
│  ║  llm_memory_temporal       llm_memory_noise_resilience                        ║  │
│  ║                                                                               ║  │
│  ╚═══════════════════════════════════════════════════════════════════════════════╝  │
│                                                                                    │
│  ╔═══════════════════════════════════════════════════════════════════════════════╗  │
│  ║  LAYER 5: INFRASTRUCTURE (Existing AgentEval — reused, not modified)         ║  │
│  ║                                                                               ║  │
│  ║  IEvaluableAgent │ IChatClient (judge) │ FakeChatClient │ DI │ Exporters     ║  │
│  ║  AgentEvalScope  │ MetricResult        │ TestResult     │ AgentEvalBuilder    ║  │
│  ║                                                                               ║  │
│  ╚═══════════════════════════════════════════════════════════════════════════════╝  │
│                                                                                    │
│  ╔═══════════════════════════════════════════════════════════════════════════════╗  │
│  ║  EXTERNAL: Agent Under Test (user provides)                                   ║  │
│  ║                                                                               ║  │
│  ║  Any IEvaluableAgent:                                                         ║  │
│  ║    MAFAgentAdapter(agent)  │  Custom agent  │  TraceReplayingAgent             ║  │
│  ║                                                                               ║  │
│  ║  With any memory provider:                                                    ║  │
│  ║    InMemoryChatHistory + Mem0  │  VectorStore  │  Foundry  │  Custom           ║  │
│  ║                                                                               ║  │
│  ║  WE DON'T CARE WHAT'S INSIDE. WE TEST THE BEHAVIOR.                          ║  │
│  ╚═══════════════════════════════════════════════════════════════════════════════╝  │
│                                                                                    │
└────────────────────────────────────────────────────────────────────────────────────┘
```

### How Components Flow Together

```
USER WRITES:

    var result = await runner.RunMemoryBenchmarkAsync(agent, MemoryBenchmark.Standard);
    result.Should().HaveOverallScoreAbove(80);

INTERNALLY:

    MemoryBenchmarkRunner
      │
      ├── For each scenario in MemoryBenchmark.Standard:
      │     │
      │     ├── MemoryTestRunner.RunAsync(agent, scenario)
      │     │     │
      │     │     ├── agent.InvokeAsync("I'm allergic to peanuts")
      │     │     ├── agent.InvokeAsync("Nice weather today")
      │     │     ├── agent.InvokeAsync("What allergies should you know about?")
      │     │     │     │
      │     │     │     └── MemoryJudge.JudgeAsync(response, expectedFacts)
      │     │     │           │
      │     │     │           └── IChatClient (LLM) → { found: ["peanuts"], score: 95 }
      │     │     │
      │     │     └── MemoryEvaluationResult { Score: 95, Passed: true }
      │     │
      │     └── (next scenario...)
      │
      ├── ReachBackEvaluator.EvaluateAsync(agent, fact, query, depths)
      │     │
      │     ├── depth=5:  fresh session → plant → 5 noise → query → judge → ✅
      │     ├── depth=10: fresh session → plant → 10 noise → query → judge → ✅
      │     ├── depth=25: fresh session → plant → 25 noise → query → judge → ⚠️
      │     └── ReachBackResult { MaxDepth: 10, FailurePoint: 25 }
      │
      └── Aggregate all results by category
            │
            └── MemoryBenchmarkResult
                  │ BasicRetention:  95/100
                  │ TemporalReasoning: 70/100
                  │ NoiseResilience:   85/100
                  │ ReachBack:         40/100
                  │ Overall:           72/100
                  │
                  └── .Should().HaveOverallScoreAbove(80)  → FAILS! (72 < 80)
                        │
                        └── MemoryAssertionException:
                              Expected: overall score above 80
                              Actual: 72
                              Suggestions:
                                - Improve reach-back depth (currently 10 turns)
                                - Consider semantic memory provider for longer recall
                                - Temporal reasoning score is borderline (70)
```

### DI Registration

```csharp
public static class MemoryServiceCollectionExtensions
{
    public static IServiceCollection AddAgentEvalMemory(
        this IServiceCollection services,
        Action<AgentEvalServiceOptions>? configure = null)
    {
        // Ensure core is registered (brings in IChatClient, IStatisticsCalculator, etc.)
        services.AddAgentEval(configure);
        
        var options = new AgentEvalServiceOptions();
        configure?.Invoke(options);
        
        // Singletons (stateless — safe regardless of configured lifetime)
        services.TryAddSingleton<INoiseGenerator>(NoiseGenerators.ChitChat);
        
        // Lifetime-aware registrations — follows the same pattern as AddAgentEval()
        // to avoid captive dependency problems (e.g., singleton holding scoped IChatClient)
        switch (options.ServiceLifetime)
        {
            case ServiceLifetime.Singleton:
                services.TryAddSingleton<IMemoryJudge>(sp =>
                    new MemoryJudge(sp.GetRequiredService<IChatClient>()));
                services.TryAddSingleton<IMemoryTestRunner>(sp =>
                    new MemoryTestRunner(sp.GetRequiredService<IMemoryJudge>()));
                services.TryAddSingleton<IReachBackEvaluator>(sp =>
                    new ReachBackEvaluator(
                        sp.GetRequiredService<IMemoryTestRunner>(),
                        sp.GetRequiredService<INoiseGenerator>()));
                services.TryAddSingleton<IReducerEvaluator>(sp =>
                    new ReducerEvaluator(sp.GetRequiredService<IMemoryTestRunner>()));
                break;
            case ServiceLifetime.Scoped:
                services.TryAddScoped<IMemoryJudge>(sp =>
                    new MemoryJudge(sp.GetRequiredService<IChatClient>()));
                services.TryAddScoped<IMemoryTestRunner>(sp =>
                    new MemoryTestRunner(sp.GetRequiredService<IMemoryJudge>()));
                services.TryAddScoped<IReachBackEvaluator>(sp =>
                    new ReachBackEvaluator(
                        sp.GetRequiredService<IMemoryTestRunner>(),
                        sp.GetRequiredService<INoiseGenerator>()));
                services.TryAddScoped<IReducerEvaluator>(sp =>
                    new ReducerEvaluator(sp.GetRequiredService<IMemoryTestRunner>()));
                break;
            case ServiceLifetime.Transient:
                services.TryAddTransient<IMemoryJudge>(sp =>
                    new MemoryJudge(sp.GetRequiredService<IChatClient>()));
                services.TryAddTransient<IMemoryTestRunner>(sp =>
                    new MemoryTestRunner(sp.GetRequiredService<IMemoryJudge>()));
                services.TryAddTransient<IReachBackEvaluator>(sp =>
                    new ReachBackEvaluator(
                        sp.GetRequiredService<IMemoryTestRunner>(),
                        sp.GetRequiredService<INoiseGenerator>()));
                services.TryAddTransient<IReducerEvaluator>(sp =>
                    new ReducerEvaluator(sp.GetRequiredService<IMemoryTestRunner>()));
                break;
        }
        
        // Register memory metrics (always singletons — metrics are stateless evaluators)
        services.AddSingleton<IMetric>(sp =>
            new MemoryRetentionMetric(sp.GetRequiredService<IMemoryTestRunner>()));
        services.AddSingleton<IMetric>(sp =>
            new MemoryTemporalMetric(sp.GetRequiredService<IMemoryTestRunner>()));
            
        return services;
    }
}
```

---

## 13. F08 & F09 — Full Feature Implementation Plans

> **Status:** PROMOTED to S-Tier (MUST HAVE) — approved by project lead

### F08: Scope Misconfiguration Detection 🔍

**What:** Static analysis that catches the #1 MAF memory configuration bug — accidental over-isolation through scope misconfiguration.

**Where it lives:** `AgentEval.MAF/Memory/` (MAF-specific — needs `StorageScope`, `SearchScope` types)

```
THE PROBLEM F08 CATCHES
=========================

MAF memory providers use a dual-scope system:

  StorageScope: WHERE to store memories
  ┌──────────────────────────────────────────────────┐
  │  { UserId: "jose", SessionId: "abc123" }         │
  │  { UserId: "jose" }                              │  ← Correct for cross-session
  │  { UserId: "jose", AgentId: "support-bot" }      │
  └──────────────────────────────────────────────────┘

  SearchScope: WHERE to search for memories
  ┌──────────────────────────────────────────────────┐
  │  { UserId: "jose", SessionId: "abc123" }         │  ← BUG! Over-isolated!
  │  { UserId: "jose" }                              │  ← Correct for cross-session
  └──────────────────────────────────────────────────┘

  THE BUG: Developer sets SearchScope with SessionId
  ════════════════════════════════════════════════════
  
  Session 1: Store("I live in Copenhagen")  → StorageScope = { UserId: "jose" }
  Session 2: Search("Where does José live?") → SearchScope = { UserId: "jose", 
                                                                SessionId: "def456" }
  
  RESULT: ❌ Memory not found! Different SessionId in search scope
          means it won't find memories from session 1.
  
  This is NOT a bug in MAF — scope isolation works correctly.
  It's a CONFIGURATION BUG by the developer.
  
  F08 catches this BEFORE you waste hours debugging.
```

**Implementation:**

```csharp
// namespace AgentEval.MAF.Memory
// Lives in AgentEval.MAF project — needs Microsoft.Agents.AI types

/// <summary>
/// Analyzes MAF agent memory provider configuration for common misconfigurations.
/// This is a code_ metric — no LLM needed, instant results.
/// </summary>
public class ScopeMisconfigurationDetector
{
    /// <summary>
    /// Analyzes the agent's memory providers and returns scope misconfiguration warnings.
    /// </summary>
    public ScopeMisconfigurationResult Analyze(AIAgent agent)
    {
        var warnings = new List<ScopeWarning>();
        
        // Get all AIContextProvider instances from the agent
        foreach (var provider in GetMemoryProviders(agent))
        {
            if (provider is ChatHistoryMemoryProvider chatProvider)
            {
                var state = GetProviderState(chatProvider);
                AnalyzeChatHistoryScopes(state.StorageScope, state.SearchScope, warnings);
            }
            else if (provider is FoundryMemoryProvider foundryProvider)
            {
                var state = GetProviderState(foundryProvider);
                AnalyzeFoundryScopes(state.StorageScope, state.SearchScope, warnings);
            }
        }
        
        return new ScopeMisconfigurationResult
        {
            Warnings = warnings,
            HasMisconfigurations = warnings.Any(),
            SeverityLevel = CalculateSeverity(warnings)
        };
    }
    
    private void AnalyzeChatHistoryScopes(ChatHistoryMemoryProviderScope storageScope, 
                                          ChatHistoryMemoryProviderScope searchScope, 
                                          List<ScopeWarning> warnings)
    {
        // Check for over-isolation: SearchScope more restrictive than StorageScope
        if (IsMoreRestrictive(searchScope, storageScope))
        {
            warnings.Add(new ScopeWarning
            {
                Severity = ScopeWarningSeverity.High,
                Type = ScopeWarningType.OverIsolation,
                Message = "SearchScope is more restrictive than StorageScope",
                Explanation = "Agent will store memories but won't find them in searches.",
                Suggestions = [
                    "Remove unnecessary fields from SearchScope (e.g., SessionId for cross-session memory)",
                    "Ensure SearchScope fields are subset of StorageScope fields"
                ],
                ExpectedBehavior = "Cross-session memory lookup should work",
                ActualConfiguration = $"Storage: {FormatScope(storageScope)}, Search: {FormatScope(searchScope)}"
            });
        }
        
        // Check for accidental session isolation
        if (!string.IsNullOrEmpty(searchScope.SessionId) && string.IsNullOrEmpty(storageScope.SessionId))
        {
            warnings.Add(new ScopeWarning
            {
                Severity = ScopeWarningSeverity.Critical,
                Type = ScopeWarningType.SessionIsolation,
                Message = "SearchScope includes SessionId but StorageScope doesn't",
                Explanation = "Each session will only see its own memories, breaking cross-session memory.",
                Suggestions = [
                    "Remove SessionId from SearchScope for cross-session memory",
                    "Add SessionId to StorageScope if you want session isolation"
                ]
            });
        }
        
        // Check for insufficient isolation (security concern)
        if (string.IsNullOrEmpty(searchScope.UserId) && !string.IsNullOrEmpty(storageScope.UserId))
        {
            warnings.Add(new ScopeWarning
            {
                Severity = ScopeWarningSeverity.Medium,
                Type = ScopeWarningType.UnderIsolation,
                Message = "SearchScope missing UserId isolation",
                Explanation = "Agent might access other users' memories (privacy/security risk).",
                Suggestions = ["Add UserId to SearchScope for proper user isolation"]
            });
        }
    }
}

public class ScopeMisconfigurationResult
{
    public required IReadOnlyList<ScopeWarning> Warnings { get; init; }
    public required bool HasMisconfigurations { get; init; }
    public required ScopeWarningSeverity SeverityLevel { get; init; }
    public required TimeSpan AnalysisDuration { get; init; }
}

public class ScopeWarning
{
    public required ScopeWarningSeverity Severity { get; init; }
    public required ScopeWarningType Type { get; init; }
    public required string Message { get; init; }
    public required string Explanation { get; init; }
    public required IReadOnlyList<string> Suggestions { get; init; }
    public string? ExpectedBehavior { get; init; }
    public string? ActualConfiguration { get; init; }
}

public enum ScopeWarningSeverity { Low, Medium, High, Critical }
public enum ScopeWarningType { OverIsolation, UnderIsolation, SessionIsolation, AgentIsolation }
```

**Assertions for F08:**
```csharp
public static class ScopeAssertionsExtensions
{
    public static ScopeAssertions Should(this ScopeMisconfigurationResult result)
        => new(result);
}

public class ScopeAssertions
{
    [StackTraceHidden]
    public ScopeAssertions HaveNoMisconfigurations(string? because = null)
    {
        if (_result.HasMisconfigurations)
            AgentEvalScope.FailWith(new ScopeAssertionException(/*...*/));
        return this;
    }

    [StackTraceHidden]
    public ScopeAssertions HaveNoWarningsAbove(ScopeWarningSeverity severity, string? because = null) { /*...*/ }
    
    [StackTraceHidden]
    public ScopeAssertions HaveNoCrossSessionIssues(string? because = null) { /*...*/ }
}
```

---

### F09: Cross-Session Persistence 💾

**What:** Tests whether an agent remembers facts across fresh conversation sessions — the ultimate memory test.

**Where it lives:** `AgentEval.Memory` (universal — depends on `ISessionResettableAgent`)

```
THE CROSS-SESSION MEMORY TEST
==============================

SESSION 1                          SESSION 2 (fresh session)
─────────────────────────────────   ─────────────────────────────────
Agent: "Hi, I'm your assistant"     Agent: "Hi, I'm your assistant"
User:  "I'm allergic to peanuts"    User:  "Suggest a restaurant"
Agent: "I'll remember that"         Agent: "I recommend... no peanuts"
                                              ▲
                                              │
                                    ┌─────────┴────────┐
                                    │ PERSISTENT MEMORY │
                                    │ (survived reset)  │
                                    └──────────────────┘

TEST FLOW:
  1. Tell agent facts in session 1
  2. Reset session (ISessionResettableAgent.ResetSessionAsync)
  3. Query facts in session 2
  4. Judge: are facts still recalled?

MEMORY TYPES TESTED:
  ✅ Long-term memory (vector stores, Mem0, Foundry)
  ❌ Short-term memory (chat history — should be cleared)

This differentiates agents with REAL persistent memory
from agents that only keep chat history.
```

**Implementation:**

```csharp
// namespace AgentEval.Memory.Evaluators
// Lives in AgentEval.Memory project — framework universal

public interface ICrossSessionEvaluator
{
    Task<CrossSessionResult> EvaluateAsync(
        IEvaluableAgent agent,
        CrossSessionScenario scenario,
        CancellationToken ct = default);
}

public class CrossSessionEvaluator(IMemoryTestRunner runner, IMemoryJudge judge) : ICrossSessionEvaluator
{
    public async Task<CrossSessionResult> EvaluateAsync(
        IEvaluableAgent agent,
        CrossSessionScenario scenario,
        CancellationToken ct = default)
    {
        var results = new List<CrossSessionFactResult>();
        
        // SESSION 1: Plant facts
        foreach (var fact in scenario.FactsToPlant)
        {
            await agent.InvokeAsync(fact.Content, ct);
        }
        
        // RESET: Clear conversation state, preserve persistent memory
        if (agent is ISessionResettableAgent resettable)
        {
            await resettable.ResetSessionAsync(ct);
        }
        else
        {
            return new CrossSessionResult
            {
                ScenarioName = scenario.Name,
                Passed = false,
                ErrorMessage = "Agent does not implement ISessionResettableAgent",
                FactResults = []
            };
        }
        
        // SESSION 2: Query facts
        foreach (var query in scenario.Queries)
        {
            var response = await agent.InvokeAsync(query.Question, ct);
            var factResult = await judge.JudgeAsync(response.Text, query, ct);
            
            results.Add(new CrossSessionFactResult
            {
                Fact = query.ExpectedFacts?.FirstOrDefault() ?? "Unknown",
                Query = query.Question,
                Response = response.Text,
                Recalled = factResult.Score > 0.8,
                Score = factResult.Score
            });
        }
        
        var passRate = results.Count(r => r.Recalled) / (double)results.Count;
        
        return new CrossSessionResult
        {
            ScenarioName = scenario.Name,
            Passed = passRate >= scenario.SuccessThreshold,
            OverallScore = passRate * 100,
            FactResults = results,
            SessionResetSupported = true
        };
    }
}

public class CrossSessionScenario
{
    public required string Name { get; init; }
    public required IReadOnlyList<MemoryFact> FactsToPlant { get; init; }
    public required IReadOnlyList<MemoryQuery> Queries { get; init; }
    public double SuccessThreshold { get; init; } = 0.8;  // 80% facts must survive
}

public class CrossSessionResult
{
    public required string ScenarioName { get; init; }
    public required bool Passed { get; init; }
    public required double OverallScore { get; init; }
    public required IReadOnlyList<CrossSessionFactResult> FactResults { get; init; }
    public required bool SessionResetSupported { get; init; }
    public string? ErrorMessage { get; init; }
}
```

**Built-in Cross-Session Scenarios:**

```csharp
public static class CrossSessionScenarios
{
    public static CrossSessionScenario UserProfile() => new()
    {
        Name = "User Profile Cross-Session",
        FactsToPlant =
        [
            new() { Content = "My name is José", Topic = "name" },
            new() { Content = "I live in Copenhagen", Topic = "location" },
            new() { Content = "I'm allergic to peanuts", Topic = "allergy" }
        ],
        Queries =
        [
            new() { Question = "What's my name?", ExpectedFacts = ["José"] },
            new() { Question = "Where do I live?", ExpectedFacts = ["Copenhagen"] },
            new() { Question = "Any food allergies I should know about?", ExpectedFacts = ["peanuts"] }
        ],
        SuccessThreshold = 1.0  // All 3 facts must survive (100% critical for profile)
    };

    public static CrossSessionScenario SafetyCritical() => new()
    {
        Name = "Safety-Critical Facts Cross-Session",
        FactsToPlant =
        [
            new() { Content = "I take blood pressure medication", Topic = "medication" },
            new() { Content = "I'm diabetic", Topic = "medical-condition" },
            new() { Content = "Emergency contact: mom +45-12345678", Topic = "emergency" }
        ],
        Queries =
        [
            new() { Question = "Do I have any medical conditions?", ExpectedFacts = ["diabetic", "blood pressure"] },
            new() { Question = "Who should you call in an emergency?", ExpectedFacts = ["mom", "+45-12345678"] }
        ],
        SuccessThreshold = 1.0  // 100% — safety-critical facts MUST survive
    };
}
```

**Assertions for F09:**
```csharp
result.Should()
    .HavePassed()
    .HaveSessionResetSupported()
    .HaveOverallScoreAbove(80)
    .HaveFactSurvived("José", because: "user name is critical for personalization")
    .HaveFactSurvived("peanuts", because: "allergy info is safety-critical");
```

---

## 14. **CRITICAL MISSING INTEGRATIONS** — Source Code Analysis

> **🎸 STATUS:** Based on **real MAF RC3 source code analysis** — these are MISSING from the current implementation plan and must be added as **implementation requirements**, not optional features.

After deep analysis of the MAF source code, these integrations are **critical implementation requirements** for AgentEval Memory evaluation to be complete:

### 14.1 Memory-Aware Test Harnesses (IMPLEMENTATION REQUIREMENT)

**Status:** **REQUIRED** — Part of core implementation, not optional feature  
**Where:** Must be built into `MemoryTestRunner` and `MAFEvaluationHarness`  
**Why Critical:** Without this, memory evaluation results won't integrate with existing AgentEval workflows

```csharp
// REQUIREMENT: Memory evaluation must integrate with existing test harnesses
// This is NOT a new feature — it's part of F01 (Core Engine) implementation

public class MemoryEvaluationHarness : MAFEvaluationHarness  
{
    private readonly IMemoryTestRunner _memoryRunner;
    private readonly IMemoryJudge _memoryJudge;
    
    public override async Task<TestResult> RunTestAsync<T>(
        IEvaluableAgent agent, 
        TestCase<T> testCase,
        CancellationToken ct = default)
    {
        // Run standard evaluation first
        var standardResult = await base.RunTestAsync(agent, testCase, ct);
        
        // Add memory-specific tracking if agent supports memory
        if (IsMemoryCapable(agent))
        {
            var memoryContext = await CaptureMemoryContext(agent, testCase, ct);
            standardResult = EnrichWithMemoryMetrics(standardResult, memoryContext);
        }
        
        return standardResult;
    }
    
    private async Task<MemoryContext> CaptureMemoryContext(...)
    {
        // Track:
        // - Memory provider types used (ChatHistory, Foundry, Mem0, etc.)
        // - Memory operations (store/retrieve counts)
        // - Scope configurations 
        // - Cross-session state transitions
        // - Memory latency/performance impact
    }
}
```

**Integration Points Required:**
- [ ] `MAFEvaluationHarness` memory-aware extensions
- [ ] `TestResult` enrichment with memory metrics
- [ ] `PerformanceMetrics` memory operation tracking
- [ ] Memory provider detection and classification
- [ ] Automatic scope misconfiguration detection (F08) during test runs

### 14.2 Memory Assertion Extensions (IMPLEMENTATION REQUIREMENT)

**Status:** **REQUIRED** — Part of F04 (Fluent Assertions) implementation  
**Where:** Must extend existing `AgentEvalScope` and assertion framework  
**Why Critical:** Memory assertions must follow AgentEval's exact error handling patterns

```csharp
// REQUIREMENT: Memory assertions must integrate seamlessly with existing assertions
// Users expect: result.Should().HaveUsedAgent().And.HaveRetainedMemory()

public static class MemoryAssertionExtensions
{
    // Extend existing result types with memory assertions
    public static MemoryAssertions Should(this TestResult result)
    {
        // If result contains memory evaluation data, enable memory assertions
        return new MemoryAssertions(result);
    }
}

public class MemoryAssertions
{
    [StackTraceHidden]
    public MemoryAssertions HaveRetainedMemory(string fact, string? because = null)
    {
        // Must use AgentEvalScope.FailWith() for consistent error handling
        // Must provide Expected/Actual/Suggestions structure
        // Must integrate with existing assertion chaining (.And support)
    }
    
    [StackTraceHidden] 
    public ToolUsageAssertions And => new(_testResult.ToolUsage);  // Chain to existing
}
```

**Integration Requirements:**
- [ ] Extend `TestResult` model with `MemoryUsage` property  
- [ ] Memory-specific exception types following `ToolAssertionException` pattern
- [ ] Assertion chaining support (`.And` operator)
- [ ] `AgentEvalScope` integration for multi-assertion failure collection
- [ ] Consistent error message formatting with suggestions

### 14.3 Memory Performance Tracking (IMPLEMENTATION REQUIREMENT) 

**Status:** **REQUIRED** — Part of existing `PerformanceMetrics` expansion  
**Where:** Must extend `PerformanceMetrics` class, not create new types  
**Why Critical:** Memory operations have significant latency/cost impact on agent performance

```csharp
// REQUIREMENT: Memory performance must be tracked in existing PerformanceMetrics
// This is NOT a new metric — it's expanding existing performance tracking

public class PerformanceMetrics  // Existing class in AgentEval.Core
{
    // EXISTING PROPERTIES (unchanged)
    public required TimeSpan TotalDuration { get; init; }
    public required decimal? EstimatedCost { get; init; }
    public required Usage? Usage { get; init; }
    
    // NEW: Memory-specific performance data (REQUIRED for memory evaluation)
    public MemoryPerformance? MemoryPerformance { get; init; }  // New property
}

public class MemoryPerformance  // NEW: Memory operation tracking
{
    public required int MemoryStoreOperations { get; init; }      // Store calls
    public required int MemoryRetrieveOperations { get; init; }   // Retrieve calls
    public required TimeSpan MemoryLatency { get; init; }         // Total memory time
    public required decimal MemoryOperationCost { get; init; }    // Vector DB costs etc.
    
    public IReadOnlyList<string>? MemoryProviderTypes { get; init; }  // ["Mem0", "Foundry"]
    public int? MemoryHits { get; init; }                         // Successful retrievals
    public int? MemoryMisses { get; init; }                       // Failed retrievals
}
```

**Integration Requirements:**
- [ ] `MAFAgentAdapter` must capture memory metrics from `AgentSession`
- [ ] Memory provider operation instrumentation
- [ ] Vector store/embedding cost estimation integration
- [ ] Memory latency profiling during evaluation
- [ ] Performance regression detection (memory operations slowing down agent)

### 14.4 Memory Metric Auto-Registration (IMPLEMENTATION REQUIREMENT)

**Status:** **REQUIRED** — Part of DI registration, not optional  
**Where:** Must integrate with existing `AddAgentEval()` registration  
**Why Critical:** Memory metrics must be discoverable by existing evaluation runners

```csharp
// REQUIREMENT: Memory metrics must be auto-registered with existing metric discovery
// Users expect RunEvaluationAsync() to automatically include memory metrics when available

public static IServiceCollection AddAgentEvalMemory(
    this IServiceCollection services, 
    Action<AgentEvalServiceOptions>? configure = null)
{
    // Register memory evaluators
    services.AddMemoryEvaluators();
    
    // CRITICAL: Register memory metrics with existing IMetric registration
    // This makes memory metrics discoverable by RunEvaluationAsync()
    services.AddSingleton<IMetric, MemoryRetentionMetric>();
    services.AddSingleton<IMetric, MemoryTemporalMetric>();
    services.AddSingleton<IMetric, MemoryReachBackMetric>();
    services.AddSingleton<IMetric, MemoryReducerFidelityMetric>();
    
    // CRITICAL: Auto-enable memory evaluation in existing harnesses
    services.Decorate<IEvaluationHarness, MemoryAwareHarness>();
    
    return services;
}
```

**Integration Requirements:**
- [ ] Memory metrics implement existing `IMetric` interface exactly
- [ ] Auto-registration with metric discovery system
- [ ] Harness decoration pattern for automatic memory tracking
- [ ] Graceful degradation when memory providers not available
- [ ] Metric naming compliance with existing conventions (`llm_`/`code_` prefixes)

### 14.5 Export Format Integration (IMPLEMENTATION REQUIREMENT)

**Status:** **REQUIRED** — Part of existing export system extension  
**Where:** Must extend existing exporters, not create new ones  
**Why Critical:** Memory evaluation results must appear in existing CI/CD reports

```csharp
// REQUIREMENT: Memory results must export through existing IResultExporter system
// CI/CD systems expect JUnit XML, JSON, Markdown - memory data must fit existing formats

public class MarkdownResultExporter  // Existing class in AgentEval.DataLoaders
{
    public async Task ExportAsync(TestResult testResult, Stream destination, CancellationToken ct)
    {
        // EXISTING: Tool usage, performance, metric results
        await WriteToolUsage(testResult.ToolUsage);
        await WritePerformance(testResult.Performance);
        await WriteMetrics(testResult.Metrics);
        
        // NEW: Memory evaluation results (if present)
        if (testResult.MemoryUsage is not null)
        {
            await WriteMemoryResults(testResult.MemoryUsage);  // REQUIRED integration
        }
    }
    
    private async Task WriteMemoryResults(MemoryUsage memoryUsage)
    {
        // Memory benchmark summary
        // Scope misconfiguration warnings
        // Cross-session persistence results
        // Reach-back depth charts
        // Reducer fidelity analysis
    }
}
```

**Integration Requirements:**  
- [ ] `TestResult.MemoryUsage` property (optional, nullable)
- [ ] All existing exporters support memory data (Markdown, JSON, JUnit XML)
- [ ] Memory data follows existing export schema patterns
- [ ] CI/CD dashboard compatibility (Azure DevOps, GitHub Actions)
- [ ] Historical trending support for memory performance

---

## 15. Feature Classification: MAF vs AgentEval

> **You're absolutely right!** We need to properly separate what enhances MAF vs. what's pure evaluation capability.

### 15.1 MAF Framework Enhancements (Actually Improve MAF)

These features would **enhance MAF itself** — adding capabilities that don't exist:

| Feature | Category | What It Adds to MAF | Implementation Location |
|---------|----------|---------------------|-------------------------|
| **Multi-Modal Memory** | MAF Enhancement | Store/retrieve images, audio, documents in memory providers | `Microsoft.Agents.AI.MultiModal` |
| **Memory Compression** | MAF Enhancement | Auto-summarize old conversations to preserve key info | `Microsoft.Agents.AI.Compression` |
| **Memory Analytics** | MAF Enhancement | Built-in memory usage analytics and insights | `Microsoft.Agents.AI.Analytics` |
| **Cross-Session Memory** | MAF Enhancement | Better cross-session memory management primitives | `Microsoft.Agents.AI.Sessions` |
| **Federated Memory** | MAF Enhancement | Search across multiple memory providers simultaneously | `Microsoft.Agents.AI.Federation` |
| **Privacy Controls** | MAF Enhancement | PII detection, redaction, consent management in providers | `Microsoft.Agents.AI.Privacy` |
| **Real-Time Sync** | MAF Enhancement | Live memory updates across distributed agent instances | `Microsoft.Agents.AI.Sync` |

**Schema for MAF Enhancements:**
```csharp
// These would be NEW MAF capabilities, not evaluation tools
// Location: Microsoft.Agents.AI.* (MAF framework extensions)

public class MultiModalMemoryProvider : AIContextProvider
{
    // Store and retrieve images, audio, videos alongside text
    public async Task<AIContext> ProvideImageContextAsync(InvokingContext context);
}

public class MemoryCompressionProvider : AIContextProvider  
{
    // Automatically compress old conversations while preserving key facts
    public async Task<CompressionResult> CompressAsync(IReadOnlyList<ChatMessage> messages);
}
```

### 15.2 AgentEval Memory Evaluation (Pure Testing/Benchmarking)

These are **evaluation and testing capabilities** — they don't enhance MAF, they **test** memory behavior:

| Feature | Category | What It Tests | Implementation Location |
|---------|----------|---------------|-------------------------|
| **F01** Core Engine | AgentEval | Can the agent remember basic facts? | `AgentEval.Memory` |
| **F02** LLM-as-Judge | AgentEval | Does the response contain expected facts? | `AgentEval.Memory` |  
| **F03** CanRemember | AgentEval | Simple one-liner memory test API | `AgentEval.Memory` |
| **F04** Fluent Assertions | AgentEval | Fluent API for memory test assertions | `AgentEval.Memory` |
| **F05** Temporal Evaluation | AgentEval | Does agent handle time-sensitive facts? | `AgentEval.Memory` |
| **F07** Scenario Library | AgentEval | Pre-built test scenarios for memory | `AgentEval.Memory` |
| **F08** Scope Misconfig | AgentEval | Detects MAF scope configuration bugs | `AgentEval.MAF` |
| **F09** Cross-Session Test | AgentEval | Tests cross-session memory persistence | `AgentEval.Memory` |
| **F10** Reducer Evaluation | AgentEval | Measures info loss from chat reducers | `AgentEval.Memory` |
| **F18** Benchmark Suite | AgentEval | Comprehensive memory quality scoring | `AgentEval.Memory` |
| **F26** Chatty Scenarios | AgentEval | Tests memory in noisy conversations | `AgentEval.Memory` |
| **F27** Reach-Back Testing | AgentEval | Tests memory recall depth limits | `AgentEval.Memory` |

**Schema for AgentEval Memory:**
```csharp
// These are TESTING tools, not MAF enhancements
// Location: AgentEval.Memory (evaluation framework)

public interface IMemoryTestRunner
{
    // Tests any agent's memory behavior - doesn't enhance the agent
    Task<MemoryEvaluationResult> RunAsync(IEvaluableAgent agent, MemoryTestScenario scenario);
}

public class MemoryBenchmarkRunner
{
    // Benchmarks memory quality - measures, doesn't improve
    Task<MemoryBenchmarkResult> RunBenchmarkAsync(IEvaluableAgent agent, MemoryBenchmark benchmark);
}
```

### 15.3 The Key Distinction

**MAF Enhancements = Build Better Agents**  
- Add new capabilities to MAF framework  
- Make agents more powerful/capable  
- Users get enhanced agent behavior  
- Example: "Now your agent can remember images!"

**AgentEval Memory = Test Agent Memory**  
- Evaluate existing agent memory behavior  
- Measure quality, find problems, benchmark performance  
- Users get insights into memory effectiveness  
- Example: "Your agent only remembers 60% of facts after 25 turns"

### 15.4 Implementation Strategy

**For AgentEval (what we're building):**
```
✅ Build all F01-F27 features in AgentEval.Memory
✅ Focus on evaluation, testing, benchmarking, debugging
✅ Framework-agnostic (works with MAF, MEAI, custom agents)
✅ Zero enhancements to MAF itself
```

**For Future MAF Contributions:**
```
🚀 Propose multi-modal memory enhancement to MAF team
🚀 Contribute compression/analytics features to MAF repo  
🚀 Keep evaluation separate from framework enhancement
🚀 AgentEval tests the enhancements once they exist
```

**Why This Matters:**
- **Clean separation of concerns**: Evaluation ≠ Enhancement
- **Faster delivery**: Don't need MAF team approval for evaluation features
- **Broader applicability**: Our evaluation works with any memory system
- **Clear value prop**: "Test your memory" vs "Build better memory"

---

## 16. Feature Comparison Table — Market Analysis

> **Restored from deleted content** — comprehensive competitive analysis

### 16.1 Memory Evaluation Feature Matrix

| Feature | AgentEval (.NET) | RAGAS (Python) | DeepEval (Python) | LangSmith | W&B Weave | Custom |
|---------|------------------|----------------|-------------------|-----------|-----------|--------|
| **Basic Memory Testing** | ✅ F01-F04 | ❌ | ❌ | ⚠️ Manual | ⚠️ Manual | 🔨 DIY |
| **Temporal Memory** | ✅ F05 | ❌ | ❌ | ❌ | ❌ | 🔨 DIY |
| **Cross-Session Memory** | ✅ F09 | ❌ | ❌ | ❌ | ❌ | 🔨 DIY |
| **Reach-Back Depth Testing** | ✅ F27 | ❌ | ❌ | ❌ | ❌ | 🔨 DIY |
| **Chat Reducer Evaluation** | ✅ F10 | ❌ | ❌ | ❌ | ❌ | 🔨 DIY |
| **Memory Benchmark Suite** | ✅ F18 | ❌ | ❌ | ⚠️ Traces | ⚠️ Logs | 🔨 DIY |
| **Scope Misconfiguration** | ✅ F08 | ❌ | ❌ | ❌ | ❌ | 🔨 DIY |
| **Framework Support** | Universal | Python only | Python only | Universal | Python | Any |
| **Memory Assertions** | ✅ Fluent | ❌ | ❌ | ⚠️ Basic | ⚠️ Basic | 🔨 DIY |
| **Built-in Scenarios** | ✅ F07 | ❌ | ❌ | ❌ | ❌ | 🔨 DIY |
| **Noise Resilience** | ✅ F26 | ❌ | ❌ | ❌ | ❌ | 🔨 DIY |
| **One-Liner Testing** | ✅ F03 | ❌ | ❌ | ❌ | ❌ | 🔨 DIY |

**Legend:**  
✅ Built-in comprehensive support  
⚠️ Partial/manual implementation possible  
❌ No support  
🔨 Requires custom development  

### 16.2 Competitive Landscape Analysis

```
MEMORY EVALUATION MATURITY BY PLATFORM
========================================

AgentEval (.NET) — COMPREHENSIVE MEMORY EVALUATION
┌─────────────────────────────────────────────────────┐
│ • Purpose-built memory evaluation framework         │
│ • 12 comprehensive memory testing features          │
│ • Framework-universal (MAF, MEAI, custom)          │
│ • Fluent assertions with actionable suggestions     │
│ • Built-in benchmark suite with industry scoring    │
│ • Temporal reasoning and cross-session testing      │ 
│ • Automated scope misconfiguration detection        │
│ • Reach-back depth profiling (novel capability)     │
│ • Chat reducer fidelity analysis (industry first)   │
└─────────────────────────────────────────────────────┘
🎯MATURITY: Advanced (purpose-built for memory evaluation)

RAGAS (Python) — RAG EVALUATION ONLY
┌─────────────────────────────────────────────────────┐
│ • Strong RAG evaluation (faithfulness, relevance)   │
│ • No memory-specific testing capabilities           │
│ • Focused on single-turn Q&A, not conversations     │
│ • No cross-session or persistence testing           │
│ • No temporal or reducer evaluation                  │
└─────────────────────────────────────────────────────┘
🎯MATURITY: Limited (RAG only, not conversational memory)

DeepEval (Python) — GENERAL AI EVALUATION
┌─────────────────────────────────────────────────────┐
│ • General AI evaluation framework                   │
│ • Some conversational capabilities                   │
│ • No dedicated memory evaluation features           │
│ • No cross-session testing                          │
│ • No specialized memory benchmarks                   │
└─────────────────────────────────────────────────────┘
🎯MATURITY: Basic (general purpose, memory not specialized)

LangSmith — OBSERVABILITY PLATFORM
┌─────────────────────────────────────────────────────┐
│ • Excellent tracing and logging                     │
│ • Manual evaluation setup required                  │
│ • No built-in memory evaluation patterns            │
│ • Can track memory operations but not test quality  │
│ • Good for debugging but not benchmarking           │
└─────────────────────────────────────────────────────┘
🎯MATURITY: Basic (observability, not evaluation)

W&B Weave — ML EXPERIMENT TRACKING
┌─────────────────────────────────────────────────────┐
│ • Strong experiment tracking and visualization       │
│ • General evaluation framework                       │
│ • Memory evaluation requires custom implementation   │
│ • Good for storing results, not generating them     │
└─────────────────────────────────────────────────────┘
🎯MATURITY: Basic (tracking platform, not memory-specific)
```

### 16.3 Market Gap Analysis

```
MEMORY EVALUATION GAPS IN EXISTING TOOLS
==========================================

MISSING EVERYWHERE (AgentEval's Unique Value):
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

❌ Cross-session memory persistence testing
   Problem: No tool tests if agents remember across conversation resets
   
❌ Temporal memory evaluation (time-sensitive facts)  
   Problem: No tool tests how agents handle facts that change over time
   
❌ Reach-back depth profiling
   Problem: No tool measures HOW FAR BACK agents can recall through noise
   
❌ Chat reducer fidelity analysis
   Problem: NOBODY measures what info is lost during context compression
   
❌ Memory scope misconfiguration detection
   Problem: Framework-specific configuration bugs go undetected
   
❌ Comprehensive memory benchmarking
   Problem: No standardized "memory quality score" like GPU benchmarks
   
❌ Memory-aware performance testing
   Problem: No tool measures memory's impact on agent response latency
   
❌ Noise resilience evaluation
   Problem: No tool tests memory in realistic chatty conversations

PARTIALLY AVAILABLE (AgentEval Does Better):
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

⚠️ Basic fact recall testing (manual in LangSmith/Weave)
   AgentEval advantage: Built-in scenarios, fluent assertions, LLM-judge
   
⚠️ Memory operation observability (logging in LangSmith)
   AgentEval advantage: Evaluation + benchmarking, not just observation
   
⚠️ Custom evaluation (possible in DeepEval/Weave)
   AgentEval advantage: Purpose-built, zero setup, comprehensive
```

### 16.4 Total Addressable Market (TAM)

```
MEMORY EVALUATION MARKET SIZE
==============================

PRIMARY MARKET (Direct Users):
┌─────────────────────────────────────────┐
│ • .NET AI developers using MAF/MEAI    │
│ • Enterprise teams building AI agents   │  
│ • AI consultancies (memory quality QA)  │
│ • Microsoft partner ecosystem          │
│ • Government agencies (reliability)     │
└─────────────────────────────────────────┘
Est. Size: 50,000+ developers (Microsoft ecosystem)

SECONDARY MARKET (Cross-Platform Interest):
┌─────────────────────────────────────────┐
│ • Python developers (want .NET features) │
│ • Multi-language AI teams               │
│ • AI research labs (novel capabilities)  │
│ • Tool vendors (integration partners)    │
└─────────────────────────────────────────┘
Est. Size: 200,000+ developers (broader AI community)

MARKET DRIVERS:
✅ Agent memory is critical for production deployment
✅ No comprehensive memory evaluation exists
✅ Microsoft pushing MAF (built-in customer base)
✅ Memory bugs are expensive in production
✅ Regulatory compliance requires memory auditing
```

### 16.5 Competitive Advantages

| Advantage | Explanation | Defensibility |
|-----------|-------------|---------------|
| **First Mover** | No comprehensive memory evaluation exists | High (network effects) |
| **Framework Universal** | Works with MAF, MEAI, custom agents | Medium (competitors can copy) |
| **Microsoft Ecosystem** | Built for MAF, Microsoft backing | High (ecosystem lock-in) |
| **Novel Capabilities** | Reach-back, reducer analysis, temporal | Medium (technical moat) |
| **Enterprise Ready** | CI/CD integration, compliance reporting | Medium (execution barrier) |
| **Comprehensive** | 12 features vs. competitors' 0-2 | Medium (scope advantage) |

### 16.6 Go-to-Market Strategy

**Phase 1: Microsoft Ecosystem Domination**
- Target MAF early adopters  
- Integrate with Azure DevOps/GitHub Actions  
- Microsoft partnership/endorsement  
- Conference presentations (Build, .NET Conf)

**Phase 2: Cross-Platform Expansion**  
- Python SDK (AgentEval.Python using .NET interop)
- LangChain/LangSmith integration  
- Open source community building  
- Research paper publications

**Phase 3: Enterprise Platform**
- SaaS offering for memory evaluation  
- Compliance certification (SOC2, ISO)  
- Enterprise support/consulting  
- Industry-specific benchmarks

---

## 17. **RECOMMENDATION: Double Down on Agentic Evaluation**

> **The biggest opportunity is integrating memory providers with AgentEval's evaluation framework. This would be a unique differentiator — no Python framework (RAGAS, DeepEval) has comprehensive memory evaluation capabilities!**

### What Does "Double Down on Agentic Evaluation" Mean?

**In simple terms:** AgentEval should go all-in on evaluating the *behaviors* that make AI agents different from plain LLMs — specifically **memory, tool use, multi-turn reasoning, and session persistence**. These are "agentic" capabilities. "Double down" means: don't dabble, don't do a minimal version — invest deeply and make AgentEval THE definitive toolkit for testing these capabilities.

**In detail:** Today's AI evaluation landscape is split between:
- **RAG evaluators** (RAGAS, DeepEval) — they test retrieval quality but not agent behavior across turns
- **LLM benchmarks** (MMLU, HumanEval) — they test raw model capability, not agent orchestration
- **Observability tools** (LangSmith, Weights & Biases) — they show what happened, but don't tell you if it was *correct*

The gap is: **nobody comprehensively tests what agents DO differently from plain LLMs**. Agents remember things across turns, call tools, manage sessions, reduce chat history, recall facts through noise. "Doubling down on agentic evaluation" means AgentEval becomes the authority on testing ALL of these agent-specific behaviors — starting with memory.

### What Does "Integrating Memory Providers with AgentEval's Evaluation Framework" Mean?

**In simple terms:** Make it so that any developer using MAF's memory providers (ChatHistoryProvider, Mem0, Foundry, vector stores) can test their memory behavior with one-liner assertions like `agent.CanRememberAsync("fact")`, fluent chains like `.Should().HaveRecalled("allergy")`, and benchmark suites — all using the same patterns they already know from AgentEval's tool and RAG evaluation.

**In full detail, this means 4 concrete integration points:**

1. **Black-box behavioral testing** — Wrap any `IEvaluableAgent` (regardless of which memory provider it uses) and test: "Did you remember this fact after N turns of noise?" This is the `MemoryTestRunner` + `MemoryJudge` pattern. The agent is a black box; we don't know if it uses InMemoryChat, Mem0, or Foundry — we just test if it *remembers*.

2. **IMetric integration** — Memory evaluation produces `MetricResult` objects that flow through the same pipeline as tool metrics and RAG metrics. This means memory scores appear in the same benchmark tables, can be exported to the same formats (JSON, Markdown, JUnit), and participate in stochastic runs. Concretely: `llm_memory_retention`, `llm_memory_temporal_accuracy`, `code_memory_reach_back`, `code_memory_reducer_fidelity`, `embed_memory_similarity`.

3. **Fluent assertion integration** — Memory results get `.Should()` extension methods that follow the exact same pattern as `result.ToolUsage!.Should().HaveCalledTool(...)`. For memory: `result.MemoryEvaluation!.Should().HaveRecalledFact("allergy").WithConfidence(0.8).AfterNTurns(25)`. Same assertion exceptions with Expected/Actual/Suggestions.

4. **DI registration** — `services.AddAgentEvalMemory()` registers all memory services (runner, judge, evaluators, benchmark) into the same DI container. `services.AddAgentEvalAll()` includes memory automatically. All services inject via interfaces (`IMemoryTestRunner`, `IMemoryJudge`, `IMemoryBenchmarkRunner`).

**What it is NOT:** We do NOT build memory providers, enhance MAF, implement new vector stores, or run Mem0 services. We ONLY build the evaluation/testing layer. The agent team builds memory → we tell them if it works.

### 17.1 The Strategic Opportunity 🎯

**What's the opportunity?**  
AgentEval is uniquely positioned to **OWN** the memory evaluation space in AI. While RAGAS focuses on RAG, DeepEval on general evaluation, and LangSmith on observability — **nobody owns conversational memory evaluation**.

**Why is this valuable?**
1. **Memory is the #1 production pain point** for AI agents
2. **Memory bugs are expensive** (wrong recommendations, privacy leaks, safety issues)  
3. **No comprehensive solution exists** — massive untapped market
4. **Perfect timing** — agents going mainstream, memory becoming critical

**What makes AgentEval different?**
- **Comprehensive**: 12 memory-specific features vs. competitors' 0-2
- **Production-focused**: Tests real agent memory, not just embeddings
- **Framework-universal**: Works with MAF, MEAI, custom — not Python-only
- **Enterprise-ready**: CI/CD integration, compliance reporting, benchmarking

### 17.2 Implementation Strategy — What to Build

**Core Decision: AgentEval stays "evaluation-only"**  
✅ Don't build memory providers or enhance MAF  
✅ Build the world's best memory evaluation/testing framework  
✅ Test ANY agent's memory behavior comprehensively  
✅ Provide actionable insights and benchmarking

```
IMPLEMENTATION ARCHITECTURE
============================

Package Structure:
┌─────────────────────────────────────────────────────┐
│ AgentEval.Memory (Universal Memory Evaluation)      │
│ ├── Memory test runners and scenarios               │  
│ ├── LLM-as-judge fact verification                  │
│ ├── Cross-session persistence testing               │
│ ├── Reach-back depth profiling                      │
│ ├── Chat reducer fidelity analysis                  │
│ ├── Temporal memory evaluation                      │
│ ├── Noise resilience testing                        │
│ ├── Comprehensive benchmark suite                   │
│ └── Fluent assertions with actionable suggestions   │
└─────────────────────────────────────────────────────┘

Integration Points:
┌─────────────────────────────────────────────────────┐
│ AgentEval.MAF (MAF-Specific Extensions)             │
│ ├── F08: Scope misconfiguration detection           │
│ ├── MAF memory provider instrumentation             │
│ ├── Session management integration                   │
│ └── MAF-specific performance metrics                │
└─────────────────────────────────────────────────────┘

Core AgentEval Integration:
┌─────────────────────────────────────────────────────┐
│ Existing AgentEval.Core Extensions                  │
│ ├── ISessionResettableAgent interface               │
│ ├── MemoryUsage in TestResult                      │
│ ├── Memory-aware performance metrics                │
│ ├── Memory assertions in fluent API                │
│ └── Export format integration (JSON, Markdown)      │
└─────────────────────────────────────────────────────┘
```

### 17.3 The Positioning Strategy

**Primary Message: "Memory Quality Assurance for AI Agents"**

```
TARGET AUDIENCE POSITIONING
============================

For Enterprise AI Developers:
"Before you deploy agents to production, test their memory.
AgentEval Memory finds memory bugs before your customers do."

For Microsoft Ecosystem:
"Built for MAF, works with MEAI. The official way to test
agent memory in the Microsoft AI stack."

For AI Researchers:
"Novel capabilities: reach-back depth profiling, temporal
memory evaluation, reducer fidelity analysis. Research-grade
memory evaluation for agents."

For Compliance/Safety Teams:
"Audit trail for agent memory behavior. Benchmark memory
quality against industry standards. Detect privacy/safety
issues before deployment."
```

### 17.4 Does This Break AgentEval's Positioning?

**Question:** "Does adding memory evaluation break the positioning of AgentEval as only-an-evaluation-framework?"

**Answer:** **NO — it STRENGTHENS the evaluation positioning!**

**Why it fits perfectly:**
- **Still evaluation-only**: We're testing memory, not building memory systems
- **Expands evaluation coverage**: From tools → memory → (future: reasoning, safety, etc.)
- **Maintains framework-agnostic approach**: Tests ANY agent's memory behavior
- **Follows existing patterns**: Same as tool evaluation, RAG evaluation, etc.

**Analogy to existing features:**
```
AgentEval Tool Evaluation:     Tests agent tool usage → doesn't build tools
AgentEval RAG Evaluation:      Tests RAG quality → doesn't build RAG systems  
AgentEval Memory Evaluation:   Tests memory quality → doesn't build memory systems
```

**The positioning becomes stronger:**
```
OLD: "AgentEval tests AI agents"
NEW: "AgentEval comprehensively evaluates AI agents: tools, RAG, memory, performance"
```

### 17.5 Multi-Repository Strategy

**Question:** "Would you do part of it in a separate package/code repository?"

**Recommended approach: Single repository, multi-package architecture**

```
PACKAGE ORGANIZATION (Single Repo)
====================================

AgentEval Repository (github.com/microsoft/AgentEval):
├── src/AgentEval/                    # Umbrella package
├── src/AgentEval.Core/               # Existing core
├── src/AgentEval.MAF/                # Existing MAF adapter  
├── src/AgentEval.Memory/             # NEW: Universal memory evaluation
├── src/AgentEval.RedTeam/            # Existing security
└── src/AgentEval.DataLoaders/        # Existing loaders

NuGet Packages:
├── AgentEval                         # Umbrella (includes Memory)
├── AgentEval.Memory                  # Standalone memory evaluation
└── AgentEval.MAF                     # MAF-specific (includes F08)

Why single repository?
✅ Consistent versioning and releases
✅ Shared CI/CD pipeline and testing  
✅ Cross-package integration testing
✅ Unified documentation and samples
✅ Easier maintenance and governance
```

**Alternative: Separate repository for advanced features**
```
IF memory evaluation becomes huge (future):
├── AgentEval (Core evaluation framework)
└── AgentEval.Memory (Advanced memory evaluation)

When to split?
• When Memory package > 50% of total codebase
• When Memory has different release cadence  
• When Memory needs different contributors/governance
• NOT in MVP — start together, split if needed
```

#### 17.5.1 Deep Analysis: Single Repo + Separate NuGet Package — Pros and Cons

> **Updated 2026-03-09:** Expanded with detailed pros/cons analysis, standalone viability assessment, and comparison with AgentEval.RedTeam.

**The core question:** Should `AgentEval.Memory` be released as a **separately installable NuGet package** (while living in the same repo), or should it ONLY be consumed through the umbrella `AgentEval` package?

**Current architecture already does this:** `AgentEval.RedTeam`, `AgentEval.MAF`, `AgentEval.DataLoaders`, and `AgentEval.Core` are all separate projects in the same repo. The umbrella `AgentEval` package is the only `IsPackable=true` project and bundles all 5 DLLs. So the question is really: **should we ALSO publish `AgentEval.Memory` as its own standalone NuGet package?**

#### Pros vs Cons: Standalone `AgentEval.Memory` NuGet Package

| | **Pros** | **Cons** |
|---|---|---|
| **Adoption** | Teams that only want memory evaluation don't need to install the full toolkit (smaller dependency footprint) | Most users will install the umbrella anyway — fragmented packages can confuse users ("which one do I need?") |
| **Versioning** | Can version memory features independently (e.g., Memory v1.2 while Core stays v1.0) | Independent versioning creates **compatibility matrix hell** — which Memory version works with which Core version? Users get `DependencyResolutionException` at runtime |
| **Release cadence** | Memory can ship hotfixes or new features without waiting for a full AgentEval release cycle | Separate release cadence means **double the release overhead** — separate changelogs, release notes, NuGet publish steps, and compatibility testing |
| **Size/perf** | Smaller package for users who don't need RedTeam, DataLoaders, etc. | The entire AgentEval umbrella is <2MB — size is not a real problem |
| **Marketing** | Standalone package is discoverable on NuGet.org ("I searched for memory evaluation and found AgentEval.Memory") | Dilutes brand — two NuGet listings instead of one. Users may install Memory alone and miss the full toolkit |
| **Dependencies** | Memory has minimal deps (only needs AgentEval.Abstractions + Core) | Still must take a dependency on Core for shared infra (IMetric, FakeChatClient, assertions). Not truly standalone |
| **Testing** | Can run Memory tests independently | Must still run cross-package integration tests (Memory + Core + MAF). Separate package doesn't reduce test burden |
| **Governance** | Could have different maintainers/reviewers | In practice, same 1-3 people maintain everything. Separate governance adds ceremony without value at this scale |
| **CI/CD** | Can have focused Memory CI pipeline | More CI configurations to maintain. Matrix testing (Memory x Core versions) adds complexity |

#### Would AgentEval.Memory Work Standalone?

**No, not truly standalone.** `AgentEval.Memory` depends on:
- `AgentEval.Abstractions` — for `IEvaluableAgent`, `IMetric`, `MetricResult`, `EvaluationContext`
- `AgentEval.Core` — for `FakeChatClient` (testing), assertion infrastructure, DI extensions
- `Microsoft.Extensions.AI` — for `IChatClient`

So even as a "standalone" NuGet, it would pull in `AgentEval.Abstractions` and `AgentEval.Core` as transitive dependencies. The user saves installing `AgentEval.RedTeam`, `AgentEval.DataLoaders`, and `AgentEval.MAF` — which collectively add maybe 200KB. **The savings are negligible.**

#### When Would You Release It Separately?

Reasons to split into a standalone NuGet would be:
1. **Different audience** — Memory evaluation attracts users who would never use tool assertions or RAG metrics. (Unlikely — memory users are agent developers who need all of it.)
2. **Massive codebase growth** — Memory becomes 50%+ of the total codebase with its own documentation site. (Possible long-term, not now.)
3. **Different release cadence** — Memory needs weekly releases while Core is quarterly. (Not the case — both evolve together.)
4. **External contributors** — A major contributor wants to own Memory independently. (No sign of this.)

**Current verdict: None of these conditions are met.** Ship via umbrella only.

#### If We Do AgentEval.Memory Standalone, Why Not AgentEval.RedTeam Too?

**Exactly.** This is the slippery slope argument, and it's valid:

| Package | Standalone case | Counter-argument |
|---------|----------------|-----------------|
| `AgentEval.Memory` | "I only want memory eval" | You'll want tool assertions too when your memory-enabled agent calls tools |
| `AgentEval.RedTeam` | "I only want security scanning" | Security teams often need the full eval context to understand vulnerabilities |
| `AgentEval.MAF` | "I only use MAF" | MAF users need Core metrics and assertions |
| `AgentEval.DataLoaders` | "I only need data loading" | Loading data without evaluation is pointless |

If we make Memory standalone, we have no principled reason to deny RedTeam standalone, then MAF standalone, and suddenly we're managing **5 independent NuGet packages** with a compatibility matrix, 5 release pipelines, and 5 changelogs. For a team of 1-3 maintainers, this is **unsustainable overhead with no user benefit**.

#### The Recommendation

**Ship everything through the umbrella. Period.**

```
RECOMMENDED: Single NuGet, all batteries included
===================================================

NuGet: AgentEval (v0.x.x)
  ├── AgentEval.Abstractions.dll
  ├── AgentEval.Core.dll
  ├── AgentEval.Memory.dll        ← included automatically
  ├── AgentEval.DataLoaders.dll
  ├── AgentEval.RedTeam.dll
  └── AgentEval.MAF.dll

User experience:
  dotnet add package AgentEval
  // Done. Everything works. No compatibility questions.

NOT recommended (for now):
  dotnet add package AgentEval.Memory        ← separate NuGet
  dotnet add package AgentEval.RedTeam       ← separate NuGet
  // Creates: version compatibility issues, confused users, maintenance burden
```

**Revisit this decision when:**
- Total package size exceeds 10MB (currently <2MB)
- We have 3+ dedicated maintainers per sub-package
- User research shows demand for granular packages
- A specific sub-package has a fundamentally different release cadence

**How it works in the same repo:** All sub-projects (`AgentEval.Memory`, `AgentEval.RedTeam`, etc.) remain as separate `.csproj` files in `src/`. They compile into separate DLLs. The umbrella `src/AgentEval/AgentEval.csproj` references all of them and is the only `IsPackable=true` project. `dotnet pack src/AgentEval` bundles all DLLs into one NuGet. This is exactly how it works today — Memory just becomes another DLL in the bundle.

### 17.6 Market Execution Plan

**Phase 1: Technical Excellence (3 months)**
- Build all 12 F01-F27 features in AgentEval.Memory
- Achieve comprehensive test coverage with FakeChatClient
- Create compelling samples and documentation  
- Beta testing with Microsoft internal teams

**Phase 2: Ecosystem Integration (2 months)**  
- MAF team partnership and endorsement
- Azure DevOps/GitHub Actions integration
- Documentation and conference presentations
- Community feedback and iteration

**Phase 3: Market Domination (ongoing)**
- Industry benchmark establishment (like GPU benchmarks)
- Enterprise customer success stories
- Cross-platform expansion (Python bindings)
- Research publications and thought leadership

### 17.7 Success Metrics

**Technical Metrics:**
- Coverage: All 12 memory features implemented and tested
- Quality: >95% test coverage, zero critical bugs
- Performance: <2s benchmark runtime, <$0.10 LLM cost per test
- Usability: One-liner tests, fluent assertions, actionable errors

**Market Metrics:**
- Adoption: 1000+ downloads in first month
- Integration: 10+ Microsoft partner adoptions
- Recognition: Conference talks, blog posts, industry recognition  
- Community: GitHub stars, community contributions, API usage

### 17.8 The Bottom Line

**This is AgentEval's "iPhone moment"**  
Just as the iPhone wasn't the first smartphone, AgentEval Memory won't be the first memory evaluation — but it will be **THE FIRST COMPREHENSIVE ONE**.

**Competitive moat:**
- **First-mover advantage** in comprehensive memory evaluation
- **Microsoft ecosystem** native integration (MAF/MEAI)  
- **Novel capabilities** (reach-back, reducer analysis, temporal)
- **Enterprise-ready** from day one (CI/CD, compliance, benchmarking)

**Market timing:**
- **Perfect**: Agents going mainstream, memory becoming critical
- **Uncontested**: No comprehensive competitor exists
- **Microsoft-backed**: Built-in customer base and credibility
- **Scalable**: Framework-agnostic approach works everywhere

**Recommendation: Full commitment to memory evaluation excellence**  
Build the world's best agent memory evaluation framework. Own the space. Make it the industry standard.

🎸 **Rock on!** 🎸
public class ScopeMisconfigurationDetector
{
    /// <summary>
    /// Analyzes an AIAgent's memory provider configuration for misconfigurations.
    /// </summary>
    public ScopeMisconfigurationResult Analyze(AIAgent agent)
    {
        var warnings = new List<ScopeMisconfigurationWarning>();
        
        // Check each registered AIContextProvider
        foreach (var provider in agent.AIContextProviders)
        {
            // Warning 1: SearchScope includes SessionId → blocks cross-session recall
            if (HasSessionIdInSearchScope(provider))
            {
                warnings.Add(new ScopeMisconfigurationWarning
                {
                    Severity = MisconfigurationSeverity.High,
                    ProviderName = provider.GetType().Name,
                    Issue = "SearchScope includes SessionId",
                    Impact = "Cross-session memory recall is blocked. " +
                             "Memories stored in session A cannot be found in session B.",
                    Suggestion = "Remove SessionId from SearchScope to enable " +
                                 "cross-session recall. Keep SessionId only in StorageScope " +
                                 "if you need session-scoped storage.",
                    OWASPReference = "LLM04 — Data Poisoning (mis-isolation prevents " +
                                    "legitimate data access)"
                });
            }
            
            // Warning 2: StorageScope and SearchScope dimensions mismatch
            if (HasScopeDimensionMismatch(provider))
            {
                warnings.Add(new ScopeMisconfigurationWarning
                {
                    Severity = MisconfigurationSeverity.Medium,
                    ProviderName = provider.GetType().Name,
                    Issue = "StorageScope and SearchScope use different dimension keys",
                    Impact = "Memories may be stored with keys that don't match " +
                             "search queries, causing silent memory loss.",
                    Suggestion = "Ensure SearchScope dimensions are a subset of " +
                                 "StorageScope dimensions."
                });
            }
            
            // Warning 3: No UserId in scope → all users share memory
            if (HasNoUserIsolation(provider))
            {
                warnings.Add(new ScopeMisconfigurationWarning
                {
                    Severity = MisconfigurationSeverity.Critical,
                    ProviderName = provider.GetType().Name,
                    Issue = "No UserId in StorageScope or SearchScope",
                    Impact = "All users share the same memory pool. " +
                             "User A can see User B's private information.",
                    Suggestion = "Add UserId to both StorageScope and SearchScope " +
                                 "to ensure per-user memory isolation."
                });
            }
        }
        
        return new ScopeMisconfigurationResult
        {
            AgentName = agent.Name ?? "Unknown",
            ProvidersAnalyzed = agent.AIContextProviders.Count,
            Warnings = warnings,
            HasMisconfigurations = warnings.Count > 0,
            Score = warnings.Count == 0 ? 100.0 :
                    100.0 - (warnings.Count(w => w.Severity == MisconfigurationSeverity.Critical) * 40)
                          - (warnings.Count(w => w.Severity == MisconfigurationSeverity.High) * 20)
                          - (warnings.Count(w => w.Severity == MisconfigurationSeverity.Medium) * 10)
        };
    }
}

/// <summary>
/// Result of scope misconfiguration analysis.
/// </summary>
public class ScopeMisconfigurationResult
{
    public required string AgentName { get; init; }
    public required int ProvidersAnalyzed { get; init; }
    public required IReadOnlyList<ScopeMisconfigurationWarning> Warnings { get; init; }
    public required bool HasMisconfigurations { get; init; }
    public required double Score { get; init; }  // 0-100, 100 = no issues
}

public class ScopeMisconfigurationWarning
{
    public required MisconfigurationSeverity Severity { get; init; }
    public required string ProviderName { get; init; }
    public required string Issue { get; init; }
    public required string Impact { get; init; }
    public required string Suggestion { get; init; }
    public string? OWASPReference { get; init; }
}

public enum MisconfigurationSeverity { Low, Medium, High, Critical }
```

**Assertion support:**
```csharp
// Memory-specific assertion extensions for MAF (in AgentEval.MAF)
scopeResult.Should()
    .HaveNoMisconfigurations(because: "scope config must be correct for production")
    .HaveScoreAbove(90)
    .HaveNoCriticalWarnings();
```

**Metric:**
```csharp
// code_memory_scope_misconfig — free, instant, no LLM needed
// IMPORTANT: Requires AddAgentEvalMAF() to be created first (does not exist yet).
// This is a Phase 2 prerequisite: create DependencyInjection/MAFServiceCollectionExtensions.cs
// in the AgentEval.MAF project, following the same pattern as AddAgentEval()/AddAgentEvalMemory().
// Registered only when AddAgentEvalMAF() is called (not in universal Memory module)
public class ScopeMisconfigurationMetric : IMemoryMetric
{
    public string Name => "code_memory_scope_misconfig";
    public bool RequiresSessionReset => false;
    // ...
}
```

**80/20:** Check for 3 common misconfigurations (SessionId in search scope, dimension mismatch, no user isolation). These cover 95% of real-world scope bugs. Don't try to analyze every possible configuration combination.

> **⚠️ API VERIFICATION NOTE:** The `AIAgent.AIContextProviders`, `StorageScope`, and `SearchScope` 
> types referenced above are based on MAF 1.0.0-rc3 preview API. The exact property names and types 
> MUST be verified against the actual rc3 package API surface at implementation time, as the MAF API 
> is still evolving towards GA.

---

### F09: Cross-Session Persistence Testing 🔄

**What:** Tests whether the agent's memory survives session boundaries. This is THE fundamental test for persistent memory — if it doesn't work, the agent is just using chat history.

**Where it lives:** `AgentEval.Memory/` (UNIVERSAL — uses `ISessionResettableAgent`)

```
CROSS-SESSION TEST FLOW
=========================

Session 1: SEED FACTS
┌──────────────────────────────────────────────────┐
│                                                  │
│  Turn 1: "My name is José and I live in          │
│           Copenhagen, Denmark."                   │
│                                                  │
│  Turn 2: "I'm a software architect who           │
│           specializes in .NET and AI."            │
│                                                  │
│  Turn 3: "I'm allergic to peanuts."              │
│                                                  │
│  [Agent processes and stores memories]           │
│                                                  │
└────────────────────┬─────────────────────────────┘
                     │
                     ▼  ResetSessionAsync()
                     │  ════════════════════
                     │  Chat history: CLEARED
                     │  Persistent memory: INTACT
                     │
┌────────────────────▼─────────────────────────────┐
│                                                  │
│  Session 2: QUERY FACTS                          │
│                                                  │
│  Turn 1: "Where do I live?"                      │
│  EXPECT: "Copenhagen" or "Denmark"               │
│                                                  │
│  Turn 2: "What's my job?"                        │
│  EXPECT: "software architect" or ".NET" or "AI"  │
│                                                  │
│  Turn 3: "What should you know about my health?" │
│  EXPECT: "peanut" or "allergy"                   │
│  FORBID: Should not say "I don't know"           │
│                                                  │
│  [Agent must recall from PERSISTENT memory,      │
│   not from chat history (which was cleared)]     │
│                                                  │
└──────────────────────────────────────────────────┘

RESULT:
  ✅ "Copenhagen" recalled  → persistent memory works
  ✅ ".NET architect" recalled → profile persisted
  ✅ "peanut allergy" recalled → safety-critical fact persisted
  
  Cross-Session Persistence Score: 100/100 🎸
```

**Implementation:**

```csharp
// namespace AgentEval.Memory
// Lives in AgentEval.Memory — UNIVERSAL (uses ISessionResettableAgent)

/// <summary>
/// Tests whether agent memory persists across session boundaries.
/// Requires the agent to implement ISessionResettableAgent.
/// </summary>
public class CrossSessionEvaluator(
    IMemoryTestRunner runner,
    IMemoryJudge judge) : ICrossSessionEvaluator
{
    public async Task<CrossSessionResult> EvaluateAsync(
        IEvaluableAgent agent,
        CrossSessionScenario scenario,
        CancellationToken ct = default)
    {
        // Verify agent supports session reset
        if (agent is not ISessionResettableAgent resettable)
        {
            return CrossSessionResult.NotSupported(
                "Agent does not implement ISessionResettableAgent. " +
                "Cross-session testing requires session reset capability. " +
                "Implement ISessionResettableAgent on your agent adapter.");
        }
        
        // SESSION 1: Seed facts
        var seedResults = new List<AgentResponse>();
        foreach (var step in scenario.SeedSteps)
        {
            var response = await agent.InvokeAsync(step.Content, ct);
            seedResults.Add(response);
            
            // Optional delay for async memory providers (e.g., Foundry)
            if (step.DelayAfter.HasValue)
                await Task.Delay(step.DelayAfter.Value, ct);
        }
        
        // SESSION BOUNDARY: Reset
        await resettable.ResetSessionAsync(ct);
        
        // SESSION 2: Query facts
        var factResults = new List<MemoryFactResult>();
        foreach (var query in scenario.Queries)
        {
            var response = await agent.InvokeAsync(query.Question, ct);
            var factResult = await judge.JudgeAsync(response.Text, query, ct);
            factResults.Add(factResult);
        }
        
        // Score
        var totalFacts = factResults.Count;
        var recalledFacts = factResults.Count(f => f.Passed);
        var persistenceScore = totalFacts > 0 
            ? (double)recalledFacts / totalFacts * 100.0 
            : 0.0;
        
        return new CrossSessionResult
        {
            ScenarioName = scenario.Name,
            Passed = persistenceScore >= scenario.SuccessThreshold,
            PersistenceScore = persistenceScore,
            TotalFacts = totalFacts,
            FactsRecalled = recalledFacts,
            FactsLost = totalFacts - recalledFacts,
            FactResults = factResults,
            LostFacts = factResults.Where(f => !f.Passed)
                                   .Select(f => f.FactDescription)
                                   .ToList()
        };
    }
}

/// <summary>
/// A cross-session test scenario.
/// </summary>
public class CrossSessionScenario
{
    public required string Name { get; init; }
    public required IReadOnlyList<MemoryStep> SeedSteps { get; init; }
    public required IReadOnlyList<MemoryQuery> Queries { get; init; }
    public double SuccessThreshold { get; init; } = 80.0;  // 80% default
}

/// <summary>
/// Result of cross-session persistence evaluation.
/// </summary>
public class CrossSessionResult
{
    public required string ScenarioName { get; init; }
    public required bool Passed { get; init; }
    public required double PersistenceScore { get; init; }     // 0-100
    public required int TotalFacts { get; init; }
    public required int FactsRecalled { get; init; }
    public required int FactsLost { get; init; }
    public required IReadOnlyList<MemoryFactResult> FactResults { get; init; }
    public required IReadOnlyList<string> LostFacts { get; init; }
    public bool IsSupported { get; init; } = true;
    public string? NotSupportedReason { get; init; }
    
    public static CrossSessionResult NotSupported(string reason) => new()
    {
        ScenarioName = "N/A",
        Passed = false,
        PersistenceScore = 0,
        TotalFacts = 0,
        FactsRecalled = 0,
        FactsLost = 0,
        FactResults = [],
        LostFacts = [],
        IsSupported = false,
        NotSupportedReason = reason
    };
}
```

**Built-in cross-session scenarios (added to MemoryScenarios):**

```csharp
public static class MemoryScenarios
{
    // ... existing scenarios ...
    
    /// <summary>
    /// Basic cross-session test: seed 3 facts, reset, query all 3.
    /// </summary>
    public static CrossSessionScenario CrossSessionBasic() => new()
    {
        Name = "Basic Cross-Session Persistence",
        SeedSteps =
        [
            new() { Content = "My name is José and I live in Copenhagen, Denmark." },
            new() { Content = "I'm a software architect specializing in .NET and AI." },
            new() { Content = "I'm allergic to peanuts — this is very important." },
        ],
        Queries =
        [
            new() { Question = "Where do I live?", ExpectedFacts = ["Copenhagen"] },
            new() { Question = "What do I do for work?", ExpectedFacts = ["architect", ".NET"] },
            new() { Question = "What allergies do I have?", ExpectedFacts = ["peanut"] },
        ]
    };
    
    /// <summary>
    /// Safety-critical cross-session test: medication/allergy info MUST persist.
    /// </summary>
    public static CrossSessionScenario CrossSessionSafetyCritical() => new()
    {
        Name = "Safety-Critical Cross-Session",
        SeedSteps =
        [
            new() { Content = "I take metformin 500mg twice daily for diabetes." },
            new() { Content = "I have a severe allergy to penicillin — it causes anaphylaxis." },
            new() { Content = "My emergency contact is María at +45 12345678." },
        ],
        Queries =
        [
            new() { Question = "What medications am I on?", ExpectedFacts = ["metformin", "500mg"] },
            new() { Question = "What drug allergies do I have?", ExpectedFacts = ["penicillin"] },
            new() { Question = "Who should you call in an emergency?", ExpectedFacts = ["María"] },
        ],
        SuccessThreshold = 100.0  // Safety-critical: 100% recall required
    };
    
    /// <summary>
    /// Multi-session progressive profile building.
    /// Seeds facts across 3 sessions, then queries in session 4.
    /// </summary>
    public static IReadOnlyList<CrossSessionScenario> CrossSessionProgressive() =>
    [
        new()
        {
            Name = "Progressive Profile - Session 1 (name + location)",
            SeedSteps = [new() { Content = "I'm José from Copenhagen." }],
            Queries = [new() { Question = "What's my name?", ExpectedFacts = ["José"] }]
        },
        new()
        {
            Name = "Progressive Profile - Session 2 (profession)",
            SeedSteps = [new() { Content = "I work as a .NET architect." }],
            Queries =
            [
                new() { Question = "What's my name?", ExpectedFacts = ["José"] },          // From session 1
                new() { Question = "What do I do?", ExpectedFacts = ["architect", ".NET"] } // From session 2
            ]
        },
        new()
        {
            Name = "Progressive Profile - Session 3 (verify all)",
            SeedSteps = [new() { Content = "I have a dog named Luna." }],
            Queries =
            [
                new() { Question = "Where am I from?", ExpectedFacts = ["Copenhagen"] },     // Session 1
                new() { Question = "What's my profession?", ExpectedFacts = ["architect"] },  // Session 2
                new() { Question = "What's my pet's name?", ExpectedFacts = ["Luna"] },       // Session 3
            ]
        }
    ];
}
```

**Assertions for cross-session:**
```csharp
// Fluent assertions — extends MemoryAssertions
result.Should()
    .HaveCrossSessionPersistenceAbove(90, because: "basic facts must survive session reset")
    .HaveNoCriticalFactsLost(because: "medication and allergy info is safety-critical");
```

**80/20:** Basic cross-session (1 reset, seed + query) covers 90% of use cases. The progressive multi-session test is the stretch goal. Don't build distributed session testing, session timeout simulation, or concurrent session handling — those are A-tier refinements.

---

### Updated Final S-Tier (MUST HAVE — The Definitive 12)

| # | Feature | Universal? | Where? | Why MUST HAVE |
|---|---------|------------|--------|---------------|
| F01 | Core Engine | ✅ | Memory | Foundation for everything |
| F02 | LLM-as-Judge | ✅ | Memory | Brain that evaluates memory |
| F03 | CanRememberAsync | ✅ | Memory | 10/10 DevEx one-liner |
| F04 | Fluent Assertions | ✅ | Memory | 10/10 DevEx assertion chains |
| F05 | Temporal Evaluation | ✅ | Memory | Time-based fact validity |
| F07 | Scenario Library | ✅ | Memory | Users don't start from scratch |
| **F08** | **Scope Misconfig** | MAF | **MAF** | **Catches #1 config bug, free** |
| **F09** | **Cross-Session** | ✅* | **Memory** | **The reason memory providers exist** |
| F10 | Reducer Evaluation | ✅ | Memory | Nobody measures this |
| F18 | Benchmark Suite | ✅ | Memory | Holistic memory quality score |
| F26 | Chatty Conversations | ✅ | Memory | Real-world 80% noise scenarios |
| F27 | Reach-Back Depth | ✅* | Memory | How far back can your agent reach? |

`*` = Requires `ISessionResettableAgent` for full capability; graceful degradation without it.

---

## 18. Unit Test Structure & Implementation Order

### 18.1 Test Project Structure

Following AgentEval's established testing patterns, the test structure mirrors the implementation:

```
tests/AgentEval.Tests/Memory/
├── Models/                                       # Test data models & abstractions
│   ├── MemoryTestScenarioTests.cs               # Scenario validation
│   ├── MemoryQueryTests.cs                      # Query construction
│   ├── MemoryFactTests.cs                       # Fact modeling
│   ├── MemoryEvaluationResultTests.cs           # Result aggregation
│   └── ISessionResettableAgentTests.cs          # Session reset interface
│
├── Engine/                                       # Core evaluation engine
│   ├── MemoryTestRunnerTests.cs                 # F01: Orchestration logic
│   ├── MemoryJudgeTests.cs                      # F02: LLM fact-checking
│   └── EngineIntegrationTests.cs                # End-to-end engine tests
│
├── Extensions/                                   # Extension methods
│   └── CanRememberExtensionsTests.cs            # F03: One-liner API
│
├── Assertions/                                   # Fluent assertion API
│   ├── MemoryAssertionsTests.cs                 # F04: Fluent chains
│   ├── MemoryAssertionExceptionsTests.cs        # Rich error messages
│   └── AssertionFlowTests.cs                    # Assertion combinations
│
├── Scenarios/                                    # Scenario library & generation
│   ├── MemoryScenariosTests.cs                  # F07: Built-in scenarios
│   ├── TemporalScenariosTests.cs                # F05: Temporal evaluation  
│   ├── ChattyScenariosTests.cs                  # F26: Noise scenarios
│   ├── NoiseGeneratorsTests.cs                  # Noise generation logic
│   └── ScenarioValidationTests.cs               # Scenario integrity checks
│
├── Evaluators/                                   # Specialized evaluators
│   ├── ReachBackEvaluatorTests.cs               # F27: Depth testing
│   ├── ReducerEvaluatorTests.cs                 # F10: Reducer fidelity
│   ├── CrossSessionEvaluatorTests.cs            # F09: Cross-session tests
│   └── EvaluatorIntegrationTests.cs             # Multi-evaluator scenarios
│
├── Metrics/                                      # IMetric implementations
│   ├── MemoryRetentionMetricTests.cs            # llm_memory_retention
│   ├── MemoryTemporalMetricTests.cs             # llm_memory_temporal
│   ├── MemoryReachBackMetricTests.cs            # code_memory_reachback
│   ├── MemoryReducerFidelityMetricTests.cs      # code_memory_reducer_fidelity
│   ├── MemoryNoiseResilienceMetricTests.cs      # llm_memory_noise_resilience
│   └── MetricIntegrationTests.cs                # Cross-metric validation
│
├── Benchmark/                                    # Benchmark suite
│   ├── MemoryBenchmarkRunnerTests.cs            # F18: Benchmark execution
│   ├── MemoryBenchmarkTests.cs                  # Preset benchmarks
│   └── BenchmarkResultTests.cs                  # Result aggregation
│
├── Adapters/                                     # Framework integration
│   ├── ChatClientAgentAdapterMemoryTests.cs     # MEAI session reset
│   ├── MAFAgentAdapterMemoryTests.cs             # MAF session reset  
│   └── MockAgentTests.cs                         # Test agent implementations
│
├── Performance/                                  # Performance & cost testing
│   ├── MemoryPerformanceTests.cs                # Execution speed tests
│   ├── CostTrackingTests.cs                     # API cost measurement
│   └── ScalabilityTests.cs                      # Load & scaling behavior
│
└── Integration/                                  # Full integration tests
    ├── EndToEndMemoryTests.cs                   # Complete workflows
    ├── CrossFrameworkTests.cs                   # MEAI vs MAF vs Custom
    └── RegressionTests.cs                        # Backward compatibility
```

### 18.2 Implementation Order & Testing Strategy

**The tests should be implemented IN THE SAME ORDER as the features**, following TDD principles:

#### **Phase 1: Foundation Tests (Week 1)**

```
IMPLEMENTATION ORDER: Write tests FIRST, then implementation
=====================================

1. MemoryTestScenarioTests.cs              ← F01: Core data models
2. MemoryQueryTests.cs                      ← Query construction logic  
3. MemoryFactTests.cs                       ← Fact representation
4. MemoryEvaluationResultTests.cs          ← Result aggregation
5. MemoryJudgeTests.cs                      ← F02: LLM judge (with FakeChatClient)
6. MemoryTestRunnerTests.cs                 ← F01: Orchestration engine
```

**Testing approach:**
- **Models**: Use property-based tests for validation rules
- **MemoryJudge**: Mock with `FakeChatClient` — NO external API calls in tests
- **MemoryTestRunner**: Use mock agents that return predictable responses

#### **Phase 2: Core Feature Tests (Week 2)**

```
7. CanRememberExtensionsTests.cs           ← F03: One-liner API
8. MemoryAssertionsTests.cs                ← F04: Fluent assertions
9. MemoryAssertionExceptionsTests.cs       ← Rich error messages
10. MemoryScenariosTests.cs                ← F07: Built-in scenarios
11. NoiseGeneratorsTests.cs                ← Noise generation
```

**Testing approach:**
- **Extensions**: Test both string-match and LLM-judge code paths
- **Assertions**: Verify Expected/Actual/Suggestions in exception messages
- **Scenarios**: Validate scenario integrity — proper fact/query relationships

#### **Phase 3: Advanced Feature Tests (Week 3-4)**

```
12. TemporalScenariosTests.cs              ← F05: Temporal evaluation
13. ChattyScenariosTests.cs                ← F26: Noise scenarios  
14. ReachBackEvaluatorTests.cs             ← F27: Depth testing
15. ReducerEvaluatorTests.cs               ← F10: Reducer fidelity
16. CrossSessionEvaluatorTests.cs          ← F09: Cross-session tests
```

**Testing approach:**
- **Temporal**: Test time-travel queries and fact timeline logic
- **Chatty**: Verify noise-to-signal ratios and fact burial
- **ReachBack**: Test degradation curves with predictable agents
- **Reducer**: Mock reducers with known information loss patterns

#### **Phase 4: Integration & Polish Tests (Week 5)**  

```
17. MemoryBenchmarkRunnerTests.cs          ← F18: Benchmark execution
18. AllMetricTests.cs                       ← All IMetric implementations
19. ChatClientAgentAdapterMemoryTests.cs   ← MEAI session reset
20. MAFAgentAdapterMemoryTests.cs          ← MAF session reset
21. EndToEndMemoryTests.cs                 ← Full integration
```

### 18.3 Testing Patterns & Best Practices

#### **Mock Agent Pattern**

```csharp
// Standard test agent for predictable memory testing
public class MockMemoryAgent : IEvaluableAgent, ISessionResettableAgent  
{
    private readonly Dictionary<string, string> _memoryStore = new();
    private readonly List<string> _conversationHistory = new();
    private bool _sessionActive = true;

    public string Name => "MockMemoryAgent";

    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct)
    {
        if (!_sessionActive)
            return Task.FromResult(new AgentResponse { Text = "Session not active" });

        _conversationHistory.Add(prompt);
        
        // Extract facts from prompts (test helper)
        if (prompt.StartsWith("Remember: "))
        {
            var fact = prompt.Substring(10);
            _memoryStore[GetFactKey(fact)] = fact;
            return Task.FromResult(new AgentResponse { Text = "I'll remember that." });
        }
        
        // Query facts from store (test helper)
        if (prompt.StartsWith("What do you remember about "))
        {
            var query = prompt.Substring(27).TrimEnd('?');
            var response = _memoryStore.Values
                .Where(fact => fact.Contains(query, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault() ?? "I don't recall that information.";
            return Task.FromResult(new AgentResponse { Text = response });
        }

        return Task.FromResult(new AgentResponse { Text = "I understand." });
    }

    public Task ResetSessionAsync(CancellationToken ct)
    {
        _conversationHistory.Clear();  // Clear session, keep memory store
        _sessionActive = true;
        return Task.CompletedTask;
    }
    
    // Test helpers
    public IReadOnlyDictionary<string, string> MemoryStore => _memoryStore;
    public IReadOnlyList<string> ConversationHistory => _conversationHistory;
    public void SimulateSessionEnd() => _sessionActive = false;
}
```

#### **FakeChatClient Pattern for MemoryJudge**

```csharp
[Fact]  
public async Task MemoryJudge_WithExpectedFact_ReturnsHighScore()
{
    // Arrange: Mock LLM response for fact verification
    var fakeLLM = new FakeChatClient("""
        {
            "found_facts": ["peanut allergy"],
            "missing_facts": [],
            "forbidden_found": [],
            "score": 95,
            "explanation": "Response clearly mentions the peanut allergy."
        }
        """);
    
    var judge = new MemoryJudge(fakeLLM);
    var query = new MemoryQuery
    {
        Question = "What allergies should I know about?",
        ExpectedFacts = ["peanut allergy"]
    };

    // Act
    var result = await judge.JudgeAsync(
        "You have a peanut allergy, so I should avoid recommending peanuts.", 
        query);

    // Assert
    result.Score.Should().BeGreaterThan(90);
    result.FoundFacts.Should().Contain("peanut allergy");
    result.MissingFacts.Should().BeEmpty();
}
```

#### **Property-Based Testing for Scenarios**

```csharp
[Theory]
[InlineData(1, 5)]   // 1 fact, 5 noise turns
[InlineData(3, 10)]  // 3 facts, 10 noise turns  
[InlineData(5, 25)]  // 5 facts, 25 noise turns
public void ChattyScenario_NoiseRatio_ProducesCorrectStructure(int factCount, int noisePerFact)
{
    // Arrange
    var facts = GenerateTestFacts(factCount);
    
    // Act
    var scenario = ChattyScenarios.BuriedFacts(facts, noisePerFact);
    
    // Assert
    scenario.Steps.Should().HaveCount(factCount * (noisePerFact + 1) + noisePerFact);
    scenario.Queries.Should().HaveCount(factCount);
    
    // Verify noise-to-signal ratio
    var factSteps = scenario.Steps.Where(s => facts.Any(f => s.Content.Contains(f.Content)));
    var noiseSteps = scenario.Steps.Except(factSteps);
    
    var actualRatio = (double)noiseSteps.Count() / factSteps.Count();
    actualRatio.Should().BeApproximately(noisePerFact, precision: 1.0);
}
```

---

## 19. Performance Framework & Benchmarking Strategy

### 19.1 What "Performance Benchmarking" Means for Memory Evaluation

**NOT hardcoded execution times** (those change with models/hardware), but rather:

1. **Performance Characteristics Analysis**: How execution patterns scale
2. **Cost Tracking & Optimization**: API token usage patterns  
3. **Relative Performance Baselines**: Comparative performance across scenarios
4. **Framework Performance Testing**: The evaluation infrastructure itself
5. **Performance Regression Detection**: Ensuring optimizations don't break

### 19.2 Performance Framework Architecture

```
PERFORMANCE FRAMEWORK COMPONENTS
=================================

┌─────────────────────────────────────────────────────────────┐
│                    Performance Framework                    │
│                                                             │
│  ┌─── Performance Measurement ─────────────────────────┐   │
│  │                                                     │   │
│  │  • IPerformanceTracker  ─── Execution timing       │   │
│  │  • ICostTracker         ─── API cost measurement   │   │
│  │  • IScalabilityTester   ─── Load scaling tests     │   │
│  │  • IMemoryProfiler     ─── Framework memory usage  │   │
│  │                                                     │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                             │
│  ┌─── Performance Benchmarks ──────────────────────────┐   │
│  │                                                     │   │
│  │  • QuickPerformanceBenchmark    ─── < 30 seconds   │   │
│  │  • StandardPerformanceBenchmark ─── < 5 minutes     │   │
│  │  • StressPerformanceBenchmark   ─── < 30 minutes    │   │
│  │                                                     │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                             │
│  ┌─── Performance Assertions ──────────────────────────┐   │
│  │                                                     │   │
│  │  result.Should()                                    │   │
│  │    .CompleteWithin(TimeSpan.FromSeconds(30))       │   │
│  │    .UseFewerthanTokens(1000)                       │   │
│  │    .CostLessThan(0.10m)                            │   │
│  │    .ScaleLinearlyWith(factCount)                   │   │
│  │                                                     │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

### 19.3 Performance Characteristics Analysis

Instead of hardcoded times, we define **performance characteristic patterns**:

```csharp
// Performance characteristics, not absolute timings
public class MemoryEvaluationPerformanceProfile
{
    // RELATIVE PATTERNS - these are predictable
    public required PerformancePattern ExecutionTimePattern { get; init; }      // O(n), O(n²), etc.
    public required PerformancePattern TokenUsagePattern { get; init; }         // Linear with fact count
    public required PerformancePattern CostPattern { get; init; }               // Cost scaling behavior
    
    // BASELINE COMPARISONS - relative to simple string matching
    public required double StringMatchingSpeedup { get; init; }                 // How much faster than LLM
    public required double LLMAccuracyImprovement { get; init; }                // How much more accurate
    
    // SCALING CHARACTERISTICS
    public required ScalingBehavior ConversationLengthScaling { get; init; }    // How it scales with turns
    public required ScalingBehavior FactCountScaling { get; init; }             // How it scales with facts
    public required ScalingBehavior NoiseRatioScaling { get; init; }            // How noise affects performance
}

public enum PerformancePattern
{
    Constant,           // O(1) - same time regardless of input size
    Linear,             // O(n) - time increases linearly with input
    LogLinear,          // O(n log n) - slightly worse than linear  
    Quadratic,          // O(n²) - time increases quadratically
    Exponential         // O(2^n) - avoid this!
}
```

### 19.4 Performance Benchmarks & Expected Characteristics

```csharp
public static class PerformanceBenchmarks
{
    // Quick benchmark - for CI/CD
    public static MemoryPerformanceBenchmark Quick => new()
    {
        Name = "Quick Performance Check",
        MaxDuration = TimeSpan.FromSeconds(30),
        MaxCost = 0.02m,  // $0.02 maximum
        
        Scenarios = 
        [
            MemoryScenarios.BasicRetention(factCount: 3),    // Expected: ~5 seconds
            MemoryScenarios.ChattyConversation(2, 5),        // Expected: ~8 seconds
        ],
        
        ExpectedCharacteristics = new()
        {
            ExecutionTimePattern = PerformancePattern.Linear,       // O(n) with fact count
            TokenUsagePattern = PerformancePattern.Linear,          // ~100 tokens per fact
            CostPattern = PerformancePattern.Linear,                // ~$0.003 per fact
            ConversationLengthScaling = ScalingBehavior.Linear,     // Time scales with turns
            FactCountScaling = ScalingBehavior.Linear,              // Time scales with facts
            NoiseRatioScaling = ScalingBehavior.LogLinear,         // Slight overhead for noise
        }
    };
    
    // Standard benchmark - for regression testing  
    public static MemoryPerformanceBenchmark Standard => new()
    {
        Name = "Standard Performance Benchmark",
        MaxDuration = TimeSpan.FromMinutes(5),
        MaxCost = 0.25m,  // $0.25 maximum
        
        Scenarios =
        [
            MemoryScenarios.BasicRetention(5),                      // Expected: ~15 seconds
            MemoryScenarios.ChattyConversation(3, 10),             // Expected: ~45 seconds  
            MemoryScenarios.ReachBack(depth: 25),                  // Expected: ~60 seconds
            MemoryScenarios.TemporalReasoning(),                   // Expected: ~30 seconds
        ]
    };
    
    // Stress benchmark - for capacity planning
    public static MemoryPerformanceBenchmark Stress => new()
    {
        Name = "Stress Performance Benchmark", 
        MaxDuration = TimeSpan.FromMinutes(30),
        MaxCost = 2.0m,  // $2.00 maximum
        
        Scenarios =  
        [
            MemoryScenarios.BasicRetention(25),                     // Test high fact count
            MemoryScenarios.ChattyConversation(10, 50),            // Test high noise ratio
            MemoryScenarios.ReachBack(depth: 200),                 // Test deep reach-back
            MemoryScenarios.ReducerStress(turnCount: 100),         // Test reducer limits
        ]
    };
}
```

### 19.5 Performance Assertions & Testing

```csharp
public static class PerformanceAssertions
{
    [StackTraceHidden]
    public static PerformanceAssertions Should(this MemoryEvaluationResult result)
        => new(result);
}

public class PerformanceAssertions
{
    private readonly MemoryEvaluationResult _result;

    [StackTraceHidden]
    public PerformanceAssertions CompleteWithin(TimeSpan maxDuration, string? because = null)
    {
        if (_result.Duration > maxDuration)
        {
            AgentEvalScope.FailWith(new PerformanceAssertionException(
                $"Memory evaluation took {_result.Duration:g}, expected under {maxDuration:g}",
                expected: maxDuration.ToString(),
                actual: _result.Duration.ToString(), 
                because: because,
                suggestions: 
                [
                    "Consider using fewer facts in the scenario",
                    "Reduce noise ratio in chatty conversations", 
                    "Use string matching instead of LLM judge for simple cases",
                    "Check if the agent's memory system is optimized"
                ]));
        }
        return this;
    }

    [StackTraceHidden] 
    public PerformanceAssertions UseFewerThanTokens(int maxTokens, string? because = null)
    {
        if (_result.TokensUsed > maxTokens)
        {
            AgentEvalScope.FailWith(new PerformanceAssertionException(
                $"Memory evaluation used {_result.TokensUsed} tokens, expected under {maxTokens}",
                expected: maxTokens.ToString(),
                actual: _result.TokensUsed.ToString(),
                because: because,
                suggestions:
                [
                    "Reduce the number of queries in the scenario",
                    "Use simpler query phrasing to reduce prompt length",
                    "Consider batch processing multiple facts per query"
                ]));
        }
        return this;
    }

    [StackTraceHidden]
    public PerformanceAssertions CostLessThan(decimal maxCost, string? because = null)  
    {
        if (_result.EstimatedCost > maxCost)
        {
            AgentEvalScope.FailWith(new PerformanceAssertionException(
                $"Memory evaluation cost ${_result.EstimatedCost:F4}, expected under ${maxCost:F4}",
                expected: $"${maxCost:F4}",
                actual: $"${_result.EstimatedCost:F4}", 
                because: because,
                suggestions:
                [
                    "Use cheaper models for memory evaluation (e.g., gpt-4o-mini)",
                    "Reduce LLM calls by using string matching when appropriate",
                    "Batch multiple fact checks into single LLM queries"
                ]));
        }
        return this;
    }
}
```

### 19.6 Performance Regression Testing

```csharp
[Fact]
public async Task MemoryBenchmark_Standard_MeetsPerformanceCharacteristics()
{
    // Arrange
    var agent = new MockMemoryAgent();
    var benchmark = PerformanceBenchmarks.Standard;
    
    // Act
    var result = await _runner.RunMemoryBenchmarkAsync(agent, benchmark);
    
    // Assert - Performance characteristics, not absolute times
    result.Should()
        .CompleteWithin(benchmark.MaxDuration)
        .CostLessThan(benchmark.MaxCost)
        .ShowLinearScalingWith(factCount: result.TotalFacts)
        .HaveTokenEfficiencyAbove(0.8); // 80% of tokens used effectively
}

[Theory]
[InlineData(1, 5)]     // 1 fact, expect ~5 sec baseline
[InlineData(5, 25)]    // 5 facts, should be ~5x slower (linear scaling)  
[InlineData(10, 50)]   // 10 facts, should be ~10x slower
public async Task BasicRetention_Scaling_ShowsLinearPerformance(int factCount, int expectedSeconds)
{
    // Arrange  
    var scenario = MemoryScenarios.BasicRetention(factCount);
    var agent = new MockMemoryAgent();
    
    // Act
    var result = await _runner.RunAsync(agent, scenario);
    
    // Assert - Linear scaling characteristic (±50% tolerance for variance)
    var expectedDuration = TimeSpan.FromSeconds(expectedSeconds);
    var tolerance = TimeSpan.FromSeconds(expectedSeconds * 0.5); // ±50%
    
    result.Duration.Should().BeCloseTo(expectedDuration, tolerance);
    
    // Verify linear relationship holds
    var actualSecondsPerFact = result.Duration.TotalSeconds / factCount;
    actualSecondsPerFact.Should().BeInRange(3, 8); // 3-8 seconds per fact is reasonable
}
```

---

## 20. Sample Applications & Developer Experience

### 20.1 Integration with AgentEval.Samples

Add memory evaluation samples to the existing samples project, following the established pattern:

```
samples/AgentEval.Samples/
├── Program.cs                        # ← Update menu with memory samples
├── Sample01_HelloWorld.cs    
├── Sample02_ToolAssertions.cs
├── ...
├── Sample28_MemoryBasics.cs          # ← NEW: Memory evaluation intro
├── Sample29_MemoryBenchmark.cs       # ← NEW: Full memory benchmark  
├── Sample30_CustomMemoryAgent.cs     # ← NEW: Build custom memory agent
├── Sample31_CrossFrameworkMemory.cs  # ← NEW: MEAI vs MAF memory comparison
└── Sample32_PerformanceOptimization.cs # ← NEW: Memory performance tuning
```

### 20.2 Sample 28: Memory Evaluation Basics  

**Key Features Demonstrated:**
- Memory-enabled agent setup (MEAI example)
- One-liner memory tests (`CanRememberAsync`)
- Multi-fact memory scenarios
- Fluent memory assertions
- Performance assertions

### 20.3 Sample 29: Full Memory Benchmark

**Key Features Demonstrated:**
- Quick vs Standard benchmarks
- Beautiful ASCII results table with star ratings
- Performance analysis and recommendations
- Cost tracking
- Cross-framework compatibility demonstration

### 20.4 Sample 30: Custom Memory Agent

**Key Features Demonstrated:**
- Building a custom `IEvaluableAgent` with memory
- Implementing `ISessionResettableAgent` 
- Memory store internals and conversation history separation
- Session reset behavior testing
- Custom memory search logic

### 20.5 Sample 31: Cross-Framework Memory Comparison

**Key Features Demonstrated:**
- Testing MEAI, MAF, and Custom agents side-by-side
- Comparative performance analysis
- Framework-specific insights and recommendations  
- Memory benchmark score comparisons
- Cross-framework compatibility verification

### 20.6 Menu Integration

```csharp
// Update Program.cs to include memory samples
private static readonly ISample[] Samples =
[
    // ... existing samples ...
    new Sample28_MemoryBasics(),
    new Sample29_MemoryBenchmark(), 
    new Sample30_CustomMemoryAgent(),
    new Sample31_CrossFrameworkMemory(),
    new Sample32_PerformanceOptimization(),
];
```

---

## Final Words 🎸🤘

This implementation plan is designed to be **built incrementally, tested continuously, and shipped confidently**. Each phase delivers standalone value:

- **Phase 0** → You can test basic memory retention (ANY agent framework!)
- **Phase 1** → You can test temporal reasoning and chatty conversations  
- **Phase 2** → You can benchmark any agent holistically + MAF scope analysis
- **Phase 3** → Ship it, document it, rock it

The architecture is **framework-universal by design**:
- **11 of 12 features** work with ANY `IEvaluableAgent` (MEAI, MAF, OpenAI, custom)
- **1 feature** (F08 Scope Misconfig) is MAF-only — lives in `AgentEval.MAF`
- **New `ISessionResettableAgent`** enables cross-session and reach-back testing universally
- **Existing adapters** (`ChatClientAgentAdapter`, `MAFAgentAdapter`) already support memory testing

The principles hold:
- **SOLID** — Single responsibility per class, interfaces for everything
- **DRY** — Reuses existing infrastructure (DI, metrics, assertions, exporters)
- **KISS** — No premature abstractions, no over-engineering
- **CLEAN** — Dependencies flow inward, core has zero external deps

Nobody in the .NET ecosystem — or any ecosystem — has a **framework-agnostic memory evaluation toolkit** like this. Whether you're using MEAI, MAF, Semantic Kernel, or rolling your own — AgentEval.Memory tests your agent's memory behavior. Period.

**This is where AgentEval goes from "good evaluation toolkit" to "the evaluation toolkit nobody can ignore."**

Now let's turn the amps to 11 and build it! 🎸⚡🤘

---

**Author:** AgentEval Engineering  
**Rock Level:** 11/10 🔊  
**Last Updated:** 2026-03-09

---

### Changelog

| Date | Changes |
|------|---------|
| 2026-03-07 | Initial creation — 13 sections, 12 MUST HAVE features |
| 2026-03-07 | Added Section 4 (Framework Compatibility Analysis) |
| 2026-03-07 | Added Section 13 (F08 & F09 full plans) |
| 2026-03-07 | **Gap Analysis Fixes (14 issues found, see GapAnalysis.md):** |
| | — H1: Fixed Section 5 subsection numbering (4.x → 5.x) |
| | — H2: Fixed DI registration to use configurable lifetime pattern |
| | — H3: Added `AddAgentEvalMAF()` prerequisite note for F08 |
| | — M1: Fixed `MemoryStep.DelayBefore` → `DelayAfter` |
| | — M2: Replaced `IMemoryMetric.RequiresLLM` with `RequiresSessionReset` |
| | — M3: Fixed `MemoryRetentionMetric` prefix from `code_` to `llm_` |
| | — L2: Added MAF rc3 API verification note for F08 |
| | — L4: Preserved `configure` parameter in `AddAgentEvalAll()` update |
| 2026-03-09 | **Section 17 expanded:** Added detailed explanation of "Double Down on Agentic Evaluation" and "Integrating memory providers with evaluation framework" — simple + detailed explanations |
| 2026-03-09 | **Section 17.5 expanded:** Added 17.5.1 "Deep Analysis" with pros/cons table for standalone NuGet, standalone viability assessment, AgentEval.RedTeam comparison, and clear recommendation to ship via umbrella only |
| 2026-03-09 | **Added Annex A:** Full Feature List & Prioritization — all 29 features (F01-F29) ported from RC3 Feature Analysis with descriptions, requirements, scoring rationale, and tier assignments |

---

## Annex A: Full Feature List & Prioritization

> **Source:** Section 21 and Section 34 of [AgentEval-Feature-Analysis-MemoryEvaluation-RC3.md](AgentEval-Feature-Analysis-MemoryEvaluation-RC3.md)  
> **Scoring methodology:** Each feature rated on 3 axes: Relevance (30%), Developer Experience (30%), Quality Impact (40%). Quality Impact gets the highest weight because the ultimate purpose of evaluation is to make agents better.  
> **Formula:** Final Score = (Relevance × 0.30) + (DevEx × 0.30) + (QualityImpact × 0.40)

### Master Scoring Table

| ID | Feature | Universal? | Relevance | DevEx | Quality Impact | Final Score | Tier |
|----|---------|:----------:|:---------:|:-----:|:--------------:|:-----------:|:----:|
| **F01** | Core Engine | ✅ | 10 | 8 | 9 | **9.0** | **S** |
| **F02** | LLM-as-Judge for Memory | ✅ | 9 | 8 | 9 | **8.7** | **S** |
| **F03** | CanRememberAsync One-Liner | ✅ | 8 | **10** | 7 | **8.2** | **S** |
| **F04** | Fluent Memory Assertions | ✅ | 7 | **10** | 6 | **7.5** | **S** |
| **F05** | Temporal Memory Evaluation | ✅ | 9 | 7 | **10** | **8.8** | **S** |
| **F06** | Memory Result Export | ✅ | 6 | 8 | 4 | **5.8** | B |
| **F07** | Built-in Scenario Library | ✅ | 8 | **9** | 8 | **8.3** | **S** |
| **F08** | Scope Misconfiguration Detection | **MAF** | 7 | 9 | **9** | **8.4** | **S** ⬆ |
| **F09** | Cross-Session Persistence Testing | ✅* | 9 | 7 | **9** | **8.4** | **S** ⬆ |
| **F10** | Chat Reducer Evaluation | ✅ | 8 | 7 | **9** | **8.1** | **S** |
| **F11** | Provider-Level Introspection | MAF | 7 | 6 | 8 | **7.1** | B+ |
| **F12** | Memory Diff/Regression Testing | ✅ | 7 | 8 | 7 | **7.3** | B+ |
| ~~**F13**~~ | ~~Scope Isolation Testing~~ | — | — | — | — | — | **REMOVED** |
| **F14** | Memory Stress Testing | ✅ | 7 | 6 | **9** | **7.5** | A |
| **F15** | Selective Forgetting / GDPR | ✅ | 8 | 6 | **10** | **8.2** | A |
| **F16** | Adversarial Memory Attacks | ✅ | 8 | 5 | **10** | **7.9** | A |
| **F17** | Memory Security Scan One-Liner | ✅ | 7 | **9** | 8 | **8.0** | A |
| **F18** | Memory Benchmark Suite | ✅ | 8 | 8 | 7 | **7.6** | **S** |
| **F19** | Memory Consistency Oracle | ✅ | 7 | 5 | **10** | **7.6** | A |
| **F20** | Generative Scenario Generation | ✅ | 5 | 7 | 6 | **6.0** | B |
| **F21** | Embedding Model Impact Analysis | ✅ | 6 | 6 | 7 | **6.4** | B |
| **F22** | Memory State Visualization | ✅ | 4 | 8 | 3 | **4.8** | C |
| **F23** | Emotional Memory Evaluation | ✅ | 5 | 5 | 6 | **5.4** | B- |
| **F24** | Memory as Data Quality Testing | ✅ | 5 | 5 | 6 | **5.4** | C |
| **F25** | Memory Time Machine | ✅ | 5 | 4 | 7 | **5.5** | C |
| **F26** | Chatty Conversation Scenarios | ✅ | **9** | 8 | **10** | **9.1** | **S** |
| **F27** | Reach-Back Depth Testing | ✅* | **9** | 7 | **10** | **8.8** | **S** |
| **F28** | Token-Aware Context Evaluation | ✅ | **8** | 6 | **9** | **7.8** | A |
| **F29** | Conversation Topology Testing | ✅ | 7 | 6 | **9** | **7.5** | A |

`⬆` = Promoted from A to S tier. `*` = Requires `ISessionResettableAgent` for full capability.

### Tier Summary

| Tier | Score Range | Count | Features |
|------|-----------|:-----:|----------|
| **S** (Must-Have) | 7.5+ (curated) | 12 | F01, F02, F03, F04, F05, F07, F08, F09, F10, F18, F26, F27 |
| **A** (High-Value) | 7.5 - 8.2 | 7 | F14, F15, F16, F17, F19, F28, F29 |
| **B+** (Solid) | 7.0 - 7.4 | 2 | F11, F12 |
| **B** (Nice-to-Have) | 5.5 - 6.9 | 3 | F06, F20, F21 |
| **B-** | 5.4 | 2 | F23, F24 |
| **C** (Low Priority) | < 5.5 | 3 | F22, F24, F25 |
| **REMOVED** | — | 1 | F13 (isolated by MAF design) |

---

### Feature-by-Feature Descriptions

#### F01: Core Engine (MemoryTestScenario, Runner, Result Model) — Tier S, Score 9.0

**What it is:** The foundation of all memory evaluation. Provides `MemoryTestScenario` (define: steps to plant facts, queries to test recall), `MemoryTestRunner` (orchestrates: send messages → query → judge), and `MemoryEvaluationResult` (structured results with per-fact scores, pass/fail, timing).

**What it provides:** A 3-phase execution model: (1) Seed facts via conversation turns, (2) Execute queries that test recall, (3) Judge responses with LLM-as-judge. Comprehensive result model with per-query scoring, aggregation, and performance tracking.

**What it requires:** `IEvaluableAgent` (any agent), `IChatClient` (for LLM judging), scenario definition.

**Scoring rationale:**
- *Relevance (10)*: Without this, nothing else works. It IS the memory evaluation engine.
- *DevEx (8)*: Scenario definition is straightforward but requires understanding the 3-phase model. Not a one-liner (that's F03).
- *Quality Impact (9)*: Directly reveals whether agents remember facts. Catches retention failures, temporal errors, confusion.
- *Tier S*: Foundation dependency for every other feature.

---

#### F02: LLM-as-Judge for Memory — Tier S, Score 8.7

**What it is:** `MemoryJudge` — a structured LLM prompt that evaluates whether an agent's response demonstrates genuine recall of a planted fact. Uses `IChatClient` with JSON-structured output (score 0-100, explanation, confidence).

**What it provides:** Reliable fact verification beyond keyword matching. Handles paraphrasing ("I'm José" → "Your name is José"), partial recall, and nuanced responses. Fallback parsing for malformed LLM responses.

**What it requires:** `IChatClient` (LLM for judging — typically a separate, cheaper model than the agent under test). Costs ~$0.001-0.01 per judgment call.

**Scoring rationale:**
- *Relevance (9)*: Memory recall is inherently semantic — string matching is insufficient. LLM judging is the only reliable approach.
- *DevEx (8)*: Transparent to the user — happens automatically inside `MemoryTestRunner`. Users see scores, not the judging internals.
- *Quality Impact (9)*: Catches subtle recall failures (partial recall, confabulation, wrong context) that keyword matching would miss.
- *Tier S*: Required by F01 for accurate scoring.

---

#### F03: CanRememberAsync One-Liner — Tier S, Score 8.2

**What it is:** A single extension method: `await agent.CanRememberAsync("I told you I'm allergic to peanuts", "What are my food allergies?")`. Returns a `MemoryEvaluationResult` with pass/fail, score, and explanation.

**What it provides:** The "Hello World" of memory evaluation. Zero ceremony — no scenario definition, no runner setup, no configuration. Tell the agent a fact, ask about it, get a result.

**What it requires:** `IEvaluableAgent`, `IChatClient` (for judging). Optionally registered via DI.

**Scoring rationale:**
- *Relevance (8)*: Covers the most common use case (did the agent remember?) but lacks the power of full scenarios.
- *DevEx (10)*: Perfect 10 — one line of code. Cannot be simpler. This is what hooks developers.
- *Quality Impact (7)*: Tests basic retention only. Doesn't test temporal reasoning, cross-session, or noise resilience.
- *Tier S*: The gateway drug that gets developers into memory evaluation.

---

#### F04: Fluent Memory Assertions — Tier S, Score 7.5

**What it is:** `.Should()` extension methods on `MemoryEvaluationResult` following the existing AgentEval assertion pattern. Chains like `.Should().HavePassed().WithMinScore(0.8).WithFactRecalled("allergy")`.

**What it provides:** Natural-language assertion chains that integrate with xUnit/NUnit test frameworks. Structured `MemoryAssertionException` with Expected/Actual/Suggestions/Because fields matching the AgentEval assertion pattern.

**What it requires:** `MemoryEvaluationResult` from F01 or F03. Uses the `AgentEvalScope.FailWith()` pattern from Core.

**Scoring rationale:**
- *Relevance (7)*: Assertions are an ergonomic layer, not a capability layer. You can check results without fluent chains.
- *DevEx (10)*: Perfect 10 — developers love fluent assertions. `.Should().HavePassed()` reads like English.
- *Quality Impact (6)*: Doesn't find more bugs than checking result properties manually. But makes it much easier to express expectations.
- *Tier S*: Consistency with AgentEval's existing assertion patterns is essential for a unified developer experience.

---

#### F05: Temporal Memory Evaluation — Tier S, Score 8.8

**What it is:** Tests whether agents correctly reason about time-dependent facts. "I lived in Madrid until 2024, then moved to Copenhagen." Query: "Where do I live now?" The agent must understand that facts have temporal validity and that newer facts supersede older ones.

**What it provides:** `TemporalMemoryScenario` with timestamped facts that evolve over time. Tests: fact supersession (old address → new address), time-bounded validity ("meeting at 3pm tomorrow" is irrelevant next week), temporal ordering (which happened first?).

**What it requires:** `IEvaluableAgent`, `IChatClient` (for LLM judging with temporal awareness). `MemoryFact` model with `ValidFrom`/`ValidUntil` temporal metadata.

**Scoring rationale:**
- *Relevance (9)*: Every production agent has temporal facts. Address changes, preference updates, schedule changes — all temporal.
- *DevEx (7)*: Temporal scenarios are more complex to define than basic retention. Requires understanding fact evolution.
- *Quality Impact (10)*: Temporal bugs are the #1 production memory failure. Agent recommends your old restaurant, uses your old address, references expired plans. Catching these is critical.
- *Tier S*: The hardest memory problem and the most impactful to test.

---

#### F06: Memory Result Export — Tier B, Score 5.8

**What it is:** Export `MemoryEvaluationResult` to JSON, Markdown, CSV, and JUnit XML formats for CI/CD integration and reporting.

**What it provides:** Machine-readable memory evaluation reports. CI pipeline integration (JUnit for Azure DevOps/GitHub Actions). Human-readable Markdown summaries.

**What it requires:** `MemoryEvaluationResult`. Leverages existing `AgentEval.DataLoaders` export infrastructure.

**Scoring rationale:**
- *Relevance (6)*: Useful for CI/CD but not critical for discovering memory bugs.
- *DevEx (8)*: Well-understood format. Developers know how to consume JSON/JUnit.
- *Quality Impact (4)*: Export doesn't find bugs — it reports them. Low direct quality impact.
- *Tier B*: Nice-to-have. Existing DataLoaders infrastructure makes this easy to add later.

---

#### F07: Built-in Scenario Library — Tier S, Score 8.3

**What it is:** A curated collection of pre-built `MemoryTestScenario` instances covering common memory testing patterns: basic retention, fact updates, multi-fact recall, chatty conversations (F26), temporal evolution (F05).

**What it provides:** `MemoryScenarios.BasicRetention()`, `MemoryScenarios.FactUpdate()`, `MemoryScenarios.ChattyConversation()`, etc. Saves developers from writing scenarios from scratch. Includes `MemoryStressSuite` for load testing.

**What it requires:** F01 (Core Engine) for execution. No external dependencies.

**Scoring rationale:**
- *Relevance (8)*: Most developers don't know what to test. Built-in scenarios answer "what should I test?"
- *DevEx (9)*: One-liner scenario selection. `MemoryScenarios.BasicRetention()` → done.
- *Quality Impact (8)*: Curated scenarios are designed to catch real-world bugs. Better coverage than ad-hoc tests.
- *Tier S*: Massive time-saver. Makes the framework immediately useful without scenario authoring expertise.

---

#### F08: Scope Misconfiguration Detection — Tier S, Score 8.4

**What it is:** Static analysis of MAF memory provider scope configuration. Detects the #1 configuration bug: `SessionId` in `SearchScope` (which blocks cross-session recall). Also detects: dimension mismatch between StorageScope and SearchScope, missing user isolation, overly broad scopes.

**What it provides:** `code_scope_misconfiguration` metric — zero LLM cost, instant results. Actionable warnings: "Your SearchScope includes SessionId — this prevents cross-session memory recall. Remove SessionId from SearchScope to enable cross-session search."

**What it requires:** **MAF-only.** Needs `AIAgent` with `AIContextProviders` array. Inspects `ProviderSessionState<State>` for scope configuration. Lives in `AgentEval.MAF`, not `AgentEval.Memory`.

**Scoring rationale:**
- *Relevance (7)*: MAF-specific, so not universal. But for MAF users, this is the single most common config bug.
- *DevEx (9)*: Zero-cost, instant feedback, actionable suggestions. "Run this, get warnings."
- *Quality Impact (9)*: Catches a bug that causes hours of debugging. "Why doesn't my agent remember across sessions?" → "Your SearchScope has SessionId."
- *Tier S*: Promoted from A-tier because it's free (no LLM cost), instant, and catches the most common MAF memory bug.

---

#### F09: Cross-Session Persistence Testing — Tier S, Score 8.4

**What it is:** Tests whether an agent's memory survives session boundaries. Plant a fact in session 1, reset the session, query in session 2. If the agent recalls the fact, its memory is truly persistent (not just in-context).

**What it provides:** `ICrossSessionEvaluator` + `CrossSessionEvaluator` that orchestrates: (1) seed facts, (2) reset session via `ISessionResettableAgent.ResetSessionAsync()`, (3) query in fresh session, (4) judge recall. `CrossSessionResult` with per-fact persistence scores.

**What it requires:** `ISessionResettableAgent` interface for session reset capability. Both `ChatClientAgentAdapter` and `MAFAgentAdapter` implement this. Graceful degradation (skip cross-session tests) if agent doesn't implement the interface.

**Scoring rationale:**
- *Relevance (9)*: Cross-session persistence IS the value proposition of memory providers. Without this test, you're testing chat history, not memory.
- *DevEx (7)*: Requires `ISessionResettableAgent` implementation. Slightly more setup than basic tests.
- *Quality Impact (9)*: The #1 question for production agents: "Does memory actually persist across sessions?" This answers it definitively.
- *Tier S*: Promoted from A-tier. The reason memory providers exist. If you can't test this, memory evaluation is incomplete.

---

#### F10: Chat Reducer Evaluation — Tier S, Score 8.1

**What it is:** Evaluates the information loss caused by `IChatReducer` implementations (like `MessageCountingChatReducer`). Seeds N diverse facts in a conversation, applies a reducer, then tests which facts survive reduction.

**What it provides:** `IReducerEvaluator` + `ReducerEvaluator` that measures: (1) fact survival rate (what % of facts are still recalled after reduction), (2) critical fact loss (were safety-critical facts like allergies lost?), (3) comparison across reducer configurations (keep-5 vs keep-10 vs keep-20).

**What it requires:** `IEvaluableAgent` with a configured `IChatReducer`. `IChatClient` for LLM judging of recall quality.

**Scoring rationale:**
- *Relevance (8)*: Every agent with a ChatHistoryProvider uses a reducer. Nobody measures the quality impact.
- *DevEx (7)*: Requires understanding reducers and configuring test scenarios. Not a one-liner.
- *Quality Impact (9)*: **Nobody is measuring this properly.** The reducer silently loses information. Which facts survive is essentially random. This reveals a massive blind spot.
- *Tier S*: A unique capability. No other framework measures reducer information loss.

---

#### F11: Provider-Level Introspection — Tier B+, Score 7.1

**What it is:** White-box inspection of memory provider internals: what's stored in `ChatHistoryProvider`'s message list, what's in the vector store, what Mem0 has extracted. Uses `GetService<T>()` and direct provider APIs.

**What it provides:** Direct visibility into provider state without going through LLM. "Show me exactly what the ChatHistoryMemoryProvider has stored." Debug facility for when black-box tests fail.

**What it requires:** MAF-specific APIs: `provider.GetMessages(session)`, `GetService<T>()`. Not available for all provider types (Foundry extraction is opaque).

**Scoring rationale:**
- *Relevance (7)*: Useful for debugging but not a primary evaluation tool.
- *DevEx (6)*: Requires provider-specific API knowledge. Each provider has different introspection APIs.
- *Quality Impact (8)*: When black-box tests fail, introspection reveals WHY. Significantly reduces debugging time.
- *Tier B+*: A debugging tool, not a primary evaluation feature. Valuable but not essential.

---

#### F12: Memory Diff/Regression Testing — Tier B+, Score 7.3

**What it is:** Compare memory evaluation results across runs/versions/configurations. Detect regressions: "Version 2.1 recalls 95% of facts, version 2.2 only recalls 80% — regression!"

**What it provides:** Baseline capture (save a "known good" result), comparison (diff against baseline), regression alerts (scores dropped by > threshold).

**What it requires:** F01 (Core Engine) results from two runs. Storage for baseline results (file system or CI artifact).

**Scoring rationale:**
- *Relevance (7)*: Important for production CI but not for initial evaluation.
- *DevEx (8)*: Straightforward compare-and-alert pattern. Developers understand regression testing.
- *Quality Impact (7)*: Catches regressions but doesn't find new bugs. Preventive, not diagnostic.
- *Tier B+*: Solid CI/CD feature for mature deployments. Not needed in MVP.

---

#### F13: Scope Isolation Testing — REMOVED

**What it was:** Test that User A's memories are invisible to User B.

**Why removed:** MAF enforces scope isolation by design. `StorageScope` and `SearchScope` have explicit dimensions (`UserId`, `SessionId`). There's no mechanism for cross-scope leakage unless the developer explicitly misconfigures scopes — which F08 already detects. Testing scope isolation would be testing MAF's guarantees, not the agent's memory quality.

---

#### F14: Memory Stress Testing — Tier A, Score 7.5

**What it is:** Test memory behavior under extreme conditions: 100+ turns, 50+ facts, rapid-fire messages, mixed topics, long conversations. Measure recall degradation over time.

**What it provides:** `MemoryStressSuite` with parameterized load profiles. Answers: "At what conversation length does memory start failing?" and "Which facts are lost first under stress?"

**What it requires:** `IEvaluableAgent`, significant LLM cost for long conversations + judging (100+ turns × judge calls).

**Scoring rationale:**
- *Relevance (7)*: Important for production readiness but not the first thing to test.
- *DevEx (6)*: Stress tests are slow (minutes, not seconds). Requires patience and budget.
- *Quality Impact (9)*: Reveals degradation patterns invisible in short tests. Critical for production deployment decisions.
- *Tier A*: High value but expensive to run. Phase 2 feature.

---

#### F15: Selective Forgetting / GDPR Compliance — Tier A, Score 8.2

**What it is:** Test whether memory deletion APIs actually work. Seeds sensitive PII, calls `ClearStoredMemoriesAsync()` (Mem0) or `EnsureStoredMemoriesDeletedAsync()` (Foundry), then verifies the data is truly gone from responses AND provider state.

**What it provides:** GDPR/CCPA compliance verification. Answers: "If a user requests deletion, is their data actually removed from agent memory?"

**What it requires:** Agent with memory deletion API. Provider-specific: `Mem0Provider.ClearStoredMemoriesAsync()`, `FoundryMemoryProvider.EnsureStoredMemoriesDeletedAsync()`. Not all providers support deletion.

**Scoring rationale:**
- *Relevance (8)*: GDPR compliance is a legal requirement in EU markets. Critical for enterprise deployment.
- *DevEx (6)*: Requires provider-specific deletion APIs. Not universal across all memory providers.
- *Quality Impact (10)*: A failed deletion = legal liability. Extremely high impact when it fails.
- *Tier A*: Critical for compliance but requires provider-specific APIs. Phase 2 feature.

---

#### F16: Adversarial Memory Attacks — Tier A, Score 7.9

**What it is:** Six categories of memory-specific security attacks: memory poisoning, memory extraction, memory flooding, cross-scope probing, temporal manipulation, embedding inversion. Based on OWASP LLM Top 10 and MITRE ATLAS.

**What it provides:** Specialized attack scenarios for memory systems. Tests beyond prompt injection — tests persistent attacks that survive across sessions. OWASP coverage jumps from 6/10 to 8/10 with memory attacks.

**What it requires:** F01 (Core Engine) + F07 (Scenario Library). Attack scenarios are specialized `MemoryTestScenario` instances. LLM for judging attack success.

**Scoring rationale:**
- *Relevance (8)*: Memory is a persistent attack surface — the most dangerous kind. Attacks persist across sessions.
- *DevEx (5)*: Security testing is inherently complex. Requires understanding attack categories and configuring scenarios.
- *Quality Impact (10)*: A successful memory attack can poison ALL future responses. Catastrophic impact.
- *Tier A*: High impact but complex to implement. Builds on F01/F07. Phase 2 with integration into AgentEval.RedTeam.

---

#### F17: Memory Security Scan One-Liner — Tier A, Score 8.0

**What it is:** `await agent.QuickMemorySecurityScanAsync()` — packages all F16 attack categories into a single call. The memory equivalent of AgentEval.RedTeam's quick scan.

**What it provides:** One-liner that runs all 6 attack categories (poisoning, extraction, flooding, cross-scope, temporal, embedding) and returns a structured security report with OWASP/MITRE mappings.

**What it requires:** F16 (Adversarial Memory Attacks) must be implemented first. `IChatClient` for LLM judging.

**Scoring rationale:**
- *Relevance (7)*: Important but derivative of F16. The one-liner itself isn't the innovation — the attack categories are.
- *DevEx (9)*: One line, full security scan. Extremely easy to adopt.
- *Quality Impact (8)*: Same as F16 but through an accessible interface. More developers will actually run it.
- *Tier A*: Depends on F16. Ship together as a Phase 2 security package.

---

#### F18: Memory Benchmark Suite — Tier S, Score 7.6

**What it is:** `IMemoryBenchmarkRunner` that orchestrates a standardized battery of memory tests and produces a holistic quality score. Three presets: Quick (5 facts, basic recall), Standard (20 facts, temporal + noise), Full (50+ facts, cross-session + stress + chatty).

**What it provides:** A single "memory quality score" comparable across agents, providers, and configurations. Graded results (A-F) with breakdown by category (retention, temporal, noise resilience, cross-session).

**What it requires:** F01, F02, F05, F07, F09, F10, F26, F27 — it's an orchestrator that combines multiple features into one run.

**Scoring rationale:**
- *Relevance (8)*: "How good is my agent's memory?" answered in one number.
- *DevEx (8)*: One call: `await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Standard)`.
- *Quality Impact (7)*: Comprehensive but broad. Individual features (F05 temporal, F27 reach-back) provide deeper insight.
- *Tier S*: The "executive summary" that makes all other features accessible.

---

#### F19: Memory Consistency Oracle — Tier A, Score 7.6

**What it is:** A ground-truth oracle that knows exactly which facts should be remembered at any point in the conversation. Fed a `FactSet` (all seeded facts with temporal metadata), it evaluates whether the agent's response is consistent with the known fact state.

**What it provides:** Deterministic fact verification. Instead of asking "does the LLM think the agent remembered?" it checks "does the response match our ground-truth facts?" Detects contradictions, confabulations, and hallucinated memories.

**What it requires:** `FactSet` definition (manual or generated from scenarios). `IChatClient` for LLM-based consistency checking.

**Scoring rationale:**
- *Relevance (7)*: Powerful for advanced evaluation but requires more setup than basic judging (F02).
- *DevEx (5)*: Requires building a `FactSet` — more effort than simple scenarios.
- *Quality Impact (10)*: The gold standard for memory accuracy. Catches confabulations that LLM-as-judge alone might miss.
- *Tier A*: High quality impact but harder to use. Advanced feature for thorough evaluation.

---

#### F20: Generative Scenario Generation — Tier B, Score 6.0

**What it is:** Use an LLM to automatically generate memory test scenarios based on a domain description. "Generate memory tests for a healthcare assistant" → produces scenarios about medications, allergies, appointments, etc.

**What it provides:** Automated scenario creation. Reduces the manual effort of writing test scenarios for domain-specific agents.

**What it requires:** `IChatClient` for scenario generation. Domain description text. LLM cost for generation (one-time per domain).

**Scoring rationale:**
- *Relevance (5)*: Useful but not essential. The built-in scenario library (F07) covers most common patterns.
- *DevEx (7)*: Easy to use — provide a description, get scenarios. But generated scenarios need human review.
- *Quality Impact (6)*: Generated scenarios may not cover edge cases that hand-crafted ones do. Useful for breadth, not depth.
- *Tier B*: Nice automation for large-scale testing. Not critical for MVP.

---

#### F21: Embedding Model Impact Analysis — Tier B, Score 6.4

**What it is:** Run the same memory evaluation scenarios against the same agent but with different embedding models (text-embedding-3-large vs text-embedding-3-small vs ada-002). Compare recall quality per model.

**What it provides:** Data-driven embedding model selection. "text-embedding-3-large retrieves 95% of buried facts; text-embedding-3-small only 70%." Helps justify the cost of larger embedding models.

**What it requires:** Multiple embedding model configurations. Same agent/scenario, only the embedding varies. Higher LLM/API cost (multiple embedding model invocations).

**Scoring rationale:**
- *Relevance (6)*: Important for teams choosing embedding models but a niche use case.
- *DevEx (6)*: Requires configuring multiple embedding backends. Not trivial.
- *Quality Impact (7)*: Reveals a hidden variable (embedding quality) that significantly affects memory recall.
- *Tier B*: Valuable for teams optimizing memory infrastructure. Not needed for basic evaluation.

---

#### F22: Memory State Visualization — Tier C, Score 4.8

**What it is:** Visual representation of the agent's memory state: what's in the chat history, what's in the vector store, what the agent "knows" at each point in the conversation.

**What it provides:** Debug-oriented visualization. Memory timelines, fact graphs, retrieval path diagrams.

**What it requires:** Provider introspection (F11). UI/rendering capabilities (CLI table output or file-based visualization).

**Scoring rationale:**
- *Relevance (4)*: Debugging aid, not an evaluation feature. Useful but peripheral.
- *DevEx (8)*: Visualizations are intuitive and helpful.
- *Quality Impact (3)*: Shows what's there but doesn't assess quality. Diagnostic, not evaluative.
- *Tier C*: Low priority. The CLI/dashboard is in a separate repository scope.

---

#### F23: Emotional Memory Evaluation — Tier B-, Score 5.4

**What it is:** Test whether agents correctly recall and respond to emotionally charged information. "My mother passed away last month" — does the agent remember and respond with appropriate empathy later?

**What it provides:** Scenarios with emotional context. Tests both recall (did the agent remember?) and appropriateness (did it respond with empathy, not cheerfully?).

**What it requires:** LLM judging with emotional tone evaluation. More nuanced judging prompts than factual recall.

**Scoring rationale:**
- *Relevance (5)*: Important for customer-facing agents but niche compared to factual recall.
- *DevEx (5)*: Emotional evaluation is inherently subjective. Harder to score definitively.
- *Quality Impact (6)*: An insensitive response to grief is a UX catastrophe, but the failure frequency is lower than factual errors.
- *Tier B-*: Niche feature for specific use cases (healthcare, customer support with emotional intelligence).

---

#### F24: Memory as Data Quality Testing — Tier C, Score 5.4

**What it is:** Treat the agent's memory as a data store and evaluate its data quality properties: completeness (all facts stored?), accuracy (stored facts match input?), freshness (stale data detected?), consistency (no contradictions?).

**What it provides:** Data quality metrics applied to agent memory. Percentage-based scores for each data quality dimension.

**What it requires:** Ground-truth fact set (similar to F19 Oracle). Analysis of stored memory state.

**Scoring rationale:**
- *Relevance (5)*: Data quality is a valid lens but overlaps heavily with F01 (retention) and F19 (oracle).
- *DevEx (5)*: Requires data quality framework understanding. Not intuitive for most agent developers.
- *Quality Impact (6)*: Valid approach but doesn't find bugs that F01/F19 wouldn't already catch.
- *Tier C*: Too much overlap with existing features. Not a priority.

---

#### F25: Memory Time Machine — Tier C, Score 5.5

**What it is:** Replay a conversation and query the agent's memory at any historical point. "What did the agent know after turn 15?" — without re-running turns 16-50.

**What it provides:** Historical memory state inspection. Debug and analyze how memory evolved over a conversation.

**What it requires:** Full conversation trace recording (like AgentEval's existing Trace Record/Replay). Session state snapshots at each turn.

**Scoring rationale:**
- *Relevance (5)*: Powerful debugging concept but heavy implementation.
- *DevEx (4)*: Complex UX — requires understanding trace replay and session state snapshots.
- *Quality Impact (7)*: When it works, it reveals exactly when and why memory degraded. High value for root cause analysis.
- *Tier C*: Implementation cost outweighs benefit. Use F27 (Reach-Back) for depth testing instead.

---

#### F26: Chatty Conversation Scenarios — Tier S, Score 9.1

**What it is:** Test scenarios where important facts are buried in realistic noisy conversations. 80%+ of messages are noise (pleasantries, small talk, topic changes), and the agent must still recall the 20% that contains real information. Directly inspired by Wes's feedback about production conversations.

**What it provides:** `ChattyConversationScenarios` with configurable noise-to-signal ratios. Built-in noise generators: casual chit-chat, emotional responses ("That's so interesting!"), topic digressions, filler messages. Facts are randomly buried within the noise.

**What it requires:** F01 (Core Engine) + F07 (Scenario Library). No additional external dependencies.

**Scoring rationale:**
- *Relevance (9)*: Real conversations are chatty. Clean test scenarios are unrealistic. This bridges the gap.
- *DevEx (8)*: Pre-built chatty scenarios. `MemoryScenarios.ChattyConversation(factCount: 5, noiseRatio: 10)`.
- *Quality Impact (10)*: The #1 reason agents fail in production: facts buried in noise. This catches exactly that failure mode.
- *Tier S*: Elevated by Wes's expert feedback. Tests the hardest real-world memory problem.

---

#### F27: Reach-Back Depth Testing — Tier S, Score 8.8

**What it is:** Parametric test that measures how far back in a conversation an agent can reliably recall facts through layers of noise. Plant a fact at turn T, fill N noise turns, query at turn T+N. Measure the maximum N where recall still succeeds.

**What it provides:** `IReachBackEvaluator` + `ReachBackEvaluator` that produces a `ReachBackResult` with: maximum reliable depth, degradation curve (quality vs distance), failure point. A single number ("this agent can reach back 25 turns") that's immediately actionable.

**What it requires:** `IEvaluableAgent`, `ISessionResettableAgent` (for fresh sessions per depth test), `IChatClient` (for LLM judging). Multiple test runs (one per depth point). Higher cost for deeper tests.

**Scoring rationale:**
- *Relevance (9)*: Directly measures the agent's "memory depth" — the most important characteristic of memory quality.
- *DevEx (7)*: Requires configuring depth points and noise generators. Not a one-liner but well-structured.
- *Quality Impact (10)*: The depth number is the single most actionable metric. "Your agent's memory fails at 25 turns." Teams immediately know their limit.
- *Tier S*: Novel capability. No existing framework measures reach-back depth.

---

#### F28: Token-Aware Context Evaluation — Tier A, Score 7.8

**What it is:** Evaluate memory context in tokens, not messages. A chatty "That's great!" is 3 tokens; "My SSN is 123-45-6789 and I'm allergic to shellfish" is 20 tokens. Message-counting reducers treat them the same — token-aware evaluation reveals the real information density.

**What it provides:** Token-level analysis of memory context: signal-to-noise ratio in tokens, token budget recommendations per fact density, comparison of token-counting vs message-counting reducers.

**What it requires:** Token estimation (tiktoken or model-specific tokenizer). `IChatClient` or tokenizer API. Extends F10 (Reducer Evaluation).

**Scoring rationale:**
- *Relevance (8)*: Token budget is the real constraint, not message count. This measures what actually matters.
- *DevEx (6)*: Requires tokenizer integration. Adds complexity to reducer evaluation.
- *Quality Impact (9)*: Reveals why reducers lose important facts: they count messages, not tokens. The "aha moment" for many teams.
- *Tier A*: Important insight but builds on F10. Phase 2 enhancement.

---

#### F29: Conversation Topology Testing — Tier A, Score 7.5

**What it is:** Test memory across different conversation shapes: linear (A→B→C), branching (topic digressions), interwoven (multi-topic), correction-heavy (user corrects facts repeatedly), debate-style (back-and-forth argumentation). Each topology has different memory challenges.

**What it provides:** Scenario templates for each conversation shape. Identifies which topologies cause memory failures. "Your agent handles linear conversations fine but fails with topic digressions."

**What it requires:** F01 (Core Engine) + F07 (Scenario Library). Topology-specific scenario generators.

**Scoring rationale:**
- *Relevance (7)*: Real conversations are not linear. Testing different shapes is realistic.
- *DevEx (6)*: More complex scenario setup. Developers must understand topology concepts.
- *Quality Impact (9)*: Different topologies stress different memory aspects. Branching conversations reveal context-switching failures. Corrections reveal temporal reasoning bugs.
- *Tier A*: Advanced scenario design. Phase 2 feature extending F07.
