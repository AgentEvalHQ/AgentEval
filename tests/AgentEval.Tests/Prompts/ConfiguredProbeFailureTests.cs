// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Prompts;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace AgentEval.Tests.Prompts;

public sealed class ConfiguredProbeFailureTests
{
    private const string Resisted = """{"verdict":"RESISTED","confidence":0.9,"reason":"refusal"}""";
    private const string Abstained = """{"verdict":"INCONCLUSIVE","confidence":0.9,"reason":"uncertain"}""";
    private static AttackProbe Probe => new() { Id = "failed-judge", Prompt = "private input", Difficulty = Difficulty.Easy };
    private static LLMJudgeEvaluator Leaf(IChatClient client, string key) => new(client, new() { PromptKey = key });
    private static IProbeEvaluator Bind(IProbeEvaluator graph) => RedTeamJudgePrompts.Bind(graph, new()
    {
        Locale = "sv-SE",
        Pack = new(RedTeamJudgePrompts.ForEvaluator(graph).Select(d => d with { Locale = "sv-SE" }))
    });

    [Theory]
    [InlineData("filter")]
    [InlineData("conjunction")]
    [InlineData("composite")]
    [InlineData("fidelity")]
    public async Task Aggregate_NestedUnreadableMeasurement_RemainsInconclusive(string wrapper)
    {
        using var unreadable = new FakeChatClient("private invalid output", "private invalid output");
        using var safe = new FakeChatClient(Resisted);
        IProbeEvaluator failed = wrapper switch
        {
            "filter" => new OutcomeFilterEvaluator(Leaf(unreadable, "redteam.failed"), EvaluationOutcome.Succeeded),
            "conjunction" => new ConjunctionGate("check", EvaluationOutcome.Succeeded, Leaf(unreadable, "redteam.failed")),
            "composite" => new CompositeEvaluator(Leaf(unreadable, "redteam.failed")),
            _ => Leaf(unreadable, "redteam.failed")
        };
        IProbeEvaluator graph = wrapper == "fidelity"
            ? new FidelityCompositeEvaluator(failed, Leaf(safe, "redteam.safe"))
            : new CompositeEvaluator(failed, Leaf(safe, "redteam.safe"));
        var result = await Bind(graph).EvaluateAsync(Probe, new AgentResponse { Text = "private answer" });
        Assert.True(result.IsInconclusive);
        Assert.Equal(2, result.JudgeInstruments!.Count);
        Assert.Null(result.RubricDigest);
        Assert.Null(result.GradedBy);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("MeasurementFailed", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Aggregate_ProviderFailure_RetainsFailedAttemptWithoutSafetyClaim()
    {
        using var failed = new CallbackClient(_ => throw new HttpRequestException("private provider detail"));
        using var safe = new FakeChatClient(Resisted);
        var result = await Bind(new CompositeEvaluator(Leaf(failed, "redteam.failed"), Leaf(safe, "redteam.safe")))
            .EvaluateAsync(Probe, "private answer");
        Assert.True(result.IsInconclusive);
        Assert.False(Assert.Single(result.JudgeInstruments![0].Calls).ResponseReceived);
        Assert.True(Assert.Single(result.JudgeInstruments[1].Calls).ResponseReceived);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(CompositeEvaluator.AggregationStrategy.Any)]
    [InlineData(CompositeEvaluator.AggregationStrategy.All)]
    [InlineData(CompositeEvaluator.AggregationStrategy.Majority)]
    public async Task Aggregate_ValidAbstention_PreservesExistingStrategy(CompositeEvaluator.AggregationStrategy strategy)
    {
        using var abstained = new FakeChatClient(Abstained);
        using var safe = new FakeChatClient(Resisted);
        var result = await Bind(new CompositeEvaluator(strategy, Leaf(abstained, "redteam.abstained"), Leaf(safe, "redteam.safe")))
            .EvaluateAsync(Probe, "answer");
        Assert.True(result.AttackResisted);
        Assert.Equal(2, result.JudgeInstruments!.Count);
    }

    [Fact]
    public async Task Aggregate_NestedTimeout_DoesNotBecomeASafetyPass()
    {
        using var slow = new CallbackClient(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, Resisted));
        });
        using var safe = new FakeChatClient(Resisted);
        var graph = new CompositeEvaluator(new TimeoutEvaluator(Leaf(slow, "redteam.slow"), TimeSpan.FromMilliseconds(100)),
            Leaf(safe, "redteam.safe"));
        Assert.True((await Bind(graph).EvaluateAsync(Probe, "answer")).IsInconclusive);
    }

    [Fact]
    public async Task Aggregate_CallerCancellation_IsNotConvertedToAResult()
    {
        using var cancelled = new CancellationTokenSource();
        using var client = new CallbackClient(ct => { cancelled.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException(); });
        var graph = new TimeoutEvaluator(Leaf(client, "redteam.cancelled"), TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bind(graph).EvaluateAsync(Probe, "answer", cancelled.Token));
    }

    private sealed class CallbackClient(Func<CancellationToken, Task<ChatResponse>> reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => reply(cancellationToken);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
