# Memory Benchmark & Presentation Layer — Complete Implementation Plan

**Date:** March 15, 2026
**Synthesised from:** `Memory-Benchmark-Assessment-and-Roadmap.md` + `agent-tki-benchmark-reporting.md`
**Status:** Active implementation guide — supersedes both source documents for planning purposes

---

## 1. Assessment of Both Source Documents

### 1.1 `Memory-Benchmark-Assessment-and-Roadmap.md` (the Roadmap)

**Strongest points:**

- **The Octagon** — 8 canonical memory dimensions that map 1:1 to existing benchmark categories. This is the best conceptual contribution in either document. It gives the radar chart a precise semantic foundation rather than generic axes.
- **Codebase-grounded gap analysis** — precise: only 5 of 20 available scenarios are used by the benchmark runner. 75% of built scenario richness is wasted.
- **ISessionResettableAgent architectural fix** — identifying that both adapters already have reset capability but don't implement the interface, with the clean recommendation to move the interface to Abstractions. Small effort, very high impact.
- **Scenario depth levels** — mapping Quick/Standard/Full to 1/2/3+ scenarios per category is elegant. No new interfaces, just wiring what exists.
- **Rich .NET data models** — `MemoryBaseline`, `IBaselineStore`, `IBaselineComparer`, `RadarChartData` are well-designed and respect .NET idioms.
- **Integration with existing AgentEval export infrastructure** — baselines plugging into the existing `IResultExporter` pipeline is exactly right.
- **5 Questions + 3 Advanced Questions** — a clear, teachable framework for what memory quality means.
- **ASCII console renderer** — delivers immediate value, works everywhere, consistent with the samples.

**Gaps in the Roadmap:**
- Visualization design is aspirational ("bring your own library") — no specific charting implementation
- Regression detection is not designed (only mentioned in Phase 5 as "consider stochastic")
- No timeline or progression tracking design
- Stochastic evaluation barely addressed
- HTML report output not specified
- CI integration not covered

---

### 1.2 `agent-tki-benchmark-reporting.md` (the TKI Guide)

**Strongest points:**

