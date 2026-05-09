// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using GdprBenchmarkFactory = AgentEval.GdprBenchmark.GdprBenchmark;
using AgentEval.GdprBenchmark.Articles;
using AgentEval.GdprBenchmark.Articles.Building;
using AgentEval.GdprBenchmark.Articles.Loading;
using Xunit;

namespace AgentEval.Tests.GdprBenchmark;

/// <summary>
/// End-to-end tests for the GDPR Audit-Grade single-judge preset.
/// Exercises <see cref="AgentEval.Evals.CapByWorstAggregation"/> through the full evaluation
/// tree using a programmable stub judge.
/// </summary>
public class E2E_AuditGradeSingleJudgeTest
{
    /// <summary>
    /// Stub evaluator that accepts a delegate to determine the score per evaluation call.
    /// The delegate receives the <c>input</c> string and the <c>criteria</c> list so that
    /// the test can pattern-match on criteria content to target specific scenarios.
    /// NOTE: Discriminating by criteria content is a v1 expedient. It is brittle if YAML
    /// criteria wording changes; for production use, consider passing a scenario-id hint
    /// through the input or a dedicated context object.
    /// </summary>
    private sealed class ProgrammableJudge : AgentEval.Core.IEvaluator
    {
        /// <summary>
        /// Function called per evaluation; returns an integer score (0–100).
        /// Parameters: (input, criteria).
        /// </summary>
        public Func<string, IEnumerable<string>, int> Score { get; init; } = (_, _) => 95;

        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            var crits = criteria.ToList();
            var s = Score(input, crits);
            return Task.FromResult(new AgentEval.Core.EvaluationResult
            {
                OverallScore = s,
                Summary = "stub",
                CriteriaResults = crits
                    .Select(c => new AgentEval.Core.CriterionResult
                    {
                        Criterion = c,
                        Met = s >= 70,
                        Explanation = "stub"
                    })
                    .ToList()
            });
        }
    }

    // ── Registry factory ──────────────────────────────────────────────────────

    private static ArticlesRegistry BuildRegistry(ProgrammableJudge judge)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(judge, judgeModel: "stub");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new ArticlesRegistry(loader, articleBuilder);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AuditGrade_WithArt9CriticalFail_OverallCapsAtFail()
    {
        // Arrange — every article passes (95) EXCEPT Art 9 which fails (20).
        // Discrimination is done by pattern-matching criteria that contain "Article 9"
        // or "special category" phrases present in all seven Art 9 scenarios.
        // NOTE: This pattern-match is intentionally simple (v1 expedient);
        // if criteria wording changes in the YAML, this test must be updated.
        var judge = new ProgrammableJudge
        {
            Score = (input, criteria) =>
            {
                bool isArt9 = criteria.Any(c =>
                    c.Contains("Article 9", StringComparison.OrdinalIgnoreCase)
                    || c.Contains("special category", StringComparison.OrdinalIgnoreCase)
                    || c.Contains("special-category", StringComparison.OrdinalIgnoreCase));
                return isArt9 ? 20 : 95;
            }
        };

        var registry = BuildRegistry(judge);
        var auditGrade = GdprBenchmarkFactory.AuditGrade(registry);
        var input = new EvalInput(Query: "test", Response: "test response");

        // Act
        var result = await auditGrade.EvaluateAsync(input);

        // Assert — Art 9 is severity=critical in the YAML, so a score of 20 (below
        // pass_threshold=0.95) causes a critical-severity fail at the article level.
        // CapByWorstAggregation at the top-level preset must cap the overall score at <= 0.40.
        Assert.False(result.Score.Passed);
        Assert.Equal("critical", result.Score.Severity);
        Assert.True(result.Score.Value <= 0.40,
            $"Expected overall score capped at 0.40 by CapByWorst, got {result.Score.Value}");
        Assert.Equal("CapByWorst", result.Details.AggregationStrategy);
    }

    [Fact]
    public async Task AuditGrade_AllPassing_ReturnsPass()
    {
        // Arrange — every article scores 100; preset threshold is 0.90
        var judge = new ProgrammableJudge { Score = (_, _) => 100 };
        var registry = BuildRegistry(judge);
        var auditGrade = GdprBenchmarkFactory.AuditGrade(registry);

        // Act
        var result = await auditGrade.EvaluateAsync(new EvalInput(Query: "q", Response: "r"));

        // Assert
        Assert.True(result.Score.Passed);
    }
}
