// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Core;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;
using Xunit;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tests.Core;

/// <summary>
/// T63 / T13-8: usage is reported only when BOTH token counts were. A provider that sends just
/// <c>TotalTokenCount</c>, or one of the two counts (some OpenAI-compatible servers, streaming), used to be mapped as
/// <c>{prompt, 0}</c> / <c>{0, 0}</c>: the harness then marked the counts as real and priced the 0. A missing count
/// is not 0, so the usage is null and the harness's estimated path labels its own estimate.
/// </summary>
public sealed class TokenUsageMappingTests
{
    public static TheoryData<UsageDetails?> PartialUsage => new()
    {
        new UsageDetails { TotalTokenCount = 50 },
        new UsageDetails { InputTokenCount = 120 },
        new UsageDetails { OutputTokenCount = 30, TotalTokenCount = 30 },
    };

    [Theory]
    [MemberData(nameof(PartialUsage))]
    public async Task ChatClientAdapter_PartialUsage_IsNotReported(UsageDetails? usage)
    {
        var adapter = new ChatClientAgentAdapter(new UsageChatClient(usage), "subject");

        var response = await adapter.InvokeAsync("hi");

        Assert.Null(response.TokenUsage); // was {120, 0} / {0, 0}
    }

    [Theory]
    [MemberData(nameof(PartialUsage))]
    public async Task ChatClientAdapter_Streaming_PartialUsage_IsNotReported(UsageDetails? usage)
    {
        var adapter = new ChatClientAgentAdapter(new UsageChatClient(usage), "subject");

        AgentResponseChunk? last = null;
        await foreach (var chunk in adapter.InvokeStreamingAsync("hi"))
            last = chunk;

        Assert.NotNull(last);
        Assert.True(last!.IsComplete);
        Assert.Null(last.Usage);
    }

    [Fact]
    public async Task ChatClientAdapter_FullUsage_MapsBothCounts()
    {
        var usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 };
        var adapter = new ChatClientAgentAdapter(new UsageChatClient(usage), "subject");

        var response = await adapter.InvokeAsync("hi");
        AgentResponseChunk? last = null;
        await foreach (var chunk in adapter.InvokeStreamingAsync("hi"))
            last = chunk;

        Assert.Equal(10, response.TokenUsage!.PromptTokens);
        Assert.Equal(5, response.TokenUsage.CompletionTokens);
        Assert.Equal(10, last!.Usage!.PromptTokens);
        Assert.Equal(5, last.Usage.CompletionTokens);
    }

    [Theory]
    [MemberData(nameof(PartialUsage))]
    public void TraceMapping_PartialUsage_IsNotReported(UsageDetails? usage)
    {
        Assert.Null(TraceMapping.ToTokenUsage(usage));
    }

    [Fact]
    public void TraceMapping_FullUsage_MapsBothCounts()
    {
        var mapped = TraceMapping.ToTokenUsage(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 });

        Assert.Equal(10, mapped!.PromptTokens);
        Assert.Equal(5, mapped.CompletionTokens);
    }

    [Fact]
    public void FromUsage_ReportedZeros_AreARealZero()
    {
        // Guard: both counts reported as 0 is a measurement, not an absence.
        var mapped = TokenUsageMapping.FromUsage(new UsageDetails { InputTokenCount = 0, OutputTokenCount = 0 });

        Assert.NotNull(mapped);
        Assert.Equal(0, mapped!.TotalTokens);
    }

    /// <summary>Answers "ok" with the given usage, on the response and as a streamed <see cref="UsageContent"/>.</summary>
    internal sealed class UsageChatClient(UsageDetails? usage) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { Usage = usage, ModelId = "gpt-4o" });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok") { ModelId = "gpt-4o" };
            if (usage is not null)
                yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new UsageContent(usage)] };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
