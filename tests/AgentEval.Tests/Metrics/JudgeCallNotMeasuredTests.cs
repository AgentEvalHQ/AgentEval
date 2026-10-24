// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.MAF.Evaluators;
using AgentEval.Metrics.RAG;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

using EvaluationContext = AgentEval.Core.EvaluationContext;

namespace AgentEval.Tests.Metrics;

/// <summary>
/// T59 (re-check T13-2): a judge call that failed measured nothing, yet the LLM metrics threw out of the run (a provider
/// error, a timeout), <see cref="RetryPolicyExtensions.EvaluateWithRetryAsync"/> turned retries that ran out into a Fail
/// at 0, and the MAF wrappers turned a thrown metric into a measured 0 rated Poor. Each is now not measured with the
/// error; the caller's own cancellation still propagates.
/// </summary>
public sealed class JudgeCallNotMeasuredTests
{
    private static EvaluationContext Full => JudgeReplyNotMeasuredTests.Full;

    public static TheoryData<string> JudgePaths()
    {
        var data = new TheoryData<string>();
        foreach (var path in JudgeReplyNotMeasuredTests.JudgePaths.Keys)
            data.Add(path);
        return data;
    }

    [Theory]
    [MemberData(nameof(JudgePaths))]
    public async Task AFailedJudgeCall_IsNotMeasured_WithTheError_NotThrown(string path)
    {
        var judge = new FakeChatClient { ThrowOnNextCall = true, ThrowMessage = "HTTP 503 from the provider" };

        var result = await JudgeReplyNotMeasuredTests.JudgePaths[path](judge, CancellationToken.None);

        Assert.False(result.Measured, path);
        Assert.False(result.Passed, path);
        Assert.Equal(NotMeasuredCause.JudgeCallFailed, result.NotMeasuredCause);
        Assert.Equal("System.InvalidOperationException", result.Details!["errorType"]);
        Assert.Contains("HTTP 503 from the provider", result.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(JudgePaths))]
    public async Task AProviderSideTimeout_IsAFailedCall_NotACancellation(string path)
    {
        // HttpClient reports its own timeout as a TaskCanceledException while the caller's token is not cancelled.
        var judge = new ThrowingChatClient(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));

        var result = await JudgeReplyNotMeasuredTests.JudgePaths[path](judge, CancellationToken.None);

