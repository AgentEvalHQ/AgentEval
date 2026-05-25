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
/// Plan-13 T1.1 — focused component tests for the new Pillar 6 Governance and
/// Accountability composite (Art 28, 30, 33, 34, 35, 37-39, 44-49, 5(2)).
/// </summary>
public class Pillar6Governance_Tests
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

    private static ArticlesRegistry BuildRegistry(int stubScore = 95)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(new StubEvaluator(stubScore), judgeModel: "stub");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new ArticlesRegistry(loader, articleBuilder);
    }

    // ── Pillar 6 shape ─────────────────────────────────────────────────────────

    [Fact]
    public void Pillar6_Build_HasExpectedKeyAndComponentCount()
    {
        var registry = BuildRegistry();
        var pillar = Pillar6Governance.Build(registry);

        Assert.Equal("Pillar6-Governance", pillar.Key);
        Assert.Equal(8, pillar.Components.Count);
    }

    [Fact]
    public void Pillar6_ComponentWeights_SumToOne()
    {
        var registry = BuildRegistry();
        var pillar = Pillar6Governance.Build(registry);

        var weightSum = pillar.Components.Sum(c => c.Weight);

        Assert.Equal(1.0, weightSum, precision: 10);
    }

    [Fact]
    public async Task Pillar6_EvaluateAsync_PassingStub_ProducesEightSubResults()
    {
        var registry = BuildRegistry(stubScore: 95);
        var pillar = Pillar6Governance.Build(registry);
        var input = new EvalInput(Query: "test query", Response: "test response");

        var result = await pillar.EvaluateAsync(input);

        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(8, result.Details.SubResults!.Count);
    }

    [Fact]
    public void Pillar6_Composes_ExpectedEightArticleControlIds()
    {
        var registry = BuildRegistry();
        var pillar = Pillar6Governance.Build(registry);

        var keys = pillar.Components.Select(c => c.Eval.Key).ToHashSet();
        Assert.Contains("gdpr.art28.processor_contracts",       keys);
        Assert.Contains("gdpr.art30.records_of_processing",     keys);
        Assert.Contains("gdpr.art33.breach_notification",       keys);
        Assert.Contains("gdpr.art34.breach_communication",      keys);
        Assert.Contains("gdpr.art35.dpia",                      keys);
        Assert.Contains("gdpr.art37_39.dpo",                    keys);
        Assert.Contains("gdpr.art44_49.international_transfers",keys);
        Assert.Contains("gdpr.art5_2.accountability",           keys);
    }

    [Fact]
    public void Pillar6_NullRegistry_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Pillar6Governance.Build(null!));
}
