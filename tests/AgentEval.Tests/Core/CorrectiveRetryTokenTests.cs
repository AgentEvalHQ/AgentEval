// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using AgentEval.Core;
using AgentEval.Evals;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Core;

public sealed class CorrectiveRetryTokenTests
{
    [Theory]
    [InlineData(1, "all")]
    [InlineData(2, "all")]
    [InlineData(1, "input")]
    [InlineData(2, "input")]
    [InlineData(1, "output")]
    [InlineData(2, "output")]
    public async Task CorrectiveRetry_PartialUsage_IsNotPricedOrTotalled(int missingAttempt, string component)
    {
        var result = await Evaluate(call => call == missingAttempt
            ? component == "all" ? null : new UsageDetails
            {
                InputTokenCount = component == "input" ? null : 100,
                OutputTokenCount = component == "output" ? null : 50
            }
            : new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 });
        Assert.True(result.Score.Passed);
        Assert.Null(result.Provenance.TokensUsed);
        Assert.Null(result.Provenance.EstimatedCost);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 50)]
    public async Task CorrectiveRetry_CompleteUsage_PreservesMeasuredTotals(long input, long output)
    {
        var result = await Evaluate(_ => new UsageDetails { InputTokenCount = input, OutputTokenCount = output });
        Assert.Equal((int)(2 * (input + output)), result.Provenance.TokensUsed);
        Assert.NotNull(result.Provenance.EstimatedCost);
        if (input + output == 0) Assert.Equal(0, result.Provenance.EstimatedCost);
    }

    private static Task<EvalResult> Evaluate(Func<int, UsageDetails?> usage) => new AtomicLlmEval(
        new ChatClientEvaluator(new RetryClient(usage)), "retry-tokens", "Retry tokens", "quality", "1.0.0",
        ["accuracy"], judgeModel: "gpt-4o-mini").EvaluateAsync(new EvalInput("q", Response: "a"));

    private sealed class RetryClient(Func<int, UsageDetails?> usage) : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var call = ++_calls;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                call == 1 ? "no usable verdict" : """{"overallScore":90}""")) { Usage = usage(call) });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
