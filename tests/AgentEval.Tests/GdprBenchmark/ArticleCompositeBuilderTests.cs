// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.GdprBenchmark.Articles.Building;
using AgentEval.GdprBenchmark.Articles.Loading;
using AgentEval.GdprBenchmark.Articles.Models;
using Xunit;

namespace AgentEval.Tests.GdprBenchmark;

public class ArticleCompositeBuilderTests
{
    // ── Stub ──────────────────────────────────────────────────────────────────

    private sealed class FakeEvaluator : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input,
            string output,
            IEnumerable<string> criteria,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentEval.Core.EvaluationResult { OverallScore = 80 });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArticleCompositeBuilder MakeSut() =>
        new(new ScenarioToAtomicEval(new FakeEvaluator(), judgeModel: "stub"));

    private static async Task<ArticleSpec> LoadArt17Async()
    {
        var loader = new ArticleScenarioYamlLoader();
        var assembly = typeof(AgentEval.GdprBenchmark.Articles.ArticlesRegistry).Assembly;
        var all = await loader.LoadAllFromAssemblyAsync(assembly, includeTestFixtures: false);
        return all.Single(a => a.Metadata.ControlId == "gdpr.art17.erasure");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Build_ProducesCompositeWithCorrectKey_AndComponentCount()
    {
        var art17 = await LoadArt17Async();
        var sut = MakeSut();

        var composite = sut.Build(art17);

        Assert.Equal("gdpr.art17.erasure", composite.Key);
        Assert.Equal(art17.Scenarios.Count, composite.Components.Count);
        Assert.Equal(0.70, composite.Threshold);
    }

    [Fact]
    public async Task Build_ComponentWeights_MatchScenarioWeights()
    {
        var art17 = await LoadArt17Async();
        var sut = MakeSut();

        var composite = sut.Build(art17);

        for (int i = 0; i < art17.Scenarios.Count; i++)
        {
            Assert.Equal(art17.Scenarios[i].Weight, composite.Components[i].Weight, precision: 10);
        }
    }

    [Fact]
    public void Build_NullArticle_Throws()
    {
        var sut = MakeSut();
        Assert.Throws<ArgumentNullException>(() => sut.Build(null!));
    }

    [Fact]
    public void Constructor_NullScenarioBuilder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ArticleCompositeBuilder(null!));
    }

    [Fact]
    public async Task Build_CompositeKey_EqualToControlId()
    {
        var art17 = await LoadArt17Async();
        var sut = MakeSut();

        var composite = sut.Build(art17);

        Assert.Equal(art17.Metadata.ControlId, composite.Key);
        Assert.Equal(art17.Metadata.Title, composite.Name);
    }

    [Fact]
    public async Task Build_Art9SpecAggregationMin_UsesMinAggregation()
    {
        // Arrange — Art 9 YAML specifies aggregation: "min"
        var loader = new ArticleScenarioYamlLoader();
        var assembly = typeof(AgentEval.GdprBenchmark.Articles.ArticlesRegistry).Assembly;
        var all = await loader.LoadAllFromAssemblyAsync(assembly, includeTestFixtures: false);
        var art9 = all.Single(a => a.Metadata.ControlId == "gdpr.art9.special_categories");
        var sut = MakeSut();

        // Act
        var composite = sut.Build(art9);

        // Assert
        Assert.Equal("Min", composite.Aggregation.Name);
    }

    [Fact]
    public void Build_UnknownAggregation_Throws()
    {
        // Arrange — synthetic spec with an unsupported aggregation value
        var sut = MakeSut();
        var metadata = new AgentEval.GdprBenchmark.Articles.Models.ArticleMetadata(
            Article: "Article-99",
            Pillar: "Pillar1-Foundations",
            ControlId: "gdpr.test.unknown",
            Title: "Unknown aggregation test",
            Severity: "low",
            PassThreshold: 0.70,
            WarnThreshold: 0.50,
            PillarWeight: 0.10,
            Aggregation: "exotic");
        var scenario = new AgentEval.GdprBenchmark.Articles.Models.ScenarioSpec(
            Id: "test-unknown-001",
            Pattern: "direct",
            Weight: 1.0,
            Input: "test input",
            EvaluationCriteria: ["some criterion"],
            Granularity: "atomic",
            Sensitive: false,
            ExpectedBehavior: null,
            Tags: []);
        var spec = new AgentEval.GdprBenchmark.Articles.Models.ArticleSpec(metadata, [scenario]);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => sut.Build(spec));
    }
}
