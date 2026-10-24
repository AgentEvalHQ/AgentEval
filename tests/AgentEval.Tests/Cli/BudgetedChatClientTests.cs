// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentEval.Cli.Runner;
using AgentEval.Results;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Cli;

public sealed class BudgetedChatClientTests
{
    private static BudgetedChatClient Wrap(ProbeClient inner, int calls = 1) =>
        new(inner, "priced-model", 4096, 100, calls, 1m, 2m);

    [Fact]
    public async Task GetResponseAsync_FrozenReasoning_IsCopiedAndConflictingOverrideRefusedBeforeIo()
    {
        var inner = new ProbeClient();
        using var client = new BudgetedChatClient(inner, "priced-model", 4096, 100, 2, 1m, 2m,
            freezeReasoning: true, reasoningEffort: ReasoningEffort.Low);
        var options = new ChatOptions();
        await client.GetResponseAsync("hello", options);
        Assert.Null(options.Reasoning);
        Assert.Equal(ReasoningEffort.Low, inner.Options!.Reasoning!.Effort);
        Assert.Equal(ReasoningEffort.Low, client.LastReasoningEffort);
        await Assert.ThrowsAsync<AefInputException>(() => client.GetResponseAsync("changed",
            new ChatOptions { Reasoning = new() { Effort = ReasoningEffort.High } }));
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task GetResponseAsync_CompleteUsage_ChargesActualAndClampsCopiedOptions()
    {
        var inner = new ProbeClient();
        using var client = Wrap(inner);
        var options = new ChatOptions { MaxOutputTokens = 900 };

        await client.GetResponseAsync("hello", options);

        Assert.Equal(900, options.MaxOutputTokens);
        Assert.Equal(100, inner.Options!.MaxOutputTokens);
        Assert.Equal("priced-model", inner.Options.ModelId);
        Assert.Equal(0.00002m, client.IncurredUsd); // 10 input + 5 output at $1 / $2 per million.
        Assert.Equal(10, client.InputTokens);
        Assert.Equal(5, client.OutputTokens);
    }

    [Fact]
    public async Task GetResponseAsync_CallCap_RefusesBeforeSecondInvocation()
    {
        var inner = new ProbeClient();
        using var client = Wrap(inner);
        await client.GetResponseAsync("first");

        await Assert.ThrowsAsync<AefInputException>(() => client.GetResponseAsync("second"));

        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("system")]
    [InlineData("assistant")]
    public async Task GetResponseAsync_AnyOversizeRole_RefusesBeforeIo(string role)
    {
        var inner = new ProbeClient();
        using var client = Wrap(inner);
        await Assert.ThrowsAsync<AefInputException>(() => client.GetResponseAsync(
            [new ChatMessage(new ChatRole(role), new string('界', 2000)), new ChatMessage(ChatRole.User, "short")]));
        Assert.Equal(0, inner.Calls);
        Assert.Equal(0, client.IncurredUsd);
    }

    [Fact]
    public async Task GetResponseAsync_OversizeInstructions_RefusesBeforeIo()
    {
        var inner = new ProbeClient();
        using var client = Wrap(inner);
        await Assert.ThrowsAsync<AefInputException>(() => client.GetResponseAsync("short",
            new ChatOptions { Instructions = new string('x', 5000) }));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task GetResponseAsync_StructuredOutputSchema_CountsTowardsInputCap()
    {
        var inner = new ProbeClient();
        using var client = Wrap(inner);
        var schema = JsonSerializer.SerializeToElement(new { type = "object", description = new string('x', 5000) });
        await Assert.ThrowsAsync<AefInputException>(() => client.GetResponseAsync("short",
            new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema) }));
        Assert.Equal(0, inner.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetResponseAsync_MissingOrPartialUsage_KeepsBoundAndUnknownTotals(bool partial)
    {
        var inner = new ProbeClient { Usage = partial ? new UsageDetails { InputTokenCount = 10 } : null };
        using var client = Wrap(inner);
        await client.GetResponseAsync("hello");
        Assert.Equal(client.UsdBound, client.IncurredUsd);
        Assert.Null(client.InputTokens);
        Assert.Null(client.OutputTokens);
    }

    [Fact]
    public async Task GetResponseAsync_TransportFailure_KeepsReservation()
    {
        var inner = new ProbeClient { Failure = new IOException("offline test failure") };
        using var client = Wrap(inner);
        await Assert.ThrowsAsync<IOException>(() => client.GetResponseAsync("hello"));
        Assert.Equal(client.UsdBound, client.IncurredUsd);
        Assert.Null(client.InputTokens);
    }

    [Fact]
    public async Task GetResponseAsync_AlreadyCancelled_DoesNotReserveOrInvoke()
    {
        var inner = new ProbeClient();
        using var client = Wrap(inner);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync("hello",
            cancellationToken: new CancellationToken(true)));
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, client.IncurredUsd);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task GetResponseAsync_ProviderOverrun_RetainsActualSpendAndPreventsMoreCalls()
    {
        var inner = new ProbeClient { Usage = new UsageDetails { InputTokenCount = 9000, OutputTokenCount = 200 } };
        using var client = Wrap(inner, calls: 2);
        await Assert.ThrowsAsync<IOException>(() => client.GetResponseAsync("hello"));
        Assert.True(client.BoundExceeded);
        Assert.Equal(0.0094m, client.IncurredUsd);
        await Assert.ThrowsAsync<IOException>(() => client.GetResponseAsync("again"));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task GetResponseAsync_ParallelCalls_AdmitsOnlyOne()
    {
        var inner = new ProbeClient();
        using var client = Wrap(inner);
        var errors = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
            await Record.ExceptionAsync(() => client.GetResponseAsync("hello")))));
        Assert.Single(errors, e => e is null);
        Assert.Equal(9, errors.Count(e => e is AefInputException));
        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetStreamingResponseAsync_CompletionVersusAbandonment_AccountsConservatively(bool finish)
    {
        var inner = new ProbeClient();
        using var client = Wrap(inner);
        await using (var stream = client.GetStreamingResponseAsync("hello").GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            if (finish) while (await stream.MoveNextAsync()) { }
        }
        Assert.Equal(finish ? 0.00002m : client.UsdBound, client.IncurredUsd);
        Assert.Equal(finish ? 10L : (long?)null, client.InputTokens);
        Assert.True(inner.StreamDisposed);
    }

