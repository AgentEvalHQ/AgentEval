// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using AgentEval.Compliance.Gdpr.Pillars;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

/// <summary>
/// Parameterized tests for Phase 3 G3.1-G3.6 + plan-13 T1.1: verifies all six GDPR
/// pillar composites build correctly and produce the expected evaluation structure
/// when run against stub evaluators. Pillar 6 (Governance and Accountability) added
/// in v1.1 / plan-13 T1.1 with the 8 governance article YAMLs.
/// </summary>
public class PillarCompositesTests
{
    // ── Stub evaluator ────────────────────────────────────────────────────────

    private sealed class StubEvaluator : AgentEval.Core.IEvaluator
    {
        private readonly int _score;
        public StubEvaluator(int score) { _score = score; }

        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input,
            string output,
            IEnumerable<string> criteria,
            CancellationToken ct = default)
        {
            var crits = criteria.ToList();
            return Task.FromResult(new AgentEval.Core.EvaluationResult
            {
                OverallScore = _score,
                Summary = "stub",
                CriteriaResults = crits
                    .Select(c => new AgentEval.Core.CriterionResult
                    {
                        Criterion = c,
                        Met = _score >= 70,
                        Explanation = "stub"
                    })
                    .ToList()
            });
        }
    }

    // ── Registry factory ──────────────────────────────────────────────────────

    private static ArticlesRegistry BuildRegistry(int stubScore = 95)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(new StubEvaluator(stubScore), judgeModel: "stub");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new ArticlesRegistry(loader, articleBuilder);
    }

    // ── Pillar build helpers ──────────────────────────────────────────────────

    private static CompositeEval BuildPillar(int pillarNumber, ArticlesRegistry registry) =>
        pillarNumber switch
        {
            1 => Pillar1Foundations.Build(registry),
            2 => Pillar2LawfulBasis.Build(registry),
            3 => Pillar3SubjectRights.Build(registry),
            4 => Pillar4Transparency.Build(registry),
            5 => Pillar5PrivacyDesign.Build(registry),
            6 => Pillar6Governance.Build(registry),
            _ => throw new ArgumentOutOfRangeException(nameof(pillarNumber))
        };

    // ── Theory: key, component count, weight sum ──────────────────────────────

    [Theory]
    [InlineData(1, "Pillar1-Foundations",   8)]
    [InlineData(2, "Pillar2-LawfulBasis",   2)]
    [InlineData(3, "Pillar3-SubjectRights", 7)]
    [InlineData(4, "Pillar4-Transparency",  2)]
    [InlineData(5, "Pillar5-PrivacyDesign", 2)]
    [InlineData(6, "Pillar6-Governance",    8)]
    public void Pillar_Build_HasCorrectKeyAndComponentCount(
        int pillarNumber,
        string expectedKey,
        int expectedArticleCount)
    {
        var registry = BuildRegistry();
        var pillar = BuildPillar(pillarNumber, registry);

        Assert.Equal(expectedKey, pillar.Key);
        Assert.Equal(expectedArticleCount, pillar.Components.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Pillar_ComponentWeights_SumToOne(int pillarNumber)
    {
        var registry = BuildRegistry();
        var pillar = BuildPillar(pillarNumber, registry);

        var weightSum = pillar.Components.Sum(c => c.Weight);

        Assert.Equal(1.0, weightSum, precision: 10);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(2, 2)]
    [InlineData(3, 7)]
    [InlineData(4, 2)]
    [InlineData(5, 2)]
    [InlineData(6, 8)]
    public async Task Pillar_EvaluateAsync_PassingStub_ProducesSubResultsPerArticle(
        int pillarNumber,
        int expectedSubResultCount)
    {
        var registry = BuildRegistry(stubScore: 95);
        var pillar = BuildPillar(pillarNumber, registry);
        var input = new EvalInput(Query: "test query", Response: "test response");

        var result = await pillar.EvaluateAsync(input);

        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(expectedSubResultCount, result.Details.SubResults!.Count);
    }

    // ── Smoke test: all 6 pillars build without exceptions ───────────────────

    [Fact]
    public void AllPillars_Build_WithoutException()
    {
        var registry = BuildRegistry();

        var pillar1 = Pillar1Foundations.Build(registry);
        var pillar2 = Pillar2LawfulBasis.Build(registry);
        var pillar3 = Pillar3SubjectRights.Build(registry);
        var pillar4 = Pillar4Transparency.Build(registry);
        var pillar5 = Pillar5PrivacyDesign.Build(registry);
        var pillar6 = Pillar6Governance.Build(registry);

        Assert.NotNull(pillar1);
        Assert.NotNull(pillar2);
        Assert.NotNull(pillar3);
        Assert.NotNull(pillar4);
        Assert.NotNull(pillar5);
        Assert.NotNull(pillar6);
    }

    // ── Specific key checks ───────────────────────────────────────────────────

    [Fact]
    public void Pillar1_Key_IsCorrect()
    {
        var pillar = Pillar1Foundations.Build(BuildRegistry());
        Assert.Equal("Pillar1-Foundations", pillar.Key);
    }

    [Fact]
    public void Pillar3_Key_IsCorrect()
    {
        var pillar = Pillar3SubjectRights.Build(BuildRegistry());
        Assert.Equal("Pillar3-SubjectRights", pillar.Key);
    }

    // ── Null guard tests ──────────────────────────────────────────────────────

    [Fact]
    public void Pillar1_NullRegistry_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Pillar1Foundations.Build(null!));

    [Fact]
    public void Pillar2_NullRegistry_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Pillar2LawfulBasis.Build(null!));

    [Fact]
    public void Pillar3_NullRegistry_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Pillar3SubjectRights.Build(null!));

    [Fact]
    public void Pillar4_NullRegistry_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Pillar4Transparency.Build(null!));

    [Fact]
    public void Pillar5_NullRegistry_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Pillar5PrivacyDesign.Build(null!));

    [Fact]
    public void Pillar6_NullRegistry_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Pillar6Governance.Build(null!));

    // ── PillarCompositeBuilder guard tests ────────────────────────────────────

    [Fact]
    public void PillarCompositeBuilder_NullKey_Throws()
    {
        var registry = BuildRegistry();
        var articles = new[] { (registry.Get("gdpr.art6.lawful_basis"), 1.0) };
        // ThrowIfNullOrWhiteSpace raises ArgumentNullException for null (subclass of ArgumentException)
        Assert.ThrowsAny<ArgumentException>(() =>
            PillarCompositeBuilder.Build(null!, "Name", articles));
    }

    [Fact]
    public void PillarCompositeBuilder_EmptyArticleList_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            PillarCompositeBuilder.Build("key", "name", Array.Empty<(CompositeEval, double)>()));
    }

    [Fact]
    public void PillarCompositeBuilder_NullArticles_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PillarCompositeBuilder.Build("key", "name", null!));
    }

    // ── Threshold is null at pillar level ─────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Pillar_Threshold_IsNull(int pillarNumber)
    {
        var registry = BuildRegistry();
        var pillar = BuildPillar(pillarNumber, registry);

        Assert.Null(pillar.Threshold);
    }
}
