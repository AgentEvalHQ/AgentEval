// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Tracing;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// Tests for TraceRecordingAgent and TraceReplayingAgent.
/// </summary>
public class TraceRecordingAndReplayTests
{
    #region TraceRecordingAgent Tests

    [Fact]
    public async Task TraceRecordingAgent_RecordsBasicInvocation()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("Test response");
        await using var recorder = new TraceRecordingAgent(mockAgent, "basic_test");

        // Act
        var response = await recorder.InvokeAsync("Hello");

        // Assert
        Assert.Equal("Test response", response.Text);
        Assert.Equal(2, recorder.Trace.Entries.Count); // Request + Response
        Assert.Equal("Hello", recorder.Trace.Entries[0].Prompt);
        Assert.Equal(TraceEntryType.Request, recorder.Trace.Entries[0].Type);
        Assert.Equal("Test response", recorder.Trace.Entries[1].Text);
        Assert.Equal(TraceEntryType.Response, recorder.Trace.Entries[1].Type);
    }

    [Fact]
    public async Task TraceRecordingAgent_RecordsMultipleInvocations()
    {
        // Arrange
        var responses = new Queue<string>(new[] { "First", "Second", "Third" });
        var mockAgent = new MockTestableAgent(() => responses.Dequeue());
        await using var recorder = new TraceRecordingAgent(mockAgent, "multi_test");

        // Act
        await recorder.InvokeAsync("Prompt 1");
        await recorder.InvokeAsync("Prompt 2");
        await recorder.InvokeAsync("Prompt 3");
        
        // Trigger finalization
        await recorder.ToJsonAsync();

        // Assert
        Assert.Equal(6, recorder.Trace.Entries.Count); // 3 Request + 3 Response pairs
        Assert.Equal(3, recorder.Trace.Performance?.CallCount);
    }

    [Fact]
    public async Task TraceRecordingAgent_RecordsTokenUsage()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("Response", new TokenUsage { PromptTokens = 10, CompletionTokens = 20 });
        await using var recorder = new TraceRecordingAgent(mockAgent, "token_test");

        // Act
        await recorder.InvokeAsync("Hello");

        // Assert
        var responseEntry = recorder.Trace.Entries.FirstOrDefault(e => e.Type == TraceEntryType.Response);
        Assert.NotNull(responseEntry);
        Assert.NotNull(responseEntry.TokenUsage);
        Assert.Equal(10, responseEntry.TokenUsage.PromptTokens);
        Assert.Equal(20, responseEntry.TokenUsage.CompletionTokens);
    }

    [Fact]
    public async Task TraceRecordingAgent_SavesTraceToFile()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("Test response");
        var tempFile = Path.Combine(Path.GetTempPath(), $"trace_test_{Guid.NewGuid()}.trace.json");

        try
        {
            await using (var recorder = new TraceRecordingAgent(mockAgent, "file_test"))
            {
                await recorder.InvokeAsync("Hello");
                await recorder.SaveAsync(tempFile);
            }

            // Act
            var loadedTrace = await TraceSerializer.LoadFromFileAsync(tempFile);

            // Assert
            Assert.Equal("file_test", loadedTrace.TraceName);
            Assert.Equal(2, loadedTrace.Entries.Count);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task TraceRecordingAgent_RecordsErrors()
    {
        // Arrange
        var mockAgent = new MockTestableAgent(() => throw new InvalidOperationException("Test error"));
        await using var recorder = new TraceRecordingAgent(mockAgent, "error_test");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.InvokeAsync("Hello"));

        var responseEntry = recorder.Trace.Entries.FirstOrDefault(e => e.Type == TraceEntryType.Response);
        Assert.NotNull(responseEntry);
        Assert.NotNull(responseEntry.Error);
        Assert.Equal("InvalidOperationException", responseEntry.Error.Type);
        Assert.Contains("Test error", responseEntry.Error.Message);
    }

    [Fact]
    public async Task TraceRecordingAgent_CalculatesPerformanceMetrics()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("Response", new TokenUsage { PromptTokens = 10, CompletionTokens = 20 });
        await using var recorder = new TraceRecordingAgent(mockAgent, "perf_test");

        // Act
        await recorder.InvokeAsync("Hello 1");
        await recorder.InvokeAsync("Hello 2");

        // Get trace to trigger finalization
        var json = await recorder.ToJsonAsync();

        // Assert
        Assert.NotNull(recorder.Trace.Performance);
        Assert.Equal(2, recorder.Trace.Performance.CallCount);
        Assert.Equal(20, recorder.Trace.Performance.TotalPromptTokens);
        Assert.Equal(40, recorder.Trace.Performance.TotalCompletionTokens);
    }

    #endregion

    #region TraceReplayingAgent Tests

    [Fact]
    public async Task TraceReplayingAgent_ReplaysBasicInvocation()
    {
        // Arrange
        var trace = CreateBasicTrace("Hello", "Recorded response");
        var replayer = new TraceReplayingAgent(trace);

        // Act
        var response = await replayer.InvokeAsync("Hello");

        // Assert
        Assert.Equal("Recorded response", response.Text);
    }

    [Fact]
    public async Task TraceReplayingAgent_ReplaysMultipleInvocations()
    {
        // Arrange
        var trace = CreateMultiTurnTrace(
            ("Hello", "First"),
            ("How are you?", "Second"),
            ("Goodbye", "Third"));
        var replayer = new TraceReplayingAgent(trace);

        // Act
        var response1 = await replayer.InvokeAsync("Hello");
        var response2 = await replayer.InvokeAsync("How are you?");
        var response3 = await replayer.InvokeAsync("Goodbye");

        // Assert
        Assert.Equal("First", response1.Text);
        Assert.Equal("Second", response2.Text);
        Assert.Equal("Third", response3.Text);
    }

    [Fact]
    public async Task TraceReplayingAgent_ThrowsWhenTraceExhausted()
    {
        // Arrange
        var trace = CreateBasicTrace("Hello", "Response");
        var replayer = new TraceReplayingAgent(trace);
        await replayer.InvokeAsync("Hello");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            replayer.InvokeAsync("Another prompt"));
        Assert.Contains("exhausted", ex.Message);
    }

    [Fact]
    public async Task TraceReplayingAgent_ValidatesExactMatch()
    {
        // Arrange
        var trace = CreateBasicTrace("Expected prompt", "Response");
        var replayer = new TraceReplayingAgent(trace, new TraceReplayOptions
        {
            ValidateRequests = true,
            RequestMatchingMode = RequestMatchingMode.Exact
        });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<TraceReplayMismatchException>(() =>
            replayer.InvokeAsync("Different prompt"));
        Assert.Contains("mismatch", ex.Message);
    }

    [Fact]
    public async Task TraceReplayingAgent_AllowsAnyMatchByDefault()
    {
        // Arrange
        var trace = CreateBasicTrace("Recorded prompt", "Response");
        var replayer = new TraceReplayingAgent(trace);

        // Act - Different prompt should still work with index-based matching
        var response = await replayer.InvokeAsync("Completely different prompt");

        // Assert
        Assert.Equal("Response", response.Text);
    }

    [Fact]
    public async Task TraceReplayingAgent_CanReset()
    {
        // Arrange
        var trace = CreateBasicTrace("Hello", "Response");
        var replayer = new TraceReplayingAgent(trace);
        await replayer.InvokeAsync("Hello");

        // Act
        replayer.Reset();
        var response = await replayer.InvokeAsync("Hello again");

        // Assert
        Assert.Equal("Response", response.Text);
    }

    [Fact]
    public async Task TraceReplayingAgent_ReplaysRecordedError()
    {
        // Arrange
        var trace = CreateTraceWithError("Hello", "InvalidOperationException", "Test error");
        var replayer = new TraceReplayingAgent(trace);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<TraceReplayException>(() =>
            replayer.InvokeAsync("Hello"));
        Assert.Contains("Test error", ex.Message);
    }

    [Fact]
    public async Task TraceReplayingAgent_FromFileAsync_LoadsAndReplays()
    {
        // Arrange
        var trace = CreateBasicTrace("Hello", "File response");
        var tempFile = Path.Combine(Path.GetTempPath(), $"replay_test_{Guid.NewGuid()}.trace.json");

        try
        {
            await TraceSerializer.SaveToFileAsync(trace, tempFile);

            // Act
            var replayer = await TraceReplayingAgent.FromFileAsync(tempFile);
            var response = await replayer.InvokeAsync("Hello");

            // Assert
            Assert.Equal("File response", response.Text);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    #endregion

    #region Round-Trip Tests

    [Fact]
    public async Task RoundTrip_RecordAndReplay()
    {
        // Arrange - Record
        var responses = new Queue<string>(new[] { "First response", "Second response" });
        var mockAgent = new MockTestableAgent(() => responses.Dequeue());
        AgentTrace recordedTrace;

        await using (var recorder = new TraceRecordingAgent(mockAgent, "roundtrip_test"))
        {
            await recorder.InvokeAsync("First prompt");
            await recorder.InvokeAsync("Second prompt");
            recordedTrace = recorder.Trace;
        }

        // Act - Replay
        var replayer = new TraceReplayingAgent(recordedTrace);
        var replay1 = await replayer.InvokeAsync("First prompt");
        var replay2 = await replayer.InvokeAsync("Second prompt");

        // Assert
        Assert.Equal("First response", replay1.Text);
        Assert.Equal("Second response", replay2.Text);
    }

    [Fact]
    public async Task RoundTrip_RecordSaveLoadReplay()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("Recorded response");
        var tempFile = Path.Combine(Path.GetTempPath(), $"roundtrip_{Guid.NewGuid()}.trace.json");

        try
        {
            // Record and save
            await using (var recorder = new TraceRecordingAgent(mockAgent, "save_load_test"))
            {
                await recorder.InvokeAsync("Test prompt");
                await recorder.SaveAsync(tempFile);
            }

            // Load and replay
            var replayer = await TraceReplayingAgent.FromFileAsync(tempFile);
            var response = await replayer.InvokeAsync("Test prompt");

            // Assert
            Assert.Equal("Recorded response", response.Text);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    #endregion

    #region TraceSerializer Tests

    [Fact]
    public async Task TraceSerializer_RoundTripsTrace()
    {
        // Arrange
        var trace = new AgentTrace
        {
            TraceName = "serialization_test",
            AgentName = "TestAgent",
            ModelId = "gpt-4",
            CapturedAt = DateTimeOffset.UtcNow,
            Entries = new List<TraceEntry>
            {
                new() { Type = TraceEntryType.Request, Index = 0, Prompt = "Hello" },
                new() { Type = TraceEntryType.Response, Index = 0, Text = "World", DurationMs = 100 }
            },
            Performance = new TracePerformance
            {
                TotalDurationMs = 100,
                CallCount = 1
            }
        };

        // Act
        var json = await TraceSerializer.SerializeToStringAsync(trace);
        var deserialized = await TraceSerializer.DeserializeFromStringAsync(json);

        // Assert
        Assert.Equal("serialization_test", deserialized.TraceName);
        Assert.Equal("TestAgent", deserialized.AgentName);
        Assert.Equal("gpt-4", deserialized.ModelId);
        Assert.Equal(2, deserialized.Entries.Count);
        Assert.Equal("Hello", deserialized.Entries[0].Prompt);
        Assert.Equal("World", deserialized.Entries[1].Text);
    }

    #endregion

    #region Helper Methods

    private static AgentTrace CreateBasicTrace(string prompt, string response)
    {
        return new AgentTrace
        {
            TraceName = "test_trace",
            AgentName = "TestAgent",
            Entries = new List<TraceEntry>
            {
                new() { Type = TraceEntryType.Request, Index = 0, Prompt = prompt },
                new() { Type = TraceEntryType.Response, Index = 0, Text = response }
            }
        };
    }

    private static AgentTrace CreateMultiTurnTrace(params (string Prompt, string Response)[] turns)
    {
        var entries = new List<TraceEntry>();
        for (int i = 0; i < turns.Length; i++)
        {
            entries.Add(new TraceEntry { Type = TraceEntryType.Request, Index = i, Prompt = turns[i].Prompt });
            entries.Add(new TraceEntry { Type = TraceEntryType.Response, Index = i, Text = turns[i].Response });
        }

        return new AgentTrace
        {
            TraceName = "multi_turn_trace",
            AgentName = "TestAgent",
            Entries = entries
        };
    }

    private static AgentTrace CreateTraceWithError(string prompt, string errorType, string errorMessage)
    {
        return new AgentTrace
        {
            TraceName = "error_trace",
            Entries = new List<TraceEntry>
            {
                new() { Type = TraceEntryType.Request, Index = 0, Prompt = prompt },
                new() { Type = TraceEntryType.Response, Index = 0, Error = new TraceError { Type = errorType, Message = errorMessage } }
            }
        };
    }

    #endregion

    #region Streaming Recording Tests

    [Fact]
    public async Task TraceRecordingAgent_RecordsStreamingInvocation()
    {
        // Arrange
        var mockAgent = new MockStreamingAgent(new[] { "Hello", " ", "World", "!" });
        await using var recorder = new TraceRecordingAgent(mockAgent, "streaming_test");

        // Act
        var chunks = new List<AgentResponseChunk>();
        await foreach (var chunk in recorder.InvokeStreamingAsync("Test prompt"))
        {
            chunks.Add(chunk);
        }

        // Assert
        Assert.Equal(4, chunks.Count);
        Assert.Equal("Hello World!", string.Concat(chunks.Select(c => c.Text)));

        var responseEntry = recorder.Trace.Entries.FirstOrDefault(e => e.Type == TraceEntryType.Response);
        Assert.NotNull(responseEntry);
        Assert.True(responseEntry.IsStreaming);
        Assert.NotNull(responseEntry.StreamingChunks);
        Assert.Equal(4, responseEntry.StreamingChunks.Count);
    }

    [Fact]
    public async Task TraceRecordingAgent_RecordsStreamingChunkTimings()
    {
        // Arrange
        var mockAgent = new MockStreamingAgent(new[] { "A", "B", "C" }, chunkDelay: 50);
        await using var recorder = new TraceRecordingAgent(mockAgent, "timing_test");

        // Act
        await foreach (var chunk in recorder.InvokeStreamingAsync("Test"))
        {
            // Consume all chunks
        }

        // Assert
        var responseEntry = recorder.Trace.Entries.FirstOrDefault(e => e.Type == TraceEntryType.Response);
        Assert.NotNull(responseEntry?.StreamingChunks);

        // First chunk may have 0 delay, subsequent chunks should have ~50ms delay
        for (int i = 1; i < responseEntry.StreamingChunks.Count; i++)
        {
            Assert.True(responseEntry.StreamingChunks[i].DelayMs >= 40,
                $"Chunk {i} delay was {responseEntry.StreamingChunks[i].DelayMs}ms, expected >= 40ms");
        }
    }

    [Fact]
    public async Task TraceRecordingAgent_RecordsTimeToFirstToken()
    {
        // Arrange
        var mockAgent = new MockStreamingAgent(new[] { "First", " token" });
        await using var recorder = new TraceRecordingAgent(mockAgent, "ttft_test");

        // Act
        await foreach (var _ in recorder.InvokeStreamingAsync("Test"))
        {
        }
        
        // Trigger finalization
        await recorder.ToJsonAsync();

        // Assert - TTFT is stored on Performance, not entry
        Assert.NotNull(recorder.Trace.Performance);
        Assert.NotNull(recorder.Trace.Performance.TimeToFirstTokenMs);
        Assert.True(recorder.Trace.Performance.TimeToFirstTokenMs >= 0);
    }

    [Fact]
    public async Task TraceRecordingAgent_RecordsStreamingToolCalls()
    {
        // Arrange
        var toolCall = new ToolCallInfo { Name = "SearchTool", CallId = "call_123" };
        var mockAgent = new MockStreamingAgent(new[] { "Searching", "...", "Done" }, toolCall: toolCall);
        await using var recorder = new TraceRecordingAgent(mockAgent, "streaming_tools_test");

        // Act
        await foreach (var _ in recorder.InvokeStreamingAsync("Search for something"))
        {
        }

        // Assert
        var responseEntry = recorder.Trace.Entries.FirstOrDefault(e => e.Type == TraceEntryType.Response);
        Assert.NotNull(responseEntry);
        Assert.NotNull(responseEntry.ToolCalls);
        Assert.True(responseEntry.ToolCalls.Count > 0);
        Assert.Equal("SearchTool", responseEntry.ToolCalls[0].Name);
    }

    [Fact]
    public async Task TraceRecordingAgent_ThrowsForNonStreamingAgent()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("Response");
        await using var recorder = new TraceRecordingAgent(mockAgent, "non_streaming");

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in recorder.InvokeStreamingAsync("Test"))
            {
            }
        });
    }

    #endregion

    #region Streaming Replay Tests

    [Fact]
    public async Task TraceReplayingAgent_ReplaysStreamingChunks()
    {
        // Arrange
        var trace = CreateStreamingTrace("Test", new[] { "Hello", " ", "World" });
        var replayer = new TraceReplayingAgent(trace);

        // Act
        var chunks = new List<AgentResponseChunk>();
        await foreach (var chunk in replayer.InvokeStreamingAsync("Test"))
        {
            chunks.Add(chunk);
        }

        // Assert
        Assert.Equal(3, chunks.Count);
        Assert.Equal("Hello", chunks[0].Text);
        Assert.Equal(" ", chunks[1].Text);
        Assert.Equal("World", chunks[2].Text);
        Assert.True(chunks[2].IsComplete);
    }

    [Fact]
    public async Task TraceReplayingAgent_SimulatesStreamingDelay()
    {
        // Arrange
        var trace = CreateStreamingTrace("Test", new[] { "A", "B", "C" }, chunkDelayMs: 50);
        var replayer = new TraceReplayingAgent(trace, new TraceReplayOptions
        {
            SimulateStreamingDelay = true
        });

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var _ in replayer.InvokeStreamingAsync("Test"))
        {
        }
        sw.Stop();

        // Assert - Should have ~100ms total delay (2 chunks with 50ms each after first)
        Assert.True(sw.ElapsedMilliseconds >= 80, $"Expected >= 80ms, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task TraceReplayingAgent_FallsBackToTextChunking()
    {
        // Arrange - Create trace without streaming chunks
        var trace = CreateBasicTrace("Test", "This is a longer response text");
        var replayer = new TraceReplayingAgent(trace, new TraceReplayOptions
        {
            DefaultChunkSize = 5
        });

        // Act
        var chunks = new List<AgentResponseChunk>();
        await foreach (var chunk in replayer.InvokeStreamingAsync("Test"))
        {
            chunks.Add(chunk);
        }

        // Assert
        var fullText = string.Concat(chunks.Select(c => c.Text));
        Assert.Equal("This is a longer response text", fullText);
        Assert.True(chunks.Count > 1, "Should have multiple chunks");
    }

    [Fact]
    public async Task StreamingRoundTrip_RecordAndReplayStreaming()
    {
        // Arrange - Record
        var originalChunks = new[] { "Part ", "one. ", "Part ", "two." };
        var mockAgent = new MockStreamingAgent(originalChunks);
        AgentTrace recordedTrace;

        await using (var recorder = new TraceRecordingAgent(mockAgent, "streaming_roundtrip"))
        {
            await foreach (var _ in recorder.InvokeStreamingAsync("Test prompt"))
            {
            }
            recordedTrace = recorder.Trace;
        }

        // Act - Replay
        var replayer = new TraceReplayingAgent(recordedTrace);
        var replayedChunks = new List<string>();
        await foreach (var chunk in replayer.InvokeStreamingAsync("Test prompt"))
        {
            if (chunk.Text != null)
                replayedChunks.Add(chunk.Text);
        }

        // Assert
        Assert.Equal(originalChunks.Length, replayedChunks.Count);
        for (int i = 0; i < originalChunks.Length; i++)
        {
            Assert.Equal(originalChunks[i], replayedChunks[i]);
        }
    }

    [Fact]
    public async Task StreamingRoundTrip_PreservesToolCallsInStreaming()
    {
        // Arrange - Record with tool call
        var toolCall = new ToolCallInfo { Name = "Calculator", CallId = "calc_1" };
        var mockAgent = new MockStreamingAgent(new[] { "Computing", "...", "Result: 42" }, toolCall: toolCall);
        AgentTrace recordedTrace;

        await using (var recorder = new TraceRecordingAgent(mockAgent, "tool_streaming_roundtrip"))
        {
            await foreach (var _ in recorder.InvokeStreamingAsync("Calculate something"))
            {
            }
            recordedTrace = recorder.Trace;
        }

        // Act - Replay
        var replayer = new TraceReplayingAgent(recordedTrace);
        var hasToolCall = false;
        await foreach (var chunk in replayer.InvokeStreamingAsync("Calculate something"))
        {
            if (chunk.ToolCallStarted != null)
            {
                hasToolCall = true;
                Assert.Equal("Calculator", chunk.ToolCallStarted.Name);
            }
        }

        // Assert
        Assert.True(hasToolCall, "Replayed stream should include tool call");
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

    #region Mock Streaming Agent

    private class MockStreamingAgent : IEvaluableAgent, IStreamableAgent
    {
        private readonly string[] _chunks;
        private readonly int _chunkDelay;
        private readonly ToolCallInfo? _toolCall;

        public MockStreamingAgent(string[] chunks, int chunkDelay = 0, ToolCallInfo? toolCall = null)
        {
            _chunks = chunks;
            _chunkDelay = chunkDelay;
            _toolCall = toolCall;
        }

        public string Name => "MockStreamingAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse
            {
                Text = string.Concat(_chunks)
            });
        }

        public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(
            string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < _chunks.Length; i++)
            {
                if (_chunkDelay > 0 && i > 0)
                {
                    await Task.Delay(_chunkDelay, cancellationToken);
                }

                // Create chunk with optional tool call (init-only property)
                var chunk = new AgentResponseChunk
                {
                    Text = _chunks[i],
                    IsComplete = i == _chunks.Length - 1,
                    ToolCallStarted = (i == 0) ? _toolCall : null
                };

                yield return chunk;
            }
        }
    }

    #endregion

    #region Streaming Trace Helpers

    private static AgentTrace CreateStreamingTrace(string prompt, string[] chunks, int chunkDelayMs = 0)
    {
        var streamingChunks = chunks.Select((text, index) => new TraceStreamChunk
        {
            Index = index,
            Text = text,
            DelayMs = index > 0 ? chunkDelayMs : 0
        }).ToList();

        return new AgentTrace
        {
            TraceName = "streaming_trace",
            AgentName = "TestAgent",
            Entries = new List<TraceEntry>
            {
                new() { Type = TraceEntryType.Request, Index = 0, Prompt = prompt },
                new() 
                { 
                    Type = TraceEntryType.Response, 
                    Index = 0, 
                    Text = string.Concat(chunks),
                    IsStreaming = true,
                    StreamingChunks = streamingChunks
                }
            }
        };
    }

    #endregion
}
