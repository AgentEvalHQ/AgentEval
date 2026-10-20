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
public class LongMemEvalSampleSupportTests
{
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

    private static ExternalBenchmarkResult Result(int selected, int scored, int correct, int agentErrors) => new()
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
        PerTypeResults = new Dictionary<string, TypeResult>(),
        QuestionResults = [],
        Duration = TimeSpan.FromSeconds(1),
        Options = new ExternalBenchmarkOptions { DatasetMode = "S" }
    };

    private sealed class FixedUsageClient(long inputTokens, string? fail = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
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
