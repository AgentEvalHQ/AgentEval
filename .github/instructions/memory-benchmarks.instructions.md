---
applyTo: "src/AgentEval.Memory/**,tests/AgentEval.Memory.Tests/**,samples/AgentEval.Samples/MemoryEvaluation/**"
description: Guidelines for working with AgentEval Memory module — benchmarks, reporting, HTML reports, and LongMemEval integration
---

# AgentEval Memory & Benchmarks — Agent Instructions

## Module Overview

The `AgentEval.Memory` module provides comprehensive memory evaluation for AI agents:
- Native benchmarks (12 scenario types across 5 presets)
- External benchmarks (LongMemEval — ICLR 2025, 500 questions, 6 types)
- Baseline persistence with JSON file storage
- Interactive HTML reports with pentagon/radar charts, timeline, and comparison
- History injection modes (Auto, Structured, TextBlob)

## Architecture

```
src/AgentEval.Memory/
├── Abstractions/       Interfaces for memory operations
├── Assertions/         Memory-specific fluent assertion API
├── Data/               Embedded datasets, LongMemEval data files
├── DataLoading/        Scenario & corpus loaders, exporters
├── Engine/             Core: MemoryTestRunner, MemoryJudge
├── Evaluators/         MemoryBenchmarkRunner, ReachBack, Reducer, CrossSession evaluators
├── Extensions/         DI registration, helper methods
├── External/           External benchmarks (LongMemEval), interfaces
├── Metrics/            Memory-specific evaluation metrics
├── Models/             Core data: Benchmark, Result, Baseline, Config
├── Report/             HTML report template, pentagon mapper
├── Reporting/          Baseline store, comparer, output formatting
├── Scenarios/          Scenario providers: Memory, Chatty, Temporal, CrossSession
└── Temporal/           Temporal memory runner & scenarios
```

## CRITICAL: Never Interrupt Running Benchmarks

**NEVER kill, cancel, or interrupt a benchmark that is in progress.** LLM benchmarks make hundreds of API calls and can take 10-100+ minutes to complete. Killing a benchmark wastes all progress, API costs, and time.

- If you need to add console output, logging, or cosmetic changes — **wait for the benchmark to finish first**, then make changes and re-run.
- If a benchmark appears to produce no output, it may still be running. Check the terminal for signs of life (CPU usage, network activity). The runner logs progress via `Console.WriteLine` after each question.
- If you want to add progress reporting to a runner that lacks it, wait for the current run to complete.
- The only valid reason to interrupt is an explicit user request or an unrecoverable crash.

## Benchmark Types

### Native Benchmarks (MemoryBenchmarkRunner)

Native benchmarks use synthetic scenarios with controlled facts, noise, and queries to measure specific memory capabilities.

| Preset | Categories | Scenarios/Cat | Duration | Use Case |
|--------|-----------|:-------------:|----------|----------|
| **Quick** | 3 | 1 | ~2min | CI pipelines, fast feedback |
| **Standard** | 8 | 2 | ~5min | Staging, development |
| **Full** | 12 | 3+ | ~10min | Pre-release, comprehensive |
| **Diagnostic** | 11 | Multiple | ~15min | Deep analysis, context limits |
| **Overflow** | 8 | With overflow | ~10min | Reducer stress testing |

### 12 Native Scenario Types

| # | Type | Tests |
|---|------|-------|
| 1 | **BasicRetention** | Direct fact recall |
| 2 | **TemporalReasoning** | Time-sensitive fact ordering |
| 3 | **NoiseResilience** | Recall through distracting conversation |
| 4 | **ReachBackDepth** | Recall through noise layers (degradation curve) |
| 5 | **FactUpdateHandling** | Handling corrected/updated facts |
| 6 | **MultiTopic** | Memory across different conversation topics |
| 7 | **CrossSession** | Memory persistence after session reset |
| 8 | **ReducerFidelity** | Information retention after context compression |
| 9 | **Abstention** | Correctly refuses to answer unanswerable questions |
| 10 | **ConflictResolution** | Detecting/resolving implicit contradictions |
| 11 | **MultiSessionReasoning** | Synthesizing facts across session boundaries |
| 12 | **PreferenceExtraction** | Inferring user preferences from behavioral signals |

### External Benchmarks (LongMemEvalBenchmarkRunner)

