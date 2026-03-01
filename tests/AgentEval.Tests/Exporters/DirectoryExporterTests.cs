// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Exporters;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Exporters;

/// <summary>
/// Tests for <see cref="DirectoryExporter"/> — ADR-002 structured directory export.
/// </summary>
public class DirectoryExporterTests : IDisposable
{
    private readonly string _tempDir;

    public DirectoryExporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "agenteval-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ─── Sample data ────────────────────────────────────────────────────

    private static EvaluationReport CreateSampleReport() => new()
    {
        RunId = "test1234",
        Name = "Baseline Suite",
        StartTime = new DateTimeOffset(2026, 3, 1, 14, 30, 0, TimeSpan.Zero),
        EndTime = new DateTimeOffset(2026, 3, 1, 14, 35, 32, TimeSpan.Zero),
        TotalTests = 3,
        PassedTests = 2,
        FailedTests = 1,
        OverallScore = 78.3,
        Agent = new AgentInfo { Name = "TestAgent", Model = "gpt-4o", Version = "2026-02-01" },
        TestResults = new List<TestResultSummary>
        {
            new()
            {
                Name = "greeting_test", Score = 95.0, Passed = true, DurationMs = 1200,
                Category = "Basic",
                MetricScores = new() { ["llm_relevance"] = 92, ["llm_faithfulness"] = 98 }
            },
            new()
            {
                Name = "knowledge_test", Score = 88.0, Passed = true, DurationMs = 2300,
                Category = "Knowledge",
                MetricScores = new() { ["llm_relevance"] = 85, ["llm_faithfulness"] = 91 }
            },
            new()
            {
                Name = "reasoning_test", Score = 45.0, Passed = false, DurationMs = 3100,
                Category = "Reasoning", Error = "Incomplete reasoning",
                MetricScores = new() { ["llm_relevance"] = 60, ["llm_faithfulness"] = 72 }
            }
        },
        Metadata = new() { ["temperature"] = "0.7", ["maxTokens"] = "1000" }
    };

    private static EvaluationReport CreateMinimalReport() => new()
    {
        RunId = "mini0001",
        TotalTests = 0,
        PassedTests = 0,
        FailedTests = 0,
        OverallScore = 0,
        TestResults = new()
    };

    // ─── IResultExporter conformance ───────────────────────────────

    [Fact]
    public void Format_ReturnsDirectory()
    {
        var exporter = new DirectoryExporter();

        Assert.Equal(ExportFormat.Directory, exporter.Format);
    }

    [Fact]
    public void FormatName_ReturnsDirectory()
    {
        var exporter = new DirectoryExporter();

        Assert.Equal("Directory", exporter.FormatName);
    }

    [Fact]
    public void FileExtension_ReturnsEmpty()
    {
        var exporter = new DirectoryExporter();

        Assert.Equal("", exporter.FileExtension);
    }

    [Fact]
    public void ContentType_ReturnsDirectoryMimeType()
    {
        var exporter = new DirectoryExporter();

        Assert.Equal("application/x-directory", exporter.ContentType);
    }

