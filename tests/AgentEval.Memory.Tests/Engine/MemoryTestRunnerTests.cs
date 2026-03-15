using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Engine;

public class MemoryTestRunnerTests
{
    [Fact]
    public async Task RunAsync_WithValidAgentAndScenario_ShouldReturnResults()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(memoryJudge, NullLogger<MemoryTestRunner>.Instance);
        
        var agent = new TestMemoryAgent();
        var scenario = CreateTestScenario();

        // Act
        var result = await runner.RunAsync(agent, scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.OverallScore > 0);
        Assert.NotEmpty(result.QueryResults);
        Assert.Equal(2, result.QueryResults.Count); // Two queries in test scenario
    }

    [Fact]
    public async Task RunAsync_WithNullAgent_ShouldThrowArgumentNullException()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(memoryJudge, NullLogger<MemoryTestRunner>.Instance);
        var scenario = CreateTestScenario();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.RunAsync(null!, scenario));
    }

    [Fact]
    public async Task RunAsync_WithNullScenario_ShouldThrowArgumentNullException()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(memoryJudge, NullLogger<MemoryTestRunner>.Instance);
        var agent = new TestMemoryAgent();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.RunAsync(agent, null!));
    }

    [Fact]
    public async Task RunAsync_WithMultipleQueries_ShouldEvaluateAllQueries()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(memoryJudge, NullLogger<MemoryTestRunner>.Instance);
        
        var agent = new TestMemoryAgent();
        var scenario = CreateComplexTestScenario();

        // Act
        var result = await runner.RunAsync(agent, scenario);

        // Assert
        Assert.Equal(3, result.QueryResults.Count);
        Assert.All(result.QueryResults, qr => Assert.True(qr.Score > 0));
        Assert.True(result.OverallScore > 0);
    }

    private static MemoryTestScenario CreateTestScenario()
    {
        var facts = new[]
        {
            MemoryFact.Create("My name is Alice"),
            MemoryFact.Create("I work as a developer")
        };

        var steps = facts.Select(f => MemoryStep.Fact($"Remember: {f.Content}")).ToArray();
        
        var queries = new[]
        {
            MemoryQuery.Create("What is my name?", facts[0]),
            MemoryQuery.Create("What is my job?", facts[1])
        };

        return new MemoryTestScenario
        {
            Name = "Basic Test",
            Description = "Tests basic fact recall",
            Steps = steps,
            Queries = queries
        };
    }

    [Fact]
    public async Task RunMemoryQueriesAsync_WithQueries_RunsWithoutSetupSteps()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(memoryJudge, NullLogger<MemoryTestRunner>.Instance);

        var agent = new TestMemoryAgent();
        // Pre-seed the agent's memory directly so queries succeed without setup steps
        await agent.InvokeAsync("Remember: My name is Alice");

        var queries = new[]
        {
            MemoryQuery.Create("What is my name?", MemoryFact.Create("My name is Alice"))
        };

        // Act
        var result = await runner.RunMemoryQueriesAsync(agent, queries, "DirectQueryTest");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("DirectQueryTest", result.ScenarioName);
        Assert.Single(result.QueryResults);
        Assert.True(result.OverallScore > 0);
    }

    [Fact]
    public async Task RunMemoryQueriesAsync_WithMultipleQueries_EvaluatesAll()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(memoryJudge, NullLogger<MemoryTestRunner>.Instance);

        var agent = new TestMemoryAgent();
        await agent.InvokeAsync("Remember: My name is Bob");
        await agent.InvokeAsync("Remember: I live in Seattle");

        var queries = new[]
        {
            MemoryQuery.Create("What is my name?", MemoryFact.Create("My name is Bob")),
            MemoryQuery.Create("Where do I live?", MemoryFact.Create("I live in Seattle")),
            MemoryQuery.Create("Do I have any pets?", MemoryFact.Create("I have a cat"))
        };

        // Act
        var result = await runner.RunMemoryQueriesAsync(agent, queries, "MultiQueryTest");

        // Assert
        Assert.Equal(3, result.QueryResults.Count);
        Assert.All(result.QueryResults, qr => Assert.True(qr.Score > 0));
    }

    [Fact]
    public async Task RunAsync_WithSessionResetMarkerAndResettableAgent_ResetsSession()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(memoryJudge, NullLogger<MemoryTestRunner>.Instance);

        var agent = new ResettableTestMemoryAgent();

        var facts = new[]
        {
            MemoryFact.Create("My name is Alice"),
            MemoryFact.Create("I work as a developer")
        };

        var steps = new[]
        {
            MemoryStep.Fact("Remember: My name is Alice"),
            MemoryStep.System("[SESSION_RESET_POINT]"),
            MemoryStep.Fact("Remember: I work as a developer")
        };

        var queries = new[]
        {
            MemoryQuery.Create("What is my name?", facts[0]),
            MemoryQuery.Create("What is my job?", facts[1])
        };

        var scenario = new MemoryTestScenario
        {
            Name = "Session Reset Test",
            Description = "Tests session reset behavior with resettable agent",
            Steps = steps,
            Queries = queries
        };

        // Act
        var result = await runner.RunAsync(agent, scenario);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1, agent.ResetCount);
        // The agent should NOT have received the reset marker as a prompt
        Assert.DoesNotContain("[SESSION_RESET_POINT]", agent.ReceivedPrompts);
        // The agent should have received the two fact steps (and query prompts), but not the reset marker
        Assert.Contains(agent.ReceivedPrompts, p => p.Contains("My name is Alice"));
        Assert.Contains(agent.ReceivedPrompts, p => p.Contains("I work as a developer"));
    }

    [Fact]
    public async Task RunAsync_WithSessionResetMarkerAndNonResettableAgent_SkipsMarker()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(memoryJudge, NullLogger<MemoryTestRunner>.Instance);

        var agent = new TestMemoryAgent();

        var facts = new[]
        {
            MemoryFact.Create("My name is Alice"),
            MemoryFact.Create("I work as a developer")
        };

        var steps = new[]
        {
            MemoryStep.Fact("Remember: My name is Alice"),
            MemoryStep.System("[SESSION_RESET_POINT]"),
            MemoryStep.Fact("Remember: I work as a developer")
        };

        var queries = new[]
        {
            MemoryQuery.Create("What is my name?", facts[0]),
            MemoryQuery.Create("What is my job?", facts[1])
        };

        var scenario = new MemoryTestScenario
        {
            Name = "Session Reset Non-Resettable Test",
            Description = "Tests session reset behavior with non-resettable agent",
            Steps = steps,
            Queries = queries
        };

        // Act
        var result = await runner.RunAsync(agent, scenario);

        // Assert - should not throw, marker should be skipped
        Assert.NotNull(result);
        // The agent should NOT have received the reset marker as a prompt
        Assert.DoesNotContain("[SESSION_RESET_POINT]", agent.ReceivedPrompts);
    }

    private static MemoryTestScenario CreateComplexTestScenario()
    {
        var facts = new[]
        {
            MemoryFact.Create("My name is Bob"),
            MemoryFact.Create("I live in Seattle"),
            MemoryFact.Create("I have a dog named Max")
        };

        var steps = facts.Select(f => MemoryStep.Fact($"Please remember: {f.Content}")).ToArray();

        var queries = new[]
        {
            MemoryQuery.Create("What is my name?", facts[0]),
            MemoryQuery.Create("Where do I live?", facts[1]),
            MemoryQuery.Create("Do I have any pets?", facts[2])
        };

        return new MemoryTestScenario
        {
            Name = "Complex Test",
            Description = "Tests multiple types of facts",
            Steps = steps,
            Queries = queries
        };
    }
}

