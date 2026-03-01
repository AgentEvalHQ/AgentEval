// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Core;
using AgentEval.MAF;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentResponse = AgentEval.Core.AgentResponse;
using MAFAgentResponse = Microsoft.Agents.AI.AgentResponse;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Tests for <see cref="MAFIdentifiableAgentAdapter"/> — ensures ModelId and TokenUsage
/// are correctly propagated to the returned AgentResponse.
/// </summary>
public class MAFIdentifiableAgentAdapterTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullAgent_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MAFIdentifiableAgentAdapter(null!, "model-1", "Model 1"));
    }

    [Fact]
    public void Constructor_NullModelId_ThrowsArgumentNullException()
    {
        var agent = new FakeAIAgent("TestAgent", "Hello");
        Assert.Throws<ArgumentNullException>(() =>
            new MAFIdentifiableAgentAdapter(agent, null!, "Model 1"));
    }

    [Fact]
    public void Constructor_NullModelDisplayName_ThrowsArgumentNullException()
    {
        var agent = new FakeAIAgent("TestAgent", "Hello");
        Assert.Throws<ArgumentNullException>(() =>
            new MAFIdentifiableAgentAdapter(agent, "model-1", null!));
    }

    [Fact]
    public void Constructor_ValidArgs_SetsProperties()
    {
        var agent = new FakeAIAgent("TestAgent", "Hello");
        var adapter = new MAFIdentifiableAgentAdapter(agent, "gpt-4o", "GPT-4o");

        Assert.Equal("TestAgent", adapter.Name);
        Assert.Equal("gpt-4o", adapter.ModelId);
        Assert.Equal("GPT-4o", adapter.ModelDisplayName);
    }

    #endregion

    #region InvokeAsync Tests

    [Fact]
    public async Task InvokeAsync_ReturnsResponseWithModelId()
    {
        // Arrange
        var agent = new FakeAIAgent("TestAgent", "Hello, World!");
        var adapter = new MAFIdentifiableAgentAdapter(agent, "gpt-4o-2024-08-06", "GPT-4o");

        // Act
        var response = await adapter.InvokeAsync("Say hello");

        // Assert
        Assert.Equal("Hello, World!", response.Text);
        Assert.Equal("gpt-4o-2024-08-06", response.ModelId);
    }

    [Fact]
    public async Task InvokeAsync_WithUsage_ReturnsTokenUsage()
    {
        // Arrange
        var usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 25 };
        var agent = new FakeAIAgent("TestAgent", "Response text", usage);
        var adapter = new MAFIdentifiableAgentAdapter(agent, "gpt-4o", "GPT-4o");

        // Act
        var response = await adapter.InvokeAsync("Prompt");

        // Assert
        Assert.NotNull(response.TokenUsage);
        Assert.Equal(10, response.TokenUsage.PromptTokens);
        Assert.Equal(25, response.TokenUsage.CompletionTokens);
    }

    [Fact]
    public async Task InvokeAsync_WithoutUsage_TokenUsageIsNull()
    {
        // Arrange
        var agent = new FakeAIAgent("TestAgent", "Response text", usage: null);
        var adapter = new MAFIdentifiableAgentAdapter(agent, "gpt-4o", "GPT-4o");

        // Act
        var response = await adapter.InvokeAsync("Prompt");

        // Assert
        Assert.Null(response.TokenUsage);
    }

    [Fact]
    public async Task InvokeAsync_BothModelIdAndTokenUsage_ArePresent()
    {
        // Arrange — verifies the fix where both were missing
        var usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = 100 };
        var agent = new FakeAIAgent("TestAgent", "Full response", usage);
        var adapter = new MAFIdentifiableAgentAdapter(agent, "gpt-4o-mini", "GPT-4o Mini");

        // Act
        var response = await adapter.InvokeAsync("Test prompt");

        // Assert — the core regression test
        Assert.Equal("gpt-4o-mini", response.ModelId);
        Assert.NotNull(response.TokenUsage);
        Assert.Equal(50, response.TokenUsage.PromptTokens);
        Assert.Equal(100, response.TokenUsage.CompletionTokens);
        Assert.Equal("Full response", response.Text);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsRawMessages()
    {
        // Arrange
        var agent = new FakeAIAgent("TestAgent", "Some response");
        var adapter = new MAFIdentifiableAgentAdapter(agent, "model-1", "Model 1");

        // Act
        var response = await adapter.InvokeAsync("Prompt");

        // Assert
        Assert.NotNull(response.RawMessages);
        Assert.NotEmpty(response.RawMessages);
    }

    #endregion
}

#region Test Helpers

/// <summary>
/// A fake AIAgent subclass for testing MAF adapters without requiring
/// an actual LLM backend.
/// </summary>
internal class FakeAIAgent : AIAgent
{
    private readonly string _responseText;
    private readonly UsageDetails? _usage;

    public override string? Name { get; }

    public FakeAIAgent(string name, string responseText, UsageDetails? usage = null)
    {
        Name = name;
        _responseText = responseText;
        _usage = usage;
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        return new ValueTask<AgentSession>(new FakeAgentSession());
    }

    protected override Task<MAFAgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var responseMessage = new ChatMessage(ChatRole.Assistant, _responseText);
        var response = new MAFAgentResponse(responseMessage)
        {
            Usage = _usage
        };
        return Task.FromResult(response);
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Streaming not needed for these tests.");
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<JsonElement>(JsonSerializer.SerializeToElement(new { }));
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<AgentSession>(new FakeAgentSession());
    }
}

/// <summary>
/// Minimal AgentSession implementation for testing.
/// </summary>
internal class FakeAgentSession : AgentSession
{
}

#endregion
