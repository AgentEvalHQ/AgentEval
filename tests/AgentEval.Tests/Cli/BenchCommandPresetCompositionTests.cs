// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles;
using AgentEval.GdprBenchmark.Articles.Building;
using AgentEval.GdprBenchmark.Articles.Loading;
using Xunit;
using GdprBenchmarkFactory = AgentEval.GdprBenchmark.GdprBenchmark;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for the <see cref="BenchCommand.ResolvePreset"/> additive composition logic.
/// </summary>
public class BenchCommandPresetCompositionTests
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

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolvePreset_BareStandard_ReturnsStandard()
    {
        // Arrange
        var (articles, scenarioBuilder) = MakeRegistry();
        var expected = GdprBenchmarkFactory.Standard(articles);

        // Act
        var result = BenchCommand.ResolvePreset("standard", articles, scenarioBuilder);

        // Assert
        Assert.Equal("gdpr.compliance.standard", result.Key);
        Assert.Equal(expected.Components.Count, result.Components.Count);
    }

    [Fact]
    public void ResolvePreset_StandardPlusHealthcare_ReturnsAugmentedComposite()
    {
        // Arrange
        var (articles, scenarioBuilder) = MakeRegistry();
        var standard = GdprBenchmarkFactory.Standard(articles);
        var standardArt9 = FindArticle(standard, "gdpr.art9.special_categories")!;
        var originalArt9Count = standardArt9.Components.Count;

        // Act
        var result = BenchCommand.ResolvePreset("standard+healthcare", articles, scenarioBuilder);

        // Assert
        var augArt9 = FindArticle(result, "gdpr.art9.special_categories")!;
        Assert.True(augArt9.Components.Count > originalArt9Count,
            "standard+healthcare should add healthcare scenarios to Art 9.");
    }

    [Fact]
    public void ResolvePreset_UnknownPack_Throws()
    {
        // Arrange
        var (articles, scenarioBuilder) = MakeRegistry();

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(
            () => BenchCommand.ResolvePreset("standard+unknownpack", articles, scenarioBuilder));
        Assert.Contains("unknownpack", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePreset_UnknownBasePreset_Throws()
    {
        // Arrange
        var (articles, scenarioBuilder) = MakeRegistry();

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(
            () => BenchCommand.ResolvePreset("enterprise", articles, scenarioBuilder));
        Assert.Contains("enterprise", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePreset_StandardPlusHealthcarePlusHR_BothPacksApplied()
    {
        // Arrange
        var (articles, scenarioBuilder) = MakeRegistry();
        var standard = GdprBenchmarkFactory.Standard(articles);
        var originalArt9Count = FindArticle(standard, "gdpr.art9.special_categories")!.Components.Count;
        var originalArt22Count = FindArticle(standard, "gdpr.art22.automated")!.Components.Count;

        // Act
        var result = BenchCommand.ResolvePreset("standard+healthcare+hr", articles, scenarioBuilder);

        // Assert
        Assert.True(FindArticle(result, "gdpr.art9.special_categories")!.Components.Count > originalArt9Count,
            "Healthcare scenarios should be added to Art 9.");
        Assert.True(FindArticle(result, "gdpr.art22.automated")!.Components.Count > originalArt22Count,
            "HR scenarios should be added to Art 22.");
    }
}
