using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using AgentEval.Memory.Tests.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Evaluators;

public class ReducerEvaluatorTests
{
    private readonly ReducerEvaluator _evaluator;
    private readonly TestMemoryAgent _agent;

    public ReducerEvaluatorTests()
    {
        var fakeChatClient = new FakeChatClient();
        var judge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(judge, NullLogger<MemoryTestRunner>.Instance);
        _evaluator = new ReducerEvaluator(runner, NullLogger<ReducerEvaluator>.Instance);
        _agent = new TestMemoryAgent();
    }

    [Fact]
    public async Task EvaluateAsync_WithValidInputs_ReturnsResult()
    {
        var facts = new[]
        {
            MemoryFact.Create("My name is Alice"),
            MemoryFact.Create("My name is Bob")
        };

        var result = await _evaluator.EvaluateAsync(_agent, facts, noiseCount: 5);

        Assert.NotNull(result);
        Assert.Equal(2, result.FactResults.Count);
        Assert.True(result.Duration > TimeSpan.Zero);
        Assert.Contains("ReducerFidelity", result.ScenarioName);
    }

    [Fact]
    public async Task EvaluateAsync_FactResultsMapToInputFacts()
    {
        var facts = new[]
        {
            MemoryFact.Create("I live in Seattle", "location", 80),
            MemoryFact.Create("My name is Alice", "personal", 60)
        };

        var result = await _evaluator.EvaluateAsync(_agent, facts, noiseCount: 3);

        Assert.Equal(2, result.FactResults.Count);
        Assert.Same(facts[0], result.FactResults[0].Fact);
        Assert.Same(facts[1], result.FactResults[1].Fact);
    }

    [Fact]
    public async Task EvaluateAsync_SetsPreReductionMessageCount()
    {
        var facts = new[] { MemoryFact.Create("test fact") };

        var result = await _evaluator.EvaluateAsync(_agent, facts, noiseCount: 10);

        // PreReductionMessageCount = facts.Count + noiseCount = 1 + 10 = 11
        Assert.Equal(11, result.PreReductionMessageCount);
    }

    [Fact]
    public async Task EvaluateAsync_ScenarioNameIncludesFactAndNoiseCount()
    {
        var facts = new[]
        {
            MemoryFact.Create("fact1"),
            MemoryFact.Create("fact2"),
            MemoryFact.Create("fact3")
        };

        var result = await _evaluator.EvaluateAsync(_agent, facts, noiseCount: 15);

        Assert.Contains("3facts", result.ScenarioName);
        Assert.Contains("15noise", result.ScenarioName);
    }

    [Fact]
    public async Task EvaluateAsync_WithNullAgent_ThrowsArgumentNullException()
    {
        var facts = new[] { MemoryFact.Create("test") };

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _evaluator.EvaluateAsync(null!, facts));
    }

    [Fact]
    public async Task EvaluateAsync_WithNullFacts_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _evaluator.EvaluateAsync(_agent, null!));
    }

    [Fact]
    public async Task EvaluateAsync_DefaultNoiseCount_Is20()
    {
        var facts = new[] { MemoryFact.Create("test") };

        var result = await _evaluator.EvaluateAsync(_agent, facts);

        // Default noise = 20, steps = 1 fact + 20 noise = 21
        Assert.Equal(21, result.PreReductionMessageCount);
    }

    [Fact]
    public async Task EvaluateAsync_EachFactResultHasScoreAndResponse()
    {
        var facts = new[]
        {
            MemoryFact.Create("My name is Alice"),
            MemoryFact.Create("I live in Seattle")
        };

        var result = await _evaluator.EvaluateAsync(_agent, facts, noiseCount: 5);

        Assert.All(result.FactResults, fr =>
        {
            Assert.True(fr.Score >= 0 && fr.Score <= 100);
            Assert.NotNull(fr.Response);
        });
    }
}
