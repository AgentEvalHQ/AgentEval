// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using System.Runtime.CompilerServices;
using AgentEval.Benchmarks;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;
using Xunit;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tests.Tracing;

public sealed class StreamingTraceTokenBoundaryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recorder_NegativeChunkCanceledByPositiveChunk_DoesNotRecordFabricatedZero(bool input)
    {
        var response = await Record(Counts(input, -100), Counts(input, 100));
        Assert.Null(response.TokenUsage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recorder_PositiveChunksOverflowBackToZero_DoesNotRecordFabricatedZero(bool input)
    {
        // Unchecked long addition can wrap Max + Max + 2 back to zero, defeating a
        // validity check applied only after ToChatResponse has folded usage chunks.
        var response = await Record(Counts(input, long.MaxValue), Counts(input, long.MaxValue), Counts(input, 2));
        Assert.Null(response.TokenUsage);
    }

    [Fact]
    public async Task Recorder_ValidComponentsSplitAcrossChunks_RecordsCompleteUsage()
    {
        var response = await Record(new() { InputTokenCount = 100 }, new() { OutputTokenCount = 20 });
        Assert.NotNull(response.TokenUsage);
        Assert.Equal(100, response.TokenUsage!.PromptTokens);
        Assert.Equal(20, response.TokenUsage.CompletionTokens);
        Assert.Equal(120, response.TokenUsage.TotalTokens);
    }

    [Fact]
    public async Task Recorder_RecordedZeroChunks_KeepGenuineZero()
    {
        var response = await Record(new() { InputTokenCount = 0 }, new() { OutputTokenCount = 0 });
        Assert.NotNull(response.TokenUsage);
        Assert.Equal(0, response.TokenUsage!.TotalTokens);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 20)]
    public async Task Recorder_BreakAfterUsage_KeepsTextWithoutCertifyingCompleteUsage(long input, long output)
    {
        var trace = await RecordUntilUsage(input, output);
        var response = Assert.Single(trace.Entries, e => e.Type == TraceEntryType.Response);

        Assert.Equal("answer", response.Text);
        Assert.Equal("answer", Assert.Single(response.StreamingChunks!).Text);
        Assert.True(response.IsStreaming);
        Assert.Null(response.TokenUsage);
    }

    [Fact]
    public async Task AutoAudit_PersistedStreamBrokenAfterUsage_CannotCertifyAccounting()
    {
        var trace = await RecordUntilUsage(100, 20);
        var reloaded = await TraceSerializer.DeserializeFromStringAsync(await TraceSerializer.SerializeToStringAsync(trace));
        var result = AutoAuditRunner.Evaluate("partial", reloaded, reloaded);

        Assert.False(result.TokensReported);
        Assert.False(result.FidelityMeasured);
        Assert.Null(AutoAuditRunner.Compare([result]).Winner);
    }

    [Fact]
    public async Task TraceFidelity_PersistedStreamBrokenAfterUsage_WithholdsCleanVerdict()
    {
        var trace = await RecordUntilUsage(100, 20);
        var reloaded = await TraceSerializer.DeserializeFromStringAsync(await TraceSerializer.SerializeToStringAsync(trace));
        var result = new TraceFidelityRunner().ReconcileToEvalResult(reloaded, reloaded);

        Assert.False(result.Score.Passed);
        var tokens = Assert.Single(result.Details!.SubResults!, r => r.Metric.Key == "trace_fidelity.token_under_reporting");
        Assert.Equal("skipped", tokens.Score.Label);
    }

    [Fact]
    public async Task Recorder_CancellationAfterNaturalEof_KeepsCompleteUsage()
    {
        using var cancellation = new CancellationTokenSource();
        var trace = new AgentTrace();
        using var recorder = new TraceRecordingChatClient(new ChunkClient([new() { InputTokenCount = 100, OutputTokenCount = 20 }]), "subject", trace);
        await using (var stream = recorder.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "question")], cancellationToken: cancellation.Token).GetAsyncEnumerator())
        {
            while (await stream.MoveNextAsync()) { }
            cancellation.Cancel();
        }

        Assert.Equal(120, Assert.Single(trace.Entries, e => e.Type == TraceEntryType.Response).TokenUsage!.TotalTokens);
    }

    private static async Task<AgentTrace> RecordUntilUsage(long input, long output)
    {
        var trace = new AgentTrace();
        using var recorder = new TraceRecordingChatClient(new ChunkClient([new() { InputTokenCount = input, OutputTokenCount = output }]), "subject", trace);
        await foreach (var update in recorder.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "question")]))
        {
            if (update.Contents.OfType<UsageContent>().Any())
                break; // Even the last update is not evidence that the provider reached EOF.
        }
        return trace;
    }

    private static UsageDetails Counts(bool input, long count) => new()
    {
        InputTokenCount = input ? count : 0,
        OutputTokenCount = input ? 0 : count,
    };

    private static async Task<TraceEntry> Record(params UsageDetails[] counts)
    {
        var trace = new AgentTrace();
        var recorder = new TraceRecordingChatClient(new ChunkClient(counts), "subject", trace);
        await foreach (var _ in recorder.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "question")])) { }
        var response = Assert.Single(trace.Entries, e => e.Type == TraceEntryType.Response);
        Assert.True(response.IsStreaming);
        Assert.Equal("answer", response.Text);
        return response;
    }

    private sealed class ChunkClient(UsageDetails[] counts) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "answer");
            foreach (var usage in counts)
                yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new UsageContent(usage)] };
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
