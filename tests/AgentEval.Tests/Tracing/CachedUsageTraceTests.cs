// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Tests.Core;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Tracing;

public sealed class CachedUsageTraceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RecordSerializeReplay_CachedUsage_RetainsLosslessCounts(bool streaming, bool streamingReplay)
    {
        var usage = new UsageDetails
        {
            InputTokenCount = (long)int.MaxValue + 100, OutputTokenCount = 10, CachedInputTokenCount = int.MaxValue
        };
        await using var recorder = new TraceRecordingAgent(
            new ChatClientAgentAdapter(new TokenUsageMappingTests.UsageChatClient(usage)), "cache");
        if (streaming)
        {
            await foreach (var _ in recorder.InvokeStreamingAsync("hello")) { }
        }
        else await recorder.InvokeAsync("hello");
        var json = await TraceSerializer.SerializeToStringAsync(recorder.Trace);
        var trace = await TraceSerializer.DeserializeFromStringAsync(json);
        var replayer = new TraceReplayingAgent(trace);
        TokenUsage? replayUsage = null;
        if (streamingReplay)
        {
            await foreach (var chunk in replayer.InvokeStreamingAsync("hello"))
            {
                if (chunk.IsComplete) replayUsage = chunk.Usage;
                else Assert.Null(chunk.Usage);
            }
        }
        else replayUsage = (await replayer.InvokeAsync("hello")).TokenUsage;
        Assert.Equal(int.MaxValue, replayUsage!.PromptTokens);
        Assert.Equal((long)int.MaxValue + 100, replayUsage.ReportedInputTokenCount);
        Assert.Equal(int.MaxValue, replayUsage.CachedInputTokenCount);
    }

    [Theory]
    [InlineData("answer", false)]
    [InlineData("", false)]
    [InlineData("answer", true)]
    public async Task RecordSerializeReplay_MixedResponse_RetainsAmbiguity(string text, bool legacyUsageFlag)
    {
        var agent = new MixedAgent(text, legacyUsageFlag);
        await using var recorder = new TraceRecordingAgent(agent, "mixed");
        await recorder.InvokeAsync("hello");
        await AssertReplay(recorder.Trace);

        await using var chatRecorder = new ChatTraceRecorder(agent);
        await chatRecorder.AddUserTurnAsync("hello");
        await AssertReplay(chatRecorder.ToAgentTrace());

        async Task AssertReplay(AgentTrace recorded)
        {
            var trace = await TraceSerializer.DeserializeFromStringAsync(await TraceSerializer.SerializeToStringAsync(recorded));
            var replayer = new TraceReplayingAgent(trace);
            var response = await replayer.InvokeAsync("hello");
            Assert.True(response.HasMixedModelUsage);
            Assert.Equal(legacyUsageFlag, response.TokenUsage is not null);
            replayer.Reset();
            var completed = 0;
            await foreach (var chunk in replayer.InvokeStreamingAsync("hello"))
            {
                Assert.Equal(chunk.IsComplete, chunk.HasMixedModelUsage);
                if (chunk.IsComplete)
                {
                    completed++;
                    Assert.Equal(legacyUsageFlag, chunk.Usage is not null);
                }
            }
            Assert.Equal(1, completed);
        }
    }

    private sealed class MixedAgent(string text, bool legacyUsageFlag) : IEvaluableAgent
    {
        public string Name => "mixed";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse
            {
                Text = text, HasMixedModelUsage = !legacyUsageFlag,
                TokenUsage = legacyUsageFlag ? new() { PromptTokens = 10, CompletionTokens = 5, HasMixedModelUsage = true } : null
            });
    }

    [Fact]
    public void TraceMapping_LegacyUsage_RemainsUnknownCache()
    {
        var usage = System.Text.Json.JsonSerializer.Deserialize<TraceTokenUsage>("{\"promptTokens\":10,\"completionTokens\":5}");
        Assert.Null(TraceMapping.ToAgentUsage(usage)!.CachedInputTokenCount);
    }
}
