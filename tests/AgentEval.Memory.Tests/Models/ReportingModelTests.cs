// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.Models;

public class ReportingModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // --- ConfigurationId tests ---

    [Fact]
    public void ConfigurationId_SameProperties_ProducesSameHash()
    {
        var config1 = new AgentBenchmarkConfig
        {
            AgentName = "TestAgent",
            ModelId = "gpt-4o",
            ReducerStrategy = "SlidingWindow(50)",
            MemoryProvider = "InMemory"
        };
        var config2 = new AgentBenchmarkConfig
        {
            AgentName = "TestAgent",
            ModelId = "gpt-4o",
            ReducerStrategy = "SlidingWindow(50)",
            MemoryProvider = "InMemory"
        };

        Assert.Equal(config1.ConfigurationId, config2.ConfigurationId);
    }

    [Fact]
    public void ConfigurationId_DifferentModelId_ProducesDifferentHash()
    {
        var config1 = new AgentBenchmarkConfig { AgentName = "A", ModelId = "gpt-4o" };
        var config2 = new AgentBenchmarkConfig { AgentName = "A", ModelId = "gpt-4o-mini" };

        Assert.NotEqual(config1.ConfigurationId, config2.ConfigurationId);
    }

    [Fact]
    public void ConfigurationId_DifferentReducer_ProducesDifferentHash()
    {
        var config1 = new AgentBenchmarkConfig { AgentName = "A", ReducerStrategy = "SlidingWindow(50)" };
        var config2 = new AgentBenchmarkConfig { AgentName = "A", ReducerStrategy = "Summarizer" };

        Assert.NotEqual(config1.ConfigurationId, config2.ConfigurationId);
    }

    [Fact]
    public void ConfigurationId_ContextProviderOrder_DoesNotMatter()
    {
        var config1 = new AgentBenchmarkConfig
        {
            AgentName = "A",
            ContextProviders = ["ProviderA", "ProviderB", "ProviderC"]
        };
        var config2 = new AgentBenchmarkConfig
        {
            AgentName = "A",
            ContextProviders = ["ProviderC", "ProviderA", "ProviderB"]
        };

        Assert.Equal(config1.ConfigurationId, config2.ConfigurationId);
    }

    [Fact]
    public void ConfigurationId_NullProperties_HandledGracefully()
    {
        var config = new AgentBenchmarkConfig
        {
            AgentName = "A"
            // All other properties null/default
        };

        var id = config.ConfigurationId;
        Assert.NotNull(id);
        Assert.Equal(12, id.Length);
        Assert.Matches("^[A-F0-9]{12}$", id); // Must be valid uppercase hex
    }

    [Fact]
    public void ConfigurationId_DifferentAgentName_ProducesDifferentHash()
    {
        var config1 = new AgentBenchmarkConfig { AgentName = "AgentA" };
        var config2 = new AgentBenchmarkConfig { AgentName = "AgentB" };
        Assert.NotEqual(config1.ConfigurationId, config2.ConfigurationId);
    }

    // --- DimensionComparison tests ---

    [Fact]
    public void DimensionComparison_BestScore_ReturnsMax()
    {
        var dim = new DimensionComparison
        {
            DimensionName = "Recall",
            Scores = new Dictionary<string, double> { ["bl-1"] = 80, ["bl-2"] = 95, ["bl-3"] = 72 }
        };

        Assert.Equal(95, dim.BestScore);
        Assert.Equal("bl-2", dim.BestBaselineId);
    }

    [Fact]
    public void DimensionComparison_EmptyScores_ReturnsDefaults()
    {
        var dim = new DimensionComparison
        {
            DimensionName = "Recall",
            Scores = new Dictionary<string, double>()
        };

        Assert.Equal(0, dim.BestScore);
        Assert.Null(dim.BestBaselineId);
    }

    // --- StochasticData tests ---

    [Fact]
    public void StochasticData_CoefficientOfVariation_ComputedCorrectly()
    {
        var data = new StochasticData { Runs = 5, Mean = 80, StdDev = 4, Min = 75, Max = 85 };
        Assert.Equal(0.05, data.CoefficientOfVariation, precision: 10);
    }

    [Fact]
    public void StochasticData_CoefficientOfVariation_MeanZero_ReturnsZero()
    {
        var data = new StochasticData { Runs = 5, Mean = 0, StdDev = 4, Min = 0, Max = 0 };
        Assert.Equal(0, data.CoefficientOfVariation);
    }

    // --- JSON round-trip tests ---

    [Fact]
    public void MemoryBaseline_JsonRoundTrip_Succeeds()
    {
        var baseline = CreateTestBaseline();

        var json = JsonSerializer.Serialize(baseline, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MemoryBaseline>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(baseline.Id, deserialized.Id);
        Assert.Equal(baseline.Name, deserialized.Name);
        Assert.Equal(baseline.ConfigurationId, deserialized.ConfigurationId);
        Assert.Equal(baseline.OverallScore, deserialized.OverallScore);
        Assert.Equal(baseline.Grade, deserialized.Grade);
        Assert.Equal(baseline.Stars, deserialized.Stars);
        Assert.Equal(baseline.CategoryResults.Count, deserialized.CategoryResults.Count);
        Assert.Equal(baseline.DimensionScores.Count, deserialized.DimensionScores.Count);
        Assert.Equal(baseline.Recommendations.Count, deserialized.Recommendations.Count);
        Assert.Equal(baseline.Tags.Count, deserialized.Tags.Count);
        Assert.Equal(baseline.AgentConfig.AgentName, deserialized.AgentConfig.AgentName);
        Assert.Equal(baseline.Benchmark.Preset, deserialized.Benchmark.Preset);
    }

    [Fact]
    public void BenchmarkManifest_JsonRoundTrip_Succeeds()
    {
        var manifest = new BenchmarkManifest
        {
            SchemaVersion = "1.0",
            GeneratedAt = DateTimeOffset.UtcNow,
            GeneratedBy = "AgentEval.Memory v1.0.0",
            Agent = new ManifestAgentInfo { Name = "TestAgent" },
            Benchmarks =
            [
                new ManifestBenchmarkGroup
                {
                    BenchmarkId = "memory-full",
                    Preset = "Full",
                    Categories = ["Basic Retention", "Temporal Reasoning"],
                    Baselines =
                    [
                        new ManifestBaselineEntry
                        {
                            Id = "bl-001",
                            File = "baselines/test.json",
                            Name = "Test",
                            ConfigurationId = "AABBCCDDEE01",
                            Timestamp = DateTimeOffset.UtcNow,
                            OverallScore = 82.5,
                            Grade = "B"
                        }
                    ]
                }
            ],
            Archetypes = "archetypes.json"
        };

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<BenchmarkManifest>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("1.0", deserialized.SchemaVersion);
        Assert.Equal("TestAgent", deserialized.Agent.Name);
        Assert.Single(deserialized.Benchmarks);
        Assert.Single(deserialized.Benchmarks[0].Baselines);
        Assert.Equal("bl-001", deserialized.Benchmarks[0].Baselines[0].Id);
    }

    [Fact]
    public void MemoryBaseline_JsonOutput_UsesSnakeCase()
    {
        var baseline = CreateTestBaseline();
        var json = JsonSerializer.Serialize(baseline, JsonOptions);

        Assert.Contains("\"overall_score\"", json);
        Assert.Contains("\"agent_config\"", json);
        Assert.Contains("\"configuration_id\"", json);
        Assert.Contains("\"dimension_scores\"", json);
        Assert.Contains("\"category_results\"", json);
        Assert.Contains("\"agent_name\"", json);
    }

    [Fact]
    public void MemoryBaseline_JsonRoundTrip_PreservesCategoryContent()
    {
        var baseline = CreateTestBaseline();
        var json = JsonSerializer.Serialize(baseline, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MemoryBaseline>(json, JsonOptions)!;

        // Verify actual content, not just counts
        Assert.Equal(95, deserialized.CategoryResults["Basic Retention"].Score);
        Assert.Equal("A", deserialized.CategoryResults["Basic Retention"].Grade);
        Assert.False(deserialized.CategoryResults["Basic Retention"].Skipped);
        Assert.True(deserialized.CategoryResults["Cross-Session"].Skipped);
        Assert.Equal(91.5, deserialized.DimensionScores["Recall"]);
        Assert.Contains("Improve noise resilience", deserialized.Recommendations);
        Assert.Equal("gpt-4o", deserialized.AgentConfig.ModelId);
        Assert.Equal("SlidingWindow(50)", deserialized.AgentConfig.ReducerStrategy);
    }

    [Fact]
    public void MemoryBaseline_JsonOutput_OmitsNullFields()
    {
        var baseline = CreateTestBaseline();
        // Description is null — should be omitted from JSON
        Assert.Null(baseline.Description);

        var json = JsonSerializer.Serialize(baseline, JsonOptions);
        Assert.DoesNotContain("\"description\"", json);
    }

    // --- Helpers ---

    private static MemoryBaseline CreateTestBaseline()
    {
        var config = new AgentBenchmarkConfig
        {
            AgentName = "TestAgent",
            ModelId = "gpt-4o",
            ReducerStrategy = "SlidingWindow(50)",
            MemoryProvider = "InMemory"
        };

        return new MemoryBaseline
        {
            Id = "bl-test0001",
            Name = "Test Baseline",
            Timestamp = DateTimeOffset.UtcNow,
            ConfigurationId = config.ConfigurationId,
            AgentConfig = config,
            Benchmark = new BenchmarkExecutionInfo
            {
                Preset = "Full",
                Duration = TimeSpan.FromSeconds(12.5)
            },
            OverallScore = 82.5,
            Grade = "B",
            Stars = 4,
            CategoryResults = new Dictionary<string, CategoryScoreEntry>
            {
                ["Basic Retention"] = new() { Score = 95, Grade = "A", Skipped = false },
                ["Temporal Reasoning"] = new() { Score = 72, Grade = "C", Skipped = false },
                ["Cross-Session"] = new() { Score = 0, Grade = "F", Skipped = true, Recommendation = "Implement ISessionResettableAgent" }
            },
            DimensionScores = new Dictionary<string, double>
            {
                ["Recall"] = 91.5,
                ["Resilience"] = 71,
                ["Temporal"] = 77.5,
                ["Persistence"] = 85,
                ["Organization"] = 80
            },
            Recommendations = ["Improve noise resilience", "Add persistent memory"],
            Tags = ["test", "v1.0"]
        };
    }
}
