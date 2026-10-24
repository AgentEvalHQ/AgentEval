// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF;

public sealed class StreamingUsageAccountingTests
{
    public static IEnumerable<object?[]> UsageCases()
    {
        (string Name, UsageDetails[] Contributions, int? Input, int? Output)[] cases =
        [
            ("additive", [Usage(10, 2), Usage(20, 3)], 30, 5),
            ("split", [Usage(100, null), Usage(null, 20)], 100, 20),
            ("single", [Usage(10, 5)], 10, 5),
            ("zero", [Usage(0, 0)], 0, 0),
            ("missing-input", [Usage(null, 20)], null, null),
            ("missing-output", [Usage(100, null)], null, null),
            ("no-usage", [], null, null),
            ("empty-after-valid", [Usage(10, 5), new UsageDetails()], 10, 5),
            ("total-after-valid", [Usage(10, 5), new UsageDetails { TotalTokenCount = 15 }], 10, 5),
            ("negative-input-then-valid", [Usage(-1, 2), Usage(10, 5)], null, null),
            ("negative-output-then-valid", [Usage(2, -1), Usage(10, 5)], null, null),
            ("input-overflow", [Usage(long.MaxValue, 0), Usage(1, 1)], null, null),
            ("output-overflow", [Usage(0, long.MaxValue), Usage(1, 1)], null, null),
            ("large-valid-input", [Usage((long)int.MaxValue + 10, 0)], int.MaxValue, 0),
        ];
        foreach (var maf in new[] { false, true })
        foreach (var item in cases)
            yield return [maf, item.Name, item.Contributions, item.Input, item.Output];
    }

    [Theory]
    [MemberData(nameof(UsageCases))]
    public async Task StreamingHarness_UsageContributions_PreservesObservedAccount(
        bool maf, string scenario, UsageDetails[] contributions, int? input, int? output)
    {
        var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationStreamingAsync(
            Adapter(maf, contributions), new TestCase { Name = scenario, Input = "Say hello." },
            options: new EvaluationOptions
            {
                EvaluateResponse = false, TrackPerformance = true, TrackTools = false,
                ModelName = "gpt-4o", Verbose = false,
            });

        Assert.Equal("Hello.tail", result.ActualOutput);
        var performance = Assert.IsType<PerformanceMetrics>(result.Performance);
        Assert.Equal(input is null, performance.TokensAreEstimated);
        if (input is not null)
        {
            Assert.Equal(input, performance.PromptTokens);
            Assert.Equal(output, performance.CompletionTokens);
            Assert.Equal((input.Value * 0.0025m + output!.Value * 0.01m) / 1000m,
                performance.EstimatedCost);
        }
        else
        {
            Assert.True(performance.PromptTokens > 0);
            Assert.True(performance.CompletionTokens > 0);
            Assert.NotNull(performance.EstimatedCost); // Explicitly labelled harness estimate.
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingAdapter_EarlyDisposalAfterUsage_DoesNotPublishCompleteAccount(bool maf)
    {
        var chunks = new List<AgentResponseChunk>();
        await foreach (var chunk in Adapter(maf, [Usage(10, 5)]).InvokeStreamingAsync("hello"))
        {
            chunks.Add(chunk);
            if (chunk.Text == "tail") break;
        }

        Assert.Equal("Hello.tail", string.Concat(chunks.Select(c => c.Text)));
        Assert.All(chunks, c => { Assert.False(c.IsComplete); Assert.Null(c.Usage); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingAdapter_CancellationAfterUsage_DoesNotPublishCompleteAccount(bool maf)
    {
        using var cancellation = new CancellationTokenSource();
        var chunks = new List<AgentResponseChunk>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in Adapter(maf, [Usage(10, 5)])
                .InvokeStreamingAsync("hello", cancellation.Token))
            {
                chunks.Add(chunk);
                if (chunk.Text == "tail") cancellation.Cancel();
            }
        });

        Assert.Equal("Hello.tail", string.Concat(chunks.Select(c => c.Text)));
        Assert.All(chunks, c => { Assert.False(c.IsComplete); Assert.Null(c.Usage); });
    }

    private static UsageDetails Usage(long? input, long? output) =>
        new() { InputTokenCount = input, OutputTokenCount = output };

    private static IStreamableAgent Adapter(bool maf, UsageDetails[] contributions) => maf
        ? new MAFAgentAdapter(new ContributionAgent(contributions))
        : new ChatClientAgentAdapter(new ContributionClient(contributions));

    private static async IAsyncEnumerable<ChatResponseUpdate> Updates(UsageDetails[] contributions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello.");
        foreach (var usage in contributions)
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new UsageContent(usage)] };
        yield return new ChatResponseUpdate(ChatRole.Assistant, "tail");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class ContributionClient(UsageDetails[] contributions) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Updates(contributions, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ContributionAgent(UsageDetails[] contributions) : AIAgent
    {
        public override string? Name => "usage contributions";
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
            new(new SimpleAgentSession());

        protected override Task<Microsoft.Agents.AI.AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages,
            AgentSession? session = null, AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in Updates(contributions, cancellationToken))
                yield return new AgentResponseUpdate { Role = update.Role, Contents = update.Contents };
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            new(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            new(new SimpleAgentSession());
    }
}