LongMemEval is an external academic benchmark (ICLR 2025) with 500 questions across 6 types, using real conversation histories (~115K tokens per question in S mode).

**6 LongMemEval Question Types:**

| Type | Count | Description | Judge |
|------|:-----:|-------------|-------|
| single-session-user | 70 | Recall facts the user stated | Strict |
| single-session-assistant | 56 | Recall the assistant's own responses | Strict |
| single-session-preference | 30 | Infer user preferences from context | Lenient |
| multi-session | 133 | Synthesize info across 2-6 sessions | Strict |
| temporal-reasoning | 133 | Reason about time/order/durations | Tolerant (±1) |
| knowledge-update | 78 | Return latest version of updated facts | Tolerant |
| + 30 abstention questions (embedded across types, identified by `_abs` suffix) |

**3 Dataset Modes:**

| Mode | Sessions/Q | Tokens/Q | Purpose |
|------|:----------:|:--------:|---------|
| Oracle | 1-6 (evidence only) | 2-10K | Upper bound, no retrieval needed |
| S (Small) | ~48 avg | ~115K | Fits 128K context window |
| M (Medium) | ~500 | >128K | Requires retrieval/RAG |

**Published Reference Baselines (S mode, 500q):**
- GPT-4o direct: 60.6% (Fig 3b — plain question, no intermediate reasoning)
- GPT-4o + CoN: 64.0% (Chain-of-Note: extract notes first, then reason)
- ChatGPT online: 57.7% (commercial memory system, 97q with 3-6 sessions, human eval — NOT comparable to long-context reading)
- Llama-3.1-8B direct: 45.4%
- Llama-3.1-70B direct: 33.4%

## History Injection Modes

```csharp
public enum HistoryInjectionMode
{
    Auto = 0,                  // Use Structured if IHistoryInjectableAgent, else TextBlob
    StructuredChatHistory,     // Fast: inject as ChatMessage pairs (0 extra LLM calls)
    TextBlob                   // Visible to middleware: prepend as text blob in user message
}
```

- **Auto** (default): Picks Structured if the agent implements `IHistoryInjectableAgent`, falls back to TextBlob otherwise.
- **StructuredChatHistory**: Injects history as `ChatMessage` pairs directly into agent state. Fastest (0 LLM formatting calls), but conversation history is invisible to MAF `AIContextProviders`.
- **TextBlob**: Prepends all conversation history as a formatted text block in the user message. Matches the LongMemEval paper's evaluation format. History content is visible to MAF middleware/context providers.

**When matching the LongMemEval paper**, always use `TextBlob` mode — it matches the paper's "full-history-session" long-context reading approach.

## Running Benchmarks

### Native Benchmark
```csharp
var runner = MemoryBenchmarkRunner.Create(chatClient);
var benchmark = MemoryBenchmark.Quick;  // or .Standard, .Full, .Diagnostic, .Overflow

var result = await runner.RunBenchmarkAsync(agent, benchmark, progress: null, ct);
// result.OverallScore, result.Grade, result.Stars, result.Passed
```

### LongMemEval Benchmark
```csharp
var runner = LongMemEvalBenchmarkRunner.Create(chatClient, datasetPath);

var options = new ExternalBenchmarkOptions
{
    MaxQuestions = 100,           // null = all 500
    StratifiedSampling = true,   // proportional across types
    RandomSeed = 42,             // reproducible
    DatasetMode = "S",           // ~115K tokens per question
    HistoryInjectionMode = HistoryInjectionMode.TextBlob,
    PreserveSessionBoundaries = true,
    IncludeTimestamps = true,
};

var result = await runner.RunAsync(agent, config, options);
// result.OverallAccuracy, result.TaskAveragedAccuracy, result.PerTypeResults
```

### Running via Samples
```powershell
dotnet run --project samples/AgentEval.Samples
# Select from menu: Group G (Memory Evaluation), items G1-G10
```

### Timing Expectations
- **Quick native** (~3 categories): ~2 minutes
- **Standard native** (~8 categories): ~5 minutes
- **Full native** (~12 categories): ~10 minutes
- **LongMemEval 50q**: ~10 minutes
- **LongMemEval 100q**: ~20 minutes
- **LongMemEval 500q (full)**: ~100 minutes

## Baseline Storage & File Structure

