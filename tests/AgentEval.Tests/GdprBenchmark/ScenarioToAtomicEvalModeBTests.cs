// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles.Building;
using AgentEval.GdprBenchmark.Articles.Models;
using Xunit;

namespace AgentEval.Tests.GdprBenchmark;

/// <summary>
/// Tests for Phase 7 (G7.3) Mode-B per-criterion escalation in
/// <see cref="ScenarioToAtomicEval"/>, including the multi-judge path.
/// </summary>
public class ScenarioToAtomicEvalModeBTests
{
    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class FakeEvaluator : AgentEval.Core.IEvaluator
    {
        private readonly int _score;
        public FakeEvaluator(int score = 80) { _score = score; }

        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            var crits = criteria.ToList();
            return Task.FromResult(new AgentEval.Core.EvaluationResult
            {
                OverallScore = _score,
                Summary = "stub",
                CriteriaResults = crits
                    .Select(c => new AgentEval.Core.CriterionResult
                    {
                        Criterion = c, Met = _score >= 70, Explanation = "stub"
                    })
                    .ToList()
            });
        }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static ArticleMetadata MakeArticle(string severity = "high") =>
        new(
            Article: "Article-9",
            Pillar: "Pillar1-Foundations",
            ControlId: "gdpr.art9.special_categories",
            Title: "Special categories",
            Severity: severity,
            PassThreshold: 0.90,
            WarnThreshold: 0.70,
            PillarWeight: 0.20);

    private static ScenarioSpec MakeScenario(string granularity, int criteriaCount = 3) =>
        new(
            Id: "gdpr-art9-001",
            Pattern: "direct",
            Weight: 0.30,
            Input: "Test input.",
            EvaluationCriteria: Enumerable.Range(1, criteriaCount)
                .Select(i => $"Criterion {i}")
                .ToList(),
            Granularity: granularity,
            Sensitive: true,
            ExpectedBehavior: null,
            Tags: new[] { "gdpr", "article-9" });

    // ── Mode-B tests ──────────────────────────────────────────────────────────

    [Fact]
    public void Build_ModeB_CompositeScenario_ReturnsCompositeEval()
    {
        // Arrange
        var judge = new FakeEvaluator();
        var sut = new ScenarioToAtomicEval(judge, useModeB: true);
        var article = MakeArticle();
        var scenario = MakeScenario(granularity: "composite", criteriaCount: 3);

        // Act
        var eval = sut.Build(article, scenario);

        // Assert
        Assert.IsType<CompositeEval>(eval);
        var composite = (CompositeEval)eval;
        Assert.Equal(3, composite.Components.Count);
        Assert.Equal("gdpr-art9-001", composite.Key);
        Assert.Contains("Mode-B", composite.Name);
        Assert.Equal(0.90, composite.Threshold);
    }

    [Fact]
    public void Build_ModeB_AtomicScenario_StillReturnsAtomicLlmEval()
    {
        // Arrange — useModeB=true but granularity is "atomic" → Mode B does not escalate
        var judge = new FakeEvaluator();
        var sut = new ScenarioToAtomicEval(judge, useModeB: true);
        var article = MakeArticle();
        var scenario = MakeScenario(granularity: "atomic", criteriaCount: 3);

        // Act
        var eval = sut.Build(article, scenario);

        // Assert
        Assert.IsType<AtomicLlmEval>(eval);
    }

    [Fact]
    public void Build_ModeA_CompositeScenario_ReturnsAtomicLlmEval()
    {
        // Arrange — useModeB=false (default) + composite-granularity → composite flag is just metadata
        var judge = new FakeEvaluator();
        var sut = new ScenarioToAtomicEval(judge, useModeB: false);
        var article = MakeArticle();
        var scenario = MakeScenario(granularity: "composite", criteriaCount: 3);

        // Act
        var eval = sut.Build(article, scenario);

        // Assert — Mode A treats composite-granularity as metadata only; returns AtomicLlmEval
        Assert.IsType<AtomicLlmEval>(eval);
    }

    [Fact]
    public void Build_ModeB_CompositeScenario_EachComponentHasCorrectWeight()
    {
        // Arrange — 4 criteria; each component weight should be 1/4 = 0.25
        var judge = new FakeEvaluator();
        var sut = new ScenarioToAtomicEval(judge, useModeB: true);
        var article = MakeArticle();
        var scenario = MakeScenario(granularity: "composite", criteriaCount: 4);

        // Act
        var eval = sut.Build(article, scenario);

        // Assert
        var composite = Assert.IsType<CompositeEval>(eval);
        Assert.All(composite.Components, c => Assert.Equal(0.25, c.Weight, precision: 10));
    }

    // ── Multi-judge path tests ────────────────────────────────────────────────

    [Fact]
    public void Build_MultiJudge_CriticalArticle_ReturnsMultiJudgeWrapper()
    {
        // Arrange — two judges + critical article → MultiJudgeWrapper
        var judge1 = new FakeEvaluator(90);
        var judge2 = new FakeEvaluator(85);
        var judges = new (AgentEval.Core.IEvaluator Judge, double Weight)[]
        {
            (judge1, 1.0),
            (judge2, 1.0),
        };
        var sut = new ScenarioToAtomicEval(judge1, judges: judges);
        var article = MakeArticle(severity: "critical");
        var scenario = MakeScenario(granularity: "atomic");

        // Act
        var eval = sut.Build(article, scenario);

        // Assert
        Assert.IsType<MultiJudgeWrapper>(eval);
        var wrapper = (MultiJudgeWrapper)eval;
        Assert.Equal(2, wrapper.Judges.Count);
        Assert.Equal("WeightedMedian", wrapper.Aggregation.Name);
    }

    [Fact]
    public void Build_MultiJudge_NonCriticalArticle_ReturnsSingleAtomicLlmEval()
    {
        // Arrange — two judges but article is high severity (not critical) → single atomic
        var judge1 = new FakeEvaluator(90);
        var judge2 = new FakeEvaluator(85);
        var judges = new (AgentEval.Core.IEvaluator Judge, double Weight)[]
        {
            (judge1, 1.0),
            (judge2, 1.0),
        };
        var sut = new ScenarioToAtomicEval(judge1, judges: judges);
        var article = MakeArticle(severity: "high");
        var scenario = MakeScenario(granularity: "atomic");

        // Act
        var eval = sut.Build(article, scenario);

        // Assert — multi-judge path only activates for critical severity
        Assert.IsType<AtomicLlmEval>(eval);
    }

    [Fact]
    public void Build_SingleJudgeList_ActsLikeSingleJudge()
    {
        // Arrange — only one judge in the list → no multi-judge wrapping even for critical
        var judge = new FakeEvaluator(90);
        var judges = new (AgentEval.Core.IEvaluator Judge, double Weight)[] { (judge, 1.0) };
        var sut = new ScenarioToAtomicEval(judge, judges: judges);
        var article = MakeArticle(severity: "critical");
        var scenario = MakeScenario(granularity: "atomic");

        // Act
        var eval = sut.Build(article, scenario);

        // Assert — single-judge list does not trigger multi-judge wrapper
        Assert.IsType<AtomicLlmEval>(eval);
    }
}
