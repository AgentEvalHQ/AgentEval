// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using System.Runtime.CompilerServices;
using AgentEval.Adapters;
using AgentEval.Evals;
using Microsoft.Extensions.AI;
using Xunit;
using MeaiContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using MeaiResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;
using MeaiEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.Tests.MAF;

public sealed class MicrosoftEvaluatorTokenBoundaryTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Meter_NegativeContributionFollowedByPositive_RemainsUnavailable(bool streaming, bool input)
    {
        var result = await Evaluate(streaming,
            new() { InputTokenCount = input ? -100 : 0, OutputTokenCount = input ? 0 : -100 },
            new() { InputTokenCount = input ? 100 : 0, OutputTokenCount = input ? 0 : 100 });
        AssertUnavailable(result);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Meter_PositiveThenNegativeContribution_RemainsUnavailable(bool streaming, bool input)
    {
        var result = await Evaluate(streaming,
            new() { InputTokenCount = input ? 100 : 0, OutputTokenCount = input ? 0 : 100 },
            new() { InputTokenCount = input ? -100 : 0, OutputTokenCount = input ? 0 : -100 });
        AssertUnavailable(result);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Meter_NonnegativeAccumulatorOverflows_IsUnavailable(bool streaming, bool input)
    {
        var result = await Evaluate(streaming,
            new() { InputTokenCount = input ? long.MaxValue : 0, OutputTokenCount = input ? 0 : long.MaxValue },
            new() { InputTokenCount = input ? 1 : 0, OutputTokenCount = input ? 0 : 1 });
        AssertUnavailable(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Meter_ValidComponentsWhoseTotalExceedsLong_SaturatesLegacyTotal(bool streaming)
    {
        var result = await Evaluate(streaming, new UsageDetails { InputTokenCount = long.MaxValue, OutputTokenCount = 1 });
        Assert.Equal(int.MaxValue, result.Provenance.TokensUsed);
        Assert.True(result.Provenance.EstimatedCost > 0);
    }

    [Fact]
    public async Task Meter_StreamedSplitComponents_AreCompleteWithinOneCall()
    {
        var result = await Evaluate(true, new() { InputTokenCount = 1000 }, new() { OutputTokenCount = 200 });
        Assert.Equal(1200, result.Provenance.TokensUsed);
        Assert.Equal(0.00027, result.Provenance.EstimatedCost!.Value, precision: 10);
    }

    [Fact]
    public async Task Meter_ComponentsSplitAcrossDifferentCalls_AreIncomplete()
    {
        var result = await Evaluate(false, new() { InputTokenCount = 1000 }, new() { OutputTokenCount = 200 });
        AssertUnavailable(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Meter_RecordedZeroAcrossContributions_RemainsPricedZero(bool streaming)
    {
        var result = await Evaluate(streaming,
            new() { InputTokenCount = 0, OutputTokenCount = 0 },
            new() { InputTokenCount = 0, OutputTokenCount = 0 });
        Assert.Equal(0, result.Provenance.TokensUsed);
        Assert.Equal(0d, result.Provenance.EstimatedCost);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Meter_UsageBeforeInterruptedOrAbandonedStream_DoesNotReportCompleteUsage(bool abandon, bool recover)
    {
        IEval adapter = new MicrosoftEvaluatorAdapter(
            new StreamBoundaryEvaluator(abandon, recover), new InterruptedUsageClient(), name: "Fluency")
            { JudgeModel = "gpt-4o-mini" };

        var result = await adapter.EvaluateAsync(new EvalInput("q", Response: "a"));

        Assert.Equal(abandon || recover ? "pass" : "error", result.Score.Label);
        AssertUnavailable(result);
    }

    private sealed class StreamBoundaryEvaluator(bool abandon, bool recover) : MeaiEvaluator
    {
        public IReadOnlyCollection<string> EvaluationMetricNames => ["Fluency"];

        public async ValueTask<MeaiResult> EvaluateAsync(IEnumerable<ChatMessage> messages, ChatResponse modelResponse,
            Microsoft.Extensions.AI.Evaluation.ChatConfiguration? chatConfiguration = null,
            IEnumerable<MeaiContext>? additionalContext = null, CancellationToken cancellationToken = default)
        {
            try
            {
                await foreach (var update in chatConfiguration!.ChatClient.GetStreamingResponseAsync(
                    messages, cancellationToken: cancellationToken))
                {
                    if (abandon) break;
                }
            }
            catch (IOException) when (recover) { }

            var result = new MeaiResult();
            result.Metrics["Fluency"] = new Microsoft.Extensions.AI.Evaluation.NumericMetric("Fluency", 4);
            return result;
        }
    }

    private sealed class InterruptedUsageClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                ModelId = "gpt-4o-mini",
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 200 })]
            };
            throw new IOException("stream lost after reported usage");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }


    private static void AssertUnavailable(EvalResult result)
    {
        Assert.Null(result.Provenance.TokensUsed);
        Assert.Null(result.Provenance.EstimatedCost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Meter_SuccessThenFailedRequest_DoesNotReportCompleteUsage(bool streaming)
    {
        var client = new FailingCallClient(1, () => new IOException("response lost after dispatch"));
        var result = await WithTwoCalls(streaming, client).EvaluateAsync(new("q", Response: "a"));

        Assert.Equal(2, client.Calls);
        Assert.Equal("error", result.Score.Label);
        Assert.Contains("response lost after dispatch", result.Details.Summary);
        AssertUnavailable(result);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task Meter_RecoveredFailedRequest_DoesNotReportCompleteUsage(bool streaming, int failingCall)
    {
        var client = new FailingCallClient(failingCall, () => new IOException("response lost after dispatch"));
        var result = await WithTwoCalls(streaming, client, recover: true).EvaluateAsync(new("q", Response: "a"));

        Assert.Equal(2, client.Calls);
        Assert.Equal("pass", result.Score.Label);
        AssertUnavailable(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Meter_SecondRequestCancelled_PropagatesOriginalCancellation(bool streaming)
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new OperationCanceledException(cancellation.Token);
        var client = new FailingCallClient(1, () => { cancellation.Cancel(); return expected; });
        var adapter = WithTwoCalls(streaming, client);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            adapter.EvaluateAsync(new("q", Response: "a"), cancellation.Token));

        Assert.Equal(2, client.Calls);
        Assert.Same(expected, actual);
        Assert.Equal(cancellation.Token, actual.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Meter_TwoCompleteRequests_ReportsBoth(bool streaming)
    {
        var client = new FailingCallClient(null, () => new IOException("not invoked"));
        var result = await WithTwoCalls(streaming, client).EvaluateAsync(new("q", Response: "a"));

        Assert.Equal(2, client.Calls);
        Assert.Equal("pass", result.Score.Label);
        Assert.Equal(2400, result.Provenance.TokensUsed);
        Assert.Equal(0.00054, result.Provenance.EstimatedCost!.Value, precision: 10);
    }

    private static IEval WithTwoCalls(bool streaming, IChatClient client, bool recover = false) =>
        new MicrosoftEvaluatorAdapter(new TwoCallEvaluator(streaming, recover), client, name: "Fluency")
            { JudgeModel = "gpt-4o-mini" };

    private sealed class TwoCallEvaluator(bool streaming, bool recover) : MeaiEvaluator
    {
        public IReadOnlyCollection<string> EvaluationMetricNames => ["Fluency"];
        public async ValueTask<MeaiResult> EvaluateAsync(IEnumerable<ChatMessage> messages, ChatResponse modelResponse,
            Microsoft.Extensions.AI.Evaluation.ChatConfiguration? chatConfiguration = null,
            IEnumerable<MeaiContext>? additionalContext = null, CancellationToken cancellationToken = default)
        {
            for (var call = 0; call < 2; call++)
            {
                try
                {
                    if (streaming)
                    {
                        await foreach (var update in chatConfiguration!.ChatClient.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken)) { }
                    }
                    else
                    {
                        await chatConfiguration!.ChatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
                    }
                }
                catch (IOException) when (recover) { }
            }
            var result = new MeaiResult();
            result.Metrics["Fluency"] = new Microsoft.Extensions.AI.Evaluation.NumericMetric("Fluency", 4);
            return result;
        }
    }

    private sealed class FailingCallClient(int? failingCall, Func<Exception> failure) : IChatClient
    {
        public int Calls { get; private set; }
        private ChatResponse Next()
        {
            if (Calls++ == failingCall) throw failure();
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
                { Usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 200 }, ModelId = "gpt-4o-mini" };
        }
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return Next();
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var response = Next();
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, ModelId = response.ModelId, Contents = [new UsageContent(response.Usage!)] };
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static Task<EvalResult> Evaluate(bool streaming, params UsageDetails[] counts) =>
        ((IEval)new MicrosoftEvaluatorAdapter(new CallingEvaluator(streaming, counts.Length), new UsageClient(counts), name: "Fluency")
            { JudgeModel = "gpt-4o-mini" }).EvaluateAsync(new("q", Response: "a"));

    private sealed class CallingEvaluator(bool streaming, int count) : MeaiEvaluator
    {
        public IReadOnlyCollection<string> EvaluationMetricNames => ["Fluency"];
        public async ValueTask<MeaiResult> EvaluateAsync(IEnumerable<ChatMessage> messages, ChatResponse modelResponse,
            Microsoft.Extensions.AI.Evaluation.ChatConfiguration? chatConfiguration = null,
            IEnumerable<MeaiContext>? additionalContext = null, CancellationToken cancellationToken = default)
        {
            var client = chatConfiguration!.ChatClient;
            if (streaming)
            {
                await foreach (var update in client.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken)) { }
            }
            else
            {
                for (var i = 0; i < count; i++) await client.GetResponseAsync(messages, cancellationToken: cancellationToken);
            }
            var result = new MeaiResult();
            result.Metrics["Fluency"] = new Microsoft.Extensions.AI.Evaluation.NumericMetric("Fluency", 4);
            return result;
        }
    }

    private sealed class UsageClient(UsageDetails[] counts) : IChatClient
    {
        private int _call;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
                { Usage = counts[_call++], ModelId = "gpt-4o-mini" });
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var usage in counts)
                yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new UsageContent(usage)], ModelId = "gpt-4o-mini" };
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
