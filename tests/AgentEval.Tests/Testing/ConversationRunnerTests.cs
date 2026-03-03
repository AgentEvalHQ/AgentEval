// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests;

public class ConversationRunnerTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullAgent_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ConversationRunner((IEvaluableAgent)null!));
    }

    [Fact]
    public void Constructor_WithOptions_DoesNotThrow()
    {
        var agent = new MockConversationAgent();
        var options = new ConversationRunnerOptions
        {
            TurnTimeout = TimeSpan.FromSeconds(60),
            ContinueOnError = true,
            MaxRetries = 3
        };

        var runner = new ConversationRunner(agent, options);
        Assert.NotNull(runner);
    }

    #endregion

    #region RunAsync Tests

    [Fact]
    public async Task RunAsync_SimpleConversation_ReturnsResult()
    {
        var agent = new MockConversationAgent("Hello! How can I help you?");
        var runner = new ConversationRunner(agent);

        var testCase = ConversationalTestCase.Create("Simple Test")
            .AddUserTurn("Hello")
            .Build();

        var result = await runner.RunAsync(testCase);

        Assert.NotNull(result);
        Assert.Equal(testCase, result.TestCase);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task RunAsync_UserTurn_GetsAssistantResponse()
    {
        var agent = new MockConversationAgent("I'm doing great, thanks!");
        var runner = new ConversationRunner(agent);

        var testCase = ConversationalTestCase.Create("Test")
            .AddUserTurn("How are you?")
            .Build();

        var result = await runner.RunAsync(testCase);

        // Should have user turn + assistant response
        Assert.Equal(2, result.ActualTurns.Count);
        Assert.Equal("user", result.ActualTurns[0].Role);
        Assert.Equal("assistant", result.ActualTurns[1].Role);
        Assert.Equal("I'm doing great, thanks!", result.ActualTurns[1].Content);
    }

    [Fact]
    public async Task RunAsync_SystemPrompt_IncludedInConversation()
    {
        var agent = new MockConversationAgent("I am a weather assistant.");
        var runner = new ConversationRunner(agent);

        var testCase = ConversationalTestCase.Create("Test")
            .WithSystemPrompt("You are a helpful weather assistant.")
            .AddUserTurn("What do you do?")
            .Build();

        var result = await runner.RunAsync(testCase);

        // Should have system + user + assistant turns
        Assert.Equal(3, result.ActualTurns.Count);
        Assert.Equal("system", result.ActualTurns[0].Role);
        Assert.Equal("user", result.ActualTurns[1].Role);
        Assert.Equal("assistant", result.ActualTurns[2].Role);
        // Agent only received one InvokeAsync call (user turn only)
        Assert.Single(agent.ReceivedPrompts);
        Assert.Equal("What do you do?", agent.ReceivedPrompts[0]);
    }

    [Fact]
    public async Task RunAsync_MultipleUserTurns_GetsMultipleResponses()
    {
        var responses = new Queue<string>(new[] { "Response 1", "Response 2" });
        var agent = new MockConversationAgent(responses);
        var runner = new ConversationRunner(agent);

        var testCase = ConversationalTestCase.Create("Multi-turn")
            .AddUserTurn("First question")
            .AddUserTurn("Second question")
            .Build();

        var result = await runner.RunAsync(testCase);

        // 2 user turns + 2 assistant responses = 4 turns
        Assert.Equal(4, result.ActualTurns.Count);
        Assert.Contains(result.ActualTurns, t => t.Content == "Response 1");
        Assert.Contains(result.ActualTurns, t => t.Content == "Response 2");
    }

    [Fact]
    public async Task RunAsync_ToolResponse_IncludedInConversation()
    {
        var agent = new MockConversationAgent("The weather is sunny.");
        var runner = new ConversationRunner(agent);

        var testCase = ConversationalTestCase.Create("Tool Test")
            .AddUserTurn("What's the weather?")
            .AddToolResponse("{\"temp\": 72}", "call_123")
            .Build();

        var result = await runner.RunAsync(testCase);

        Assert.Contains(result.ActualTurns, t => t.Role == "tool");
    }

    [Fact]
    public async Task RunAsync_RecordsTurnDurations()
    {
        var agent = new MockConversationAgent("Response");
        var runner = new ConversationRunner(agent);

        var testCase = ConversationalTestCase.Create("Test")
            .AddUserTurn("Question")
            .Build();

        var result = await runner.RunAsync(testCase);

        Assert.NotEmpty(result.TurnDurations);
        Assert.All(result.TurnDurations, d => Assert.True(d >= TimeSpan.Zero));
    }

    #endregion

    #region Assertion Tests

    [Fact]
    public async Task RunAsync_ExpectedToolsCalled_PassesAssertion()
    {
        var toolCallMessages = new List<object>
        {
            new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new TextContent("Weather retrieved"),
                new FunctionCallContent("get_weather", "get_weather", new Dictionary<string, object?>())
            })
        };
        var agent = new MockConversationAgent("Weather retrieved", toolCallMessages);
        var runner = new ConversationRunner(agent);

        var testCase = ConversationalTestCase.Create("Tool Test")
            .AddUserTurn("Get weather")
            .ExpectTools("get_weather")
            .Build();

        var result = await runner.RunAsync(testCase);

        Assert.True(result.Success);
        Assert.Contains(result.Assertions, a => a.Name == "ExpectedTools" && a.Passed);
    }

    [Fact]
    public async Task RunAsync_MissingExpectedTools_FailsAssertion()
    {
        var agent = new MockConversationAgent("I can't do that");
        var runner = new ConversationRunner(agent);

        var testCase = ConversationalTestCase.Create("Tool Test")
            .AddUserTurn("Get weather")
            .ExpectTools("get_weather", "format_response")
            .Build();

        var result = await runner.RunAsync(testCase);

        Assert.False(result.Success);
        var toolAssertion = result.Assertions.FirstOrDefault(a => a.Name == "ExpectedTools");
        Assert.NotNull(toolAssertion);
        Assert.False(toolAssertion.Passed);
        Assert.Contains("Missing tools", toolAssertion.Message);
    }

    [Fact]
    public async Task RunAsync_WithinMaxDuration_PassesAssertion()
    {
        var agent = new MockConversationAgent("Quick response");
        var runner = new ConversationRunner(agent);

        var testCase = ConversationalTestCase.Create("Fast Test")
            .AddUserTurn("Quick question")
            .WithMaxDuration(TimeSpan.FromSeconds(10))
            .Build();

        var result = await runner.RunAsync(testCase);

        Assert.Contains(result.Assertions, a => a.Name == "MaxDuration" && a.Passed);
    }

    [Fact]
    public async Task RunAsync_ConversationCompleteness_ChecksResponses()
    {
        var agent = new MockConversationAgent("Response");
        var runner = new ConversationRunner(agent);

        var testCase = ConversationalTestCase.Create("Test")
            .AddUserTurn("Question")
            .Build();

        var result = await runner.RunAsync(testCase);

        Assert.Contains(result.Assertions, a => a.Name == "ConversationCompleteness" && a.Passed);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task RunAsync_AgentThrows_CapturesError()
    {
        var agent = new MockConversationAgent(shouldThrow: true);
        var runner = new ConversationRunner(agent);

        var testCase = ConversationalTestCase.Create("Error Test")
            .AddUserTurn("Trigger error")
            .Build();

        var result = await runner.RunAsync(testCase);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task RunAsync_Cancellation_PropagatesException()
    {
        var agent = new MockConversationAgent("Response");
        var runner = new ConversationRunner(agent);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var testCase = ConversationalTestCase.Create("Cancel Test")
            .AddUserTurn("Question")
            .Build();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.RunAsync(testCase, cts.Token));
    }

    #endregion

    #region RunAllAsync Tests

    [Fact]
    public async Task RunAllAsync_MultipleTestCases_RunsAll()
    {
        var agent = new MockConversationAgent("Response");
        var runner = new ConversationRunner(agent);

        var testCases = new[]
        {
            ConversationalTestCase.Create("Test1").AddUserTurn("Q1").Build(),
            ConversationalTestCase.Create("Test2").AddUserTurn("Q2").Build(),
            ConversationalTestCase.Create("Test3").AddUserTurn("Q3").Build()
        };

        var results = await runner.RunAllAsync(testCases);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.NotNull(r.TestCase));
    }

    [Fact]
    public async Task RunAllAsync_EmptyList_ReturnsEmpty()
    {
        var agent = new MockConversationAgent("Response");
        var runner = new ConversationRunner(agent);

        var results = await runner.RunAllAsync(Array.Empty<ConversationalTestCase>());

        Assert.Empty(results);
    }

    #endregion

    #region Helper Mock

    private class MockConversationAgent : IEvaluableAgent
    {
        private readonly Queue<string> _responses;
        private readonly bool _shouldThrow;
        private readonly IReadOnlyList<object>? _rawMessages;

        public string Name => "MockConversationAgent";
        public List<string> ReceivedPrompts { get; } = new();

        public MockConversationAgent(string response = "Mock response", IReadOnlyList<object>? rawMessages = null)
        {
            _responses = new Queue<string>(new[] { response });
            _rawMessages = rawMessages;
        }

        public MockConversationAgent(Queue<string> responses)
        {
            _responses = responses;
            _rawMessages = null;
        }

        public MockConversationAgent(bool shouldThrow)
        {
            _responses = new Queue<string>();
            _shouldThrow = shouldThrow;
        }

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            ReceivedPrompts.Add(prompt);

            if (_shouldThrow)
                throw new InvalidOperationException("Mock agent error");

            var responseText = _responses.Count > 0 ? _responses.Dequeue() : "Default response";

            return Task.FromResult(new AgentResponse
            {
                Text = responseText,
                RawMessages = _rawMessages
            });
        }
    }

    #endregion
}
