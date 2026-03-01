# Export Formats

AgentEval includes a complete export system for evaluation results, designed for CI/CD integration, reporting, and analysis.

## Overview

All exporters implement the `IResultExporter` interface and can be created via the `ResultExporterFactory`:

```csharp
using AgentEval.Exporters;

// Create by format enum
var exporter = ResultExporterFactory.Create(ExportFormat.Junit);

// Or by file extension
var exporter = ResultExporterFactory.CreateFromExtension(".json");
```

## Available Formats

| Format | Extension | Use Case | ContentType |
|--------|-----------|----------|-------------|
| **JSON** | `.json` | Programmatic access, dashboards, APIs | `application/json` |
| **JUnit XML** | `.xml` | CI/CD (GitHub Actions, Azure DevOps, Jenkins) | `application/xml` |
| **Markdown** | `.md` | PR comments, documentation, GitHub rendering | `text/markdown` |
| **TRX** | `.trx` | Visual Studio Test Explorer, Azure DevOps | `application/xml` |
| **CSV** | `.csv` | Excel, Power BI, business intelligence tools | `text/csv` |
| **Directory** | *(dir)* | Cross-run comparison, history, reproducibility (ADR-002) | `application/x-directory` |

## Quick Start

```csharp
// 1. Build an EvaluationReport
var report = new EvaluationReport
{
    Name = "Agent Quality Check",
    TotalTests = 10,
    PassedTests = 8,
    FailedTests = 2,
    OverallScore = 82.5,
    StartTime = DateTimeOffset.UtcNow.AddSeconds(-30),
    EndTime = DateTimeOffset.UtcNow,
    Agent = new AgentInfo { Name = "CustomerBot", Model = "gpt-4o" },
    TestResults = results // List<TestResultSummary>
};

// 2. Export to any format
var exporter = ResultExporterFactory.Create(ExportFormat.Junit);
await using var stream = File.Create("results.xml");
await exporter.ExportAsync(report, stream);
```

## Format Details

### JSON

Structured JSON with camelCase naming. Ideal for programmatic consumption.

```csharp
var exporter = ResultExporterFactory.Create(ExportFormat.Json);
await using var stream = File.Create("results.json");
await exporter.ExportAsync(report, stream);

// Or export to string directly
var jsonExporter = new JsonExporter();
var json = await jsonExporter.ExportToStringAsync(report);
```

Output includes `runId`, `stats`, `overallScore`, `agent` info, and each test result with optional `metricScores`.

### JUnit XML

Standard JUnit XML format compatible with all major CI/CD systems:

- **GitHub Actions**: `dorny/test-reporter@v1`, `EnricoMi/publish-unit-test-result-action`
- **Azure DevOps**: `PublishTestResults@2` with `testResultsFormat: 'JUnit'`
- **Jenkins**: Built-in JUnit plugin
- **GitLab CI**: `artifacts:reports:junit`
- **CircleCI**: `store_test_results`

```csharp
var exporter = ResultExporterFactory.Create(ExportFormat.Junit);
await using var stream = File.Create("results.xml");
await exporter.ExportAsync(report, stream);
```

Tests are grouped by category into `<testsuite>` elements. Failed tests include `<failure>` elements with score and error details. Metric scores are written to `<system-out>`.

### Markdown

GitHub-flavored Markdown with tables, emoji status indicators, and configurable sections.

```csharp
var mdExporter = new MarkdownExporter
{
    Options = new MarkdownExportOptions
    {
        FailuresFirst = true,          // Show failures at top
        IncludeFailureDetails = true,  // Detailed failure section
        IncludeMetricBreakdown = true, // Dynamic metric table
        IncludeFooter = true           // Run ID + timestamp
    }
};

// Export to string (ideal for PR comments)
var markdown = mdExporter.ExportToString(report);

// Or export to stream
await using var stream = File.Create("results.md");
await mdExporter.ExportAsync(report, stream);
```

The Markdown exporter renders:
- Status header with ✅/❌ emoji
- Results table with score, status, and duration
- Optional failure details section
- Optional metric breakdown table (dynamic columns from `MetricScores`)
- Footer with run ID and timestamp

### TRX

Visual Studio TRX format — native for .NET tooling and Azure DevOps.

```csharp
var exporter = ResultExporterFactory.Create(ExportFormat.Trx);
await using var stream = File.Create("results.trx");
await exporter.ExportAsync(report, stream);
```

Uses deterministic GUIDs based on test names for reproducible output. Includes full TRX structure: `TestRun`, `Times`, `ResultSummary`, `TestDefinitions`, `TestEntries`, and `Results`.

### CSV

Comma-separated values optimized for Excel and business intelligence tools.

```csharp
var exporter = ResultExporterFactory.Create(ExportFormat.Csv);
await using var stream = File.Create("results.csv");
await exporter.ExportAsync(report, stream);

// Or export to string
var csvExporter = new CsvExporter();
var csv = await csvExporter.ExportToStringAsync(report);
```

Fixed columns: `RunId`, `TestName`, `Category`, `Score`, `Passed`, `Skipped`, `DurationMs`, `Error`, `AgentName`, `AgentModel`.

Dynamic columns are appended for each unique key in `MetricScores` (e.g., `relevance`, `correctness`). Special characters (commas, quotes, newlines) are properly escaped per RFC 4180.

### Directory (ADR-002)

Structured directory format for cross-run comparison, history tracking, and reproducibility. Produces multiple files per run instead of a single file.

