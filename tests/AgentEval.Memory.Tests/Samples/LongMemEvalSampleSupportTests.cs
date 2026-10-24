// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;
using AgentEval.Samples;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Memory.Tests.Samples;

/// <summary>
/// The G8/G10 sample files' accounting and verdict, as shipped. On Bitdeer G10 sent all 50 questions to a model the
/// provider does not serve and reported "0/0 scored, 50 selected, 0 inconclusive", exit 0: the questions were gone from
/// the accounting and the run read as completed. One class, because <see cref="SampleOutcome"/> is process-wide state.
/// </summary>
[Collection("LongMemEval console")]
public class LongMemEvalSampleSupportTests
{
    [Fact]
    public void Accounting_UnscoredQuestions_KeepsFailureCategoriesAndDenominators()
    {
        static QuestionResult Question(string id, bool? correct, string? code, JudgeOutcomeStatus? status) => new()
        {
            QuestionId = id, QuestionType = "test", Question = "synthetic question", GoldAnswer = "yes",
            AgentResponse = "synthetic answer", Correct = correct, RawScore = correct is true ? 100 : null,
            SafeFailureCode = code, JudgeStatus = status,
        };
        var result = Result(4, 1, 1, 1, questions:
        [
            Question("scored", true, null, null), Question("agent", null, "agent_error", null),
            Question("judge", null, null, JudgeOutcomeStatus.Empty), Question("unknown", null, null, null),
        ]);

        var text = Capture(() => LongMemEvalSampleSupport.PrintAccounting(result));

        Assert.Contains("4 selected", text);
        Assert.Contains("1 scored (1 correct, 0 wrong)", text);
        Assert.Contains("2 judge-inconclusive", text);
        Assert.Contains("agent_error ×1", text);
        Assert.Contains("Empty ×1", text);
        Assert.Contains("unknown ×1", text);
        Assert.Contains("never counted right or wrong", text);
    }

