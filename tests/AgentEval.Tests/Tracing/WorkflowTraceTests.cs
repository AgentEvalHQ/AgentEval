// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Tracing;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// Tests for WorkflowTraceRecorder and WorkflowTraceReplayingAgent.
/// </summary>
public class WorkflowTraceTests
{
    #region WorkflowTraceRecorder Tests

    [Fact]
    public async Task WorkflowTraceRecorder_RecordsBasicExecution()
    {
        // Arrange
        var mockWorkflow = new MockWorkflowAgent(new WorkflowExecutionResult
        {
            FinalOutput = "Final result from workflow",
            Steps = new List<ExecutorStep>
            {
                new()
                {
                    ExecutorId = "step1",
                    ExecutorName = "Step One",
                    Output = "Step 1 output",
                    StepIndex = 0,
                    Duration = TimeSpan.FromMilliseconds(100)
                },
                new()
                {
                    ExecutorId = "step2",
                    Output = "Step 2 output",
                    StepIndex = 1,
                    Duration = TimeSpan.FromMilliseconds(200)
                }
            },
            TotalDuration = TimeSpan.FromMilliseconds(300)
        });

        await using var recorder = new WorkflowTraceRecorder(mockWorkflow, "test_workflow");

        // Act
        var result = await recorder.ExecuteWorkflowAsync("Test prompt");

        // Assert
        Assert.Equal("Final result from workflow", result.FinalOutput);
        Assert.Equal(2, result.Steps.Count);

        var trace = recorder.Trace;
        Assert.Equal("test_workflow", trace.TraceName);
        Assert.Equal("Test prompt", trace.OriginalPrompt);
        Assert.Equal("Final result from workflow", trace.FinalOutput);
        Assert.Equal(2, trace.Steps.Count);
    }

    [Fact]
    public async Task WorkflowTraceRecorder_RecordsStepDetails()
    {
        // Arrange
        var toolCalls = new List<ToolCallRecord>
        {
            new() { Name = "SearchTool", CallId = "call_1", Result = "Search result" }
        };

        var mockWorkflow = new MockWorkflowAgent(new WorkflowExecutionResult
        {
            FinalOutput = "Done",
            Steps = new List<ExecutorStep>
            {
                new()
                {
                    ExecutorId = "researcher",
                    ExecutorName = "Researcher Agent",
                    Input = "Research this topic",
                    Output = "Research findings",
                    StepIndex = 0,
                    StartOffset = TimeSpan.Zero,
                    Duration = TimeSpan.FromMilliseconds(500),
                    ToolCalls = toolCalls,
                    TokenUsage = new TokenUsage { PromptTokens = 100, CompletionTokens = 50 }
                }
            },
            TotalDuration = TimeSpan.FromMilliseconds(500)
        });

        await using var recorder = new WorkflowTraceRecorder(mockWorkflow, "detailed_workflow");

        // Act
        await recorder.ExecuteWorkflowAsync("Research AI");

        // Assert
        var trace = recorder.Trace;
        var step = trace.Steps[0];

        Assert.Equal("researcher", step.ExecutorId);
        Assert.Equal("Researcher Agent", step.ExecutorName);
        Assert.Equal("Research this topic", step.Input);
        Assert.Equal("Research findings", step.Output);
        Assert.Equal(500, step.DurationMs);
        Assert.NotNull(step.ToolCalls);
        Assert.Single(step.ToolCalls);
        Assert.Equal("SearchTool", step.ToolCalls[0].Name);
        Assert.NotNull(step.TokenUsage);
        Assert.Equal(100, step.TokenUsage.PromptTokens);
        Assert.Equal(50, step.TokenUsage.CompletionTokens);
    }

    [Fact]
    public async Task WorkflowTraceRecorder_RecordsPerformanceMetrics()
    {
        // Arrange
        var mockWorkflow = new MockWorkflowAgent(new WorkflowExecutionResult
        {
            FinalOutput = "Done",
            Steps = new List<ExecutorStep>
            {
                new()
                {
                    ExecutorId = "step1", Output = "Out1", StepIndex = 0,
                    Duration = TimeSpan.FromMilliseconds(100),
                    TokenUsage = new TokenUsage { PromptTokens = 50, CompletionTokens = 25 },
                    ToolCalls = new List<ToolCallRecord>
                    {
                        new() { Name = "Tool1", CallId = "c1" },
                        new() { Name = "Tool2", CallId = "c2" }
                    }
                },
                new()
                {
                    ExecutorId = "step2", Output = "Out2", StepIndex = 1,
                    Duration = TimeSpan.FromMilliseconds(200),
                    TokenUsage = new TokenUsage { PromptTokens = 100, CompletionTokens = 75 },
                    ToolCalls = new List<ToolCallRecord>
                    {
                        new() { Name = "Tool3", CallId = "c3" }
                    }
                }
            },
            TotalDuration = TimeSpan.FromMilliseconds(300)
        });

        await using var recorder = new WorkflowTraceRecorder(mockWorkflow, "perf_workflow");

        // Act
        await recorder.ExecuteWorkflowAsync("Test");

        // Assert
        var perf = recorder.Trace.Performance!;
        Assert.Equal(300, perf.TotalDurationMs);
        Assert.Equal(2, perf.StepCount);
        Assert.Equal(3, perf.TotalToolCalls);
        Assert.Equal(150, perf.TotalPromptTokens);
        Assert.Equal(100, perf.TotalCompletionTokens);
        Assert.Equal(250, perf.TotalTokens);
        Assert.NotNull(perf.DurationByExecutor);
        Assert.Equal(100, perf.DurationByExecutor["step1"]);
        Assert.Equal(200, perf.DurationByExecutor["step2"]);
    }

