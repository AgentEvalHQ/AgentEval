// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.Reporting;

/// <summary>
/// An external-benchmark baseline keeps only scores, so whatever a score is NOT over has to travel with it. The Bitdeer
/// sweep of 2026-10-20 saved a LongMemEval baseline at 95.7% "over 3" single-session-preference questions when one was
/// scored, and with no trace of the 3 of 50 questions that had no verdict.
/// </summary>
public class ExternalBaselineExtensionsTests
{
    private static readonly AgentBenchmarkConfig Config = new()
    {
        AgentName = "LongMemEval-Agent",
        ModelId = "test-model",
        ReducerStrategy = "None",
        MemoryProvider = "TextBlob"
    };

    [Fact]
    public void ScenarioCount_IsTheScoredDenominator_NotTheSelectedCount()
    {
        // 3 selected, 1 scored (wrong), 2 without a verdict: the 0% is over 1 question, not 3.
        var result = Result(selected: 3, scored: 1, correct: 0, agentErrors: 1, inconclusive: 1);

        var baseline = result.ToBaseline("run", Config);

        var entry = baseline.CategoryResults["single-session-preference"];
        Assert.Equal(1, entry.ScenarioCount);
        Assert.Equal(0, entry.Score);
    }

    [Fact]
    public void UnscoredQuestions_AreStatedWithTheScore()
    {
        var result = Result(selected: 50, scored: 47, correct: 45, agentErrors: 1, inconclusive: 2);

        var baseline = result.ToBaseline("run", Config);

        Assert.Contains(
            "3 of 50 selected questions are not in the score (1 agent error(s), 2 judge-inconclusive): accuracy is over the 47 scored.",
            baseline.Recommendations);
    }

    [Fact]
    public void EveryQuestionScored_AddsNoUnscoredLine()
    {
        var result = Result(selected: 4, scored: 4, correct: 3, agentErrors: 0, inconclusive: 0);

        var baseline = result.ToBaseline("run", Config);

        Assert.DoesNotContain(baseline.Recommendations, r => r.Contains("not in the score", StringComparison.Ordinal));
        Assert.Equal(4, baseline.CategoryResults["single-session-preference"].ScenarioCount);
    }

    [Fact]
    public void InconclusiveScoredAsIncorrect_IsInTheScore_SoItIsNotReportedAsLeftOut()
    {
        // JudgeFailurePolicy.RetryThenIncorrect: the inconclusive question is in the denominator, as a wrong answer.
        var result = Result(selected: 2, scored: 2, correct: 1, agentErrors: 0, inconclusive: 1);

        var baseline = result.ToBaseline("run", Config);

        Assert.DoesNotContain(baseline.Recommendations, r => r.Contains("not in the score", StringComparison.Ordinal));
    }

    [Fact]
    public void NoQuestionScored_RefusesToMakeAScoredBaseline()
    {
        // G10 on a provider without its model: every question an agent error. Refused rather than stored as 0%.
        var result = Result(selected: 50, scored: 0, correct: 0, agentErrors: 50, inconclusive: 0);

        Assert.Throws<InvalidOperationException>(() => result.ToBaseline("run", Config));
    }

    private static ExternalBenchmarkResult Result(int selected, int scored, int correct, int agentErrors, int inconclusive)
    {
        double? accuracy = scored > 0 ? (double)correct / scored * 100 : null;
        var type = new TypeResult
        {
            TypeName = "single-session-preference",
            TotalQuestions = selected,
            ScoredQuestions = scored,
            CorrectQuestions = correct,
            InconclusiveQuestions = inconclusive,
            AgentFailureQuestions = agentErrors,
            Duration = TimeSpan.FromSeconds(1)
        };
        return new ExternalBenchmarkResult
        {
            BenchmarkId = "longmemeval",
            BenchmarkName = $"LongMemEval-S {selected}q",
            OverallAccuracy = accuracy,
            TaskAveragedAccuracy = accuracy,
            SelectedQuestions = selected,
            AgentCompletedQuestions = selected - agentErrors,
            ScoredQuestions = scored,
            CorrectQuestions = correct,
            IncorrectQuestions = scored - correct,
            InconclusiveQuestions = inconclusive,
            AgentFailureQuestions = agentErrors,
            PerTypeResults = new Dictionary<string, TypeResult> { [type.TypeName] = type },
            QuestionResults = [],
            Duration = TimeSpan.FromSeconds(1),
            Options = new ExternalBenchmarkOptions { DatasetMode = "S" }
        };
    }
}
