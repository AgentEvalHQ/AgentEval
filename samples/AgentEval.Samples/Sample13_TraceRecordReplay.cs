// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Sample 13: Trace Record &amp; Replay
/// 
/// Demonstrates how to record agent executions to trace files and replay them
/// for deterministic testing without calling external AI services.
/// 
/// Features covered:
/// - Single-agent trace recording and replay
/// - Multi-turn chat trace recording and replay
/// - Workflow trace recording and replay
/// - Streaming with timing preservation
/// - Trace serialization to JSON files
/// </summary>
public static class Sample13_TraceRecordReplay
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║           SAMPLE 13: TRACE RECORD & REPLAY                   ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

        await DemoSingleAgentRecordReplay();
        await DemoMultiTurnChatRecordReplay();
        await DemoWorkflowRecordReplay();
        await DemoStreamingRecordReplay();

        Console.WriteLine("\n✅ Sample 13 complete!\n");
    }

    /// <summary>
    /// Demo 1: Single Agent Record &amp; Replay
    /// Record a single agent invocation and replay it without calling the AI.
    /// </summary>
    private static async Task DemoSingleAgentRecordReplay()
    {
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ Demo 1: Single Agent Record & Replay                        │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘\n");

        // Create a mock agent that simulates tool calls
        var mockAgent = new MockToolAgent();

        // Step 1: RECORD the execution
        Console.WriteLine("📼 RECORDING execution...\n");
        
        await using (var recorder = new TraceRecordingAgent(mockAgent, "weather_query"))
        {
            var response = await recorder.InvokeAsync("What's the weather in Seattle?");
            
            Console.WriteLine($"   Prompt: What's the weather in Seattle?");
            Console.WriteLine($"   Response: {response.Text}");
            Console.WriteLine($"   Tool calls recorded: {recorder.Trace.Entries.Count(e => e.ToolCalls?.Count > 0)}");
            
            // Save trace to file
            var traceJson = await recorder.ToJsonAsync();
            Console.WriteLine($"\n   📁 Trace saved ({traceJson.Length} bytes)");
        }

        // Step 2: REPLAY the execution (no AI calls!)
        Console.WriteLine("\n▶️  REPLAYING execution (no AI service calls)...\n");
        
        // Create trace for replay
        var replayTrace = new AgentTrace
        {
            TraceName = "weather_query_replay",
            AgentName = "MockToolAgent",
            Entries = new List<TraceEntry>
            {
                new() { Type = TraceEntryType.Request, Index = 0, Prompt = "What's the weather in Seattle?" },
                new() 
                { 
                    Type = TraceEntryType.Response, 
                    Index = 0, 
                    Text = "The weather in Seattle is 58°F with light rain.",
                    ToolCalls = new List<TraceToolCall>
                    {
                        new() { Name = "GetWeather", Arguments = "{\"location\":\"Seattle\"}", Result = "58°F, light rain", Succeeded = true }
                    }
                }
            }
        };

        var replayer = new TraceReplayingAgent(replayTrace);
        var replayedResponse = await replayer.InvokeAsync("What's the weather in Seattle?");

        Console.WriteLine($"   Prompt: What's the weather in Seattle?");
        Console.WriteLine($"   Replayed Response: {replayedResponse.Text}");
        Console.WriteLine($"   Current replay index: {replayer.CurrentIndex} of {replayer.TotalPairs}");

        Console.WriteLine("\n   ✓ Same result without calling AI service!\n");
    }

    /// <summary>
    /// Demo 2: Multi-Turn Chat Record &amp; Replay
    /// Record a conversation and replay it for testing.
    /// </summary>
    private static async Task DemoMultiTurnChatRecordReplay()
    {
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ Demo 2: Multi-Turn Chat Record & Replay                     │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘\n");

        var mockAgent = new MockToolAgent();

        // Step 1: RECORD the conversation
        Console.WriteLine("📼 RECORDING multi-turn conversation...\n");

        await using var recorder = new ChatTraceRecorder(mockAgent, "travel_planning");
        
        // Turn 1
        var response1 = await recorder.AddUserTurnAsync("I want to plan a trip to Paris");
        Console.WriteLine($"   User: I want to plan a trip to Paris");
        Console.WriteLine($"   Agent: {response1}\n");

        // Turn 2
        var response2 = await recorder.AddUserTurnAsync("What hotels do you recommend?");
        Console.WriteLine($"   User: What hotels do you recommend?");
        Console.WriteLine($"   Agent: {response2}\n");

        // Turn 3
        var response3 = await recorder.AddUserTurnAsync("Book the first one please");
        Console.WriteLine($"   User: Book the first one please");
        Console.WriteLine($"   Agent: {response3}\n");

        // Get the chat result
        var chatResult = recorder.GetResult();
        Console.WriteLine($"   📊 Recorded {chatResult.TotalTurnCount} turns");
        Console.WriteLine($"   📊 Total tokens: {chatResult.AggregatePerformance?.TotalTokens ?? 0}");

        // Convert to AgentTrace for replay
        var trace = recorder.ToAgentTrace();
        Console.WriteLine($"   📁 Trace has {trace.Entries.Count} entries\n");

        // Step 2: REPLAY for verification
        Console.WriteLine("▶️  Replaying conversation for testing...\n");
        
        var chatReplayer = new TraceReplayingAgent(trace);
        
        // Replay each turn
        var r1 = await chatReplayer.InvokeAsync("I want to plan a trip to Paris");
        Console.WriteLine($"   Replay turn 1: {r1.Text[..Math.Min(50, r1.Text.Length)]}...");
        
        var r2 = await chatReplayer.InvokeAsync("What hotels do you recommend?");
        Console.WriteLine($"   Replay turn 2: {r2.Text[..Math.Min(50, r2.Text.Length)]}...");
        
        var r3 = await chatReplayer.InvokeAsync("Book the first one please");
        Console.WriteLine($"   Replay turn 3: {r3.Text[..Math.Min(50, r3.Text.Length)]}...");

        Console.WriteLine("\n   ✓ Multi-turn conversation replayed successfully!\n");
    }

    /// <summary>
    /// Demo 3: Workflow Record &amp; Replay
    /// Record a multi-step workflow and replay it.
    /// </summary>
    private static async Task DemoWorkflowRecordReplay()
    {
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ Demo 3: Workflow Record & Replay                            │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘\n");

        var mockWorkflow = new MockWorkflowAgent();

        // Step 1: RECORD the workflow execution
        Console.WriteLine("📼 RECORDING workflow execution...\n");

        await using (var recorder = new WorkflowTraceRecorder(mockWorkflow, "research_workflow"))
        {
            var result = await recorder.ExecuteWorkflowAsync("Research the best practices for AI testing");

            Console.WriteLine($"   Prompt: Research the best practices for AI testing");
            Console.WriteLine($"   Final Output: {result.FinalOutput[..Math.Min(60, result.FinalOutput.Length)]}...");
            Console.WriteLine($"   Steps recorded: {result.Steps.Count}");

            foreach (var step in result.Steps)
            {
                Console.WriteLine($"     • {step.ExecutorId}: {step.Output?[..Math.Min(40, step.Output?.Length ?? 0)]}...");
            }

            // Show trace summary
            var trace = recorder.Trace;
            Console.WriteLine($"\n   📊 Trace Performance:");
            Console.WriteLine($"      Total duration: {trace.Performance?.TotalDurationMs}ms");
            Console.WriteLine($"      Total tokens: {trace.Performance?.TotalTokens}");
            Console.WriteLine($"      Tool calls: {trace.Performance?.TotalToolCalls}");
        }

        // Step 2: REPLAY the workflow
        Console.WriteLine("\n▶️  REPLAYING workflow...\n");

        // Create a trace for replay
        var replayTrace = new WorkflowTrace
        {
            TraceName = "research_workflow_replay",
            OriginalPrompt = "Research the best practices for AI testing",
            FinalOutput = "Here are the best practices for AI testing: 1) Use deterministic tests where possible, 2) Record and replay for consistency, 3) Measure tool call accuracy.",
            Steps = new List<WorkflowTraceStep>
            {
                new() 
                { 
                    ExecutorId = "researcher",
                    ExecutorName = "Research Agent",
                    Input = "Research AI testing",
                    Output = "Found 10 articles on AI testing best practices",
                    StepIndex = 0,
                    DurationMs = 500,
                    ToolCalls = new List<TraceToolCall>
                    {
                        new() { Name = "WebSearch", Result = "10 results", Succeeded = true }
                    }
                },
                new()
                {
                    ExecutorId = "summarizer",
                    ExecutorName = "Summarizer Agent",
                    Input = "Summarize findings",
                    Output = "Key practices: deterministic tests, record/replay, tool accuracy",
                    StepIndex = 1,
                    DurationMs = 300
                }
            },
            Performance = new WorkflowTracePerformance
            {
                TotalDurationMs = 800,
                StepCount = 2,
                TotalToolCalls = 1,
                TotalPromptTokens = 300,
                TotalCompletionTokens = 200
            }
        };

        var workflowReplayer = new WorkflowTraceReplayingAgent(replayTrace);
        var replayedResult = await workflowReplayer.ExecuteWorkflowAsync("Research the best practices for AI testing");

        Console.WriteLine($"   Replayed {replayedResult.Steps.Count} steps:");
        foreach (var step in replayedResult.Steps)
        {
            Console.WriteLine($"     • {step.ExecutorId}: {step.Output?[..Math.Min(50, step.Output?.Length ?? 0)]}...");
        }
        Console.WriteLine($"   Final: {replayedResult.FinalOutput[..Math.Min(60, replayedResult.FinalOutput.Length)]}...");

        Console.WriteLine("\n   ✓ Workflow replayed successfully!\n");
    }

    /// <summary>
    /// Demo 4: Streaming Record &amp; Replay
    /// Record streaming responses with chunk timing and replay them.
    /// </summary>
    private static async Task DemoStreamingRecordReplay()
    {
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ Demo 4: Streaming Record & Replay                           │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘\n");

        var mockStreamingAgent = new MockStreamingAgent();

        // Step 1: RECORD streaming execution
        Console.WriteLine("📼 RECORDING streaming execution...\n");

        await using (var recorder = new TraceRecordingAgent(mockStreamingAgent, "streaming_demo"))
        {
            Console.Write("   Response: ");
            await foreach (var chunk in recorder.InvokeStreamingAsync("Tell me a short joke"))
            {
                Console.Write(chunk.Text);
                await Task.Delay(10); // Simulate display
            }
            Console.WriteLine();

            var trace = recorder.Trace;
            var responseEntry = trace.Entries.FirstOrDefault(e => e.Type == TraceEntryType.Response);
            Console.WriteLine($"\n   📊 Recorded {responseEntry?.StreamingChunks?.Count ?? 0} chunks");
            Console.WriteLine($"   📊 Time to first token: {trace.Performance?.TimeToFirstTokenMs}ms");
        }

        // Step 2: REPLAY with timing simulation
        Console.WriteLine("\n▶️  REPLAYING streaming with timing...\n");

        var streamingTrace = new AgentTrace
        {
            TraceName = "streaming_replay",
            AgentName = "MockStreamingAgent",
            Entries = new List<TraceEntry>
            {
                new() { Type = TraceEntryType.Request, Index = 0, Prompt = "Tell me a short joke" },
                new()
                {
                    Type = TraceEntryType.Response,
                    Index = 0,
                    Text = "Why did the developer go broke? Because he used up all his cache!",
                    IsStreaming = true,
                    StreamingChunks = new List<TraceStreamChunk>
                    {
                        new() { Index = 0, Text = "Why did ", DelayMs = 0 },
                        new() { Index = 1, Text = "the developer ", DelayMs = 50 },
                        new() { Index = 2, Text = "go broke? ", DelayMs = 50 },
                        new() { Index = 3, Text = "Because he ", DelayMs = 50 },
                        new() { Index = 4, Text = "used up all ", DelayMs = 50 },
                        new() { Index = 5, Text = "his cache!", DelayMs = 50 }
                    }
                }
            },
            Performance = new TracePerformance
            {
                TimeToFirstTokenMs = 100,
                TotalDurationMs = 350
            }
        };

        var streamReplayer = new TraceReplayingAgent(streamingTrace, new TraceReplayOptions
        {
            SimulateStreamingDelay = true // Replay with original timing
        });

        Console.Write("   Replay: ");
        await foreach (var chunk in streamReplayer.InvokeStreamingAsync("Tell me a short joke"))
        {
            Console.Write(chunk.Text);
        }
        Console.WriteLine();

        Console.WriteLine("\n   ✓ Streaming replayed with timing preserved!\n");
    }

    #region Mock Agents

    /// <summary>
    /// Mock agent that simulates tool calls.
    /// </summary>
    private class MockToolAgent : IEvaluableAgent
    {
        private int _callCount;

        public string Name => "MockToolAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            _callCount++;

            // Simulate different responses based on prompt
            var response = prompt.ToLowerInvariant() switch
            {
                var p when p.Contains("weather") => "The weather in Seattle is 58°F with light rain.",
                var p when p.Contains("paris") || p.Contains("trip") => "Great choice! Paris is beautiful. Let me help you plan your trip.",
                var p when p.Contains("hotel") => "I recommend Hotel Le Bristol Paris - a 5-star hotel near the Champs-Élysées.",
                var p when p.Contains("book") => "I've booked Hotel Le Bristol for you. Confirmation #12345.",
                _ => "I'm here to help with your request."
            };

            return Task.FromResult(new AgentResponse
            {
                Text = response,
                TokenUsage = new TokenUsage { PromptTokens = 50, CompletionTokens = 30 }
            });
        }
    }

    /// <summary>
    /// Mock streaming agent.
    /// </summary>
    private class MockStreamingAgent : IEvaluableAgent, IStreamableAgent
    {
        public string Name => "MockStreamingAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse
            {
                Text = "Why did the developer go broke? Because he used up all his cache!"
            });
        }

        public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(
            string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var chunks = new[] { "Why did ", "the developer ", "go broke? ", "Because he ", "used up all ", "his cache!" };
            
            for (int i = 0; i < chunks.Length; i++)
            {
                if (i > 0) await Task.Delay(50, cancellationToken);
                
                yield return new AgentResponseChunk
                {
                    Text = chunks[i],
                    IsComplete = i == chunks.Length - 1
                };
            }
        }
    }

    /// <summary>
    /// Mock workflow agent.
    /// </summary>
    private class MockWorkflowAgent : IWorkflowEvaluableAgent
    {
        public string Name => "MockWorkflowAgent";
        public IReadOnlyList<string> ExecutorIds => new[] { "researcher", "summarizer" };
        public string? WorkflowType => "Research";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse
            {
                Text = "Research complete. Here are the best practices for AI testing."
            });
        }

        public Task<WorkflowExecutionResult> ExecuteWorkflowAsync(string prompt, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WorkflowExecutionResult
            {
                FinalOutput = "Here are the best practices for AI testing: 1) Use deterministic tests, 2) Record and replay, 3) Measure accuracy.",
                Steps = new List<ExecutorStep>
                {
                    new()
                    {
                        ExecutorId = "researcher",
                        ExecutorName = "Research Agent",
                        Input = "Research AI testing best practices",
                        Output = "Found 10 relevant articles",
                        StepIndex = 0,
                        Duration = TimeSpan.FromMilliseconds(500),
                        ToolCalls = new List<ToolCallRecord>
                        {
                            new() { Name = "WebSearch", CallId = "ws_1", Result = "10 results" }
                        }
                    },
                    new()
                    {
                        ExecutorId = "summarizer",
                        ExecutorName = "Summarizer Agent",
                        Input = "Summarize the research findings",
                        Output = "Key practices: determinism, record/replay, accuracy metrics",
                        StepIndex = 1,
                        Duration = TimeSpan.FromMilliseconds(300)
                    }
                },
                TotalDuration = TimeSpan.FromMilliseconds(800)
            });
        }
    }

    #endregion
}
