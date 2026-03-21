# AgentEval Memory Benchmark — Implementation Plan

**Date:** March 21, 2026
**Reference:** Memory-Benchmark-Assessment-and-Roadmap.md (decisions), Memory-Benchmark-Reporting-Proposals.md (visual design), agenteval-memory-benchmark-report.html (prototype)
**Starting Point:** 42 .cs files in AgentEval.Memory, 0 reporting/baseline classes, 0 embedded resources, all evaluators working, 348 unit tests passing
**Goal:** Implement the complete reporting, baseline persistence, export bridge, and HTML report system

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

### Checkpoint 1: Build Verification

```bash
dotnet build src/AgentEval.Memory/AgentEval.Memory.csproj
```

**Expected:** 0 errors. All 7 new files compile. No test changes needed — these are pure data models with no logic.

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

**Grade computation helper** (private, in BaselineExtensions):
```csharp
private static string ComputeGrade(double score) => score switch
{
    >= 95 => "A+", >= 90 => "A", >= 85 => "A-",
    >= 80 => "B+", >= 75 => "B", >= 70 => "B-",
    >= 65 => "C+", >= 60 => "C", >= 55 => "C-",
    >= 50 => "D+", >= 45 => "D", >= 40 => "D-",
    _ => "F"
};
```

**Test file:** `tests/AgentEval.Memory.Tests/Reporting/BaselineExtensionsTests.cs`

**Test cases:**
1. Basic conversion — all fields populated correctly
2. ConfigurationId matches config's computed ID
3. DimensionScores computed correctly via PentagonConsolidator
4. Skipped categories preserved in CategoryResults
5. Tags default to empty list when not provided
6. Grade computation matches MemoryBenchmarkResult.Grade
7. Unique ID generated each call (no collisions)

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

## Phase 4: Persistence (IBaselineStore + JsonFileBaselineStore)

### Task 4.1: Create IBaselineStore.cs

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

### Task 4.2: Create IBaselineComparer.cs

**File:** `src/AgentEval.Memory/Reporting/IBaselineComparer.cs`
**Namespace:** `AgentEval.Memory.Reporting`

```csharp
public interface IBaselineComparer
{
    BaselineComparison Compare(IReadOnlyList<MemoryBaseline> baselines);
}
```

### Task 4.3: Create MemoryReportingOptions.cs

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

### Task 4.4: Create JsonFileBaselineStore.cs

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

### Task 4.5: Create BaselineComparer.cs

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

### Checkpoint 4: Build + Full Test Suite

```bash
dotnet build src/AgentEval.Memory/AgentEval.Memory.csproj
dotnet test tests/AgentEval.Memory.Tests/AgentEval.Memory.Tests.csproj
```

**Expected:** 0 errors. All new tests pass. All existing 348 tests still pass.

---

## Phase 5: Embedded Resources (HTML Report + Archetypes)

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

**Verify:**
- `dotnet build` succeeds
- Embedded resources are accessible via `Assembly.GetManifestResourceStream()`
- Resource names are correct: `AgentEval.Report.report.html`, `AgentEval.Report.archetypes.json` (because `RootNamespace=AgentEval`)

### Checkpoint 5: Build Verification

```bash
dotnet build src/AgentEval.Memory/AgentEval.Memory.csproj
```

**Expected:** 0 errors. Embedded resources included in assembly.

**Manual verification:** Write a quick test that loads the embedded resources and verifies they're non-empty:
```csharp
[Fact]
public void EmbeddedResources_AreAccessible()
{
    var assembly = typeof(MemoryBaseline).Assembly;
    var reportStream = assembly.GetManifestResourceStream("AgentEval.Report.report.html");
    Assert.NotNull(reportStream);
    Assert.True(reportStream.Length > 0);

    var archetypeStream = assembly.GetManifestResourceStream("AgentEval.Report.archetypes.json");
    Assert.NotNull(archetypeStream);
    Assert.True(archetypeStream.Length > 0);
}
```

---

## Phase 6: DI Registration

### Task 6.1: Add AddAgentEvalMemoryReporting() to DI

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

### Checkpoint 6: Build + Full Test Suite

