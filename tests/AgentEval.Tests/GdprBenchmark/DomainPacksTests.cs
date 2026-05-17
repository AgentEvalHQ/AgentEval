// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles;
using AgentEval.GdprBenchmark.Articles.Building;
using AgentEval.GdprBenchmark.Articles.Loading;
using AgentEval.GdprBenchmark.Composition;
using AgentEval.GdprBenchmark.DomainPacks.ChildrensService;
using AgentEval.GdprBenchmark.DomainPacks.Healthcare;
using AgentEval.GdprBenchmark.DomainPacks.HR;
// Alias is required: this file's enclosing namespace `AgentEval.Tests.GdprBenchmark` shadows
// the same-named factory type in `AgentEval.Benchmarks`, and the `using AgentEval.GdprBenchmark.*;`
// directives above also import the parent namespace — both effects make the bare identifier
// `GdprBenchmark` resolve to a namespace, not the factory type (CS0234).
// See lastreview/11-phase4-gate-review.md §4 (Concern A). Do not remove without first
// restructuring this file's namespace or fully-qualifying every call site.
using GdprBenchmarkFactory = AgentEval.Benchmarks.GdprBenchmark;
using Xunit;

namespace AgentEval.Tests.GdprBenchmark;

/// <summary>
/// Tests for the Healthcare, HR, and ChildrensService domain pack factories and
/// their interaction with the Standard benchmark via WithExtraScenarios.
/// </summary>
public class DomainPacksTests
{
    // ── Stub ──────────────────────────────────────────────────────────────────

