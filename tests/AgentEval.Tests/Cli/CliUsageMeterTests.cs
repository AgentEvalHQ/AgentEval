// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Cli.Infrastructure;
using AgentEval.Results.Writing;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Cli;

public class CliUsageMeterTests
{
    private sealed class Client(bool report = true, bool failStream = false) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "private"))
            { Usage = report ? new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 } : null });
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate { ModelId = "reported-model", Contents = [new UsageContent(new() { InputTokenCount = 10, OutputTokenCount = 2 })] };
            await Task.Yield();
            if (failStream) throw new IOException("private failure");
        }
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }

    private sealed class Harness(IChatClient client) : AgentEval.Core.IEvaluationHarness
    {
        public async Task<AgentEval.Models.TestResult> RunEvaluationAsync(AgentEval.Core.IEvaluableAgent agent,
            AgentEval.Models.TestCase testCase, AgentEval.Core.EvaluationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            await client.GetResponseAsync("subject", cancellationToken: cancellationToken);
            using (CliUsageMeter.Attribute(AefUsageRole.Judge))
                await client.GetResponseAsync("judge", cancellationToken: cancellationToken);
            return new() { TestName = testCase.Name!, Passed = true, Score = 100 };
        }
    }

    [Fact]
    public async Task UsageHarness_ConcurrentTrials_AssociatesOnlyOwnCallsAndRestoresParent()
    {
        using var meter = CliUsageMeter.Begin();
        using var client = CliUsageMeter.Wrap(new Client(), "agent", "model");
        using var parent = CliUsageMeter.Attribute(caseId: "parent");
        var harness = new CliUsageEvaluationHarness(new Harness(client));
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => harness.RunEvaluationAsync(
            null!, new AgentEval.Models.TestCase { Name = "same", Input = "same" })));
        foreach (var result in results)
        {
            var usage = harness.TakeUsage(result)!;
            Assert.Equal(2, usage.Count);
            Assert.All(usage, u => Assert.Equal(10, u.InputTokens));
            Assert.Single(usage, u => u.Role == AefUsageRole.Agent);
            Assert.Single(usage, u => u.Role == AefUsageRole.Judge);
            Assert.Null(harness.TakeUsage(result));
        }
        Assert.Null(meter.Snapshot("parent"));
        await client.GetResponseAsync("after");
        Assert.Equal(10, Assert.Single(meter.Snapshot("parent")!).InputTokens);
    }

    [Fact]
    public async Task Attribute_ConcurrentReusedClient_IsolatesRolesAndCasesAndRestoresParent()
    {
        using var meter = CliUsageMeter.Begin();
        using var client = CliUsageMeter.Wrap(new Client(), "agent", "same-model");
        await Task.WhenAll(Enumerable.Range(0, 5).Select(async i =>
        {
            using var caseScope = CliUsageMeter.Attribute(caseId: "case-" + i);
            await client.GetResponseAsync("subject");
            using (CliUsageMeter.Attribute(AefUsageRole.Judge))
                await client.GetResponseAsync("judge");
            await client.GetResponseAsync("subject again");
        }));
        for (var i = 0; i < 5; i++)
        {
            var usage = meter.Snapshot("case-" + i)!;
            Assert.Equal(20, Assert.Single(usage, u => u.Role == AefUsageRole.Agent).InputTokens);
            Assert.Equal(10, Assert.Single(usage, u => u.Role == AefUsageRole.Judge).InputTokens);
        }
        Assert.Equal(100, Assert.Single(meter.Snapshot()!, u => u.Role == AefUsageRole.Agent).InputTokens);
        Assert.Equal(50, Assert.Single(meter.Snapshot()!, u => u.Role == AefUsageRole.Judge).InputTokens);
    }

    [Fact]
    public async Task Snapshot_ConcurrentRolesAndModels_SeparatesTotalsAndPricesKnownModel()
    {
        using var meter = CliUsageMeter.Begin();
        using var agent = CliChatClientDiagnostics.Wrap(new Client(), "sut", "gpt-4o-mini");
        using var judge = CliChatClientDiagnostics.Wrap(new Client(), "judge", "unknown-test-model");
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => agent.GetResponseAsync("q")));
        await judge.GetResponseAsync("q");
        var entries = meter.Snapshot()!;
        var a = Assert.Single(entries, u => u.Role == AefUsageRole.Agent);
        Assert.Equal(100, a.InputTokens); Assert.Equal(20, a.OutputTokens);
        Assert.NotNull(a.CostUsd); Assert.True(a.CostUsd > 0);
        var j = Assert.Single(entries, u => u.Role == AefUsageRole.Judge);
        Assert.Equal(10, j.InputTokens); Assert.Null(j.CostUsd);
    }

    [Fact]
    public async Task Snapshot_MissingReport_DoesNotExposePartialTotal()
    {
        using var meter = CliUsageMeter.Begin();
        using var reporting = CliUsageMeter.Wrap(new Client(), "judge", "same");
        using var silent = CliUsageMeter.Wrap(new Client(false), "judge", "same");
        await reporting.GetResponseAsync("q"); await silent.GetResponseAsync("q");
        var unknown = Assert.Single(meter.Snapshot()!);
        Assert.Null(unknown.InputTokens); Assert.Null(unknown.OutputTokens); Assert.Null(unknown.CostUsd);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Snapshot_Streaming_RequiresCompletedEnumeration(bool fail, bool stopEarly)
    {
        using var meter = CliUsageMeter.Begin();
        using var client = CliUsageMeter.Wrap(new Client(failStream: fail), "judge", "same");
        try { await foreach (var update in client.GetStreamingResponseAsync("q")) { if (stopEarly) break; } }
        catch (IOException) when (fail) { }
        Assert.Equal("reported-model", Assert.Single(meter.Snapshot()!).Model);
        if (fail || stopEarly)
        {
            var unknown = Assert.Single(meter.Snapshot()!);
            Assert.Null(unknown.InputTokens); Assert.Null(unknown.OutputTokens); Assert.Null(unknown.CostUsd);
        }
        else Assert.Equal(10, Assert.Single(meter.Snapshot()!).InputTokens);
    }

    [Fact]
    public async Task Begin_NestedScopes_RestoresOuterWithoutLeakingCalls()
    {
        using var outer = CliUsageMeter.Begin();
        using (var inner = CliUsageMeter.Begin())
        {
            using var client = CliUsageMeter.Wrap(new Client(), "judge", "inner");
            await client.GetResponseAsync("q");
            Assert.Single(inner.Snapshot()!);
        }
        Assert.Null(CliUsageMeter.CurrentUsage);
        using var outerClient = CliUsageMeter.Wrap(new Client(), "sut", "outer");
        await outerClient.GetResponseAsync("q");
        Assert.Equal("outer", Assert.Single(outer.Snapshot()!).Model);
    }
}