### Convention
```
.agenteval/benchmarks/{agent-slug}/
├── manifest.json              # Index of all baselines for this agent
├── report.html                # Interactive HTML report (auto-copied from embedded resource)
├── archetypes.json            # Reference/archetype baselines (optional)
└── baselines/
    ├── 2026-04-17_name-1.json
    ├── 2026-04-18_name-2.json
    └── ...
```

Each agent gets its own directory under `.agenteval/benchmarks/`. The agent slug is derived from the `AgentBenchmarkConfig.AgentName` (e.g., `LongMemEval-gpt-4o` → `longmemeval-gpt-4o`).

### Saving Baselines
```csharp
var baseline = result.ToBaseline(
    "Descriptive Name",
    config,
    tags: ["longmemeval", "paper-repro", "gpt-4o"],
    pentagonMapperFull: LongMemEvalPentagonMapper.Consolidate);

var store = new JsonFileBaselineStore();
await store.SaveAsync(baseline);
```

### Manifest Structure
The `manifest.json` file indexes all baselines and is auto-updated on each save:
```json
{
  "schema_version": "1.0",
  "generated_at": "2026-04-17T...",
  "generated_by": "AgentEval.Memory",
  "agent": { "name": "LongMemEval-gpt-4o" },
  "benchmarks": [{
    "benchmark_id": "memory-full",
    "preset": "Full",
    "categories": ["Basic Retention", ...],
    "baselines": [{
      "id": "bl-abc123",
      "file": "baselines/2026-04-17_name.json",
      "name": "My Baseline",
      "overall_score": 66.0,
      "grade": "D"
    }]
  }]
}
```

