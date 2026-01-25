// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Tracing;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// Tests for ChatExecutionResult and ChatTraceRecorder.
/// </summary>
public class ChatExecutionResultTests
{
    #region ChatExecutionResult Tests

    [Fact]
    public void ChatExecutionResult_CountsTurnsCorrectly()
    {
        // Arrange
        var turns = new List<ChatTurn>
        {
            new() { Role = ChatRole.User, Content = "Hello", TurnIndex = 0 },
            new() { Role = ChatRole.Assistant, Content = "Hi there!", TurnIndex = 1 },
            new() { Role = ChatRole.User, Content = "How are you?", TurnIndex = 2 },
            new() { Role = ChatRole.Assistant, Content = "I'm doing great!", TurnIndex = 3 }
        };

        var result = new ChatExecutionResult { Turns = turns };

        // Assert
        Assert.Equal(4, result.TotalTurnCount);
        Assert.Equal(2, result.UserTurnCount);
        Assert.Equal(2, result.AgentTurnCount);
    }

    [Fact]
    public void ChatExecutionResult_GetsFinalResponse()
    {
        // Arrange
        var turns = new List<ChatTurn>
        {
            new() { Role = ChatRole.User, Content = "Hello", TurnIndex = 0 },
            new() { Role = ChatRole.Assistant, Content = "First response", TurnIndex = 1 },
            new() { Role = ChatRole.User, Content = "More?", TurnIndex = 2 },
            new() { Role = ChatRole.Assistant, Content = "Final response", TurnIndex = 3 }
        };

        var result = new ChatExecutionResult { Turns = turns };

        // Assert
        Assert.Equal("Final response", result.FinalResponse);
    }

    [Fact]
    public void ChatExecutionResult_GetsAllAgentResponses()
    {
        // Arrange
        var turns = new List<ChatTurn>
        {
            new() { Role = ChatRole.User, Content = "A", TurnIndex = 0 },
            new() { Role = ChatRole.Assistant, Content = "Response 1", TurnIndex = 1 },
            new() { Role = ChatRole.User, Content = "B", TurnIndex = 2 },
            new() { Role = ChatRole.Assistant, Content = "Response 2", TurnIndex = 3 },
            new() { Role = ChatRole.User, Content = "C", TurnIndex = 4 },
            new() { Role = ChatRole.Assistant, Content = "Response 3", TurnIndex = 5 }
        };

        var result = new ChatExecutionResult { Turns = turns };

        // Assert
        var responses = result.AgentResponses.ToList();
        Assert.Equal(3, responses.Count);
        Assert.Equal("Response 1", responses[0]);
        Assert.Equal("Response 2", responses[1]);
        Assert.Equal("Response 3", responses[2]);
    }

    [Fact]
    public void ChatExecutionResult_GetsAllUserPrompts()
    {
        // Arrange
        var turns = new List<ChatTurn>
        {
            new() { Role = ChatRole.User, Content = "Prompt 1", TurnIndex = 0 },
            new() { Role = ChatRole.Assistant, Content = "A", TurnIndex = 1 },
            new() { Role = ChatRole.User, Content = "Prompt 2", TurnIndex = 2 },
            new() { Role = ChatRole.Assistant, Content = "B", TurnIndex = 3 }
        };

        var result = new ChatExecutionResult { Turns = turns };

        // Assert
        var prompts = result.UserPrompts.ToList();
        Assert.Equal(2, prompts.Count);
        Assert.Equal("Prompt 1", prompts[0]);
        Assert.Equal("Prompt 2", prompts[1]);
    }

    [Fact]
    public void ChatExecutionResult_AllTurnsSucceeded_WhenNoErrors()
    {
        // Arrange
        var turns = new List<ChatTurn>
        {
            new() { Role = ChatRole.User, Content = "A", TurnIndex = 0 },
            new() { Role = ChatRole.Assistant, Content = "B", TurnIndex = 1, Error = null }
        };

        var result = new ChatExecutionResult { Turns = turns };

        // Assert
        Assert.True(result.AllTurnsSucceeded);
        Assert.Empty(result.FailedTurns);
    }