    [Fact]
    public async Task WorkflowTraceRecorder_RecordsRoutingDecisions()
    {
        // Arrange
        var routingDecisions = new List<RoutingDecision>
        {
            new()
            {
                DeciderExecutorId = "router",
                PossibleEdgeIds = new[] { "edge1", "edge2" },
                SelectedEdgeId = "edge1",
                EvaluatedValue = "condition > 5",
                SelectionReason = "Value was 7",
                DecisionTime = TimeSpan.FromMilliseconds(50)
            }
        };

        var mockWorkflow = new MockWorkflowAgent(new WorkflowExecutionResult
        {
            FinalOutput = "Routed result",
            Steps = new List<ExecutorStep>
            {
                new() { ExecutorId = "router", Output = "7", StepIndex = 0 },
                new() { ExecutorId = "handler", Output = "Handled", StepIndex = 1 }
            },
            TotalDuration = TimeSpan.FromMilliseconds(100),
            RoutingDecisions = routingDecisions
        });

        await using var recorder = new WorkflowTraceRecorder(mockWorkflow, "routing_workflow");

        // Act
        await recorder.ExecuteWorkflowAsync("Test routing");

        // Assert
        var trace = recorder.Trace;
        Assert.NotNull(trace.RoutingDecisions);
        Assert.Single(trace.RoutingDecisions);
        var rd = trace.RoutingDecisions[0];
        Assert.Equal("router", rd.RouterId);
        Assert.Equal("edge1", rd.SelectedExecutorId);
        Assert.Equal("condition > 5", rd.Condition);
        Assert.Equal("Value was 7", rd.Result);
    }

