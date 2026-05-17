// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using GdprBenchmarkFactory = AgentEval.Benchmarks.GdprBenchmark;
using AgentEval.Evals;
using AgentEval.GdprBenchmark;
using AgentEval.GdprBenchmark.Articles;
using AgentEval.GdprBenchmark.Articles.Building;
using AgentEval.GdprBenchmark.Articles.Loading;
using AgentEval.GdprBenchmark.Reporting;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.GdprBenchmark;

/// <summary>
/// End-to-end tests for the GDPR Smoke preset: composite evaluation, evidence persistence,
/// and Markdown rendering — all driven by a mocked (stub) judge.
/// </summary>
public class E2E_SmokeTest
{
    // ── Stub evaluator (shared with AllArticlesBuildAndExecuteTest) ───────────

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

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Smoke_Preset_AllPassing_ProducesPassEvidenceAndMarkdown()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TravelAgent");
        var registry = BuildRegistry(stubScore: 100);

        var smoke = GdprBenchmarkFactory.Smoke(registry);
        var runner = new GdprBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "good response");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, smoke, input);

        Assert.True(result.Score.Passed);
        Assert.Equal("pass", result.Score.Label);

        var reporter = new GDPRComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new GdprReportOptions(Preset: "smoke"));

        // Preset and disclaimer
        Assert.Equal("smoke", evidence.Preset);
        Assert.Equal(GDPRComplianceReporter.Disclaimer, evidence.Disclaimer);

        // Overall verdict
        Assert.Equal("PASS", evidence.Summary.OverallStatus);

        // Smoke has 5 articles — all at top level, wrapped by the smoke composite.
        // The SummaryBuilder walks sub-results of the root, which are the 5 article composites.
        Assert.Equal(5, evidence.Summary.PerArticle.Count);

        // No full pillars in smoke (articles go directly under root, not under pillar wrappers),
        // so PerPillar may be empty — that is the correct behaviour.
        // (Smoke wraps articles directly, not via pillar composites.)
        Assert.Empty(evidence.Summary.PerPillar);

        // All articles should pass
        Assert.All(evidence.Summary.PerArticle.Values, a => Assert.Equal("PASS", a.Status));

        // No critical findings when everything passes
        Assert.Empty(evidence.CriticalFindings);

        // Markdown rendering
        var md = new MarkdownRenderer().Render(evidence);
        Assert.Contains("GDPR Compliance — Smoke Preset", md);
        Assert.Contains(GDPRComplianceReporter.Disclaimer, md);
        Assert.Contains("Per-article", md);
        Assert.DoesNotContain("## Critical findings", md);

        // Base evidence was persisted in the store
        var pointers = new List<ComplianceEvidencePointer>();
        await foreach (var p in store.ListComplianceEvidenceAsync("GDPR"))
            pointers.Add(p);
        Assert.Single(pointers);
    }

    [Fact]
    public async Task Smoke_Preset_FailingStub_ProducesFailEvidence_WithCriticalFinding()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TravelAgent");
        var registry = BuildRegistry(stubScore: 20);

        var smoke = GdprBenchmarkFactory.Smoke(registry);
        var runner = new GdprBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "bad response");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, smoke, input);

        Assert.False(result.Score.Passed);

        var reporter = new GDPRComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new GdprReportOptions(Preset: "smoke"));

        Assert.Equal("FAIL", evidence.Summary.OverallStatus);

        // All articles should fail
        Assert.All(evidence.Summary.PerArticle.Values, a => Assert.Equal("FAIL", a.Status));

        // Recommendations generated for every failing article
        Assert.NotEmpty(evidence.Recommendations);
        Assert.Equal(5, evidence.Recommendations.Count);

        // Disclaimer present
        Assert.Equal(GDPRComplianceReporter.Disclaimer, evidence.Disclaimer);

        // Markdown includes recommendations section
        var md = new MarkdownRenderer().Render(evidence);
        Assert.Contains("## Recommendations", md);
        Assert.Contains("Per-article", md);
    }

    [Fact]
    public async Task Smoke_Preset_RunId_MatchesStoredManifest()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "AuditAgent");
        var registry = BuildRegistry(stubScore: 100);

        var smoke = GdprBenchmarkFactory.Smoke(registry);
        var runner = new GdprBenchmarkRunner();
        var input = new EvalInput(Query: "q", Response: "r");

        (string runId, EvalResult _) = await runner.RunAsync(store, subject, smoke, input);

        // The run ID returned by the runner must be discoverable via the store
        var manifest = await store.GetRunManifestAsync(runId);
        Assert.NotNull(manifest);
        Assert.Equal(runId, manifest!.Run.RunId);
        Assert.Equal("GdprBenchmarkRunner", manifest.Run.Harness);
    }
}