    [Fact]
    public void ChatExecutionResult_AllTurnsSucceeded_FalseWhenErrors()
    {
        // Arrange
        var turns = new List<ChatTurn>
        {
            new() { Role = ChatRole.User, Content = "A", TurnIndex = 0 },
            new() { Role = ChatRole.Assistant, Content = "B", TurnIndex = 1, 
                Error = new TraceError { Type = "TestError", Message = "Test" } }
        };

        var result = new ChatExecutionResult { Turns = turns };

        // Assert
        Assert.False(result.AllTurnsSucceeded);
        Assert.Single(result.FailedTurns);
    }

    [Fact]
    public void ChatExecutionResult_HandlesSystemMessages()
    {
        // Arrange
        var turns = new List<ChatTurn>
        {
            new() { Role = ChatRole.System, Content = "You are a helpful assistant", TurnIndex = 0 },
            new() { Role = ChatRole.User, Content = "Hello", TurnIndex = 1 },
            new() { Role = ChatRole.Assistant, Content = "Hi!", TurnIndex = 2 }
        };

        var result = new ChatExecutionResult { Turns = turns };

        // Assert
        Assert.Equal(3, result.TotalTurnCount);
        Assert.Equal(1, result.UserTurnCount);
        Assert.Equal(1, result.AgentTurnCount);
    }

    #endregion

    #region ChatTurn Tests

    [Fact]
    public void ChatTurn_IsUserTurn_True()
    {
        var turn = new ChatTurn { Role = ChatRole.User, Content = "Hello" };
        Assert.True(turn.IsUserTurn);
        Assert.False(turn.IsAgentTurn);
    }

    [Fact]
    public void ChatTurn_IsAgentTurn_True()
    {
        var turn = new ChatTurn { Role = ChatRole.Assistant, Content = "Hello" };
        Assert.False(turn.IsUserTurn);
        Assert.True(turn.IsAgentTurn);
    }

    #endregion

    #region ChatTraceRecorder Tests

    [Fact]
    public async Task ChatTraceRecorder_RecordsMultiTurnConversation()
    {
        // Arrange
        var responses = new Queue<string>(new[] { "Hi!", "I'm well, thanks!", "Goodbye!" });
        var mockAgent = new MockTestableAgent(() => responses.Dequeue());
        await using var recorder = new ChatTraceRecorder(mockAgent, "test_conv");

        // Act
        await recorder.AddUserTurnAsync("Hello");
        await recorder.AddUserTurnAsync("How are you?");
        await recorder.AddUserTurnAsync("Bye");

        var result = recorder.GetResult();

        // Assert
        Assert.Equal(6, result.TotalTurnCount);
        Assert.Equal(3, result.UserTurnCount);
        Assert.Equal(3, result.AgentTurnCount);
        Assert.Equal("Goodbye!", result.FinalResponse);
        Assert.Equal("test_conv", result.ConversationId);
    }

    [Fact]
    public async Task ChatTraceRecorder_TracksTokenUsage()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("Response", new TokenUsage { PromptTokens = 10, CompletionTokens = 20 });
        await using var recorder = new ChatTraceRecorder(mockAgent);

        // Act
        await recorder.AddUserTurnAsync("Hello");
        await recorder.AddUserTurnAsync("Again");

        var result = recorder.GetResult();

