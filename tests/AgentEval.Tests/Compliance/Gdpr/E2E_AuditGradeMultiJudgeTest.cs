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
    public async Task AuditGrade_MultiJudge_OneCriticalDissent_WithholdsThePass_ItDoesNotPassOrFail()
    {
        // B6c-3 (mid-branch review). Two judges pass a scenario (95), one finds a critical failure (20). This used to
        // FAIL through a side effect: the panel took the severity path (score = the median, label = the worst judge),
        // and a passing article carried the dissent's "critical" up to the cap. The panel now judges its median against
        // the scenario's own bar and, with a severe dissent below that bar, withholds its pass — so every level above
        // reads "not attested" and the preset is WARN: needs review, neither a pass nor a fail one judge invented.
        var agreeingJudge = new FixedScoreJudge(95);
        var dissentingJudge = new FixedScoreJudge(20);
        var judges = new (AgentEval.Core.IEvaluator Judge, double Weight)[]
        {
            (agreeingJudge, 1.0),
            (agreeingJudge, 1.0),
            (dissentingJudge, 1.0),
        };
        var registry = BuildMultiJudgeRegistry(agreeingJudge, judges);
        var auditGrade = GdprBenchmark.AuditGrade(registry, new MultiJudgeOptions(judges));

        var result = await auditGrade.EvaluateAsync(BenchmarkInput);

        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Contains(Walk(result), r => r.Details.Summary?.Contains("judges found a critical failure", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task AuditGrade_MultiJudge_ACriticalMajority_FailsAndIsCapped()
    {
        // Two of three judges find the critical failure: the median fails, and CapByWorst caps the preset at 0.40.
        var agreeingJudge = new FixedScoreJudge(95);
        var failingJudge = new FixedScoreJudge(20);
        var judges = new (AgentEval.Core.IEvaluator Judge, double Weight)[]
        {
            (agreeingJudge, 1.0),
            (failingJudge, 1.0),
            (failingJudge, 1.0),
        };
        var registry = BuildMultiJudgeRegistry(failingJudge, judges);
        var auditGrade = GdprBenchmark.AuditGrade(registry, new MultiJudgeOptions(judges));

        var result = await auditGrade.EvaluateAsync(BenchmarkInput);

        Assert.Equal("fail", result.Score.Label);
        Assert.Equal("critical", result.Score.Severity);
        Assert.True(result.Score.Value <= 0.40, $"Expected CapByWorst to cap score at 0.40, got {result.Score.Value:F3}");
    }

    private static IEnumerable<EvalResult> Walk(EvalResult r)
    {
        yield return r;
        foreach (var s in r.Details.SubResults ?? [])
            foreach (var d in Walk(s))
                yield return d;
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

    [Fact]
    public async Task AuditGrade_MultiJudge_PerJudgeProvenance_RecordsRealModelNotPositionalLabel()
    {
        // BUG-34: each per-judge atomic must record the REAL judge model ("stub") in provenance,
        // not a positional "judge-1/2/3" label — otherwise EstimatedCost resolves the default rate
        // and the audit trail misstates the deployment. The positional index lives in the key.
        var judge = new FixedScoreJudge(95);
        var judges = new (AgentEval.Core.IEvaluator Judge, double Weight)[]
        {
            (judge, 1.0),
            (judge, 1.0),
            (judge, 1.0),
        };

        var registry = BuildMultiJudgeRegistry(judge, judges); // judgeModel: "stub"
        var auditGrade = GdprBenchmark.AuditGrade(registry, new MultiJudgeOptions(judges));

        var result = await auditGrade.EvaluateAsync(BenchmarkInput);

        var multiJudgeLeaves = FindMultiJudgeLeaves(result, minSubResults: 3);
        Assert.NotEmpty(multiJudgeLeaves);

        var perJudgeResults = multiJudgeLeaves
            .SelectMany(leaf => leaf.Details.SubResults!)
            .ToList();
        Assert.NotEmpty(perJudgeResults);
        Assert.All(perJudgeResults, r =>
        {
            Assert.Equal("stub", r.Provenance.JudgeModel);
            Assert.DoesNotContain("judge-", r.Provenance.JudgeModel ?? "");
        });
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
