// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Compliance.EuAiAct;
using AgentEval.Compliance.EuAiAct.Articles;
using AgentEval.Compliance.EuAiAct.Articles.Building;
using AgentEval.Compliance.EuAiAct.Articles.Loading;
using AgentEval.Compliance.EuAiAct.Reporting;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Compliance.EuAiAct.EndToEnd;

/// <summary>
/// End-to-end tests for the EU AI Act Standard preset:
/// verifies the full six-pillar, 13-article tree shape, evidence persistence,
/// schema validation, and critical findings list.
/// </summary>
public class EuAiActStandardE2ETest
{
    // ── Stub evaluator ────────────────────────────────────────────────────────

    private sealed class StubEvaluator(int score) : AgentEval.Core.IEvaluator
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
                    .Select(c => new AgentEval.Core.CriterionResult
                    {
                        Criterion = c,
                        Met = score >= 50,
                        Explanation = "stub"
                    })
                    .ToList()
            });
    }

    // ── Registry factory ──────────────────────────────────────────────────────

    private static EuAiActArticlesRegistry BuildRegistry(int stubScore)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(
            new StubEvaluator(stubScore), judgeModel: "stub");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new EuAiActArticlesRegistry(loader, articleBuilder);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Standard_Preset_Has6PillarsAnd15Articles()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "EuAiStandardAgent");
        var registry = BuildRegistry(stubScore: 100);

        var standard = EuAiActBenchmark.Standard(registry);
        var runner = new EuAiActBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "compliant response");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, standard, input);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "standard"));

        // 6 pillars in PerPillar
        Assert.Equal(6, evidence.Summary.PerPillar.Count);

        // 15 articles in PerArticle (13 original + Art 9 risk-management + Art 10 data-governance, plan-13 T1.2)
        Assert.Equal(15, evidence.Summary.PerArticle.Count);
    }

    [Fact]
    public async Task Standard_Preset_AllPassing_EvidenceSchemaValidationPasses()
    {
        // The reporter validates against the embedded schema before persisting;
        // if schema validation failed it would throw InvalidOperationException.
        // A successful SaveReportAsync call proves schema validation passed.
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "EuAiStandardAgent2");
        var registry = BuildRegistry(stubScore: 100);

        var standard = EuAiActBenchmark.Standard(registry);
        var runner = new EuAiActBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "compliant response");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, standard, input);

        var reporter = new EuAiActComplianceReporter(registry);

        // Should not throw — schema validation is embedded in SaveReportAsync
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "standard"));

        Assert.NotNull(evidence);
    }

    [Fact]
    public async Task Standard_Preset_AllPassing_CriticalFindingsIsEmpty()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "EuAiStandardAgent3");
        var registry = BuildRegistry(stubScore: 100);

        var standard = EuAiActBenchmark.Standard(registry);
        var runner = new EuAiActBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "compliant response");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, standard, input);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "standard"));

        // No critical failures when all stubs pass
        Assert.Empty(evidence.CriticalFindings);
    }

    [Fact]
    public async Task Standard_Preset_AllFailing_CriticalFindingsListIsSensible()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "EuAiStandardFailAgent");
        var registry = BuildRegistry(stubScore: 0);

        var standard = EuAiActBenchmark.Standard(registry);
        var runner = new EuAiActBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "bad response");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, standard, input);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "standard"));

        // Critical findings should be non-empty (4 critical-severity articles in the EU AI Act)
        Assert.NotEmpty(evidence.CriticalFindings);

        // Every critical finding should carry the "critical" severity label
        Assert.All(evidence.CriticalFindings, f =>
            Assert.Equal("critical", f.Score.Severity));
    }

    [Fact]
    public async Task Standard_Preset_PillarKeys_MatchExpected()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "EuAiPillarAgent");
        var registry = BuildRegistry(stubScore: 100);

        var standard = EuAiActBenchmark.Standard(registry);
        var runner = new EuAiActBenchmarkRunner();
        var input = new EvalInput(Query: "q", Response: "r");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, standard, input);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "standard"));

        var pillarKeys = evidence.Summary.PerPillar.Keys.ToHashSet();
        Assert.Contains("Pillar1-ProhibitedPractices", pillarKeys);
        Assert.Contains("Pillar2-TransparencyToPersons", pillarKeys);
        Assert.Contains("Pillar3-HumanOversight", pillarKeys);
        Assert.Contains("Pillar4-RiskTierBehavior", pillarKeys);
        Assert.Contains("Pillar5-RobustnessAccuracy", pillarKeys);
        Assert.Contains("Pillar6-GpaiSelfAwareness", pillarKeys);
    }

    [Fact]
    public async Task Standard_Preset_AllArticleIds_PresentInPerArticle()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "EuAiArticleAgent");
        var registry = BuildRegistry(stubScore: 100);

        var standard = EuAiActBenchmark.Standard(registry);
        var runner = new EuAiActBenchmarkRunner();
        var input = new EvalInput(Query: "q", Response: "r");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, standard, input);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "standard"));

        var articleKeys = evidence.Summary.PerArticle.Keys.ToHashSet();

        // All 15 control IDs must appear (13 original + Art 9 + Art 10, plan-13 T1.2)
        var expectedIds = new[]
        {
            "eu_ai.art5.subliminal+exploitation",
            "eu_ai.art5.social_scoring+predictive",
            "eu_ai.art5.biometric_scraping+emotion",
            "eu_ai.art5.biometric_cat+realtime_id",
            "eu_ai.art9.risk_management",
            "eu_ai.art10.data_governance",
            "eu_ai.art13.deployer_transparency",
            "eu_ai.art14.human_oversight",
            "eu_ai.art15.robustness",
            "eu_ai.art50.ai_disclosure",
            "eu_ai.art50.deepfake_label",
            "eu_ai.art50.emotion_disclosure",
            "eu_ai.art50.text_disclosure",
            "eu_ai.annex3.risk_tier_recognition",
            "eu_ai.gpai.self_provenance",
        };

        foreach (var id in expectedIds)
            Assert.Contains(id, articleKeys);
    }
}
