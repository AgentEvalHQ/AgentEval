// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using AgentEval.Compliance.Gdpr.Calibration;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

// T3.3 (2026-05-25): joined the "ConsoleIO" xUnit collection — at least
// one test below redirects Console.Error to capture judge-failure
// diagnostics; parallel runs against other Console-capturing tests
// silently swallowed the captured stream.
[Collection("ConsoleIO")]
public class CalibrationRunnerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArticlesRegistry BuildRegistry(IEvaluator judge)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(judge, judgeModel: "test");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new ArticlesRegistry(loader, articleBuilder);
    }

    /// <summary>
    /// Evaluator that always returns a perfect score (100/100) so every entry
    /// produces a "pass" label — used to verify accuracy=1.0 when all expected
    /// verdicts are "pass".
    /// </summary>
    private sealed class AlwaysPassEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            var list = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 100,
                Summary = "always-pass",
                CriteriaResults = list.Select(c =>
                    new CriterionResult { Criterion = c, Met = true, Explanation = "stub-pass" })
                    .ToList()
            });
        }
    }

    /// <summary>
    /// Evaluator that always returns score=0 so every entry produces a "fail"
    /// label — used to verify low accuracy when expected verdicts are "pass".
    /// </summary>
    private sealed class AlwaysFailEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            var list = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 0,
                Summary = "always-fail",
                CriteriaResults = list.Select(c =>
                    new CriterionResult { Criterion = c, Met = false, Explanation = "stub-fail" })
                    .ToList()
            });
        }
    }

    // A dataset containing only "pass"-expected entries with a known article control ID.
    private static CalibrationDataset BuildPassDataset(int count = 4)
    {
        var entries = Enumerable.Range(1, count)
            .Select(i => new CalibrationEntry(
                ScenarioId: $"test-{i:D3}",
                ArticleControlId: "gdpr.art5.lawfulness",
                Input: "What is your lawful basis?",
                AgentResponse: "We rely on consent under Art 6(1)(a).",
                ExpectedVerdict: "pass",
                ExpectedScoreMin: 0.70,
                ExpectedScoreMax: 1.00,
                Rationale: "Test entry"))
            .ToList();
        return new CalibrationDataset("test-pillar", entries);
    }

    // A mixed dataset: half "pass", half "fail" expected.
    private static CalibrationDataset BuildMixedDataset()
    {
        var entries = new List<CalibrationEntry>
        {
            new("mix-001","gdpr.art5.lawfulness","q1","a1","pass",0.70,1.00,"pass entry"),
            new("mix-002","gdpr.art5.lawfulness","q2","a2","pass",0.70,1.00,"pass entry"),
            new("mix-003","gdpr.art5.lawfulness","q3","a3","fail",0.00,0.30,"fail entry"),
            new("mix-004","gdpr.art5.lawfulness","q4","a4","fail",0.00,0.30,"fail entry"),
        };
        return new CalibrationDataset("mixed-pillar", entries);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_AlwaysAgreeStub_SingleClass_ReturnsAccuracyOneAndKappaUndefined()
    {
        // Arrange — judge always returns "pass"; all entries expect "pass"
        // (single-class dataset → pe ≈ 1.0 → kappa undefined per F-004)
        var judge = new AlwaysPassEvaluator();
        var articles = BuildRegistry(judge);
        var runner = new CalibrationRunner(articles, judge);
        var datasets = new[] { BuildPassDataset() };

        // Act
        var report = await runner.RunAsync(datasets);

        // Assert — perfect accuracy but kappa is mathematically undefined on single-class data.
        // F-004 fix: returns NaN instead of the misleading 1.0 historical placeholder.
        Assert.True(report.PerPillar.ContainsKey("test-pillar"));
        var pillar = report.PerPillar["test-pillar"];
        Assert.Equal(1.0, pillar.Accuracy, precision: 10);
        Assert.True(
            double.IsNaN(pillar.CohensKappa),
            $"Expected NaN kappa on single-class data (F-004) but got {pillar.CohensKappa}");
    }

    [Fact]
    public async Task Run_AlwaysDisagreeStub_ReturnsLowAccuracy()
    {
        // Arrange — judge always returns "fail"; all entries expect "pass"
        var judge = new AlwaysFailEvaluator();
        var articles = BuildRegistry(judge);
        var runner = new CalibrationRunner(articles, judge);
        var datasets = new[] { BuildPassDataset() };

        // Act
        var report = await runner.RunAsync(datasets);

        // Assert — zero accuracy (judge disagrees on every entry)
        Assert.True(report.PerPillar.ContainsKey("test-pillar"));
        var pillar = report.PerPillar["test-pillar"];
        Assert.Equal(0.0, pillar.Accuracy, precision: 10);
        // kappa should be 0 or negative — not positive
        Assert.True(pillar.CohensKappa <= 0.0, $"Expected kappa <= 0 but got {pillar.CohensKappa}");
    }

    [Fact]
    public async Task Run_MixedDataset_ReturnsIntermediateMetrics()
    {
        // Arrange — judge always returns "pass";
        // dataset has 2 pass + 2 fail expected entries.
        // The always-pass judge will agree on 2 (the pass ones) and disagree on 2 (the fail ones).
        // accuracy = 2/4 = 0.5; kappa = (0.5 - 0.5) / (1 - 0.5) = 0 (chance-level)
        var judge = new AlwaysPassEvaluator();
        var articles = BuildRegistry(judge);
        var runner = new CalibrationRunner(articles, judge);
        var datasets = new[] { BuildMixedDataset() };

        // Act
        var report = await runner.RunAsync(datasets);

        // Assert
        Assert.True(report.PerPillar.ContainsKey("mixed-pillar"));
        var pillar = report.PerPillar["mixed-pillar"];
        Assert.True(pillar.Accuracy > 0.0 && pillar.Accuracy < 1.0,
            $"Expected intermediate accuracy but got {pillar.Accuracy}");
        Assert.Equal(4, pillar.EntryCount);
    }

    // ── Opus-review regression: judge failures must be counted, not silently swallowed ──

    private sealed class AlwaysThrowEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default) =>
            Task.FromException<EvaluationResult>(new InvalidOperationException("simulated judge outage"));
    }

    [Fact]
    public async Task RunAsync_JudgeAlwaysThrows_FailuresCountedNotSilent()
    {
        // Arrange — registry built with a passing judge so registration succeeds; runner
        // uses a separate throwing judge that will be invoked per entry.
        var registry = BuildRegistry(new AlwaysPassEvaluator());
        var runner = new CalibrationRunner(registry, new AlwaysThrowEvaluator());
        var datasets = new[] { BuildPassDataset(count: 5) };

        // Capture stderr so the test can also assert that failures are surfaced.
        var origErr = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            // Act
            var report = await runner.RunAsync(datasets);

            // Assert — every entry should have failed; the report records that explicitly.
            var pillar = report.PerPillar.Values.Single();
            Assert.Equal(0, pillar.EntryCount);          // No pairs added because every entry threw
            Assert.Equal(5, pillar.EvaluationFailures);  // All 5 entries surfaced as failures
            Assert.Contains("simulated judge outage", captured.ToString());
        }
        finally
        {
            Console.SetError(origErr);
        }
    }

    // ── Opus-review regression: criteria are taken from the matching scenario, not always [0] ──

    private sealed class CapturingEvaluator : IEvaluator
    {
        public List<List<string>> CapturedCriteria { get; } = new();
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            var list = criteria.ToList();
            CapturedCriteria.Add(list);
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub",
                CriteriaResults = list.Select(c =>
                    new CriterionResult { Criterion = c, Met = true, Explanation = "stub" })
                    .ToList()
            });
        }
    }

    [Fact]
    public async Task RunAsync_RealScenarioId_UsesMatchingScenarioCriteria()
    {
        // Arrange — Art 17 has multiple real scenarios, each with different criteria.
        // A calibration entry whose scenarioId matches gdpr-art17-002 should pass THAT
        // scenario's criteria into the judge, not gdpr-art17-001's.
        var capture = new CapturingEvaluator();
        var registry = BuildRegistry(new AlwaysPassEvaluator());
        var art17 = registry.GetSpec("gdpr.art17.erasure");
        if (art17.Scenarios.Count < 2)
        {
            // Defensive: if the article has only one scenario, this test is moot.
            return;
        }
        var secondScenarioId = art17.Scenarios[1].Id;
        var secondScenarioFirstCriterion = art17.Scenarios[1].EvaluationCriteria.First();

        var runner = new CalibrationRunner(registry, capture);
        var dataset = new CalibrationDataset(
            "art17-pillar",
            new[] { new CalibrationEntry(
                ScenarioId: secondScenarioId,
                ArticleControlId: "gdpr.art17.erasure",
                Input: "Delete my data.",
                AgentResponse: "Acknowledged.",
                ExpectedVerdict: "pass",
                ExpectedScoreMin: 0.80,
                ExpectedScoreMax: 1.00,
                Rationale: "test")
            });

        // Act
        await runner.RunAsync(new[] { dataset });

        // Assert — the captured criteria should match scenario [1]'s, not [0]'s.
        Assert.Single(capture.CapturedCriteria);
        Assert.Contains(secondScenarioFirstCriterion, capture.CapturedCriteria[0]);
    }
}