    [Fact]
    public async Task ExportAsync_StreamFallback_WritesSummaryJson()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);

        stream.Position = 0;
        var json = await new StreamReader(stream).ReadToEndAsync();

        Assert.Contains("\"runId\"", json);
        Assert.Contains("test1234", json);
        Assert.Contains("\"stats\"", json);
        Assert.Contains("\"passRate\"", json);
        Assert.Contains("\"metrics\"", json);
    }

    [Fact]
    public async Task ExportAsync_StreamFallback_ProducesValidJson()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();

        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);

        stream.Position = 0;
        var doc = JsonDocument.Parse(stream);
        Assert.Equal("test1234", doc.RootElement.GetProperty("runId").GetString());
        Assert.True(doc.RootElement.TryGetProperty("stats", out _));
        Assert.True(doc.RootElement.TryGetProperty("metrics", out _));
    }

    [Fact]
    public async Task ExportAsync_NullReport_Throws()
    {
        var exporter = new DirectoryExporter();
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => exporter.ExportAsync(null!, stream));
    }

    [Fact]
    public async Task ExportAsync_NullStream_Throws()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => exporter.ExportAsync(report, null!));
    }

    // ─── ExportToDirectoryAsync ─────────────────────────────────────

    [Fact]
    public async Task ExportToDirectoryAsync_CreatesDirectory()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "new-subdir");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        Assert.True(Directory.Exists(outputDir));
    }

    [Fact]
    public async Task ExportToDirectoryAsync_ProducesAllThreeFiles()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "run1");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        Assert.True(File.Exists(Path.Combine(outputDir, DirectoryExporter.ResultsFileName)));
        Assert.True(File.Exists(Path.Combine(outputDir, DirectoryExporter.SummaryFileName)));
        Assert.True(File.Exists(Path.Combine(outputDir, DirectoryExporter.RunFileName)));
    }

    [Fact]
    public async Task ExportToDirectoryAsync_WithConfig_CopiesConfigFile()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "run-with-config");

        // Create a fake config file with non-.json extension
        var configPath = Path.Combine(_tempDir, "my-config.yaml");
        await File.WriteAllTextAsync(configPath, "dataset:\n  - name: test1\n    input: hello");

        await exporter.ExportToDirectoryAsync(report, outputDir, configFilePath: configPath);

        // Original filename should be preserved (not renamed to config.json)
        var configDest = Path.Combine(outputDir, "my-config.yaml");
        Assert.True(File.Exists(configDest));
        var content = await File.ReadAllTextAsync(configDest);
        Assert.Contains("dataset:", content);
    }

    [Fact]
    public async Task ExportToDirectoryAsync_WithoutConfig_NoConfigFile()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "run-no-config");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        Assert.False(File.Exists(Path.Combine(outputDir, DirectoryExporter.ConfigFileName)));
    }

    [Fact]
    public async Task ExportToDirectoryAsync_WithNonexistentConfig_NoConfigFile()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "run-bad-config");

        await exporter.ExportToDirectoryAsync(report, outputDir, configFilePath: "/nonexistent/path.json");

        Assert.False(File.Exists(Path.Combine(outputDir, DirectoryExporter.ConfigFileName)));
    }

    // ─── results.jsonl ──────────────────────────────────────────────

    [Fact]
    public async Task ResultsJsonl_HasOneLinePerTest()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "jsonl-lines");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var lines = (await File.ReadAllLinesAsync(Path.Combine(outputDir, DirectoryExporter.ResultsFileName)))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task ResultsJsonl_EachLineIsValidJson()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "jsonl-valid");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var lines = (await File.ReadAllLinesAsync(Path.Combine(outputDir, DirectoryExporter.ResultsFileName)))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        foreach (var line in lines)
        {
            var doc = JsonDocument.Parse(line);
            Assert.NotNull(doc.RootElement.GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task ResultsJsonl_ContainsExpectedFields()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "jsonl-fields");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var firstLine = (await File.ReadAllLinesAsync(Path.Combine(outputDir, DirectoryExporter.ResultsFileName)))
            .First(l => !string.IsNullOrWhiteSpace(l));

        var doc = JsonDocument.Parse(firstLine);
        var root = doc.RootElement;

        Assert.Equal("greeting_test", root.GetProperty("name").GetString());
        Assert.True(root.GetProperty("passed").GetBoolean());
        Assert.Equal(95.0, root.GetProperty("score").GetDouble());
        Assert.Equal(1200, root.GetProperty("durationMs").GetInt64());
        Assert.Equal("Basic", root.GetProperty("category").GetString());
    }

    [Fact]
    public async Task ResultsJsonl_ContainsMetricScores()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "jsonl-metrics");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var firstLine = (await File.ReadAllLinesAsync(Path.Combine(outputDir, DirectoryExporter.ResultsFileName)))
            .First(l => !string.IsNullOrWhiteSpace(l));

        var doc = JsonDocument.Parse(firstLine);
        var metrics = doc.RootElement.GetProperty("metrics");

        Assert.Equal(92, metrics.GetProperty("llm_relevance").GetDouble());
        Assert.Equal(98, metrics.GetProperty("llm_faithfulness").GetDouble());
    }

    [Fact]
    public async Task ResultsJsonl_FailedTest_ContainsError()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "jsonl-error");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var lastLine = (await File.ReadAllLinesAsync(Path.Combine(outputDir, DirectoryExporter.ResultsFileName)))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Last();

        var doc = JsonDocument.Parse(lastLine);
        Assert.False(doc.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal("Incomplete reasoning", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ResultsJsonl_EmptyMetricScores_OmitsMetricsField()
    {
        var exporter = new DirectoryExporter();
        var report = new EvaluationReport
        {
            RunId = "nometric01",
            TotalTests = 1,
            PassedTests = 1,
            OverallScore = 80,
            TestResults = new List<TestResultSummary>
            {
                new() { Name = "no_metrics_test", Score = 80, Passed = true, DurationMs = 500 }
            }
        };
        var outputDir = Path.Combine(_tempDir, "jsonl-no-metrics");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var firstLine = (await File.ReadAllLinesAsync(Path.Combine(outputDir, DirectoryExporter.ResultsFileName)))
            .First(l => !string.IsNullOrWhiteSpace(l));

        var doc = JsonDocument.Parse(firstLine);
        // When MetricScores is empty, the "metrics" field should be omitted (WhenWritingNull)
        Assert.False(doc.RootElement.TryGetProperty("metrics", out _));
    }

    // ─── summary.json ───────────────────────────────────────────────

    [Fact]
    public async Task SummaryJson_HasCorrectStats()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "summary-stats");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var json = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.SummaryFileName));
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("test1234", root.GetProperty("runId").GetString());
        Assert.Equal("Baseline Suite", root.GetProperty("name").GetString());

        var stats = root.GetProperty("stats");
        Assert.Equal(3, stats.GetProperty("total").GetInt32());
        Assert.Equal(2, stats.GetProperty("passed").GetInt32());
        Assert.Equal(1, stats.GetProperty("failed").GetInt32());

        // PassRate should be ~0.6667
        var passRate = stats.GetProperty("passRate").GetDouble();
        Assert.InRange(passRate, 0.66, 0.67);
    }

    [Fact]
    public async Task SummaryJson_HasMetricAggregates()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "summary-metrics");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var json = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.SummaryFileName));
        var doc = JsonDocument.Parse(json);
        var metrics = doc.RootElement.GetProperty("metrics");

        // llm_relevance: [92, 85, 60] → mean ≈ 79, min = 60, max = 92
        var relevance = metrics.GetProperty("llm_relevance");
        Assert.Equal(3, relevance.GetProperty("sampleSize").GetInt32());
        Assert.Equal(60, relevance.GetProperty("min").GetDouble());
        Assert.Equal(92, relevance.GetProperty("max").GetDouble());
        Assert.InRange(relevance.GetProperty("mean").GetDouble(), 78, 80);

        // llm_faithfulness: [98, 91, 72] → mean ≈ 87, min = 72, max = 98
        var faithfulness = metrics.GetProperty("llm_faithfulness");
        Assert.Equal(3, faithfulness.GetProperty("sampleSize").GetInt32());
        Assert.Equal(72, faithfulness.GetProperty("min").GetDouble());
        Assert.Equal(98, faithfulness.GetProperty("max").GetDouble());
        Assert.InRange(faithfulness.GetProperty("mean").GetDouble(), 86, 88);
    }

    [Fact]
    public async Task SummaryJson_MetricAggregates_HaveStdDevAndPercentiles()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "summary-percentiles");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var json = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.SummaryFileName));
        var doc = JsonDocument.Parse(json);
        var relevance = doc.RootElement.GetProperty("metrics").GetProperty("llm_relevance");

        // stdDev should be > 0 for [92, 85, 60]
        Assert.True(relevance.GetProperty("stdDev").GetDouble() > 0);
        // p50 (median) should be 85 for sorted [60, 85, 92]
        Assert.Equal(85, relevance.GetProperty("p50").GetDouble());
        // p95 and p99 should exist
        Assert.True(relevance.TryGetProperty("p95", out _));
        Assert.True(relevance.TryGetProperty("p99", out _));
    }

    [Fact]
    public async Task SummaryJson_HasOverallScore()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "summary-score");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var json = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.SummaryFileName));
        var doc = JsonDocument.Parse(json);

        Assert.Equal(78.3, doc.RootElement.GetProperty("overallScore").GetDouble());
    }

    // ─── run.json ───────────────────────────────────────────────────

    [Fact]
    public async Task RunJson_HasAgentInfo()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "run-agent");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var json = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.RunFileName));
        var doc = JsonDocument.Parse(json);
        var agent = doc.RootElement.GetProperty("agent");

        Assert.Equal("TestAgent", agent.GetProperty("name").GetString());
        Assert.Equal("gpt-4o", agent.GetProperty("model").GetString());
        Assert.Equal("2026-02-01", agent.GetProperty("version").GetString());
    }

    [Fact]
    public async Task RunJson_HasEnvironmentInfo()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "run-env");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var json = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.RunFileName));
        var doc = JsonDocument.Parse(json);
        var env = doc.RootElement.GetProperty("environment");

        // These should be populated from the runtime
        Assert.False(string.IsNullOrEmpty(env.GetProperty("machine").GetString()));
        Assert.False(string.IsNullOrEmpty(env.GetProperty("os").GetString()));
        Assert.False(string.IsNullOrEmpty(env.GetProperty("dotnetVersion").GetString()));
    }

    [Fact]
    public async Task RunJson_HasTimestampAndDuration()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "run-time");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var json = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.RunFileName));
        var doc = JsonDocument.Parse(json);

        Assert.Equal("test1234", doc.RootElement.GetProperty("runId").GetString());
        Assert.True(doc.RootElement.TryGetProperty("timestamp", out _));
        Assert.Equal("00:05:32", doc.RootElement.GetProperty("duration").GetString());
    }

    [Fact]
    public async Task RunJson_HasParameters()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "run-params");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var json = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.RunFileName));
        var doc = JsonDocument.Parse(json);
        var parameters = doc.RootElement.GetProperty("parameters");

        Assert.Equal("0.7", parameters.GetProperty("temperature").GetString());
        Assert.Equal("1000", parameters.GetProperty("maxTokens").GetString());
    }

    [Fact]
    public async Task RunJson_NoAgent_OmitsAgentProperty()
    {
        var exporter = new DirectoryExporter();
        var report = CreateMinimalReport();
        var outputDir = Path.Combine(_tempDir, "run-no-agent");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var json = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.RunFileName));
        // Agent should be null → omitted by WhenWritingNull
        Assert.DoesNotContain("\"agent\"", json);
    }

    // ─── Edge cases ─────────────────────────────────────────────────

    [Fact]
    public async Task ExportToDirectoryAsync_EmptyReport_ProducesValidFiles()
    {
        var exporter = new DirectoryExporter();
        var report = CreateMinimalReport();
        var outputDir = Path.Combine(_tempDir, "empty-report");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        // results.jsonl should be empty (or whitespace only)
        var jsonlContent = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.ResultsFileName));
        var nonEmptyLines = jsonlContent.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Empty(nonEmptyLines);

        // summary.json should still have valid structure
        var summaryJson = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.SummaryFileName));
        var doc = JsonDocument.Parse(summaryJson);
        Assert.Equal(0, doc.RootElement.GetProperty("stats").GetProperty("total").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("stats").GetProperty("passRate").GetDouble());
    }

    [Fact]
    public async Task ExportToDirectoryAsync_SingleTestNoMetrics_ProducesValidFiles()
    {
        var exporter = new DirectoryExporter();
        var report = new EvaluationReport
        {
            RunId = "single01",
            TotalTests = 1,
            PassedTests = 1,
            OverallScore = 100,
            StartTime = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2026, 3, 1, 10, 0, 1, TimeSpan.Zero),
            TestResults = new List<TestResultSummary>
            {
                new() { Name = "only_test", Score = 100, Passed = true, DurationMs = 500 }
            }
        };
        var outputDir = Path.Combine(_tempDir, "single-test");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        // results.jsonl — 1 line
        var lines = (await File.ReadAllLinesAsync(Path.Combine(outputDir, DirectoryExporter.ResultsFileName)))
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Single(lines);

        // summary.json — no metrics key (empty dict serialized)
        var summaryJson = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.SummaryFileName));
        var summaryDoc = JsonDocument.Parse(summaryJson);
        var metrics = summaryDoc.RootElement.GetProperty("metrics");
        Assert.Empty(metrics.EnumerateObject().ToList());

        // run.json valid parse
        var runJson = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.RunFileName));
        var runDoc = JsonDocument.Parse(runJson);
        Assert.Equal("single01", runDoc.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task ExportToDirectoryAsync_SingleMetricSingleTest_StdDevIsZero()
    {
        var exporter = new DirectoryExporter();
        var report = new EvaluationReport
        {
            RunId = "stats01",
            TotalTests = 1,
            PassedTests = 1,
            OverallScore = 90,
            StartTime = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2026, 3, 1, 10, 0, 5, TimeSpan.Zero),
            TestResults = new List<TestResultSummary>
            {
                new()
                {
                    Name = "solo", Score = 90, Passed = true, DurationMs = 1000,
                    MetricScores = new() { ["llm_relevance"] = 85.5 }
                }
            }
        };
        var outputDir = Path.Combine(_tempDir, "single-metric");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var json = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.SummaryFileName));
        var doc = JsonDocument.Parse(json);
        var relevance = doc.RootElement.GetProperty("metrics").GetProperty("llm_relevance");

        // Single value: stddev = 0, all percentiles = value, min = max = mean = value
        Assert.Equal(0, relevance.GetProperty("stdDev").GetDouble());
        Assert.Equal(85.5, relevance.GetProperty("mean").GetDouble());
        Assert.Equal(85.5, relevance.GetProperty("min").GetDouble());
        Assert.Equal(85.5, relevance.GetProperty("max").GetDouble());
        Assert.Equal(85.5, relevance.GetProperty("p50").GetDouble());
        Assert.Equal(85.5, relevance.GetProperty("p95").GetDouble());
        Assert.Equal(85.5, relevance.GetProperty("p99").GetDouble());
        Assert.Equal(1, relevance.GetProperty("sampleSize").GetInt32());
    }

    [Fact]
    public async Task SummaryJson_IsValidJson()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "summary-valid");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var json = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.SummaryFileName));
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task RunJson_IsValidJson()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();
        var outputDir = Path.Combine(_tempDir, "run-valid");

        await exporter.ExportToDirectoryAsync(report, outputDir);

        var json = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.RunFileName));
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task ExportToDirectoryAsync_OverwritesExistingFiles()
    {
        var exporter = new DirectoryExporter();
        var outputDir = Path.Combine(_tempDir, "overwrite-test");

        // First write
        var report1 = CreateSampleReport();
        report1.RunId = "first_run";
        await exporter.ExportToDirectoryAsync(report1, outputDir);

        // Second write should overwrite
        var report2 = CreateSampleReport();
        report2.RunId = "second_run";
        await exporter.ExportToDirectoryAsync(report2, outputDir);

        var json = await File.ReadAllTextAsync(Path.Combine(outputDir, DirectoryExporter.SummaryFileName));
        Assert.Contains("second_run", json);
        Assert.DoesNotContain("first_run", json);
    }

    [Fact]
    public async Task ExportToDirectoryAsync_NullReport_Throws()
    {
        var exporter = new DirectoryExporter();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => exporter.ExportToDirectoryAsync(null!, Path.Combine(_tempDir, "null-test")));
    }

    [Fact]
    public async Task ExportToDirectoryAsync_NullPath_Throws()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();

        // ThrowIfNullOrWhiteSpace throws ArgumentNullException for null
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => exporter.ExportToDirectoryAsync(report, null!));
    }

    [Fact]
    public async Task ExportToDirectoryAsync_EmptyPath_Throws()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();

        await Assert.ThrowsAsync<ArgumentException>(
            () => exporter.ExportToDirectoryAsync(report, ""));
    }

    [Fact]
    public async Task ExportToDirectoryAsync_WhitespacePath_Throws()
    {
        var exporter = new DirectoryExporter();
        var report = CreateSampleReport();

        await Assert.ThrowsAsync<ArgumentException>(
            () => exporter.ExportToDirectoryAsync(report, "   "));
    }

    // ─── GenerateDirectoryName ──────────────────────────────────────

    [Fact]
    public void GenerateDirectoryName_ProducesTimestampAndModel()
    {
        var report = CreateSampleReport();

        var name = DirectoryExporter.GenerateDirectoryName(report);

        Assert.Equal("2026-03-01_14-30-00_gpt-4o", name);
    }

    [Fact]
    public void GenerateDirectoryName_NoAgent_UsesUnknown()
    {
        var report = CreateMinimalReport();

        var name = DirectoryExporter.GenerateDirectoryName(report);

        Assert.Contains("unknown", name);
    }

    [Fact]
    public void GenerateDirectoryName_SlashInModel_IsEscaped()
    {
        var report = CreateSampleReport();
        report.Agent!.Model = "org/model-name";

        var name = DirectoryExporter.GenerateDirectoryName(report);

        Assert.DoesNotContain("/", name);
        Assert.Contains("org-model-name", name);
    }

    [Fact]
    public void GenerateDirectoryName_BackslashInModel_IsEscaped()
    {
        var report = CreateSampleReport();
        report.Agent!.Model = @"org\model-name";

        var name = DirectoryExporter.GenerateDirectoryName(report);

        Assert.DoesNotContain(@"\", name);
        Assert.Contains("org-model-name", name);
    }

    [Fact]
    public void GenerateDirectoryName_ColonInModel_IsEscaped()
    {
        var report = CreateSampleReport();
        report.Agent!.Model = "model:v2.1";

        var name = DirectoryExporter.GenerateDirectoryName(report);

        Assert.DoesNotContain(":", name);
        Assert.Contains("model-v2.1", name);
    }

    [Fact]
    public void GenerateDirectoryName_NullReport_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DirectoryExporter.GenerateDirectoryName(null!));
    }

    // ─── Factory + DI integration ───────────────────────────────────

    [Fact]
    public void Factory_Creates_DirectoryExporter()
    {
        var exporter = ResultExporterFactory.Create(ExportFormat.Directory);

        Assert.IsType<DirectoryExporter>(exporter);
        Assert.Equal(ExportFormat.Directory, exporter.Format);
    }

    [Fact]
    public void ExporterRegistry_CanRegister_DirectoryExporter()
    {
        var registry = new ExporterRegistry();
        var exporter = new DirectoryExporter();

        registry.Register(exporter);

        Assert.True(registry.Contains("Directory"));
        Assert.Same(exporter, registry.Get("Directory"));
    }
}
