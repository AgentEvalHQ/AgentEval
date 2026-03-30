// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using Xunit;

namespace AgentEval.Memory.Tests.Reporting;

public class JsonFileBaselineStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileBaselineStore _store;

    public JsonFileBaselineStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agenteval-test-{Guid.NewGuid():N}");
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
    public async Task SaveAsync_CreatesBaselineDirectory()
    {
        var baseline = CreateBaseline("Test");
        await _store.SaveAsync(baseline);

        var agentDir = Path.Combine(_tempDir, "testagent");
        Assert.True(Directory.Exists(Path.Combine(agentDir, "baselines")));
    }

    [Fact]
    public async Task SaveAsync_WritesValidJsonFile()
    {
        var baseline = CreateBaseline("Test");
        await _store.SaveAsync(baseline);

        var files = Directory.GetFiles(Path.Combine(_store.GetReportDirectory("TestAgent"), "baselines"), "*.json");
        Assert.Single(files);

        var content = await File.ReadAllTextAsync(files[0]);
        var deserialized = JsonSerializer.Deserialize<MemoryBaseline>(content, JsonFileBaselineStore.JsonOptions);
        Assert.NotNull(deserialized);
        Assert.Equal(baseline.Id, deserialized.Id);
    }

    [Fact]
    public async Task SaveAsync_CreatesManifest()
    {
        var baseline = CreateBaseline("Test");
        await _store.SaveAsync(baseline);

        var manifestPath = Path.Combine(_store.GetReportDirectory("TestAgent"), "manifest.json");
        Assert.True(File.Exists(manifestPath));

        var content = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<BenchmarkManifest>(content, JsonFileBaselineStore.JsonOptions);
        Assert.NotNull(manifest);
        Assert.Equal("1.0", manifest.SchemaVersion);
        Assert.Equal("TestAgent", manifest.Agent.Name);
        Assert.Single(manifest.Benchmarks);
        Assert.Single(manifest.Benchmarks[0].Baselines);
    }

    [Fact]
    public async Task SaveAsync_CopiesReportHtml()
    {
        var baseline = CreateBaseline("Test");
        await _store.SaveAsync(baseline);

        Assert.True(File.Exists(Path.Combine(_store.GetReportDirectory("TestAgent"), "report.html")));
    }

    [Fact]
    public async Task SaveAsync_DoesNotOverwriteExistingReportHtml()
    {
        var agentDir = Path.Combine(_tempDir, "testagent");
        Directory.CreateDirectory(agentDir);
        var reportPath = Path.Combine(agentDir, "report.html");
        await File.WriteAllTextAsync(reportPath, "CUSTOM CONTENT");

        var baseline = CreateBaseline("Test");
        await _store.SaveAsync(baseline);

        var content = await File.ReadAllTextAsync(reportPath);
        Assert.Equal("CUSTOM CONTENT", content);
    }

    [Fact]
    public async Task LoadAsync_ReturnsCorrectBaseline()
    {
        var baseline = CreateBaseline("Test");
        await _store.SaveAsync(baseline);

        var loaded = await _store.LoadAsync(baseline.Id);

        Assert.NotNull(loaded);
        Assert.Equal(baseline.Id, loaded.Id);
        Assert.Equal(baseline.Name, loaded.Name);
        Assert.Equal(baseline.OverallScore, loaded.OverallScore);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNullForNonExistent()
    {
        var result = await _store.LoadAsync("non-existent-id");
        Assert.Null(result);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllBaselinesOrderedByTimestamp()
    {
        var b1 = CreateBaseline("First", timestamp: DateTimeOffset.UtcNow.AddDays(-2));
        var b2 = CreateBaseline("Second", timestamp: DateTimeOffset.UtcNow.AddDays(-1));
        var b3 = CreateBaseline("Third", timestamp: DateTimeOffset.UtcNow);

        await _store.SaveAsync(b1);
        await _store.SaveAsync(b2);
        await _store.SaveAsync(b3);

        var list = await _store.ListAsync(agentName: "TestAgent");

        Assert.Equal(3, list.Count);
        Assert.Equal("First", list[0].Name);
        Assert.Equal("Third", list[2].Name);
    }

    [Fact]
    public async Task ListAsync_FiltersByAgentName()
    {
        await _store.SaveAsync(CreateBaseline("A", agentName: "Agent1"));
        await _store.SaveAsync(CreateBaseline("B", agentName: "Agent2"));

        var list = await _store.ListAsync(agentName: "Agent1");
        Assert.Single(list);
        Assert.Equal("Agent1", list[0].AgentConfig.AgentName);
    }

    [Fact]
    public async Task ListAsync_FiltersByTags()
    {
        await _store.SaveAsync(CreateBaseline("Prod", tags: ["production", "v2.1"]));
        await _store.SaveAsync(CreateBaseline("Dev", tags: ["development"]));

        var list = await _store.ListAsync(agentName: "TestAgent", tags: ["production"]);
        Assert.Single(list);
        Assert.Equal("Prod", list[0].Name);
    }

    [Fact]
    public async Task DeleteAsync_RemovesFileAndRebuildsManifest()
    {
        var b1 = CreateBaseline("Keep");
        var b2 = CreateBaseline("Delete");
        await _store.SaveAsync(b1);
        await _store.SaveAsync(b2);

        var deleted = await _store.DeleteAsync(b2.Id);
        Assert.True(deleted);

        var list = await _store.ListAsync(agentName: "TestAgent");
        Assert.Single(list);
        Assert.Equal("Keep", list[0].Name);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsFalseForNonExistent()
    {
        var result = await _store.DeleteAsync("non-existent");
        Assert.False(result);
    }

    [Fact]
    public async Task MultipleSaves_ProduceCorrectManifest()
    {
        await _store.SaveAsync(CreateBaseline("V1", timestamp: DateTimeOffset.UtcNow.AddDays(-2)));
        await _store.SaveAsync(CreateBaseline("V2", timestamp: DateTimeOffset.UtcNow.AddDays(-1)));
        await _store.SaveAsync(CreateBaseline("V3", timestamp: DateTimeOffset.UtcNow));

        var manifestPath = Path.Combine(_store.GetReportDirectory("TestAgent"), "manifest.json");
        var content = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<BenchmarkManifest>(content, JsonFileBaselineStore.JsonOptions);

        Assert.NotNull(manifest);
        Assert.Equal(3, manifest.Benchmarks[0].Baselines.Count);
    }

    [Fact]
    public async Task ManifestGroupsByPreset()
    {
        await _store.SaveAsync(CreateBaseline("Quick1", preset: "Quick"));
        await _store.SaveAsync(CreateBaseline("Full1", preset: "Full"));

        var manifestPath = Path.Combine(_store.GetReportDirectory("TestAgent"), "manifest.json");
        var content = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<BenchmarkManifest>(content, JsonFileBaselineStore.JsonOptions);

        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.Benchmarks.Count);
        Assert.Contains(manifest.Benchmarks, g => g.Preset == "Quick");
        Assert.Contains(manifest.Benchmarks, g => g.Preset == "Full");
    }

    [Fact]
    public void Slugify_HandlesSpecialCharacters()
    {
        Assert.Equal("hello-world", JsonFileBaselineStore.Slugify("Hello World!"));
        Assert.Equal("v2-1-gpt-4o", JsonFileBaselineStore.Slugify("v2.1 gpt-4o"));
        Assert.Equal("unnamed", JsonFileBaselineStore.Slugify(""));
        Assert.Equal("unnamed", JsonFileBaselineStore.Slugify("   "));
    }

    [Fact]
    public async Task ListAsync_SkipsCorruptFiles()
    {
        // Save a valid baseline
        await _store.SaveAsync(CreateBaseline("Valid"));

        // Write a corrupt JSON file directly
        var corruptPath = Path.Combine(_tempDir, "testagent", "baselines", "corrupt.json");
        await File.WriteAllTextAsync(corruptPath, "{ this is not valid json!!!");

        // ListAsync should return only the valid baseline, not crash
        var list = await _store.ListAsync(agentName: "TestAgent");
        Assert.Single(list);
        Assert.Equal("Valid", list[0].Name);
    }

    [Fact]
    public void Slugify_TruncatesLongNames()
    {
        var longName = new string('a', 100);
        var slug = JsonFileBaselineStore.Slugify(longName);
        Assert.True(slug.Length <= 60);
    }

    [Fact]
    public void Slugify_SpecialCharsOnly_ReturnsUnnamed()
    {
        Assert.Equal("unnamed", JsonFileBaselineStore.Slugify("@#$%^&*"));
    }

    [Fact]
    public void DefaultConstructor_UsesDefaultOptions()
    {
        var store = new JsonFileBaselineStore();
        // Verify it doesn't throw and creates with default OutputPath
        var reportDir = store.GetReportDirectory("TestAgent");
        Assert.Contains("testagent", reportDir);
    }

    [Fact]
    public void GetReportDirectory_ReturnsAbsolutePath()
    {
        var dir = _store.GetReportDirectory("TestAgent");
        Assert.True(Path.IsPathRooted(dir));
        Assert.Contains("testagent", dir);
    }

    [Fact]
    public async Task JsonOutput_UsesSnakeCaseNaming()
    {
        var baseline = CreateBaseline("Test");
        await _store.SaveAsync(baseline);

        var files = Directory.GetFiles(Path.Combine(_store.GetReportDirectory("TestAgent"), "baselines"), "*.json");
        var content = await File.ReadAllTextAsync(files[0]);

        Assert.Contains("\"overall_score\"", content);
        Assert.Contains("\"configuration_id\"", content);
        Assert.Contains("\"agent_config\"", content);
    }

    // --- Helpers ---

    private static MemoryBaseline CreateBaseline(
        string name,
        string agentName = "TestAgent",
        string preset = "Quick",
        DateTimeOffset? timestamp = null,
        IReadOnlyList<string>? tags = null)
    {
        var config = new AgentBenchmarkConfig
        {
            AgentName = agentName,
            ModelId = "gpt-4o",
            ReducerStrategy = "SlidingWindow(50)",
            MemoryProvider = "InMemory"
        };

        return new MemoryBaseline
        {
            Id = $"bl-{Guid.NewGuid():N}"[..11],
            Name = name,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            ConfigurationId = config.ConfigurationId,
            AgentConfig = config,
            Benchmark = new BenchmarkExecutionInfo { Preset = preset, Duration = TimeSpan.FromSeconds(5) },
            OverallScore = 82.5,
            Grade = "B",
            Stars = 4,
            CategoryResults = new Dictionary<string, CategoryScoreEntry>
            {
                ["Basic Retention"] = new() { Score = 90, Grade = "A", Skipped = false }
            },
            DimensionScores = new Dictionary<string, double>
            {
                ["Recall"] = 90, ["Resilience"] = 70, ["Temporal"] = 75, ["Persistence"] = 85, ["Organization"] = 80
            },
            Recommendations = ["Test recommendation"],
            Tags = tags?.ToList() ?? []
        };
    }
}
