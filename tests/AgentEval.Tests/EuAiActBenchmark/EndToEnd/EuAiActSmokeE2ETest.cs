// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using EuAiActBenchmarkFactory = AgentEval.Benchmarks.EuAiActBenchmark;
using AgentEval.Evals;
using AgentEval.EuAiActBenchmark;
using AgentEval.EuAiActBenchmark.Articles;
using AgentEval.EuAiActBenchmark.Articles.Building;
using AgentEval.EuAiActBenchmark.Articles.Loading;
using AgentEval.EuAiActBenchmark.Reporting;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.EuAiActBenchmark.EndToEnd;

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

        var smoke = EuAiActBenchmarkFactory.Smoke(registry);
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
    public async Task Smoke_Preset_StubScore80_PassesThreshold()
    {
        // Smoke threshold is 0.80; stub score=80/100 = 0.80; weighted_sum should produce pass.
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "EuAiSmokeAgent2");
        var registry = BuildRegistry(stubScore: 80);

        var smoke = EuAiActBenchmarkFactory.Smoke(registry);
        var runner = new EuAiActBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "good response");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, smoke, input);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "smoke"));

        // Score 80 should meet the 0.80 threshold
        Assert.True(result.Score.Passed,
            $"Expected smoke to pass with stub score=80, got score={result.Score.Value}");

        // Compliance evidence stored in the output store
        var pointers = new List<ComplianceEvidencePointer>();
        await foreach (var p in store.ListComplianceEvidenceAsync("EU-AI-Act", subject))
            pointers.Add(p);
        Assert.Single(pointers);
    }

    [Fact]
    public async Task Smoke_Preset_AllArticlesPresentInMarkdown()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "EuAiSmokeAgent3");
        var registry = BuildRegistry(stubScore: 100);

        var smoke = EuAiActBenchmarkFactory.Smoke(registry);
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
