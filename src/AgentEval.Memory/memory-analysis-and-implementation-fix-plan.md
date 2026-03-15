# AgentEval.Memory — Analysis & Implementation Fix Plan

**Date:** 2026-03-14
**Status:** Pre-release — all changes are non-breaking since there are no external consumers.

---

## Implementation Tracking

| # | Issue | Severity | Status | Files Changed |
|---|-------|----------|--------|---------------|
| 5 | Static stop-word HashSet | Medium | ✅ done | `Engine/MemoryJudge.cs` |
| 2 | Forbidden fact double penalty | High | ✅ done | `Engine/MemoryTestRunner.cs` |
| 4 | Real token counts from ChatResponse.Usage | High | ✅ done | `Engine/MemoryJudge.cs`, `Engine/MemoryTestRunner.cs` |
| 1 | MemoryTestRunner session reset awareness | Critical | ✅ done | `Engine/MemoryTestRunner.cs` |
| 3 | Metrics bridge (ToEvaluationContext) | High | ✅ done | `Extensions/MemoryEvaluationContextExtensions.cs` |
| 6 | Tests for untested public APIs | Critical | ✅ done | `tests/AgentEval.Memory.Tests/` (4 new test files, 39 new tests) |

---

## Executive Summary

AgentEval.Memory is architecturally sound but has **six concrete issues** that range from silent data corruption to dead code. None are showstoppers, but fixing them before release prevents users from hitting confusing behavior and filing bugs we already know about.

This document covers each issue with: the problem, evidence from the code, the proposed fix, and the reasoning behind the recommendation.

---

## Issue 1: MemoryTestRunner Does Not Process Session Reset Markers

### Problem

`CanRememberAcrossSessionsAsync()` and all `CrossSessionScenarios` embed `[SESSION_RESET_POINT]` markers in their step lists. But `MemoryTestRunner.ExecuteSetupStepsAsync()` treats every step identically — it calls `agent.InvokeAsync(step.Content)` for each one, including markers. The literal string `"[SESSION_RESET_POINT]"` gets sent to the agent as a conversation message. No session reset ever happens.

This means:

- `CanRememberAcrossSessionsAsync()` advertises cross-session testing but runs a single continuous session.
- All 8 `CrossSessionScenarios` factory methods produce scenarios that cannot work through the standard runner.
- The only code that actually resets sessions is `CrossSessionEvaluator`, which bypasses `MemoryTestRunner` entirely and implements its own orchestration.

### Evidence

**MemoryTestRunner.ExecuteSetupStepsAsync()** (Engine/MemoryTestRunner.cs:119-150):
```csharp
foreach (var step in steps)
{
    var response = await agent.InvokeAsync(step.Content, cancellationToken);
    // No check for SESSION_RESET_POINT, no session reset logic
}
```

**CrossSessionScenarios** inserts markers like:
```csharp
steps.Add(MemoryStep.System("[SESSION_RESET_POINT]"));
```

**CrossSessionEvaluator** does the right thing (Evaluators/CrossSessionEvaluator.cs):
```csharp
await resettable.ResetSessionAsync(cancellationToken);  // Actually resets
```

### Proposed Fix

**Make `MemoryTestRunner` session-aware.** When it encounters a step whose content is `[SESSION_RESET_POINT]`, it should:

