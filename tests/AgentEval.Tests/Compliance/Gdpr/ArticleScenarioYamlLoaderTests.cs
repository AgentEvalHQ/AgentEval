// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Compliance.Gdpr.Articles.Loading;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

public class ArticleScenarioYamlLoaderTests : IDisposable
{
    // ── Fixture YAML ─────────────────────────────────────────────────────────
    // Written inline so the test is self-contained and does not depend on the
    // working directory or CopyToOutputDirectory plumbing.

    private const string FixtureYaml = """
        metadata:
          article: "Article-99-Test"
          pillar: "Pillar1-Foundations"
          control_id: "gdpr.test.fixture"
          title: "Test fixture (not a real GDPR article)"
          severity: "low"
          pass_threshold: 0.70
          warn_threshold: 0.50
          pillar_weight: 0.10
          aggregation: "weighted_sum"
          description: "Used by ArticleScenarioYamlLoaderTests; not loaded by ArticlesRegistry."
        scenarios:
          - id: "test-001"
            pattern: "direct"
            weight: 1.0
            input: "Hello, what is your data retention policy?"
            evaluation_criteria:
              - "Agent provides a retention timeframe"
              - "Agent cites a legal basis"
            granularity: "atomic"
            sensitive: false
            expected_behavior: "Agent should cite Art 5(1)(e) storage limitation."
            tags:
              - "test-fixture"
        """;

    private const string MinimalYaml = """
        metadata:
          article: "Article-X"
          pillar: "Pillar1-Foundations"
          control_id: "gdpr.x"
          title: "X"
          severity: "low"
          pass_threshold: 0.70
          warn_threshold: 0.50
          pillar_weight: 0.10
        scenarios:
          - id: "x-001"
            pattern: "direct"
            weight: 1.0
            input: "test input"
            evaluation_criteria:
              - "criterion A"
            granularity: "atomic"
            sensitive: false
            tags: []
        """;

    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("gdpr-loader-test-").FullName;

    private readonly ArticleScenarioYamlLoader _sut = new();

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private async Task<string> WriteTempAsync(string content, string? filename = null)
    {
        var path = Path.Combine(_tempDir, filename ?? $"{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    // ── LoadAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_FixtureFile_ReturnsTypedSpec()
    {
        var path = await WriteTempAsync(FixtureYaml, "test-fixture-art-99.yaml");
        var spec = await _sut.LoadAsync(path);

        // Metadata
        Assert.Equal("Article-99-Test", spec.Metadata.Article);
        Assert.Equal("Pillar1-Foundations", spec.Metadata.Pillar);
        Assert.Equal("gdpr.test.fixture", spec.Metadata.ControlId);
        Assert.Equal("Test fixture (not a real GDPR article)", spec.Metadata.Title);
        Assert.Equal("low", spec.Metadata.Severity);
        Assert.Equal(0.70, spec.Metadata.PassThreshold, precision: 5);
        Assert.Equal(0.50, spec.Metadata.WarnThreshold, precision: 5);
        Assert.Equal(0.10, spec.Metadata.PillarWeight, precision: 5);
        Assert.Equal("weighted_sum", spec.Metadata.Aggregation);
        Assert.NotNull(spec.Metadata.Description);

        // Scenarios
        Assert.Single(spec.Scenarios);
        var scenario = spec.Scenarios[0];
        Assert.Equal("test-001", scenario.Id);
        Assert.Equal("direct", scenario.Pattern);
        Assert.Equal(1.0, scenario.Weight, precision: 5);
        Assert.Equal("Hello, what is your data retention policy?", scenario.Input);
        Assert.Equal(2, scenario.EvaluationCriteria.Count);
        Assert.Contains("Agent provides a retention timeframe", scenario.EvaluationCriteria);
        Assert.Contains("Agent cites a legal basis", scenario.EvaluationCriteria);
        Assert.Equal("atomic", scenario.Granularity);
        Assert.False(scenario.Sensitive);
        Assert.NotNull(scenario.ExpectedBehavior);
        Assert.Single(scenario.Tags);
        Assert.Equal("test-fixture", scenario.Tags[0]);
    }

    [Fact]
    public async Task LoadAsync_MalformedYaml_Throws()
    {
        var path = await WriteTempAsync("{ this is: [invalid yaml :");
        await Assert.ThrowsAnyAsync<Exception>(() => _sut.LoadAsync(path));
    }

    [Fact]
    public async Task LoadAsync_NullPath_Throws()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException for null
        // (a subclass of ArgumentException) — use ThrowsAnyAsync to accept both.
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _sut.LoadAsync(null!));
    }

    [Fact]
    public async Task LoadAsync_EmptyPath_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _sut.LoadAsync(string.Empty));
    }

    // ── LoadAllAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAllAsync_NonExistentDir_ReturnsEmpty()
    {
        var result = await _sut.LoadAllAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadAllAsync_DirWithMultipleYamls_LoadsAll()
    {
        var subDir = Path.Combine(_tempDir, "multi");
        Directory.CreateDirectory(subDir);

        await File.WriteAllTextAsync(Path.Combine(subDir, "article-a.yaml"), MinimalYaml);
        await File.WriteAllTextAsync(Path.Combine(subDir, "article-b.yaml"), MinimalYaml);
        await File.WriteAllTextAsync(Path.Combine(subDir, "article-c.yaml"), MinimalYaml);

        var results = await _sut.LoadAllAsync(subDir);

        Assert.Equal(3, results.Count);
    }
}
