// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;

namespace AgentEval.Tests.Compliance.Gdpr.Golden;

/// <summary>
/// Shared fixture helper for plan-13 T1.1 Pillar 6 Governance per-article golden
/// tests. Builds an <see cref="ArticlesRegistry"/> backed by a fixed-score stub
/// evaluator (mirrors the EU AI Act <c>EuAiActFixture</c> pattern that T1.2
/// introduced).
/// </summary>
internal static class GdprFixture
{
    /// <summary>
    /// Builds a registry whose every scenario returns <paramref name="stubScore"/>
    /// (0-100) from the evaluator stub.
    /// </summary>
    internal static ArticlesRegistry BuildRegistry(int stubScore)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(
            new GdprFixedScoreEvaluator(stubScore), judgeModel: "stub");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new ArticlesRegistry(loader, articleBuilder);
    }
}

/// <summary>
/// Stub evaluator that always returns a fixed score for every criterion. Used by
/// the Pillar 6 per-article golden tests for deterministic build / evaluate
/// verification without LLM cost.
/// </summary>
internal sealed class GdprFixedScoreEvaluator(int score) : AgentEval.Core.IEvaluator
{
    public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
        string input,
        string output,
        IEnumerable<string> criteria,
        CancellationToken ct = default) =>
        Task.FromResult(new AgentEval.Core.EvaluationResult
        {
            OverallScore = score,
            Summary = "stub",
            CriteriaResults = criteria
                .Select(x => new AgentEval.Core.CriterionResult
                {
                    Criterion = x,
                    Met = score >= 50,
                    Explanation = "stub"
                })
                .ToList()
        });
}
