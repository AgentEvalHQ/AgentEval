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

namespace AgentEval.Tests.Compliance.EuAiAct.Reporting;

/// <summary>
/// Unit tests for <see cref="EuAiActComplianceReporter"/>.
/// Exercises evidence persistence, disclaimer content, and audit-chain integration.
/// </summary>
public class EuAiActComplianceReporterTests
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheSummary_CountsOnlyMeasuredFailuresAsFailedScenarios()
    {
        // Review round 4 M6 (B10p), the EU copy of the GDPR summary.
        static EvalResult Leaf(string key, string label) => new(
            new(key, key, "test", "1.0"), new(label == "pass" ? 1.0 : 0.0, null, label, label == "pass", 0.75, "none", null),
            new(null, null, null, null, null), new("atomic", null, null, null, null, 0, false), DateTimeOffset.UtcNow);
        static EvalResult Node(string key, string label, params EvalResult[] children) => new(
            new(key, key, "test", "1.0"), new(0.5, null, label, false, 0.85, "none", null),
            new(null, null, null, children, null), new("composite", null, null, null, null, 0, false), DateTimeOffset.UtcNow);
        var article = Node("euaiact.art5", "error", Leaf("s1", "error"), Leaf("s2", "warn"), Leaf("s3", "fail"), Leaf("s4", "pass"));
        var root = Node("root", "error", Node("Pillar1", "error", article));

        var summary = new AgentEval.Compliance.EuAiAct.Reporting.SummaryBuilder(BuildRegistry(100)).Build(root);

        Assert.Equal(1, summary.PerArticle["euaiact.art5"].ScenariosFailed);
    }

    private static EuAiActArticlesRegistry BuildRegistry(int stubScore)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(
            new StubEvaluator(stubScore), judgeModel: "stub");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new EuAiActArticlesRegistry(loader, articleBuilder);
    }

    private static async Task<(InMemoryOutputStore Store, SubjectIdentity Subject,
        string RunId, EvalResult Result, EuAiActArticlesRegistry Registry)>
        RunSmokeAsync(int stubScore = 100)
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "ReporterTestAgent");
        var registry = BuildRegistry(stubScore);

        var smoke = EuAiActBenchmark.Smoke(registry);
        var runner = new EuAiActBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "response");

        var (runId, result) = await runner.RunAsync(store, subject, smoke, input);
        return (store, subject, runId, result, registry);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveReportAsync_PersistsBaseEvidenceThroughStore()
    {
        var (store, subject, runId, result, registry) = await RunSmokeAsync(stubScore: 100);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "smoke"));

        // Listing compliance evidence for "EU-AI-Act" and subject should return exactly one entry
        var pointers = new List<ComplianceEvidencePointer>();
        await foreach (var p in store.ListComplianceEvidenceAsync("EU-AI-Act", subject))
            pointers.Add(p);

        Assert.Single(pointers);

        // The Disclaimer on the returned wrapper matches the reporter constant
        Assert.Equal(EuAiActComplianceReporter.Disclaimer, evidence.Disclaimer);
    }

    [Fact]
    public async Task SaveReportAsync_DisclaimerNotEmpty()
    {
        var (store, subject, runId, result, registry) = await RunSmokeAsync(stubScore: 100);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "smoke"));

        Assert.False(string.IsNullOrWhiteSpace(evidence.Disclaimer),
            "Disclaimer must not be null or whitespace.");
    }

    [Fact]
    public async Task SaveReportAsync_DisclaimerContainsKeyPhrases()
    {
        var (store, subject, runId, result, registry) = await RunSmokeAsync(stubScore: 100);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "smoke"));

        // Verify the disclaimer is the canonical EU AI Act disclaimer text
        Assert.Contains("Regulation (EU) 2024/1689", evidence.Disclaimer);
        Assert.Contains("A passing run does not constitute legal compliance attestation", evidence.Disclaimer);
    }

    [Fact]
    public async Task SaveReportAsync_RegulationIsEuAiAct()
    {
        var (store, subject, runId, result, registry) = await RunSmokeAsync(stubScore: 100);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new EuAiActReportOptions(Preset: "smoke"));

        Assert.Equal("EU-AI-Act", evidence.Base.Regulation);
        Assert.Equal(EuAiActComplianceReporter.Regulation, evidence.Base.Regulation);
    }

    [Fact]
    public async Task SaveReportAsync_DefaultOptions_PresetIsStandard()
    {
        // When options are omitted, the default preset should be "standard"
        var (store, subject, runId, result, registry) = await RunSmokeAsync(stubScore: 100);

        var reporter = new EuAiActComplianceReporter(registry);
        var evidence = await reporter.SaveReportAsync(store, subject, runId, result);

        Assert.Equal("standard", evidence.Preset);
    }

    [Fact]
    public async Task SaveReportAsync_NullStore_Throws()
    {
        var (_, subject, runId, result, registry) = await RunSmokeAsync(stubScore: 100);
        var reporter = new EuAiActComplianceReporter(registry);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            reporter.SaveReportAsync(null!, subject, runId, result));
    }

    [Fact]
    public async Task SaveReportAsync_NullSubject_Throws()
    {
        var (store, _, runId, result, registry) = await RunSmokeAsync(stubScore: 100);
        var reporter = new EuAiActComplianceReporter(registry);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            reporter.SaveReportAsync(store, null!, runId, result));
    }
}
