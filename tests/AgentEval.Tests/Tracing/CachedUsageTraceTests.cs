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
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordSerializeReplay_CachedUsage_RetainsLosslessCounts(bool streaming)
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
        var replay = await new TraceReplayingAgent(trace).InvokeAsync("hello");
        Assert.Equal(int.MaxValue, replay.TokenUsage!.PromptTokens);
        Assert.Equal((long)int.MaxValue + 100, replay.TokenUsage.ReportedInputTokenCount);
        Assert.Equal(int.MaxValue, replay.TokenUsage.CachedInputTokenCount);
    }

    [Fact]
    public void TraceMapping_LegacyUsage_RemainsUnknownCache()
    {
        var usage = System.Text.Json.JsonSerializer.Deserialize<TraceTokenUsage>("{\"promptTokens\":10,\"completionTokens\":5}");
        Assert.Null(TraceMapping.ToAgentUsage(usage)!.CachedInputTokenCount);
    }
}
