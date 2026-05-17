// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

/// <summary>
/// Parameterized golden test for Phase 2 — exercises all 21 GDPR article composites
/// with stub evaluators returning fixed scores. Verifies build correctness and
/// end-to-end evaluation pipeline for every control_id.
/// </summary>
public class AllArticlesBuildAndExecuteTest
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

    private static ArticlesRegistry BuildRegistry(int stubScore)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(new StubEvaluator(stubScore), judgeModel: "stub");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new ArticlesRegistry(loader, articleBuilder);
    }

    // ── Passing theory — all 21 articles ─────────────────────────────────────

    [Theory]
    [InlineData("gdpr.art5.lawfulness")]
    [InlineData("gdpr.art5.purpose")]
    [InlineData("gdpr.art5.minimization")]
    [InlineData("gdpr.art5.accuracy")]
    [InlineData("gdpr.art5.retention")]
    [InlineData("gdpr.art5.integrity")]
    [InlineData("gdpr.art8.children")]
    [InlineData("gdpr.art9.special_categories")]
    [InlineData("gdpr.art6.lawful_basis")]
    [InlineData("gdpr.art7.consent")]
    [InlineData("gdpr.art13.information_collection")]
    [InlineData("gdpr.art14.information_indirect")]
    [InlineData("gdpr.art15.access")]
    [InlineData("gdpr.art16.rectification")]
    [InlineData("gdpr.art17.erasure")]
    [InlineData("gdpr.art18.restriction")]
    [InlineData("gdpr.art20.portability")]
    [InlineData("gdpr.art21.object")]
    [InlineData("gdpr.art22.automated")]
    [InlineData("gdpr.art25.privacy_by_design")]
    [InlineData("gdpr.art32.security")]
    public async Task Article_Composite_PassingStub_AllScenariosPass(string controlId)
    {
        var registry = BuildRegistry(stubScore: 95);
        var article = registry.Get(controlId);
        var input = new EvalInput(Query: "test", Response: "test response");

        var result = await article.EvaluateAsync(input);

        Assert.Equal(controlId, result.Metric.Key);
        Assert.Equal(article.Components.Count, result.Details.SubResults!.Count);
        Assert.True(result.Score.Value >= 0.85,
            $"{controlId}: expected score >= 0.85 with all-passing stubs, got {result.Score.Value}");
    }

    // ── Failing theory — selected critical articles ────────────────────────────

    [Theory]
    [InlineData("gdpr.art17.erasure")]
    [InlineData("gdpr.art9.special_categories")]
    [InlineData("gdpr.art22.automated")]
    public async Task Article_Composite_FailingStub_FailsBelowThreshold(string controlId)
    {
        var registry = BuildRegistry(stubScore: 20);
        var article = registry.Get(controlId);
        var input = new EvalInput(Query: "test", Response: "bad response");

        var result = await article.EvaluateAsync(input);

        Assert.False(result.Score.Passed, $"{controlId} should fail with stubScore=20");
        Assert.True(result.Score.Value < article.Threshold,
            $"{controlId}: expected score < threshold ({article.Threshold}), got {result.Score.Value}");
    }
}
