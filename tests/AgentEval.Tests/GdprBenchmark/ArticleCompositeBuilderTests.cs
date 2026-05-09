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
        Assert.Equal(0.80, composite.Threshold);
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
}
