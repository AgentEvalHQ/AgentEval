// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Models;
using AgentEval.Output;
using Microsoft.Extensions.AI;
using System.Text.Json.Nodes;
using Json.Schema;
using Xunit;

namespace AgentEval.Tests.Evals;

public sealed class CachedJudgeCostTests
{
    private const string Verdict = """{"overallScore":90,"reasoning":"supported","criteriaResults":[]}""";

    [Theory]
    [InlineData(8000L, 8000L, 16000L, TokenCostStatus.CacheAdjusted, "0.0096", false)]
    [InlineData(null, 8000L, null, TokenCostStatus.UndiscountedUnknownCache, "0.024", false)]
    [InlineData(8000L, null, null, TokenCostStatus.UndiscountedUnknownCache, "0.024", false)]
    [InlineData(0L, 0L, 0L, TokenCostStatus.Standard, "0.024", false)]
    [InlineData(8000L, 8000L, 16000L, TokenCostStatus.CacheAdjusted, "0.0096", true)]
    [InlineData(null, 8000L, null, TokenCostStatus.UndiscountedUnknownCache, "0.024", true)]
    [InlineData(8000L, null, null, TokenCostStatus.UndiscountedUnknownCache, "0.024", true)]
    [InlineData(0L, 0L, 0L, TokenCostStatus.Standard, "0.024", true)]
    public async Task Evaluate_CorrectiveRetry_PersistsSummedCacheUsageAndRates(
        long? first, long? second, long? expected, TokenCostStatus status, string amount, bool configured)
    {
        using var client = new UsageClient(first, second, configured);
        var judge = new ChatClientEvaluator(client);
        var eval = Create(configured ? judge.WithPrompts(new()) : judge);
        var result = await eval.EvaluateAsync(new(Query: "question", Response: "answer"));
        Assert.Equal(2, client.Calls);
        Assert.True(result.Score.Passed);
        Assert.Equal(configured, result.Provenance.JudgePrompt is not null);
        var evidence = result.Provenance.CostEstimate!;
        Assert.Equal(20000, evidence.InputTokens);
        Assert.Equal(2000, evidence.OutputTokens);
        Assert.Equal(expected, evidence.CachedInputTokens);
        Assert.Equal(status, evidence.Status);
        Assert.Equal(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), evidence.Amount);
        Assert.Equal((double)evidence.Amount!.Value, result.Provenance.EstimatedCost);
        Assert.False(result.Provenance.CacheHit); // Provider input caching is not an evaluation-result cache hit.
        var scenario = EvalResultPersistence.ToScenarioResult(result, "case", "case");
        var restored = EvalResultPersistence.FromScenarioResult(scenario)!;
        Assert.Equal(evidence, restored.Provenance.CostEstimate);
        var asm = typeof(FileSystemOutputStore).Assembly;
        using var stream = asm.GetManifestResourceStream(asm.GetManifestResourceNames().Single(n => n.EndsWith(".eval-result.schema.json")))!;
        using var reader = new StreamReader(stream);
        Assert.True(JsonSchema.FromText(reader.ReadToEnd()).Evaluate(JsonNode.Parse(scenario.Output)).IsValid);
    }

    [Theory]
    [InlineData(10001L, 0L)]
    [InlineData(-1L, 0L)]
    public async Task Evaluate_InvalidCacheOnEarlierAttempt_LaterUsageCannotHideIt(long first, long second)
    {
        using var client = new UsageClient(first, second);
        var result = await Create(new ChatClientEvaluator(client)).EvaluateAsync(new(Query: "q", Response: "a"));
        Assert.Equal(TokenCostStatus.Invalid, result.Provenance.CostEstimate!.Status);
        Assert.Null(result.Provenance.EstimatedCost);
    }

    private static AtomicLlmEval Create(IEvaluator evaluator) => new(evaluator,
        key: "cached-judge", name: "Cached judge", category: "quality", version: "1.0.0",
        criteria: ["supported"], judgeModel: "private-deployment",
        rateResolver: _ => new(0.001, 0.002) { CachedInputRatePer1K = 0.0001, Source = "test-contract-v1" });

    private sealed class UsageClient(long? first, long? second, bool configured = false) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            const string configuredVerdict = """{"overallScore":90,"summary":"supported","improvements":[],"criteriaResults":[{"id":"criterion-1","met":true,"explanation":"supported"}]}""";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Calls == 1 ? "invalid" : configured ? configuredVerdict : Verdict))
            {
                Usage = new() { InputTokenCount = 10000, OutputTokenCount = 1000, CachedInputTokenCount = Calls == 1 ? first : second }
            });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
