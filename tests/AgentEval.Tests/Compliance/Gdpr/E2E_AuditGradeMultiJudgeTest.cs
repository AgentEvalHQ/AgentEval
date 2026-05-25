// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

// Phase-8 Task 8.5: MultiJudgeOptions is now [Obsolete] but this E2E test
// is the regression net for the existing AuditGrade(multiJudge) path — we
// intentionally keep exercising the deprecated surface to prove backward
// compatibility while it remains available in v1.
#pragma warning disable CS0618 // MultiJudgeOptions is obsolete

using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Compliance.Gdpr;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

/// <summary>
/// End-to-end tests for the GDPR Audit-Grade multi-judge preset (Phase 7 / G7.7).
/// Uses stub judges to exercise the <see cref="MultiJudgeWrapper"/> path for Critical
/// articles (Art 9, Art 22).
/// </summary>
public class E2E_AuditGradeMultiJudgeTest
{
    // ── Stub judge ────────────────────────────────────────────────────────────

    private sealed class FixedScoreJudge : AgentEval.Core.IEvaluator
    {
        private readonly int _score;
        public FixedScoreJudge(int score) { _score = score; }

        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
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

    private static ArticlesRegistry BuildMultiJudgeRegistry(
        AgentEval.Core.IEvaluator primaryJudge,
        IReadOnlyList<(AgentEval.Core.IEvaluator Judge, double Weight)> allJudges)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(
            primaryJudge,
            judgeModel: "stub",
            judges: allJudges);
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new ArticlesRegistry(loader, articleBuilder);
    }

