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
/// End-to-end smoke tests for the EU AI Act Smoke preset:
/// composite evaluation, evidence persistence, and Markdown rendering —
/// all driven by a mocked (stub) judge returning score=80.
/// </summary>
public class EuAiActSmokeE2ETest
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
    public async Task Smoke_Preset_RunCompletes_EvidenceCreated_SummaryHas5Articles()
    {
        // Stub score of 80 is exactly at the smoke threshold (0.80), but weighted_sum of
        // 80/100 = 0.80 which equals threshold. Use 100 to ensure a clear PASS.
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "EuAiSmokeAgent");
        var registry = BuildRegistry(stubScore: 100);

        var smoke = EuAiActBenchmark.Smoke(registry);
        var runner = new EuAiActBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "good response");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, smoke, input);

        // Run completed
        Assert.NotEmpty(runId);
        Assert.NotNull(result);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "smoke"));

        // Evidence created
        Assert.NotNull(evidence);

        // Summary has 5 articles (the smoke preset contains 5 controls)
        Assert.Equal(5, evidence.Summary.PerArticle.Count);

        // Smoke flat preset — no pillar layer
        Assert.Empty(evidence.Summary.PerPillar);

        // OverallStatus is reasonable (PASS or WARN with score=100)
        Assert.NotNull(evidence.Summary.OverallStatus);
        Assert.Contains(evidence.Summary.OverallStatus, new[] { "PASS", "WARN", "FAIL" });

        // Markdown contains the disclaimer
        var md = new MarkdownRenderer().Render(evidence);
        Assert.Contains(EuAiActComplianceReporter.Disclaimer, md);
        Assert.Contains("EU AI Act Compliance", md);
        Assert.Contains("Per-article", md);
    }

    [Fact]
    public async Task Smoke_Preset_StubScore80_FailsOnTheCriticalArticlesItMisses()
    {
        // Every scenario at 0.80 meets the preset's 0.80 average, but the Art 5 prohibited-practice articles are
        // CRITICAL with a 0.85 article threshold, so they fail. This used to read PASS — a critical failure averaged out
        // (#203 review, B4). Smoke now caps a threshold pass by severity, as the docs' verdict table promises.
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "EuAiSmokeAgent2");
        var registry = BuildRegistry(stubScore: 80);

        var smoke = EuAiActBenchmark.Smoke(registry);
        var runner = new EuAiActBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "good response");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, smoke, input);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "smoke"));

        Assert.True(result.Score.Value >= 0.80, $"the average itself meets the bar: {result.Score.Value}");
        Assert.Equal("fail", result.Score.Label);
        Assert.False(result.Score.Passed);

        // Compliance evidence stored in the output store
        var pointers = new List<ComplianceEvidencePointer>();
        await foreach (var p in store.ListComplianceEvidenceAsync("EU-AI-Act", subject))
            pointers.Add(p);
        Assert.Single(pointers);
    }

    [Fact]
    public async Task Smoke_Preset_StubScore90_EveryArticleClearsItsThreshold_Passes()
    {
        var registry = BuildRegistry(stubScore: 90);
        var smoke = EuAiActBenchmark.Smoke(registry);

        var result = await smoke.EvaluateAsync(new EvalInput(Query: "test", Response: "good response"));

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
    }

    [Fact]
    public async Task Smoke_Preset_AllArticlesPresentInMarkdown()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "EuAiSmokeAgent3");
        var registry = BuildRegistry(stubScore: 100);

        var smoke = EuAiActBenchmark.Smoke(registry);
        var runner = new EuAiActBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "good response");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, smoke, input);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "smoke"));

        var md = new MarkdownRenderer().Render(evidence);

        // All 5 smoke controls should appear in the per-article table
        Assert.Contains("eu_ai.art5.social_scoring+predictive", md);
        Assert.Contains("eu_ai.art5.biometric_scraping+emotion", md);
        Assert.Contains("eu_ai.art50.ai_disclosure", md);
        Assert.Contains("eu_ai.art14.human_oversight", md);
        Assert.Contains("eu_ai.annex3.risk_tier_recognition", md);
    }
}
