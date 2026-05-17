// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

// Alias is required: this file's enclosing namespace `AgentEval.Tests.EuAiActBenchmark.Reporting`
// shadows the same-named factory type in `AgentEval.Benchmarks`, and the
// `using AgentEval.EuAiActBenchmark.*;` directives below also import the parent namespace —
// both effects make the bare identifier `EuAiActBenchmark` resolve to a namespace, not the
// factory type (CS0234). See lastreview/11-phase4-gate-review.md §4 (Concern A). Do not
// remove without first restructuring this file's namespace or fully-qualifying every call site.
using EuAiActBenchmarkFactory = AgentEval.Benchmarks.EuAiActBenchmark;
using AgentEval.Evals;
using AgentEval.EuAiActBenchmark;
using AgentEval.EuAiActBenchmark.Articles;
using AgentEval.EuAiActBenchmark.Articles.Building;
using AgentEval.EuAiActBenchmark.Articles.Loading;
using AgentEval.EuAiActBenchmark.Reporting;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.EuAiActBenchmark.Reporting;

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

        var smoke = EuAiActBenchmarkFactory.Smoke(registry);
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
