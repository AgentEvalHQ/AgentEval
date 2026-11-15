// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Cli.Infrastructure;
using AgentEval.Results.Writing;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Cli;

public class CliUsageMeterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_LongPricingMetadata_SealsUsageWithoutDroppingDetailedRates(bool highPrecision)
    {
        var key = "long-rate-source-" + Guid.NewGuid().ToString("N");
        var rate = highPrecision ? 0.1234567890123456789012345678m : 0.001m;
        var pricing = new AgentEval.Models.TokenPricing(rate, rate, rate)
        { Source = "https://example.invalid/rate-card/" + new string('a', 1500) };
        AgentEval.Models.ModelPricing.SetPricing(key, pricing);
        using var meter = CliUsageMeter.Begin();
        using var client = CliUsageMeter.Wrap(new Client(), "agent", key);
        await client.GetResponseAsync("question");
        var usage = meter.Snapshot()!;
        var start = DateTimeOffset.UtcNow.AddSeconds(-2);
        var leaf = new AgentEval.Evals.EvalResult(new("cost-fixture", "Cost fixture", "test", "1.0.0"),
            new(1, null, "pass", true, null, "none", null), new(null, null, null, null, null),
            new("atomic-code", null, null, null, null, 0, false)
            { CostEstimate = AgentEval.Models.TokenCostEstimate.Calculate(10, 2, null, pricing) }, start);
        var root = Path.Combine(Path.GetTempPath(), "agenteval-cost-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (directory, _) = AgentEval.Results.Adapters.Native.AefCompositeRunWriter.Write(root, leaf,
                AgentEval.Results.Adapters.Native.AefCompositeRunWriter.Undescribed, new()
                {
                    SubjectRef = "agent:fixture", SuiteRef = "bench:fixture", Lane = "test",
                    TargetMode = AefTargetMode.Mocked, Usage = usage
                }, start, start.AddSeconds(1));
            Assert.Equal(AgentEval.Results.Integrity.AefOutcome.Intact,
                AgentEval.Results.Integrity.AefRunVerifier.Verify(directory).Outcome);
            Assert.InRange(Assert.Single(usage).CostSource!.Length, 1, 128);
            Assert.Contains(pricing.Source, File.ReadAllText(Path.Combine(directory, "results.ndjson")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_CachedProviderUsage_PreservesDiscountAndSubset(bool streaming)
    {
        using var meter = CliUsageMeter.Begin();
        using var client = CliUsageMeter.Wrap(new AgentEval.Tests.Core.TokenUsageMappingTests.UsageChatClient(new()
        {
            InputTokenCount = 10000, OutputTokenCount = 1000, CachedInputTokenCount = 8000
        }), "agent");
        if (streaming)
        {
            await foreach (var _ in client.GetStreamingResponseAsync("question")) { }
        }
        else await client.GetResponseAsync("question");
        var result = Assert.Single(meter.Snapshot()!);
        Assert.Equal(8000, result.CacheReadInputTokens);
        Assert.Equal(0.025, result.CostUsd!.Value, 10); // gpt-4o: 2k ordinary + 8k cached + 1k output
        Assert.Contains("CacheAdjusted", result.CostSource!);
        Assert.Contains("cached=0.00125", result.CostSource!);
    }

    [Fact]
    public async Task Snapshot_MixedCacheAssumptions_BoundsSummaryAndKeepsUnknownSubset()
    {
        using var meter = CliUsageMeter.Begin();
        foreach (long? cached in new long?[] { 8000, null })
        {
            using var client = CliUsageMeter.Wrap(new AgentEval.Tests.Core.TokenUsageMappingTests.UsageChatClient(new()
            { InputTokenCount = 10000, OutputTokenCount = 1000, CachedInputTokenCount = cached }), "agent");
            await client.GetResponseAsync("question");
        }
        var usage = Assert.Single(meter.Snapshot()!);
        Assert.Null(usage.CacheReadInputTokens);
        Assert.Equal(0.06, usage.CostUsd!.Value, 10);
        Assert.Contains("MixedCacheAssumptions", usage.CostSource!);
        Assert.InRange(usage.CostSource!.Length, 1, 128);
    }
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
