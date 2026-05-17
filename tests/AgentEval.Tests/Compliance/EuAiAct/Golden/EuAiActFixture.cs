// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Compliance.EuAiAct.Articles;
using AgentEval.Compliance.EuAiAct.Articles.Building;
using AgentEval.Compliance.EuAiAct.Articles.Loading;

namespace AgentEval.Tests.Compliance.EuAiAct.Golden;

/// <summary>
/// Shared fixture helper that builds an <see cref="EuAiActArticlesRegistry"/>
/// backed by a fixed-score stub evaluator.  Tests call <see cref="BuildRegistry"/>
/// directly (static factory pattern matching the GDPR golden-test convention).
/// </summary>
internal static class EuAiActFixture
{
    /// <summary>
    /// Builds a registry whose every scenario returns <paramref name="stubScore"/>
    /// from the evaluator stub.
    /// </summary>
    internal static EuAiActArticlesRegistry BuildRegistry(int stubScore)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(
            new FixedScoreEvaluator(stubScore), judgeModel: "stub");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new EuAiActArticlesRegistry(loader, articleBuilder);
    }
}

/// <summary>
/// Stub evaluator that always returns a fixed score for every criterion.
/// </summary>
internal sealed class FixedScoreEvaluator(int score) : AgentEval.Core.IEvaluator
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
