// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

// Alias is required: this file's enclosing namespace `AgentEval.Tests.GdprBenchmark` shadows
// the same-named factory type in `AgentEval.Benchmarks`, and the `using AgentEval.GdprBenchmark.*;`
// directives below also import the parent namespace — both effects make the bare identifier
// `GdprBenchmark` resolve to a namespace, not the factory type (CS0234).
// See lastreview/11-phase4-gate-review.md §4 (Concern A). Do not remove without first
// restructuring this file's namespace or fully-qualifying every call site.
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
/// End-to-end tests for the GDPR Standard preset: verifies the full five-pillar,
/// 21-article tree shape, evidence persistence, and Markdown rendering with a mocked judge.
/// </summary>
public class E2E_StandardTest
{
    // ── Stub evaluator ────────────────────────────────────────────────────────

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
    public async Task Standard_Preset_AllPassing_ProducesPassEvidenceWithFullPillarTree()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "ComplianceAgent");
        var registry = BuildRegistry(stubScore: 100);

        var standard = GdprBenchmarkFactory.Standard(registry);
        var runner = new GdprBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "compliant response");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, standard, input);

        Assert.True(result.Score.Passed);

        var reporter = new GDPRComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new GdprReportOptions(Preset: "standard"));

        // Preset and disclaimer
        Assert.Equal("standard", evidence.Preset);
        Assert.Equal(GDPRComplianceReporter.Disclaimer, evidence.Disclaimer);

        // Overall verdict
        Assert.Equal("PASS", evidence.Summary.OverallStatus);

        // Standard has 5 pillars
        Assert.Equal(5, evidence.Summary.PerPillar.Count);

        // Standard has all 21 articles
        Assert.Equal(21, evidence.Summary.PerArticle.Count);

        // All pillars should pass
        Assert.All(evidence.Summary.PerPillar.Values, p => Assert.Equal("PASS", p.Status));

        // All articles should pass
        Assert.All(evidence.Summary.PerArticle.Values, a => Assert.Equal("PASS", a.Status));

        // No critical findings
        Assert.Empty(evidence.CriticalFindings);

        // No recommendations (everything passed)
        Assert.Empty(evidence.Recommendations);

        // Markdown rendering
        var md = new MarkdownRenderer().Render(evidence);
        Assert.Contains("GDPR Compliance — Standard Preset", md);
        Assert.Contains(GDPRComplianceReporter.Disclaimer, md);
        Assert.Contains("Per-pillar", md);
        Assert.Contains("Per-article", md);
        Assert.DoesNotContain("## Critical findings", md);
        Assert.DoesNotContain("## Recommendations", md);

        // Base evidence persisted
        var pointers = new List<ComplianceEvidencePointer>();
        await foreach (var p in store.ListComplianceEvidenceAsync("GDPR"))
            pointers.Add(p);
        Assert.Single(pointers);

        // Base evidence has 21 controls (one per article)
        var baseEvidence = await store.GetComplianceEvidenceAsync(
            "GDPR", subject, evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss"));
        Assert.NotNull(baseEvidence);
        Assert.Equal(21, baseEvidence!.Controls.Count);
    }

    [Fact]
    public async Task Standard_Preset_FailingStub_ProducesFailEvidence_WithRecommendationsForAllArticles()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "ComplianceAgent");
        var registry = BuildRegistry(stubScore: 20);

        var standard = GdprBenchmarkFactory.Standard(registry);
        var runner = new GdprBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "bad response");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, standard, input);

        Assert.False(result.Score.Passed);

        var reporter = new GDPRComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new GdprReportOptions(Preset: "standard"));

        Assert.Equal("FAIL", evidence.Summary.OverallStatus);

        // All 5 pillars fail
        Assert.Equal(5, evidence.Summary.PerPillar.Count);
        Assert.All(evidence.Summary.PerPillar.Values, p => Assert.Equal("FAIL", p.Status));

        // All 21 articles fail
        Assert.Equal(21, evidence.Summary.PerArticle.Count);
        Assert.All(evidence.Summary.PerArticle.Values, a => Assert.Equal("FAIL", a.Status));

        // Recommendations for all 21 articles
        Assert.Equal(21, evidence.Recommendations.Count);

        // Markdown has recommendations section
        var md = new MarkdownRenderer().Render(evidence);
        Assert.Contains("## Recommendations", md);
        Assert.Contains("Per-pillar", md);
        Assert.Contains("Per-article", md);
    }

    [Fact]
    public async Task Standard_Preset_PillarKeys_MatchExpected()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "PillarCheckAgent");
        var registry = BuildRegistry(stubScore: 100);

        var standard = GdprBenchmarkFactory.Standard(registry);
        var runner = new GdprBenchmarkRunner();
        var input = new EvalInput(Query: "q", Response: "r");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, standard, input);

        var reporter = new GDPRComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new GdprReportOptions(Preset: "standard"));

        var pillarKeys = evidence.Summary.PerPillar.Keys.ToHashSet();
        Assert.Contains("Pillar1-Foundations", pillarKeys);
        Assert.Contains("Pillar2-LawfulBasis", pillarKeys);
        Assert.Contains("Pillar3-SubjectRights", pillarKeys);
        Assert.Contains("Pillar4-Transparency", pillarKeys);
        Assert.Contains("Pillar5-PrivacyDesign", pillarKeys);
    }

    [Fact]
    public async Task Standard_Preset_GdprAttestation_DefaultJudgeMode_IsSet()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "AttestationAgent");
        var registry = BuildRegistry(stubScore: 100);

        var standard = GdprBenchmarkFactory.Standard(registry);
        var runner = new GdprBenchmarkRunner();
        var input = new EvalInput(Query: "q", Response: "r");

        (string runId, EvalResult result) = await runner.RunAsync(store, subject, standard, input);

        var reporter = new GDPRComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result);

        Assert.Equal("mode-a", evidence.GdprAttestation.JudgeMode);
        Assert.Contains("gdpr-judge-system", evidence.GdprAttestation.PromptVersions.Keys);
    }
}