### JSON Serialization
- Uses `JsonNamingPolicy.SnakeCaseLower` (C# `AgentName` → JSON `agent_name`)
- Enums as CamelCase strings
- Nulls omitted (`DefaultIgnoreCondition.WhenWritingNull`)

## HTML Report System

### Overview
The report is a single-file HTML page (`report.html`) that loads data from `manifest.json` and baseline JSON files via JavaScript `fetch()`. It requires an HTTP server — it will NOT work via `file://` URLs.

### Serving the Report
**Always serve from the `benchmarks/` parent directory** so the report can discover sibling agent directories:

```powershell
python -m http.server 9090 --directory ".agenteval/benchmarks"
# Then open: http://localhost:9090/{agent-slug}/report.html
```

**Why the parent directory?** The report's `discoverReports()` function fetches `../` (the parent) to find all sibling agent directories. If you serve from a specific agent directory, the report switcher dropdown won't work and you'll only see one agent.

### Report Sections
1. **Overview** — Grade hero (score %, grade letter, stars), dimension KPIs, category KPIs, reference scores for LongMemEval
2. **Pentagon** — Radar chart (Chart.js) with multi-baseline overlay, category bar chart comparison
3. **Trends** — Timeline of overall score across baselines, per-dimension trend charts
4. **Comparison** — Side-by-side A vs B baseline comparison with waterfall delta chart

### Report Switcher
The report auto-discovers sibling agent directories by parsing the Python HTTP server's directory listing. A dropdown appears at the top when multiple agents are found, allowing navigation between different agent reports.

### How Data Flows
1. `report.html` loads → fetches `./manifest.json`
2. Reads all baseline entries from the manifest
3. Fetches each `baselines/*.json` file
4. Populates charts, KPIs, and comparison tools
5. `discoverReports()` fetches `../` to find sibling agent dirs with their own `manifest.json`

### Pentagon Dimensions

**Native benchmarks (12 categories → 5 dimensions):**
- **Recall** = avg(BasicRetention, ReachBackDepth)
- **Resilience** = avg(NoiseResilience, ReducerFidelity)
- **Temporal** = avg(TemporalReasoning, FactUpdateHandling, ConflictResolution)
- **Persistence** = avg(CrossSession, MultiSessionReasoning)
- **Organization** = avg(MultiTopic, Abstention, PreferenceExtraction)

**LongMemEval (6 types → 5 dimensions, from the paper's Table 1):**
- **Information Extraction** = avg(single-session-user, single-session-assistant, single-session-preference)
- **Multi-Session** = multi-session
- **Temporal** = temporal-reasoning
- **Knowledge Update** = knowledge-update
- **Abstention** = cross-type, all `_abs` suffix questions

The report HTML recognizes LongMemEval baselines and switches to external dimension info (`EXT_DIM_INFO`, `EXT_CAT_INFO`) automatically.

## Key Interfaces

```csharp
// Core agent contract — required for all benchmarks
public interface IEvaluableAgent
{
    string Name { get; }
    Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default);
}

// Fast history injection (0 LLM calls) — used by StructuredChatHistory mode
public interface IHistoryInjectableAgent
{
    void InjectConversationHistory(
        IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns);
}

// Session reset — used by CrossSession evaluator
public interface ISessionResettableAgent
{
    Task ResetSessionAsync(CancellationToken ct = default);
}
```

## LongMemEval Judge System

Each question type uses a tailored judge prompt:

| Method | Used For | Behavior |
|--------|----------|----------|
| `Standard()` | SSU, SSA, multi-session | Strict: must contain correct answer |
| `Preference()` | SSP | Lenient: utilizes personal info correctly |
| `Temporal()` | temporal-reasoning | Tolerant: ±1 day/week/month allowed |
| `KnowledgeUpdate()` | knowledge-update | Tolerant: old + updated both OK |
| `Abstention()` | `_abs` suffix | Correct if identifies as unanswerable |

Binary scoring only: 0 (wrong) or 100 (correct). No partial credit.

## Error Handling in Runners

The `LongMemEvalBenchmarkRunner` has built-in error handling:
- **Azure content filter** (HTTP 400): Question marked as incorrect with `[CONTENT_FILTER]`, logs warning, continues to next question
- **Other exceptions**: Marked as incorrect with `[ERROR: message]`, continues
- **Only `OperationCanceledException`** propagates (user cancellation)
- **Console progress** is printed after each question: `[N/Total] type ✓/✗/⚠ (Xs)`

## Samples (Group G: Memory Evaluation)

| # | Sample | Description |
|---|--------|-------------|
| G1 | `01_MemoryBasics.cs` | Simple fact retention test |
| G2 | `02_MemoryBenchmarkDemo.cs` | Quick/Standard/Full benchmark tiers |
| G3 | `03_MemoryScenariosDemo.cs` | Using scenario factories |
| G4 | `04_MemoryDI.cs` | Dependency injection setup |
| G5 | `05_MemoryCrossSession.cs` | Session reset & persistence |
| G6 | `06_MemoryBenchmarkReporting.cs` | Baseline save & retrieval |
| G7 | `07_LongMemEvalBenchmark.cs` | LongMemEval benchmark run |
| G8 | `08_RunSingleBenchmark.cs` | Running one category in isolation |
| G9 | `09_MemoryAIContextProvider.cs` | MAF AIContextProvider integration |
| G10 | `10_LongMemEvalBaselineRepro.cs` | Paper baseline reproduction (gpt-4o, TextBlob) |

## LongMemEval Dataset Setup

The S-mode dataset is NOT included in the repo (264 MB). Download from HuggingFace:

```
https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/tree/main
```

Place at: `src/AgentEval.Memory/Data/longmemeval/longmemeval_s_cleaned.json`

The Oracle dataset (14.7 MB) may be embedded or smaller.

## Common Pitfalls

1. **Serving report from wrong directory** — Serve from `.agenteval/benchmarks/` (parent), NOT from a specific agent directory. Otherwise the report switcher won't discover sibling agents.

2. **Opening report via file://** — Will fail because JavaScript `fetch()` can't load `manifest.json` over `file://` protocol. Always use an HTTP server.

3. **Using wrong injection mode for paper comparison** — LongMemEval paper uses long-context reading (entire history in the prompt). Use `HistoryInjectionMode.TextBlob` to match. `StructuredChatHistory` is faster but doesn't match the paper's methodology.

4. **Confusing paper baselines** — The 57.7% figure is ChatGPT's commercial memory system (97q, 3-6 sessions, human eval). The comparable long-context baselines are 60.6% (direct) and 64.0% (CoN).

5. **Content filter crashes** — Azure OpenAI may reject some ~115K token prompts. The runner catches these and continues. Expect 1-3% of questions to hit content filters on S-mode datasets.

6. **Stratified vs full runs** — 100q stratified gives a reasonable estimate but can differ ±5% from the full 500q run due to sampling variance. For definitive results, use `MaxQuestions = null`.

7. **Not setting RandomSeed** — Without a seed, each run samples different questions. Set `RandomSeed = 42` (or any fixed value) for reproducible comparisons.
