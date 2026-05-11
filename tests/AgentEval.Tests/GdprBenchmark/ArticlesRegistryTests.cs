// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.GdprBenchmark.Articles;
using AgentEval.GdprBenchmark.Articles.Building;
using AgentEval.GdprBenchmark.Articles.Loading;
using Xunit;

namespace AgentEval.Tests.GdprBenchmark;

public class ArticlesRegistryTests
{
    // ── Stub ──────────────────────────────────────────────────────────────────

    private sealed class FakeEvaluator : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input,
            string output,
            IEnumerable<string> criteria,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Registry construction does not invoke the evaluator.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArticlesRegistry BuildRegistry()
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(new FakeEvaluator(), judgeModel: "stub");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new ArticlesRegistry(loader, articleBuilder);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_LoadsAll21Articles()
    {
        var registry = BuildRegistry();

        Assert.True(registry.All.Count >= 21,
            $"Expected at least 21 articles, got {registry.All.Count}");

        // Assert specific control IDs are present
        Assert.Contains("gdpr.art17.erasure",         registry.All.Keys);
        Assert.Contains("gdpr.art9.special_categories", registry.All.Keys);
        Assert.Contains("gdpr.art22.automated",       registry.All.Keys);
        Assert.Contains("gdpr.art5.lawfulness",       registry.All.Keys);
    }

    [Fact]
    public void Registry_GetByControlId_ReturnsExpectedComposite()
    {
        var registry = BuildRegistry();

        var composite = registry.Get("gdpr.art17.erasure");

        Assert.Equal("gdpr.art17.erasure", composite.Key);
        Assert.Equal("Right to erasure ('right to be forgotten')", composite.Name);
        Assert.Equal(0.70, composite.Threshold);
    }

    [Fact]
    public void Registry_GetUnknownId_Throws()
    {
        var registry = BuildRegistry();

        Assert.Throws<KeyNotFoundException>(() => registry.Get("gdpr.art999.fake"));
    }

    [Fact]
    public void Registry_GetSpec_ReturnsArticleSpec()
    {
        var registry = BuildRegistry();

        var spec = registry.GetSpec("gdpr.art9.special_categories");

        Assert.Equal("gdpr.art9.special_categories", spec.Metadata.ControlId);
        Assert.Equal("min", spec.Metadata.Aggregation);
    }

    [Fact]
    public void Registry_GetSpec_UnknownId_Throws()
    {
        var registry = BuildRegistry();

        Assert.Throws<KeyNotFoundException>(() => registry.GetSpec("gdpr.art999.fake"));
    }

    [Fact]
    public void Registry_AllArticles_HaveNonZeroComponents()
    {
        var registry = BuildRegistry();

        foreach (var (controlId, composite) in registry.All)
        {
            Assert.True(composite.Components.Count > 0,
                $"Article '{controlId}' has no components.");
        }
    }

    [Fact]
    public void Constructor_NullLoader_Throws()
    {
        var scenarioBuilder = new ScenarioToAtomicEval(new FakeEvaluator());
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        Assert.Throws<ArgumentNullException>(() => new ArticlesRegistry(null!, articleBuilder));
    }

    [Fact]
    public void Constructor_NullBuilder_Throws()
    {
        var loader = new ArticleScenarioYamlLoader();
        Assert.Throws<ArgumentNullException>(() => new ArticlesRegistry(loader, null!));
    }
}
