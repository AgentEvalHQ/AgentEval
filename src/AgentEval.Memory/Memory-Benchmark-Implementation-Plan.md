# AgentEval Memory Benchmark — Implementation Plan

**Date:** March 21, 2026
**Reference:** Memory-Benchmark-Assessment-and-Roadmap.md (decisions), Memory-Benchmark-Reporting-Proposals.md (visual design), agenteval-memory-benchmark-report.html (prototype)
**Starting Point:** 42 .cs files in AgentEval.Memory, 0 reporting/baseline classes, 0 embedded resources, all evaluators working, 348 unit tests passing
**Goal:** Implement the complete reporting, baseline persistence, export bridge, and HTML report system (Roadmap Phase 1)

---

## Implementation Progress Tracker

| Task | Description | % Done | Reviewed | Notes |
|------|-------------|--------|----------|-------|
| **Task 0** | Housekeeping — delete stray Samples project | 100% | ✅ | Deleted. Build passes. |
| **Task 1.1** | CategoryScoreEntry + StochasticData models | 100% | ✅ | Created. Build passes 0 errors 0 warnings. |
| **Task 1.2** | AgentBenchmarkConfig (with ConfigurationId) | 100% | ✅ | Created with SHA256 ConfigurationId. |
| **Task 1.3** | BenchmarkExecutionInfo model | 100% | ✅ | Created. |
| **Task 1.4** | MemoryBaseline model | 100% | ✅ | Created. Uses concrete Dictionary/List for JSON compat. |
| **Task 1.5** | RadarChartData + RadarChartSeries models | 100% | ✅ | Created. |
| **Task 1.6** | BaselineComparison + DimensionComparison models | 100% | ✅ | Created. BestScore/BestBaselineId handle empty. |
| **Task 1.7** | BenchmarkManifest (4 classes) model | 100% | ✅ | Created. Uses concrete List for JSON compat. |
| **Task 1.8** | Model unit tests (ConfigId, JSON round-trip, snake_case) | 100% | ✅ | 12 tests, all pass across 3 TFMs. |
| **CP1** | Checkpoint 1 — Build + Test | 100% | ✅ | 180 tests/TFM (168 existing + 12 new). 0 errors, 0 warnings. |
| **Task 2.1** | PentagonConsolidator | 100% | ✅ | 6 tests pass. Handles skipped, partial, empty gracefully. |
| **Task 2.2** | BaselineExtensions (.ToBaseline()) | 100% | ✅ | 10 tests pass. 5-grade system verified. Added InternalsVisibleTo for test project. |
| **CP2** | Checkpoint 2 — Build + Test | 100% | ✅ | 196 tests/TFM. 0 errors. .csproj updated with InternalsVisibleTo. |
| **Task 3.1** | MemoryBenchmarkReportExtensions (.ToEvaluationReport()) | 100% | ✅ | 8 tests pass. Bridges to all 6 existing exporters. |
| **CP3** | Checkpoint 3 — Build + Test | 100% | ✅ | 204 tests/TFM. 0 errors. |
| **Task 4.1** | Enrich MemoryBenchmarkRunner (scenario depth) | 100% | ✅ | All 8 categories enriched. Backward-compat 9-param ctor preserved. Samples still build. |
| **Task 4.2** | Benchmark runner depth tests | 100% | ✅ | 8 new tests added to existing file. Quick/Standard/Full all verified. |
| **CP4** | Checkpoint 4 — Build + Test | 100% | ✅ | 211 tests/TFM. 0 errors. Samples build. Backward compat confirmed. |
| **Task 5.1** | report.html (pentagon + dynamic manifest loading) | 100% | ✅ | Dynamic manifest fetch, pentagon 5-axis, config cards, comparer. |
| **Task 5.2** | archetypes.json (6 memory-architecture archetypes) | 100% | ✅ | 6 archetypes with 5 pentagon axis scores each. |
| **Task 5.3** | .csproj EmbeddedResource config | 100% | ✅ | Added EmbeddedResource items + InternalsVisibleTo for tests. |
| **Task 5.4** | Embedded resource accessibility tests | 100% | ✅ | 3 tests: report.html exists, archetypes.json exists, archetypes has 6 entries with 5 axes. |
| **CP5** | Checkpoint 5 — Build + Test | 100% | ✅ | 214 tests/TFM. 0 errors. Resources embedded correctly. |
| **Task 6.1** | IBaselineStore interface | 100% | ✅ | Created. |
| **Task 6.2** | IBaselineComparer interface | 100% | ✅ | Created. |
| **Task 6.3** | MemoryReportingOptions | 100% | ✅ | Created with defaults. |
| **Task 6.4** | JsonFileBaselineStore implementation | 100% | ✅ | 16 tests. Fixed ResolveRootPath lowercase bug. Auto manifest + template copy. |
| **Task 6.5** | BaselineComparer implementation | 100% | ✅ | 6 tests. Handles missing dimensions. Pentagon axes in RadarChart. |
| **CP6** | Checkpoint 6 — Build + Full Memory Test Suite | 100% | ✅ | 236 tests/TFM. 0 errors. Store round-trips, manifest rebuilds, comparer works. |
| **Task 7.1** | DI registration (AddAgentEvalMemoryReporting) | 100% | ✅ | 5 new DI tests. AddAgentEvalMemory() now includes reporting. |
| **CP7** | Checkpoint 7 — Build + Test | 100% | ✅ | 241 tests/TFM. 0 errors. |
| **Task 8.1** | End-to-end integration tests | 100% | ✅ | 7 tests: round-trip, configId routing, comparison, export bridge, manifest, file structure. |
| **CP8** | Checkpoint 8 — Full solution build + test | 100% | ✅ | 248 tests/TFM. Full solution builds. 0 errors. |
| **Task 9.1** | Sample: 06_MemoryBenchmarkReporting.cs | 100% | ✅ | 2 configs, comparison table, export bridge demo, report path. |
| **Task 9.2** | Program.cs — register in Group G | 100% | ✅ | 6th entry in Memory Evaluation group. |
| **CP9** | Checkpoint 9 — Build + manual sample test | 100% | ✅ | Sample compiles. Run with `dotnet run -- G6` (needs Azure creds). |
| **Task 10.x** | Final verification + review checklist | 100% | ✅ | 278 tests/TFM. 57 .cs files. Full solution builds. Two review passes completed. |

---

## Implementation Summary

### What Was Built

| Category | Files | Details |
|----------|-------|---------|
| **Data Models** | 7 new .cs files in `Models/` | `CategoryScoreEntry` + `StochasticData`, `AgentBenchmarkConfig` (with `ConfigurationId`), `BenchmarkExecutionInfo`, `MemoryBaseline`, `RadarChartData` + `RadarChartSeries`, `BaselineComparison` + `DimensionComparison`, `BenchmarkManifest` (4 classes) |
| **Reporting Logic** | 7 new .cs files in `Reporting/` | `PentagonConsolidator` (8→5 dimension mapping), `BaselineExtensions` (`.ToBaseline()`), `IBaselineStore`, `IBaselineComparer`, `JsonFileBaselineStore` (persistence + manifest + template copy), `BaselineComparer`, `MemoryReportingOptions` |
| **Export Bridge** | 1 new .cs file in `Extensions/` | `MemoryBenchmarkReportExtensions` (`.ToEvaluationReport()` — bridges to all 6 existing exporters) |
| **Scenario Depth** | 1 modified .cs file in `Evaluators/` | `MemoryBenchmarkRunner` — Quick=1, Standard=2, Full=3+ scenarios per category using existing scenario library |
| **Embedded Resources** | 2 files in `Report/` | `report.html` (interactive Chart.js dashboard with pentagon, timeline, comparer), `archetypes.json` (6 memory-architecture reference profiles) |
| **DI Registration** | 1 modified .cs file in `Extensions/` | `AddAgentEvalMemoryReporting()` added; `AddAgentEvalMemory()` updated to include it |
| **Sample** | 1 new + 1 modified in `samples/` | `06_MemoryBenchmarkReporting.cs` (2 configs, comparison, export demo) + `Program.cs` registration |
| **Housekeeping** | 1 deleted folder | Removed orphaned `src/AgentEval.Memory/Samples/` |

### Numbers

| Metric | Before | After | Delta |
|--------|--------|-------|-------|
| .cs files in AgentEval.Memory | 42 | 57 | +15 |
| Tests per TFM | 168 | 278 | +110 |
| Embedded resources | 0 | 2 | +2 |
| Test files | 10 | 16 | +6 new + 2 modified |

### Key Implementation Decisions Made During Coding

1. **Concrete types for JSON deserialization:** `MemoryBaseline` and `BenchmarkManifest` use `Dictionary<>` and `List<>` instead of `IReadOnlyDictionary`/`IReadOnlyList` — `System.Text.Json` cannot deserialize to interface types without custom converters. Verified by round-trip tests in Phase 1.

2. **Backward-compatible constructor chain:** `MemoryBenchmarkRunner` gained `ICrossSessionScenarios` as a 10th parameter. The original 9-parameter constructor delegates to the new one with `new CrossSessionScenarios()` as default. Zero breaking changes to existing tests, samples, or DI.

