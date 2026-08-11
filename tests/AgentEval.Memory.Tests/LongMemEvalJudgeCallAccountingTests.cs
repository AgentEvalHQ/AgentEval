// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using static AgentEval.Memory.Tests.LongMemEvalStructuredJudgeTests;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Separates judge retries from first-attempt calls, so a caller reconciling an exact provider-call
/// budget can see which calls AgentEval chose to make on its own.
/// </summary>
/// <remarks>
/// <see cref="ExternalJudgmentResult.LlmCallCount"/> counts every provider call, retries included, and
/// on its own cannot distinguish "the judge was called twice because the run asked for two questions"
/// from "the judge was called twice because the first answer was unreadable". A validity gate that
/// fail-closes on an exact count therefore rejects runs whose only anomaly was an internal retry.
/// </remarks>
public class LongMemEvalJudgeCallAccountingTests
{
    [Fact]
    public async Task CleanVerdict_CountsOnePrimaryCallAndNoRetries()
    {
        var judge = CreateJudge(Response("yes"));

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 1
        });

        Assert.Equal(JudgeOutcomeStatus.Yes, result.Status);
        Assert.Equal(1, result.LlmCallCount);
        Assert.Equal(1, result.PrimaryLlmCallCount);
        Assert.Equal(0, result.RetryLlmCallCount);
        Assert.Equal(1, result.AttemptsUsed);
    }

    [Fact]
    public async Task ForcedRetry_ReportsPrimaryAndRetryCallsDistinctly()
    {
        // First response is unparseable, second is a clean verdict — the exact shape that inflates
        // LlmCallCount today with no way to attribute the extra call.
        var judge = CreateJudge(Response("I cannot tell"), Response("yes"));

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 1
        });

        Assert.Equal(JudgeOutcomeStatus.Yes, result.Status);
        Assert.Equal(2, result.LlmCallCount);
        // The whole point: the second call is attributable to a retry, not to the question.
        Assert.Equal(1, result.PrimaryLlmCallCount);
        Assert.Equal(1, result.RetryLlmCallCount);
        Assert.Equal(2, result.AttemptsUsed);
    }

    [Fact]
    public async Task ExhaustedRetries_StillAttributesEveryCall()
    {
        var judge = CreateJudge(Response("maybe"), Response("unclear"), Response("hmm"));

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 2
        });

        Assert.Equal(JudgeOutcomeStatus.Invalid, result.Status);
        Assert.Equal(3, result.LlmCallCount);
        Assert.Equal(1, result.PrimaryLlmCallCount);
        Assert.Equal(2, result.RetryLlmCallCount);
        Assert.Equal(3, result.AttemptsUsed);
    }

    [Fact]
    public async Task ProviderThrowsThenSucceeds_CountsTheFailedCallAsPrimary()
    {
        var client = new SequencedChatClient(
            _ => throw new InvalidOperationException("boom"),
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")));
        var judge = new LongMemEvalJudge(client, NullLogger<LongMemEvalJudge>.Instance);

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 1
        });

        Assert.Equal(JudgeOutcomeStatus.Yes, result.Status);
        // The throwing call was still paid for and still belongs to the first attempt.
        Assert.Equal(1, result.PrimaryLlmCallCount);
        Assert.Equal(1, result.RetryLlmCallCount);
        Assert.Equal(2, result.LlmCallCount);
    }

    [Fact]
    public async Task ResponseFormatFallback_CountsAsPrimaryNotAsRetry()
    {
        // One logical attempt can cost three provider calls when the provider rejects the schema and
        // then JSON mode. None of those is a retry, and counting them as retries would understate
        // what a single attempt costs.
        var client = new SequencedChatClient(
            _ => throw new InvalidOperationException("400 invalid_request_error: response_format is not supported"),
            _ => throw new InvalidOperationException("400 invalid_request_error: json_object is not supported"),
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"verdict":"yes","reasoning":"ok"}""")));
        var judge = new LongMemEvalJudge(client, NullLogger<LongMemEvalJudge>.Instance);

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            JudgeVerdictProtocol = JudgeVerdictProtocol.StructuredJson,
            MaxJudgeRetries = 1
        });

        Assert.Equal(JudgeOutcomeStatus.Yes, result.Status);
        Assert.Equal(3, result.LlmCallCount);
        Assert.Equal(3, result.PrimaryLlmCallCount);
        Assert.Equal(0, result.RetryLlmCallCount);
        Assert.Equal(1, result.AttemptsUsed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TotalAlwaysEqualsPrimaryPlusRetry(int maxRetries)
    {
        var judge = CreateJudge(
            Response("maybe"), Response("maybe"), Response("maybe"), Response("maybe"));

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = maxRetries
        });

        Assert.Equal(result.LlmCallCount, result.PrimaryLlmCallCount + result.RetryLlmCallCount);
    }

    /// <summary>Chat client driven by per-call behaviours, so a call can throw rather than answer.</summary>
    private sealed class SequencedChatClient(params Func<ChatOptions?, ChatResponse>[] behaviours) : IChatClient
    {
        private readonly Queue<Func<ChatOptions?, ChatResponse>> _behaviours = new(behaviours);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_behaviours.Count == 0)
                throw new InvalidOperationException("No behaviour configured.");
            return Task.FromResult(_behaviours.Dequeue().Invoke(options));
        }

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
