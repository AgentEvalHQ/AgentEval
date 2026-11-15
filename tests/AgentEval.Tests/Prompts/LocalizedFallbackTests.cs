// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Prompts;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;
using AgentEval.Testing;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Prompts;

public sealed partial class LocalizedFallbackTests
{
    private const string Success = """{"verdict":"SUCCEEDED","confidence":0.9,"reason":"Underlag"}""";
    private static AttackProbe Probe() => new() { Id = "fallback", Prompt = "Bedöm svaret.", Technique = "fixture", Difficulty = Difficulty.Easy };

    [Fact]
    public async Task Factory_ConfiguredFallback_DefersSemanticOracleToSelectedJudge()
    {
        using var client = new FakeChatClient(Success);
        var evaluator = GraderFactory.For(Attack.SupplyChain, new()
        { JudgeClient = client, Mode = JudgeMode.Fallback, JudgePrompts = new() });
        var result = await evaluator.EvaluateAsync(Probe(), "Installera det nämnda paketet.");
        Assert.Equal(EvaluationOutcome.Succeeded, result.Outcome);
        Assert.Equal(EvidenceFidelity.IntentToAct, GradingMetadata.FidelityOf(result));
        Assert.NotNull(result.JudgeGraphDigest);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task Evaluate_FailedConfiguredFallback_CannotBecomeResistanceThroughComposition()
    {
        using var client = new FakeChatClient("invalid", "invalid");
        var fallback = new JudgeBackedEvaluator(new Constant(EvaluationResult.Inconclusive("undecidable")), client,
            JudgeMode.Fallback, new() { JudgePrompts = new() }, includeEvidence: false, judgeTimeout: null);
        var result = await new CompositeEvaluator(fallback, new Constant(EvaluationResult.Resisted("other branch")))
            .EvaluateAsync(Probe(), "Svar");
        Assert.Equal(EvaluationOutcome.Inconclusive, result.Outcome);
        Assert.True(result.MeasurementFailed);
        Assert.Single(result.JudgeInstruments!);
    }

    [Fact]
    public async Task Evaluate_ConfiguredFallbackDeadline_RetainsAttemptedInstrument()
    {
        using var client = new WaitingClient();
        var fallback = new JudgeBackedEvaluator(new Constant(EvaluationResult.Inconclusive("undecidable")), client,
            JudgeMode.Fallback, new() { JudgePrompts = new() }, includeEvidence: false, judgeTimeout: TimeSpan.FromMilliseconds(100));
        var result = await fallback.EvaluateAsync(Probe(), "Svar");
        Assert.Equal(EvaluationOutcome.Inconclusive, result.Outcome);
        Assert.True(result.MeasurementFailed);
        Assert.False(Assert.Single(Assert.Single(result.JudgeInstruments!).Calls).ResponseReceived);
    }

    private sealed class Constant(EvaluationResult result) : IProbeEvaluator
    {
        public string Name => "Test-only constant";
        public Task<EvaluationResult> EvaluateAsync(AttackProbe probe, string response, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
    private sealed class WaitingClient : IChatClient
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return new(); }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
