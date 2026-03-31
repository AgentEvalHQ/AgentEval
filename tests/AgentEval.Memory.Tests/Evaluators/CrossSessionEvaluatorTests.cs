using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using AgentEval.Memory.Tests.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Evaluators;

public class CrossSessionEvaluatorTests
{
    private readonly CrossSessionEvaluator _evaluator;

    public CrossSessionEvaluatorTests()
    {
        var fakeChatClient = new FakeChatClient();
        var judge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        _evaluator = new CrossSessionEvaluator(judge, NullLogger<CrossSessionEvaluator>.Instance);
    }

    [Fact]
    public async Task EvaluateAsync_AgentWithoutSessionReset_ReturnsNotSupported()
    {
        var agent = new TestMemoryAgent(); // Does not implement ISessionResettableAgent
        var facts = new[] { MemoryFact.Create("test fact") };

        var result = await _evaluator.EvaluateAsync(agent, facts);

        Assert.False(result.SessionResetSupported);
        Assert.False(result.Passed);
        Assert.Equal(0, result.OverallScore);
        Assert.Empty(result.FactResults);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("ISessionResettableAgent", result.ErrorMessage);
    }

    [Fact]
    public async Task EvaluateAsync_AgentWithSessionReset_RunsEvaluation()
    {
        var agent = new ResettableTestAgent();
        var facts = new[]
        {
            MemoryFact.Create("My name is Alice"),
            MemoryFact.Create("I live in Seattle")
        };

        var result = await _evaluator.EvaluateAsync(agent, facts);

        Assert.True(result.SessionResetSupported);
        Assert.Equal(2, result.FactResults.Count);
        Assert.Equal(1, result.SessionResetCount);
        Assert.True(result.Duration > TimeSpan.Zero);
        Assert.Equal("CrossSession", result.ScenarioName);
    }

    [Fact]
    public async Task EvaluateAsync_WithSessionReset_ResetsSessionOnce()
    {
        var agent = new ResettableTestAgent();
        var facts = new[] { MemoryFact.Create("test") };

        await _evaluator.EvaluateAsync(agent, facts);

        Assert.Equal(1, agent.ResetCount);
    }

    [Fact]
    public async Task EvaluateAsync_ScoreCalculation_MatchesPassRate()
    {
        var agent = new ResettableTestAgent();
        var facts = new[]
        {
            MemoryFact.Create("My name is Alice"),
            MemoryFact.Create("I work as a developer")
        };

        var result = await _evaluator.EvaluateAsync(agent, facts);

        // All facts should get score from FakeChatClient (90), which is >= 80 threshold        
        var expectedPassRate = result.FactResults.Count(f => f.Recalled) / (double)result.FactResults.Count * 100;
        Assert.Equal(expectedPassRate, result.OverallScore);
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
        var agent = new ResettableTestAgent();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _evaluator.EvaluateAsync(agent, null!));
    }

    [Fact]
    public async Task EvaluateAsync_FactResultsContainQueryAndResponse()
    {
        var agent = new ResettableTestAgent();
        var facts = new[] { MemoryFact.Create("My name is Alice") };

        var result = await _evaluator.EvaluateAsync(agent, facts);

        var factResult = Assert.Single(result.FactResults);
        Assert.NotEmpty(factResult.Query);
        Assert.NotEmpty(factResult.Response);
        Assert.Equal("My name is Alice", factResult.Fact);
    }

    [Fact]
    public async Task EvaluateAsync_SuccessThreshold_AffectsPassedField()
    {
        // FakeChatClient always returns 90 → all facts recalled → passRate = 1.0
        var agent = new ResettableTestAgent();
        var facts = new[] { MemoryFact.Create("test") };

        // High threshold should still pass since passRate is 1.0
        var result1 = await _evaluator.EvaluateAsync(agent, facts, successThreshold: 0.8);
        Assert.True(result1.Passed);

        // Even 100% threshold passes with 1.0 rate
        var result2 = await _evaluator.EvaluateAsync(agent, facts, successThreshold: 1.0);
        Assert.True(result2.Passed);
    }
}

/// <summary>
/// Test agent that supports session reset for cross-session testing.
/// </summary>
internal class ResettableTestAgent : IEvaluableAgent, ISessionResettableAgent
{
    public string Name => "Resettable Test Agent";
    public int ResetCount { get; private set; }

    private readonly Dictionary<string, string> _memory = new();

    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var lower = prompt.ToLowerInvariant();

        if (lower.Contains("remember") || lower.Contains("confirm"))
        {
            if (lower.Contains("alice")) _memory["name"] = "Alice";
            if (lower.Contains("seattle")) _memory["location"] = "Seattle";
            if (lower.Contains("developer")) _memory["job"] = "developer";
            return Task.FromResult(new AgentResponse
            {
                Text = "I'll remember that.",
                TokenUsage = new TokenUsage { PromptTokens = 10, CompletionTokens = 5 }
            });
        }

        // Return stored info or generic response
        var response = "I recall the information you shared previously.";
        return Task.FromResult(new AgentResponse
        {
            Text = response,
            TokenUsage = new TokenUsage { PromptTokens = 10, CompletionTokens = 5 }
        });
    }

    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        ResetCount++;
        _memory.Clear();
        return Task.CompletedTask;
    }
}
