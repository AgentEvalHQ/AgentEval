// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using static AgentEval.Memory.Models.MemoryBenchmarkResult;
using Xunit;

namespace AgentEval.Memory.Tests.Reporting;

/// <summary>
/// End-to-end integration tests that exercise the full pipeline:
/// BenchmarkResult → ToBaseline → SaveAsync → LoadAsync → Compare → ToEvaluationReport
/// </summary>
public class EndToEndReportingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileBaselineStore _store;
    private readonly BaselineComparer _comparer = new();

    public EndToEndReportingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agenteval-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new JsonFileBaselineStore(new MemoryReportingOptions
        {
            OutputPath = Path.Combine(_tempDir, "{AgentName}")
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task FullPipeline_ResultToBaselineToStoreToLoad_RoundTrips()
    {
        var result = CreateBenchmarkResult(85);
        var config = CreateConfig("gpt-4o", "SlidingWindow(50)");

        var baseline = result.ToBaseline("v2.1 Production", config, tags: ["production"]);
        await _store.SaveAsync(baseline);
        var loaded = await _store.LoadAsync(baseline.Id);

        Assert.NotNull(loaded);
        Assert.Equal(baseline.Id, loaded.Id);
        Assert.Equal(baseline.Name, loaded.Name);
        Assert.Equal(baseline.OverallScore, loaded.OverallScore);
        Assert.Equal(baseline.ConfigurationId, loaded.ConfigurationId);
        Assert.Equal(baseline.Grade, loaded.Grade);
        Assert.Equal(baseline.Stars, loaded.Stars);
        Assert.Equal(baseline.CategoryResults.Count, loaded.CategoryResults.Count);
        Assert.Equal(baseline.DimensionScores.Count, loaded.DimensionScores.Count);
        Assert.Contains("production", loaded.Tags);
    }

    [Fact]
    public async Task TwoBaselines_SameConfig_SameConfigurationId()
    {
        var config = CreateConfig("gpt-4o", "SlidingWindow(50)");

        var b1 = CreateBenchmarkResult(80).ToBaseline("Week 1", config);
        var b2 = CreateBenchmarkResult(85).ToBaseline("Week 2", config);

        await _store.SaveAsync(b1);
        await _store.SaveAsync(b2);

        var list = await _store.ListAsync(agentName: "TestAgent");
        Assert.Equal(2, list.Count);
        Assert.Equal(list[0].ConfigurationId, list[1].ConfigurationId);
    }

    [Fact]
    public async Task TwoBaselines_DifferentConfig_DifferentConfigurationId()
    {
        var configA = CreateConfig("gpt-4o", "SlidingWindow(50)");
        var configB = CreateConfig("gpt-4o", "SummarizingReducer");

        var b1 = CreateBenchmarkResult(80).ToBaseline("Config A", configA);
        var b2 = CreateBenchmarkResult(85).ToBaseline("Config B", configB);

        await _store.SaveAsync(b1);
        await _store.SaveAsync(b2);

        var list = await _store.ListAsync(agentName: "TestAgent");
        Assert.Equal(2, list.Count);
        Assert.NotEqual(list[0].ConfigurationId, list[1].ConfigurationId);
    }

    [Fact]
    public async Task Comparison_TwoBaselines_ProducesValidRadarChart()
    {
        var configA = CreateConfig("gpt-4o", "SlidingWindow(50)");
        var configB = CreateConfig("gpt-4o-mini", "Summarizer");

        var b1 = CreateBenchmarkResult(80).ToBaseline("Config A", configA);
        var b2 = CreateBenchmarkResult(90).ToBaseline("Config B", configB);

        await _store.SaveAsync(b1);
        await _store.SaveAsync(b2);

        var baselines = await _store.ListAsync(agentName: "TestAgent");
        var comparison = _comparer.Compare(baselines);

        Assert.Equal(2, comparison.Baselines.Count);
        Assert.True(comparison.RadarChart.Axes.Count <= 5);
        Assert.Equal(2, comparison.RadarChart.Series.Count);
        Assert.Equal("Config A", comparison.RadarChart.Series[0].Name);
    }

    [Fact]
    public void ExportBridge_ToEvaluationReport_PopulatesFields()
    {
        var result = CreateBenchmarkResult(82);
        var report = result.ToEvaluationReport(agentName: "TestAgent", modelName: "gpt-4o");

        Assert.Equal("Quick", report.Name);
        // OverallScore is a weighted average, not the raw score
        Assert.Equal(result.OverallScore, report.OverallScore);
        Assert.InRange(report.OverallScore, 0, 100);
        Assert.NotNull(report.Agent);
        Assert.Equal("TestAgent", report.Agent!.Name);
        Assert.Equal("gpt-4o", report.Agent.Model);
        Assert.NotEmpty(report.Metadata["Grade"]);
        Assert.Equal("MemoryBenchmark", report.Metadata["BenchmarkType"]);
    }

    [Fact]
    public async Task ManifestVerification_AfterTwoSaves_HasCorrectData()
    {
        var config = CreateConfig("gpt-4o", "SlidingWindow(50)");
        await _store.SaveAsync(CreateBenchmarkResult(80).ToBaseline("V1", config));
        await _store.SaveAsync(CreateBenchmarkResult(85).ToBaseline("V2", config));

        var manifestPath = Path.Combine(_tempDir, "testagent", "manifest.json");
        Assert.True(File.Exists(manifestPath));

        var content = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<BenchmarkManifest>(content, JsonFileBaselineStore.JsonOptions);

        Assert.NotNull(manifest);
        Assert.Equal("1.0", manifest.SchemaVersion);
        Assert.Equal("TestAgent", manifest.Agent.Name);
        Assert.Equal(2, manifest.Benchmarks[0].Baselines.Count);
    }

    [Fact]
    public async Task FileStructure_AfterTwoSaves_IsComplete()
    {
        var config = CreateConfig("gpt-4o", "SlidingWindow(50)");
        await _store.SaveAsync(CreateBenchmarkResult(80).ToBaseline("V1", config));
        await _store.SaveAsync(CreateBenchmarkResult(85).ToBaseline("V2", config));

        var agentDir = Path.Combine(_tempDir, "testagent");

        // baselines folder with 2 JSON files
        var baselineFiles = Directory.GetFiles(Path.Combine(agentDir, "baselines"), "*.json");
        Assert.Equal(2, baselineFiles.Length);

        // manifest.json
        Assert.True(File.Exists(Path.Combine(agentDir, "manifest.json")));

        // report.html (auto-copied from embedded resource)
        Assert.True(File.Exists(Path.Combine(agentDir, "report.html")));

        // archetypes.json (auto-copied from embedded resource)
        Assert.True(File.Exists(Path.Combine(agentDir, "archetypes.json")));
    }

    // --- Helpers ---

    private static AgentBenchmarkConfig CreateConfig(string modelId, string reducer) => new()
    {
        AgentName = "TestAgent",
        ModelId = modelId,
        ReducerStrategy = reducer,
        MemoryProvider = "InMemory"
    };

    private static MemoryBenchmarkResult CreateBenchmarkResult(double score) => new()
    {
        BenchmarkName = "Quick",
        Duration = TimeSpan.FromSeconds(5),
        CategoryResults =
        [
            new BenchmarkCategoryResult
            {
                CategoryName = "Basic Retention",
                Score = score,
                Weight = 0.4,
                ScenarioType = BenchmarkScenarioType.BasicRetention,
                Duration = TimeSpan.FromSeconds(2)
            },
            new BenchmarkCategoryResult
            {
                CategoryName = "Temporal Reasoning",
                Score = score - 5,
                Weight = 0.3,
                ScenarioType = BenchmarkScenarioType.TemporalReasoning,
                Duration = TimeSpan.FromSeconds(1.5)
            },
            new BenchmarkCategoryResult
            {
                CategoryName = "Noise Resilience",
                Score = score - 10,
                Weight = 0.3,
                ScenarioType = BenchmarkScenarioType.NoiseResilience,
                Duration = TimeSpan.FromSeconds(1.5)
            }
        ]
    };
}
