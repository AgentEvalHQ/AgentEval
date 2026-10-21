// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// A LongMemEval or TypedMemEval node with nothing scored is not measured: its placeholder 0 never counts toward an
/// aggregate, never reads as a failure, and a question that did not run is skipped (T13-6).
/// </summary>
public sealed class BenchmarkAdaptersNotMeasuredTests
{
    [Fact]
    public void LongMemEval_ATypeWithNothingScored_AndTheRoot_AreNotMeasured()
    {
        var native = LongMemEvalResult(Question("q-null", correct: null));

        var report = LongMemEvalEvalResultAdapter.ToEvalResult(native, "Quick");

        Assert.False(report.Score.CountsTowardAggregate());
        Assert.Equal(MeasurementState.NotMeasured, report.Score.CensusBucket());
        var type = Assert.Single(report.Details.SubResults!);
        Assert.False(type.Score.CountsTowardAggregate());
        Assert.Equal(RunStatsBucket.Skipped, type.Score.StatsBucket());
        var leaf = Assert.Single(type.Details.SubResults!);
        Assert.Equal("inconclusive", leaf.Score.Label);
        Assert.Equal(MeasurementState.NotMeasured, leaf.Score.CensusBucket());
    }

    [Fact]
    public void LongMemEval_AnAgentFailure_IsAnError_NotAnInconclusiveJudgment()
    {
        var native = LongMemEvalResult(
            Question("q-ok", correct: true),
            Question("q-crash", correct: null, QuestionExecutionStatus.AgentError));

        var type = Assert.Single(LongMemEvalEvalResultAdapter.ToEvalResult(native, "Quick").Details.SubResults!);

        var crash = type.Details.SubResults!.Single(q => q.Metric.Key.EndsWith("q-crash", StringComparison.Ordinal));
        Assert.Equal("error", crash.Score.Label);
        Assert.Equal(MeasurementState.NotMeasured, crash.Score.CensusBucket());
    }

    [Fact]
    public void LongMemEval_AScoredRun_IsStillMeasured()
    {
        // Guard: a type and root with an accuracy keep their verdict and count.
        var report = LongMemEvalEvalResultAdapter.ToEvalResult(LongMemEvalResult(Question("q-ok", correct: true)), "Quick");

        Assert.True(report.Score.CountsTowardAggregate());
        Assert.Equal("pass", report.Score.Label);
        Assert.True(Assert.Single(report.Details.SubResults!).Score.CountsTowardAggregate());
    }

    [Fact]
    public async Task TypedMemEval_WhenTheJudgeNeverAnswers_NoNodeCountsTowardAnAggregate_AndUnrunReadsSkipped()
    {
        // The judge is down for the whole run (the failure path): every question it was asked is inconclusive, and the
        // duration questions are unrun because this run withholds timestamps.
        var runner = new TypedMemEvalRunner(new UnreachableJudge());
        var result = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(),
            TypedMemEvalVertical.Arithmetic,
            new TypedMemEvalOptions { IncludeTimestamps = false });

        var eval = TypedMemEvalEvalResultAdapter.ToEvalResult(result);

        var nodes = Flatten(eval).ToList();
        Assert.All(nodes, n => Assert.False(n.Score.CountsTowardAggregate(), $"{n.Metric.Key} counts toward an aggregate"));
        Assert.All(nodes, n => Assert.Equal(RunStatsBucket.Skipped, n.Score.StatsBucket()));

        var unrun = nodes.Where(n => n.Details.SubResults is null && n.Score.Label == "skipped").ToList();
        Assert.Equal(result.TypedOutcomes!.Outcomes.Unrun, unrun.Count);
        Assert.NotEmpty(unrun);
        Assert.All(unrun, n => Assert.Equal("SKIPPED", n.Score.ReportStatus()));
    }

    private static IEnumerable<EvalResult> Flatten(EvalResult node) =>
        new[] { node }.Concat((node.Details.SubResults ?? []).SelectMany(Flatten));

    private static QuestionResult Question(
        string id,
        bool? correct,
        QuestionExecutionStatus status = QuestionExecutionStatus.Completed) => new()
    {
        QuestionId = id,
        QuestionType = "temporal-reasoning",
        Question = "q",
        GoldAnswer = "g",
        AgentResponse = "a",
        Correct = correct,
        RawScore = correct switch { true => 100, false => 0, null => null },
        ExecutionStatus = status,
    };

    private static ExternalBenchmarkResult LongMemEvalResult(params QuestionResult[] questions)
    {
        var scored = questions.Count(q => q.Correct.HasValue);
        var correct = questions.Count(q => q.Correct is true);
        var accuracy = scored == 0 ? (double?)null : (double)correct / scored * 100;
        return new ExternalBenchmarkResult
        {
            BenchmarkId = "longmemeval",
            BenchmarkName = "LongMemEval-S",
            OverallAccuracy = accuracy,
            TaskAveragedAccuracy = accuracy,
            SelectedQuestions = questions.Length,
            ScoredQuestions = scored,
            CorrectQuestions = correct,
            InconclusiveQuestions = questions.Count(q => q.Correct is null && q.ExecutionStatus == QuestionExecutionStatus.Completed),
            AgentFailureQuestions = questions.Count(q => q.ExecutionStatus != QuestionExecutionStatus.Completed),
            PerTypeResults = new Dictionary<string, TypeResult>
            {
                ["temporal-reasoning"] = new()
                {
                    TypeName = "temporal-reasoning",
                    TotalQuestions = questions.Length,
                    ScoredQuestions = scored,
                    CorrectQuestions = correct,
                    InconclusiveQuestions = questions.Count(q => q.Correct is null),
                    Duration = TimeSpan.FromSeconds(1),
                },
            },
            QuestionResults = questions,
            Duration = TimeSpan.FromSeconds(1),
            Options = new ExternalBenchmarkOptions(),
        };
    }

    /// <summary>A judge endpoint that is down: every call fails, so no verdict is ever produced.</summary>
    private sealed class UnreachableJudge : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new HttpRequestException("503 Service Unavailable");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