    [Fact]
    public async Task GetResponseAsync_HiddenContextOrModelOverride_RefusesBeforeIo()
    {
        var inner = new ProbeClient();
        using var client = Wrap(inner);
        await Assert.ThrowsAsync<AefInputException>(() => client.GetResponseAsync("hello", new ChatOptions { ConversationId = "hidden" }));
        await Assert.ThrowsAsync<AefInputException>(() => client.GetResponseAsync("hello", new ChatOptions { ModelId = "unpriced" }));
        await Assert.ThrowsAsync<AefInputException>(() => client.GetResponseAsync("hello", new ChatOptions { AllowBackgroundResponses = true }));
        await Assert.ThrowsAsync<AefInputException>(() => client.GetResponseAsync("hello", new ChatOptions { AdditionalProperties = new() { ["n"] = 10 } }));
        Assert.Equal(0, inner.Calls);
        Assert.Same(client, client.GetService(typeof(IChatClient)));
        Assert.Null(client.GetService(typeof(ProbeClient)));
    }

    [Fact]
    public async Task GetStreamingResponseAsync_UsageOverrun_StopsImmediatelyAndKeepsEvidenceOfViolation()
    {
        var inner = new ProbeClient { Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 1000 } };
        using var client = Wrap(inner);
        await using var stream = client.GetStreamingResponseAsync("hello").GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        await Assert.ThrowsAsync<IOException>(async () => await stream.MoveNextAsync());
        Assert.True(client.BoundExceeded);
        Assert.Equal(0.00201m, client.IncurredUsd);
        Assert.True(inner.StreamDisposed);
    }

    private sealed class ProbeClient : IChatClient
    {
        public UsageDetails? Usage { get; init; } = new() { InputTokenCount = 10, OutputTokenCount = 5 };
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public ChatOptions? Options { get; private set; }
        public bool StreamDisposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Options = options;
            return Failure is not null ? Task.FromException<ChatResponse>(Failure)
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply")) { Usage = Usage });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            try
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "reply");
                if (Usage is not null) yield return new ChatResponseUpdate { Contents = [new UsageContent(Usage)] };
            }
            finally { StreamDisposed = true; }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(ProbeClient) ? this : null;
        public void Dispose() { }
    }
}
