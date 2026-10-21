// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Engine;

/// <summary>
/// T63 / T13-9: a memory evaluation's cost is its judge calls priced at the judge model's rate from the token counts
/// the provider reported, or null (not priced). It used to be <c>totalTokens × $0.003 / 1K</c> for every model, and
/// with no usage reported those tokens were the judge's own estimate: an estimate priced at an invented rate.
/// </summary>
public sealed class MemoryTestRunnerCostTests
{
    private static readonly MemoryQuery[] Queries =
    [
        MemoryQuery.Create("Where do I live?", MemoryFact.Create("Lives in Madrid")),
        MemoryQuery.Create("What is my job?", MemoryFact.Create("Works as a nurse")),
    ];

    private static Task<MemoryEvaluationResult> RunAsync(IChatClient judge) =>
        new MemoryTestRunner(new MemoryJudge(judge, NullLogger<MemoryJudge>.Instance), NullLogger<MemoryTestRunner>.Instance)
            .RunMemoryQueriesAsync(new EchoAgent(), Queries, "cost");

    [Fact]
    public async Task NoUsageReported_IsNotPriced()
    {
        var result = await RunAsync(new UsageJudgeClient("gpt-4o-mini", _ => null));

        Assert.True(result.TokensUsed > 0); // the judge's own estimate is still counted, as before
        Assert.Null(result.EstimatedCost);  // was TokensUsed × 0.003 / 1000
    }

    [Fact]
    public async Task UsageReported_KnownJudgeModel_IsPricedAtItsRate()
    {
        var result = await RunAsync(new UsageJudgeClient("gpt-4o-mini", _ => new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 200 }));

        // gpt-4o-mini: $0.00015 / $0.0006 per 1K → 0.00015 + 0.00012 per call, two calls.
        Assert.Equal(0.00054m, result.EstimatedCost);
        Assert.Equal(2400, result.TokensUsed);
    }

    [Fact]
    public async Task UsageReported_JudgeModelWithNoRate_IsNotPriced()
    {
        var result = await RunAsync(new UsageJudgeClient("llama3.1:8b", _ => new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 200 }));

        Assert.Equal(2400, result.TokensUsed);
        Assert.Null(result.EstimatedCost); // was 2400 × 0.003 / 1000 = 0.0072
    }

    [Fact]
    public async Task OneCountMissing_IsNotPriced()
    {
        var result = await RunAsync(new UsageJudgeClient("gpt-4o-mini", _ => new UsageDetails { InputTokenCount = 1000 }));

        Assert.Null(result.EstimatedCost);
    }

    [Fact]
    public async Task OneJudgeCallNotPriced_TheTotalIsNotPriced()
    {
        // Complete or absent: the first call reports usage, the second does not.
        var result = await RunAsync(new UsageJudgeClient("gpt-4o-mini",
            call => call == 1 ? new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 200 } : null));

        Assert.Null(result.EstimatedCost);
    }

    [Fact]
    public void PriceJudgment_FallsBackToTheJudgeTable()
    {
        // A judge model only JudgeCostMap knows is still priced; one neither table knows is not.
        AgentEval.Evals.JudgeCostMap.Register("memory-cost-test-judge", 0.001, 0.002);
        var judgment = new MemoryJudgmentResult
        {
            Score = 90, FoundFacts = [], MissingFacts = [], ForbiddenFound = [],
            TokensUsed = 1500, InputTokens = 1000, OutputTokens = 500, JudgeModel = "memory-cost-test-judge",
        };

        Assert.Equal(0.002m, MemoryTestRunner.PriceJudgment(judgment));
        Assert.Null(MemoryTestRunner.PriceJudgment(new MemoryJudgmentResult
        {
            Score = 90, FoundFacts = [], MissingFacts = [], ForbiddenFound = [],
            TokensUsed = 1500, InputTokens = 1000, OutputTokens = 500, JudgeModel = null,
        }));
    }

    /// <summary>Answers every query with the fact it was told, so the judge has something to grade.</summary>
    private sealed class EchoAgent : IEvaluableAgent
    {
        public string Name => "echo";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse { Text = "You live in Madrid and work as a nurse." });
    }

    /// <summary>A judge that scores 90 and reports, for its Nth call, the usage <paramref name="usageForCall"/> gives.</summary>
    private sealed class UsageJudgeClient(string modelId, Func<int, UsageDetails?> usageForCall) : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            const string json = """{"found_facts": [], "missing_facts": [], "forbidden_found": [], "score": 90, "explanation": "ok"}""";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
            {
                ModelId = modelId,
                Usage = usageForCall(Interlocked.Increment(ref _calls)),
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
