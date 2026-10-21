// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using System.Runtime.CompilerServices;
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