> **ADR:** [002-result-directory-structure.md](adr/002-result-directory-structure.md)

```csharp
var exporter = new DirectoryExporter();
await exporter.ExportToDirectoryAsync(report, "./results/baseline");

// Or use the auto-generated directory name
var dirName = DirectoryExporter.GenerateDirectoryName(report);
// e.g., "2026-03-01_14-30-00_gpt-4o"
await exporter.ExportToDirectoryAsync(report, $"./results/{dirName}");
```

**CLI usage:**
```bash
agenteval eval --azure --model gpt-4o --dataset tests.yaml --output-dir ./results
```

Each run produces a directory with:

| File | Format | Purpose |
|------|--------|---------|
| `results.jsonl` | JSON Lines | One JSON line per test result (streaming-friendly, append-friendly) |
| `summary.json` | JSON | Aggregate statistics with per-metric distribution (mean, min, max, stddev, percentiles) |
| `run.json` | JSON | Run metadata: agent info, environment, timestamp, duration |
| `config.json` | JSON | Copy of original config file (when provided, for reproducibility) |

The stream-based `ExportAsync` method writes `summary.json` content for `IResultExporter` compatibility. For full directory output, use `ExportToDirectoryAsync`.

## The IResultExporter Interface

```csharp
public interface IResultExporter
{
    ExportFormat Format { get; }
    string FormatName => Format.ToString();  // Default interface member
    string FileExtension { get; }
    string ContentType { get; }
    Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct = default);
}
```

The `FormatName` property is a default interface member that returns the enum name for built-in exporters. Custom exporters can override it to provide a meaningful string name for registry lookup.

### Creating Custom Exporters

Implement `IResultExporter` to add new formats. Use the `FormatName` property for registry identification:

```csharp
public class SarifExporter : IResultExporter
{
    public ExportFormat Format => ExportFormat.Json; // Closest built-in fallback
    public string FormatName => "sarif";             // Custom name for registry lookup
    public string FileExtension => ".sarif";
    public string ContentType => "application/sarif+json";

    public async Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct = default)
    {
        // Your serialization logic here
    }
}
```

### Using `IExporterRegistry` (DI-Friendly)

The `IExporterRegistry` provides dynamic exporter lookup and registration, analogous to `IMetricRegistry`. Register custom exporters via DI:

```csharp
// Register your custom exporter
services.AddSingleton<IResultExporter, SarifExporter>();
services.AddAgentEval(); // Auto-populates IExporterRegistry

// Resolve and use
var registry = serviceProvider.GetRequiredService<IExporterRegistry>();
var exporter = registry.GetRequired("sarif");
await exporter.ExportAsync(report, stream);

// List all available formats
foreach (var format in registry.GetRegisteredFormats())
{
    Console.WriteLine($"  {format}");
}
```

Built-in exporters (JSON, JUnit, Markdown, CSV, TRX, Directory) are pre-registered automatically. DI-registered exporters are added alongside them without overriding built-ins.

You can also register exporters manually at runtime:

```csharp
var registry = serviceProvider.GetRequiredService<IExporterRegistry>();
registry.Register("powerbi", new PowerBIExporter());
```

> **Note:** `ResultExporterFactory.Create()` and `ResultExporterFactory.CreateFromExtension()` continue to work for non-DI scenarios. The `IExporterRegistry` is the recommended DI-friendly alternative.

## The EvaluationReport Model

```csharp
var report = new EvaluationReport
{
    RunId = "auto-generated-8-char-hex",  // Auto-generated if not set
    Name = "Suite Name",
    StartTime = DateTimeOffset.UtcNow,
    EndTime = DateTimeOffset.UtcNow,
    TotalTests = 10,
    PassedTests = 8,
    FailedTests = 2,
    SkippedTests = 0,
    OverallScore = 85.0,
    Agent = new AgentInfo { Name = "Bot", Model = "gpt-4o" },
    Metadata = new() { ["environment"] = "staging" },
    TestResults = new List<TestResultSummary>
    {
        new()
        {
            Name = "tool_ordering_test",
            Category = "Agentic",
            Score = 95.0,
            Passed = true,
            DurationMs = 1200,
            MetricScores = new()
            {
                ["relevance"] = 92.5,
                ["correctness"] = 88.0
            }
        }
    }
};
```

Computed properties:
- `Duration` — calculated from `EndTime - StartTime`
- `PassRate` — calculated as percentage with zero-division protection

## CI/CD Integration

### GitHub Actions

```yaml
- name: Run AgentEval
  run: dotnet test --logger trx --logger "junit;LogFilePath=results.xml"

- name: Publish Results
  uses: dorny/test-reporter@v1
  with:
    name: AgentEval Results
    path: results.xml
    reporter: java-junit
```

### Azure DevOps

```yaml
- task: PublishTestResults@2
  inputs:
    testResultsFormat: 'JUnit'
    testResultsFiles: '**/results.xml'
```

## See Also

- [Step-by-Step Walkthrough](walkthrough.md) — Export results in Step 8
- [Rich Evaluation Output](rich-evaluation-output-guide.md) — Verbosity levels and trace files
- [Sample 11](https://github.com/joslat/AgentEval/blob/main/samples/AgentEval.Samples/Sample11_DatasetsAndExport.cs) — Complete export demo with all formats
- [Extensibility](extensibility.md) — Building custom plugins
- [Sample 26: Extensibility](https://github.com/joslat/AgentEval/blob/main/samples/AgentEval.Samples/Sample26_Extensibility.cs) — Custom exporter registration via DI