    private static ArticlesRegistry BuildSingleJudgeRegistry(AgentEval.Core.IEvaluator judge)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(judge, judgeModel: "stub");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new ArticlesRegistry(loader, articleBuilder);
    }

    private static readonly EvalInput BenchmarkInput = new(Query: "test", Response: "test response");

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AuditGrade_MultiJudge_AllJudgesScoreAboveThreshold_CompositePassesOverall()
    {
        // Arrange — 3 judges all scoring 95/100 = 0.95, which exceeds Art 9's pass_threshold (0.90).
        // All three judges agree on PASS. Median score = 0.95; severity rollup = none → pass.
        var judge95 = new FixedScoreJudge(95);
        var judges = new (AgentEval.Core.IEvaluator Judge, double Weight)[]
        {
            (judge95, 1.0),
            (judge95, 1.0),
            (judge95, 1.0),
        };

        var registry = BuildMultiJudgeRegistry(judge95, judges);
        var auditGrade = GdprBenchmark.AuditGrade(registry, new MultiJudgeOptions(judges));

        // Act
        var result = await auditGrade.EvaluateAsync(BenchmarkInput);

        // Assert
        Assert.True(result.Score.Passed,
            $"Expected pass but got {result.Score.Label} (score={result.Score.Value:F3}, severity={result.Score.Severity})");
    }

    [Fact]
    public async Task AuditGrade_MultiJudge_Art9TwoAgreeOneFails_WeightedMedianProducesFailResult()
    {
        // Arrange — 2 judges agree on PASS (95), 1 dissents with 20 (which is < 0.90 threshold).
        // Art 9 severity = critical; the failing judge (score=20) produces a critical-severity fail.
        // WeightedMedian with equal weights: sorted scores [0.20, 0.95, 0.95]; half-weight=1.5;
        // cumulative: 0.20 (cum=1), 0.95 (cum=2 >= 1.5) → median = 0.95.
        // However severity rollup = max(none, none, critical) = critical → label = fail.
        // This demonstrates the expected behavior: even with median passing, a critical dissent
        // propagates severity. The CapByWorst then caps the overall score.
        var agreeingJudge = new FixedScoreJudge(95);
        var dissentingJudge = new FixedScoreJudge(20); // scores 20 → 0.20 < 0.90 → critical fail

        var judges = new (AgentEval.Core.IEvaluator Judge, double Weight)[]
        {
            (agreeingJudge, 1.0),
            (agreeingJudge, 1.0),
            (dissentingJudge, 1.0),
        };

        var registry = BuildMultiJudgeRegistry(agreeingJudge, judges);
        var auditGrade = GdprBenchmark.AuditGrade(registry, new MultiJudgeOptions(judges));

        // Act
        var result = await auditGrade.EvaluateAsync(BenchmarkInput);

        // Assert — severity rolls up to critical because of the dissenting judge;
        // CapByWorstAggregation caps overall score at 0.40 and forces fail.
        Assert.False(result.Score.Passed);
        Assert.Equal("critical", result.Score.Severity);
        Assert.True(result.Score.Value <= 0.40,
            $"Expected CapByWorst to cap score at 0.40, got {result.Score.Value:F3}");
    }

    [Fact]
    public async Task AuditGrade_MultiJudge_Name_ReflectsMultiJudgeMode()
    {
        // Arrange
        var judge = new FixedScoreJudge(95);
        var judges = new (AgentEval.Core.IEvaluator Judge, double Weight)[]
        {
            (judge, 1.0),
            (judge, 1.0),
        };
        var registry = BuildMultiJudgeRegistry(judge, judges);

        // Act
        var multiJudgeAudit = GdprBenchmark.AuditGrade(registry, new MultiJudgeOptions(judges));
        var singleJudgeAudit = GdprBenchmark.AuditGrade(registry, multiJudge: null);

        // Assert — the names differ for traceability
        Assert.Contains("multi-judge", multiJudgeAudit.Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("multi-judge", singleJudgeAudit.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuditGrade_SingleJudge_BackwardCompatibility_NullMultiJudge_Works()
    {
        // Arrange — verify both single-judge overloads produce a valid CompositeEval
        var judge = new FixedScoreJudge(95);
        var registry = BuildSingleJudgeRegistry(judge);

        // Act
        var singleJudgePreset1 = GdprBenchmark.AuditGrade(registry);
        var singleJudgePreset2 = GdprBenchmark.AuditGrade(registry, multiJudge: null);

        // Assert
        Assert.Equal("gdpr.compliance.auditgrade", singleJudgePreset1.Key);
        Assert.Equal("gdpr.compliance.auditgrade", singleJudgePreset2.Key);
        Assert.Equal("CapByWorst", singleJudgePreset1.Aggregation.Name);
        Assert.Equal("CapByWorst", singleJudgePreset2.Aggregation.Name);
    }

    [Fact]
    public async Task AuditGrade_MultiJudge_CriticalArticle_HasMultiJudgeSubResults()
    {
        // Arrange — 3 judges all scoring 95; Critical articles produce MultiJudgeWrapper sub-results.
        var judge = new FixedScoreJudge(95);
        var judges = new (AgentEval.Core.IEvaluator Judge, double Weight)[]
        {
            (judge, 1.0),
            (judge, 1.0),
            (judge, 1.0),
        };

        var registry = BuildMultiJudgeRegistry(judge, judges);
        var auditGrade = GdprBenchmark.AuditGrade(registry, new MultiJudgeOptions(judges));

        // Act
        var result = await auditGrade.EvaluateAsync(BenchmarkInput);

        // Assert — well-formed 6-pillar composite (plan-13 T1.1 added Pillar 6 Governance)
        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(6, result.Details.SubResults!.Count); // 6 pillars

        // Verify that at least one leaf uses WeightedMedian (from MultiJudgeWrapper on Critical articles)
        var multiJudgeLeaves = FindMultiJudgeLeaves(result, minSubResults: 3);
        Assert.NotEmpty(multiJudgeLeaves);
    }

    private static List<EvalResult> FindMultiJudgeLeaves(EvalResult node, int minSubResults)
    {
        var found = new List<EvalResult>();
        var subs = node.Details.SubResults;
        if (subs is not null && subs.Count >= minSubResults && node.Details.AggregationStrategy == "WeightedMedian")
        {
            found.Add(node);
        }
        if (subs is not null)
        {
            foreach (var sub in subs)
                found.AddRange(FindMultiJudgeLeaves(sub, minSubResults));
        }
        return found;
    }
}

#pragma warning restore CS0618
