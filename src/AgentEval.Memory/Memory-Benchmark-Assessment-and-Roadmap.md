# AgentEval Memory Benchmark — Assessment & Reporting Roadmap

**Date:** March 21, 2026 (updated from March 8, 2026)
**Scope:** Benchmark Reporting, Baseline Persistence, HTML Report Generation, Radar Visualization, Memory Archetypes, Export Integration, Configuration Identity, Stochastic Evaluation
**Status:** Living document — consolidated from Assessment, Reporting Proposals, Implementation Plan, code analysis, and research
**Prerequisites:** All core memory evaluation features (F01-F27, F18) are 100% implemented and tested. Framework adapters completed. 348 unit tests for the memory evaluation framework + 7,557 Core tests passing.

---

## Table of Contents

1. [Executive Summary: What We Have vs. What We Need](#1-executive-summary)
2. [Decisions Already Made (Locked In)](#2-decisions-already-made)
3. [Decisions Challenged & Revised](#3-decisions-challenged--revised)
4. [Export Integration — Reusing AgentEval.DataLoaders](#4-export-integration)
5. [Configuration Identity & Workflow Design](#5-configuration-identity--workflow-design)
6. [The Scenario Library — Where Evaluations Are Defined](#6-the-scenario-library)
7. [Benchmark Definitions & Wiring](#7-benchmark-definitions--wiring)
8. [Radar Visualization — Pentagon vs Hexagon vs Octagon](#8-radar-visualization)
9. [Memory Archetypes — Rethought From Scratch](#9-memory-archetypes)
10. [Stochastic Evaluation for Memory](#10-stochastic-evaluation-for-memory)
11. [Data Models — C# Classes to Implement](#11-data-models)
12. [The Reporting & Baseline Module — Full Design](#12-the-reporting--baseline-module)
13. [Folder Structure & Discovery](#13-folder-structure--discovery)
14. [HTML Report — How It Works](#14-html-report)
15. [JsonFileBaselineStore — The Glue](#15-jsonfilebaselinestore)
16. [DI Registration & Integration](#16-di-registration--integration)
17. [End-to-End User Workflows](#17-end-to-end-user-workflows)
18. [Scenario Depth Integration](#18-scenario-depth-integration)
19. [Implementation Phases](#19-implementation-phases)
20. [Design Decisions Log](#20-design-decisions-log)

---

## 1. Executive Summary

### What's Done (100% Implemented, Tested, Verified)

| Component | Status | Evidence |
|-----------|--------|----------|
| 8 benchmark categories (Retention, Temporal, Noise, Depth, Updates, MultiTopic, CrossSession, Reducer) | Done | `Evaluators/MemoryBenchmarkRunner.cs` — all 8 `RunXxxAsync` methods |
| 3 presets (Quick/Standard/Full) with weighted scoring | Done | `Models/MemoryBenchmark.cs` — static properties with weight allocations |
| Grade/star ratings with automatic weight renormalization for skipped categories | Done | `Models/MemoryBenchmarkResult.cs` — calculated properties |
| Actionable recommendations per weak category | Done | `MemoryBenchmarkResult.Recommendations` |
| 24+ scenario factory methods across 5 interfaces | Done | `Scenarios/` + `Temporal/` — 4 scenario providers |
| Framework adapters with `ISessionResettableAgent` | Done | Moved to `AgentEval.Abstractions/Core/`, both adapters implement it |
| 5 memory-specific `IMetric` implementations with bridge | Done | `Extensions/MemoryEvaluationContextExtensions.ToEvaluationContext()` |
| All 6 issues from analysis-and-fix-plan resolved | Done | Session reset, forbidden fact penalty, metrics bridge, token counts, stop-words, tests |
| Full DI registration | Done | `AddAgentEvalMemory()` + 5 granular methods |
| 348 unit tests for the memory evaluation framework per TFM | Done | Covers evaluators, models, scenarios, DI |
| **Complete export pipeline (AgentEval.DataLoaders)** | Done | **6 formats: JSON, JUnit XML, Markdown, TRX, CSV, Directory** |

### What's Missing (This Document's Scope)

| Gap | Impact | This Document's Answer |
|-----|--------|----------------------|
| **No persistence** — results vanish after execution | Can't track improvement over time | `IBaselineStore` + `JsonFileBaselineStore` |
| **No identity** — no agent config metadata on results | Can't compare configurations meaningfully | `AgentBenchmarkConfig` + `ConfigurationId` |
| **No bridge to export pipeline** — `MemoryBenchmarkResult` not connected to `IResultExporter` | Can't export to JUnit/CSV/Markdown | `MemoryBenchmarkResult.ToEvaluationReport()` extension |
| **No visualization** — scores are numbers in a console | Hard to communicate memory quality to stakeholders | `report.html` embedded template + Chart.js |
| **No comparison** — can't overlay two benchmark runs | Can't answer "which config is better?" | Configuration identity → timeline or radar overlay |
| **No reference points** — scores lack context | "Is 71% noise resilience good?" | Memory-architecture archetypes (not domain archetypes) |
| **Underutilized scenario library** — using 25% of available scenarios | Statistically thin per-category scores | Scenario depth levels tied to presets |

---

## 2. Decisions Already Made

These are locked in from implementation evidence and should not be re-debated.

### D1: Framework Adapters — COMPLETED

`ISessionResettableAgent` moved to `AgentEval.Abstractions/Core/`. Both `ChatClientAgentAdapter` and `MAFAgentAdapter` implement it. Build: 0 warnings, 0 errors. All tests pass.

### D2: JSON Files for Baseline Storage

`JsonFileBaselineStore` writes individual JSON files + auto-rebuilds `manifest.json`. The `IBaselineStore` interface allows future implementations (SQLite, cloud).

### D3: Convention-Based Folder Structure

`.agenteval/benchmarks/{AgentName}/` with `report.html` + `manifest.json` + `baselines/*.json`. No URL parameters, no build step.

### D4: report.html as Embedded Resource

Ships in the NuGet package. Copied to the agent's benchmark folder once on first save.

### D5: Dimension Scores Pre-Computed in Baseline JSON

Old baselines survive formula changes because they carry their own computed dimensions.

### D6: Scenario Depth Within Existing Presets

Quick=1 scenario, Standard=2, Full=3+. No new presets needed.

---

## 3. Decisions Challenged & Revised

### Challenge C1: Hexagon (6) vs Pentagon (5) vs Octagon (8)

**Original decision:** Hexagon (6 axes) — consolidate 8 categories into 6.

**Challenge:** After research, data visualization best practices recommend 5-7 axes for radar charts ([Highcharts](https://www.highcharts.com/blog/tutorials/radar-chart-explained-when-they-work-when-they-fail-and-how-to-use-them-right/), [Data-to-Viz](https://www.data-to-viz.com/caveat/spider.html), [Bold BI](https://www.boldbi.com/blog/radar-charts-best-practices-and-examples/)). Both 5 and 6 fall in the optimal range. The question is whether the hexagon adds value over a pentagon or whether it's already pushing readability.

**Revised decision:** See [Section 8](#8-radar-visualization) for full analysis. **Pentagon (5) is recommended as the primary shape** with an octagon (8) option for detailed drill-down. The hexagon was a compromise that doesn't fully commit to either simplicity or detail.

### Challenge C2: Domain-Based Archetypes Are Wrong

**Original decision:** 6 archetypes by business domain (Customer Support, Healthcare, Coding Assistant, etc.).

**Challenge:** We're evaluating *memory*, not business domains. A "Customer Support Agent" archetype says nothing about memory architecture — it might use in-memory chat history, RAG, vector store, or a summarizing reducer. The archetype scores were hand-waived. Research from [MemoryAgentBench](https://arxiv.org/pdf/2602.11243) and [Memory for Autonomous LLM Agents](https://arxiv.org/html/2603.07670) categorizes memory systems by *mechanism* (context-resident, retrieval-augmented, reflective, hierarchical), not by business domain.

**Revised decision:** See [Section 9](#9-memory-archetypes). Archetypes should describe **memory architecture patterns**, not business use cases. "Stateless Context Window Agent" vs "RAG-Enhanced Agent" vs "Full Persistent Memory Agent" — these predict benchmark shapes far better than "Healthcare" vs "Coding".

### Challenge C3: Building a Parallel Export System

**Original decision:** Build `JsonExporter`, `MarkdownExporter`, `CsvExporter` inside `AgentEval.Memory`.

**Challenge:** AgentEval already has a complete, production-ready export system in `AgentEval.DataLoaders` with 6 formats (JSON, JUnit XML, Markdown, TRX, CSV, Directory), an `IExporterRegistry` for dynamic registration, `ResultExporterFactory`, full DI auto-discovery, and a bridge pattern (`TestSummary.ToEvaluationReport()`). Building parallel exporters is code duplication.

**Revised decision:** See [Section 4](#4-export-integration). Create a `MemoryBenchmarkResult.ToEvaluationReport()` bridge. All existing exporters work automatically. The HTML report is an *additional* visualization layer, not a replacement.

### Challenge C4: Stochastic Testing Should Be Considered

**Original roadmap:** No mention of stochastic evaluation for memory benchmarks.

**Challenge:** Research shows LLM agents are inherently stochastic ([KDD 2025 survey](https://arxiv.org/html/2507.21504v1)), and [pass@k metrics](https://arxiv.org/html/2507.21504v1) are becoming standard for enterprise reliability. Memory *should* be more deterministic than reasoning tasks, but the LLM judge introduces variance. Should we support multi-run evaluation?

**Revised decision:** See [Section 10](#10-stochastic-evaluation-for-memory). Optional stochastic mode — not mandatory, but available.

---

## 4. Export Integration

### The Existing Export Pipeline (DO NOT DUPLICATE)

AgentEval already has everything we need in `AgentEval.DataLoaders`:

```
src/AgentEval.Abstractions/
├── Core/IResultExporter.cs          ← interface: Format, FileExtension, ExportAsync()
├── Core/IExporterRegistry.cs        ← dynamic registry for custom exporters
└── Models/
    ├── EvaluationReport.cs          ← common report model
    └── TestSummaryExtensions.cs     ← bridge: TestSummary → EvaluationReport

src/AgentEval.DataLoaders/Exporters/
├── JsonExporter.cs                  ← structured JSON (camelCase, prettified)
├── CsvExporter.cs                   ← RFC 4180 compliant, dynamic metric columns
├── MarkdownExporter.cs              ← GitHub-flavored with emoji, configurable sections
├── JUnitXmlExporter.cs              ← CI/CD: GitHub Actions, Azure DevOps, Jenkins
├── TrxExporter.cs                   ← Visual Studio TRX format
├── DirectoryExporter.cs             ← ADR-002: results.jsonl + summary.json + run.json
├── ResultExporterFactory.cs         ← factory by format or extension
└── ExporterRegistry.cs              ← thread-safe ConcurrentDictionary registry
```

### The Bridge: MemoryBenchmarkResult → EvaluationReport

Following the existing pattern (`TestSummary.ToEvaluationReport()`), we add:

```csharp
// In AgentEval.Memory/Extensions/MemoryBenchmarkReportExtensions.cs (NEW)
public static class MemoryBenchmarkReportExtensions
{
    /// <summary>
    /// Converts a MemoryBenchmarkResult to an EvaluationReport for the standard export pipeline.
    /// Enables: await jsonExporter.ExportAsync(result.ToEvaluationReport(), stream);
    /// </summary>
    public static EvaluationReport ToEvaluationReport(
        this MemoryBenchmarkResult result,
        string? agentName = null,
        string? modelName = null)
    {
        return new EvaluationReport
        {
            Name = result.BenchmarkName,
            TotalTests = result.CategoryResults.Count,
            PassedTests = result.CategoryResults.Count(c => !c.Skipped && c.Score >= 70),
            FailedTests = result.CategoryResults.Count(c => !c.Skipped && c.Score < 70),
            SkippedTests = result.CategoryResults.Count(c => c.Skipped),
            OverallScore = result.OverallScore,
            StartTime = DateTimeOffset.UtcNow - result.Duration,
            EndTime = DateTimeOffset.UtcNow,
            Agent = new AgentInfo { Name = agentName, Model = modelName },
            Metadata = new Dictionary<string, string>
            {
                ["Grade"] = result.Grade,
                ["Stars"] = result.Stars.ToString(),
                ["BenchmarkType"] = "MemoryBenchmark"
            },
            TestResults = result.CategoryResults.Select(c => new TestResultSummary
            {
                Name = c.CategoryName,
                Category = "MemoryBenchmark",
                Score = c.Score,
                Passed = !c.Skipped && c.Score >= 70,
                Skipped = c.Skipped,
                DurationMs = (long)c.Duration.TotalMilliseconds,
                Error = c.SkipReason,
                MetricScores = new Dictionary<string, double>
                {
                    [$"memory_{c.ScenarioType}"] = c.Score
                }
            }).ToList()
        };
    }
}
```

### What This Unlocks (Zero New Export Code)

```csharp
var result = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Full);
var report = result.ToEvaluationReport(agentName: "WeatherAssistant", modelName: "gpt-4o");

// ALL existing exporters work automatically:
await new JsonExporter().ExportAsync(report, jsonStream);
await new CsvExporter().ExportAsync(report, csvStream);
await new MarkdownExporter().ExportAsync(report, mdStream);
await new JUnitXmlExporter().ExportAsync(report, junitStream);   // CI/CD integration
await new DirectoryExporter().ExportToDirectoryAsync(report, outputDir);  // ADR-002
```

### The HTML Report Is Separate

The HTML report is NOT an exporter — it's a persistent visualization layer that reads from `manifest.json` + baseline JSONs. The two systems complement each other:

- **Export pipeline**: Point-in-time snapshot for CI/CD, PR comments, dashboards
- **HTML report**: Historical tracking, configuration comparison, interactive exploration

---

## 5. Configuration Identity & Workflow Design

### The Problem

A user runs benchmarks over time with the same agent configuration (tracking improvement) and also runs benchmarks with different configurations (comparing memory strategies). The report needs to know which is which to route to the right visualization:

- **Same configuration, different dates** → Timeline (score progression over time)
- **Different configurations, same or different dates** → Radar overlay (shape comparison)

### The Solution: ConfigurationId

Every baseline carries a `ConfigurationId` — a deterministic hash of the configuration that matters for memory evaluation:

```csharp
public class AgentBenchmarkConfig
{
    // ... all properties ...

    /// <summary>
    /// Deterministic identifier computed from the configuration properties that affect
    /// memory behavior: model, reducer, memory provider, context providers.
    /// Same config → same ID → timeline grouping.
    /// Different config → different ID → radar comparison.
    /// </summary>
    public string ConfigurationId => ComputeConfigurationId();

    private string ComputeConfigurationId()
    {
        // Hash the properties that affect memory performance
        var key = string.Join("|",
            AgentName ?? "",
            ModelId ?? "",
            ReducerStrategy ?? "",
            MemoryProvider ?? "",
            string.Join(",", ContextProviders.OrderBy(p => p)));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
    }
}
```

### How the Report Uses ConfigurationId

```javascript
// In report.html JavaScript:

// 1. Group baselines by ConfigurationId
const groups = {};
manifest.baselines.forEach(b => {
    const key = b.configuration_id;
    (groups[key] = groups[key] || []).push(b);
});

// 2. Same config group → Timeline tab shows progression
//    Multiple groups → Hexagon tab shows overlay comparison
//    Groups[key].length > 1 → This config has multiple runs over time
```

### User Workflow: Tracking Improvement Over Time

```csharp
// The user runs the same config multiple times over weeks
var config = new AgentBenchmarkConfig
{
    AgentName = "WeatherAssistant",
    ModelId = "gpt-4o",
    ReducerStrategy = "SlidingWindow(50)",
    MemoryProvider = "InMemoryChatHistoryProvider"
};
// ConfigurationId: "a1b2c3d4e5f6" (same every time for this config)

// Week 1: Initial benchmark
var result1 = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Full);
await store.SaveAsync(result1.ToBaseline("Week 1", config, tags: ["sprint-1"]));

// Week 2: After improving system prompt
var result2 = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Full);
await store.SaveAsync(result2.ToBaseline("Week 2", config, tags: ["sprint-2"]));

// Week 3: After tuning temperature
var result3 = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Full);
await store.SaveAsync(result3.ToBaseline("Week 3", config, tags: ["sprint-3"]));

// The report's Timeline tab shows these 3 runs as a progression line
// because they share the same ConfigurationId
```

### User Workflow: Comparing Configurations

```csharp
// The user compares different memory strategies
var configA = new AgentBenchmarkConfig
{
    AgentName = "WeatherAssistant",
    ModelId = "gpt-4o",
    ReducerStrategy = "SlidingWindow(50)",
    MemoryProvider = "InMemoryChatHistoryProvider"
};

var configB = new AgentBenchmarkConfig
{
    AgentName = "WeatherAssistant",
    ModelId = "gpt-4o",
    ReducerStrategy = "SummarizingReducer",       // ← different!
    MemoryProvider = "InMemoryChatHistoryProvider"
};

var configC = new AgentBenchmarkConfig
{
    AgentName = "WeatherAssistant",
    ModelId = "gpt-4o-mini",                       // ← different!
    ReducerStrategy = "SlidingWindow(50)",
    MemoryProvider = "InMemoryChatHistoryProvider"
};

var resultA = await runner.RunBenchmarkAsync(agentA, MemoryBenchmark.Full);
await store.SaveAsync(resultA.ToBaseline("SlidingWindow(50)", configA));

var resultB = await runner.RunBenchmarkAsync(agentB, MemoryBenchmark.Full);
await store.SaveAsync(resultB.ToBaseline("SummarizingReducer", configB));

var resultC = await runner.RunBenchmarkAsync(agentC, MemoryBenchmark.Full);
await store.SaveAsync(resultC.ToBaseline("gpt-4o-mini", configC));

// The report's Radar tab shows 3 different shapes overlaid
// because they have different ConfigurationIds
// The user clicks config cards to toggle them on/off
```

### What Gets Stored in the Baseline JSON

```json
{
  "id": "bl-005",
  "name": "SlidingWindow(50)",
  "configuration_id": "a1b2c3d4e5f6",
  "agent_config": {
    "agent_name": "WeatherAssistant",
    "model_id": "gpt-4o",
    "reducer_strategy": "SlidingWindowChatReducer(window:50)",
    "memory_provider": "InMemoryChatHistoryProvider",
    "context_providers": ["InMemoryChatHistoryProvider"]
  },
  "timestamp": "2026-03-15T14:30:00Z",
  "overall_score": 80.3,
  "grade": "B",
  "category_results": { ... },
  "dimension_scores": { ... }
}
```

The `configuration_id` is the key that the report uses to decide: timeline or comparison.

---

## 6. The Scenario Library — Where Evaluations Are Defined

The memory evaluation scenarios live in `src/AgentEval.Memory/Scenarios/` and `src/AgentEval.Memory/Temporal/`. These are the **definitions of what gets evaluated** — each factory method creates a `MemoryTestScenario` with setup steps (facts to plant, noise to inject, session resets) and queries (questions to ask + expected/forbidden facts to verify).

### Complete Scenario Map

```
src/AgentEval.Memory/
├── Scenarios/
│   ├── IMemoryScenarios.cs              ← 4 factory methods
│   │   ├── CreateBasicMemoryTest()      → plant facts, query each
│   │   ├── CreateLongTermMemoryTest()   → facts + N conversation turns, then query
│   │   ├── CreatePriorityMemoryTest()   → high vs low importance facts
│   │   └── CreateMemoryUpdateTest()     → initial facts + corrections, query expects new
│   │
│   ├── MemoryScenarios.cs               ← implementation + 4 additional static helpers
│   │   ├── BasicRetention()             → simple fact retention
│   │   ├── RetentionWithDelay()         → facts + neutral conversation padding
│   │   ├── CategorizedMemory()          → facts grouped by topic category
│   │   └── RapidFire()                  → rapid-fire fact absorption
│   │
│   ├── IChattyConversationScenarios.cs  ← 4 factory methods
│   │   ├── CreateBuriedFactsScenario()  → facts buried in 3:1 noise ratio
│   │   ├── CreateTopicSwitchingScenario() → facts scattered among rapid topic changes
│   │   ├── CreateEmotionalDistractorScenario() → facts mixed with emotional content
│   │   └── CreateFalseInformationScenario()    → true vs false facts + correction
│   │
│   ├── ChattyConversationScenarios.cs   ← implementation + 3 additional helpers
│   │   ├── BuriedFacts()                → 20-item noise bank interleaved with facts
│   │   ├── RapidTopicChanges()          → 15 topic bank (space, cooking, travel...)
│   │   └── EmotionalDistractors()       → 15 emotional statement bank
│   │
│   ├── ICrossSessionScenarios.cs        ← 4 factory methods
│   │   ├── CreateCrossSessionMemoryTest() → facts + N session resets + query
│   │   ├── CreateRestartPersistenceTest() → facts + agent restarts + query
│   │   ├── CreateIncrementalLearningTest() → new facts per session, cumulative query
│   │   └── CreateContextSwitchingTest()   → facts across different contexts
│   │
│   └── CrossSessionScenarios.cs         ← implementation + 4 additional helpers
│       ├── CreateBasicCrossSession()     → 2-phase: establish → reset → test
│       ├── CreateSelectiveMemory()       → persistent vs session-only facts
│       ├── CreateMultiSession()          → facts distributed across N sessions
│       └── CreateInterference()          → new facts interfering with old memories
│
└── Temporal/
    ├── ITemporalMemoryScenarios.cs      ← 5 factory methods
    │   ├── CreateTimePointMemoryTest()      → events at specific timestamps
    │   ├── CreateSequenceMemoryTest()       → event ordering + chronological queries
    │   ├── CreateCausalReasoningTest()      → cause-effect chains
    │   ├── CreateOverlappingTimeWindowTest() → facts within time boundaries
    │   └── CreateMemoryDegradationTest()    → retention at 1h, 1d, 7d intervals
    │
    └── TemporalMemoryScenarios.cs       ← implementation + 4 additional helpers
        ├── CreateTimeTravel()            → "what did you know at time T?"
        ├── CreateFactEvolution()         → info updates/corrections over time
        ├── CreateCausalReasoning()       → cause-effect with timestamps
        └── CreateMemoryDegradation()     → retention at configurable intervals

TOTAL: 24+ scenario factory methods across 5 interfaces
```

### How a Scenario Works (Structure)

Every scenario is a `MemoryTestScenario` containing:

```
MemoryTestScenario
├── Name: "Buried Facts Scenario"
├── Description: "Tests recall through conversational noise"
├── Steps: [ordered sequence]
│   ├── MemoryStep.Fact("My name is José")        ← info to remember
│   ├── MemoryStep.Noise("Nice weather today!")    ← distraction
│   ├── MemoryStep.Noise("I love pizza")           ← distraction
│   ├── MemoryStep.Fact("I'm allergic to peanuts") ← info to remember
│   ├── MemoryStep.Noise("What do you think?")     ← distraction
│   ├── MemoryStep.System("[SESSION_RESET_POINT]")  ← reset trigger (cross-session only)
│   └── MemoryStep.Conversation("How are you?")    ← neutral interaction
├── Queries: [verification questions]
│   ├── MemoryQuery("What is my name?", expected: [José], forbidden: [])
│   └── MemoryQuery("What allergies do I have?", expected: [peanuts], forbidden: [])
└── Metadata: { "RequiresSessionReset": true, ... }
```

The `MemoryTestRunner` (engine) orchestrates: sends each step to the agent, then asks each query and passes the response to `MemoryJudge` (LLM-based scorer) to evaluate how well the agent recalled the expected facts.

---

## 7. Benchmark Definitions & Wiring

### Where Benchmarks Are Defined

Benchmarks are defined in `Models/MemoryBenchmark.cs` as static presets. Each preset specifies which categories to evaluate and their weights:

```
Models/MemoryBenchmark.cs
├── MemoryBenchmark.Quick     → 3 categories, fast CI feedback
│   ├── BasicRetention    (40% weight)
│   ├── TemporalReasoning (30% weight)
│   └── NoiseResilience   (30% weight)
│
├── MemoryBenchmark.Standard  → 6 categories, comprehensive
│   ├── BasicRetention    (20%)
│   ├── TemporalReasoning (15%)
│   ├── NoiseResilience   (15%)
│   ├── ReachBackDepth    (20%)
│   ├── FactUpdateHandling(15%)
│   └── MultiTopic        (15%)
│
└── MemoryBenchmark.Full      → 8 categories, complete suite
    ├── BasicRetention    (15%)
    ├── TemporalReasoning (10%)
    ├── NoiseResilience   (10%)
    ├── ReachBackDepth    (15%)
    ├── FactUpdateHandling(10%)
    ├── MultiTopic        (10%)
    ├── CrossSession      (15%) ← requires ISessionResettableAgent
    └── ReducerFidelity   (15%) ← requires ISessionResettableAgent
```

### How Benchmarks Wire to Scenarios

`MemoryBenchmarkRunner.RunBenchmarkAsync()` routes each category to the appropriate scenario factory. Here is the complete wiring:

```
Evaluators/MemoryBenchmarkRunner.cs
│
├── RunBenchmarkAsync(agent, benchmark)
│   ├── For each category in benchmark:
│   │   ├── Reset agent session (if ISessionResettableAgent)
│   │   └── switch (category.ScenarioType):
│   │
│   ├── BasicRetention → RunBasicRetentionAsync()
│   │   └── Uses: IMemoryScenarios.CreateBasicMemoryTest()
│   │       Facts: José, Copenhagen, peanut allergy, 3pm meeting, email preference
│   │       Queries: "What is my name?", "Where do I live?", etc.
│   │
│   ├── TemporalReasoning → RunTemporalReasoningAsync()
│   │   └── Uses: ITemporalMemoryScenarios.CreateSequenceMemoryTest()
│   │       Facts: Python (12mo ago) → Junior dev (6mo) → C# (2mo) → Promotion (1mo)
│   │       Queries: chronological ordering, "what was learned first?"
│   │
│   ├── NoiseResilience → RunNoiseResilienceAsync()
│   │   └── Uses: IChattyConversationScenarios.CreateBuriedFactsScenario()
│   │       Facts: Peanut allergy (importance:100), Meeting at 3pm (importance:80)
│   │       Noise: 20-item bank of chatter interleaved
│   │
│   ├── ReachBackDepth → RunReachBackAsync()
│   │   └── Uses: IReachBackEvaluator.EvaluateAsync()
│   │       Fact: Peanut allergy
│   │       Depths: [5, 10, 25] noise turns between fact and query
│   │
│   ├── FactUpdateHandling → RunFactUpdateAsync()
│   │   └── Uses: IMemoryScenarios.CreateMemoryUpdateTest()
│   │       Initial: Favorite color=blue, Car=Honda
│   │       Updated: Favorite color=green, Car=Tesla
│   │       Queries expect updated values, forbid initial values
│   │
│   ├── MultiTopic → RunMultiTopicAsync()
│   │   └── Uses: IMemoryScenarios.CreateBasicMemoryTest()
│   │       Facts across 5 topics: dog name, work company, birthday, languages, supplements
│   │       Tests cross-contamination (does pet info bleed into work answers?)
│   │
│   ├── CrossSession → RunCrossSessionAsync()
│   │   └── Uses: ICrossSessionEvaluator.EvaluateAsync()
│   │       Facts: José, Copenhagen, peanuts
│   │       Session reset → query → threshold 0.8
│   │       SKIPPED if agent doesn't implement ISessionResettableAgent
│   │
│   └── ReducerFidelity → RunReducerFidelityAsync()
│       └── Uses: IReducerEvaluator.EvaluateAsync()
│           Facts: Peanut (100), Meeting (80), Email pref (50)
│           20 noise messages → apply IChatReducer → verify facts survive
│           SKIPPED if agent doesn't implement ISessionResettableAgent
│
└── Returns: MemoryBenchmarkResult (overall score, grade, stars, per-category, recommendations)
```

### What the Benchmark Runner Currently Does NOT Have

- **No baseline/reporting code** — the runner produces `MemoryBenchmarkResult` and stops
- **No configuration metadata capture** — no way to record what model/reducer/provider was used
- **No persistence** — results are ephemeral
- **No bridge to the export pipeline** — can't produce `EvaluationReport`

These are exactly what this roadmap adds.

---

## 8. Radar Visualization — Pentagon vs Hexagon vs Octagon

### The Research

Data visualization research consistently recommends 5-7 axes for radar charts:
- [Highcharts](https://www.highcharts.com/blog/tutorials/radar-chart-explained-when-they-work-when-they-fail-and-how-to-use-them-right/): "Limit to 5-8 axes"
- [Data-to-Viz](https://www.data-to-viz.com/caveat/spider.html): "Too many axes makes comparison confusing"
- [Bold BI](https://www.boldbi.com/blog/radar-charts-best-practices-and-examples/): "4-7 variables for readability"
- [Origami Plot](https://pmc.ncbi.nlm.nih.gov/articles/PMC10599795/): Proposes improvements for >7 variables

### The Three Options

| Shape | Axes | What We Merge | Readability (2-4 configs) | Information Loss |
|-------|------|---------------|---------------------------|-----------------|
| **Pentagon (5)** | 5 | 3 merges | Excellent — cleanest shape | Some — merges Noise+Compression |
| **Hexagon (6)** | 6 | 2 merges | Good — still readable | Minimal — natural groupings only |
| **Octagon (8)** | 8 | 0 merges | Poor — labels overlap | None — full resolution |

### Pentagon (5) — Recommended

```
         ① Recall
          ╱╲
         ╱  ╲
④ Org. ╱    ╲ ② Resilience
       ╲    ╱
        ╲  ╱
     ⑤ Persist.
        ╲╱
    ③ Temporal
```

| Axis | Source Categories | Formula | Why This Works |
|---|---|---|---|
| **Recall** | BasicRetention + ReachBackDepth | avg(retention, depth) | "Can it remember?" — breadth + depth are two halves of recall |
| **Resilience** | NoiseResilience + ReducerFidelity | avg(noise, reducer) | "Does it lose information?" — both are about info loss. The *mechanism* differs (input-side vs reducer-side) but the *user question* is the same: "is my agent losing facts?" The KPI scorecard still shows the detailed breakdown |
| **Temporal** | TemporalReasoning + FactUpdateHandling | avg(temporal, updates) | "Can it handle change?" — both about tracking fact evolution over time |
| **Persistence** | CrossSession | direct (1:1) | "Does it survive resets?" — binary, architectural, stands alone |
| **Organization** | MultiTopic | direct (1:1) | "Can it organize by topic?" — orthogonal to all others |

**Why merge Noise+Compression now (reversing the earlier decision):**

The previous argument was "different failure modes = different axes." True, but:
1. The **user** doesn't care about the mechanism — they care "is my agent losing information?"
2. The **fix** insight is in the 8-category KPI scorecard, not the radar shape
3. The radar is for **shape comparison** between configs — at 5 axes, shapes are maximally distinct
4. Pentagon overlays with 3-4 configs are significantly more readable than hexagon overlays

**The KPI scorecard still shows all 8 categories.** The pentagon doesn't hide information — it organizes the shape view for readability while the detailed view preserves full resolution.

### When to Use Which View

| View | Shape | Axes | Use Case |
|------|-------|------|----------|
| **Radar overlay** | Pentagon (5) | Consolidated | Comparing 2-4 configs — shape tells the story |
| **KPI scorecard** | 8 cards | All raw categories | Detailed drill-down — each category with explanation |
| **Bar chart** | Grouped bars | All 8 categories | Comparing 5+ configs — shapes become unreadable |
| **Timeline** | Line chart | All 8 categories | Score progression over time per category |

---

## 9. Memory Archetypes — Rethought From Scratch

### Why Domain-Based Archetypes Were Wrong

The original archetypes (Customer Support, Healthcare, Coding Assistant) describe **business use cases**, not **memory architectures**. A "Customer Support Agent" might use:
- A tiny context window with no persistence → terrible memory
- RAG with vector search → great recall, no persistence
- Full persistent memory with summarization → balanced profile

The business domain doesn't predict the memory shape. What predicts it is the **memory architecture**.

### Memory-Architecture Archetypes

Research from [MemoryAgentBench (2026)](https://arxiv.org/pdf/2602.11243), [Memory for Autonomous LLM Agents (2026)](https://arxiv.org/html/2603.07670), and [MemBench (2025)](https://aclanthology.org/2025.findings-acl.989/) identifies five mechanism families for agent memory. Our archetypes map to these:

#### Archetype 1: Stateless Context Window Agent
**"No memory beyond the prompt"**

```
Recall: 45  |  Resilience: 30  |  Temporal: 25  |  Persistence: 0  |  Organization: 30
```

- **What it is:** Agent with small context window, no persistent storage, aggressive message counting reducer (keep last 5-10 messages). The most basic agent — raw LLM with conversation history.
- **Why it matters:** This is the **floor**. If your agent scores below this, something is fundamentally broken. Typical of: quick prototypes, stateless API wrappers, chatbots with `MessageCountingChatReducer(keep:5)`.
- **Memory mechanism:** Context-resident only. No external store.
- **Expected weakness:** Everything degrades rapidly with conversation length. Zero persistence.
- **Typical config:** `gpt-3.5-turbo`, `MessageCountingChatReducer(keep:5-10)`, `InMemoryChatHistoryProvider`

#### Archetype 2: Sliding Window Agent
**"Remembers recent context"**

```
Recall: 70  |  Resilience: 55  |  Temporal: 50  |  Persistence: 0  |  Organization: 55
```

- **What it is:** Agent with a larger sliding window (20-50 messages). Better recall within the window, but facts outside the window are permanently lost. No cross-session memory.
- **Why it matters:** The most common production pattern. Good enough for short conversations but silently fails for long ones. The benchmark reveals *exactly where* the window boundary causes failures via ReachBackDepth.
- **Memory mechanism:** Context-resident with windowed trimming.
- **Expected weakness:** ReachBackDepth drops sharply at window boundary. Zero persistence.
- **Typical config:** `gpt-4o`, `SlidingWindowChatReducer(window:20-50)`, `InMemoryChatHistoryProvider`

#### Archetype 3: Summarizing Agent
**"Compresses but retains essence"**

```
Recall: 75  |  Resilience: 70  |  Temporal: 65  |  Persistence: 0  |  Organization: 70
```

- **What it is:** Agent using a summarizing reducer — instead of dropping old messages, it compresses them. Better resilience and temporal awareness because summaries preserve key facts. But summarization is lossy — some details are lost.
- **Why it matters:** The first "smart" memory pattern. The benchmark reveals *what information the summarizer loses* via ReducerFidelity. Common trade-off: good compression but temporal ordering often degrades.
- **Memory mechanism:** Context-resident with compression/summarization.
- **Expected weakness:** ReducerFidelity reveals information loss. Fine details (phone numbers, specific dates) often lost. Temporal ordering may scramble.
- **Typical config:** `gpt-4o`, `SummarizingReducer`, `InMemoryChatHistoryProvider`

#### Archetype 4: RAG-Enhanced Agent
**"Retrieves on demand"**

```
Recall: 88  |  Resilience: 65  |  Temporal: 60  |  Persistence: 75  |  Organization: 85
```

- **What it is:** Agent with retrieval-augmented generation — facts are stored in a vector database and retrieved by semantic similarity when relevant. Strong recall and organization because retrieval is targeted. Persistence depends on the vector store.
- **Why it matters:** The most popular "advanced" pattern. The benchmark reveals whether retrieval is accurate enough (recall) and whether temporal context survives embedding (temporal). Common failure: temporal reasoning degrades because embeddings don't capture time well.
- **Memory mechanism:** Retrieval-augmented with external store.
- **Expected weakness:** Temporal reasoning (embeddings lose time context). Noise resilience depends on retrieval quality. Compression not applicable (no reducer).
- **Typical config:** `gpt-4o`, `SemanticSearchProvider`, vector store (Qdrant, Pinecone), optional `SlidingWindowChatReducer`

#### Archetype 5: Full Persistent Memory Agent
**"Remembers everything, forever"**

```
Recall: 92  |  Resilience: 80  |  Temporal: 85  |  Persistence: 95  |  Organization: 88
```

- **What it is:** Agent with multiple memory layers — short-term context window + long-term persistent store + semantic retrieval. The "learning and remembering advanced agent." Facts survive session resets, are organized by topic, and can be queried temporally.
- **Why it matters:** The gold standard for production agents that need to learn from users over time. The benchmark validates that all layers work together — a fact learned in session 1 is retrievable in session 10 with correct temporal context.
- **Memory mechanism:** Hierarchical — context-resident + external persistent store + retrieval.
- **Expected weakness:** Complexity — more moving parts means more failure modes. Noise resilience can suffer if the persistent store ingests noise alongside facts.
- **Typical config:** `gpt-4o`, `SummarizingReducer` + `LongTermMemoryProvider` + `SemanticSearchProvider`, persistent vector store

#### Archetype 6: Expert Router (No Long-Term Memory)
**"Routes to specialists, doesn't learn"**

```
Recall: 50  |  Resilience: 45  |  Temporal: 30  |  Persistence: 10  |  Organization: 60
```

- **What it is:** A routing agent that dispatches to specialized sub-agents. The router itself maintains minimal state — it knows which sub-agent to call but doesn't accumulate user facts. Each sub-agent may have its own memory, but the router doesn't aggregate.
- **Why it matters:** Common in multi-agent architectures (MAF, AutoGen). The benchmark reveals whether the router preserves user context across sub-agent calls. Typical failure: user tells their name to Agent A, gets routed to Agent B, which doesn't know the name.
- **Memory mechanism:** Minimal context-resident. Memory lives in sub-agents, not the router.
- **Expected weakness:** Persistence and temporal reasoning are very low because the router doesn't store long-term facts. Organization may be moderate (the router knows which sub-agent handles which domain).
- **Typical config:** `MAFAgentAdapter`, `MessageCountingChatReducer(keep:10)`, per-sub-agent memory

### Why These Archetypes Work Better

1. **Predictive:** The memory architecture directly predicts the benchmark shape. A `SlidingWindowChatReducer(50)` agent *will* score well on Recall and poorly on Persistence — the archetype confirms this.
2. **Actionable:** Seeing your agent vs the "Summarizing Agent" archetype immediately suggests: "upgrade your reducer from sliding window to summarization."
3. **Architecture-aligned:** Maps to the five memory mechanism families from research: context-resident, retrieval-augmented, reflective, hierarchical, policy-learned.
4. **Independent of business domain:** A healthcare agent and a customer support agent using the same memory architecture will have the *same* memory benchmark shape.

### Archetype File Format

```json
{
  "schema_version": "1.0",
  "archetypes": [
    {
      "id": "stateless-context",
      "name": "Stateless Context Window",
      "description": "No memory beyond the prompt. MessageCountingChatReducer(keep:5-10).",
      "memory_mechanism": "context-resident",
      "typical_config": {
        "reducers": ["MessageCountingChatReducer(keep:5-10)"],
        "context_providers": ["InMemoryChatHistoryProvider"]
      },
      "expected_scores": {
        "Recall": 45, "Resilience": 30, "Temporal": 25, "Persistence": 0, "Organization": 30
      },
      "critical_dimensions": [],
      "acceptable_weaknesses": ["Persistence", "Temporal", "Resilience"]
    }
  ]
}
```

---

## 10. Stochastic Evaluation for Memory

### The Question: Is Memory Deterministic Enough?

Memory *should* be more deterministic than reasoning tasks — "What is my name?" has a factual answer. But:

1. **LLM judge variance:** The `MemoryJudge` uses an LLM to score responses. Even with temperature=0, different phrasings of the same correct answer get slightly different scores.
2. **Agent response variance:** The agent under test may phrase answers differently each time.
3. **Noise scenario variance:** Chatty scenarios inject different noise patterns, which can shift scores.

Research confirms this is a real issue: "Because LLM-based agents are inherently stochastic, measuring consistency requires executing the same task multiple times" ([KDD 2025 Survey](https://arxiv.org/html/2507.21504v1)).

### Decision: Optional Stochastic Mode

**For CI (default):** Single run. Fast. Deterministic enough for regression detection.

**For configuration comparison:** Optional multi-run mode with statistical reporting.

```csharp
// Single run (default, for CI)
var result = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Full);

// Stochastic mode (for careful comparison)
var stochasticResult = await runner.RunBenchmarkAsync(
    agent,
    MemoryBenchmark.Full,
    options: new BenchmarkOptions { Runs = 5 });

// stochasticResult now includes:
// - Mean score per category
// - StdDev per category
// - Min/Max per category
// - Coefficient of variation
// - pass@k metrics (passed in k out of 5 runs)
```

### What This Means for Reporting

The baseline JSON optionally includes stochastic data:

```json
{
  "category_results": {
    "BasicRetention": {
      "score": 92.4,
      "grade": "A",
      "stochastic": {
        "runs": 5,
        "mean": 92.4,
        "stddev": 2.1,
        "min": 89,
        "max": 95,
        "cv": 0.023,
        "pass_at_k": { "1": 1.0, "3": 1.0, "5": 1.0 }
      }
    }
  }
}
```

The HTML report renders this as error bars on the radar chart and confidence bands on the timeline. When stochastic data is absent (single run), the chart renders normally.

### When Stochastic Mode Is Worth It

| Scenario | Recommended | Why |
|----------|-------------|-----|
| CI regression testing | No (single run) | Speed. Detect large regressions (>5%) reliably |
| Comparing two configs | Yes (3-5 runs) | A 2% difference could be noise. 5 runs gives confidence |
| Publishing benchmark baselines | Yes (5 runs) | Reproducibility for stakeholders |
| Development iteration | No (single run) | Speed. Get directional signal fast |

### Implementation Cost

Low. The benchmark runner already runs one iteration per category. Multi-run mode wraps this in a loop and aggregates statistics. The `DirectoryExporter` from `AgentEval.DataLoaders` already computes mean, stddev, p50, p95 — we can reuse those statistical helpers.

---

## 11. Data Models

### 11.1 MemoryBaseline

```csharp
public class MemoryBaseline
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string ConfigurationId { get; init; }        // ← NEW: deterministic config hash
    public required AgentBenchmarkConfig AgentConfig { get; init; }
    public required BenchmarkExecutionInfo Benchmark { get; init; }
    public required double OverallScore { get; init; }
    public required string Grade { get; init; }
    public required int Stars { get; init; }
    public required IReadOnlyDictionary<string, CategoryScoreEntry> CategoryResults { get; init; }
    public required IReadOnlyDictionary<string, double> DimensionScores { get; init; }  // pentagon 5 axes
    public required IReadOnlyList<string> Recommendations { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}
```

### 11.2 AgentBenchmarkConfig

```csharp
public class AgentBenchmarkConfig
{
    public required string AgentName { get; init; }
    public string? AgentType { get; init; }
    public string? ModelId { get; init; }
    public string? ModelVersion { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public string? ReducerStrategy { get; init; }
    public IReadOnlyList<string> ContextProviders { get; init; } = [];
    public string? MemoryProvider { get; init; }
    public IReadOnlyDictionary<string, string> CustomConfig { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Deterministic hash of memory-affecting properties.
    /// Same config → same ID → timeline grouping in reports.
    /// Different config → different ID → radar comparison.
    /// </summary>
    public string ConfigurationId => ComputeConfigurationId();

    private string ComputeConfigurationId()
    {
        var key = string.Join("|",
            AgentName ?? "", ModelId ?? "", ReducerStrategy ?? "",
            MemoryProvider ?? "", string.Join(",", ContextProviders.OrderBy(p => p)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
    }
}
```

### 11.3 CategoryScoreEntry

```csharp
public class CategoryScoreEntry
{
    public required double Score { get; init; }
    public required string Grade { get; init; }
    public required bool Skipped { get; init; }
    public int ScenarioCount { get; init; } = 1;
    public string? Recommendation { get; init; }
    public StochasticData? Stochastic { get; init; }    // ← optional, multi-run
}

public class StochasticData
{
    public required int Runs { get; init; }
    public required double Mean { get; init; }
    public required double StdDev { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }
    public double CoefficientOfVariation => Mean > 0 ? StdDev / Mean : 0;
}
```

### 11.4 BenchmarkManifest

```csharp
public class BenchmarkManifest
{
    public required string SchemaVersion { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string GeneratedBy { get; init; }
    public required ManifestAgentInfo Agent { get; init; }
    public required IReadOnlyList<ManifestBenchmarkGroup> Benchmarks { get; init; }
    public string? Archetypes { get; init; }
}

public class ManifestBaselineEntry
{
    public required string Id { get; init; }
    public required string File { get; init; }
    public required string Name { get; init; }
    public required string ConfigurationId { get; init; }    // ← for timeline vs comparison routing
    public required DateTimeOffset Timestamp { get; init; }
    public required double OverallScore { get; init; }
    public required string Grade { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}
```

### 11.5 RadarChartData (Pentagon)

```csharp
public class RadarChartData
{
    public required IReadOnlyList<string> Axes { get; init; }     // ["Recall", "Resilience", "Temporal", "Persistence", "Organization"]
    public double MaxValue { get; init; } = 100;
    public required IReadOnlyList<RadarChartSeries> Series { get; init; }
}
```

---

## 12. The Reporting & Baseline Module — Full Design

### Architecture Overview

```
User Code                               AgentEval
─────────                               ─────────
                                                            ┌─── AgentEval.DataLoaders ───┐
var result = await runner                                   │  (EXISTING - DO NOT MODIFY)  │
    .RunBenchmarkAsync(agent,            MemoryBenchmark-   │  JsonExporter                │
     MemoryBenchmark.Full);              Result             │  CsvExporter                 │
                                         │                  │  MarkdownExporter            │
       ┌─────────────────────────────────┼──────────────┐   │  JUnitXmlExporter            │
       │                                 │              │   │  TrxExporter                 │
       │  .ToEvaluationReport()  ────────┴──────→ EvaluationReport  │  DirectoryExporter    │
       │  (NEW bridge method)                    │              │                           │
       │                                         └──→ await exporter.ExportAsync(report)    │
       │                                                    └──────────────────────────────┘
       │
       │  .ToBaseline(name, config, tags) ──→ MemoryBaseline
       │                                      │
       │  await store.SaveAsync(baseline) ──→ JsonFileBaselineStore
       │                                      ├── writes baselines/{date}_{slug}.json
       │                                      ├── rebuilds manifest.json
       │                                      └── copies report.html (if missing)
       │
       │  Open report.html in browser ──────→ report.html (static template)
       │                                      ├── fetches manifest.json
       │                                      ├── groups baselines by ConfigurationId
       │                                      ├── same config → Timeline tab
       │                                      ├── diff config → Radar (pentagon) tab
       │                                      └── loads archetypes.json
       │
```

### Where New Code Lives

```
src/AgentEval.Memory/
├── Models/                              (existing folder)
│   ├── MemoryBaseline.cs                ← NEW
│   ├── AgentBenchmarkConfig.cs          ← NEW (with ConfigurationId)
│   ├── BenchmarkExecutionInfo.cs        ← NEW
│   ├── BenchmarkManifest.cs             ← NEW (4 classes)
│   ├── RadarChartData.cs                ← NEW
│   ├── BaselineComparison.cs            ← NEW
│   ├── CategoryScoreEntry.cs            ← NEW (with optional StochasticData)
│   ├── MemoryBenchmark.cs               (EXISTING - benchmark presets)
│   ├── MemoryBenchmarkResult.cs         (EXISTING - benchmark results)
│   ├── MemoryTestScenario.cs            (EXISTING - scenario structure)
│   ├── MemoryFact.cs                    (EXISTING - fact definition)
│   └── MemoryQuery.cs                   (EXISTING - query definition)
│
├── Scenarios/                           (EXISTING - the scenario library)
│   ├── IMemoryScenarios.cs              (4 basic memory scenarios)
│   ├── MemoryScenarios.cs               (+ 4 static helpers)
│   ├── IChattyConversationScenarios.cs  (4 noise/chatty scenarios)
│   ├── ChattyConversationScenarios.cs   (+ 3 helpers)
│   ├── ICrossSessionScenarios.cs        (4 cross-session scenarios)
│   └── CrossSessionScenarios.cs         (+ 4 helpers)
│
├── Temporal/                            (EXISTING - temporal scenarios)
│   ├── ITemporalMemoryScenarios.cs      (5 temporal scenarios)
│   └── TemporalMemoryScenarios.cs       (+ 4 helpers)
│
├── Evaluators/                          (EXISTING - benchmark runner + evaluators)
│   ├── MemoryBenchmarkRunner.cs         (EXISTING - wires presets → scenarios)
│   ├── IMemoryBenchmarkRunner.cs        (EXISTING)
│   ├── CrossSessionEvaluator.cs         (EXISTING)
│   ├── ReachBackEvaluator.cs            (EXISTING)
│   └── ReducerEvaluator.cs              (EXISTING)
│
├── Reporting/                           ← NEW FOLDER
│   ├── IBaselineStore.cs                ← NEW
│   ├── IBaselineComparer.cs             ← NEW
│   ├── JsonFileBaselineStore.cs         ← NEW
│   ├── BaselineComparer.cs              ← NEW
│   ├── PentagonConsolidator.cs          ← NEW (8→5 dimension mapping)
│   └── BaselineExtensions.cs            ← NEW (.ToBaseline() extension)
│
├── Report/                              ← NEW FOLDER (embedded resources)
│   ├── report.html                      ← NEW (the HTML template)
│   └── archetypes.json                  ← NEW (6 memory-architecture archetypes)
│
├── Extensions/
│   ├── AgentEvalMemoryServiceCollectionExtensions.cs  (UPDATED: add reporting DI)
│   ├── MemoryBenchmarkReportExtensions.cs             ← NEW (.ToEvaluationReport() bridge)
│   ├── CanRememberExtensions.cs         (EXISTING)
│   └── MemoryEvaluationContextExtensions.cs (EXISTING)
│
├── Engine/                              (EXISTING - core engine)
│   ├── MemoryTestRunner.cs              (scenario orchestration)
│   └── MemoryJudge.cs                   (LLM-based scoring)
│
└── Metrics/                             (EXISTING - 5 IMetric implementations)
    ├── MemoryRetentionMetric.cs
    ├── MemoryTemporalMetric.cs
    ├── MemoryNoiseResilienceMetric.cs
    ├── MemoryReachBackMetric.cs
    └── MemoryReducerFidelityMetric.cs
```

---

## 13. Folder Structure & Discovery

### Single-Agent (Most Common)

```
.agenteval/
└── benchmarks/
    └── WeatherAssistant/
        ├── report.html              ← static template (copied once from embedded resource)
        ├── manifest.json            ← auto-generated, groups baselines by ConfigurationId
        ├── archetypes.json          ← 6 memory-architecture reference profiles
        └── baselines/
            ├── 2026-03-01_v1.0-gpt35-msgcount.json     config_id: a1b2...
            ├── 2026-03-08_v1.1-gpt35-msgcount.json     config_id: a1b2... (same → timeline)
            ├── 2026-03-15_v2.0-gpt4o-sliding.json      config_id: c3d4... (different → radar)
            └── 2026-03-20_v2.1-gpt4o-summarizer.json   config_id: e5f6... (different → radar)
```

### Multi-Agent

```
.agenteval/
└── benchmarks/
    ├── archetypes.json              ← shared across agents
    ├── WeatherAssistant/
    │   ├── report.html
    │   ├── manifest.json
    │   └── baselines/
    └── CustomerSupportBot/
        ├── report.html
        ├── manifest.json
        └── baselines/
```

---

## 14. HTML Report — How It Works

Five tabs, same as the prototype in `agenteval-memory-benchmark-report.html`:

| Tab | What It Shows | Routing Logic |
|-----|---------------|---------------|
| **Overview** | Grade hero, 8 KPI scorecards, delta vs previous run | Latest baseline |
| **Pentagon** | 5-axis radar overlay, config selector cards, grouped bar chart | Baselines grouped by ConfigurationId. Different IDs → different shapes |
| **Timeline** | Overall score line, per-category lines, sparklines | Baselines with SAME ConfigurationId, ordered by timestamp |
| **Comparer** | A/B dropdown, delta waterfall, head-to-head table | Any two baselines |
| **Structure** | Folder structure documentation, manifest/JSON examples | Static |

The report loads `manifest.json` (relative to itself), uses `configuration_id` to group baselines:
- Timeline tab: shows only baselines with the same config ID (progression over time)
- Pentagon tab: shows one representative from each config ID (shape comparison)

---

## 15. JsonFileBaselineStore

```csharp
public interface IBaselineStore
{
    Task SaveAsync(MemoryBaseline baseline, CancellationToken ct = default);
    Task<MemoryBaseline?> LoadAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryBaseline>> ListAsync(
        string? agentName = null, IEnumerable<string>? tags = null,
        CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
```

`SaveAsync` does three things:
1. Writes `baselines/{date}_{slug}.json`
2. Rebuilds `manifest.json` (scans all baseline files, groups by preset)
3. Copies `report.html` and `archetypes.json` from embedded resources if missing

JSON uses `snake_case_lower` naming to match web/JS conventions.

---

## 16. DI Registration & Integration

```csharp
// NEW method in AgentEvalMemoryServiceCollectionExtensions.cs
public static IServiceCollection AddAgentEvalMemoryReporting(
    this IServiceCollection services,
    Action<MemoryReportingOptions>? configure = null)
{
    var options = new MemoryReportingOptions();
    configure?.Invoke(options);
    services.AddSingleton(options);
    services.AddScoped<IBaselineStore, JsonFileBaselineStore>();
    services.AddScoped<IBaselineComparer, BaselineComparer>();
    return services;
}

// Updated to include reporting
public static IServiceCollection AddAgentEvalMemory(this IServiceCollection services)
{
    services.AddAgentEvalMemoryCore();
    services.AddAgentEvalMemoryScenarios();
    services.AddAgentEvalMemoryTemporal();
    services.AddAgentEvalMemoryEvaluators();
    services.AddAgentEvalMemoryMetrics();
    services.AddAgentEvalMemoryReporting();   // ← NEW
    return services;
}
```

---

## 17. End-to-End User Workflows

### Workflow 1: Single Run → Export → Report

```csharp
var result = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Full);

// Export to existing pipeline (JUnit for CI, Markdown for PR, etc.)
var report = result.ToEvaluationReport(agentName: "WeatherAssistant", modelName: "gpt-4o");
await new JUnitXmlExporter().ExportAsync(report, junitStream);

// Persist as baseline for HTML report
var baseline = result.ToBaseline("v2.1 Production", config, tags: ["production"]);
await store.SaveAsync(baseline);
// → opens: .agenteval/benchmarks/WeatherAssistant/report.html
```

### Workflow 2: Track Improvement Over Time (Same Config)

```csharp
var config = new AgentBenchmarkConfig
{
    AgentName = "WeatherAssistant",
    ModelId = "gpt-4o",
    ReducerStrategy = "SlidingWindow(50)",
    MemoryProvider = "InMemoryChatHistoryProvider"
};

// Sprint 1
await store.SaveAsync(result1.ToBaseline("Sprint 1", config));
// Sprint 2 (same config → same ConfigurationId)
await store.SaveAsync(result2.ToBaseline("Sprint 2", config));
// Sprint 3
await store.SaveAsync(result3.ToBaseline("Sprint 3", config));

// report.html → Timeline tab shows 3-point progression line
```

### Workflow 3: Compare Configurations (Different Configs)

```csharp
var configA = new AgentBenchmarkConfig { ..., ReducerStrategy = "SlidingWindow(50)" };
var configB = new AgentBenchmarkConfig { ..., ReducerStrategy = "SummarizingReducer" };
var configC = new AgentBenchmarkConfig { ..., ModelId = "gpt-4o-mini" };

await store.SaveAsync(resultA.ToBaseline("SlidingWindow", configA));
await store.SaveAsync(resultB.ToBaseline("Summarizer", configB));
await store.SaveAsync(resultC.ToBaseline("gpt-4o-mini", configC));

// report.html → Pentagon tab shows 3 overlaid shapes
// User clicks config cards to toggle shapes on/off
```

---

## 18. Scenario Depth Integration

Currently each category runs 1 scenario. With depth levels tied to presets:

| Category | Quick (1) | Standard (2) | Full (3+) |
|----------|-----------|--------------|-----------|
| BasicRetention | `CreateBasicMemoryTest` | + `CreateLongTermMemoryTest` | + `CreatePriorityMemoryTest` |
| TemporalReasoning | `CreateSequenceMemoryTest` | + `CreateTimePointMemoryTest` | + `CreateCausalReasoningTest` + `CreateOverlappingTimeWindowTest` |
| NoiseResilience | `CreateBuriedFactsScenario` | + `CreateTopicSwitchingScenario` | + `CreateEmotionalDistractorScenario` + `CreateFalseInformationScenario` |
| FactUpdateHandling | `CreateMemoryUpdateTest` | + `RetentionWithDelay` | + custom conflict test |
| MultiTopic | `CreateBasicMemoryTest` (multi) | + `CategorizedMemory` | + cross-contamination test |
| CrossSession | Evaluator direct | + `CreateCrossSessionMemoryTest` | + `CreateIncrementalLearningTest` + `CreateContextSwitchingTest` |
| ReducerFidelity | Evaluator direct | + 5 facts, 40 noise | + priority-weighted facts |
| ReachBackDepth | [5,10,25] | + [5,10,25,50] | + [5,10,25,50,100] |

**Impact:** Full benchmark: 8 data points → 25+ data points. `ScenarioCount` recorded per category.

---

## 19. Implementation Phases

### Phase 1: Export Bridge + Baseline Model + Persistence + HTML Report

| Task | Files | Description |
|------|-------|-------------|
| 1.1 | `Extensions/MemoryBenchmarkReportExtensions.cs` | `.ToEvaluationReport()` bridge to existing export pipeline |
| 1.2 | `Models/MemoryBaseline.cs` | Baseline snapshot model |
| 1.3 | `Models/AgentBenchmarkConfig.cs` | Config metadata with ConfigurationId |
| 1.4 | `Models/BenchmarkExecutionInfo.cs` | Execution metadata |
| 1.5 | `Models/BenchmarkManifest.cs` | Manifest models |
| 1.6 | `Models/RadarChartData.cs` | Pentagon visualization data |
| 1.7 | `Models/BaselineComparison.cs` | Comparison result |
| 1.8 | `Models/CategoryScoreEntry.cs` | Per-category score with optional stochastic data |
| 1.9 | `Reporting/PentagonConsolidator.cs` | 8→5 dimension mapping |
| 1.10 | `Reporting/BaselineExtensions.cs` | `.ToBaseline()` extension |
| 1.11 | `Reporting/IBaselineStore.cs` | Persistence interface |
| 1.12 | `Reporting/JsonFileBaselineStore.cs` | JSON implementation + manifest + template copy |
| 1.13 | `Reporting/IBaselineComparer.cs` | Comparison interface |
| 1.14 | `Reporting/BaselineComparer.cs` | Comparison logic |
| 1.15 | `Report/report.html` | Embedded HTML template (from prototype) |
| 1.16 | `Report/archetypes.json` | 6 memory-architecture archetypes |
| 1.17 | `Extensions/AgentEvalMemoryServiceCollectionExtensions.cs` | Add `AddAgentEvalMemoryReporting()` |
| 1.18 | Tests | Store, comparer, consolidator, bridge, extensions |

### Phase 2: Scenario Depth + Optional Stochastic Mode

| Task | Files |
|------|-------|
| 2.1 | `Evaluators/MemoryBenchmarkRunner.cs` — multi-scenario per category |
| 2.2 | `Models/MemoryBenchmark.cs` — depth derivation from preset |
| 2.3 | `BenchmarkOptions` — optional `Runs` parameter for stochastic mode |
| 2.4 | Tests |

### Phase 3: Polish

| Task | Description |
|------|-------------|
| 3.1 | `MemoryBenchmark.Custom(...)` builder |
| 3.2 | Cross-agent `index.html` |
| 3.3 | Archetype comparison in HTML report |

---

## 20. Design Decisions Log

| # | Decision | Choice | Rationale |
|---|----------|--------|-----------|
| D1 | Chart shape | **Pentagon (5 axes)** | Research: 5-7 optimal. Pentagon maximally readable for shape comparison. 8-category detail in KPI scorecard. |
| D2 | Storage format | JSON files | Simple, portable, git-friendly. `IBaselineStore` for future extensibility. |
| D3 | Export strategy | **Bridge to existing `IResultExporter` pipeline** | 6 exporters already exist. Don't duplicate. `MemoryBenchmarkResult.ToEvaluationReport()` unlocks all. |
| D4 | Archetypes | **Memory-architecture based** (not business domain) | Architecture predicts benchmark shape. Domain doesn't. Research-backed. |
| D5 | Stochastic evaluation | **Optional** (default: single run) | CI needs speed. Config comparison benefits from 3-5 runs. Not mandatory. |
| D6 | Timeline vs comparison routing | **ConfigurationId** (deterministic hash) | Same config → timeline. Different config → radar overlay. Automatic, no user decision needed. |
| D7 | Framework adapters | Completed (moved to Abstractions) | Done. All tests pass. |
| D8 | Scenario depth | Within existing presets (Quick=1, Standard=2, Full=3+) | Natural extension, no new presets. |
| D9 | Noise+Compression merge | **Merge into "Resilience"** (pentagon) | User cares "is info lost?", not the mechanism. Detailed breakdown in KPI scorecard. |
| D10 | Dimension pre-computation | Stored in baseline JSON | Old baselines survive formula changes. |
| D11 | HTML report | Embedded resource, separate from export pipeline | Export = point-in-time CI artifacts. HTML report = interactive historical exploration. |

---

## Research Sources

- [MemoryAgentBench: Evaluating Memory Structure in LLM Agents](https://arxiv.org/pdf/2602.11243) — cognitive science-grounded memory evaluation
- [Memory for Autonomous LLM Agents: Mechanisms, Evaluation, and Emerging Frontiers](https://arxiv.org/html/2603.07670) — 5 memory mechanism families
- [MemBench: Comprehensive Evaluation on Memory of LLM-based Agents](https://aclanthology.org/2025.findings-acl.989/) — multi-aspect memory benchmarking
- [Evaluation and Benchmarking of LLM Agents: A Survey (KDD 2025)](https://arxiv.org/html/2507.21504v1) — stochastic evaluation, pass@k metrics
- [AMA-Bench: Evaluating Long-Horizon Memory for Agentic Applications](https://arxiv.org/html/2602.22769v1) — long-term memory evaluation
- [Radar Chart Best Practices (Highcharts)](https://www.highcharts.com/blog/tutorials/radar-chart-explained-when-they-work-when-they-fail-and-how-to-use-them-right/) — 5-8 axes recommendation
- [The Radar Chart and Its Caveats (Data-to-Viz)](https://www.data-to-viz.com/caveat/spider.html) — readability research
- [Origami Plot: Improving Radar Charts (PMC)](https://pmc.ncbi.nlm.nih.gov/articles/PMC10599795/) — >7 variable challenges
- [Agent Memory Paper List (GitHub)](https://github.com/Shichun-Liu/Agent-Memory-Paper-List) — comprehensive survey

---

*This document is the single source of truth for the reporting and benchmark module. All decisions are reasoned. All models are defined. All file locations are specified. All existing infrastructure is identified for reuse. Ready to implement.*