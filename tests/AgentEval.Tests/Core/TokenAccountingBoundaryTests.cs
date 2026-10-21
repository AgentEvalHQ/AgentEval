// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using System.Runtime.CompilerServices;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Models;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;
using Xunit;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tests.Core;

public sealed class TokenAccountingBoundaryTests
{
    [Theory]
    [InlineData(false, -1, 0)]
    [InlineData(false, 0, -1)]
    [InlineData(false, -1, 50)]
    [InlineData(false, 50, -1)]
    [InlineData(true, -1, 0)]
    [InlineData(true, 0, -1)]
    [InlineData(true, -1, 50)]
    [InlineData(true, 50, -1)]
    public async Task ChatAdapter_NegativeUsage_IsNotRecorded(bool streaming, long input, long output)
    {
        var client = new UsageClient(_ => new() { InputTokenCount = input, OutputTokenCount = output });
        var adapter = new ChatClientAgentAdapter(client, "subject");
        if (!streaming)
        {
            Assert.Null((await adapter.InvokeAsync("question")).TokenUsage);
        }
        else
        {
            AgentResponseChunk? last = null;
            await foreach (var chunk in adapter.InvokeStreamingAsync("question")) last = chunk;
            Assert.NotNull(last);
            Assert.Null(last!.Usage);
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(-1, 50)]
    [InlineData(50, -1)]
    public void TraceMapping_NegativeUsage_IsNotRecorded(long input, long output)
    {
        Assert.Null(TraceMapping.ToTokenUsage(new() { InputTokenCount = input, OutputTokenCount = output }));
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("performance")]
    [InlineData("trace")]
    public async Task ChatAdapter_LargeValidUsage_PreservesSaturatedTotalsAcrossConsumers(string consumer)
    {
        var source = new UsageDetails { InputTokenCount = long.MaxValue, OutputTokenCount = long.MaxValue };
        var adapter = new ChatClientAgentAdapter(new UsageClient(_ => source), "subject");
        var usage = (await adapter.InvokeAsync("question")).TokenUsage;
        Assert.NotNull(usage);
        Assert.Equal(int.MaxValue, usage!.PromptTokens);
        Assert.Equal(int.MaxValue, usage.CompletionTokens);
        if (consumer == "agent") Assert.Equal(int.MaxValue, usage.TotalTokens);
        else if (consumer == "performance")
        {
            // Performance capture copies these recorded components; the wider sum must not wrap.
            var performance = new PerformanceMetrics { PromptTokens = usage.PromptTokens, CompletionTokens = usage.CompletionTokens };
            Assert.Equal(int.MaxValue, performance.TotalTokens);
        }
        else
        {
            var trace = TraceMapping.ToTokenUsage(source);
            Assert.NotNull(trace);
            Assert.Equal(int.MaxValue, trace!.TotalTokens);
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(10, 5, 15)]
    public async Task ChatAdapter_ValidUsage_PreservesRecordedTotals(long input, long output, int expected)
    {
        var adapter = new ChatClientAgentAdapter(new UsageClient(_ => new() { InputTokenCount = input, OutputTokenCount = output }), "subject");
        Assert.Equal(expected, (await adapter.InvokeAsync("question")).TokenUsage!.TotalTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatAdapter_StreamingLatestUsageSnapshot_SupersedesEarlierSnapshot(bool invalidFirst)
    {
        // This adapter replaces snapshots; it does not accumulate the two UsageContent values.
        // Preserve that policy while validating each snapshot at the mapping boundary.
        var first = new UsageDetails { InputTokenCount = invalidFirst ? -100 : 10, OutputTokenCount = 0 };
        var final = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 };
        var adapter = new ChatClientAgentAdapter(new SnapshotClient([first, final]), "subject");
        AgentResponseChunk? last = null;
        await foreach (var chunk in adapter.InvokeStreamingAsync("question")) last = chunk;
        Assert.Equal(120, last!.Usage!.TotalTokens);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ChatRecorder_SaturatedRecordedUsage_SaturatesFinalPerformance(int calls)
    {
        var trace = new AgentTrace();
        var recorder = new TraceRecordingChatClient(new UsageClient(_ => new()
            { InputTokenCount = long.MaxValue, OutputTokenCount = long.MaxValue }), "subject", trace);
        for (var i = 0; i < calls; i++) await recorder.GetResponseAsync([new ChatMessage(ChatRole.User, "question")]);
        recorder.FinalizePerformance();
        Assert.NotNull(trace.Performance);
        Assert.Equal(int.MaxValue, trace.Performance!.TotalPromptTokens);
        Assert.Equal(int.MaxValue, trace.Performance.TotalCompletionTokens);
        Assert.Equal(int.MaxValue, trace.Performance.TotalTokens);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public async Task CorrectiveRetry_NegativeComponentOnEitherAttempt_CannotCancelIntoPricedTotal(int invalidAttempt, bool input)
    {
        var result = await Retry(call => new()
        {
            InputTokenCount = input ? (call == invalidAttempt ? -100 : 100) : 0,
            OutputTokenCount = input ? 0 : (call == invalidAttempt ? -100 : 100),
        });
        Assert.True(result.Score.Passed);
        AssertUnavailable(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CorrectiveRetry_NonnegativeComponentSumOverflows_IsNotPriced(bool input)
    {
        var result = await Retry(call => new()
        {
            InputTokenCount = input ? (call == 1 ? long.MaxValue : 1) : 0,
            OutputTokenCount = input ? 0 : (call == 1 ? long.MaxValue : 1),
        });
        Assert.True(result.Score.Passed);
        AssertUnavailable(result);
    }

    [Theory]
    [InlineData(false, -1, 0)]
    [InlineData(false, 0, -1)]
    [InlineData(false, -1, 50)]
    [InlineData(false, 50, -1)]
    [InlineData(true, -1, 0)]
    [InlineData(true, 0, -1)]
    [InlineData(true, -1, 50)]
    [InlineData(true, 50, -1)]
    public async Task AtomicEval_NegativeCounts_AreUnavailableWithEitherPricingPath(bool customRate, long input, long output)
    {
        var result = await Atomic(new TokenEvaluator(input, output), customRate).EvaluateAsync(new("q", Response: "a"));
        Assert.True(result.Score.Passed);
        AssertUnavailable(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicEval_ValidComponentsWhoseTotalExceedsLong_SaturatesLegacyIntTotal(bool customRate)
    {
        var result = await Atomic(new TokenEvaluator(long.MaxValue, 1), customRate).EvaluateAsync(new("q", Response: "a"));
        Assert.Equal(int.MaxValue, result.Provenance.TokensUsed);
        Assert.True(result.Provenance.EstimatedCost > 0);
        Assert.True(double.IsFinite(result.Provenance.EstimatedCost!.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicEval_RecordedZero_RemainsPricedZero(bool customRate)
    {
        var result = await Atomic(new TokenEvaluator(0, 0), customRate).EvaluateAsync(new("q", Response: "a"));
        Assert.Equal(0, result.Provenance.TokensUsed);
        Assert.Equal(0d, result.Provenance.EstimatedCost);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void JudgeCostMap_NegativeCount_IsNotPriced(long input, long output) =>
        Assert.Null(JudgeCostMap.EstimateCost("gpt-4o-mini", input, output));

    private static void AssertUnavailable(EvalResult result)
    {
        Assert.Null(result.Provenance.TokensUsed);
        Assert.Null(result.Provenance.EstimatedCost);
    }

    private static Task<EvalResult> Retry(Func<int, UsageDetails?> usage) =>
        Atomic(new ChatClientEvaluator(new UsageClient(usage, corrective: true)))
            .EvaluateAsync(new("q", Response: "a"));

    private static AtomicLlmEval Atomic(IEvaluator evaluator, bool customRate = false) => new(
        evaluator, "boundary", "Token boundary", "quality", "1.0.0", ["accuracy"],
        judgeModel: "gpt-4o-mini",
        rateResolver: customRate ? _ => new JudgeCostMap.ModelRate(0.001, 0.002) : null);

    private sealed class TokenEvaluator(long inputTokens, long outputTokens) : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default) =>
            Task.FromResult(new EvaluationResult { OverallScore = 90, InputTokenCount = inputTokens, OutputTokenCount = outputTokens });
    }

    private sealed class UsageClient(Func<int, UsageDetails?> usage, bool corrective = false) : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var call = ++_calls;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                corrective && call == 1 ? "no verdict" : """{"overallScore":90}"""))
                { Usage = usage(call), ModelId = "gpt-4o-mini" });
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var counts = usage(++_calls);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "answer")
            {
                Contents = counts is null ? [] : [new UsageContent(counts)],
                ModelId = "gpt-4o-mini",
            };
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SnapshotClient(UsageDetails[] snapshots) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var snapshot in snapshots)
                yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new UsageContent(snapshot)] };
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