/// <summary>
/// Test agent that simulates memory behavior for testing.
/// </summary>
public class TestMemoryAgent : IEvaluableAgent
{
    public string Name => "Test Memory Agent";
    private readonly Dictionary<string, string> _memory = new();
    public List<string> ReceivedPrompts { get; } = new();

    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ReceivedPrompts.Add(prompt);
        var response = ProcessPrompt(prompt);

        return Task.FromResult(new AgentResponse
        {
            Text = response,
            TokenUsage = new TokenUsage { PromptTokens = 10, CompletionTokens = 5 }
        });
    }

    private string ProcessPrompt(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        
        // Store memory
        if (lower.Contains("remember") || lower.Contains("my name is"))
        {
            if (lower.Contains("alice")) _memory["name"] = "Alice";
            if (lower.Contains("bob")) _memory["name"] = "Bob";
            if (lower.Contains("developer")) _memory["job"] = "developer";
            if (lower.Contains("seattle")) _memory["location"] = "Seattle";
            if (lower.Contains("dog named max")) _memory["pet"] = "dog named Max";
            return "I'll remember that.";
        }
        
        // Answer queries
        if (lower.Contains("what is my name") && _memory.ContainsKey("name"))
            return $"Your name is {_memory["name"]}.";
        if (lower.Contains("what is my job") && _memory.ContainsKey("job"))
            return $"You work as a {_memory["job"]}.";
        if (lower.Contains("where do i live") && _memory.ContainsKey("location"))
            return $"You live in {_memory["location"]}.";
        if (lower.Contains("do i have any pets") && _memory.ContainsKey("pet"))
            return $"Yes, you have a {_memory["pet"]}.";
            
        return "I don't know.";
    }
}

/// <summary>
/// Test agent that implements both IEvaluableAgent and ISessionResettableAgent
/// to verify session reset behavior during memory test execution.
/// </summary>
public class ResettableTestMemoryAgent : IEvaluableAgent, ISessionResettableAgent
{
    public string Name => "Resettable Test Memory Agent";
    private readonly Dictionary<string, string> _memory = new();
    public int ResetCount { get; private set; }
    public List<string> ReceivedPrompts { get; } = new();

    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ReceivedPrompts.Add(prompt);
        var response = ProcessPrompt(prompt);

        return Task.FromResult(new AgentResponse
        {
            Text = response,
            TokenUsage = new TokenUsage { PromptTokens = 10, CompletionTokens = 5 }
        });
    }

    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        ResetCount++;
        return Task.CompletedTask;
    }

    private string ProcessPrompt(string prompt)
    {
        var lower = prompt.ToLowerInvariant();

        // Store memory
        if (lower.Contains("remember") || lower.Contains("my name is"))
        {
            if (lower.Contains("alice")) _memory["name"] = "Alice";
            if (lower.Contains("bob")) _memory["name"] = "Bob";
            if (lower.Contains("developer")) _memory["job"] = "developer";
            if (lower.Contains("seattle")) _memory["location"] = "Seattle";
            if (lower.Contains("dog named max")) _memory["pet"] = "dog named Max";
            return "I'll remember that.";
        }

        // Answer queries
        if (lower.Contains("what is my name") && _memory.ContainsKey("name"))
            return $"Your name is {_memory["name"]}.";
        if (lower.Contains("what is my job") && _memory.ContainsKey("job"))
            return $"You work as a {_memory["job"]}.";
        if (lower.Contains("where do i live") && _memory.ContainsKey("location"))
            return $"You live in {_memory["location"]}.";
        if (lower.Contains("do i have any pets") && _memory.ContainsKey("pet"))
            return $"Yes, you have a {_memory["pet"]}.";

        return "I don't know.";
    }
}