```bash
dotnet build src/AgentEval.Memory/AgentEval.Memory.csproj
dotnet test tests/AgentEval.Memory.Tests/AgentEval.Memory.Tests.csproj
```

**Expected:** 0 errors. All tests pass (existing + new DI tests).

---

## Phase 7: Full Integration Test

### Task 7.1: End-to-End Integration Test

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

### Checkpoint 7: Full Solution Build + All Tests

```bash
dotnet build
dotnet test
```

**Expected:** 0 errors across entire solution. All Memory tests pass. All Core tests pass. No regressions.

---

## Phase 8: Sample Application

### Task 8.1: Create Benchmark Reporting Sample

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
📊 AgentEval Memory - Sample 33: Benchmark Reporting & Comparison
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

### Task 8.2: Register Sample in Program.cs

**File:** `samples/AgentEval.Samples/Program.cs` (EXISTING — modify)

Add the new sample to the Memory Evaluation group (Group G):
```csharp
new("Benchmark Reporting", "Run benchmarks, save baselines, compare configs, generate HTML report", MemoryBenchmarkReporting.RunAsync),
```

This becomes the 6th entry in the Memory Evaluation group.

### Checkpoint 8: Build + Run Sample (Manual)

```bash
dotnet build samples/AgentEval.Samples/AgentEval.Samples.csproj
# Manual test: dotnet run --project samples/AgentEval.Samples -- G6
# (requires Azure OpenAI credentials)
```

**Expected:** Sample compiles. With credentials, it runs two benchmarks, saves baselines, and prints comparison.

---

## Phase 9: Final Verification

### Task 9.1: Full Solution Build

```bash
dotnet build
```

**Expected:** 0 errors, 0 warnings across ALL projects.

### Task 9.2: Full Test Suite

```bash
dotnet test
```

**Expected:** All tests pass across all TFMs (net8.0, net9.0, net10.0).

### Task 9.3: Review Checklist

- [ ] **Models (7 files):** `CategoryScoreEntry`, `AgentBenchmarkConfig`, `BenchmarkExecutionInfo`, `MemoryBaseline`, `RadarChartData`, `BaselineComparison`, `BenchmarkManifest` — all in `Models/`
- [ ] **Reporting (7 files):** `PentagonConsolidator`, `BaselineExtensions`, `IBaselineStore`, `IBaselineComparer`, `JsonFileBaselineStore`, `BaselineComparer`, `MemoryReportingOptions` — all in `Reporting/`
- [ ] **Extensions (1 new file):** `MemoryBenchmarkReportExtensions` — in `Extensions/`
- [ ] **Extensions (1 modified file):** `AgentEvalMemoryServiceCollectionExtensions` — updated with `AddAgentEvalMemoryReporting()`
- [ ] **Report (2 files):** `report.html`, `archetypes.json` — embedded resources in `Report/`
- [ ] **Sample (1 new file):** `06_MemoryBenchmarkReporting.cs` — in `samples/AgentEval.Samples/MemoryEvaluation/`
- [ ] **Sample (1 modified file):** `Program.cs` — new entry in Group G
- [ ] **Tests:** PentagonConsolidator, BaselineExtensions, MemoryBenchmarkReportExtensions, JsonFileBaselineStore, BaselineComparer, DI, EndToEnd, EmbeddedResources
- [ ] **Deleted:** `src/AgentEval.Memory/Samples/` folder (stray empty project)
- [ ] **Modified:** `AgentEval.Memory.csproj` — embedded resources added
- [ ] **.csproj no other changes** — no new NuGet dependencies needed
- [ ] **No changes to existing code** — all new code is additive
- [ ] **All existing tests pass** — no regressions

### Task 9.4: File Count Verification

**Before implementation:** 42 .cs files in AgentEval.Memory
**After implementation:** 42 + 7 (Models) + 7 (Reporting) + 1 (Extensions) = **57 .cs files**
Plus 2 embedded resources (`report.html`, `archetypes.json`)
Plus 1 new sample file
Minus 1 deleted stray .csproj

---

## Summary: Implementation Order

