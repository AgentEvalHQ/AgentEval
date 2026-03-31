// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Extensions;

public class CanRememberExtensionsTests
{
    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new FakeChatClientForExtensions());
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task CanRememberAsync_SingleFact_ReturnsResult()
    {
        // Arrange
        using var sp = CreateServiceProvider();
        var agent = new SimpleTestAgent("Your name is Alice.");

        // Act
        var result = await agent.CanRememberAsync(
            "My name is Alice",
            "What is my name?",
            sp);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.QueryResults);
        Assert.Equal("Basic Memory Test", result.ScenarioName);
    }

    [Fact]
    public async Task CanRememberAsync_SingleFact_GeneratesQuestionAutomatically()
    {
        // Arrange
        using var sp = CreateServiceProvider();
        var agent = new SimpleTestAgent("Your name is Alice.");

        // Act — no question parameter, should auto-generate from "name is" keyword
        var result = await agent.CanRememberAsync(
            "My name is Alice",
            serviceProvider: sp);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.QueryResults);
    }

    [Fact]
    public async Task CanRememberAsync_MultipleFacts_ReturnsResult()
    {
        // Arrange
        using var sp = CreateServiceProvider();
        var agent = new SimpleTestAgent("Your name is Alice and you work as a developer.");
        var facts = new[] { "My name is Alice", "I work as a developer" };

        // Act
        var result = await agent.CanRememberAsync(facts, sp);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Multi-Fact Memory Test", result.ScenarioName);
        Assert.True(result.QueryResults.Count >= 2);
    }

    [Fact]
    public async Task CanRememberAsync_CustomQueries_ReturnsResult()
    {
        // Arrange
        using var sp = CreateServiceProvider();
        var agent = new SimpleTestAgent("Your name is Alice.");
        var factsAndQueries = new[]
        {
            ("My name is Alice", "What is my name?"),
            ("I live in Seattle", "Where do I live?")
        };

        // Act
        var result = await agent.CanRememberAsync(factsAndQueries, sp);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.QueryResults.Count);
    }

    [Fact]
    public async Task CanRememberThroughNoiseAsync_ReturnsResult()
    {
        // Arrange
        using var sp = CreateServiceProvider();
        var agent = new SimpleTestAgent("Your name is Alice.");
        var facts = new[] { "My name is Alice" };

        // Act
        var result = await agent.CanRememberThroughNoiseAsync(facts, 3, sp);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.QueryResults);
    }

    [Fact]
    public async Task CanRememberAcrossSessionsAsync_WithResettableAgent_ReturnsResult()
    {
        // Arrange
        using var sp = CreateServiceProvider();
        var agent = new ResettableSimpleTestAgent("Your name is Alice.");
        var facts = new[] { "My name is Alice" };

        // Act
        var result = await agent.CanRememberAcrossSessionsAsync(facts, sp);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.QueryResults);
    }

    [Fact]
    public async Task CanRememberAcrossSessionsAsync_WithNonResettableAgent_ThrowsInvalidOperation()
    {
        // Arrange
        using var sp = CreateServiceProvider();
        var agent = new SimpleTestAgent("anything");
        var facts = new[] { "My name is Alice" };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.CanRememberAcrossSessionsAsync(facts, sp));
    }

    [Fact]
    public async Task CanRememberAcrossSessionsAsync_ErrorMessage_ContainsAgentTypeName()
    {
        // Arrange
        using var sp = CreateServiceProvider();
        var agent = new SimpleTestAgent("anything");

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.CanRememberAcrossSessionsAsync(new[] { "fact" }, sp));

        // Assert
        Assert.Contains("SimpleTestAgent", ex.Message);
        Assert.Contains("ISessionResettableAgent", ex.Message);
    }

    [Fact]
    public async Task QuickMemoryCheckAsync_FactInResponse_ReturnsTrue()
    {
        // Arrange — agent echoes the fact back
        var agent = new SimpleTestAgent("The capital of France is Paris.");

        // Act
        var result = await agent.QuickMemoryCheckAsync("Paris");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task QuickMemoryCheckAsync_FactNotInResponse_ReturnsFalse()
    {
        // Arrange — agent does not mention the fact
        var agent = new SimpleTestAgent("I don't know.");

        // Act
        var result = await agent.QuickMemoryCheckAsync("Paris");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task QuickMemoryCheckAsync_CaseInsensitive()
    {
        // Arrange — agent returns fact in different case
        var agent = new SimpleTestAgent("The capital is PARIS.");

        // Act
        var result = await agent.QuickMemoryCheckAsync("paris");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task QuickMemoryCheckAsync_AgentThrows_ReturnsFalse()
    {
        // Arrange — agent that throws on invoke
        var agent = new ThrowingTestAgent();

        // Act
        var result = await agent.QuickMemoryCheckAsync("anything");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task QuickMemoryCheckAsync_Cancellation_Throws()
    {
        // Arrange
        var agent = new SimpleTestAgent("response");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => agent.QuickMemoryCheckAsync("fact", null, cts.Token));
    }

    [Fact]
    public async Task QuickMemoryCheckAsync_MultipleFacts_ReturnsDictionary()
    {
        // Arrange — agent mentions Alice but not Paris
        var agent = new SimpleTestAgent("Your name is Alice and you live in London.");
        var facts = new[] { "Alice", "Paris" };

        // Act
        var results = await agent.QuickMemoryCheckAsync(facts);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.True(results["Alice"]);
        Assert.False(results["Paris"]);
    }

    [Fact]
    public async Task QuickMemoryCheckAsync_CustomQuestion_UsesProvidedQuestion()
    {
        // Arrange — agent that records the prompt
        var agent = new RecordingTestAgent("response with Paris");

        // Act
        await agent.QuickMemoryCheckAsync("Paris", "What is the capital of France?");

        // Assert
        Assert.Single(agent.ReceivedPrompts);
        Assert.Equal("What is the capital of France?", agent.ReceivedPrompts[0]);
    }

    [Fact]
    public async Task GetMemoryTestRunner_NoServiceProvider_Throws()
    {
        // Arrange — null service provider means no IChatClient available
        var agent = new SimpleTestAgent("response");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.CanRememberAsync("fact", "question?", serviceProvider: null));

        Assert.Contains("IChatClient", ex.Message);
    }

    [Fact]
    public async Task GetMemoryTestRunner_ServiceProviderWithRunner_UsesRegisteredRunner()
    {
        // Arrange — register a custom IMemoryTestRunner
        var services = new ServiceCollection();
        var customRunner = new StubMemoryTestRunner();
        services.AddSingleton<IMemoryTestRunner>(customRunner);
        using var sp = services.BuildServiceProvider();
        var agent = new SimpleTestAgent("response");

        // Act
        var result = await agent.CanRememberAsync("fact", "question?", sp);

        // Assert — the stub runner was invoked
        Assert.True(customRunner.WasCalled);
        Assert.NotNull(result);
    }

    #region Test Doubles

    /// <summary>
    /// Minimal agent that returns a fixed response to any prompt.
    /// </summary>
    private sealed class SimpleTestAgent : IEvaluableAgent
    {
        private readonly string _response;

        public SimpleTestAgent(string response) => _response = response;
        public string Name => "SimpleTestAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentResponse
            {
                Text = _response,
                TokenUsage = new TokenUsage { PromptTokens = 5, CompletionTokens = 5 }
            });
        }
    }

    /// <summary>
    /// Agent that records received prompts and returns a fixed response.
    /// </summary>
    private sealed class RecordingTestAgent : IEvaluableAgent
    {
        private readonly string _response;
        public List<string> ReceivedPrompts { get; } = new();

        public RecordingTestAgent(string response) => _response = response;
        public string Name => "RecordingTestAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            ReceivedPrompts.Add(prompt);
            return Task.FromResult(new AgentResponse
            {
                Text = _response,
                TokenUsage = new TokenUsage { PromptTokens = 5, CompletionTokens = 5 }
            });
        }
    }

    /// <summary>
    /// Agent that implements ISessionResettableAgent for cross-session tests.
    /// </summary>
    private sealed class ResettableSimpleTestAgent : IEvaluableAgent, ISessionResettableAgent
    {
        private readonly string _response;

        public ResettableSimpleTestAgent(string response) => _response = response;
        public string Name => "ResettableSimpleTestAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse
            {
                Text = _response,
                TokenUsage = new TokenUsage { PromptTokens = 5, CompletionTokens = 5 }
            });
        }

        public Task ResetSessionAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Agent that throws on every invocation.
    /// </summary>
    private sealed class ThrowingTestAgent : IEvaluableAgent
    {
        public string Name => "ThrowingTestAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated agent failure");
    }

    /// <summary>
    /// Stub IMemoryTestRunner that records invocation and returns a minimal result.
    /// </summary>
    private sealed class StubMemoryTestRunner : IMemoryTestRunner
    {
        public bool WasCalled { get; private set; }

        public Task<MemoryEvaluationResult> RunAsync(
            IEvaluableAgent agent,
            MemoryTestScenario scenario,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new MemoryEvaluationResult
            {
                OverallScore = 100,
                QueryResults = [],
                FoundFacts = [],
                MissingFacts = [],
                ForbiddenFound = [],
                Duration = TimeSpan.Zero,
                ScenarioName = scenario.Name
            });
        }

        public Task<MemoryEvaluationResult> RunMemoryTestAsync(
            IEvaluableAgent agent,
            MemoryTestScenario scenario,
            CancellationToken cancellationToken = default) =>
            RunAsync(agent, scenario, cancellationToken);

        public Task<MemoryEvaluationResult> RunMemoryQueriesAsync(
            IEvaluableAgent agent,
            IEnumerable<MemoryQuery> queries,
            string scenarioName,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new MemoryEvaluationResult
            {
                OverallScore = 100,
                QueryResults = [],
                FoundFacts = [],
                MissingFacts = [],
                ForbiddenFound = [],
                Duration = TimeSpan.Zero,
                ScenarioName = scenarioName
            });
        }
    }

    /// <summary>
    /// Minimal IChatClient that returns valid judge JSON for memory evaluation.
    /// </summary>
    private sealed class FakeChatClientForExtensions : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("test-model", new Uri("http://localhost"));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var jsonResponse = """
            {
              "found_facts": ["Test fact"],
              "missing_facts": [],
              "forbidden_found": [],
              "score": 85.0,
              "explanation": "Good recall"
            }
            """;

            var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, jsonResponse)])
            {
                ModelId = "test-model"
            };

            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public TService? GetService<TService>(object? key = null) where TService : class => null;
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    #endregion
}
