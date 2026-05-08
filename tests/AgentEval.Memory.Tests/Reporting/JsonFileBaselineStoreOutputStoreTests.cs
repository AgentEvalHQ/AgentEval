// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Memory.Tests.Reporting;

public class JsonFileBaselineStoreOutputStoreTests : IDisposable
{
    private readonly string _tempDir;

    public JsonFileBaselineStoreOutputStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agenteval-outputstore-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task SaveAsync_WithOutputStore_AlsoWritesCanonicalBaseline()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var options = new MemoryReportingOptions
        {
            OutputPath = Path.Combine(_tempDir, "{AgentName}")
        };
        var baselineStore = new JsonFileBaselineStore(options, store, subject);
        var baseline = CreateBaseline("V1", agentName: "TestAgent", overallScore: 85.0, grade: "B");

        await baselineStore.SaveAsync(baseline);

        var summary = await store.LoadBaselineAsync(subject);
        Assert.NotNull(summary);
        Assert.Equal("PASS", summary.Verdict);
        Assert.True(summary.Metrics.ContainsKey("OverallScore"));
        Assert.Equal(85.0, summary.Metrics["OverallScore"]);
    }

    [Fact]
    public async Task SaveAsync_WithNullOutputStore_DoesNotThrow()
    {
        var nullStore = new NullOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var options = new MemoryReportingOptions
        {
            OutputPath = Path.Combine(_tempDir, "{AgentName}")
        };
        var baselineStore = new JsonFileBaselineStore(options, nullStore, subject);
        var baseline = CreateBaseline("V1");

        // Should not throw — NullOutputStore is the no-op path
        await baselineStore.SaveAsync(baseline);
    }

    [Fact]
    public async Task SaveAsync_WithOutputStore_NoSubject_DerivesSubjectFromBaseline()
    {
        var store = new InMemoryOutputStore();
        var options = new MemoryReportingOptions
        {
            OutputPath = Path.Combine(_tempDir, "{AgentName}")
        };
        // No subject provided — derived from baseline
        var baselineStore = new JsonFileBaselineStore(options, store);
        var baseline = CreateBaseline("V1", agentName: "DerivedAgent", overallScore: 60.0, grade: "D");

        await baselineStore.SaveAsync(baseline);

        var derivedSubject = new SubjectIdentity(SubjectKind.Agent, "DerivedAgent");
        var summary = await store.LoadBaselineAsync(derivedSubject);
        Assert.NotNull(summary);
        Assert.Equal("FAIL", summary.Verdict);
    }

    [Fact]
    public async Task SaveAsync_LegacyPath_StillWritten_WithOutputStore()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var options = new MemoryReportingOptions
        {
            OutputPath = Path.Combine(_tempDir, "{AgentName}"),
            AutoCopyReportTemplate = false,
            IncludeArchetypes = false
        };
        var baselineStore = new JsonFileBaselineStore(options, store, subject);
        var baseline = CreateBaseline("LegacyCheck", agentName: "TestAgent");

        await baselineStore.SaveAsync(baseline);

        // Legacy path still written
        var legacyDir = Path.Combine(_tempDir, "testagent", "baselines");
        Assert.True(Directory.Exists(legacyDir));
        Assert.NotEmpty(Directory.GetFiles(legacyDir, "*.json"));
    }

    private static MemoryBaseline CreateBaseline(
        string name,
        string agentName = "TestAgent",
        double overallScore = 82.5,
        string grade = "B")
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
            Timestamp = DateTimeOffset.UtcNow,
            ConfigurationId = config.ConfigurationId,
            AgentConfig = config,
            Benchmark = new BenchmarkExecutionInfo { Preset = "Quick", Duration = TimeSpan.FromSeconds(5) },
            OverallScore = overallScore,
            Grade = grade,
            Stars = 4,
            CategoryResults = new Dictionary<string, CategoryScoreEntry>
            {
                ["Basic Retention"] = new() { Score = 90, Grade = "A", Skipped = false },
                ["Cross Session"]   = new() { Score = 75, Grade = "B", Skipped = false }
            },
            DimensionScores = new Dictionary<string, double>
            {
                ["Recall"] = 90, ["Resilience"] = 70, ["Temporal"] = 75, ["Persistence"] = 85, ["Organization"] = 80
            },
            Recommendations = ["Test recommendation"],
            Tags = []
        };
    }
}
