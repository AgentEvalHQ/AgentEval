// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Xunit;

namespace AgentEval.Memory.Tests;

public sealed class LongMemEvalEvalResultAdapterTests
{
    [Fact]
    public void ToEvalResult_MixedOutcomes_PreservesTriStateCountsAndOmitsRawContent()
    {
        var native = Result(
            Question("q-pass", true, JudgeOutcomeStatus.Yes, "PRIVATE QUESTION 1"),
            Question("q-fail", false, JudgeOutcomeStatus.No, "PRIVATE QUESTION 2"),
            Question(
                "q-null",
                null,
                JudgeOutcomeStatus.Invalid,
                "PRIVATE QUESTION 3",
                evidence: new QuestionEvidenceEnvelope
                {
                    SchemaVersion = QuestionEvidenceEnvelope.CurrentSchemaVersion,
                    Retrieved =
                    [
                        new EvidenceReference
                        {
                            Id = "private-reference",
                            Rank = 1,
                            Content = "PRIVATE EVIDENCE CONTENT",
                        }
                    ],
                },
                diagnostics: new QuestionEvidenceDiagnostics
                {
                    Status = EvidenceObservationStatus.Observed,
                    RetrievedReferenceCount = 1,
                },
                safeFailureCode: "PRIVATE FAILURE DETAIL"));

        var report = LongMemEvalEvalResultAdapter.ToEvalResult(
            native,
            presetName: "smoke",
            judgeModel: "judge-deployment");

        Assert.Equal("warn", report.Score.Label);
        Assert.True(report.Score.Passed);
        Assert.Equal(3, report.Details.Dimensions!["selectedQuestions"]);
        Assert.Equal(2, report.Details.Dimensions["scoredQuestions"]);
        Assert.Equal(1, report.Details.Dimensions["inconclusiveQuestions"]);
        Assert.Equal((int)JudgeFailurePolicy.RetryThenInconclusive,
            report.Details.Dimensions["judgeFailurePolicy"]);
        Assert.Equal(256, report.Details.Dimensions["judgeMaxOutputTokens"]);
        Assert.Equal(0, report.Details.Dimensions["judgeTemperatureConfigured"]);

        var type = Assert.Single(report.Details.SubResults!);
        Assert.Equal("scored-questions-only", type.Details.AggregationStrategy);
        Assert.Equal(2, type.Details.Dimensions!["scoredQuestions"]);

        var inconclusive = Assert.Single(
            type.Details.SubResults!, q => q.Score.Label == "inconclusive");
        Assert.False(inconclusive.Score.Passed);
        Assert.Equal(1, inconclusive.Details.Dimensions!["isInconclusive"]);
        Assert.Equal(1, inconclusive.Details.Dimensions["hasCapturedEvidence"]);
        Assert.Equal(1, inconclusive.Details.Dimensions["hasEvidenceDiagnostics"]);
        Assert.Contains(
            inconclusive.Details.Evidence!,
            e => e.Source == "judge-status" && e.Message == "Invalid");
        Assert.Contains(
            inconclusive.Details.Evidence!,
            e => e.Source == "evidence-status" && e.Message == "CapturedAndDiagnosed");

        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("PRIVATE QUESTION", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE GOLD", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE RESPONSE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE EXPLANATION", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE EVIDENCE CONTENT", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-reference", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE FAILURE DETAIL", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToEvalResult_ZeroScored_IsInconclusiveRatherThanFailedAggregate()
    {
        var native = Result(
            Question("q-null", null, JudgeOutcomeStatus.Empty, "question"));

        var report = LongMemEvalEvalResultAdapter.ToEvalResult(native, "smoke");

        Assert.Equal("inconclusive", report.Score.Label);
        Assert.False(report.Score.Passed);
        Assert.Equal(0, report.Score.Value);
        var type = Assert.Single(report.Details.SubResults!);
        Assert.Equal("inconclusive", type.Score.Label);
        Assert.Equal(0, type.Details.Dimensions!["scoredQuestions"]);
        var leaf = Assert.Single(type.Details.SubResults!);
        Assert.Equal("inconclusive", leaf.Score.Label);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    [InlineData(double.NaN)]
    public void ToEvalResult_InvalidThreshold_Throws(double threshold)
    {
        var native = Result(Question("q", true, JudgeOutcomeStatus.Yes, "question"));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => LongMemEvalEvalResultAdapter.ToEvalResult(native, "smoke", passThresholdPercent: threshold));
    }

    [Fact]
    public void ToEvalResult_CustomThreshold_ClassifiesAgainstConfiguredThreshold()
    {
        var native = Result(
            Question("q-pass", true, JudgeOutcomeStatus.Yes, "question"),
            Question("q-pass-2", true, JudgeOutcomeStatus.Yes, "question"),
            Question("q-pass-3", true, JudgeOutcomeStatus.Yes, "question"),
            Question("q-fail", false, JudgeOutcomeStatus.No, "question"));

        var report = LongMemEvalEvalResultAdapter.ToEvalResult(
            native,
            "smoke",
            passThresholdPercent: 80);

        Assert.Equal(0.75, report.Score.Value);
        Assert.Equal("fail", report.Score.Label);
        Assert.False(report.Score.Passed);
    }

    [Fact]
    public void ToEvalResult_NoRunCost_IsNotPriced_NotZero()
    {
        // T63 / T13-9: the root wrote `EstimatedCostUsd ?? 0`, and every judged node 0: free.
        var native = Result(Question("q-pass", true, JudgeOutcomeStatus.Yes, "question"));

        var report = LongMemEvalEvalResultAdapter.ToEvalResult(native, "smoke");

        Assert.Null(native.EstimatedCostUsd);
        Assert.Null(report.Provenance.EstimatedCost);
        var type = Assert.Single(report.Details.SubResults!);
        Assert.Null(type.Provenance.EstimatedCost);
        Assert.Null(Assert.Single(type.Details.SubResults!).Provenance.EstimatedCost); // a judged question, not priced
    }

    [Fact]
    public void ToEvalResult_ARunCost_IsCarriedThrough()
    {
        var native = Result(0.42, Question("q-pass", true, JudgeOutcomeStatus.Yes, "question"));

        var report = LongMemEvalEvalResultAdapter.ToEvalResult(native, "smoke");

        Assert.Equal(0.42, report.Provenance.EstimatedCost);
    }

    [Fact]
    public void ToEvalResult_AQuestionNoJudgeWasCalledFor_SpentNothing()
    {
        // Guard: no judge call is a real 0, not "not priced".
        var unjudged = Question("q-unjudged", null, JudgeOutcomeStatus.Empty, "question", judgeLlmCallCount: 0);

        var report = LongMemEvalEvalResultAdapter.ToEvalResult(Result(unjudged), "smoke");

        var type = Assert.Single(report.Details.SubResults!);
        Assert.Equal(0.0, Assert.Single(type.Details.SubResults!).Provenance.EstimatedCost);
        Assert.Equal(0.0, type.Provenance.EstimatedCost);
    }

    private static ExternalBenchmarkResult Result(params QuestionResult[] questions) => Result(null, questions);

    private static ExternalBenchmarkResult Result(double? estimatedCostUsd, params QuestionResult[] questions)
    {
        var scored = questions.Count(q => q.Correct.HasValue);
        var correct = questions.Count(q => q.Correct is true);
        var inconclusive = questions.Count(q => q.Correct is null);
        var accuracy = scored == 0 ? (double?)null : (double)correct / scored * 100;
        var type = new TypeResult
        {
            TypeName = "multi-session",
            TotalQuestions = questions.Length,
            ScoredQuestions = scored,
            CorrectQuestions = correct,
            InconclusiveQuestions = inconclusive,
            Duration = TimeSpan.FromSeconds(1),
        };

        return new ExternalBenchmarkResult
        {
            BenchmarkId = "longmemeval",
            BenchmarkName = "LongMemEval-S",
            OverallAccuracy = accuracy,
            TaskAveragedAccuracy = accuracy,
            SelectedQuestions = questions.Length,
            AgentCompletedQuestions = questions.Length,
            ScoredQuestions = scored,
            CorrectQuestions = correct,
            IncorrectQuestions = scored - correct,
            InconclusiveQuestions = inconclusive,
            ScoredTypeCount = scored == 0 ? 0 : 1,
            PerTypeResults = new Dictionary<string, TypeResult>
            {
                ["multi-session"] = type,
            },
            QuestionResults = questions,
            Duration = TimeSpan.FromSeconds(1),
            TotalLlmCalls = questions.Sum(q => q.AgentLlmCallCount + q.JudgeLlmCallCount),
            EstimatedCostUsd = estimatedCostUsd,
            Options = new ExternalBenchmarkOptions(),
        };
    }

    private static QuestionResult Question(
        string id,
        bool? correct,
        JudgeOutcomeStatus judgeStatus,
        string question,
        QuestionEvidenceEnvelope? evidence = null,
        QuestionEvidenceDiagnostics? diagnostics = null,
        string? safeFailureCode = null,
        int judgeLlmCallCount = 1) =>
        new()
        {
            QuestionId = id,
            QuestionType = "multi-session",
            Question = question,
            GoldAnswer = "PRIVATE GOLD",
            AgentResponse = "PRIVATE RESPONSE",
            Correct = correct,
            RawScore = correct switch { true => 100, false => 0, null => null },
            JudgeStatus = judgeStatus,
            AgentLlmCallCount = 1,
            JudgeLlmCallCount = judgeLlmCallCount,
            JudgeTokensUsed = 10,
            Evidence = evidence,
            EvidenceDiagnostics = diagnostics,
            SafeFailureCode = safeFailureCode,
            JudgeExplanation = "PRIVATE EXPLANATION",
            Duration = TimeSpan.FromMilliseconds(10),
        };
}