    [Fact]
    public void PerTypeTable_PartialAndUnscoredTypes_DisclosesActualScoreDenominators()
    {
        var types = new Dictionary<string, TypeResult>
        {
            ["a-partial"] = new() { TypeName = "a-partial", TotalQuestions = 3, ScoredQuestions = 2,
                CorrectQuestions = 1, Duration = TimeSpan.Zero },
            ["b-unscored"] = new() { TypeName = "b-unscored", TotalQuestions = 2, ScoredQuestions = 0,
                CorrectQuestions = 0, Duration = TimeSpan.Zero },
        };

        var text = Capture(() => LongMemEvalSampleSupport.PrintPerTypeTable(Result(5, 2, 1, 0, types: types)));

        var partial = Assert.Single(text.Split('\n'), l => l.Contains("a-partial"));
        var unscored = Assert.Single(text.Split('\n'), l => l.Contains("b-unscored"));
        Assert.Contains(50d.ToString("F1") + "%", partial);
        Assert.Contains("n/a", unscored);
        Assert.Contains("Accuracy is correct / scored", text);
        Assert.True(text.IndexOf("a-partial", StringComparison.Ordinal) < text.IndexOf("b-unscored", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(40d)]
    [InlineData(70d)]
    public void Score_KnownZeroThresholdsAndAbsence_KeepTheirMeaning(double? score)
    {
        var expected = score is { } value ? value.ToString("F1") + "%" : "n/a";
        Assert.Equal(expected, Capture(() => LongMemEvalSampleSupport.PrintScore(score)).Trim());
    }

    [Fact]
    public void HaystackDelivery_NoCalls_ReportsNoAnswerAttempt()
    {
        var text = Capture(() => Assert.False(LongMemEvalSampleSupport.PrintHaystackDelivery([])));
        Assert.Contains("no answer call was made", text);
    }

    [Fact]
    public void DatasetPath_IsAbsoluteAndNamesTheExpectedDataset()
    {
        Assert.True(Path.IsPathFullyQualified(LongMemEvalSampleSupport.DatasetPath));
        Assert.EndsWith(Path.Combine("src", "AgentEval.Memory", "Data", "longmemeval", "longmemeval_s_cleaned.json"),
            LongMemEvalSampleSupport.DatasetPath);
    }

    [Theory]
    [InlineData(" first line\nsecond line", "first line")]
    [InlineData("short", "short")]
    public void Describe_MultilineException_KeepsOnlyItsFirstLine(string message, string expected)
    {
        Assert.Equal("InvalidOperationException: " + expected,
            LongMemEvalSampleSupport.Describe(new InvalidOperationException(message)));
    }

    [Fact]
    public void Describe_LongException_IsBounded()
    {
        Assert.Equal("InvalidOperationException: " + new string('x', 160) + "…",
            LongMemEvalSampleSupport.Describe(new InvalidOperationException(new string('x', 170))));
    }

    private static string Capture(Action action)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try { action(); return output.ToString(); }
        finally { Console.SetOut(original); }
    }
    [Fact]
    public void EveryAnswerCallFailing_IsAnError_NotACompletedRun()
    {
        SampleOutcome.Reset();

        LongMemEvalSampleSupport.RecordVerdict(Result(selected: 50, scored: 0, correct: 0, agentErrors: 50), truncated: false);

        Assert.Equal(SampleOutcome.RuntimeError, SampleOutcome.ExitCode);
        Assert.Contains("50 agent error(s) of 50 selected", Assert.Single(SampleOutcome.Records).Reason);
    }

    [Fact]
    public void QuestionsWithoutAVerdict_AreNotMeasured_AndCounted()
    {
        SampleOutcome.Reset();

        LongMemEvalSampleSupport.RecordVerdict(Result(selected: 50, scored: 47, correct: 45, agentErrors: 1), truncated: false);

        Assert.Equal(SampleOutcome.NotMeasured, SampleOutcome.ExitCode);
        Assert.Contains(
            "3 of 50 questions not scored (1 agent error(s), 2 judge-inconclusive): the accuracy is over the 47 scored",
            Assert.Single(SampleOutcome.Records).Reason);
    }

    [Fact]
    public void JudgeInconclusiveOnEveryQuestion_IsNotMeasured()
    {
        SampleOutcome.Reset();

        LongMemEvalSampleSupport.RecordVerdict(Result(selected: 4, scored: 0, correct: 0, agentErrors: 1), truncated: false);

        Assert.Equal(SampleOutcome.NotMeasured, SampleOutcome.ExitCode);
        Assert.Contains("no question was scored: 1 agent error(s) and 3 judge-inconclusive of 4 selected", SampleOutcome.Records[0].Reason);
    }

    [Fact]
    public void EveryQuestionScored_RecordsNothing()
    {
        SampleOutcome.Reset();

        LongMemEvalSampleSupport.RecordVerdict(Result(selected: 10, scored: 10, correct: 7, agentErrors: 0), truncated: false);

        Assert.Equal(SampleOutcome.Completed, SampleOutcome.ExitCode);
        Assert.Empty(SampleOutcome.Records);
    }

    [Fact]
    public void ATruncatedHaystack_FailsTheRun_HoweverWellItScored()
    {
        SampleOutcome.Reset();

        LongMemEvalSampleSupport.RecordVerdict(Result(selected: 10, scored: 10, correct: 10, agentErrors: 0), truncated: true);

        Assert.Equal(SampleOutcome.Failed, SampleOutcome.ExitCode);
    }

    [Fact]
    public void HaystackDelivery_FlagsAPromptTheProviderReadOnlyPartOf()
    {
        // What the Bitdeer run of 2026-10-21 measured: ~491K characters read as ~105K tokens, 4.7 per token.
        PromptMeasurement[] delivered = [new(491_344, 104_930, null), new(504_773, 107_909, null)];
        // A provider that kept the last 32K tokens of the same prompt.
        PromptMeasurement[] cut = [new(491_344, 104_930, null), new(504_773, 32_768, null)];

        Assert.False(LongMemEvalSampleSupport.PrintHaystackDelivery(delivered));
        Assert.True(LongMemEvalSampleSupport.PrintHaystackDelivery(cut));
    }

    [Fact]
    public void HaystackDelivery_WithoutReportedTokens_ClaimsNoTruncation()
    {
        // Not verified is said on the console; it is not evidence of a cut.
        PromptMeasurement[] unreported = [new(491_344, null, null), new(0, null, "ClientResultException: HTTP 400")];

        Assert.False(LongMemEvalSampleSupport.PrintHaystackDelivery(unreported));
    }

    [Fact]
    public async Task PromptSizeMeter_RecordsWhatWasSentAndWhatTheProviderCounted()
    {
        var meter = new PromptSizeMeter(new FixedUsageClient(inputTokens: 25));

        await meter.GetResponseAsync([new ChatMessage(ChatRole.System, "abcde"), new ChatMessage(ChatRole.User, "0123456789")]);

        var call = Assert.Single(meter.Calls);
        Assert.Equal(15, call.Characters);
        Assert.Equal(25, call.ReportedPromptTokens);
        Assert.Null(call.Error);
    }

    [Fact]
    public async Task PromptSizeMeter_KeepsAFailedCall_AndRethrows()
    {
        var meter = new PromptSizeMeter(new FixedUsageClient(inputTokens: 0, fail: "context length exceeded\nsecond line"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => meter.GetResponseAsync([new ChatMessage(ChatRole.User, "0123456789")]));

        var call = Assert.Single(meter.Calls);
        Assert.Equal(10, call.Characters);
        Assert.Null(call.ReportedPromptTokens);
        Assert.Equal("InvalidOperationException: context length exceeded", call.Error);
    }

    private static ExternalBenchmarkResult Result(int selected, int scored, int correct, int agentErrors,
        QuestionResult[]? questions = null, Dictionary<string, TypeResult>? types = null) => new()
    {
        BenchmarkId = "longmemeval",
        BenchmarkName = $"LongMemEval-S {selected}q",
        OverallAccuracy = scored > 0 ? (double)correct / scored * 100 : null,
        TaskAveragedAccuracy = scored > 0 ? (double)correct / scored * 100 : null,
        SelectedQuestions = selected,
        AgentCompletedQuestions = selected - agentErrors,
        ScoredQuestions = scored,
        CorrectQuestions = correct,
        IncorrectQuestions = scored - correct,
        InconclusiveQuestions = selected - agentErrors - scored,
        AgentFailureQuestions = agentErrors,
        PerTypeResults = types ?? new Dictionary<string, TypeResult>(),
        QuestionResults = questions ?? [],
        Duration = TimeSpan.FromSeconds(1),
        Options = new ExternalBenchmarkOptions { DatasetMode = "S" }
    };

    [Fact]
    public async Task PromptSizeMeter_CallerCancellation_IsNotRecordedAsAProviderFailure()
    {
        var meter = new PromptSizeMeter(new FixedUsageClient(inputTokens: 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            meter.GetResponseAsync([new ChatMessage(ChatRole.User, "synthetic")], cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(meter.Calls);
    }

    private sealed class FixedUsageClient(long inputTokens, string? fail = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fail is not null)
                throw new InvalidOperationException(fail);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                Usage = new UsageDetails { InputTokenCount = inputTokens },
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

[CollectionDefinition("LongMemEval console", DisableParallelization = true)]
public sealed class LongMemEvalConsoleCollection;
