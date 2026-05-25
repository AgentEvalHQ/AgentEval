// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

// plan-13 T2.8 (v1.1 — EU AI Act parity with GDPR sibling): E2E coverage
// for the AuditGrade multi-judge path. MultiJudgeOptions is [Obsolete] (the
// historical Phase-8 Task 8.5 deprecation), but this E2E test is the
// regression net for the existing AuditGrade(multiJudge) surface — we
// intentionally keep exercising the deprecated surface to prove backward
// compatibility while it remains available in v1.
#pragma warning disable CS0618 // MultiJudgeOptions is obsolete

using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Compliance.EuAiAct;
using AgentEval.Compliance.EuAiAct.Articles;
using AgentEval.Compliance.EuAiAct.Articles.Building;
using AgentEval.Compliance.EuAiAct.Articles.Loading;
using Xunit;

namespace AgentEval.Tests.Compliance.EuAiAct;

/// <summary>
/// Phase-8 T2.8 — End-to-end tests for the EU AI Act Audit-Grade multi-judge preset.
/// Mirrors <c>tests/AgentEval.Tests/Compliance/Gdpr/E2E_AuditGradeMultiJudgeTest.cs</c>
/// to give regulation parity coverage. Uses stub judges to exercise the
/// <see cref="MultiJudgeWrapper"/> path for Critical articles (Art 5, Art 9).
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

    private static EuAiActArticlesRegistry BuildMultiJudgeRegistry(
        AgentEval.Core.IEvaluator primaryJudge,
        IReadOnlyList<(AgentEval.Core.IEvaluator Judge, double Weight)> allJudges)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(
            primaryJudge,
            judgeModel: "stub",
            judges: allJudges);
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new EuAiActArticlesRegistry(loader, articleBuilder);
    }

    private static EuAiActArticlesRegistry BuildSingleJudgeRegistry(AgentEval.Core.IEvaluator judge)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(judge, judgeModel: "stub");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new EuAiActArticlesRegistry(loader, articleBuilder);
    }

    private static readonly EvalInput BenchmarkInput = new(Query: "test", Response: "test response");

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AuditGrade_MultiJudge_AllJudgesScoreAboveThreshold_CompositePassesOverall()
    {
        // Arrange — 3 judges all scoring 95/100 = 0.95, which exceeds the Audit-Grade
        // pass threshold (0.90). All three judges agree on PASS.
        var judge95 = new FixedScoreJudge(95);
        var judges = new (AgentEval.Core.IEvaluator Judge, double Weight)[]
        {
            (judge95, 1.0),
            (judge95, 1.0),
            (judge95, 1.0),
        };

        var registry = BuildMultiJudgeRegistry(judge95, judges);
        var auditGrade = EuAiActBenchmark.AuditGrade(registry, new MultiJudgeOptions(judges));

        // Act
        var result = await auditGrade.EvaluateAsync(BenchmarkInput);

        // Assert
        Assert.True(result.Score.Passed,
            $"Expected pass but got {result.Score.Label} (score={result.Score.Value:F3}, severity={result.Score.Severity})");
    }

    [Fact]
    public async Task AuditGrade_MultiJudge_DissentingJudge_CapByWorstSemanticsApply()
    {
        // Arrange — 2 judges agree on PASS (95), 1 dissents with 20 (fail).
        // Critical-severity articles (Art 5, Art 9) produce critical-severity sub-results
        // from the dissenting judge; CapByWorst then caps the composite score at 0.40
        // and forces the overall verdict to fail.
        var agreeingJudge = new FixedScoreJudge(95);
        var dissentingJudge = new FixedScoreJudge(20);

        var judges = new (AgentEval.Core.IEvaluator Judge, double Weight)[]
        {
            (agreeingJudge, 1.0),
            (agreeingJudge, 1.0),
            (dissentingJudge, 1.0),
        };

        var registry = BuildMultiJudgeRegistry(agreeingJudge, judges);
        var auditGrade = EuAiActBenchmark.AuditGrade(registry, new MultiJudgeOptions(judges));

        // Act
        var result = await auditGrade.EvaluateAsync(BenchmarkInput);

        // Assert — CapByWorstAggregation must cap the composite at 0.40 and surface
        // the critical severity from the dissenting judge.
        Assert.False(result.Score.Passed,
            $"Expected fail under CapByWorst with dissenting critical judge, got {result.Score.Label} (score={result.Score.Value:F3})");
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
        var multiJudgeAudit = EuAiActBenchmark.AuditGrade(registry, new MultiJudgeOptions(judges));
        var singleJudgeAudit = EuAiActBenchmark.AuditGrade(registry, multiJudge: null);

        // Assert — the names differ for traceability
        Assert.Contains("multi-judge", multiJudgeAudit.Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("multi-judge", singleJudgeAudit.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuditGrade_SingleJudge_BackwardCompatibility_NullMultiJudge_Works()
    {
        // Arrange — verify both single-judge overloads produce a valid CompositeEval.
        var judge = new FixedScoreJudge(95);
        var registry = BuildSingleJudgeRegistry(judge);

        // Act
        var singleJudgePreset1 = EuAiActBenchmark.AuditGrade(registry);
        var singleJudgePreset2 = EuAiActBenchmark.AuditGrade(registry, multiJudge: null);

        // Assert
        Assert.Equal("eu_ai_act.compliance.auditgrade", singleJudgePreset1.Key);
        Assert.Equal("eu_ai_act.compliance.auditgrade", singleJudgePreset2.Key);
        Assert.Equal("CapByWorst", singleJudgePreset1.Aggregation.Name);
        Assert.Equal("CapByWorst", singleJudgePreset2.Aggregation.Name);
    }

    [Fact]
    public async Task AuditGrade_MultiJudge_CriticalArticle_HasMultiJudgeSubResults()
    {
        // Arrange — 3 judges all scoring 95; Critical articles (Art 5, Art 9) produce
        // MultiJudgeWrapper sub-results that aggregate via WeightedMedian.
        var judge = new FixedScoreJudge(95);
        var judges = new (AgentEval.Core.IEvaluator Judge, double Weight)[]
        {
            (judge, 1.0),
            (judge, 1.0),
            (judge, 1.0),
        };

        var registry = BuildMultiJudgeRegistry(judge, judges);
        var auditGrade = EuAiActBenchmark.AuditGrade(registry, new MultiJudgeOptions(judges));

        // Act
        var result = await auditGrade.EvaluateAsync(BenchmarkInput);

        // Assert — well-formed 6-pillar composite (parity with GDPR Pillar 1-6 shape).
        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(6, result.Details.SubResults!.Count);

        // Verify at least one leaf uses WeightedMedian (MultiJudgeWrapper on Critical articles).
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
