# AgentEval Memory Benchmark — Assessment & Improvement Roadmap

**Date:** March 8, 2026  
**Scope:** Memory Benchmark Suite, Scenario Library Synergy, Baseline Reports, Radar/Spider Charts, Framework Adapters  
**Status:** Living document — updated as features are implemented

---

## Table of Contents

1. [Executive Assessment: Where We Stand](#1-executive-assessment-where-we-stand)
2. [What Makes a Memory Benchmark Complete](#2-what-makes-a-memory-benchmark-complete)
3. [Current Benchmark vs. Complete Benchmark Gap Analysis](#3-current-benchmark-vs-complete-benchmark-gap-analysis)
4. [Benchmark + Scenario Library Synergy (F07)](#4-benchmark--scenario-library-synergy-f07)
5. [Framework Adapters — The Missing Piece](#5-framework-adapters--the-missing-piece)
6. [Baseline Reports & Memory Profiles](#6-baseline-reports--memory-profiles)
7. [Radar/Spider Chart Visualization](#7-radarspider-chart-visualization)
8. [Proposed Memory Dimensions (The Octagon)](#8-proposed-memory-dimensions-the-octagon)
9. [Implementation Roadmap](#9-implementation-roadmap)
10. [Design Decisions & Trade-offs](#10-design-decisions--trade-offs)

---

## 1. Executive Assessment: Where We Stand

### Current State: Strong Foundation, Missing the "Show Me" Layer

The memory benchmark suite has solid **evaluation mechanics** — 8 scenario types, 3 presets (Quick/Standard/Full), weighted scoring, grade/star ratings, per-category results with actionable recommendations. The scenario library (F07) is rich with 19+ built-in scenarios across 4 providers. The two systems are already integrated: `MemoryBenchmarkRunner` injects all scenario providers and routes categories to them.

**What's strong:**
- 8 benchmark categories covering retention, temporal, noise, depth, updates, multi-topic, cross-session, and reducer fidelity
- Scenario library with 19+ ready-made scenarios (basic, chatty, temporal, cross-session)
- Weighted scoring with automatic renormalization when categories are skipped
- Actionable recommendations per weak category
- Full DI integration — everything composable and testable

**What's missing:**
- **No persistence:** Results vanish after execution. No way to save a "baseline" and compare later.
- **No identity:** Results have no metadata — who ran this, which agent configuration, when, what model.
- **No comparison:** Can't overlay two benchmark runs to see which configuration is better.
- **No visualization data:** The data structure doesn't lend itself to multi-dimensional comparison (radar/spider charts).
- **Underutilized scenario library:** The benchmark uses a fixed subset of scenarios per category. The rich scenario library (F07) has many more scenarios unused by the benchmark runner.
- **Framework adapter gap:** `ChatClientAgentAdapter` and `MAFAgentAdapter` both have session reset capabilities but don't implement `ISessionResettableAgent`, so cross-session benchmarks silently skip for wrapped agents.

### The Vision: From "Evaluate Once" to "Track Over Time"

```
TODAY:                              FUTURE:
┌─────────────┐                    ┌─────────────────────────────────────┐
│ Run Benchmark│                   │ Run → Profile → Save → Compare     │
│ Get Score    │        →          │  │                                  │
│ Done.        │                   │  ├── Baseline v1.0 (gpt-4o)        │
└─────────────┘                    │  ├── Baseline v1.1 (gpt-4o-mini)   │
                                   │  ├── Baseline v2.0 (custom reducer) │
                                   │  │                                  │
                                   │  └── Radar Chart Overlay ◆━━━━◇    │
                                   └─────────────────────────────────────┘
```

---

## 2. What Makes a Memory Benchmark Complete

A production-grade memory benchmark must answer these questions:

### The 5 Questions of Memory Quality

| # | Question | Metric Dimension | Why It Matters |
|---|----------|-----------------|----------------|
| 1 | **Can it remember?** | Retention Accuracy | Baseline capability — can the agent store and recall facts at all? |
| 2 | **How deep can it remember?** | Recall Depth | Context window limits, attention degradation, practical conversation length |
| 3 | **Can it filter noise?** | Noise Resilience | Real conversations have chit-chat, digressions, emotional content |
| 4 | **Can it handle change?** | Update Fidelity | Facts change — users correct themselves, preferences evolve |
| 5 | **Does it survive resets?** | Persistence Durability | Session boundaries, agent restarts, long-term memory |

### The 3 Advanced Questions

| # | Question | Metric Dimension | Why It Matters |
|---|----------|-----------------|----------------|
| 6 | **Can it reason about time?** | Temporal Reasoning | "What did I say last Tuesday?" vs "What's my current address?" |
| 7 | **Can it handle compression?** | Compression Fidelity | Chat history compaction/reduction loses information — how much? |
| 8 | **Can it organize by topic?** | Topic Organization | Cross-domain memory — can it keep medical vs work vs personal separate? |

### Scoring Philosophy

Each dimension should produce a score on 0-100 that is:
- **Interpretable**: 90+ = excellent, 70-89 = good, 50-69 = needs work, <50 = failing
- **Actionable**: Each score comes with specific improvement guidance
- **Comparable**: Same scoring across different agents, models, and configurations
- **Reproducible**: Same agent + same benchmark = same score (within stochastic bounds)

---

## 3. Current Benchmark vs. Complete Benchmark Gap Analysis

### What We Have (8 Categories)

| Category | Dimension Coverage | Scenario Richness | Score |
|----------|-------------------|-------------------|-------|
| BasicRetention | Retention Accuracy | 5 facts, 4 queries | ⭐⭐⭐ |
| TemporalReasoning | Temporal Reasoning | 4 temporal facts, sequence test | ⭐⭐⭐ |
| NoiseResilience | Noise Resilience | 2 buried facts, chatty scenario | ⭐⭐ |
| ReachBackDepth | Recall Depth | 1 fact, depths [5,10,25] | ⭐⭐⭐ |
| FactUpdateHandling | Update Fidelity | 2 facts + 2 corrections | ⭐⭐ |
| MultiTopic | Topic Organization | 5 topics, 4 queries | ⭐⭐ |
| CrossSession | Persistence Durability | 3 facts, 1 reset | ⭐⭐⭐ |
| ReducerFidelity | Compression Fidelity | 3 facts, 20 noise messages | ⭐⭐⭐ |

### Analysis: What's Thin

1. **NoiseResilience** uses only `CreateBuriedFactsScenario()` — the scenario library has 3 more chatty scenarios (TopicSwitching, EmotionalDistractor, FalseInformation) that are never used by the benchmark.

2. **FactUpdateHandling** tests only basic corrections — doesn't test conflicting updates, partial updates, or cascading corrections.

3. **MultiTopic** reuses `CreateBasicMemoryTest()` — doesn't leverage specialized multi-domain fact sets. There's no testing of cross-contamination (does a pet fact bleed into medical answers?).

4. **CrossSession** tests only one reset cycle — doesn't test incremental learning across sessions or context switching between domains.

### Untapped Scenario Library Potential

| Scenario Provider | Total Scenarios | Used by Benchmark | Unused |
|-------------------|----------------|-------------------|--------|
| IMemoryScenarios | 7 (3 methods + 4 static helpers) | 3 | **4 unused** |
| IChattyConversationScenarios | 4 | 1 | **3 unused** |
| ICrossSessionScenarios | 4 | 0 (uses evaluator directly) | **4 unused** |
| ITemporalMemoryScenarios | 5 | 1 | **4 unused** |
| **TOTAL** | **20** | **5** | **15 unused** |

**Conclusion:** The benchmark is using roughly 25% of the available scenario library. That's the biggest quick win — wiring up what we already have.

---

## 4. Benchmark + Scenario Library Synergy (F07)

### The Opportunity: Scenario Depth Levels

The benchmark currently runs **one scenario per category**. With the rich scenario library, we can introduce **depth levels** — each category runs multiple scenarios for a more robust score.

```
Current:
┌──────────────────┐     ┌─────────────┐
│ BasicRetention    │────▶│ 1 scenario  │──▶ Score
└──────────────────┘     └─────────────┘

Proposed:
┌──────────────────┐     ┌─────────────┐
│ BasicRetention    │────▶│ 3 scenarios │──▶ Averaged Score (more robust)
│  (Quick: 1)       │     │  basic       │
│  (Standard: 2)    │     │  long-term   │
│  (Full: 3)        │     │  priority    │
└──────────────────┘     └─────────────┘
```

### Proposed Scenario-to-Category Mapping

| Category | Quick (1 scenario) | Standard (2 scenarios) | Full (3+ scenarios) |
|----------|-------------------|----------------------|---------------------|
| BasicRetention | `CreateBasicMemoryTest` | + `CreateLongTermMemoryTest` | + `CreatePriorityMemoryTest` |
| TemporalReasoning | `CreateSequenceMemoryTest` | + `CreateTimePointMemoryTest` | + `CreateCausalReasoningTest` + `CreateOverlappingTimeWindowTest` |
| NoiseResilience | `CreateBuriedFactsScenario` | + `CreateTopicSwitchingScenario` | + `CreateEmotionalDistractorScenario` + `CreateFalseInformationScenario` |
| FactUpdateHandling | `CreateMemoryUpdateTest` | + `RetentionWithDelay` static | + custom conflict test |
| MultiTopic | `CreateBasicMemoryTest` (multi) | + `CategorizedMemory` static | + cross-contamination test |
| CrossSession | Evaluator direct (3 facts) | + `CreateCrossSessionMemoryTest` | + `CreateIncrementalLearningTest` + `CreateContextSwitchingTest` |
| ReducerFidelity | Evaluator direct (3 facts) | + 5 facts, 40 noise | + priority-weighted facts |
| ReachBackDepth | [5,10,25] | + [5,10,25,50] | + [5,10,25,50,100] |

**Impact:** Full benchmark goes from 8 data points to 25+ data points per category, producing much more statistically reliable scores.

### Implementation Approach

No new interfaces needed. Each `RunXxxAsync` method in `MemoryBenchmarkRunner` would accept a `depth` parameter (derived from the benchmark preset) and run additional scenarios, averaging their scores:

```csharp
private async Task<(double Score, bool Skipped, string? SkipReason)> RunNoiseResilienceAsync(
    IEvaluableAgent agent, BenchmarkDepth depth, CancellationToken ct)
{
    var scores = new List<double>();
    
    // Always run buried facts (Quick+)
    scores.Add(await RunScenario(_chattyScenarios.CreateBuriedFactsScenario(facts)));
    
    if (depth >= BenchmarkDepth.Standard)
        scores.Add(await RunScenario(_chattyScenarios.CreateTopicSwitchingScenario(facts)));
    
    if (depth >= BenchmarkDepth.Full)
    {
        scores.Add(await RunScenario(_chattyScenarios.CreateEmotionalDistractorScenario(facts)));
        scores.Add(await RunScenario(_chattyScenarios.CreateFalseInformationScenario(facts)));
    }
    
    return (scores.Average(), false, null);
}
```

---

## 5. Framework Adapters — The Missing Piece

### The Gap

Both existing adapters **already have** session reset capability but **don't implement** `ISessionResettableAgent`:

| Adapter | Has Reset Method | Implements Interface | Status |
|---------|-----------------|---------------------|--------|
| `ChatClientAgentAdapter` (MEAI) | `ClearHistory()` | ❌ No | One-liner to add |
| `MAFAgentAdapter` (MAF) | `ResetSessionAsync()` | ❌ No | One-liner to add |
| Custom agents (user-provided) | Varies | User decision | Already supported via interface |

### Impact of the Gap

Without this, **every** agent wrapped through the standard adapters silently skips cross-session benchmarks. The benchmark says "skipped: Agent does not implement ISessionResettableAgent" even though the underlying adapter CAN reset. This is the most impactful fix for the least effort.

### Proposed Fix

**Option A: Add `ISessionResettableAgent` to existing adapters** (RECOMMENDED)

```csharp
// In ChatClientAgentAdapter — already has ClearHistory()
public class ChatClientAgentAdapter : IEvaluableAgent, IStreamableAgent, ISessionResettableAgent
{
    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        ClearHistory();
        return Task.CompletedTask;
    }
}

// In MAFAgentAdapter — already has ResetSessionAsync()
public class MAFAgentAdapter : IEvaluableAgent, IStreamableAgent, ISessionResettableAgent
{
    // Already has the method, just add the interface declaration
    // Existing: public async Task ResetSessionAsync(CancellationToken ct) 
    //           → calls _agent.CreateSessionAsync()
}
```

**Effort:** Minimal — two one-liner changes. The methods already exist.

**Risk:** Low — adding an interface to existing classes is backwards-compatible. The interface is in `AgentEval.Memory`, so these adapters would gain a dependency on the Memory project. 

**Alternative (if dependency concern):** Move `ISessionResettableAgent` to `AgentEval.Abstractions` (it's a general-purpose interface). This is the cleaner architectural choice.

**Option B: Move `ISessionResettableAgent` to Abstractions** (CLEANEST)

Since session reset is a general agent capability (not memory-specific), the interface belongs in Abstractions:

```
AgentEval.Abstractions/  ← ISessionResettableAgent (moved here)
  └── IEvaluableAgent, IStreamableAgent, ISessionResettableAgent

AgentEval.Core/
  └── ChatClientAgentAdapter : ISessionResettableAgent ✅

AgentEval.MAF/
  └── MAFAgentAdapter : ISessionResettableAgent ✅

AgentEval.Memory/
  └── Uses ISessionResettableAgent from Abstractions
```

**Verdict:** Option B is the right architectural choice. Session reset is a general agent capability, not memory-specific. The Memory project can reference the interface from Abstractions without circular dependencies.

### Does It Make Sense?

**Absolutely yes.** This is the single highest-impact, lowest-effort improvement:
- **Effort:** Move 1 interface file, add interface declaration to 2 classes
- **Impact:** Unlocks CrossSession benchmarks for ALL wrapped agents (the most common usage pattern)
- **Risk:** Zero breaking changes (additive interface)

---

## 6. Baseline Reports & Memory Profiles

### The Concept: Named Benchmark Snapshots

A **Memory Baseline** is a named, timestamped snapshot of a benchmark run with full metadata. It answers: "Where does this agent's memory stand right now?"

```
┌────────────────────────────────────────────────────────────────┐
│                    MEMORY BASELINE REPORT                       │
├────────────────────────────────────────────────────────────────┤
│  Name:         "Production Agent v2.1"                         │
│  Description:  "GPT-4o with SlidingWindow(50) reducer"         │
│  Agent:        WeatherAssistant                                │
│  Model:        gpt-4o (2025-01-01)                             │
│  Timestamp:    2026-03-08T14:30:00Z                            │
│  Benchmark:    Full (8 categories)                             │
│  Duration:     12.4s                                           │
│                                                                │
│  OVERALL: 82.3% | Grade: B | ★★★★☆                            │
│                                                                │
│  Category Scores:                                              │
│  ═══════════════════════════════════════                        │
│  Basic Retention      ████████████████████ 95%  ★★★★★         │
│  Temporal Reasoning   ████████████████░░░░ 82%  ★★★★☆         │
│  Noise Resilience     ██████████████░░░░░░ 71%  ★★★☆☆         │
│  Reach-Back Depth     █████████████████░░░ 88%  ★★★★☆         │
│  Fact Update          ██████████████░░░░░░ 73%  ★★★☆☆         │
│  Multi-Topic          ████████████████░░░░ 80%  ★★★★☆         │
│  Cross-Session        █████████████████░░░ 85%  ★★★★☆         │
│  Reducer Fidelity     █████████████░░░░░░░ 68%  ★★★☆☆         │
│                                                                │
│  Weak areas: Reducer Fidelity, Noise Resilience                │
│  Recommendations: [3 actionable items]                         │
│                                                                │
│  Tags: ["production", "v2.1", "gpt-4o"]                       │
└────────────────────────────────────────────────────────────────┘
```

### Data Model

```csharp
/// <summary>
/// A named, timestamped snapshot of a memory benchmark run with full metadata.
/// Enables tracking memory quality over time and comparing configurations.
/// </summary>
public class MemoryBaseline
{
    /// <summary>Unique identifier for this baseline.</summary>
    public required string Id { get; init; }
    
    /// <summary>Human-readable name (e.g., "Production v2.1").</summary>
    public required string Name { get; init; }
    
    /// <summary>Description of the configuration being tested.</summary>
    public string? Description { get; init; }
    
    /// <summary>When the baseline was captured.</summary>
    public required DateTimeOffset Timestamp { get; init; }
    
    /// <summary>Agent metadata.</summary>
    public required BaselineAgentInfo Agent { get; init; }
    
    /// <summary>The benchmark result data.</summary>
    public required MemoryBenchmarkResult Result { get; init; }
    
    /// <summary>Per-dimension scores for radar chart visualization.</summary>
    public required IReadOnlyDictionary<string, double> DimensionScores { get; init; }
    
    /// <summary>Optional tags for filtering and grouping.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
    
    /// <summary>Optional key-value metadata (model version, reducer config, etc.).</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } 
        = new Dictionary<string, string>();
}

public class BaselineAgentInfo
{
    public required string AgentName { get; init; }
    public string? ModelId { get; init; }
    public string? ModelVersion { get; init; }
    public string? ReducerStrategy { get; init; }
    public string? MemoryProvider { get; init; }
}
```

### Baseline Operations

```csharp
public interface IBaselineStore
{
    /// <summary>Save a baseline snapshot.</summary>
    Task SaveAsync(MemoryBaseline baseline, CancellationToken ct = default);
    
    /// <summary>Load a baseline by ID.</summary>
    Task<MemoryBaseline?> LoadAsync(string id, CancellationToken ct = default);
    
    /// <summary>List all baselines, optionally filtered by agent name or tags.</summary>
    Task<IReadOnlyList<MemoryBaseline>> ListAsync(
        string? agentName = null, 
        IEnumerable<string>? tags = null,
        CancellationToken ct = default);
    
    /// <summary>Delete a baseline.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}

public interface IBaselineComparer
{
    /// <summary>Compare two or more baselines and produce a comparison report.</summary>
    BaselineComparison Compare(IReadOnlyList<MemoryBaseline> baselines);
}
```

### Comparison Report Model

```csharp
public class BaselineComparison
{
    /// <summary>The baselines being compared.</summary>
    public required IReadOnlyList<MemoryBaseline> Baselines { get; init; }
    
    /// <summary>Per-dimension comparison across all baselines.</summary>
    public required IReadOnlyList<DimensionComparison> Dimensions { get; init; }
    
    /// <summary>Which baseline is the overall best performer.</summary>
    public required string BestBaselineId { get; init; }
    
    /// <summary>Radar chart data for visualization.</summary>
    public required RadarChartData RadarChart { get; init; }
}

public class DimensionComparison
{
    public required string DimensionName { get; init; }
    
    /// <summary>Score per baseline ID.</summary>
    public required IReadOnlyDictionary<string, double> Scores { get; init; }
    
    /// <summary>Best score across all baselines.</summary>
    public double BestScore => Scores.Values.Max();
    
    /// <summary>ID of the baseline with the best score for this dimension.</summary>
    public string BestBaselineId => Scores.MaxBy(kvp => kvp.Value).Key;
}
```

---

## 7. Radar/Spider Chart Visualization

### Why Radar Charts?

A single number (82%) hides the shape of memory quality. An agent might score 95% on retention but 40% on temporal reasoning — the average hides a critical weakness. Radar charts expose the **shape** of memory capabilities.

```
              Retention (95%)
                   ╱╲
                  ╱  ╲
    Compression  ╱    ╲  Temporal (82%)
    (68%)       ╱      ╲
               ╱   ┌──┐ ╲
              ╱    │82│  ╲
    Cross-   ╱─────┤  ├───╲ Noise (71%)
    Session ╱      └──┘    ╲
    (85%)  ╱                ╲
           ╲      ╱  ╲      ╱
            ╲    ╱    ╲    ╱
    Multi-   ╲  ╱      ╲  ╱ Depth (88%)
    Topic     ╲╱        ╲╱
    (80%)    Update (73%)
```

### Multi-Agent Overlay

The real power is overlaying multiple configurations:

```
              Retention
                 │
           ◆━━━━━━━━━◇
          ╱ ╲   │   ╱ ╲
    Comp ╱   ╲  │  ╱   ╲ Temporal
        ╱  ◆━╲━━━━╱━◇   ╲
       ╱  ╱   ╲ │╱   ╲   ╲
      ╱──╱─────╲╳╱─────╲──╱
      ╲  ╲     ╱│╲     ╱  ╱
       ╲  ◆━━━╱━│━╲━━━◇  ╱
        ╲   ╱   │  ╲   ╱
    Cross╲╱     │   ╲╱ Noise
         ╲     │    ╱
          ╲    │   ╱
           ◆━━━━━━◇
            Update │ Depth
                 │
              Topic
    
    ◆━━━◆ Agent A: gpt-4o + SlidingWindow(50)
    ◇━━━◇ Agent B: gpt-4o-mini + Summarization
```

### Data Model for Radar Charts

```csharp
/// <summary>
/// Data structure for rendering radar/spider charts.
/// Each series is a baseline, each axis is a memory dimension.
/// </summary>
public class RadarChartData
{
    /// <summary>The dimension names (axes of the chart).</summary>
    public required IReadOnlyList<string> Axes { get; init; }
    
    /// <summary>Maximum value for each axis (typically 100).</summary>
    public double MaxValue { get; init; } = 100;
    
    /// <summary>One series per baseline being compared.</summary>
    public required IReadOnlyList<RadarChartSeries> Series { get; init; }
}

public class RadarChartSeries
{
    /// <summary>Display name for this series (e.g., "gpt-4o + SlidingWindow").</summary>
    public required string Name { get; init; }
    
    /// <summary>Score per axis, in the same order as RadarChartData.Axes.</summary>
    public required IReadOnlyList<double> Values { get; init; }
    
    /// <summary>Optional color hint for rendering.</summary>
    public string? Color { get; init; }
}
```

### Export Formats

The radar chart data should be exportable as:

1. **JSON** — For web-based rendering (Chart.js, D3.js, Plotly)
2. **Markdown table** — For GitHub/docs (degraded but useful)
3. **ASCII art** — For console output (like the samples do today)
4. **SVG** — For embedded reports (future)
5. **CSV** — For spreadsheet analysis

Console ASCII rendering (achievable now):
```
┌────────────────────────────────────────────────────────────┐
│  MEMORY RADAR — Agent Comparison                           │
├──────────────────┬──────────┬──────────┬──────────────────┤
│ Dimension        │ Agent A  │ Agent B  │ Delta            │
├──────────────────┼──────────┼──────────┼──────────────────┤
│ Retention        │ ████ 95% │ ███░ 78% │ A wins (+17%)    │
│ Temporal         │ ███░ 82% │ ████ 91% │ B wins (+9%)     │
│ Noise Resilience │ ██░░ 71% │ ███░ 75% │ B wins (+4%)     │
│ Recall Depth     │ ████ 88% │ ██░░ 65% │ A wins (+23%)    │
│ Update Fidelity  │ ██░░ 73% │ ███░ 80% │ B wins (+7%)     │
│ Topic Mgmt       │ ███░ 80% │ ███░ 82% │ ≈ tie            │
│ Persistence      │ ████ 85% │ █░░░ 45% │ A wins (+40%)    │
│ Compression      │ ██░░ 68% │ ████ 92% │ B wins (+24%)    │
├──────────────────┼──────────┼──────────┼──────────────────┤
│ OVERALL          │ 80.3%    │ 76.0%    │ A wins (+4.3%)   │
│ STRONGEST        │ Retention│ Compress │                   │
│ WEAKEST          │ Compress │ Persist  │                   │
└──────────────────┴──────────┴──────────┴──────────────────┘
```

---

## 8. Proposed Memory Dimensions (The Octagon)

### The 8 Canonical Memory Dimensions

After analyzing the benchmark categories and real-world memory failure modes, we propose 8 canonical dimensions — forming an "octagon" for radar visualization:

```
              ① Retention
                  ╱╲
                 ╱  ╲
  ⑧ Compression ╱    ╲ ② Temporal
               ╱      ╲
              ╱  agent  ╲
  ⑦ Persist. ╱  memory   ╲ ③ Noise
             ╲  quality   ╱   Resilience
              ╲          ╱
  ⑥ Topic    ╲        ╱ ④ Depth
    Mgmt      ╲      ╱
               ╲    ╱
                ╲  ╱
          ⑤ Update Fidelity
```

| # | Dimension | Measures | Benchmark Category | Score Range |
|---|-----------|---------|-------------------|-------------|
| ① | **Retention Accuracy** | Can it store and recall basic facts? | BasicRetention | 0-100 |
| ② | **Temporal Reasoning** | Can it reason about when things happened? | TemporalReasoning | 0-100 |
| ③ | **Noise Resilience** | Can it extract signal from chatty conversations? | NoiseResilience | 0-100 |
| ④ | **Recall Depth** | How many turns back can it reliably recall? | ReachBackDepth | 0-100 |
| ⑤ | **Update Fidelity** | Can it handle fact corrections and changes? | FactUpdateHandling | 0-100 |
| ⑥ | **Topic Management** | Can it organize and retrieve cross-domain facts? | MultiTopic | 0-100 |
| ⑦ | **Persistence** | Does memory survive session boundaries? | CrossSession | 0-100 |
| ⑧ | **Compression Fidelity** | How much survives context reduction? | ReducerFidelity | 0-100 |

### Dimension-to-Category Mapping

Each dimension maps 1:1 to a benchmark category. This makes the radar chart a direct visualization of the `MemoryBenchmarkResult.CategoryResults` — no additional computation needed. The `DimensionScores` dictionary in `MemoryBaseline` is simply:

```csharp
var dimensionScores = result.CategoryResults
    .Where(c => !c.Skipped)
    .ToDictionary(c => MapToDimension(c.ScenarioType), c => c.Score);
```

### Future: Additional Dimensions

If needed, the model supports adding dimensions without breaking existing baselines:

| Potential Future Dimension | What It Measures |
|---------------------------|-----------------|
| **Priority Awareness** | Does importance affect recall? (high-importance facts retained more?) |
| **Contradiction Detection** | Can it identify conflicting facts? |
| **Inference Quality** | Can it derive unsaid-but-implied facts? |
| **Concurrency Handling** | Multiple users or parallel sessions? |

---

## 9. Implementation Roadmap

### Phase 1: Framework Adapters (Quick Win)
**Effort: Small | Impact: High**

1. Move `ISessionResettableAgent` from `AgentEval.Memory` to `AgentEval.Abstractions`
2. Add `ISessionResettableAgent` implementation to `ChatClientAgentAdapter`
3. Add `ISessionResettableAgent` declaration to `MAFAgentAdapter` (method already exists)
4. Update Memory project to reference from Abstractions
5. Add tests

**Result:** Cross-session benchmarks work for ALL wrapped agents out of the box.

### Phase 2: Scenario Depth Integration (Medium Win)
**Effort: Medium | Impact: Medium-High**

1. Add `BenchmarkDepth` enum or derive from preset name (Quick/Standard/Full)
2. Modify each `RunXxxAsync` method to accept depth and run multiple scenarios
3. Average scores across scenarios per category
4. No new interfaces — just wiring existing scenarios into the benchmark runner

**Result:** More robust, statistically reliable scores using the full scenario library.

### Phase 3: Baseline Model & Persistence (Core Feature)
**Effort: Medium | Impact: High**

1. Create `MemoryBaseline`, `BaselineAgentInfo` models
2. Create `IBaselineStore` interface + `JsonFileBaselineStore` implementation
3. Create `IBaselineComparer` interface + implementation
4. Create `RadarChartData` model
5. Add baseline creation helper: `MemoryBenchmarkResult.ToBaseline(name, description, agentInfo)`
6. Add DI registration

**Result:** Benchmark results can be saved, loaded, and compared.

### Phase 4: Comparison & Visualization (The Payoff)
**Effort: Medium | Impact: Very High**

1. Create `BaselineComparer` with delta calculation
2. Create `RadarChartConsoleRenderer` for ASCII output
3. Create `RadarChartJsonExporter` for web visualization
4. Create `BaselineComparisonMarkdownExporter` for GitHub/docs
5. Add comparison sample (Sample 33 or 34)

**Result:** The full "pentagon/hexagon/octagon" experience — compare agents visually.

### Phase 5: Enhanced Benchmark Presets (Polish)
**Effort: Small | Impact: Medium**

1. Add `MemoryBenchmark.Custom(...)` builder for user-defined benchmarks
2. Add `MemoryBenchmark.Diagnostic` preset (deep analysis of weak areas)
3. Consider stochastic benchmark runs (multiple runs per scenario for statistical reliability)

---

## 10. Design Decisions & Trade-offs

### D1: Interface Location for ISessionResettableAgent

| Option | Pros | Cons |
|--------|------|------|
| **Keep in AgentEval.Memory** | No breaking changes | Adapters in Core/MAF can't implement it without Memory dependency |
| **Move to AgentEval.Abstractions** ✅ | Clean architecture, adapters can implement naturally | Minor breaking change (namespace move) |

**Decision:** Move to Abstractions. Session reset is a general agent capability. The Memory project shouldn't own it.

### D2: Baseline Storage Format

| Option | Pros | Cons |
|--------|------|------|
| **JSON files** ✅ | Simple, portable, no dependencies | No querying, manual file management |
| **SQLite** | Queryable, single file | New dependency, heavier |
| **In-memory only** | Simplest | Data lost on exit |

**Decision:** Start with JSON files (matches existing trace serialization pattern). Add richer stores later via `IBaselineStore` interface.

### D3: Radar Chart Rendering

| Option | Pros | Cons |
|--------|------|------|
| **Data model only** ✅ | Consumers choose their renderer | No built-in visualization |
| **ASCII console** ✅ | Works everywhere, great for samples | Limited aesthetics |
| **SVG generation** | Beautiful output | Complex, new dependency |
| **HTML/JS template** | Interactive | Requires browser |

**Decision:** Ship the data model + ASCII console renderer + JSON export. Let users bring their own visualization library for fancy rendering. The JSON schema is designed to be directly consumable by Chart.js, Plotly, etc.

### D4: Scenario Depth vs. Separate Scenarios

| Option | Pros | Cons |
|--------|------|------|
| **Depth levels in existing presets** ✅ | No new presets, richer data | More complex runner |
| **New presets (Deep-Quick, Deep-Standard)** | Simpler runner | Preset explosion |

**Decision:** Add depth within existing presets. Quick runs 1 scenario per category, Standard runs 2, Full runs 3+. This is the most natural extension.

---

## Appendix A: Relationship to Existing AgentEval Export Infrastructure

The baseline and comparison reports should integrate with AgentEval's existing `IResultExporter` infrastructure:

```csharp
// Convert baseline comparison to EvaluationReport for standard export
var report = comparison.ToEvaluationReport();
await exporter.ExportAsync(report, stream);
```

This means baselines can be exported as JUnit XML (for CI), JSON (for APIs), Markdown (for docs), etc. using the existing export pipeline.

## Appendix B: Usage Example (End-to-End Vision)

```csharp
// 1. Run benchmark
var result = await benchmarkRunner.RunBenchmarkAsync(agent, MemoryBenchmark.Full);

// 2. Create baseline
var baseline = result.ToBaseline(
    name: "Production v2.1",
    description: "GPT-4o with SlidingWindow(50) and semantic chunking",
    agent: new BaselineAgentInfo
    {
        AgentName = "WeatherAssistant",
        ModelId = "gpt-4o",
        ModelVersion = "2025-01-01",
        ReducerStrategy = "SlidingWindow(50)",
        MemoryProvider = "InMemoryChatHistoryProvider"
    },
    tags: ["production", "v2.1"]);

// 3. Save baseline
await baselineStore.SaveAsync(baseline);

// 4. Later: Load and compare
var baselines = await baselineStore.ListAsync(agentName: "WeatherAssistant");
var comparison = baselineComparer.Compare(baselines);

// 5. Visualize
RadarChartConsoleRenderer.Render(comparison.RadarChart);
comparison.PrintComparisonTable();

// 6. Export
await jsonExporter.ExportAsync(comparison.ToEvaluationReport(), stream);
```

---

*This document is a living assessment. Update it as features are implemented and new insights emerge.*