        Assert.False(result.Measured, path);
        Assert.Equal(NotMeasuredCause.JudgeCallFailed, result.NotMeasuredCause);
    }

    [Theory]
    [MemberData(nameof(JudgePaths))]
    public async Task TheCallersCancellation_StillPropagates(string path)
    {
        // Guard: catching the failed call must not swallow the caller cancelling.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => JudgeReplyNotMeasuredTests.JudgePaths[path](new CancellationHonouringChatClient(), cts.Token));
    }

    [Fact]
    public async Task EveryJudgeMetricInCore_ReportsAFailedCallAsNotMeasured()
    {
        // Census: a judge metric added later cannot slip back to throwing out of the run.
        var metrics = JudgeReplyNotMeasuredTests.JudgeMetrics(() => new FakeChatClient { ThrowOnNextCall = true });
        Assert.True(metrics.Count >= 12, $"found {metrics.Count} judge metrics; the census would pass vacuously");

        foreach (var metric in metrics)
        {
            var result = await metric.EvaluateAsync(Full);
            Assert.False(result.Measured, metric.GetType().Name);
            Assert.Equal(NotMeasuredCause.JudgeCallFailed, result.NotMeasuredCause);
        }
    }

    [Fact]
    public async Task RetriesThatRunOut_AreNotMeasured_WithTheLastError()
    {
        var policy = new RetryPolicy { MaxRetries = 1, InitialDelayMs = 0 };

        var result = await new ThrowingMetric(new HttpRequestException("503 Service Unavailable"))
            .EvaluateWithRetryAsync(Full, policy);

        Assert.False(result.Measured);
        Assert.False(result.Passed);
        Assert.Equal(NotMeasuredCause.JudgeCallFailed, result.NotMeasuredCause);
        Assert.Equal(2, result.Details!["attemptCount"]);
        Assert.Equal("System.Net.Http.HttpRequestException", result.Details["errorType"]);
    }

    [Fact]
    public async Task ABuiltInMetricsFailedCall_IsRetried_AndNotMeasuredWhenTheRetriesRunOut()
    {
        // The metric now catches its failed call; the retry still retries it, as it retried the throw.
        var judge = new ThrowingChatClient(new HttpRequestException("503 Service Unavailable"));
        var policy = new RetryPolicy { MaxRetries = 1, InitialDelayMs = 0 };

        var result = await new FaithfulnessMetric(judge).EvaluateWithRetryAsync(Full, policy);

        Assert.Equal(2, judge.CallCount);
        Assert.False(result.Measured);
        Assert.Equal(NotMeasuredCause.JudgeCallFailed, result.NotMeasuredCause);
        Assert.Equal(2, result.Details!["attemptCount"]);
        Assert.Contains("failed after 2 attempts", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuiltInMetricsTransientFailure_IsRetried_AndMeasured()
    {
        // Guard for the above: a judge that fails once and then answers is measured on the retry.
        var judge = new FakeChatClient("""{"score": 90, "reasoning": "supported"}""") { ThrowOnNextCall = true };

        var result = await new FaithfulnessMetric(judge).EvaluateWithRetryAsync(
            Full, new RetryPolicy { MaxRetries = 1, InitialDelayMs = 0 });

        Assert.True(result.Measured);
        Assert.Equal(90, result.Score);
    }

    [Fact]
    public async Task AFailedCallTheRetryPolicyDeclines_IsNotMeasured_NotThrown()
    {
        // A ShouldRetry that refuses the failure (e.g. an auth error) used to rethrow the metric's exception.
        var judge = new ThrowingChatClient(new HttpRequestException("401 Unauthorized"));
        var policy = new RetryPolicy { MaxRetries = 3, InitialDelayMs = 0, ShouldRetry = _ => false };

        var result = await new FaithfulnessMetric(judge).EvaluateWithRetryAsync(Full, policy);

        Assert.Equal(1, judge.CallCount);
        Assert.False(result.Measured);
        Assert.Equal(NotMeasuredCause.JudgeCallFailed, result.NotMeasuredCause);
    }

    [Fact]
    public async Task TheMafMetricAdapter_GivesAThrownMetricNoValue()
    {
        // As a Fail at 0 it became a measured MEAI value of 1.0 rated Poor.
        var adapter = new AgentEvalMetricAdapter(new ThrowingMetric(new InvalidOperationException("Metric crashed")));

        var result = await adapter.EvaluateAsync(Conversation(), Reply());

        var metric = (NumericMetric)result.Metrics["throwing_metric"];
        Assert.Null(metric.Value);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation!.Rating);
        Assert.True(metric.Interpretation.Failed);
        Assert.Contains("Metric crashed", metric.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, true)]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, false)]
    public async Task BuiltInMetric_TypedRetryPredicate_ReceivesOriginalProviderFailure(System.Net.HttpStatusCode status, bool retry)
    {
        var failure = new HttpRequestException("provider failure", null, status);
        var client = new TransientChatClient(failure);
        Exception? seen = null;
        var result = await new FaithfulnessMetric(client).EvaluateWithRetryAsync(Full, new RetryPolicy
        {
            MaxRetries = 1, InitialDelayMs = 0,
            ShouldRetry = ex =>
            {
                seen = ex;
                return ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests };
            }
        });
        Assert.Same(failure, seen);
        Assert.Equal(retry ? 2 : 1, client.CallCount);
        Assert.Equal(retry, result.Measured);
        if (retry) Assert.Equal(90, result.Score);
        // Failure internals must not leak into serialized result documents.
        Assert.DoesNotContain("StackTrace", System.Text.Json.JsonSerializer.Serialize(result));
    }

    private sealed class TransientChatClient(Exception failure) : IChatClient
    {
        public int CallCount { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (++CallCount == 1) throw failure;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"score":90,"reasoning":"supported"}""")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task TheMafCompositeEvaluator_GivesAThrownMetricNoValue()
    {
        var evaluator = new AgentEvalEvaluator([new ThrowingMetric(new InvalidOperationException("Metric crashed"))]);

        var result = await evaluator.EvaluateAsync(Conversation(), Reply());

        var metric = (NumericMetric)result.Metrics["throwing_metric"];
        Assert.Null(metric.Value);
        Assert.Equal(EvaluationRating.Inconclusive, metric.Interpretation!.Rating);
    }

    [Fact]
    public async Task TheMafMetricAdapter_ReadsAProviderSideTimeoutAsAFailedCall()
    {
        // It let every OperationCanceledException through, so an HttpClient timeout aborted the MAF evaluation.
        var adapter = new AgentEvalMetricAdapter(new ThrowingMetric(new TaskCanceledException("HttpClient.Timeout")));

        var result = await adapter.EvaluateAsync(Conversation(), Reply());

        Assert.Null(((NumericMetric)result.Metrics["throwing_metric"]).Value);
    }

    [Fact]
    public async Task TheMafMetricAdapter_StillPropagatesTheCallersCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var adapter = new AgentEvalMetricAdapter(new RelevanceMetric(new CancellationHonouringChatClient()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.EvaluateAsync(Conversation(), Reply(), cancellationToken: cts.Token).AsTask());
    }

    private static List<ChatMessage> Conversation() => [new(ChatRole.User, "What is the capital of France?")];

    private static ChatResponse Reply() => new([new ChatMessage(ChatRole.Assistant, "Paris.")]);

    /// <summary>A metric whose evaluation always throws <paramref name="error"/>.</summary>
    private sealed class ThrowingMetric(Exception error) : IMetric
    {
        public string Name => "throwing_metric";

        public string Description => "Always throws";

        public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
            => throw error;
    }

    /// <summary>A judge whose every call throws <paramref name="error"/>, counting the calls.</summary>
    private sealed class ThrowingChatClient(Exception error) : IChatClient
    {
        private int _calls;

        public int CallCount => _calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            throw error;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>A judge that honours its cancellation token and otherwise never answers.</summary>
    private sealed class CancellationHonouringChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