    [Fact]
    public async Task WorkflowTraceRecorder_SavesAndLoadsTrace()
    {
        // Arrange
        var mockWorkflow = new MockWorkflowAgent(new WorkflowExecutionResult
        {
            FinalOutput = "Saved result",
            Steps = new List<ExecutorStep>
            {
                new() { ExecutorId = "step1", Output = "Out1", StepIndex = 0, Duration = TimeSpan.FromMilliseconds(100) }
            },
            TotalDuration = TimeSpan.FromMilliseconds(100)
        });

        var tempFile = Path.GetTempFileName() + ".trace.json";

        try
        {
            await using (var recorder = new WorkflowTraceRecorder(mockWorkflow, "save_workflow"))
            {
                await recorder.ExecuteWorkflowAsync("Save me");
                await recorder.SaveAsync(tempFile);
            }

            // Act - Load the trace
            var loadedTrace = await WorkflowTraceSerializer.LoadFromFileAsync(tempFile);

            // Assert
            Assert.Equal("save_workflow", loadedTrace.TraceName);
            Assert.Equal("Save me", loadedTrace.OriginalPrompt);
            Assert.Equal("Saved result", loadedTrace.FinalOutput);
            Assert.Single(loadedTrace.Steps);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    #endregion

    #region WorkflowTraceReplayingAgent Tests

    [Fact]
    public async Task WorkflowTraceReplayingAgent_ReplaysBasicExecution()
    {
        // Arrange
        var trace = new WorkflowTrace
        {
            TraceName = "replay_test",
            OriginalPrompt = "Original prompt",
            FinalOutput = "Replayed result",
            Steps = new List<WorkflowTraceStep>
            {
                new() { ExecutorId = "step1", Output = "Step 1 output", StepIndex = 0, DurationMs = 100 },
                new() { ExecutorId = "step2", Output = "Step 2 output", StepIndex = 1, DurationMs = 200 }
            },
            Performance = new WorkflowTracePerformance { TotalDurationMs = 300, StepCount = 2 }
        };

        var replayer = new WorkflowTraceReplayingAgent(trace);

        // Act
        var result = await replayer.ExecuteWorkflowAsync("Any prompt");

        // Assert
        Assert.Equal("Replayed result", result.FinalOutput);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal("step1", result.Steps[0].ExecutorId);
        Assert.Equal("Step 1 output", result.Steps[0].Output);
        Assert.Equal(1, replayer.ExecutionCount);
    }

    [Fact]
    public async Task WorkflowTraceReplayingAgent_ReplaysStepDetails()
    {
        // Arrange
        var trace = new WorkflowTrace
        {
            TraceName = "detailed_replay",
            FinalOutput = "Done",
            Steps = new List<WorkflowTraceStep>
            {
                new()
                {
                    ExecutorId = "researcher",
                    ExecutorName = "Research Agent",
                    Input = "Research input",
                    Output = "Research output",
                    StepIndex = 0,
                    StartOffsetMs = 0,
                    DurationMs = 500,
                    ToolCalls = new List<TraceToolCall>
                    {
                        new() { Name = "SearchTool", Result = "Found it", Succeeded = true }
                    },
                    TokenUsage = new TraceTokenUsage { PromptTokens = 100, CompletionTokens = 50 }
                }
            },
            Performance = new WorkflowTracePerformance { TotalDurationMs = 500, StepCount = 1 }
        };

        var replayer = new WorkflowTraceReplayingAgent(trace);

        // Act
        var result = await replayer.ExecuteWorkflowAsync("Test");

        // Assert
        var step = result.Steps[0];
        Assert.Equal("researcher", step.ExecutorId);
        Assert.Equal("Research Agent", step.ExecutorName);
        Assert.Equal("Research input", step.Input);
        Assert.Equal("Research output", step.Output);
        Assert.Equal(TimeSpan.FromMilliseconds(500), step.Duration);
        Assert.NotNull(step.ToolCalls);
        Assert.Single(step.ToolCalls);
        Assert.Equal("SearchTool", step.ToolCalls[0].Name);
        Assert.NotNull(step.TokenUsage);
        Assert.Equal(100, step.TokenUsage.PromptTokens);
    }

    [Fact]
    public async Task WorkflowTraceReplayingAgent_ValidatesExactPrompt()
    {
        // Arrange
        var trace = new WorkflowTrace
        {
            TraceName = "exact_match",
            OriginalPrompt = "Expected prompt",
            FinalOutput = "Result",
            Steps = new List<WorkflowTraceStep>
            {
                new() { ExecutorId = "step1", Output = "Out", StepIndex = 0 }
            }
        };

        var options = new WorkflowTraceReplayOptions
        {
            ValidatePrompt = true,
            PromptMatchingMode = PromptMatchingMode.Exact,
            MismatchBehavior = MismatchBehavior.Throw
        };

        var replayer = new WorkflowTraceReplayingAgent(trace, options);

        // Act & Assert - Exact match should work
        var result = await replayer.ExecuteWorkflowAsync("Expected prompt");
        Assert.Equal("Result", result.FinalOutput);

        // Act & Assert - Mismatch should throw
        replayer.Reset();
        await Assert.ThrowsAsync<WorkflowTraceReplayMismatchException>(
            () => replayer.ExecuteWorkflowAsync("Different prompt"));
    }

    [Fact]
    public async Task WorkflowTraceReplayingAgent_SimulatesExecutionDelay()
    {
        // Arrange
        var trace = new WorkflowTrace
        {
            TraceName = "delay_test",
            FinalOutput = "Result",
            Steps = new List<WorkflowTraceStep>
            {
                new() { ExecutorId = "step1", Output = "Out", StepIndex = 0 }
            },
            Performance = new WorkflowTracePerformance { TotalDurationMs = 500, StepCount = 1 }
        };

        var options = new WorkflowTraceReplayOptions
        {
            SimulateExecutionDelay = true,
            DelayMultiplier = 0.1 // 50ms simulated delay (500 * 0.1)
        };

        var replayer = new WorkflowTraceReplayingAgent(trace, options);

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await replayer.ExecuteWorkflowAsync("Test");
        sw.Stop();

        // Assert - Should have some delay (at least 40ms to account for timing variance)
        Assert.True(sw.ElapsedMilliseconds >= 40, $"Expected delay, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task WorkflowTraceReplayingAgent_RoundTripRecordReplay()
    {
        // Arrange
        var originalWorkflow = new MockWorkflowAgent(new WorkflowExecutionResult
        {
            FinalOutput = "Round trip result",
            Steps = new List<ExecutorStep>
            {
                new()
                {
                    ExecutorId = "processor",
                    ExecutorName = "Data Processor",
                    Input = "Raw data",
                    Output = "Processed data",
                    StepIndex = 0,
                    Duration = TimeSpan.FromMilliseconds(250),
                    ToolCalls = new List<ToolCallRecord>
                    {
                        new() { Name = "TransformTool", CallId = "t1", Result = "Transformed" }
                    },
                    TokenUsage = new TokenUsage { PromptTokens = 200, CompletionTokens = 100 }
                }
            },
            TotalDuration = TimeSpan.FromMilliseconds(250)
        });

        // Record
        WorkflowTrace recordedTrace;
        await using (var recorder = new WorkflowTraceRecorder(originalWorkflow, "round_trip"))
        {
            await recorder.ExecuteWorkflowAsync("Process this data");
            recordedTrace = recorder.Trace;
        }

        // Replay
        var replayer = new WorkflowTraceReplayingAgent(recordedTrace);
        var result = await replayer.ExecuteWorkflowAsync("Process this data");

        // Assert - Replayed result matches original
        Assert.Equal("Round trip result", result.FinalOutput);
        Assert.Single(result.Steps);
        Assert.Equal("processor", result.Steps[0].ExecutorId);
        Assert.Equal("Data Processor", result.Steps[0].ExecutorName);
        Assert.Equal("Processed data", result.Steps[0].Output);
        Assert.NotNull(result.Steps[0].ToolCalls);
        Assert.Single(result.Steps[0].ToolCalls!);
        Assert.Equal("TransformTool", result.Steps[0].ToolCalls![0].Name);
    }

    [Fact]
    public async Task WorkflowTraceReplayingAgent_FromFileAsync_LoadsAndReplays()
    {
        // Arrange
        var trace = new WorkflowTrace
        {
            TraceName = "file_load_test",
            OriginalPrompt = "Load test",
            FinalOutput = "Loaded result",
            Steps = new List<WorkflowTraceStep>
            {
                new() { ExecutorId = "loader", Output = "Loaded", StepIndex = 0 }
            },
            Performance = new WorkflowTracePerformance { TotalDurationMs = 100, StepCount = 1 }
        };

        var tempFile = Path.GetTempFileName() + ".trace.json";

        try
        {
            await WorkflowTraceSerializer.SaveToFileAsync(trace, tempFile);

            // Act
            var replayer = await WorkflowTraceReplayingAgent.FromFileAsync(tempFile);
            var result = await replayer.ExecuteWorkflowAsync("Test");

            // Assert
            Assert.Equal("Loaded result", result.FinalOutput);
            Assert.Equal("file_load_test", replayer.Trace.TraceName);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task WorkflowTraceReplayingAgent_ReplaysGraphStructure()
    {
        // Arrange
        var trace = new WorkflowTrace
        {
            TraceName = "graph_test",
            FinalOutput = "Graph result",
            Steps = new List<WorkflowTraceStep>
            {
                new() { ExecutorId = "entry", Output = "Start", StepIndex = 0 },
                new() { ExecutorId = "process", Output = "Middle", StepIndex = 1 },
                new() { ExecutorId = "exit", Output = "End", StepIndex = 2 }
            },
            Graph = new WorkflowTraceGraph
            {
                Nodes = new List<string> { "entry", "process", "exit" },
                EntryPoint = "entry",
                ExitPoints = new List<string> { "exit" },
                HasConditionalRouting = false,
                HasParallelExecution = false,
                Edges = new List<WorkflowTraceEdge>
                {
                    new() { From = "entry", To = "process", EdgeType = "Sequential" },
                    new() { From = "process", To = "exit", EdgeType = "Sequential" }
                }
            },
            Performance = new WorkflowTracePerformance { TotalDurationMs = 300, StepCount = 3 }
        };

        var replayer = new WorkflowTraceReplayingAgent(trace);

        // Act
        var result = await replayer.ExecuteWorkflowAsync("Test");

        // Assert
        Assert.NotNull(result.Graph);
        Assert.Equal(3, result.Graph.Nodes.Count);
        Assert.Equal("entry", result.Graph.EntryNodeId);
        Assert.Contains("exit", result.Graph.ExitNodeIds);
        Assert.Equal(2, result.Graph.Edges.Count);
    }

    #endregion

    #region Helper Classes

    private class MockWorkflowAgent : IWorkflowEvaluableAgent
    {
        private readonly WorkflowExecutionResult _result;

        public MockWorkflowAgent(WorkflowExecutionResult result)
        {
            _result = result;
        }

        public string Name => "MockWorkflow";
        public IReadOnlyList<string> ExecutorIds => _result.Steps.Select(s => s.ExecutorId).ToList();
        public string? WorkflowType => "MockType";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse { Text = _result.FinalOutput });
        }

        public Task<WorkflowExecutionResult> ExecuteWorkflowAsync(string prompt, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }
    }

    #endregion
}
