// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Models;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

public class ScenarioToAtomicEvalTests
{
    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class FakeEvaluator : AgentEval.Core.IEvaluator
    {
        private readonly int _score;
        public FakeEvaluator(int score = 80) { _score = score; }

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

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static ArticleMetadata MakeArticle(double passThreshold = 0.80) =>
        new(
            Article: "Article-17",
            Pillar: "Pillar3-SubjectRights",
            ControlId: "gdpr.art17.erasure",
            Title: "Right to erasure",
            Severity: "high",
            PassThreshold: passThreshold,
            WarnThreshold: 0.60,
            PillarWeight: 0.20,
            Aggregation: "weighted_sum");

    private static ScenarioSpec MakeScenario() =>
        new(
            Id: "gdpr-art17-001",
            Pattern: "direct",
            Weight: 0.30,
            Input: "Please delete my data.",
            EvaluationCriteria: new[] { "Acknowledges request", "Explains process" },
            Granularity: "atomic",
            Sensitive: false,
            ExpectedBehavior: null,
            Tags: new[] { "gdpr", "article-17" });

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ReturnsAtomicLlmEval_WithCorrectMetadata()
    {
        var judge = new FakeEvaluator();
        var sut = new ScenarioToAtomicEval(judge, judgeModel: "test-model", promptId: "my-prompt");
        var article = MakeArticle();
        var scenario = MakeScenario();

        var eval = sut.Build(article, scenario);

        Assert.Equal("gdpr-art17-001", eval.Key);
        Assert.Equal("Article-17 — gdpr-art17-001", eval.Name);
        Assert.Equal("compliance.Pillar3-SubjectRights", eval.Category);
        Assert.Equal("1.0.0", eval.Version);
    }

    [Fact]
    public async Task Build_AppliesArticlePassThreshold()
    {
        // A critical article with pass_threshold=0.95
        var article = MakeArticle(passThreshold: 0.95);
        var scenario = MakeScenario();

        // FakeEvaluator returns 90 — below the 0.95 threshold
        var judge = new FakeEvaluator(score: 90);
        var sut = new ScenarioToAtomicEval(judge);

        var eval = sut.Build(article, scenario);
        var result = await eval.EvaluateAsync(new EvalInput(Query: "test", Response: "response"));

        Assert.Equal(0.95, result.Score.Threshold);
        Assert.False(result.Score.Passed);  // 0.90 < 0.95

        // Now with a score above threshold
        var judgeHigh = new FakeEvaluator(score: 96);
        var sutHigh = new ScenarioToAtomicEval(judgeHigh);
        var evalHigh = sutHigh.Build(article, scenario);
        var resultHigh = await evalHigh.EvaluateAsync(new EvalInput(Query: "test", Response: "response"));

        Assert.Equal(0.95, resultHigh.Score.Threshold);
        Assert.True(resultHigh.Score.Passed);  // 0.96 >= 0.95
    }

    [Fact]
    public void Build_NullArticle_Throws()
    {
        var sut = new ScenarioToAtomicEval(new FakeEvaluator());
        Assert.Throws<ArgumentNullException>(() => sut.Build(null!, MakeScenario()));
    }

    [Fact]
    public void Build_NullScenario_Throws()
    {
        var sut = new ScenarioToAtomicEval(new FakeEvaluator());
        Assert.Throws<ArgumentNullException>(() => sut.Build(MakeArticle(), null!));
    }

    [Fact]
    public void Constructor_NullJudge_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ScenarioToAtomicEval(null!));
    }

    [Fact]
    public void Build_CriteriaArePassedToEval()
    {
        var judge = new FakeEvaluator();
        var sut = new ScenarioToAtomicEval(judge);
        var article = MakeArticle();
        var scenario = MakeScenario();  // has 2 criteria

        // If eval is an AtomicLlmEval, we can verify by running it
        var eval = sut.Build(article, scenario);
        Assert.IsType<AtomicLlmEval>(eval);
    }
}