3. **Fixed `ResolveRootPath` bug:** The `SlugifyRegex` (`[^a-z0-9]+`) was applied without lowercasing the input, stripping uppercase letters entirely. Fixed to lowercase first. Caught by tests.

4. **Added `InternalsVisibleTo`:** `.csproj` was missing the entry for `AgentEval.Memory.Tests`, needed for testing `internal` methods like `ComputeGrade`.

5. **Grade system alignment:** `BaselineExtensions.ComputeGrade()` uses the exact same 5-grade thresholds (A/B/C/D/F) as `MemoryBenchmarkResult.Grade`. Test case 6 in `BaselineExtensionsTests` explicitly verifies `score 85 → "B"` (not "A-").

---

## Implementation Review (Post-Completion Code Review)

A thorough code review was performed against DRY, CLEAN, SOLID principles across all 15 new source files, 2 modified files, and the sample. Issues found and fixed:

### Issues Fixed

| # | Severity | File | Issue | Fix Applied |
|---|----------|------|-------|-------------|
| 1 | **CRITICAL** | `JsonFileBaselineStore.cs` | Swallowed ALL exceptions with `catch { }` (4 instances) — hid `OutOfMemoryException`, corrupt file errors, making debugging impossible | Extracted `TryDeserializeBaseline()` helper that catches only `JsonException`. All 4 catch blocks replaced with calls to this method. Clean, single-responsibility error handling. |
| 2 | **CRITICAL** | `BaselineComparer.cs:36` | Unsafe null-forgiving operator `baselines.MaxBy(...)!` — would crash on edge cases | Replaced with `?? throw new InvalidOperationException(...)` — explicit, debuggable failure |
| 3 | **HIGH** | `JsonFileBaselineStore.cs` | DRY violation — slugification logic duplicated in `ResolveRootPath()` and `Slugify()` (identical regex + lowercase + trim) | Extracted `SanitizeName()` private helper used by both methods |
| 4 | **HIGH** | `JsonFileBaselineStore.cs` | `LoadAsync` and `DeleteAsync` had duplicated scan-and-deserialize loops | Extracted `FindBaselineInDirectoryAsync()` for `LoadAsync`; `DeleteAsync` kept inline due to delete side-effect |
| 5 | **MEDIUM** | `BaselineExtensions.cs` | `FindRecommendation()` could NPE on empty recommendations | Added early return `if (recommendations.Count == 0) return null` |
| 6 | **LOW** | `JsonFileBaselineStore.cs:231` | Hardcoded `"AgentEval.Memory"` string for generator | Changed to `typeof(...).Assembly.GetName().Version` for dynamic version |

### Issues Acknowledged (Not Fixed — Acceptable for Current Scope)

| # | Severity | File | Issue | Rationale for Deferral |
|---|----------|------|-------|----------------------|
| A | HIGH | `JsonFileBaselineStore` | SRP violation — class handles persistence + manifest generation + resource copying + path sanitization | Valid concern but extracting 3 interfaces adds complexity disproportionate to benefit at this stage. The class is well-tested (16 tests). Refactoring is a Phase 3 polish item. |
| B | MEDIUM | `DimensionComparison.BestBaselineId` | Returns empty string when Scores is empty | Changing to `string?` would cascade to comparison template rendering. Current behavior is safe — empty scores only happens if zero baselines are compared, which is caught by the `ArgumentException` guard in `BaselineComparer`. |
| C | MEDIUM | Sample `06_MemoryBenchmarkReporting.cs` | Manual dependency construction instead of DI | Intentional — matches the pattern of all other samples in the project (02, 03, 04, 05). Showing DI would require a full `ServiceCollection` setup that obscures the benchmark reporting concepts being demonstrated. |
| D | LOW | `PentagonConsolidator` | Axis mapping is hardcoded (3 calls to `AddAveraged`) | With 5 axes this is clear and readable. A data-driven approach (dictionary of mappings) adds abstraction for no current benefit. |

### Second Review: Test Quality & Coverage (Post-Fix)

A second review focused specifically on test quality — assertion strength, edge cases, boundary values, and missing negative tests. 30 additional tests added.

| # | Test File | Issue | Fix Applied |
|---|-----------|-------|-------------|
| R1 | `ReportingModelTests` | ConfigurationId hex format not validated | Added `Assert.Matches("^[A-F0-9]{12}$", id)` + test for different agent names |
| R2 | `ReportingModelTests` | JSON round-trip only checked counts, not content | Added `PreservesCategoryContent` test verifying actual scores, grades, dimension values |
| R3 | `ReportingModelTests` | No test for null field omission in JSON | Added `OmitsNullFields` test verifying `description` absent when null |
| R4 | `BaselineExtensionsTests` | Floating-point boundary not tested (89.999 vs 90) | Added `[Theory]` with 12 boundary values including negatives and out-of-range |
| R5 | `BaselineExtensionsTests` | No test for empty recommendations | Added `EmptyRecommendations_NoRecommendationInCategory` test |
| R6 | `BaselineComparerTests` | No test for empty list throwing `ArgumentException` | Added `EmptyList_ThrowsArgumentException` and null test |
| R7 | `BaselineComparerTests` | Tie-breaking behavior not tested | Added `TiedScores_ReturnsFirstBaseline` test |
| R8 | `BaselineComparerTests` | Missing dimensions defaulting to 0 not verified in radar values | Added `MissingDimensionsDefaultToZero` test checking actual series values |
| R9 | `MemoryBenchmarkReportExtensionsTests` | Boundary score 70 (pass/fail edge) not tested | Added `BoundaryScore70_CountsAsPassed` and `Score69_CountsAsFailed` |
| R10 | `MemoryBenchmarkReportExtensionsTests` | Multi-word ScenarioType metric key not tested | Added `MultiWordScenarioType_ProducesCorrectMetricKey` for `ReachBackDepth` |
| R11 | `MemoryBenchmarkReportExtensionsTests` | No test for providing only agentName (not both) | Added `OnlyAgentName_CreatesAgentInfo` |
| R12 | `MemoryBenchmarkReportExtensionsTests` | No test for all categories skipped | Added `AllSkipped_ZeroPassedZeroFailed` |
| R13 | `PentagonConsolidatorTests` | Both paired categories skipped not tested | Added `BothPairedCategoriesSkipped_AxisOmitted` |
| R14 | `PentagonConsolidatorTests` | Extreme values (0, 100) not tested | Added `ExtremeValues_PassedThrough` verifying avg(0, 100) = 50 |
| R15 | `JsonFileBaselineStoreTests` | Corrupt file recovery not tested | Added `ListAsync_SkipsCorruptFiles` writing invalid JSON |
| R16 | `JsonFileBaselineStoreTests` | Slugify edge cases incomplete | Added `TruncatesLongNames` and `SpecialCharsOnly_ReturnsUnnamed` |

### Verification

After all fixes: `dotnet build` — 0 errors, 0 warnings. `dotnet test` — **278 tests/TFM** (168 original + 110 new), all passing across net8.0, net9.0, net10.0.

---

### Scope: What This Plan Covers vs. What It Defers

| In Scope (This Plan) | Deferred (Future) |
|----------------------|-------------------|
| All data models (MemoryBaseline, AgentBenchmarkConfig, etc.) | Stochastic evaluation (`BenchmarkOptions { Runs = 5 }`) |
| PentagonConsolidator (8→5 dimensions) | Multi-run aggregation (mean, stddev, min, max) |
| Export bridge (`.ToEvaluationReport()`) | `BenchmarkOptions` class |
| IBaselineStore + JsonFileBaselineStore | `MemoryBenchmark.Custom(...)` builder |
| IBaselineComparer + BaselineComparer | Cross-agent `index.html` |
| HTML report template (embedded) | Archetype comparison in HTML report |
| 6 memory-architecture archetypes | CLI: `dotnet agenteval manifest rebuild` |
| DI registration | |
| Sample application | |
| **Scenario depth (benchmark enrichment)** | |
| **Modifications to `MemoryBenchmarkRunner.cs`** | |

### Why Scenario Depth Is In This Plan (Not Deferred)

The original roadmap split reporting (Phase 1) from scenario depth (Phase 2). After reviewing the actual benchmark code, **this split is wrong.** Here's why:

**The current benchmarks are thin.** Each category runs 1 scenario with minimal data:
- NoiseResilience: 2 facts buried in noise (should be 4-6)
- ReachBackDepth: 1 fact at 3 depths (should be 2-3 facts at 5 depths)
- CrossSession: 3 facts, 1 reset (should test incremental learning + context switching)
- ReducerFidelity: 3 facts + 20 noise (should be 5+ facts + 40 noise)
- TemporalReasoning: 4 events, sequence only (should test time-point queries + causal reasoning)

The scenario library already has 15 unused scenarios that address every one of these gaps. The benchmark just doesn't wire them up. Shipping a beautiful HTML report that displays statistically unreliable data is worse than not having the report — it gives false confidence.

**The effort is small.** Each `RunXxxAsync` method gets ~10 lines to call additional existing scenarios and average scores. No new models, no new interfaces. The scenarios are already tested.

