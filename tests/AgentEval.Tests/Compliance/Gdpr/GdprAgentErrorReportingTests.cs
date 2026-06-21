// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Benchmarks;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

/// <summary>
/// End-to-end regression coverage for the full agent-error → compliance-report pipeline:
/// when every probe to the agent-under-test fails (e.g. AzulClaw had no model available),
/// <see cref="AgentScenarioEval"/> produces "error"-labelled leaves, and the report must
/// still generate successfully instead of crashing on gdpr-evidence.json schema validation.
/// </summary>
public class GdprAgentErrorReportingTests
{
    private sealed class AlwaysThrowingAgent : IEvaluableAgent
    {
        public string Name => "always-throwing-agent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Backend error 502: AzulClaw did not produce a real answer.");
    }

    private sealed class StubJudge : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria, CancellationToken ct = default) =>
            Task.FromResult(new AgentEval.Core.EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub",
                CriteriaResults = criteria
                    .Select(c => new AgentEval.Core.CriterionResult { Criterion = c, Met = true, Explanation = "stub" })
                    .ToList(),
            });
    }

    [Fact]
    public async Task SaveReportAsync_AllScenariosAgentError_DoesNotThrowSchemaValidation()
    {
        // Regression: previously every "model unavailable" probe produced an "error"-labelled
        // leaf, whose severity ("none") rolled up unchanged to its owning control via
        // WeightedSumAggregation. RecommendationExtractor read that control's severity, built a
        // Recommendation with severity="none", and gdpr-evidence.json schema validation
        // (severity enum: low/medium/high/critical only) crashed the WHOLE report — meaning
        // report.md/gdpr-evidence.json/report.pdf never got written for the entire benchmark run.
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(
            new StubJudge(), judgeModel: "stub", agent: new AlwaysThrowingAgent());
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        var registry = new ArticlesRegistry(loader, articleBuilder);

        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "ErrorPropagationTestAgent");
        var benchmark = GdprBenchmark.Smoke(registry);
        var runner = new GdprBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "unused — agent drives per scenario");

        var (runId, result) = await runner.RunAsync(store, subject, benchmark, input);

        // Sanity check the premise: every scenario really did error out (none "really" failed
        // on content — they never got a real answer to grade).
        Assert.All(EnumerateLeaves(result), leaf => Assert.Equal("error", leaf.Score.Label));

        var reporter = new GDPRComplianceReporter(registry);

        // Must not throw — this is the actual regression.
        var evidence = await reporter.SaveReportAsync(
            store, subject, runId, result, new GdprReportOptions(Preset: "smoke"));

        Assert.DoesNotContain(evidence.Recommendations, r => r.Severity == "none");
    }

    private static IEnumerable<EvalResult> EnumerateLeaves(EvalResult node)
    {
        var subs = node.Details.SubResults;
        if (subs is null || subs.Count == 0)
        {
            yield return node;
            yield break;
        }
        foreach (var child in subs)
            foreach (var leaf in EnumerateLeaves(child))
                yield return leaf;
    }
}
