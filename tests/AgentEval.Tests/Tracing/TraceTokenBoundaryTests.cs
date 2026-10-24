// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;
using Xunit;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tests.Tracing;

public sealed class TraceTokenBoundaryTests
{
    [Fact]
    public async Task RecordingAgent_TwoMappedLargeResponses_SaturatesExportedPerformance()
    {
        var recorder = new TraceRecordingAgent(Agent(), "large trace");
        await recorder.InvokeAsync("first");
        await recorder.InvokeAsync("second");
        var json = await recorder.ToJsonAsync(); // Finalizes the real public export path.
        Assert.False(string.IsNullOrWhiteSpace(json));
        AssertSaturated(recorder.Trace.Performance!);
        await recorder.DisposeAsync();
    }

    [Fact]
    public async Task ChatRecorder_TwoMappedLargeResponses_SaturatesBothPublicResults()
    {
        await using var recorder = new ChatTraceRecorder(Agent(), "large conversation");
        await recorder.AddUserTurnAsync("first");
        await recorder.AddUserTurnAsync("second");
        var result = recorder.GetResult();
        Assert.Equal(int.MaxValue, result.AggregatePerformance!.PromptTokens);
        Assert.Equal(int.MaxValue, result.AggregatePerformance.CompletionTokens);
        Assert.Equal(int.MaxValue, result.AggregatePerformance.TotalTokens);
        AssertSaturated(recorder.ToAgentTrace().Performance!);
    }

    [Fact]
    public async Task GlassBoxEvidence_TwoMappedLargeResponses_SaturatesEachComponent()
    {
        var evidence = GlassBoxEvidence.FromTrace(await RecordedChat());
        Assert.NotNull(evidence);
        Assert.Equal(2, evidence!.ChatTurnCount);
        Assert.Equal(int.MaxValue, evidence.TotalPromptTokens);
        Assert.Equal(int.MaxValue, evidence.TotalCompletionTokens);
    }

    [Fact]
    public async Task AutoAudit_TwoMappedLargeResponses_SaturatesDisplayTotals()
    {
        var chat = await RecordedChat();
        var result = AutoAuditRunner.Evaluate("large", AgentBoundary(copies: 2), chat);
        Assert.False(result.TokensReported); // The displayed sum is capped, not an exact reported total.
        Assert.False(result.FidelityMeasured);
        Assert.Null(AutoAuditRunner.Compare([result]).Winner);
        Assert.Equal(int.MaxValue, result.PromptTokens);
        Assert.Equal(int.MaxValue, result.CompletionTokens);
        Assert.Equal(int.MaxValue, result.TotalTokens);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void Fidelity_PossiblyCappedComponentOnEitherBoundary_CannotProveFidelity(bool cappedChat, bool usePerformance)
    {
        var chatUsage = cappedChat ? LargeUsage() : Usage(100, 20);
        var agentUsage = cappedChat ? Usage(100, 20) : LargeUsage();
        var chat = ChatBoundary(chatUsage);
        var agent = AgentBoundary(agentUsage);
        if (usePerformance)
            agent.Performance = new TracePerformance { TotalPromptTokens = int.MaxValue, TotalCompletionTokens = int.MaxValue };

        var report = new TraceFidelityRunner().Reconcile(agent, chat);
        var tokens = report.Discrepancies.Single(d => d.ClassKey == "token_under_reporting");
        Assert.False(tokens.Measured);
    }

    [Fact]
    public void Fidelity_ExactMaximumComponent_HasNoMarkerToDistinguishItFromSaturation()
    {
        // Even an exactly represented maximum is ambiguous after mapping: the legacy model
        // contains no bit distinguishing this value from a larger value capped to it.
        var usage = Usage(int.MaxValue, 0);
        var tokens = new TraceFidelityRunner().Reconcile(AgentBoundary(usage), ChatBoundary(usage))
            .Discrepancies.Single(d => d.ClassKey == "token_under_reporting");
        Assert.False(tokens.Measured);
    }

    [Fact]
    public void Fidelity_UncappedComponentsWhoseAggregateExceedsInt_UsesWideComparison()
    {
        var usage = Usage(1_500_000_000, 0); // Each component is exact and below the ambiguous maximum.
        var tokens = new TraceFidelityRunner().Reconcile(AgentBoundary(usage), ChatBoundary(usage, usage))
            .Discrepancies.Single(d => d.ClassKey == "token_under_reporting");
        Assert.True(tokens.Measured);
        Assert.Equal(1, tokens.Count); // 3 billion vs 1.5 billion is a real 50% shortfall.
        Assert.True(tokens.Score < 1d);
    }

    [Fact]
    public void Fidelity_OrdinaryEqualCounts_RemainMeasuredAndClean()
    {
        var usage = Usage(100, 20);
        var tokens = new TraceFidelityRunner().Reconcile(AgentBoundary(usage), ChatBoundary(usage))
            .Discrepancies.Single(d => d.ClassKey == "token_under_reporting");
        Assert.True(tokens.Measured);
        Assert.Equal(0, tokens.Count);
        Assert.Equal(1d, tokens.Score);
    }

    private static void AssertSaturated(TracePerformance performance)
    {
        Assert.NotNull(performance);
        Assert.Equal(int.MaxValue, performance.TotalPromptTokens);
        Assert.Equal(int.MaxValue, performance.TotalCompletionTokens);
        Assert.Equal(int.MaxValue, performance.TotalTokens);
    }

    private static IEvaluableAgent Agent() => new ChatClientAgentAdapter(new LargeClient(), "subject");

    private static AgentTrace AgentBoundary(int copies)
    {
        var trace = new AgentTrace();
        for (var i = 0; i < copies; i++)
            trace.Entries.Add(new TraceEntry
            {
                Type = TraceEntryType.Response, Index = i,
                TokenUsage = TraceMapping.ToTokenUsage(LargeUsage()),
            });
        return trace;
    }

    private static AgentTrace AgentBoundary(UsageDetails usage)
    {
        var trace = new AgentTrace();
        trace.Entries.Add(new TraceEntry { Type = TraceEntryType.Response, Index = 0, TokenUsage = TraceMapping.ToTokenUsage(usage) });
        return trace;
    }

    private static AgentTrace ChatBoundary(params UsageDetails[] usages)
    {
        var trace = new AgentTrace();
        for (var i = 0; i < usages.Length; i++)
            trace.Entries.Add(TraceEntry.ForChatResponse(i, "inv", "ok", 1, TraceMapping.ToTokenUsage(usages[i]), null, null, null));
        return trace;
    }

    private static async Task<AgentTrace> RecordedChat()
    {
        var trace = new AgentTrace();
        var recorder = new TraceRecordingChatClient(new LargeClient(), "subject", trace);
        await recorder.GetResponseAsync([new ChatMessage(ChatRole.User, "first")]);
        await recorder.GetResponseAsync([new ChatMessage(ChatRole.User, "second")]);
        // Consumers under test each aggregate the recorded response entries themselves.
        return trace;
    }

    private static UsageDetails LargeUsage() => new() { InputTokenCount = long.MaxValue, OutputTokenCount = long.MaxValue };
    private static UsageDetails Usage(long input, long output) => new() { InputTokenCount = input, OutputTokenCount = output };

    private sealed class LargeClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
                { Usage = LargeUsage(), ModelId = "gpt-4o-mini" });
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
