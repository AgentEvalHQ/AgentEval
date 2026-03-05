using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Engine;

public class MemoryJudgeTests
{
    [Fact]
    public async Task JudgeAsync_WithValidResponseAndFacts_ShouldReturnJudgment()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        
        var query = MemoryQuery.Create("What is my name?", MemoryFact.Create("My name is John"));
        var agentResponse = "My name is John Smith.";

        // Act
        var result = await memoryJudge.JudgeAsync(agentResponse, query);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Score > 0);
        Assert.NotNull(result.FoundFacts);
        Assert.NotNull(result.MissingFacts);
    }

    [Fact]
    public async Task JudgeAsync_WithNullResponse_ShouldThrowArgumentNullException()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var query = MemoryQuery.Create("What is my name?", MemoryFact.Create("My name is John"));

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => memoryJudge.JudgeAsync(null!, query));
    }

    [Fact]
    public async Task JudgeAsync_WithNullQuery_ShouldThrowArgumentNullException()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => memoryJudge.JudgeAsync("response", null!));
    }

    [Theory]
    [InlineData(85.0, "Good memory performance")]
    [InlineData(95.0, "Excellent recall")]
    [InlineData(45.0, "Poor memory performance")]
    public async Task JudgeAsync_WithDifferentScores_ShouldReturnExpectedScore(double expectedScore, string explanation)
    {
        // Arrange
        var fakeChatClient = new FakeChatClient(expectedScore, explanation);
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        
        var query = MemoryQuery.Create("Test query", MemoryFact.Create("Test fact"));
        var response = "Test response";

        // Act
        var result = await memoryJudge.JudgeAsync(response, query);

        // Assert
        Assert.Equal(expectedScore, result.Score);
        Assert.Equal(explanation, result.Explanation);
    }
}

/// <summary>
/// Test implementation of IChatClient for memory judge testing.
/// </summary>
public class FakeChatClient : IChatClient
{
    private readonly double _score;
    private readonly string _explanation;

    public FakeChatClient(double score = 90.0, string explanation = "Test judgment")
    {
        _score = score;
        _explanation = explanation;
    }

    public ChatClientMetadata Metadata { get; } = new("test-model", new Uri("http://localhost"));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages, 
        ChatOptions? options = null, 
        CancellationToken cancellationToken = default)
    {
        var jsonResponse = $$"""
        {
          "found_facts": ["Test fact"],
          "missing_facts": [],
          "forbidden_found": [],
          "score": {{_score}},
          "explanation": "{{_explanation}}"
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
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public TService? GetService<TService>(object? key = null) where TService : class => null;
    public object? GetService(Type serviceType, object? key = null) => null;
    public void Dispose() { }
}