**Stochastic mode remains deferred.** That genuinely adds complexity (multi-run loops, statistical aggregation, `BenchmarkOptions` class). Scenario depth is simpler — it's just wiring.

**Note on `StochasticData` in `CategoryScoreEntry`:** The field is included as `StochasticData? Stochastic` (nullable, defaults to null). Forward-compatible for future stochastic mode. This plan always leaves it null. The JSON serializer omits it (`DefaultIgnoreCondition.WhenWritingNull`).

---

## Pre-Implementation Checklist

Before starting, verify the starting state:

- [ ] `dotnet build` — 0 errors, 0 warnings across all projects
- [ ] `dotnet test` — all tests pass (348 Memory, 7,557 Core per TFM)
- [ ] `git status` — clean working tree on `joslat-memory-evaluations` branch
- [ ] Confirm no `Reporting/` folder exists in `src/AgentEval.Memory/`
- [ ] Confirm no `Report/` folder exists in `src/AgentEval.Memory/`
- [ ] Confirm `MemoryBenchmarkResult` has NO `ToBaseline()` or `ToEvaluationReport()` methods
- [ ] Confirm `src/AgentEval.Memory/Samples/` contains only an empty .csproj (to be removed in Task 0)

---

## Task 0: Housekeeping — Remove Stray Samples Project

**What:** Delete `src/AgentEval.Memory/Samples/` — an empty, orphaned project with only a .csproj referencing a non-existent `SimpleSample` class. Not in the .sln. Not functional.

**Files to delete:**
- `src/AgentEval.Memory/Samples/AgentEval.Memory.Samples.csproj`
- `src/AgentEval.Memory/Samples/` (the folder itself)

**Verify:**
- `dotnet build` still succeeds (this project was never in the solution)
- No references to `AgentEval.Memory.Samples` exist anywhere

---

## Phase 1: Data Models (Foundation)

All new models go in `src/AgentEval.Memory/Models/`. These have zero dependencies on anything outside the existing project — pure data classes.

### Task 1.1: Create CategoryScoreEntry.cs

**File:** `src/AgentEval.Memory/Models/CategoryScoreEntry.cs`
**Namespace:** `AgentEval.Memory.Models`