| Order | Task | Files Created/Modified | Tests Added | Builds On |
|-------|------|----------------------|-------------|-----------|
| 0 | Delete stray Samples project | Delete 1 folder | — | — |
| 1.1 | CategoryScoreEntry | 1 new model | — | — |
| 1.2 | AgentBenchmarkConfig | 1 new model | — | — |
| 1.3 | BenchmarkExecutionInfo | 1 new model | — | — |
| 1.4 | MemoryBaseline | 1 new model | — | 1.1, 1.2, 1.3 |
| 1.5 | RadarChartData | 1 new model | — | — |
| 1.6 | BaselineComparison | 1 new model | — | 1.4, 1.5 |
| 1.7 | BenchmarkManifest | 1 new model | — | — |
| **CP1** | **Build verification** | — | — | — |
| 2.1 | PentagonConsolidator | 1 new class | 6 tests | 1.x |
| 2.2 | BaselineExtensions | 1 new class | 7 tests | 2.1 |
| **CP2** | **Build + Test** | — | — | — |
| 3.1 | MemoryBenchmarkReportExtensions | 1 new class | 8 tests | — |
| **CP3** | **Build + Test** | — | — | — |
| 4.1 | IBaselineStore | 1 new interface | — | 1.4 |
| 4.2 | IBaselineComparer | 1 new interface | — | 1.6 |
| 4.3 | MemoryReportingOptions | 1 new class | — | — |
| 4.4 | JsonFileBaselineStore | 1 new class | 16 tests | 4.1, 4.3, 5.3 |
| 4.5 | BaselineComparer | 1 new class | 6 tests | 4.2, 2.1 |
| **CP4** | **Build + Full Test Suite** | — | — | — |
| 5.1 | report.html | 1 new file | — | — |
| 5.2 | archetypes.json | 1 new file | — | — |
| 5.3 | .csproj embedded resources | 1 modified | 1 test | 5.1, 5.2 |
| **CP5** | **Build verification** | — | — | — |
| 6.1 | DI registration | 1 modified | 5 tests | 4.x |
| **CP6** | **Build + Test** | — | — | — |
| 7.1 | End-to-end integration test | — | 7 tests | All |
| **CP7** | **Full solution build + all tests** | — | — | — |
| 8.1 | Sample: MemoryBenchmarkReporting | 1 new file | — | All |
| 8.2 | Program.cs registration | 1 modified | — | 8.1 |
| **CP8** | **Build + manual sample test** | — | — | — |
| 9.x | Final verification | — | — | All |

**Total new tests:** ~56 (6 + 7 + 8 + 16 + 6 + 1 + 5 + 7)
**Total new .cs files:** 15
**Total new non-.cs files:** 2 (report.html, archetypes.json)
**Total modified files:** 3 (.csproj, DI registration, Program.cs)
**Total deleted:** 1 folder (stray Samples/)

---

## Risk Mitigation

| Risk | Mitigation |
|------|-----------|
| Embedded resource names don't match expected paths | Task 5.3 includes a test that verifies resource accessibility. Check `RootNamespace` in .csproj matches resource path prefix. |
| JSON serialization produces wrong casing | `JsonNamingPolicy.SnakeCaseLower` configured. Tests verify actual JSON output. |
| `ConfigurationId` hash collisions | SHA256 + 12 hex chars = 48-bit collision space. Practically impossible for <1000 configs. |
| File system tests flaky on Windows | Use `Path.GetTempPath()` + unique subfolder per test. `IDisposable` cleanup in test fixture. |
| Existing tests break from model namespace changes | All new models are in `AgentEval.Memory.Models` (existing namespace). No namespace changes. |
| report.html doesn't work when opened as file:// | Documented: needs a local HTTP server for fetch() calls. Sample prints the serve command. |
| Large manifest with 100+ baselines | Manifest stores only summary data (Id, Name, Score, Grade, ConfigId, Tags). Individual baselines fetched on demand. |

---

*This plan is designed to be executed task-by-task with zero ambiguity. Each task has a clear input, output, and verification step. Build and test after every phase. No task depends on something not yet built. Rock on.* 🎸
