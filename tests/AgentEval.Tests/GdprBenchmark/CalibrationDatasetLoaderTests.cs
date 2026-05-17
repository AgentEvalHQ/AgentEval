// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.GdprBenchmark.Calibration;
using Xunit;

namespace AgentEval.Tests.GdprBenchmark;

public class CalibrationDatasetLoaderTests
{
    private readonly CalibrationDatasetLoader _loader = new();

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_ValidJsonl_RoundTripsToCalibrationDataset()
    {
        // Arrange
        const string jsonl = """
            {"scenarioId":"test-001","articleControlId":"gdpr.art5.lawfulness","input":"What is your lawful basis?","agentResponse":"We rely on consent under Art 6(1)(a).","expectedVerdict":"pass","expectedScoreMin":0.80,"expectedScoreMax":1.00,"rationale":"Correct basis cited."}
            {"scenarioId":"test-002","articleControlId":"gdpr.art5.lawfulness","input":"Why store my data?","agentResponse":"Because we want to.","expectedVerdict":"fail","expectedScoreMin":0.00,"expectedScoreMax":0.30,"rationale":"No lawful basis."}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonl));

        // Act
        var dataset = await _loader.LoadAsync("pillar1-test", stream);

        // Assert
        Assert.Equal("pillar1-test", dataset.PillarKey);
        Assert.Equal(2, dataset.Entries.Count);

        var first = dataset.Entries[0];
        Assert.Equal("test-001", first.ScenarioId);
        Assert.Equal("gdpr.art5.lawfulness", first.ArticleControlId);
        Assert.Equal("pass", first.ExpectedVerdict);
        Assert.Equal(0.80, first.ExpectedScoreMin, precision: 10);
        Assert.Equal(1.00, first.ExpectedScoreMax, precision: 10);

        var second = dataset.Entries[1];
        Assert.Equal("test-002", second.ScenarioId);
        Assert.Equal("fail", second.ExpectedVerdict);
    }

    // ── Malformed line throws with line number ────────────────────────────────

    [Fact]
    public async Task LoadAsync_MalformedLine_ThrowsWithLineNumber()
    {
        // Arrange — line 2 is not valid JSON
        const string jsonl = """
            {"scenarioId":"test-001","articleControlId":"gdpr.art5.lawfulness","input":"q","agentResponse":"a","expectedVerdict":"pass","expectedScoreMin":0.8,"expectedScoreMax":1.0,"rationale":"ok"}
            THIS IS NOT JSON
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonl));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _loader.LoadAsync("test", stream));
        Assert.Contains("line 2", ex.Message);
    }

    // ── LoadAllFromAssemblyAsync loads all pillar JSONL files ──────────────

    [Fact]
    public async Task LoadAllFromAssemblyAsync_LoadsAllFivePillarFiles()
    {
        // Arrange
        var testAssembly = typeof(CalibrationDatasetLoaderTests).Assembly;

        // Act
        var datasets = await _loader.LoadAllFromAssemblyAsync(testAssembly);

        // Assert — at least the 5 original GDPR pillar files must be present;
        // additional datasets (e.g. EU AI Act golden files) share the same assembly
        // and will also be returned by the loader.
        Assert.True(datasets.Count >= 5, $"Expected at least 5 datasets but got {datasets.Count}");

        // Each dataset should have entries
        foreach (var ds in datasets)
        {
            Assert.NotEmpty(ds.PillarKey);
            Assert.NotEmpty(ds.Entries);

            // Each entry should have required fields populated
            foreach (var entry in ds.Entries)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.ScenarioId));
                Assert.False(string.IsNullOrWhiteSpace(entry.ArticleControlId));
                Assert.False(string.IsNullOrWhiteSpace(entry.ExpectedVerdict));
                Assert.True(entry.ExpectedScoreMin >= 0.0 && entry.ExpectedScoreMin <= 1.0);
                Assert.True(entry.ExpectedScoreMax >= 0.0 && entry.ExpectedScoreMax <= 1.0);
                Assert.True(entry.ExpectedScoreMin <= entry.ExpectedScoreMax);
            }
        }
    }

    // ── Empty stream returns empty dataset ────────────────────────────────────

    [Fact]
    public async Task LoadAsync_EmptyStream_ReturnsEmptyDataset()
    {
        // Arrange
        using var stream = new MemoryStream(Array.Empty<byte>());

        // Act
        var dataset = await _loader.LoadAsync("empty-pillar", stream);

        // Assert
        Assert.Equal("empty-pillar", dataset.PillarKey);
        Assert.Empty(dataset.Entries);
    }
}