```csharp
/// <summary>
/// Per-category score entry for baseline serialization.
/// Maps from BenchmarkCategoryResult (internal runtime model) to a serializable snapshot.
/// </summary>
public class CategoryScoreEntry
{
    public required double Score { get; init; }
    public required string Grade { get; init; }
    public required bool Skipped { get; init; }
    public int ScenarioCount { get; init; } = 1;
    public string? Recommendation { get; init; }
    public StochasticData? Stochastic { get; init; }
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

**Why separate from BenchmarkCategoryResult?** `BenchmarkCategoryResult` has computed properties (`Stars`) and runtime-only fields (`Weight`, `Duration`). `CategoryScoreEntry` is the serializable subset for JSON baselines. The conversion happens in `BaselineExtensions.ToBaseline()`.

**Properties to include:**
- `Score` (double 0-100) — from `BenchmarkCategoryResult.Score`
- `Grade` (string) — computed at creation time using the same thresholds as `MemoryBenchmarkResult`
- `Skipped` (bool) — from `BenchmarkCategoryResult.Skipped`
- `ScenarioCount` (int) — defaults to 1 now, will increase with scenario depth (Phase 2)
- `Recommendation` (string?) — from `MemoryBenchmarkResult.Recommendations` filtered to this category
- `Stochastic` (StochasticData?) — null for single-run, populated for multi-run (Phase 2)

### Task 1.2: Create AgentBenchmarkConfig.cs

**File:** `src/AgentEval.Memory/Models/AgentBenchmarkConfig.cs`
**Namespace:** `AgentEval.Memory.Models`

```csharp
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Full agent configuration captured at benchmark time.
/// The user provides this when creating a baseline.
/// ConfigurationId is computed deterministically from memory-affecting properties.
/// </summary>
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
    /// Deterministic 12-char hex hash of memory-affecting properties.
    /// Same config → same ID → timeline grouping in reports.
    /// Different config → different ID → radar comparison.
    /// </summary>
    public string ConfigurationId => ComputeConfigurationId();

    private string ComputeConfigurationId()
    {
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

**Critical design detail:** `ConfigurationId` includes `AgentName` so different agents always have different IDs. It includes `ModelId`, `ReducerStrategy`, `MemoryProvider`, and sorted `ContextProviders` — the properties that affect memory benchmark results. It does NOT include `Temperature`, `MaxTokens`, or `CustomConfig` because those are secondary.

### Task 1.3: Create BenchmarkExecutionInfo.cs

**File:** `src/AgentEval.Memory/Models/BenchmarkExecutionInfo.cs`
**Namespace:** `AgentEval.Memory.Models`

```csharp
/// <summary>
/// Metadata about the benchmark execution (not the agent, but the run itself).
/// </summary>
public class BenchmarkExecutionInfo
{
    public required string Preset { get; init; }
    public required TimeSpan Duration { get; init; }
    public int? TotalLlmCalls { get; init; }
    public double? EstimatedCostUsd { get; init; }
    public string? ScenarioDepth { get; init; }
}
```

### Task 1.4: Create MemoryBaseline.cs

**File:** `src/AgentEval.Memory/Models/MemoryBaseline.cs`
**Namespace:** `AgentEval.Memory.Models`

```csharp
/// <summary>
/// A named, timestamped snapshot of a memory benchmark run with full metadata.
/// This is the central model for persistence and comparison.
/// One baseline = one JSON file in the baselines/ folder.
/// </summary>
public class MemoryBaseline
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string ConfigurationId { get; init; }
    public required AgentBenchmarkConfig AgentConfig { get; init; }
    public required BenchmarkExecutionInfo Benchmark { get; init; }
    public required double OverallScore { get; init; }
    public required string Grade { get; init; }
    public required int Stars { get; init; }
    public required IReadOnlyDictionary<string, CategoryScoreEntry> CategoryResults { get; init; }
    public required IReadOnlyDictionary<string, double> DimensionScores { get; init; }
    public required IReadOnlyList<string> Recommendations { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}
```

### Task 1.5: Create RadarChartData.cs

**File:** `src/AgentEval.Memory/Models/RadarChartData.cs`
**Namespace:** `AgentEval.Memory.Models`

```csharp
/// <summary>
/// Data structure for rendering radar/spider charts (pentagon).
/// Designed to map directly to Chart.js radar chart configuration.
/// </summary>
public class RadarChartData
{
    public required IReadOnlyList<string> Axes { get; init; }
    public double MaxValue { get; init; } = 100;
    public required IReadOnlyList<RadarChartSeries> Series { get; init; }
}

public class RadarChartSeries
{
    public required string Name { get; init; }
    public required IReadOnlyList<double> Values { get; init; }
    public string? Color { get; init; }
}
```

### Task 1.6: Create BaselineComparison.cs

**File:** `src/AgentEval.Memory/Models/BaselineComparison.cs`
**Namespace:** `AgentEval.Memory.Models`

```csharp
/// <summary>
/// Result of comparing two or more baselines.
/// </summary>
public class BaselineComparison
{
    public required IReadOnlyList<MemoryBaseline> Baselines { get; init; }
    public required IReadOnlyList<DimensionComparison> Dimensions { get; init; }
    public required string BestBaselineId { get; init; }
    public required RadarChartData RadarChart { get; init; }
}

public class DimensionComparison
{
    public required string DimensionName { get; init; }
    public required IReadOnlyDictionary<string, double> Scores { get; init; }
    public double BestScore => Scores.Values.Max();
    public string BestBaselineId => Scores.MaxBy(kvp => kvp.Value).Key;
}
```

### Task 1.7: Create BenchmarkManifest.cs

**File:** `src/AgentEval.Memory/Models/BenchmarkManifest.cs`
**Namespace:** `AgentEval.Memory.Models`

```csharp
/// <summary>
/// The manifest.json model — auto-generated index of all baselines for an agent.
/// The HTML report loads this to discover available baselines.
/// </summary>
public class BenchmarkManifest
{
    public required string SchemaVersion { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string GeneratedBy { get; init; }
    public required ManifestAgentInfo Agent { get; init; }
    public required IReadOnlyList<ManifestBenchmarkGroup> Benchmarks { get; init; }
    public string? Archetypes { get; init; }
}

public class ManifestAgentInfo
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Repository { get; init; }
    public string? Team { get; init; }
}

public class ManifestBenchmarkGroup
{
    public required string BenchmarkId { get; init; }
    public required string Preset { get; init; }
    public required IReadOnlyList<string> Categories { get; init; }
    public required IReadOnlyList<ManifestBaselineEntry> Baselines { get; init; }
}

public class ManifestBaselineEntry
{
    public required string Id { get; init; }
    public required string File { get; init; }
    public required string Name { get; init; }
    public required string ConfigurationId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required double OverallScore { get; init; }
    public required string Grade { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}
```

### Task 1.8: Model Unit Tests

**File:** `tests/AgentEval.Memory.Tests/Models/ReportingModelTests.cs`

These validate the non-trivial logic in the data models before anything else builds on them.

**Test cases:**
1. `AgentBenchmarkConfig.ConfigurationId` — same config properties → same hash (deterministic)
2. `AgentBenchmarkConfig.ConfigurationId` — different ModelId → different hash
3. `AgentBenchmarkConfig.ConfigurationId` — different ReducerStrategy → different hash
4. `AgentBenchmarkConfig.ConfigurationId` — ContextProviders order doesn't matter (sorted internally)
5. `AgentBenchmarkConfig.ConfigurationId` — null properties handled gracefully (no NullReferenceException)
6. `DimensionComparison.BestScore` — returns max across scores
7. `DimensionComparison.BestBaselineId` — returns ID of highest scorer
8. `StochasticData.CoefficientOfVariation` — computed correctly, handles Mean=0
9. **JSON round-trip:** `MemoryBaseline` survives `JsonSerializer.Serialize` → `JsonSerializer.Deserialize` with `SnakeCaseLower` naming (critical — `IReadOnlyDictionary` and `IReadOnlyList` deserialization can fail without proper converter config). **If this test fails:** the model properties must use concrete types (`Dictionary<>`, `List<>`) for JSON deserialization, or the `JsonSerializerOptions` must include converters. Fix this BEFORE proceeding to Phase 5 (the store depends on it).
10. **JSON round-trip:** `BenchmarkManifest` survives serialize → deserialize
11. **JSON round-trip:** Verify `snake_case` naming in output — `"overall_score"`, `"agent_config"`, `"configuration_id"` (not camelCase, not PascalCase)

**Why test JSON round-trip here?** `JsonFileBaselineStore` depends on correct serialization. If `IReadOnlyDictionary<string, CategoryScoreEntry>` can't round-trip, the store silently breaks. Catching this in Phase 1 prevents debugging in Phase 4.

### Checkpoint 1: Build + Test

```bash
dotnet build src/AgentEval.Memory/AgentEval.Memory.csproj
dotnet test tests/AgentEval.Memory.Tests/AgentEval.Memory.Tests.csproj
```

**Expected:** 0 errors. All 7 new model files compile. 10 new model tests pass. All existing 348 tests still pass.

---

## Phase 2: Pentagon Consolidator + Baseline Extensions

### Task 2.1: Create Reporting/ folder and PentagonConsolidator.cs

**File:** `src/AgentEval.Memory/Reporting/PentagonConsolidator.cs`
**Namespace:** `AgentEval.Memory.Reporting`

This class maps 8 benchmark categories → 5 pentagon dimensions.

```csharp
using AgentEval.Memory.Models;

/// <summary>
/// Maps 8 benchmark categories to 5 pentagon dimensions for radar visualization.
///
/// Mapping:
///   Recall      = avg(BasicRetention, ReachBackDepth)
///   Resilience  = avg(NoiseResilience, ReducerFidelity)
///   Temporal    = avg(TemporalReasoning, FactUpdateHandling)
///   Persistence = CrossSession (1:1)
///   Organization = MultiTopic (1:1)
///
/// Handles gracefully when categories are skipped (uses available data only).
/// </summary>
public static class PentagonConsolidator
{
    public static readonly IReadOnlyList<string> Axes =
        ["Recall", "Resilience", "Temporal", "Persistence", "Organization"];

    public static IReadOnlyDictionary<string, double> Consolidate(
        IReadOnlyList<MemoryBenchmarkResult.BenchmarkCategoryResult> categoryResults)
    {
        // Implementation: build lookup from ScenarioType → Score, skip Skipped categories
        // For each axis, compute average of available source categories
        // If no source categories available for an axis, omit it from the result
    }
}
```

**Test file:** `tests/AgentEval.Memory.Tests/Reporting/PentagonConsolidatorTests.cs`

**Test cases:**
1. All 8 categories present → all 5 axes computed correctly
2. CrossSession skipped → Persistence axis omitted, other 4 axes present
3. Both BasicRetention and ReachBackDepth present → Recall = average
4. Only BasicRetention present (Quick preset) → Recall = BasicRetention score directly
5. Empty category list → empty result
6. All categories skipped → empty result

### Task 2.2: Create BaselineExtensions.cs

**File:** `src/AgentEval.Memory/Reporting/BaselineExtensions.cs`
**Namespace:** `AgentEval.Memory.Reporting`

```csharp
/// <summary>
/// Extension method to convert a MemoryBenchmarkResult into a MemoryBaseline.
/// This is the primary API users call after running a benchmark.
///
/// Usage:
///   var baseline = result.ToBaseline("v2.1 Production", config, tags: ["prod"]);
/// </summary>
public static class BaselineExtensions
{
    public static MemoryBaseline ToBaseline(
        this MemoryBenchmarkResult result,
        string name,
        AgentBenchmarkConfig config,
        string? description = null,
        IReadOnlyList<string>? tags = null)
    {
        // 1. Generate unique ID: "bl-" + 8 random hex chars
        // 2. Convert each BenchmarkCategoryResult to CategoryScoreEntry
        //    - Grade computed from score using same thresholds as MemoryBenchmarkResult.Grade
        //    - Recommendation: match from result.Recommendations (if available for this category)
        // 3. Compute pentagon dimension scores via PentagonConsolidator
        // 4. Build BenchmarkExecutionInfo from result metadata
        // 5. Return MemoryBaseline with all fields populated
    }
}
```

**Grade computation helper** (private, in BaselineExtensions) — **MUST match `MemoryBenchmarkResult.Grade` exactly** (5-grade system: A/B/C/D/F):
```csharp
private static string ComputeGrade(double score) => score switch
{
    >= 90 => "A",
    >= 80 => "B",
    >= 70 => "C",
    >= 60 => "D",
    _ => "F"
};
```

**CRITICAL:** This uses the SAME thresholds as `MemoryBenchmarkResult.Grade` (line 36 of `MemoryBenchmarkResult.cs`). Do NOT use a finer-grained system (A+/A/A-) — it would cause divergence where `result.Grade` says "B" but `baseline.CategoryResults["X"].Grade` says "B+". One grading system everywhere.

**Test file:** `tests/AgentEval.Memory.Tests/Reporting/BaselineExtensionsTests.cs`

**Test cases:**
1. Basic conversion — all fields populated correctly (Name, Timestamp, OverallScore, Stars)
2. ConfigurationId in baseline matches `config.ConfigurationId`
3. DimensionScores computed correctly via PentagonConsolidator (5 axes)
4. Skipped categories preserved in CategoryResults with `Skipped = true`
5. Tags default to empty list when not provided; tags passed through when provided
6. Per-category Grade uses SAME thresholds as `MemoryBenchmarkResult.Grade` (5-grade system) — test: score 85 → "B" (not "A-"), score 95 → "A" (not "A+")
7. Unique ID generated each call — call twice, get different IDs
8. `Benchmark.Preset` populated from `result.BenchmarkName`
9. `Benchmark.Duration` populated from `result.Duration`

### Checkpoint 2: Build + Test

```bash
dotnet build src/AgentEval.Memory/AgentEval.Memory.csproj
dotnet test tests/AgentEval.Memory.Tests/AgentEval.Memory.Tests.csproj
```

**Expected:** 0 errors. New tests pass. All existing 348 tests still pass.

---

## Phase 3: Export Bridge

### Task 3.1: Create MemoryBenchmarkReportExtensions.cs

**File:** `src/AgentEval.Memory/Extensions/MemoryBenchmarkReportExtensions.cs`
**Namespace:** `AgentEval.Memory.Extensions`

```csharp
using AgentEval.Memory.Models;
using AgentEval.Models;

/// <summary>
/// Bridge from MemoryBenchmarkResult to the AgentEval export pipeline.
/// Converts to EvaluationReport so all 6 existing exporters (JSON, CSV, Markdown,
/// JUnit XML, TRX, Directory) work automatically.
///
/// Follows the same pattern as TestSummaryExtensions.ToEvaluationReport() in
/// AgentEval.Abstractions/Models/TestSummaryExtensions.cs.
/// </summary>
public static class MemoryBenchmarkReportExtensions
{
    public static EvaluationReport ToEvaluationReport(
        this MemoryBenchmarkResult result,
        string? agentName = null,
        string? modelName = null)
    {
        // Map each BenchmarkCategoryResult to TestResultSummary
        // Set Metadata with Grade, Stars, BenchmarkType
        // Return EvaluationReport
    }
}
```

**Test file:** `tests/AgentEval.Memory.Tests/Extensions/MemoryBenchmarkReportExtensionsTests.cs`

**Test cases:**
1. Conversion produces correct TotalTests, PassedTests, FailedTests, SkippedTests counts
2. OverallScore maps correctly
3. AgentInfo populated when agentName/modelName provided
4. AgentInfo is null when no agent info provided
5. Each category maps to a TestResultSummary with correct Name, Score, Passed, Skipped
6. Metadata contains "Grade", "Stars", "BenchmarkType"
7. Skipped categories have `Skipped = true` and `Error = SkipReason`
8. MetricScores dictionary has `memory_{ScenarioType}` key

### Checkpoint 3: Build + Test

```bash
dotnet build src/AgentEval.Memory/AgentEval.Memory.csproj
dotnet test tests/AgentEval.Memory.Tests/AgentEval.Memory.Tests.csproj
```

**Expected:** 0 errors. New tests pass. Existing tests unaffected.

---

## Phase 4: Scenario Depth — Enrich the Benchmark Runner

> **Why here?** This modifies existing code (`MemoryBenchmarkRunner.cs`) and doesn't depend on any new reporting infrastructure. It must happen before the reporting phases so the baselines produced by the runner contain statistically meaningful data.

### Task 4.1: Enrich MemoryBenchmarkRunner.cs

**File:** `src/AgentEval.Memory/Evaluators/MemoryBenchmarkRunner.cs` (EXISTING — modify)

Each `RunXxxAsync` method is modified to run additional scenarios from the existing scenario library and average their scores. The preset name determines depth (Quick=1 scenario, Standard=2, Full=3+). The preset name is passed through from `RunBenchmarkAsync` → `RunCategoryAsync` → each handler.

**Step 1:** Add a `string presetName` parameter to `RunCategoryAsync` and each `RunXxxAsync` method. Pass `benchmark.Name` from `RunBenchmarkAsync`.

**Step 2:** Modify each handler. Here is the exact change for each:

#### BasicRetention (currently: 5 facts, 4 queries)
```
Quick:    CreateBasicMemoryTest (current — 5 facts, 4 queries)
Standard: + CreateLongTermMemoryTest (facts + 10 conversation turns, then query)
Full:     + CreatePriorityMemoryTest (high vs low importance facts)
```
Score = average of all scenario scores.

#### TemporalReasoning (currently: 4 timestamped events, sequence only)
```
Quick:    CreateSequenceMemoryTest (current — chronological ordering)
Standard: + CreateTimePointMemoryTest (events at specific time points)
Full:     + CreateCausalReasoningTest (cause-effect chains)
```

#### NoiseResilience (currently: 2 facts in noise — TOO THIN)
```
Quick:    CreateBuriedFactsScenario (current — 2 facts in 3:1 noise)
Standard: + CreateTopicSwitchingScenario (facts scattered among rapid topic changes)
Full:     + CreateEmotionalDistractorScenario (facts + emotional content)
          + CreateFalseInformationScenario (true vs false facts + correction)
```

#### ReachBackDepth (currently: 1 fact, depths [5,10,25])
```
Quick:    [5, 10, 25] (current)
Standard: [5, 10, 25, 50] (deeper probing)
Full:     [5, 10, 25, 50, 100] (full depth profiling)
```

#### FactUpdateHandling (currently: 2 facts + 2 corrections)
```
Quick:    CreateMemoryUpdateTest (current — 2 corrections)
Standard: + RetentionWithDelay with correction facts (verify corrections stick after conversation turns)
Full:     (same as Standard — the scenario library doesn't have a 3rd update variant yet)
```

#### MultiTopic (currently: 5 facts across 5 topics, no cross-contamination test)
```
Quick:    CreateBasicMemoryTest with multi-topic facts (current)
Standard: + CategorizedMemory (facts grouped by topic, category-specific queries)
Full:     (same as Standard — cross-contamination scenario would need new code, defer)
```

#### CrossSession (currently: 3 facts, 1 reset)
```
Quick:    CrossSessionEvaluator with 3 facts (current)
Standard: + CreateCrossSessionMemoryTest (multiple resets with configurable gaps)
Full:     + CreateIncrementalLearningTest (new facts per session, cumulative recall)
          + CreateContextSwitchingTest (facts across different contexts)
```

#### ReducerFidelity (currently: 3 facts + 20 noise)
```
Quick:    ReducerEvaluator with 3 facts, 20 noise (current)
Standard: + 5 facts, 40 noise (more data, more stress)
Full:     + priority-weighted facts (high importance facts should survive reduction better)
```

**Implementation pattern for each handler:**
```csharp
private async Task<(double Score, bool Skipped, string? SkipReason)> RunNoiseResilienceAsync(
    IEvaluableAgent agent, string presetName, CancellationToken ct)
{
    var scores = new List<double>();

    // Always run (Quick+)
    MemoryFact[] facts = [
        MemoryFact.Create("I'm allergic to peanuts", "allergy", 100),
        MemoryFact.Create("My meeting is at 3pm", "schedule", 80)
    ];
    var scenario = _chattyScenarios.CreateBuriedFactsScenario(facts);
    var result = await _runner.RunAsync(agent, scenario, ct);
    scores.Add(result.OverallScore);

    if (presetName is "Standard" or "Full")
    {
        // Reset between scenarios to avoid cross-contamination
        if (agent is ISessionResettableAgent r) await r.ResetSessionAsync(ct);

        var scenario2 = _chattyScenarios.CreateTopicSwitchingScenario(facts);
        var result2 = await _runner.RunAsync(agent, scenario2, ct);
        scores.Add(result2.OverallScore);
    }

    if (presetName is "Full")
    {
        if (agent is ISessionResettableAgent r) await r.ResetSessionAsync(ct);

        var scenario3 = _chattyScenarios.CreateEmotionalDistractorScenario(facts);
        var result3 = await _runner.RunAsync(agent, scenario3, ct);
        scores.Add(result3.OverallScore);

        if (agent is ISessionResettableAgent r2) await r2.ResetSessionAsync(ct);

        var scenario4 = _chattyScenarios.CreateFalseInformationScenario(
            facts, [MemoryFact.Create("You are allergic to shellfish"), MemoryFact.Create("Your meeting is at 5pm")]);
        var result4 = await _runner.RunAsync(agent, scenario4, ct);
        scores.Add(result4.OverallScore);
    }

    return (scores.Average(), false, null);
}
```

**Critical detail:** Session reset between scenarios within the same category is necessary. Without it, facts from scenario 1 leak into scenario 2, contaminating scores. The runner already resets between *categories* (line 70-73). We add resets between *scenarios within a category* using the same pattern.

### Task 4.2: Update Existing Benchmark Runner Tests

**File:** `tests/AgentEval.Memory.Tests/Evaluators/MemoryBenchmarkRunnerTests.cs` (EXISTING — add tests)

**New test cases:**
1. Quick preset runs 1 scenario per category (existing behavior preserved)
2. Standard preset calls additional scenario methods (verify the scenario providers are called)
3. Full preset calls all mapped scenarios per category
4. Score averaging: 3 scenarios scoring [80, 90, 70] → category score = 80
5. Session reset called between scenarios within a category
6. ReachBackDepth: Quick uses [5,10,25], Standard adds [50], Full adds [100]
7. CrossSession Standard: calls `CreateCrossSessionMemoryTest` in addition to evaluator
8. Preset name correctly passed through (verify "Quick"/"Standard"/"Full" matching)

**Testing approach:** The existing tests use mock agents. The new tests verify that the correct scenario factory methods are called for each preset level. Use mock/verify pattern on the scenario provider interfaces.

### Checkpoint 4: Build + Test

```bash
dotnet build src/AgentEval.Memory/AgentEval.Memory.csproj
dotnet test tests/AgentEval.Memory.Tests/AgentEval.Memory.Tests.csproj
```

**Expected:** 0 errors. All existing benchmark runner tests still pass (they use Quick preset, which is unchanged). New tests verify Standard/Full depth. All other 348 tests unaffected.

---

## Phase 5: Embedded Resources (HTML Report + Archetypes)

> **Why before the store?** `JsonFileBaselineStore` (Phase 6) copies `report.html` and `archetypes.json` from embedded resources on `SaveAsync()`. The embedded resources MUST exist in the assembly before the store can be implemented and tested.

### Task 5.1: Create Report/ folder with report.html

**File:** `src/AgentEval.Memory/Report/report.html`

Copy the existing prototype from `src/AgentEval.Memory/agenteval-memory-benchmark-report.html` and modify it to:
1. Load data from `./manifest.json` (fetch) instead of hardcoded `BL` array
2. Load individual baselines on demand (fetch baseline file from manifest entry)
3. Group baselines by `configuration_id` for timeline vs radar routing
4. Change hexagon (6 axes) to pentagon (5 axes): `['Recall', 'Resilience', 'Temporal', 'Persistence', 'Organization']`
5. Add archetype loading from `archetypes` path in manifest
6. Keep the existing 5-tab structure (Overview, Pentagon, Timeline, Comparer, Structure)

**Key changes from prototype:**
- Replace hardcoded `const BL = [...]` with `fetch('./manifest.json')` + on-demand baseline loading
- Pentagon labels and colors (5 instead of 6)
- Config cards show `configuration_id` grouping
- Timeline tab filters by selected `configuration_id`
- Add archetype toggle in Pentagon tab (dashed reference shapes)

### Task 5.2: Create Report/archetypes.json

**File:** `src/AgentEval.Memory/Report/archetypes.json`

The 6 memory-architecture archetypes from the roadmap document (Section 9):
1. Stateless Context Window
2. Sliding Window Agent
3. Summarizing Agent
4. RAG-Enhanced Agent
5. Full Persistent Memory Agent
6. Expert Router (No Long-Term Memory)

Each with `expected_scores` using the 5 pentagon axes.

### Task 5.3: Configure Embedded Resources in .csproj

**File:** `src/AgentEval.Memory/AgentEval.Memory.csproj`

Add to the .csproj:
```xml
<ItemGroup>
  <EmbeddedResource Include="Report\report.html" />
  <EmbeddedResource Include="Report\archetypes.json" />
</ItemGroup>
```

### Task 5.4: Embedded Resource Accessibility Test

**File:** `tests/AgentEval.Memory.Tests/Reporting/EmbeddedResourceTests.cs`

```csharp
[Fact]
public void ReportHtml_IsAccessibleAsEmbeddedResource()
{
    var assembly = typeof(MemoryBaseline).Assembly;
    var names = assembly.GetManifestResourceNames();
    // Find the report.html resource (name depends on RootNamespace + folder path)
    var reportName = names.FirstOrDefault(n => n.EndsWith("report.html"));
    Assert.NotNull(reportName);
    using var stream = assembly.GetManifestResourceStream(reportName);
    Assert.NotNull(stream);
    Assert.True(stream.Length > 0);
}

[Fact]
public void ArchetypesJson_IsAccessibleAsEmbeddedResource()
{
    var assembly = typeof(MemoryBaseline).Assembly;
    var names = assembly.GetManifestResourceNames();
    var archetypeName = names.FirstOrDefault(n => n.EndsWith("archetypes.json"));
    Assert.NotNull(archetypeName);
    using var stream = assembly.GetManifestResourceStream(archetypeName);
    Assert.NotNull(stream);
    Assert.True(stream.Length > 0);
}

[Fact]
public void ArchetypesJson_ContainsValidJson_With6Archetypes()
{
    var assembly = typeof(MemoryBaseline).Assembly;
    var name = assembly.GetManifestResourceNames().First(n => n.EndsWith("archetypes.json"));
    using var stream = assembly.GetManifestResourceStream(name)!;
    using var reader = new StreamReader(stream);
    var json = reader.ReadToEnd();
    var doc = JsonDocument.Parse(json);
    var archetypes = doc.RootElement.GetProperty("archetypes");
    Assert.Equal(6, archetypes.GetArrayLength());
}
```

**Why test resource names dynamically?** The exact embedded resource name depends on `RootNamespace` (which is `AgentEval` for this project) and the folder structure. Using `GetManifestResourceNames()` + `.EndsWith()` is robust against namespace variations. The test will catch misconfigured `.csproj` entries immediately.

### Checkpoint 5: Build + Test

```bash
dotnet build src/AgentEval.Memory/AgentEval.Memory.csproj
dotnet test tests/AgentEval.Memory.Tests/AgentEval.Memory.Tests.csproj
```

**Expected:** 0 errors. Embedded resources included in assembly. 3 new resource tests pass. All existing tests still pass.

---

## Phase 6: Persistence (IBaselineStore + JsonFileBaselineStore)

### Task 6.1: Create IBaselineStore.cs

**File:** `src/AgentEval.Memory/Reporting/IBaselineStore.cs`
**Namespace:** `AgentEval.Memory.Reporting`

```csharp
/// <summary>
/// Persistence interface for memory benchmark baselines.
/// Implementations handle saving, loading, listing, and deleting baselines.
/// </summary>
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
```

### Task 6.2: Create IBaselineComparer.cs

**File:** `src/AgentEval.Memory/Reporting/IBaselineComparer.cs`
**Namespace:** `AgentEval.Memory.Reporting`

```csharp
public interface IBaselineComparer
{
    BaselineComparison Compare(IReadOnlyList<MemoryBaseline> baselines);
}
```

### Task 6.3: Create MemoryReportingOptions.cs

**File:** `src/AgentEval.Memory/Reporting/MemoryReportingOptions.cs`
**Namespace:** `AgentEval.Memory.Reporting`

```csharp
public class MemoryReportingOptions
{
    /// <summary>
    /// Root path for benchmark output. {AgentName} is replaced at runtime.
    /// Default: ".agenteval/benchmarks/{AgentName}"
    /// </summary>
    public string OutputPath { get; set; } = ".agenteval/benchmarks/{AgentName}";

    /// <summary>Whether to auto-copy report.html on first baseline save.</summary>
    public bool AutoCopyReportTemplate { get; set; } = true;

    /// <summary>Whether to auto-copy archetypes.json alongside the report.</summary>
    public bool IncludeArchetypes { get; set; } = true;
}
```

### Task 6.4: Create JsonFileBaselineStore.cs

**File:** `src/AgentEval.Memory/Reporting/JsonFileBaselineStore.cs`
**Namespace:** `AgentEval.Memory.Reporting`

This is the most complex implementation. It does 3 things on `SaveAsync`:
1. Writes the baseline JSON to `baselines/{date}_{slug}.json`
2. Rebuilds `manifest.json` by scanning all baseline files
3. Copies `report.html` and `archetypes.json` from embedded resources if missing

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public class JsonFileBaselineStore : IBaselineStore
{
    private readonly MemoryReportingOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public JsonFileBaselineStore(MemoryReportingOptions options)
    {
        _options = options;
    }

    public async Task SaveAsync(MemoryBaseline baseline, CancellationToken ct = default)
    {
        var rootPath = ResolveRootPath(baseline.AgentConfig.AgentName);
        var baselinesDir = Path.Combine(rootPath, "baselines");
        Directory.CreateDirectory(baselinesDir);

        // 1. Write baseline JSON
        var filename = $"{baseline.Timestamp:yyyy-MM-dd}_{Slugify(baseline.Name)}.json";
        var path = Path.Combine(baselinesDir, filename);
        var json = JsonSerializer.Serialize(baseline, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);

        // 2. Rebuild manifest
        await RebuildManifestAsync(rootPath, ct);

        // 3. Copy templates if missing
        if (_options.AutoCopyReportTemplate)
            await EnsureReportTemplateAsync(rootPath, ct);
        if (_options.IncludeArchetypes)
            await EnsureArchetypesAsync(rootPath, ct);
    }

    // LoadAsync: scan baselines/ folder for matching ID
    // ListAsync: scan and optionally filter by agentName/tags
    // DeleteAsync: delete file and rebuild manifest
    // RebuildManifestAsync: scan baselines/*.json, deserialize header info, write manifest.json
    // EnsureReportTemplateAsync: copy from embedded resource if report.html missing
    // EnsureArchetypesAsync: copy from embedded resource if archetypes.json missing
    // Slugify: lowercase, replace non-alphanumeric with hyphens, truncate to 60 chars
    // ResolveRootPath: replace {AgentName} token in OutputPath
}
```

**Key implementation details:**
- `RebuildManifestAsync` must NOT deserialize entire baselines for the manifest — it should read each file, deserialize to get `Id`, `Name`, `ConfigurationId`, `Timestamp`, `OverallScore`, `Grade`, `Tags`, `Benchmark.Preset`. The manifest stores only this summary data.
- `Slugify` must handle edge cases: empty strings, unicode, very long names.
- `ResolveRootPath` replaces `{AgentName}` with the sanitized agent name.
- All file I/O is async.

**Test file:** `tests/AgentEval.Memory.Tests/Reporting/JsonFileBaselineStoreTests.cs`

**Test cases (use temp directory for isolation):**
1. `SaveAsync` creates baselines directory if not exists
2. `SaveAsync` writes valid JSON file with correct filename format
3. `SaveAsync` rebuilds manifest.json with correct structure
4. `SaveAsync` copies report.html from embedded resource on first call
5. `SaveAsync` does NOT overwrite existing report.html
6. `LoadAsync` returns correct baseline by ID
7. `LoadAsync` returns null for non-existent ID
8. `ListAsync` returns all baselines ordered by timestamp
9. `ListAsync` filters by agent name
10. `ListAsync` filters by tags
11. `DeleteAsync` removes file and rebuilds manifest
12. `DeleteAsync` returns false for non-existent ID
13. Multiple saves produce correct manifest with all entries
14. Manifest groups baselines by benchmark preset
15. Slugify handles special characters, spaces, unicode
16. JSON uses snake_case naming

### Task 6.5: Create BaselineComparer.cs

**File:** `src/AgentEval.Memory/Reporting/BaselineComparer.cs`
**Namespace:** `AgentEval.Memory.Reporting`

```csharp
public class BaselineComparer : IBaselineComparer
{
    public BaselineComparison Compare(IReadOnlyList<MemoryBaseline> baselines)
    {
        // 1. Collect all dimension names across all baselines
        // 2. For each dimension, build scores dictionary (baselineId → score)
        // 3. Determine best baseline (highest overall score)
        // 4. Build RadarChartData with Pentagon axes
        // 5. Return BaselineComparison
    }
}
```

**Test file:** `tests/AgentEval.Memory.Tests/Reporting/BaselineComparerTests.cs`

**Test cases:**
1. Compare 2 baselines — correct dimension comparisons
2. Compare 3 baselines — best baseline identified correctly
3. RadarChartData has correct axes (Pentagon 5)
4. RadarChartData series match baseline count
5. Single baseline — comparison still works (self-comparison)
6. Baselines with different dimension sets — handles missing axes gracefully

### Checkpoint 6: Build + Full Memory Test Suite

```bash
dotnet build src/AgentEval.Memory/AgentEval.Memory.csproj
dotnet test tests/AgentEval.Memory.Tests/AgentEval.Memory.Tests.csproj
```

**Expected:** 0 errors. All new tests pass (models + consolidator + extensions + export bridge + scenario depth + embedded resources + store + comparer). All existing tests still pass.

---

## Phase 7: DI Registration

### Task 7.1: Add AddAgentEvalMemoryReporting() to DI

**File:** `src/AgentEval.Memory/Extensions/AgentEvalMemoryServiceCollectionExtensions.cs` (EXISTING — modify)

Add a new method `AddAgentEvalMemoryReporting()` that registers:
- `MemoryReportingOptions` as Singleton
- `IBaselineStore` → `JsonFileBaselineStore` as Scoped
- `IBaselineComparer` → `BaselineComparer` as Scoped

Update `AddAgentEvalMemory()` to call `AddAgentEvalMemoryReporting()`.

**Test file:** `tests/AgentEval.Memory.Tests/Extensions/AgentEvalMemoryServiceCollectionExtensionsTests.cs` (EXISTING — add tests)

**New test cases:**
1. `AddAgentEvalMemoryReporting` registers `IBaselineStore`
2. `AddAgentEvalMemoryReporting` registers `IBaselineComparer`
3. `AddAgentEvalMemoryReporting` registers `MemoryReportingOptions`
4. `AddAgentEvalMemoryReporting` with custom options — options are applied
5. `AddAgentEvalMemory` now resolves `IBaselineStore` (integration)

### Checkpoint 7: Build + Test

```bash
dotnet build src/AgentEval.Memory/AgentEval.Memory.csproj
dotnet test tests/AgentEval.Memory.Tests/AgentEval.Memory.Tests.csproj
```

**Expected:** 0 errors. All tests pass (existing + new DI tests).

---

## Phase 8: End-to-End Integration Test

### Task 8.1: End-to-End Integration Test

**File:** `tests/AgentEval.Memory.Tests/Reporting/EndToEndReportingTests.cs`

This test exercises the full pipeline without a real LLM — it creates a mock `MemoryBenchmarkResult`, converts it to a baseline, saves it, loads it, saves a second baseline with a different config, compares them, and verifies the export bridge.

**Test cases:**
1. Full pipeline: `MemoryBenchmarkResult` → `.ToBaseline()` → `store.SaveAsync()` → `store.LoadAsync()` → verify round-trip
2. Two baselines with same config → `store.ListAsync()` returns both, same `ConfigurationId`
3. Two baselines with different config → `store.ListAsync()` returns both, different `ConfigurationId`
4. Comparison: 2 baselines → `comparer.Compare()` → verify `RadarChartData` has 5 axes, 2 series
5. Export bridge: `result.ToEvaluationReport()` → verify `EvaluationReport` fields
6. Manifest verification: after 2 saves, `manifest.json` has 2 entries with correct data
7. File structure verification: `baselines/` folder has 2 JSON files, `report.html` exists, `archetypes.json` exists

### Checkpoint 8: Full Solution Build + All Tests

```bash
dotnet build
dotnet test
```

**Expected:** 0 errors across entire solution. All Memory tests pass. All Core tests pass. No regressions.

**This is the critical gate.** If the full solution passes here, the implementation is functionally complete. Phase 9 is the sample (demonstration), Phase 10 is final review.

---

## Phase 9: Sample Application

### Task 9.1: Create Benchmark Reporting Sample

**File:** `samples/AgentEval.Samples/MemoryEvaluation/06_MemoryBenchmarkReporting.cs`
**Namespace:** `AgentEval.Samples`
**Class:** `MemoryBenchmarkReporting`

This sample demonstrates the full reporting workflow with a real LLM agent. The user can see how changing the agent's memory configuration produces different benchmark shapes.

**What the sample does:**
1. Creates an Azure OpenAI chat client
2. Sets up all evaluation components (same pattern as existing `02_MemoryBenchmarkDemo.cs`)
3. **Runs the Quick benchmark with Config A: SlidingWindow(10)**
   - Agent created with `includeHistory: true` and a system prompt mentioning "sliding window of 10 messages"
   - Saves baseline with `AgentBenchmarkConfig { ReducerStrategy = "SlidingWindow(10)" }`
4. **Runs the Quick benchmark with Config B: "Full History"**
   - Agent created with `includeHistory: true` (no reducer limitation in system prompt)
   - Saves baseline with `AgentBenchmarkConfig { ReducerStrategy = "FullHistory" }`
5. **Compares the two baselines** using `BaselineComparer`
6. **Prints a console comparison table** showing per-dimension scores and deltas
7. **Prints the output folder path** so the user can open `report.html`

**Console output example:**
```
═══════════════════════════════════════════════════════════════════
📊 AgentEval Memory — Benchmark Reporting & Comparison
═══════════════════════════════════════════════════════════════════

📝 Running benchmark with Config A: SlidingWindow(10)...
   Overall: 62.3%  Grade: C  ★★★☆☆

📝 Running benchmark with Config B: Full History...
   Overall: 81.7%  Grade: B  ★★★★☆

📊 Comparison:
   ┌──────────────┬──────────┬──────────┬──────────────────┐
   │ Dimension    │ Config A │ Config B │ Winner           │
   ├──────────────┼──────────┼──────────┼──────────────────┤
   │ Recall       │    58.0% │    89.0% │ B wins (+31.0%)  │
   │ Resilience   │    45.0% │    72.0% │ B wins (+27.0%)  │
   │ Temporal     │    67.5% │    79.0% │ B wins (+11.5%)  │
   ├──────────────┼──────────┼──────────┼──────────────────┤
   │ OVERALL      │    62.3% │    81.7% │ B wins (+19.4%)  │
   └──────────────┴──────────┴──────────┴──────────────────┘

📁 Report saved to: .agenteval/benchmarks/MemoryAgent/
   Open report.html in a browser (serve with: python -m http.server 8080)
```

**Key implementation details:**
- Use the same `AIConfig.IsConfigured` check as other samples
- The two agent configs differ in their system prompt (one mentions limited window, other doesn't)
- Both use `chatClient.AsEvaluableAgent(includeHistory: true)` — the actual memory difference comes from the system prompt and how the LLM interprets it
- Export bridge is demonstrated: `result.ToEvaluationReport()` printed as JSON snippet
- Baselines saved to `.agenteval/benchmarks/MemoryAgent/`

### Task 9.2: Register Sample in Program.cs

**File:** `samples/AgentEval.Samples/Program.cs` (EXISTING — modify)

Add the new sample to the Memory Evaluation group (Group G):
```csharp
new("Benchmark Reporting", "Run benchmarks, save baselines, compare configs, generate HTML report", MemoryBenchmarkReporting.RunAsync),
```

This becomes the 6th entry in the Memory Evaluation group.

### Checkpoint 9: Build + Run Sample (Manual)

```bash
dotnet build samples/AgentEval.Samples/AgentEval.Samples.csproj
# Manual test: dotnet run --project samples/AgentEval.Samples -- G6
# (requires Azure OpenAI credentials)
```

**Expected:** Sample compiles. With credentials, it runs two benchmarks, saves baselines, and prints comparison.

---

## Phase 10: Final Verification & Review

### Task 10.1: Full Solution Build

```bash
dotnet build
```

**Expected:** 0 errors, 0 warnings across ALL projects.

### Task 10.2: Full Test Suite

```bash
dotnet test
```

**Expected:** All tests pass across all TFMs (net8.0, net9.0, net10.0).

### Task 10.3: Review Checklist

- [ ] **Models (7 new files):** `CategoryScoreEntry`, `AgentBenchmarkConfig`, `BenchmarkExecutionInfo`, `MemoryBaseline`, `RadarChartData`, `BaselineComparison`, `BenchmarkManifest` — all in `Models/`
- [ ] **Reporting (7 new files):** `PentagonConsolidator`, `BaselineExtensions`, `IBaselineStore`, `IBaselineComparer`, `JsonFileBaselineStore`, `BaselineComparer`, `MemoryReportingOptions` — all in `Reporting/`
- [ ] **Extensions (1 new file):** `MemoryBenchmarkReportExtensions` — in `Extensions/`
- [ ] **Extensions (1 modified file):** `AgentEvalMemoryServiceCollectionExtensions` — updated with `AddAgentEvalMemoryReporting()`
- [ ] **Evaluators (1 modified file):** `MemoryBenchmarkRunner.cs` — scenario depth: preset name passed, multi-scenario per category, session reset between scenarios
- [ ] **Report (2 new files):** `report.html`, `archetypes.json` — embedded resources in `Report/`
- [ ] **Sample (1 new file):** `06_MemoryBenchmarkReporting.cs` — in `samples/AgentEval.Samples/MemoryEvaluation/`
- [ ] **Sample (1 modified file):** `Program.cs` — new entry in Group G
- [ ] **Tests:** ReportingModelTests, PentagonConsolidator, BaselineExtensions, MemoryBenchmarkReportExtensions, BenchmarkRunnerDepth (added to existing), EmbeddedResources, JsonFileBaselineStore, BaselineComparer, DI (added to existing), EndToEnd
- [ ] **Deleted:** `src/AgentEval.Memory/Samples/` folder (stray empty project)
- [ ] **Modified:** `AgentEval.Memory.csproj` — embedded resources added
- [ ] **.csproj no other changes** — no new NuGet dependencies needed
- [ ] **Only existing file modified with logic changes:** `MemoryBenchmarkRunner.cs` (additive — existing Quick behavior preserved, Standard/Full get additional scenarios)
- [ ] **All existing tests pass** — no regressions (Quick preset behavior unchanged)

### Task 10.4: File Count Verification

**Before implementation:** 42 .cs files in AgentEval.Memory
**After implementation:** 42 + 7 (Models) + 7 (Reporting) + 1 (Extensions) = **57 .cs files** in src
Plus 2 embedded resources (`report.html`, `archetypes.json`) in `Report/`
Plus 8 new test files in `tests/AgentEval.Memory.Tests/` (plus additions to 1 existing DI test file)
Plus 1 new sample file in `samples/AgentEval.Samples/MemoryEvaluation/`
Minus 1 deleted stray folder (`src/AgentEval.Memory/Samples/`)

---

## Summary: Implementation Order

| Phase | Task | Files Created/Modified | Tests Added | Depends On |
|-------|------|----------------------|-------------|-----------|
| 0 | Delete stray Samples project | Delete 1 folder | — | — |
| 1.1 | CategoryScoreEntry + StochasticData | 1 new model | — | — |
| 1.2 | AgentBenchmarkConfig (with ConfigurationId) | 1 new model | — | — |
| 1.3 | BenchmarkExecutionInfo | 1 new model | — | — |
| 1.4 | MemoryBaseline | 1 new model | — | 1.1, 1.2, 1.3 |
| 1.5 | RadarChartData + RadarChartSeries | 1 new model | — | — |
| 1.6 | BaselineComparison + DimensionComparison | 1 new model | — | 1.4, 1.5 |
| 1.7 | BenchmarkManifest (4 classes) | 1 new model | — | — |
| 1.8 | Model unit tests (ConfigId, JSON round-trip, snake_case) | 1 new test file | 11 tests | 1.1–1.7 |
| **CP1** | **Build + Test** | — | — | — |
| 2.1 | PentagonConsolidator | 1 new class | 6 tests | 1.x |
| 2.2 | BaselineExtensions (.ToBaseline()) | 1 new class | 9 tests | 2.1 |
| **CP2** | **Build + Test** | — | — | — |
| 3.1 | MemoryBenchmarkReportExtensions (.ToEvaluationReport()) | 1 new class | 8 tests | — |
| **CP3** | **Build + Test** | — | — | — |
| 4.1 | Enrich MemoryBenchmarkRunner (scenario depth) | 1 modified | — | — |
| 4.2 | Benchmark runner depth tests | 1 modified test file | 8 tests | 4.1 |
| **CP4** | **Build + Test** | — | — | — |
| 5.1 | report.html (from prototype, pentagon + dynamic) | 1 new file | — | — |
| 5.2 | archetypes.json (6 memory-architecture archetypes) | 1 new file | — | — |
| 5.3 | .csproj EmbeddedResource config | 1 modified | — | 5.1, 5.2 |
| 5.4 | Embedded resource accessibility tests | 1 new test file | 3 tests | 5.3 |
| **CP5** | **Build + Test** | — | — | — |
| 6.1 | IBaselineStore | 1 new interface | — | 1.4 |
| 6.2 | IBaselineComparer | 1 new interface | — | 1.6 |
| 6.3 | MemoryReportingOptions | 1 new class | — | — |
| 6.4 | JsonFileBaselineStore | 1 new class | 16 tests | 6.1, 6.3, 5.3 |
| 6.5 | BaselineComparer | 1 new class | 6 tests | 6.2, 2.1 |
| **CP6** | **Build + Full Memory Test Suite** | — | — | — |
| 7.1 | DI registration (AddAgentEvalMemoryReporting) | 1 modified | 5 tests | 6.x |
| **CP7** | **Build + Test** | — | — | — |
| 8.1 | End-to-end integration tests | 1 new test file | 7 tests | All above |
| **CP8** | **Full solution `dotnet build` + `dotnet test`** | — | — | — |
| 9.1 | Sample: 06_MemoryBenchmarkReporting.cs | 1 new file | — | All above |
| 9.2 | Program.cs — register in Group G | 1 modified | — | 9.1 |
| **CP9** | **Build samples project + manual run** | — | — | — |
| 10.x | Final verification + review checklist | — | — | All |

**Total new tests:** ~80 (11 + 6 + 9 + 8 + 8 + 3 + 16 + 6 + 5 + 7 + 1 DI integration)
**Total new .cs files:** 16 (7 models + 7 reporting + 1 extension + 1 model test file)
**Total modified files:** 5 (MemoryBenchmarkRunner.cs, .csproj, DI registration, Program.cs, existing benchmark runner test file)
**Total new non-.cs files:** 2 (report.html, archetypes.json)
**Total deleted:** 1 folder (stray Samples/)
**Total new test files:** 7 (ReportingModelTests, PentagonConsolidatorTests, BaselineExtensionsTests, MemoryBenchmarkReportExtensionsTests, EmbeddedResourceTests, JsonFileBaselineStoreTests, EndToEndReportingTests — plus additions to existing DI test file and existing MemoryBenchmarkRunnerTests file)

---

## Risk Mitigation

| Risk | Mitigation |
|------|-----------|
| Embedded resource names don't match expected paths | Task 4.4 includes 3 tests that verify resource accessibility using `GetManifestResourceNames()` + `.EndsWith()` — robust against namespace variations. |
| JSON serialization produces wrong casing | `JsonNamingPolicy.SnakeCaseLower` configured. Task 1.8 tests verify JSON round-trip before anything depends on serialization. |
| `IReadOnlyDictionary`/`IReadOnlyList` can't deserialize | Task 1.8 JSON round-trip test catches this in Phase 1. Fix: ensure `JsonSerializerOptions` can handle these types (may need concrete types in deserialization or custom converters). |
| `ConfigurationId` hash collisions | SHA256 + 12 hex chars = 48-bit collision space. Practically impossible for <1000 configs. Task 1.8 tests determinism. |
| File system tests flaky on Windows | Use `Path.GetTempPath()` + unique subfolder per test. `IDisposable` cleanup in test fixture. |
| Existing tests break from model namespace changes | All new models are in `AgentEval.Memory.Models` (existing namespace). No namespace changes. No existing file modified except DI registration and .csproj. |
| report.html doesn't work when opened as file:// | Documented: needs a local HTTP server for `fetch()` calls. Sample prints the `python -m http.server` command. |
| Large manifest with 100+ baselines | Manifest stores only summary data (Id, Name, Score, Grade, ConfigId, Tags). Individual baselines fetched on demand by the report. |
| `AgentEval.Samples.csproj` missing project references for new types | Verified: already references both `AgentEval.Memory` and `AgentEval.DataLoaders`. No .csproj changes needed for the sample. |
| Test project missing references | Verified: `AgentEval.Memory.Tests.csproj` references `AgentEval.Memory` + `AgentEval.Core` (which transitively includes Abstractions). `EvaluationReport` is in Abstractions, so the export bridge tests work. |
| `AgentEval.Memory.csproj` can't see `EvaluationReport` for export bridge | Verified: `.csproj` directly references `AgentEval.Abstractions` (line 15). `EvaluationReport` lives in `AgentEval.Models` namespace inside Abstractions. Task 3.1 will compile. |
| Grade system divergence (5 grades vs 13 grades) | **Resolved:** Plan uses 5-grade system (A/B/C/D/F) matching `MemoryBenchmarkResult.Grade` exactly. The `ComputeGrade()` helper in Task 2.2 uses identical thresholds. Task 2.2 test case 6 explicitly verifies no divergence. |

---

## Dependency Chain (Visual)

```
Phase 0: Housekeeping ───────────────────────────────────────── (independent)
           │
Phase 1: Models (7 files + tests) ──────────────────────────── (foundation)
           │
     ┌─────┼──────────┐
     │     │          │
Phase 2: Phase 3:   Phase 4:
Pentagon Export     Scenario Depth
+ Ext.   Bridge    (enrich runner)
     │     │          │   ← all three are independent of each other
     │     │          │
Phase 5: Embedded Resources ────────────────────────────────── (must exist before store)
     │
Phase 6: Persistence (store + comparer) ────────────────────── (uses Phase 2 + Phase 5)
     │
Phase 7: DI Registration ──────────────────────────────────── (wires Phase 6)
     │
Phase 8: Integration Tests ────────────────────────────────── (validates all above)
     │
Phase 9: Sample Application ───────────────────────────────── (demonstrates all above)
     │
Phase 10: Final Review ────────────────────────────────────── (verify everything)
```

Every phase builds only on completed, tested phases. No forward references. No circular dependencies. Each checkpoint verifies that the phase's outputs are correct before the next phase begins.

---

*This plan is designed to be executed task-by-task with zero ambiguity. Each task has a clear input, output, and verification step. Build and test after every phase. No task depends on something not yet built. Rock on.* 🎸
