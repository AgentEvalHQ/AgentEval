// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Engine;

public sealed class MemoryTokenBoundaryTests
{
    [Fact]
    public async Task Runner_TwoSaturatedJudgments_SaturatesWholeRunTotal()
    {
        var judge = new MemoryJudge(new LargeUsageClient(), NullLogger<MemoryJudge>.Instance);
        var query = MemoryQuery.Create("Where?", MemoryFact.Create("Madrid"));
        var one = await judge.JudgeAsync("Madrid", query);
        Assert.Equal(int.MaxValue, one.TokensUsed); // Existing per-judgment saturation is the guard.

        var runner = new MemoryTestRunner(judge, NullLogger<MemoryTestRunner>.Instance);
        var result = await runner.RunMemoryQueriesAsync(new EchoAgent(), [query, query], "large usage");
        Assert.All(result.QueryResults, q => Assert.Equal(int.MaxValue, q.TokensUsed));
        Assert.Equal(int.MaxValue, result.TokensUsed); // Existing int accumulation wraps to -2.
    }

    private sealed class EchoAgent : IEvaluableAgent
    {
        public string Name => "echo";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse { Text = "Madrid" });
    }

    private sealed class LargeUsageClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"score":90,"found_facts":[],"missing_facts":[],"forbidden_found":[],"explanation":"ok"}"""))
            {
                ModelId = "gpt-4o-mini",
                Usage = new UsageDetails { InputTokenCount = long.MaxValue, OutputTokenCount = long.MaxValue },
            });
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