        // Assert
        Assert.NotNull(result.AggregatePerformance);
        Assert.Equal(20, result.AggregatePerformance.PromptTokens);
        Assert.Equal(40, result.AggregatePerformance.CompletionTokens);
    }

    [Fact]
    public async Task ChatTraceRecorder_RecordsTimestamps()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("Response");
        await using var recorder = new ChatTraceRecorder(mockAgent);

        // Act
        await recorder.AddUserTurnAsync("First");
        await Task.Delay(50); // Small delay
        await recorder.AddUserTurnAsync("Second");

        var result = recorder.GetResult();

        // Assert
        var turn1 = result.Turns[0];
        var turn2 = result.Turns[2]; // Skip agent response

        Assert.True(turn2.Timestamp > turn1.Timestamp);
    }

    [Fact]
    public async Task ChatTraceRecorder_HandlesErrors()
    {
        // Arrange
        var callCount = 0;
        var mockAgent = new MockTestableAgent(() =>
        {
            callCount++;
            if (callCount == 2)
                throw new InvalidOperationException("API error");
            return "Response";
        });
        await using var recorder = new ChatTraceRecorder(mockAgent);

        // Act
        await recorder.AddUserTurnAsync("First");
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.AddUserTurnAsync("Second"));

        var result = recorder.GetResult();

        // Assert
        Assert.Equal(4, result.TotalTurnCount); // 2 user + 2 agent (one with error)
        Assert.False(result.AllTurnsSucceeded);
        Assert.Single(result.FailedTurns);
    }

    [Fact]
    public async Task ChatTraceRecorder_ConvertsToAgentTrace()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("Response");
        await using var recorder = new ChatTraceRecorder(mockAgent, "trace_conv");

        await recorder.AddUserTurnAsync("Hello");
        await recorder.AddUserTurnAsync("Goodbye");

        // Act
        var trace = recorder.ToAgentTrace();

        // Assert
        Assert.Equal("1.0", trace.Version);
        Assert.Contains("trace_conv", trace.TraceName);
        Assert.Equal(4, trace.Entries.Count); // 2 requests + 2 responses
        Assert.Equal(2, trace.Performance?.CallCount);
    }

    [Fact]
    public async Task ChatTraceRecorder_RoundTrip_SaveAndReplay()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("Response");
        var tempFile = Path.Combine(Path.GetTempPath(), $"chat_trace_{Guid.NewGuid()}.trace.json");

        try
        {
            await using (var recorder = new ChatTraceRecorder(mockAgent, "roundtrip"))
            {
                await recorder.AddUserTurnAsync("Hello");
                await recorder.AddUserTurnAsync("Goodbye");
                await recorder.SaveAsync(tempFile);
            }

            // Act - Load and replay
            var replayer = await TraceReplayingAgent.FromFileAsync(tempFile);
            var response1 = await replayer.InvokeAsync("Hello");
            var response2 = await replayer.InvokeAsync("Goodbye");

            // Assert
            Assert.Equal("Response", response1.Text);
            Assert.Equal("Response", response2.Text);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ChatTraceRecorder_SystemMessage_MustBeFirst()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("Response");
        var recorder = new ChatTraceRecorder(mockAgent);

        // Act - Add user turn first, then try system
        await recorder.AddUserTurnAsync("Hello");

        // Assert
        Assert.Throws<InvalidOperationException>(() => 
            recorder.AddSystemMessage("System prompt"));
    }

    [Fact]
    public void ChatTraceRecorder_SystemMessage_WhenFirst_Succeeds()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("Response");
        var recorder = new ChatTraceRecorder(mockAgent);

        // Act
        recorder.AddSystemMessage("You are a helpful assistant");

        // Assert - no exception
        Assert.Equal(1, recorder.TurnCount);
    }

    #endregion

    #region Mock Agent

    private class MockTestableAgent : IEvaluableAgent
    {
        private readonly Func<string> _responseFactory;
        private readonly TokenUsage? _tokenUsage;

        public MockTestableAgent(string fixedResponse, TokenUsage? tokenUsage = null)
            : this(() => fixedResponse, tokenUsage)
        {
        }

        public MockTestableAgent(Func<string> responseFactory, TokenUsage? tokenUsage = null)
        {
            _responseFactory = responseFactory;
            _tokenUsage = tokenUsage;
        }

        public string Name => "MockAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var text = _responseFactory();
            return Task.FromResult(new AgentResponse
            {
                Text = text,
                TokenUsage = _tokenUsage
            });
        }
    }

    #endregion
}
