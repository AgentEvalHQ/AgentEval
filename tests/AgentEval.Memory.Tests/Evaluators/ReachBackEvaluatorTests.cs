using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using AgentEval.Memory.Tests.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Evaluators;

public class ReachBackEvaluatorTests
{
    private readonly ReachBackEvaluator _evaluator;
    private readonly TestMemoryAgent _agent;

    public ReachBackEvaluatorTests()
    {
        var fakeChatClient = new FakeChatClient();
        var judge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(judge, NullLogger<MemoryTestRunner>.Instance);
        _evaluator = new ReachBackEvaluator(runner, judge, NullLogger<ReachBackEvaluator>.Instance);
        _agent = new TestMemoryAgent();
    }

    [Fact]
    public async Task EvaluateAsync_WithValidInputs_ReturnsResult()
    {
        var fact = MemoryFact.Create("My name is Alice");
        var query = MemoryQuery.Create("What is my name?", fact);

        var result = await _evaluator.EvaluateAsync(_agent, fact, query, [2, 5]);

        Assert.NotNull(result);
        Assert.Equal(2, result.DepthResults.Count);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task EvaluateAsync_DepthsAreOrderedAscending()
    {
        var fact = MemoryFact.Create("My name is Alice");
        var query = MemoryQuery.Create("What is my name?", fact);

        var result = await _evaluator.EvaluateAsync(_agent, fact, query, [10, 2, 5]);

        // Depths should be processed in ascending order  
        Assert.Equal(2, result.DepthResults[0].Depth);
        Assert.Equal(5, result.DepthResults[1].Depth);
        Assert.Equal(10, result.DepthResults[2].Depth);
    }

    [Fact]
    public async Task EvaluateAsync_EachDepthHasDuration()
    {
        var fact = MemoryFact.Create("My name is Bob");
        var query = MemoryQuery.Create("What is my name?", fact);

        var result = await _evaluator.EvaluateAsync(_agent, fact, query, [2, 5, 10]);

        Assert.All(result.DepthResults, dr =>
        {
            Assert.True(dr.Duration >= TimeSpan.Zero);
            Assert.NotNull(dr.Response);
        });
    }

    [Fact]
    public async Task EvaluateAsync_PreservesFactReference()
    {
        var fact = MemoryFact.Create("My name is Alice");
        var query = MemoryQuery.Create("What is my name?", fact);

        var result = await _evaluator.EvaluateAsync(_agent, fact, query, [3]);

        Assert.Same(fact, result.Fact);
    }

    [Fact]
    public async Task EvaluateAsync_WithNullAgent_ThrowsArgumentNullException()
    {
        var fact = MemoryFact.Create("test");
        var query = MemoryQuery.Create("test?", fact);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _evaluator.EvaluateAsync(null!, fact, query, [5]));
    }

    [Fact]
    public async Task EvaluateAsync_WithNullFact_ThrowsArgumentNullException()
    {
        var query = MemoryQuery.Create("test?", MemoryFact.Create("test"));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _evaluator.EvaluateAsync(_agent, null!, query, [5]));
    }

    [Fact]
    public async Task EvaluateAsync_WithNullQuery_ThrowsArgumentNullException()
    {
        var fact = MemoryFact.Create("test");

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _evaluator.EvaluateAsync(_agent, fact, null!, [5]));
    }

    [Fact]
    public async Task EvaluateAsync_WithNullDepths_ThrowsArgumentNullException()
    {
        var fact = MemoryFact.Create("test");
        var query = MemoryQuery.Create("test?", fact);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _evaluator.EvaluateAsync(_agent, fact, query, null!));
    }

    [Fact]
    public async Task EvaluateAsync_WithSingleDepth_ReturnsSingleResult()
    {
        var fact = MemoryFact.Create("My name is Alice");
        var query = MemoryQuery.Create("What is my name?", fact);

        var result = await _evaluator.EvaluateAsync(_agent, fact, query, [5]);

        Assert.Single(result.DepthResults);
        Assert.Equal(5, result.DepthResults[0].Depth);
    }

    [Fact]
    public async Task EvaluateAsync_WithCancellation_ThrowsOperationCanceled()
    {
        var fact = MemoryFact.Create("test");
        var query = MemoryQuery.Create("test?", fact);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _evaluator.EvaluateAsync(_agent, fact, query, [5, 10], cts.Token));
    }

    [Fact]
    public async Task EvaluateAsync_WithResettableAgent_ResetsSessionBetweenDepths()
    {
        var fakeChatClient = new FakeChatClient();
        var judge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(judge, NullLogger<MemoryTestRunner>.Instance);
        var evaluator = new ReachBackEvaluator(runner, judge, NullLogger<ReachBackEvaluator>.Instance);
        var agent = new ResettableTestAgent();

        var fact = MemoryFact.Create("My name is Alice");
        var query = MemoryQuery.Create("What is my name?", fact);

        await evaluator.EvaluateAsync(agent, fact, query, [2, 5, 10]);

        // Agent should be reset once per depth (3 depths = 3 resets)
        Assert.Equal(3, agent.ResetCount);
    }
}