1. Check if the agent implements `ISessionResettableAgent`.
2. If yes: call `ResetSessionAsync()` and skip sending the marker as text.
3. If no: log a warning and skip the step (the scenario requires session reset but the agent doesn't support it).

```csharp
private async Task ExecuteSetupStepsAsync(
    IEvaluableAgent agent, IReadOnlyList<MemoryStep> steps, CancellationToken ct)
{
    foreach (var step in steps)
    {
        if (step.Content.Contains("[SESSION_RESET_POINT]"))
        {
            if (agent is ISessionResettableAgent resettable)
            {
                await resettable.ResetSessionAsync(ct);
                _logger.LogDebug("Session reset executed");
            }
            else
            {
                _logger.LogWarning("Scenario requires session reset but agent does not implement ISessionResettableAgent");
            }
            continue;
        }

        var response = await agent.InvokeAsync(step.Content, ct);
        // ... existing validation logic ...
    }
}
```

### Why This Approach

- **Option A (this one):** Runner becomes session-aware. All existing scenarios work automatically. `CanRememberAcrossSessionsAsync()` starts working without changes.
- **Option B:** Route cross-session scenarios to `CrossSessionEvaluator` from the extension method. This breaks the return type contract (`CrossSessionResult` vs `MemoryEvaluationResult`) and creates two parallel orchestration paths.
- **Option C:** New `CrossSessionTestRunner` subclass. Over-engineering for what's effectively a 10-line `if` check.

Option A is simplest and makes the entire scenario library composable with the standard runner.

### Impact

- Fixes `CanRememberAcrossSessionsAsync()` — currently silently broken.
- Makes all 8 `CrossSessionScenarios` methods usable with the standard runner.
- Does not affect `CrossSessionEvaluator` (it bypasses the runner anyway).

---

## Issue 2: Forbidden Fact Double Penalty

### Problem

Forbidden facts are penalized twice:

1. **At query level:** The MemoryJudge prompt instructs the LLM to "Subtract 10-20 points per forbidden fact found" — so each query's score already includes the penalty.
2. **At aggregate level:** `MemoryTestRunner.AggregateResults()` subtracts `allForbiddenFound.Count * 10` from the averaged score.

If a forbidden fact appears in 3 queries, the total penalty is:
- 3 × (10-20 points) at query level = 30-60 points across the average
- Plus 10 points at aggregate level
- **Total: 40-70 points** for a single forbidden fact

### Evidence

**MemoryJudge prompt** (Engine/MemoryJudge.cs:125-126):
```
- Subtract 10-20 points per forbidden fact found
```

**AggregateResults** (Engine/MemoryTestRunner.cs:207-209):
```csharp
var baseScore = queryResults.Average(r => r.Score);                    // Already penalized
var forbiddenPenalty = allForbiddenFound.Count * 10;                   // Penalty again
var overallScore = Math.Max(0, baseScore - forbiddenPenalty);          // Double hit
```

### Proposed Fix

**Remove the aggregate-level penalty.** The LLM judge already factors forbidden facts into the per-query score. The aggregate should simply average scores.

```csharp
private static MemoryEvaluationResult AggregateResults(...)
{
    var overallScore = queryResults.Count > 0 ? queryResults.Average(r => r.Score) : 0;
    // No additional forbidden fact penalty — already included in per-query scores by the judge
    // ...
}
```

### Why This Approach

- The LLM judge is the authoritative scorer. It sees the full response context and decides how severely a forbidden fact matters (10-20 point range). The aggregate layer has no additional information to justify a second penalty.
- The `allForbiddenFound` collection is still useful for reporting (assertions, result display) — just not for double-dipping on the score.
- Alternative: Remove the penalty from the judge prompt and apply it only at aggregate level. But this is worse because the LLM's score would no longer reflect the actual response quality it observed.

### Impact

- Scores become more accurate and less punitive.
- Forbidden fact assertions (`NotHaveRecalledForbiddenFacts()`) still work (they check the collection, not the score).
- Scenarios testing forbidden facts (FalseInformation, etc.) produce fairer scores.

---

## Issue 3: Memory Metrics Are Dead Code

### Problem

All 5 memory metrics (`MemoryRetentionMetric`, `MemoryTemporalMetric`, `MemoryNoiseResilienceMetric`, `MemoryReachBackMetric`, `MemoryReducerFidelityMetric`) expect an `EvaluationContext` containing a `MemoryEvaluationResult` at key `"MemoryEvaluationResult"`. But **no code anywhere in the pipeline populates this context property**.

Every metric starts with:
```csharp
var memoryResult = context.GetProperty<MemoryEvaluationResult>("MemoryEvaluationResult");
if (memoryResult == null)
{
    return MetricResult.Fail(Name, "MemoryEvaluationResult not found in evaluation context.");
}
```

Since nothing sets this property, **every metric always fails with "not found."**

The metrics are:
- Registered in DI via `AddAgentEvalMemoryMetrics()`.
- Have sophisticated internal logic (degradation analysis, noise pattern detection, compression ratios).
- Zero test coverage.
- Never called by any evaluator.

### Proposed Fix

**Option A (recommended): Add a bridge method that populates the context.**

Create an extension on `MemoryEvaluationResult` or `MemoryTestRunner` that creates a properly populated `EvaluationContext`:

```csharp
// In MemoryEvaluationResult or a new extension class
public static EvaluationContext ToEvaluationContext(this MemoryEvaluationResult result)
{
    var context = new EvaluationContext();
    context.SetProperty("MemoryEvaluationResult", result);
    return context;
}
```

Then document the intended usage:
```csharp
var result = await runner.RunAsync(agent, scenario);
var context = result.ToEvaluationContext();
var metricResult = await retentionMetric.EvaluateAsync(context);
```

**Option B: Wire metrics into MemoryBenchmarkRunner.**

Have the benchmark runner automatically evaluate metrics after each category and include metric results in `MemoryBenchmarkResult`. This is more integrated but couples the benchmark to the metric system.

**Option C: Remove the metrics entirely.**

If they're not going to be used, delete them. Dead code with zero tests is a maintenance liability.

### Why Option A

- Keeps metrics optional (users opt in) rather than forcing them into the benchmark pipeline.
- Aligns with the core AgentEval metric pattern where metrics are composable building blocks.
- Low effort: one extension method + documentation.
- Option B creates tight coupling. Option C throws away working code that has a clear purpose.

### Impact

- Metrics become actually usable.
- Enables users to build custom evaluation pipelines with memory-specific metrics.
- Requires adding tests for all 5 metrics (currently zero coverage).

---

## Issue 4: Token Estimation and Cost Tracking

### Problem

`MemoryJudge.EstimateTokenUsage()` uses `(charCount) / 4` to estimate tokens. This is inaccurate by 10-30% and makes all downstream token/cost data unreliable.

Meanwhile, the `ChatResponse` returned from `_chatClient.GetResponseAsync()` has a `.Usage` property (`UsageDetails`) with actual `InputTokenCount` and `OutputTokenCount` — the same data that `ChatClientAgentAdapter` already extracts for agent responses. But MemoryJudge ignores it.

Additionally, the cost multiplier in `MemoryTestRunner.AggregateResults()`:
```csharp
var estimatedCost = totalTokens * 0.00001m;  // $0.01 per 1K tokens
```
Is roughly **200-900x too low** compared to actual model pricing ($2-9 per 1K tokens for GPT-4/Claude).

The assertions `HaveUsedFewerTokens()` and `HaveCostLessThan()` are therefore useless — they pass or fail on wrong numbers.

### Proposed Fix

**Step 1: Extract actual token usage from ChatResponse in MemoryJudge.**

Change `JudgeAsync` to capture usage from the response:

```csharp
var chatResponse = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
var responseText = chatResponse.Text ?? "";

// Use actual token counts when available, fall back to estimation
var tokensUsed = ExtractTokenUsage(chatResponse) ?? EstimateTokenUsage(query, responseText);
```

```csharp
private static int? ExtractTokenUsage(ChatResponse response)
{
    if (response.Usage is { } usage)
    {
        var input = (int)(usage.InputTokenCount ?? 0);
        var output = (int)(usage.OutputTokenCount ?? 0);
        if (input > 0 || output > 0)
            return input + output;
    }
    return null;
}
```

**Step 2: Make the cost multiplier configurable or remove the hardcoded value.**

Either:
- Accept cost-per-token as a parameter on `MemoryTestRunner` or `MemoryTestScenario`.
- Or remove cost estimation entirely and let users compute costs from token counts using their own pricing.

The hardcoded `0.00001m` is wrong for every model and creates false confidence.

**Step 3: Update `MemoryJudgmentResult` to pass actual tokens through.**

The `TokensUsed` field already exists — just populate it with real data instead of the estimate.

### Why

- The data is already available in `ChatResponse.Usage` — we're just not using it.
- `ChatClientAgentAdapter` in AgentEval.Core already does this exact extraction (via `ConvertTokenUsage`), proving the pattern works.
- Cost assertions are a liability when they pass/fail on data that's off by orders of magnitude. Better to be accurate or not offer the feature.

### Impact

- Token counts become accurate (when the underlying provider returns usage data).
- Cost assertions become meaningful.
- No behavioral change for providers that don't return usage data (falls back to estimation).

---

## Issue 5: HasSignificantOverlap Performance

### Problem

`MemoryJudge.HasSignificantOverlap()` allocates a new `HashSet<string>` containing 39 stop words on every call. In a full benchmark run, this method is called 360-3,000 times (3 `MatchFactsByContent` calls per judgment × multiple facts × 30-40 judgments).

### Proposed Fix

Make the stop-word set a `static readonly` field:

```csharp
private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
{
    "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
    "have", "has", "had", "do", "does", "did", "will", "would", "could",
    "should", "may", "might", "shall", "can", "to", "of", "in", "for",
    "on", "with", "at", "by", "from", "or", "and", "not", "no", "but",
    "if", "that", "this", "it", "its", "my", "your", "user", "users"
};

private static bool HasSignificantOverlap(string text1, string text2)
{
    var words1 = text1.Split([' ', ',', '.', '!', '?', ':', ';', '\'', '"'],
            StringSplitOptions.RemoveEmptyEntries)
        .Where(w => w.Length > 2 && !StopWords.Contains(w))
        // ...
}
```

### Why

One-line change. Eliminates thousands of allocations. No behavioral change. The stop-word list is constant data — there's no reason for it to be per-call.

### Impact

Reduced GC pressure during benchmark runs. Measurable improvement for long-running evaluations.

---

## Issue 6: Test Coverage Gaps in Public API

### Problem

Several public-facing APIs have **zero test coverage**:

| Component | Methods | Severity |
|-----------|---------|----------|
| `CanRememberExtensions` | 7 public methods | **Critical** — primary convenience API |
| `MemoryAssertions` + `MemoryEvaluationAssertions` + `MemoryQueryAssertions` | 12 assertion methods | **Critical** — test framework integration |
| 5 Metric classes | 5 `EvaluateAsync()` methods | **High** — currently dead code (Issue 3) |
| `TemporalMemoryRunner` | 3 methods | **Medium** — only DI-registered, never functionally tested |
| `MemoryScenarios` | `RetentionWithDelay`, `CategorizedFacts`, etc. | **Medium** — factory methods never invoked in tests |

This is not about chasing coverage percentages. These are APIs that users will call directly. If they break, no test catches it.

### Proposed Fix

Add tests in this priority order:

**Priority 1 — CanRememberExtensions (critical, most-used API):**
- `CanRememberAsync(fact)` — single fact
- `CanRememberAsync(facts)` — multiple facts
- `CanRememberAsync(factsAndQueries)` — custom queries
- `CanRememberThroughNoiseAsync()` — noise resilience
- `CanRememberAcrossSessionsAsync()` — cross-session (after Issue 1 fix)
- `QuickMemoryCheckAsync()` — string matching (no LLM)
- Error cases: null agent, null facts, missing IChatClient

**Priority 2 — MemoryAssertions (critical, test integration):**
- Each of 8 `MemoryEvaluationAssertions` methods (pass + fail case)
- Each of 4 `MemoryQueryAssertions` methods (pass + fail case)
- Verify failure messages contain suggestions
- Chain multiple assertions

**Priority 3 — Metrics (high, enables Issue 3 fix):**
- Each metric with a populated `EvaluationContext`
- Each metric with missing context (should fail gracefully)
- Threshold boundary testing
- Non-applicable scenarios (e.g., temporal metric on non-temporal result)

**Priority 4 — TemporalMemoryRunner (medium):**
- `RunTemporalScenarioAsync` with temporal and non-temporal scenarios
- `TestTimeTravelQueriesAsync` with conversation history
- `TestCausalReasoningAsync` with causal chain
- Temporal metadata enrichment verification

### Why This Priority Order

Users encounter APIs in this order: extensions (one-liners) → assertions (test suites) → metrics (custom pipelines) → temporal runner (specialized). Testing should follow the same priority.

### Impact

Prevents regressions in the APIs users will use most frequently. Enables safe refactoring of Issues 1-4.

---

## Implementation Order

The fixes have dependencies:

```
Issue 5 (StopWords static) ─── no dependencies, quick win
      │
Issue 2 (Double penalty)  ─── no dependencies, score accuracy
      │
Issue 4 (Token estimation) ── no dependencies, data accuracy
      │
Issue 1 (Runner session reset) ── enables CanRemember fix
      │
Issue 3 (Metrics bridge) ──── needs Issue 4 for accurate token data
      │
Issue 6 (Tests) ──────────── needs Issues 1-5 fixed first
```

**Recommended order:**
1. Issue 5 — Static stop words (5 min, zero risk)
2. Issue 2 — Remove double penalty (10 min, score behavior change)
3. Issue 4 — Real token counts (30 min, data accuracy)
4. Issue 1 — Runner session awareness (30 min, functional fix)
5. Issue 3 — Metrics bridge method (20 min, enables dead code)
6. Issue 6 — Tests (2-4 hours, validates everything)

**Total estimated effort: ~4-6 hours.**

---

## What NOT to Fix

Some things I considered and decided against:

- **Question generation heuristics in CanRememberExtensions** — Yes, pattern-matching keywords is fragile. But replacing it with LLM-based question generation would add latency and cost to a convenience API. The fallback question ("What do you remember?") is adequate. Document the limitation, don't over-engineer it.

- **MemoryBenchmarkRunner switch statement** — Yes, an 8-way switch on `BenchmarkScenarioType` is a code smell. But replacing it with a strategy pattern for 8 cases that will rarely change is textbook over-engineering. The switch is readable and maintainable.

- **Cost assertion removal** — After Issue 4, cost estimates become reasonable (when providers return usage data). Renaming to `HaveEstimatedCostLessThan()` was considered but would be a breaking rename for no functional benefit. Keep the name, fix the data.

- **CanRememberAcrossSessionsAsync return type change** — Returning `CrossSessionResult` instead of `MemoryEvaluationResult` would be more honest, but changes the extension method's signature and breaks the one-liner pattern. Issue 1's fix makes it work correctly with the current return type.