    private sealed class FakeEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken ct = default) =>
            Task.FromResult(new EvaluationResult { OverallScore = 80 });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (ArticlesRegistry articles, ScenarioToAtomicEval scenarioBuilder) MakeRegistry()
    {
        var judge = new FakeEvaluator();
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(judge, judgeModel: "stub");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        var articles = new ArticlesRegistry(loader, articleBuilder);
        return (articles, scenarioBuilder);
    }

    /// <summary>
    /// Recursively finds a composite with the given key anywhere in the tree.
    /// </summary>
    private static CompositeEval? FindArticle(CompositeEval node, string key)
    {
        if (node.Key == key) return node;
        foreach (var c in node.Components)
        {
            if (c.Eval is CompositeEval child)
            {
                var found = FindArticle(child, key);
                if (found is not null) return found;
            }
        }
        return null;
    }

    // ── Healthcare tests ──────────────────────────────────────────────────────

    [Fact]
    public void Healthcare_AddsScenarios_To_Targeted_Articles()
    {
        // Arrange
        var (articles, scenarioBuilder) = MakeRegistry();
        var standard = GdprBenchmarkFactory.Standard(articles);
        var standardArt9 = FindArticle(standard, "gdpr.art9.special_categories")!;
        var standardArt32 = FindArticle(standard, "gdpr.art32.security")!;
        var originalArt9Count = standardArt9.Components.Count;
        var originalArt32Count = standardArt32.Components.Count;

        // Act
        var healthcare = GdprBenchmarkFactory.Healthcare(articles, scenarioBuilder);

        // Assert
        var augArt9 = FindArticle(healthcare, "gdpr.art9.special_categories")!;
        var augArt32 = FindArticle(healthcare, "gdpr.art32.security")!;

        Assert.True(augArt9.Components.Count > originalArt9Count,
            "Healthcare pack should add scenarios to Art 9.");
        Assert.True(augArt32.Components.Count > originalArt32Count,
            "Healthcare pack should add scenarios to Art 32.");
    }

    [Fact]
    public void Healthcare_PreservesOriginal_NonTargetedArticles()
    {
        // Arrange
        var (articles, scenarioBuilder) = MakeRegistry();
        var standard = GdprBenchmarkFactory.Standard(articles);
        var standardArt17 = FindArticle(standard, "gdpr.art17.erasure")!;
        var originalArt17Count = standardArt17.Components.Count;

        // Act
        var healthcare = GdprBenchmarkFactory.Healthcare(articles, scenarioBuilder);

        // Assert — Art 17 (erasure) is not targeted by the healthcare pack
        var augArt17 = FindArticle(healthcare, "gdpr.art17.erasure")!;
        Assert.Equal(originalArt17Count, augArt17.Components.Count);
    }

    // ── HR tests ──────────────────────────────────────────────────────────────

    [Fact]
    public void HR_AddsScenarios_To_Targeted_Articles()
    {
        // Arrange
        var (articles, scenarioBuilder) = MakeRegistry();
        var standard = GdprBenchmarkFactory.Standard(articles);
        var standardArt22 = FindArticle(standard, "gdpr.art22.automated")!;
        var standardArt6 = FindArticle(standard, "gdpr.art6.lawful_basis")!;
        var originalArt22Count = standardArt22.Components.Count;
        var originalArt6Count = standardArt6.Components.Count;

        // Act
        var hr = GdprBenchmarkFactory.HR(articles, scenarioBuilder);

        // Assert
        var augArt22 = FindArticle(hr, "gdpr.art22.automated")!;
        var augArt6 = FindArticle(hr, "gdpr.art6.lawful_basis")!;

        Assert.True(augArt22.Components.Count > originalArt22Count,
            "HR pack should add scenarios to Art 22.");
        Assert.True(augArt6.Components.Count > originalArt6Count,
            "HR pack should add scenarios to Art 6.");
    }

    [Fact]
    public void HR_PreservesOriginal_NonTargetedArticles()
    {
        // Arrange
        var (articles, scenarioBuilder) = MakeRegistry();
        var standard = GdprBenchmarkFactory.Standard(articles);
        var standardArt9 = FindArticle(standard, "gdpr.art9.special_categories")!;
        var originalArt9Count = standardArt9.Components.Count;

        // Act
        var hr = GdprBenchmarkFactory.HR(articles, scenarioBuilder);

        // Assert — Art 9 is not targeted by the HR pack
        var augArt9 = FindArticle(hr, "gdpr.art9.special_categories")!;
        Assert.Equal(originalArt9Count, augArt9.Components.Count);
    }

    // ── ChildrensService tests ────────────────────────────────────────────────

    [Fact]
    public void ChildrensService_AddsScenarios_To_Targeted_Articles()
    {
        // Arrange
        var (articles, scenarioBuilder) = MakeRegistry();
        var standard = GdprBenchmarkFactory.Standard(articles);
        var standardArt8 = FindArticle(standard, "gdpr.art8.children")!;
        var standardArt13 = FindArticle(standard, "gdpr.art13.information_collection")!;
        var originalArt8Count = standardArt8.Components.Count;
        var originalArt13Count = standardArt13.Components.Count;

        // Act
        var childrens = GdprBenchmarkFactory.ChildrensService(articles, scenarioBuilder);

        // Assert
        var augArt8 = FindArticle(childrens, "gdpr.art8.children")!;
        var augArt13 = FindArticle(childrens, "gdpr.art13.information_collection")!;

        Assert.True(augArt8.Components.Count > originalArt8Count,
            "ChildrensService pack should add scenarios to Art 8.");
        Assert.True(augArt13.Components.Count > originalArt13Count,
            "ChildrensService pack should add scenarios to Art 13.");
    }

    // ── Combined pack tests ───────────────────────────────────────────────────

    [Fact]
    public void Healthcare_Combined_With_HR_BothPacksApply()
    {
        // Arrange
        var (articles, scenarioBuilder) = MakeRegistry();
        var standard = GdprBenchmarkFactory.Standard(articles);
        var originalArt9Count = FindArticle(standard, "gdpr.art9.special_categories")!.Components.Count;
        var originalArt22Count = FindArticle(standard, "gdpr.art22.automated")!.Components.Count;

        // Act — apply healthcare additions then HR additions
        var healthcareAdditions = HealthcareScenarios.Load(scenarioBuilder);
        var hrAdditions = HRScenarios.Load(scenarioBuilder);

        var combined = standard
            .WithExtraScenarios(healthcareAdditions)
            .WithExtraScenarios(hrAdditions);

        // Assert — both packs' articles are augmented
        var combinedArt9 = FindArticle(combined, "gdpr.art9.special_categories")!;
        var combinedArt22 = FindArticle(combined, "gdpr.art22.automated")!;

        Assert.True(combinedArt9.Components.Count > originalArt9Count,
            "Healthcare scenarios should be applied to Art 9.");
        Assert.True(combinedArt22.Components.Count > originalArt22Count,
            "HR scenarios should be applied to Art 22.");
    }
}