- **Complete pipeline architecture** — JSON → Parser → Metrics Store → Evaluation Engine → Report Generator → HTML. Decoupled, extensible, CI-friendly.
- **Standalone HTML output rationale** — single self-contained file that opens in any browser, commits to git, attaches to CI artifacts. The right call.
- **Plotly.js** as primary charting library — handles radar, timeline, box plots, heatmaps, violin plots natively with interactivity. The specific code examples are ready to use.
- **Difference spider** — a separate radar chart where each axis shows the *delta* between two runs, not absolute values. Immediately identifies what changed and in which direction. This is the most innovative visualization idea in either document.
- **Regression detection algorithms** — four specific algorithms: threshold, moving average, linear regression trend, and change-point (CUSUM/Bayesian). Each catches a different failure mode.
- **Stochastic + Memory 2×2 matrix** — classifying each task by (memory delta) × (stochastic variance) into four quadrants (Ideal / Risky / Investigate / Critical) is powerful and actionable.
- **Normalization strategy** — explicit rules for mapping all metric types to a 0–100 scale for radar display.
- **Statistical rigor** — confidence intervals, z-scores, inter-rater reliability (Cohen's kappa). Appropriate for publishable or production-grade benchmarks.
- **CI integration design** — CLI command, build artifact upload, fail-build-on-regression. Production-ready.
- **12-section report layout** — a concrete spec that a developer can implement directly.

**Gaps in the TKI Guide:**
- Not grounded in the AgentEval codebase — data models are JSON, not .NET types
- Memory dimensions are generic (uses "memory_recall_accuracy", "memory_integration_score") rather than the precise Octagon
- Archetype system (Analytical, Creative, etc.) is generic task classification, not memory-specific
- No mention of scenario depth, reducer evaluation, or session reset mechanics
- Doesn't integrate with AgentEval's existing IResultExporter infrastructure
- The Node.js report generator suggestion conflicts with the .NET-native approach AgentEval should take

---

### 1.3 What to Carry Forward From Each

| Concern | Source |
|---------|--------|
| The 8 canonical memory dimensions (The Octagon) | Roadmap |
| .NET data models (`MemoryBaseline`, `IBaselineStore`) | Roadmap |
| `ISessionResettableAgent` → Abstractions fix | Roadmap |
| Scenario depth levels (Quick/Standard/Full → 1/2/3+ scenarios) | Roadmap |
| Integration with `IResultExporter` | Roadmap |
| ASCII console renderer as MVP | Roadmap |
| 5+3 Questions framework | Roadmap |
| Pipeline architecture (JSON → HTML) | TKI Guide |
| Standalone HTML artifact design | TKI Guide |
| Plotly.js as charting library | TKI Guide |
| Difference spider (delta radar chart) | TKI Guide |
| Regression detection algorithms (4 types) | TKI Guide |
| Stochastic 2×2 matrix (variance × memory delta) | TKI Guide |
| Normalization strategy (all axes to 0–100) | TKI Guide |
| Confidence intervals + statistical analysis | TKI Guide |
| 12-section report layout (adapted for memory) | TKI Guide |
| CI integration (CLI, artifact, fail-on-regression) | TKI Guide |

---

## 2. The Vision

```
TODAY:                                    TARGET:
┌──────────────┐                          ┌────────────────────────────────────────────────┐
│ Run Benchmark │                          │  Run → Profile → Save → Compare → Report       │
│ Get Score     │          ──▶             │                                                │
│ Done.         │                          │  baseline-gpt4o-v2.1.json   ◆ The Octagon     │
└──────────────┘                          │  baseline-gpt4o-mini.json   ◇ overlay          │
                                          │  baseline-custom-reducer.json                  │
                                          │                                                │
                                          │  ┌─────────────────────────────────────────┐  │
                                          │  │  MEMORY RADAR — 3 agents compared       │  │
                                          │  │  Retention ████ ███ ██                  │  │
                                          │  │  Temporal  ███  ████ ███                │  │
                                          │  │  ...                                    │  │
                                          │  └─────────────────────────────────────────┘  │
                                          │                                                │
                                          │  timeline.png  regression-report.html          │
                                          │  results.xml   report.html  [CI artifact]      │
                                          └────────────────────────────────────────────────┘
```

The shift is from **point-in-time evaluation** to **longitudinal tracking**. A single score is a snapshot. A baseline with history is a story.

---

## 3. Summary: What We Are Building

Ten distinct feature areas, each independently deliverable:

1. **F1 — Framework Adapter Fix** (`ISessionResettableAgent` → Abstractions)
2. **F2 — Scenario Depth Integration** (wire all 20 built scenarios, not just 5)
3. **F3 — Baseline Model & Persistence** (save/load/list named benchmark snapshots)
4. **F4 — Comparison Engine** (delta calculation, winner/loser, direction)
5. **F5 — Console Presentation Layer** (ASCII radar table, comparison table, progress bars)
6. **F6 — HTML Report Generator** (standalone self-contained HTML with Plotly.js)
7. **F7 — Regression Detection** (4 algorithms: threshold / moving average / linear trend / change-point)
8. **F8 — Stochastic Memory Analysis** (repeated trials, confidence intervals, 2×2 matrix)
9. **F9 — Timeline & Progression Tracking** (sparklines, cumulative improvement, milestone markers)
10. **F10 — CI Integration** (CLI command, fail-on-regression, artifact export)

---

## 4. Tech Stack

| Layer | Technology | Rationale |
|-------|-----------|-----------|
| **Evaluation engine** | Existing AgentEval.Memory infrastructure | Already built; don't duplicate |
| **Baseline storage** | JSON files (one per run) | Human-readable, diffable in git, matches existing trace serialization pattern |
| **Report generator** | .NET Razor template or string interpolation → emits HTML | Stays in the .NET toolchain; no Node.js dependency |
| **Charting library** | **Plotly.js** (CDN, inlined in HTML) | Handles radar/spider, timelines, heatmaps, box plots natively; interactive hover out of the box |
| **Layout/styling** | Tailwind CSS (CDN) + single HTML file | Self-contained, no build step, polished appearance |
| **Statistical analysis** | Pre-computed in .NET, embedded as JSON in HTML | Deterministic; keeps statistics in the typed domain where we have type safety |
| **Export integration** | Existing `IResultExporter` infrastructure | Consistency with rest of AgentEval export pipeline |
| **CLI entry point** | `dotnet run --project AgentEval.Cli -- memory benchmark ...` | CI-friendly, same pattern as existing CLI |
| **LLM narrative** | Optional Anthropic API call for executive summary | Non-blocking; skipped if credentials absent |

### Why .NET, not Node.js

The TKI Guide suggests Node.js as the report generator because Plotly.js is JavaScript. This is the wrong call for AgentEval: the framework is .NET-native, the developers using it are .NET developers, and the CI environment already runs `dotnet`. The solution is to pre-compute all statistical analysis in .NET (where we have type safety and the existing evaluation infrastructure), serialize the results to JSON, and embed that JSON directly into an HTML template that contains Plotly.js from CDN. This is a standard pattern — the same JSON that feeds the HTML also feeds any other consumer.

### Why Standalone HTML

A single self-contained `.html` file (Plotly from CDN + embedded JSON data):
- Opens in any browser, no server
- Can be committed to git alongside baselines
- Attaches to CI artifacts in GitHub Actions, Azure DevOps, etc.
- Can be emailed or shared as-is
- Identical output given identical inputs (deterministic)

---

## 5. The Octagon: 8 Canonical Memory Dimensions

The Octagon is the conceptual heart of this plan. Eight dimensions cover the full space of memory quality failures:

```
              ① Retention
                  ╱╲
                 ╱  ╲
  ⑧ Compression ╱    ╲ ② Temporal
               ╱      ╲
              ╱  memory ╲
  ⑦ Persist. ╱  quality  ╲ ③ Noise
             ╲            ╱   Resilience
              ╲          ╱
  ⑥ Topic    ╲          ╱ ④ Depth
    Mgmt      ╲        ╱
               ╲      ╱
                ╲    ╱
          ⑤ Update Fidelity
```

| # | Dimension | The Question It Answers | Benchmark Category | Lower-is-Better? |
|---|-----------|------------------------|-------------------|-----------------|
| ① | **Retention Accuracy** | Can it store and recall basic facts? | BasicRetention | No |
| ② | **Temporal Reasoning** | Can it reason about when things happened? | TemporalReasoning | No |
| ③ | **Noise Resilience** | Can it extract signal from chatty conversations? | NoiseResilience | No |
| ④ | **Recall Depth** | How many turns back can it reliably recall? | ReachBackDepth | No |
| ⑤ | **Update Fidelity** | Can it handle fact corrections and changes? | FactUpdateHandling | No |
| ⑥ | **Topic Management** | Can it keep cross-domain facts separated and organized? | MultiTopic | No |
| ⑦ | **Persistence** | Does memory survive session boundaries? | CrossSession | No |
| ⑧ | **Compression Fidelity** | How much survives context reduction/summarization? | ReducerFidelity | No |

All 8 dimensions are normalized to **0–100** for radar display. "Better" always means farther from center. The mapping from `CategoryResults` to dimension scores is a pure projection — no new computation:

```csharp
var dimensionScores = result.CategoryResults
    .Where(c => !c.Skipped)
    .ToDictionary(
        c => MapToDimension(c.ScenarioType),  // enum → string label
        c => c.Score * 100.0);               // 0.0–1.0 → 0–100
```

### Future Dimensions (non-breaking additions)

The data model supports adding dimensions without invalidating existing baselines (missing dimensions are rendered as zero on older baselines):

| Potential Dimension | Measures |
|--------------------|---------|
| Priority Awareness | Does importance affect recall? |
| Contradiction Detection | Can it identify conflicting facts? |
| Inference Quality | Can it derive implied-but-unsaid facts? |

---

## 6. Canonical Data Models

### 6.1 MemoryBaseline

```csharp
/// <summary>
/// A named, timestamped snapshot of a memory benchmark run.
/// The unit of comparison — save one per configuration being tested.
/// </summary>
public class MemoryBaseline
{
    public required string Id { get; init; }              // GUID or slug
    public required string Name { get; init; }            // "Production v2.1"
    public string? Description { get; init; }             // "GPT-4o + SlidingWindow(50)"
    public required DateTimeOffset Timestamp { get; init; }
    public required BaselineAgentInfo Agent { get; init; }
    public required MemoryBenchmarkResult Result { get; init; }

    /// <summary>Octagon scores (0–100), keyed by dimension name.</summary>
    public required IReadOnlyDictionary<string, double> DimensionScores { get; init; }

    /// <summary>Stochastic metadata when multiple runs were used.</summary>
    public StochasticInfo? Stochastic { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}

public class BaselineAgentInfo
{
    public required string AgentName { get; init; }
    public string? ModelId { get; init; }
    public string? ModelVersion { get; init; }
    public string? ReducerStrategy { get; init; }
    public string? MemoryProvider { get; init; }
    public string? Runtime { get; init; }               // "dotnet-10.0"
}

public class StochasticInfo
{
    public int Runs { get; init; }
    public double MeanVariance { get; init; }
    public double MaxVariance { get; init; }
    /// <summary>Dimension names with high variance across runs.</summary>
    public IReadOnlyList<string> UnstableDimensions { get; init; } = [];
}
```

### 6.2 JSON Baseline File Schema

Each baseline serializes to a single JSON file (`{id}.json`):

```json
{
  "id": "baseline-2026-03-15-001",
  "name": "Production v2.1",
  "description": "GPT-4o with SlidingWindow(50) reducer",
  "timestamp": "2026-03-15T14:30:00Z",
  "agent": {
    "agentName": "WeatherAssistant",
    "modelId": "gpt-4o",
    "modelVersion": "2025-01-01",
    "reducerStrategy": "SlidingWindow(50)",
    "memoryProvider": "InMemoryChatHistoryProvider",
    "runtime": "dotnet-10.0"
  },
  "dimensionScores": {
    "Retention":    95.0,
    "Temporal":     82.0,
    "NoiseResil":   71.0,
    "RecallDepth":  88.0,
    "UpdateFidel":  73.0,
    "TopicMgmt":    80.0,
    "Persistence":  85.0,
    "Compression":  68.0
  },
  "overallScore": 80.3,
  "grade": "B",
  "stochastic": null,
  "tags": ["production", "v2.1", "gpt-4o"],
  "metadata": { "preset": "Full", "durationMs": "12400" }
}
```

### 6.3 Baseline Store Interface

```csharp
public interface IBaselineStore
{
    Task SaveAsync(MemoryBaseline baseline, CancellationToken ct = default);
    Task<MemoryBaseline?> LoadAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryBaseline>> ListAsync(
        string? agentName = null,
        IEnumerable<string>? tags = null,
        CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}

// Default implementation: one JSON file per baseline in a configurable directory.
public class JsonFileBaselineStore : IBaselineStore { ... }
```

### 6.4 Comparison Engine Interface

```csharp
public interface IBaselineComparer
{
    /// <summary>Compare two or more baselines in chronological order.</summary>
    BaselineComparison Compare(IReadOnlyList<MemoryBaseline> baselines);
}

public class BaselineComparison
{
    public required IReadOnlyList<MemoryBaseline> Baselines { get; init; }
    public required IReadOnlyList<DimensionComparison> Dimensions { get; init; }
    public required string BestBaselineId { get; init; }
    public required IReadOnlyList<RegressionFlag> Regressions { get; init; }
    public required RadarChartData RadarChart { get; init; }
    public required RadarChartData DeltaRadarChart { get; init; }  // difference spider
}

public class DimensionComparison
{
    public required string DimensionName { get; init; }
    public required IReadOnlyDictionary<string, double> Scores { get; init; }  // baselineId → score
    public required IReadOnlyDictionary<string, double> Deltas { get; init; }  // vs. first baseline
    public required string BestBaselineId { get; init; }
    public required string Direction { get; init; }  // "improved" | "regressed" | "stable"
}

public class RegressionFlag
{
    public required string DimensionName { get; init; }
    public required double DeltaPct { get; init; }
    public required string Severity { get; init; }   // "warning" | "critical"
    public required string Algorithm { get; init; }  // which detection algorithm fired
}
```

### 6.5 Radar Chart Data Model

```csharp
/// <summary>
/// Self-describing data structure consumable directly by Plotly.js, Chart.js, or D3.
/// </summary>
public class RadarChartData
{
    public required IReadOnlyList<string> Axes { get; init; }     // dimension labels
    public double MaxValue { get; init; } = 100;
    public required IReadOnlyList<RadarChartSeries> Series { get; init; }
}

public class RadarChartSeries
{
    public required string Name { get; init; }
    public required IReadOnlyList<double> Values { get; init; }   // one per axis
    public string? Color { get; init; }
    public bool IsDashed { get; init; }  // true for baseline/reference series
}
```

---

## 7. Feature Areas

---

### F1 — Framework Adapter Fix

**Effort: XS | Impact: High**

**Problem:** Both `ChatClientAgentAdapter` and `MAFAgentAdapter` have session reset logic but don't implement `ISessionResettableAgent`, so every cross-session benchmark silently skips.

**Fix:**

1. Move `ISessionResettableAgent` from `AgentEval.Memory` → `AgentEval.Abstractions` (session reset is a general agent capability, not memory-specific).
2. Add `ISessionResettableAgent` to `ChatClientAgentAdapter` (wraps existing `ClearHistory()`).
3. Add `ISessionResettableAgent` declaration to `MAFAgentAdapter` (method already exists).
4. Update `AgentEval.Memory` to reference the interface from Abstractions.

```csharp
// AgentEval.Core — ChatClientAgentAdapter
public class ChatClientAgentAdapter : IEvaluableAgent, IStreamableAgent, ISessionResettableAgent
{
    public Task ResetSessionAsync(CancellationToken ct = default)
    {
        ClearHistory();
        return Task.CompletedTask;
    }
}

// AgentEval.MAF — MAFAgentAdapter (method body already exists)
public class MAFAgentAdapter : IEvaluableAgent, IStreamableAgent, ISessionResettableAgent
{
    // existing: public async Task ResetSessionAsync(CancellationToken ct) { ... }
}
```

**Result:** Cross-session benchmarks work for all wrapped agents. Zero breaking changes.

---

### F2 — Scenario Depth Integration

**Effort: S | Impact: Medium-High**

**Problem:** 15 of 20 built scenarios are never called by the benchmark runner. Each category runs one fixed scenario.

**Fix:** Each `RunXxxAsync` method accepts depth (derived from the benchmark preset) and runs additional scenarios, averaging scores.

```csharp
private async Task<CategoryResult> RunNoiseResilienceAsync(
    IEvaluableAgent agent, BenchmarkDepth depth, CancellationToken ct)
{
    var scores = new List<double>();

    // Quick+: always run
    scores.Add(await RunScenario(_chattyScenarios.CreateBuriedFactsScenario(facts)));

    // Standard+: add topic switching
    if (depth >= BenchmarkDepth.Standard)
        scores.Add(await RunScenario(_chattyScenarios.CreateTopicSwitchingScenario(facts)));

    // Full: add emotional distractor and false information
    if (depth >= BenchmarkDepth.Full)
    {
        scores.Add(await RunScenario(_chattyScenarios.CreateEmotionalDistractorScenario(facts)));
        scores.Add(await RunScenario(_chattyScenarios.CreateFalseInformationScenario(facts)));
    }

    return new CategoryResult { Score = scores.Average(), ... };
}
```

**Scenario-to-preset mapping:**

| Category | Quick (1) | Standard (2) | Full (3+) |
|----------|-----------|--------------|-----------|
| BasicRetention | `CreateBasicMemoryTest` | + `CreateLongTermMemoryTest` | + `CreatePriorityMemoryTest` |
| TemporalReasoning | `CreateSequenceMemoryTest` | + `CreateTimePointMemoryTest` | + `CreateCausalReasoningTest` |
| NoiseResilience | `CreateBuriedFactsScenario` | + `CreateTopicSwitchingScenario` | + `CreateEmotionalDistractorScenario` + `CreateFalseInformationScenario` |
| FactUpdateHandling | `CreateMemoryUpdateTest` | + `RetentionWithDelay` | + conflict test |
| MultiTopic | `CreateBasicMemoryTest` (multi) | + `CategorizedMemory` static | + cross-contamination test |
| CrossSession | evaluator direct (3 facts) | + `CreateCrossSessionMemoryTest` | + `CreateIncrementalLearningTest` |
| ReducerFidelity | evaluator direct (3 facts) | + 5 facts, 40 noise | + priority-weighted |
| ReachBackDepth | depths [5,10,25] | + [50] | + [100] |

**Result:** Full benchmark uses 25+ data points per run instead of 8. Scores are statistically more reliable.

---

### F3 — Baseline Model & Persistence

**Effort: S–M | Impact: High**

**Deliverables:**
1. `MemoryBaseline` and `BaselineAgentInfo` record types (see Section 6.1)
2. `IBaselineStore` interface (see Section 6.3)
3. `JsonFileBaselineStore` — one JSON file per baseline in a configurable directory
4. Extension method `MemoryBenchmarkResult.ToBaseline(name, description, agentInfo, tags)`
5. DI registration in `AddAgentEvalMemory()`

**End-to-end usage:**

```csharp
// Run
var result = await benchmarkRunner.RunBenchmarkAsync(agent, MemoryBenchmark.Full);

// Create and save baseline
var baseline = result.ToBaseline(
    name: "Production v2.1",
    description: "GPT-4o with SlidingWindow(50)",
    agent: new BaselineAgentInfo { AgentName = "WeatherAssistant", ModelId = "gpt-4o" },
    tags: ["production", "v2.1"]);

await baselineStore.SaveAsync(baseline);
```

**File naming convention:** `{agentName}-{timestamp:yyyyMMdd-HHmm}.json` or user-provided slug.

---

### F4 — Comparison Engine

**Effort: M | Impact: Very High**

**Deliverables:**
1. `IBaselineComparer` interface
2. `BaselineComparer` implementation
3. `BaselineComparison` and `DimensionComparison` output models
4. Delta calculation: absolute delta and percentage delta per dimension
5. Direction logic: for all 8 octagon dimensions, "higher is better"
6. Winner identification: best baseline per dimension and overall
7. `RadarChartData` producer for both absolute and delta views
8. Regression flag list (threshold-based in this phase; advanced algorithms in F7)

**Comparer logic (pseudocode):**

```
For each dimension in the Octagon:
  1. Collect [score₁, score₂, ..., scoreₙ] across all baselines
  2. Compute delta from baseline[0] for each subsequent baseline
  3. Flag as regression if delta < -5% (warning) or delta < -10% (critical)
  4. Identify winner (max score)
  5. Add to DimensionComparison

Produce two RadarChartData objects:
  A. Absolute: one series per baseline, values = DimensionScores
  B. Delta: one series per non-baseline run, values = deltas from baseline (centred at 0)
```

---

### F5 — Console Presentation Layer

**Effort: S | Impact: High (immediately useful)**

This is the MVP visualization — works everywhere, no browser required, fits in CI logs and sample output.

**Components:**

**Comparison table (ASCII):**

```
┌────────────────────────────────────────────────────────────────────┐
│  MEMORY RADAR — Agent Comparison                                    │
├──────────────────┬──────────┬──────────┬──────────────────────────┤
│ Dimension        │ Agent A  │ Agent B  │ Delta (B vs A)           │
├──────────────────┼──────────┼──────────┼──────────────────────────┤
│ Retention        │ ████ 95% │ ███░ 78% │ ▼ -17%  A wins           │
│ Temporal         │ ███░ 82% │ ████ 91% │ ▲ +9%   B wins           │
│ Noise Resilience │ ██░░ 71% │ ███░ 75% │ ▲ +4%   B wins           │
│ Recall Depth     │ ████ 88% │ ██░░ 65% │ ▼ -23%  A wins           │
│ Update Fidelity  │ ██░░ 73% │ ███░ 80% │ ▲ +7%   B wins           │
│ Topic Management │ ███░ 80% │ ███░ 82% │ ≈ +2%   tie              │
│ Persistence      │ ████ 85% │ █░░░ 45% │ ▼ -40%  A wins           │
│ Compression      │ ██░░ 68% │ ████ 92% │ ▲ +24%  B wins           │
├──────────────────┼──────────┼──────────┼──────────────────────────┤
│ OVERALL          │  80.3%   │  76.0%   │ ▼ -4.3% A wins overall   │
│ STRONGEST        │ Retention│ Compress │                           │
│ WEAKEST          │ Compress │ Persist  │                           │
└──────────────────┴──────────┴──────────┴──────────────────────────┘
```

**Regression report card:**

```
⚠  2 regressions detected vs. previous baseline:
   CRITICAL  Persistence    -40%  (was 85%, now 45%)
   WARNING   Recall Depth   -23%  (was 88%, now 65%)
✓  3 improvements:
   Compression  +24%, Temporal +9%, Update Fidelity +7%
```

**Progression sparklines (last N runs per dimension):**

```
Retention    ▁▃▅▇█ ↑  95%
Temporal     ▃▅▃▅▇ ~  82%
Compression  █▇▅▃▁ ↓  68%  ⚠ declining
```

**API:**

```csharp
public static class RadarChartConsoleRenderer
{
    public static void RenderComparisonTable(BaselineComparison comparison);
    public static void RenderRegressionReport(BaselineComparison comparison);
    public static void RenderSparklines(IReadOnlyList<MemoryBaseline> history, int maxRuns = 10);
    public static void RenderSingleBaseline(MemoryBaseline baseline);
}
```

---

### F6 — HTML Report Generator

**Effort: M–L | Impact: Very High**

A standalone HTML file with embedded Plotly.js and all benchmark data. The report generator is a .NET class that produces the HTML string from a `BaselineComparison` (or single `MemoryBaseline`).

**Report sections (adapted from TKI Guide for memory context):**

| # | Section | Source |
|---|---------|--------|
| 1 | Header (agent, model, config, timestamp, tags) | Baseline metadata |
| 2 | Score Card (8 dimensions with traffic-light vs. baseline) | DimensionComparison |
| 3 | Radar Chart — absolute values, overlaid per baseline | RadarChartData |
| 4 | Difference Spider — delta from baseline, ±axis centred at 0 | DeltaRadarChart |
| 5 | Regression Report (flagged regressions with severity) | RegressionFlags |
| 6 | Stochastic Analysis (box plots per dimension, confidence bands) | StochasticInfo |
| 7 | Timeline (multi-dimension line chart across all saved baselines) | IBaselineStore |
| 8 | Memory Effectiveness (cold vs. warm comparison, memory delta) | If stochastic enabled |
| 9 | Progression Dashboard (sparklines, cumulative improvement) | History |
| 10 | Raw Data (collapsible JSON) | Baseline JSON |

**Interactivity (via Plotly.js — no additional code required):**
- Hover on any radar axis → exact value + delta from baseline
- Click legend to toggle individual baselines
- Zoom timeline
- Export current view as PNG (built into Plotly toolbar)
- Dark/light mode via CSS `prefers-color-scheme`

**Plotly.js radar trace (ready to embed):**

```javascript
// Absolute radar
const traces = baselines.map((b, i) => ({
  type: 'scatterpolar',
  r: DIMENSIONS.map(d => b.dimensionScores[d] ?? 0),
  theta: DIMENSIONS,
  fill: 'toself',
  name: b.name,
  fillcolor: COLORS[i] + '33',  // semi-transparent
  line: { color: COLORS[i], dash: i === 0 ? 'solid' : 'dash' }
}));

// Difference spider (delta from first baseline)
const ref = baselines[0];
const deltaTraces = baselines.slice(1).map((b, i) => ({
  type: 'scatterpolar',
  r: DIMENSIONS.map(d => (b.dimensionScores[d] ?? 0) - (ref.dimensionScores[d] ?? 0)),
  theta: DIMENSIONS,
  fill: 'toself',
  name: `Δ ${b.name} vs ${ref.name}`,
  line: { color: DELTA_COLORS[i] }
}));

Plotly.newPlot('radar', traces, {
  polar: { radialaxis: { visible: true, range: [0, 100] } },
  showlegend: true, title: 'Memory Benchmark — Octagon'
});
```

**Generator API:**

```csharp
public class MemoryBenchmarkHtmlReporter
{
    /// <summary>
    /// Generate a standalone HTML report for a single baseline.
    /// </summary>
    public string GenerateSingleReport(MemoryBaseline baseline);

    /// <summary>
    /// Generate a standalone HTML report comparing multiple baselines.
    /// Includes radar overlay, difference spider, regression analysis.
    /// </summary>
    public string GenerateComparisonReport(BaselineComparison comparison);

    /// <summary>
    /// Generate a full history report from all saved baselines.
    /// Includes timeline, progression, and regression detection.
    /// </summary>
    public string GenerateHistoryReport(
        IReadOnlyList<MemoryBaseline> history,
        RegressionReport? regressions = null);
}
```

---

### F7 — Regression Detection

**Effort: M | Impact: High (critical for CI)**

Four complementary algorithms. Use all four; each catches a failure mode the others miss.

**Algorithm 1 — Threshold (fast, predictable):**
Compare current value to the immediately previous baseline.
- `delta < -5%` → Warning
- `delta < -10%` → Critical

```csharp
RegressionSeverity? CheckThreshold(double previous, double current,
    double warningPct = 0.05, double criticalPct = 0.10)
{
    var delta = (previous - current) / previous;
    if (delta >= criticalPct) return RegressionSeverity.Critical;
    if (delta >= warningPct)  return RegressionSeverity.Warning;
    return null;
}
```

**Algorithm 2 — Moving average (noise-resilient):**
Compare current value to the trailing N-run moving average. Flags if current is >K standard deviations below the average.

```csharp
RegressionResult CheckMovingAverage(IReadOnlyList<double> history,
    int window = 5, double threshold = 2.0)
{
    var recent = history.TakeLast(window).ToList();
    var mean = recent.Average();
    var std  = Math.Sqrt(recent.Select(v => Math.Pow(v - mean, 2)).Average());
    var curr = history.Last();
    return new RegressionResult
    {
        IsRegression = curr < mean - threshold * std,
        ZScore = std > 0 ? (curr - mean) / std : 0,
        Mean = mean, Std = std
    };
}
```

**Algorithm 3 — Linear regression trend (catches slow degradation):**
Fit a linear regression over the last N baselines. If slope is negative and statistically significant (p < 0.05), flag a regression trend. This catches steady slow degradation that threshold and moving average both miss.

**Algorithm 4 — Change-point detection (CUSUM):**
Detect when the underlying distribution of a dimension has shifted. Most appropriate for dimensions with stochastic variance. Implement as a simple CUSUM (cumulative sum control chart):

```csharp
bool DetectChangePoint(IReadOnlyList<double> values, double sensitivity = 1.0)
{
    var mean = values.Average();
    double cusum = 0;
    foreach (var v in values)
    {
        cusum = Math.Max(0, cusum + (mean - v) - sensitivity);
        if (cusum > sensitivity * 3) return true;  // change point detected
    }
    return false;
}
```

**Regression Report output:**

```csharp
public class RegressionReport
{
    public required IReadOnlyList<RegressionFlag> Flags { get; init; }
    public required IReadOnlyList<string> StableDimensions { get; init; }
    public required IReadOnlyList<string> ImprovingDimensions { get; init; }
    public bool HasCriticalRegressions => Flags.Any(f => f.Severity == "critical");
}
```

---

### F8 — Stochastic Memory Analysis

**Effort: M | Impact: Medium-High (for publishable/production results)**

**Why it matters:** A single benchmark run is a sample from a distribution. An agent scoring 90% ± 15% on Retention is less trustworthy than one scoring 85% ± 2%.

**Implementation:** `StochasticMemoryBenchmarkRunner` wraps the existing `MemoryBenchmarkRunner` and runs it N times, collecting per-dimension scores across runs.

```csharp
public class StochasticMemoryOptions
{
    public int Runs { get; init; } = 3;           // min 3, recommend 5+
    public double VarianceThreshold { get; init; } = 0.10;  // 10% std dev = "unstable"
}

public class StochasticMemoryResult
{
    public required MemoryBaseline AggregateBaseline { get; init; }  // mean scores
    public required IReadOnlyList<MemoryBaseline> IndividualRuns { get; init; }

    /// <summary>Per-dimension: mean, std dev, 95% CI, stability flag.</summary>
    public required IReadOnlyDictionary<string, DimensionStats> DimensionStats { get; init; }
}

public class DimensionStats
{
    public double Mean { get; init; }
    public double StdDev { get; init; }
    public double ConfidenceLower { get; init; }  // 95% CI lower
    public double ConfidenceUpper { get; init; }  // 95% CI upper
    public bool IsUnstable { get; init; }         // StdDev > threshold
}
```

**The 2×2 Memory Effectiveness Matrix** (from the TKI Guide — the most actionable stochastic output):

For agents tested with and without memory (cold start vs. warm start):

|  | Low Stochastic Variance | High Stochastic Variance |
|--|------------------------|-------------------------|
| **Memory Helps** (warm > cold) | ✅ Ideal — stable + memory adds value | ⚠ Risky — memory helps but behaviour is unpredictable |
| **Memory Hurts** (warm < cold) | 🔍 Investigate — stable but memory is net negative | ❌ Critical — unstable and memory makes it worse |

Visualized as a scatter plot: X = stochastic variance, Y = memory delta (warm − cold score).

**Confidence bands on radar chart:** When stochastic data is available, each radar polygon gets a shaded band at ±1σ around the mean polygon. This immediately shows which dimensions are reliable vs. noisy.

---

### F9 — Timeline & Progression Tracking

**Effort: M | Impact: High (long-term value)**

Once multiple baselines are saved, timeline and progression tracking become available automatically.

**Timeline chart (Plotly line chart):**
- X-axis: baseline timestamps or run labels
- Y-axis: dimension score (0–100)
- One line per dimension; grouped by related dimensions (e.g., Memory group: Retention, Depth, Persistence)
- Confidence band: ±1σ from stochastic data when available
- Regression markers: red dots where a regression flag fired
- Milestone annotations: version tags from baseline metadata

**Progression score (single aggregate number per run):**

```
progression_score = Σ (weight_i × score_i) / Σ weight_i
```

Default weights: Retention ×2, Persistence ×1.5, all others ×1. User-configurable.

**Sparkline dashboard:** A grid showing the last N runs as small inline charts per dimension. At a glance: what's trending up, flat, or down.

**Progression alerts (configurable per dimension):**
- **Regression alert:** dimension drops >K standard deviations below its moving average
- **Plateau alert:** dimension hasn't improved by >X% in last N runs (ceiling hit)
- **Trade-off alert:** two dimensions moving in opposite directions (e.g., Compression improving while Recall Depth falling — over-compression)

---

### F10 — CI Integration

**Effort: S | Impact: High (production adoption)**

**CLI command:**

```bash
# Run benchmark and compare against saved baseline
dotnet run --project AgentEval.Cli -- memory benchmark \
  --agent MyAgent \
  --preset Full \
  --baseline baselines/release-v2.0.json \
  --output report.html \
  --fail-on-regression

# List saved baselines
dotnet run --project AgentEval.Cli -- memory list --agent MyAgent

# Compare two baselines
dotnet run --project AgentEval.Cli -- memory compare \
  --baseline baselines/v2.0.json \
  --current baselines/v2.1.json \
  --output comparison-report.html
```

**Exit codes:**
- `0` — all dimensions stable or improved
- `1` — one or more warning-level regressions
- `2` — one or more critical regressions

**GitHub Actions integration:**

```yaml
- name: Memory Benchmark
  run: |
    dotnet run --project AgentEval.Cli -- memory benchmark \
      --agent ${{ env.AGENT_NAME }} \
      --preset Standard \
      --baseline baselines/main.json \
      --output memory-report.html \
      --fail-on-regression

- name: Upload Report
  uses: actions/upload-artifact@v4
  with:
    name: memory-benchmark-report
    path: memory-report.html
  if: always()  # upload even on regression failure
```

---

## 8. Implementation Phases

| Phase | Features | Effort | Impact | Dependency |
|-------|----------|--------|--------|-----------|
| **Phase 1** | F1 (adapter fix) | XS | High | None |
| **Phase 2** | F2 (scenario depth), F3 (baseline model) | S–M | High | Phase 1 |
| **Phase 3** | F4 (comparison engine), F5 (console renderer) | M | Very High | Phase 2 |
| **Phase 4** | F6 (HTML report), F7 (regression detection) | M–L | Very High | Phase 3 |
| **Phase 5** | F8 (stochastic), F9 (timeline), F10 (CI) | M | High | Phase 4 |

### Phase 1 — Framework Adapter Fix (Quick Win)

1. Move `ISessionResettableAgent` to `AgentEval.Abstractions`
2. Implement in `ChatClientAgentAdapter` (wraps `ClearHistory()`)
3. Declare in `MAFAgentAdapter` (method body already exists)
4. Update `AgentEval.Memory` references
5. Add/update tests

**Deliverable:** Cross-session benchmarks work for all wrapped agents.

### Phase 2 — Richer Benchmarks

1. Add `BenchmarkDepth` enum (or derive from existing preset name)
2. Modify each `RunXxxAsync` to accept depth, run multiple scenarios, average scores
3. Create `MemoryBaseline`, `BaselineAgentInfo`, `StochasticInfo` models
4. Create `IBaselineStore` + `JsonFileBaselineStore`
5. Add `MemoryBenchmarkResult.ToBaseline(...)` extension
6. Register in DI: `AddAgentEvalMemory()` now includes `IBaselineStore`
7. Tests for each scenario path

**Deliverable:** Scores are more statistically robust; baselines can be saved to disk.

### Phase 3 — Comparison & Console Visualization

1. Create `IBaselineComparer` + `BaselineComparer`
2. Implement delta calculation, direction, winner logic
3. Produce `RadarChartData` (absolute + delta)
4. Implement `RadarChartConsoleRenderer` (ASCII table, regression card, sparklines)
5. Wire into `MemoryBenchmarkRunner.RunBenchmarkAsync()` fluent result API

**Deliverable:** `comparison.PrintComparisonTable()` — the full ASCII radar experience.

### Phase 4 — HTML Report & Regression Detection

1. Implement all 4 regression detection algorithms
2. Create `RegressionReport` and `RegressionFlag` models
3. Build `MemoryBenchmarkHtmlReporter` — generates standalone HTML
4. Embed Plotly.js traces for: radar, difference spider, regression-annotated timeline, stochastic box plots
5. Add interactivity: baseline selector, metric toggles, export PNG
6. Write to file or return as string (for CI artifact upload)

**Deliverable:** Full interactive HTML report that can be committed to git or uploaded as CI artifact.

### Phase 5 — Statistical Rigor, Timeline & CI

1. Implement `StochasticMemoryBenchmarkRunner` (wraps existing runner, N runs)
2. Add confidence interval calculation (95% CI using t-distribution or bootstrap)
3. Add memory effectiveness 2×2 matrix (cold vs. warm comparison)
4. Build timeline section in HTML report (multi-dimension line chart with confidence bands)
5. Build progression dashboard (sparklines + cumulative improvement chart)
6. Implement CLI commands (`memory benchmark`, `memory compare`, `memory list`)
7. Add exit code logic for CI fail-on-regression
8. GitHub Actions example in documentation

**Deliverable:** Production-grade memory benchmarking with full CI integration.

---

## 9. Key Design Decisions

### D1: .NET-native report generation (not Node.js)

The TKI Guide suggests Node.js as the report generator because Plotly.js is JavaScript. For AgentEval, all statistical analysis stays in .NET (typed, testable, integrated with existing evaluators). The HTML generator is a pure .NET string-building class that embeds pre-computed JSON and a Plotly.js CDN reference. The same JSON data that feeds the HTML also feeds any other consumer.

### D2: JSON file storage (not SQLite)

Simple, human-readable, diffable in git, no new dependencies. The `IBaselineStore` abstraction means a SQLite or remote store can be dropped in later without changing consumers. Matches the existing trace serialization pattern.

### D3: All 4 regression algorithms (not just threshold)

Each algorithm catches a different failure mode: threshold catches abrupt drops, moving average catches noisy signals, linear regression catches slow steady degradation, CUSUM catches distribution shifts. Running all 4 with independent flag collections avoids false negatives. The CI command only fails on critical flags by default — warning flags are reported but don't break the build unless `--fail-on-warning` is set.

### D4: The Octagon as the primary normalization target

All 8 dimensions map to 0–100. "Better" always means higher. Inverse metrics (if added in the future) are inverted before storage. This means all visualization code is uniform — no per-axis special casing.

### D5: `ISessionResettableAgent` belongs in Abstractions

Session reset is a general agent capability. The Memory project should consume it, not own it. Moving it to Abstractions lets Core and MAF adapters implement it naturally without taking a dependency on Memory. This is the single most important architectural correction.

---

## 10. End-to-End Usage (Target State)

```csharp
// Run a full benchmark
var result = await benchmarkRunner.RunBenchmarkAsync(agent, MemoryBenchmark.Full);

// Save as named baseline
var baseline = result.ToBaseline(
    name: "v2.1 — GPT-4o with SlidingWindow(50)",
    agent: new BaselineAgentInfo { AgentName = "WeatherAssistant", ModelId = "gpt-4o" },
    tags: ["production", "v2.1"]);
await baselineStore.SaveAsync(baseline);

// Load history and compare
var history   = await baselineStore.ListAsync(agentName: "WeatherAssistant");
var comparison = baselineComparer.Compare(history);

// Console output (works everywhere)
RadarChartConsoleRenderer.RenderComparisonTable(comparison);
RadarChartConsoleRenderer.RenderRegressionReport(comparison);

// HTML report (for CI artifacts and sharing)
var html = htmlReporter.GenerateComparisonReport(comparison);
await File.WriteAllTextAsync("memory-report.html", html);
```

**Console output:**

```
┌────────────────────────────────────────────────────────────────────┐
│  MEMORY RADAR — WeatherAssistant  (3 baselines compared)           │
├──────────────────┬──────────┬──────────┬──────────────────────────┤
│ Dimension        │ v2.0     │ v2.1     │ Delta (v2.1 vs v2.0)     │
├──────────────────┼──────────┼──────────┼──────────────────────────┤
│ Retention        │ ████ 93% │ ████ 95% │ ▲ +2%                    │
│ Temporal         │ ███░ 80% │ ███░ 82% │ ▲ +2%                    │
│ Noise Resilience │ ██░░ 69% │ ██░░ 71% │ ▲ +3%                    │
│ Recall Depth     │ ████ 87% │ ████ 88% │ ▲ +1%                    │
│ Update Fidelity  │ ██░░ 71% │ ██░░ 73% │ ▲ +3%                    │
│ Topic Management │ ███░ 79% │ ███░ 80% │ ▲ +1%                    │
│ Persistence      │ ████ 86% │ ████ 85% │ ▼ -1%  ⚠                 │
│ Compression      │ ██░░ 65% │ ██░░ 68% │ ▲ +4%                    │
├──────────────────┼──────────┼──────────┼──────────────────────────┤
│ OVERALL          │  78.8%   │  80.3%   │ ▲ +1.5%  v2.1 wins       │
└──────────────────┴──────────┴──────────┴──────────────────────────┘

⚠  1 warning: Persistence -1% (within threshold, monitoring)
✓  7 improvements across all other dimensions
```

---

*This plan supersedes both `Memory-Benchmark-Assessment-and-Roadmap.md` and `agent-tki-benchmark-reporting.md` for implementation planning purposes. The source documents remain as reference for rationale and historical context.*
