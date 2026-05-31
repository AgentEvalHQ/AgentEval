// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

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
    public void Registry_LoadsAll29Articles()
    {
        var registry = BuildRegistry();

        // Plan-13 T1.1 added 8 Pillar 6 Governance articles on top of the 21
        // Phase-2 baseline (21 + 8 = 29). Lower-bound assertion keeps the test
        // robust against future Pillar 7+ additions.
        Assert.True(registry.All.Count >= 29,
            $"Expected at least 29 articles, got {registry.All.Count}");

        // Assert specific control IDs are present (mix of baseline + Pillar 6)
        Assert.Contains("gdpr.art17.erasure",         registry.All.Keys);
        Assert.Contains("gdpr.art9.special_categories", registry.All.Keys);
        Assert.Contains("gdpr.art22.automated",       registry.All.Keys);
        Assert.Contains("gdpr.art5.lawfulness",       registry.All.Keys);
        // Plan-13 T1.1 Pillar 6 Governance spot checks:
        Assert.Contains("gdpr.art28.processor_contracts", registry.All.Keys);
        Assert.Contains("gdpr.art33.breach_notification", registry.All.Keys);
        Assert.Contains("gdpr.art35.dpia",                registry.All.Keys);
        Assert.Contains("gdpr.art44_49.international_transfers", registry.All.Keys);
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
    public void Registry_TryGetSpec_KnownAndUnknownIds()
    {
        // MNT-09: non-throwing lookup — true + spec for a known id, false + null for unknown
        // (and for null/whitespace), so callers needn't use exception-as-control-flow.
        var registry = BuildRegistry();

        Assert.True(registry.TryGetSpec("gdpr.art9.special_categories", out var known));
        Assert.NotNull(known);
        Assert.Equal("gdpr.art9.special_categories", known!.Metadata.ControlId);

        Assert.False(registry.TryGetSpec("gdpr.art999.fake", out var missing));
        Assert.Null(missing);

        Assert.False(registry.TryGetSpec("  ", out var blank));
        Assert.Null(blank);
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
