// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;
using AgentEval.Models;
using AgentEval.Assertions;
using AgentEval.MAF;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Unit tests for workflow tool tracking:
/// - WorkflowExecutionResult.ToolUsage and Timeline computed properties
/// - Workflow-level tool assertions (HaveAnyExecutorCalledTool, HaveTotalToolCallCount)
/// - Per-executor tool assertions with arguments (HaveCalledToolWithArgument)
/// - ToolCallRecord.ExecutorId propagation
/// - WorkflowTestCase.ExpectedTools and PerExecutorExpectedTools
/// </summary>
public class WorkflowToolTrackingTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // HELPER: Build WorkflowExecutionResult with tool calls
    // ═══════════════════════════════════════════════════════════════════════════

    private static WorkflowExecutionResult CreateResultWithTools(
        params (string executorId, string output, (string toolName, string callId, object? result)[] tools)[] steps)
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = steps.LastOrDefault().output ?? "",
            Steps = steps.Select((s, i) => new ExecutorStep
            {
                ExecutorId = s.executorId,
                Output = s.output,
                StepIndex = i,
                Duration = TimeSpan.FromMilliseconds(500 * (i + 1)),
                StartOffset = TimeSpan.FromMilliseconds(500 * i),
                ToolCalls = s.tools.Length > 0
                    ? s.tools.Select((t, ti) => new ToolCallRecord
                    {
                        Name = t.toolName,
                        CallId = t.callId,
                        Result = t.result,
                        Order = ti + 1,
                        ExecutorId = s.executorId,
                        StartTime = DateTimeOffset.UtcNow.AddMilliseconds(-100),
                        EndTime = DateTimeOffset.UtcNow
                    }).ToList()
                    : null
            }).ToList(),
            TotalDuration = TimeSpan.FromSeconds(steps.Length),
            OriginalPrompt = "test prompt"
        };
    }

    private static WorkflowExecutionResult CreateResultWithToolArgs(
        string executorId,
        (string toolName, string callId, IDictionary<string, object?> args)[] tools)
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps =
            [
                new ExecutorStep
                {
                    ExecutorId = executorId,
                    Output = "output",
                    StepIndex = 0,
                    Duration = TimeSpan.FromSeconds(1),
                    ToolCalls = tools.Select((t, i) => new ToolCallRecord
                    {
                        Name = t.toolName,
                        CallId = t.callId,
                        Arguments = t.args,
                        Order = i + 1,
                        ExecutorId = executorId
                    }).ToList()
                }
            ],
            TotalDuration = TimeSpan.FromSeconds(1)
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TOOLUSAGE COMPUTED PROPERTY
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ToolUsage_WithNoToolCalls_ReturnsNull()
    {
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps =
            [
                new ExecutorStep { ExecutorId = "agent1", Output = "hello", StepIndex = 0, Duration = TimeSpan.FromSeconds(1) }
            ],
            TotalDuration = TimeSpan.FromSeconds(1)
        };

        Assert.Null(result.ToolUsage);
    }

    [Fact]
    public void ToolUsage_WithToolCalls_ReturnsAggregatedReport()
    {
        var result = CreateResultWithTools(
            ("TripPlanner", "plan", [("GetInfoAbout", "c1", "Tokyo info"), ("GetInfoAbout", "c2", "Beijing info")]),
            ("FlightAgent", "flights", [("SearchFlights", "c3", "flights found"), ("BookFlight", "c4", "booked")]),
            ("Presenter", "final", []));

        var toolUsage = result.ToolUsage;

        Assert.NotNull(toolUsage);
        Assert.Equal(4, toolUsage.Count);
        Assert.Contains("GetInfoAbout", toolUsage.UniqueToolNames);
        Assert.Contains("SearchFlights", toolUsage.UniqueToolNames);
        Assert.Contains("BookFlight", toolUsage.UniqueToolNames);
        Assert.True(toolUsage.WasToolCalled("GetInfoAbout"));
        Assert.True(toolUsage.WasToolCalled("SearchFlights"));
        Assert.True(toolUsage.WasToolCalled("BookFlight"));
        Assert.False(toolUsage.WasToolCalled("NonExistentTool"));
    }

    [Fact]
    public void ToolUsage_HasGlobalOrderAcrossExecutors()
    {
        var result = CreateResultWithTools(
            ("Agent1", "out1", [("ToolA", "c1", "r1")]),
            ("Agent2", "out2", [("ToolB", "c2", "r2"), ("ToolC", "c3", "r3")]));

        var toolUsage = result.ToolUsage!;

        Assert.Equal(3, toolUsage.Count);
        var orders = toolUsage.Calls.Select(c => c.Order).ToList();
        Assert.Equal([1, 2, 3], orders);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TIMELINE COMPUTED PROPERTY
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Timeline_WithNoToolCalls_ReturnsNull()
    {
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps =
            [
                new ExecutorStep { ExecutorId = "agent1", Output = "hello", StepIndex = 0, Duration = TimeSpan.FromSeconds(1) }
            ],
            TotalDuration = TimeSpan.FromSeconds(1)
        };

        Assert.Null(result.Timeline);
    }

    [Fact]
    public void Timeline_WithToolCalls_ReturnsTimeline()
    {
        var result = CreateResultWithTools(
            ("Agent1", "out1", [("ToolA", "c1", "r1")]),
            ("Agent2", "out2", [("ToolB", "c2", "r2")]));

        var timeline = result.Timeline;

        Assert.NotNull(timeline);
        Assert.Equal(2, timeline.TotalToolCalls);
        Assert.Contains(timeline.Invocations, i => i.ToolName.Contains("ToolA"));
        Assert.Contains(timeline.Invocations, i => i.ToolName.Contains("ToolB"));
    }

    [Fact]
    public void Timeline_ToolNamesIncludeExecutorId()
    {
        var result = CreateResultWithTools(
            ("TripPlanner", "out1", [("GetInfoAbout", "c1", "r1")]));

        var timeline = result.Timeline!;
        var invocation = timeline.Invocations[0];

        Assert.Equal("TripPlanner/GetInfoAbout", invocation.ToolName);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TOOLCALLRECORD.EXECUTORID
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ToolCallRecord_ExecutorId_IsPropagated()
    {
        var result = CreateResultWithTools(
            ("Agent1", "out1", [("ToolA", "c1", "r1")]),
            ("Agent2", "out2", [("ToolB", "c2", "r2")]));

        var agent1Tools = result.Steps[0].ToolCalls!;
        var agent2Tools = result.Steps[1].ToolCalls!;

        Assert.Equal("Agent1", agent1Tools[0].ExecutorId);
        Assert.Equal("Agent2", agent2Tools[0].ExecutorId);
    }

    [Fact]
    public void ToolUsage_PreservesExecutorId()
    {
        var result = CreateResultWithTools(
            ("Agent1", "out1", [("ToolA", "c1", "r1")]),
            ("Agent2", "out2", [("ToolB", "c2", "r2")]));

        var toolUsage = result.ToolUsage!;
        var toolA = toolUsage.Calls.First(c => c.Name == "ToolA");
        var toolB = toolUsage.Calls.First(c => c.Name == "ToolB");

        Assert.Equal("Agent1", toolA.ExecutorId);
        Assert.Equal("Agent2", toolB.ExecutorId);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // WORKFLOW-LEVEL TOOL ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveAnyExecutorCalledTool_WhenToolExists_DoesNotThrow()
    {
        var result = CreateResultWithTools(
            ("Agent1", "out1", [("SearchTool", "c1", "found")]),
            ("Agent2", "out2", []));

        var exception = Record.Exception(() =>
            result.Should()
                .HaveAnyExecutorCalledTool("SearchTool")
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveAnyExecutorCalledTool_CaseInsensitive_DoesNotThrow()
    {
        var result = CreateResultWithTools(
            ("Agent1", "out1", [("SearchTool", "c1", "found")]));

        var exception = Record.Exception(() =>
            result.Should()
                .HaveAnyExecutorCalledTool("searchtool")
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveAnyExecutorCalledTool_WhenMissing_ThrowsException()
    {
        var result = CreateResultWithTools(
            ("Agent1", "out1", [("ToolA", "c1", "r1")]));

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should()
                .HaveAnyExecutorCalledTool("ToolB")
                .Validate());

        Assert.Contains("ToolB", exception.Message);
        Assert.Contains("ToolA", exception.Message);
    }

    [Fact]
    public void HaveAnyExecutorCalledTool_WhenNoToolCalls_ThrowsException()
    {
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps =
            [
                new ExecutorStep { ExecutorId = "agent1", Output = "hello", StepIndex = 0, Duration = TimeSpan.FromSeconds(1) }
            ],
            TotalDuration = TimeSpan.FromSeconds(1)
        };

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should()
                .HaveAnyExecutorCalledTool("SomeTool")
                .Validate());

        Assert.Contains("SomeTool", exception.Message);
    }

    [Fact]
    public void HaveTotalToolCallCount_WithCorrectCount_DoesNotThrow()
    {
        var result = CreateResultWithTools(
            ("Agent1", "out1", [("ToolA", "c1", "r1"), ("ToolB", "c2", "r2")]),
            ("Agent2", "out2", [("ToolC", "c3", "r3")]));

        var exception = Record.Exception(() =>
            result.Should()
                .HaveTotalToolCallCount(3)
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveTotalToolCallCount_WithWrongCount_ThrowsException()
    {
        var result = CreateResultWithTools(
            ("Agent1", "out1", [("ToolA", "c1", "r1")]));

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should()
                .HaveTotalToolCallCount(5)
                .Validate());

        Assert.Contains("5", exception.Message);
        Assert.Contains("1", exception.Message);
    }

    [Fact]
    public void HaveAtLeastTotalToolCalls_WhenEnough_DoesNotThrow()
    {
        var result = CreateResultWithTools(
            ("Agent1", "out1", [("ToolA", "c1", "r1"), ("ToolB", "c2", "r2")]));

        var exception = Record.Exception(() =>
            result.Should()
                .HaveAtLeastTotalToolCalls(2)
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveAtLeastTotalToolCalls_WhenNotEnough_ThrowsException()
    {
        var result = CreateResultWithTools(
            ("Agent1", "out1", [("ToolA", "c1", "r1")]));

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should()
                .HaveAtLeastTotalToolCalls(5)
                .Validate());

        Assert.Contains("5", exception.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PER-EXECUTOR TOOL ASSERTIONS WITH ARGUMENTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveCalledToolWithArgument_WhenMatches_DoesNotThrow()
    {
        var result = CreateResultWithToolArgs("TripPlanner",
        [
            ("GetInfoAbout", "c1", new Dictionary<string, object?> { ["city"] = "Tokyo" })
        ]);

        var exception = Record.Exception(() =>
            result.Should()
                .ForExecutor("TripPlanner")
                    .HaveCalledToolWithArgument("GetInfoAbout", "city", "Tokyo")
                .And()
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveCalledToolWithArgument_CaseInsensitiveSubstring_DoesNotThrow()
    {
        var result = CreateResultWithToolArgs("Agent1",
        [
            ("SearchFlights", "c1", new Dictionary<string, object?> { ["from"] = "Tokyo Narita" })
        ]);

        var exception = Record.Exception(() =>
            result.Should()
                .ForExecutor("Agent1")
                    .HaveCalledToolWithArgument("SearchFlights", "from", "tokyo")
                .And()
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveCalledToolWithArgument_WhenToolNotCalled_ThrowsException()
    {
        var result = CreateResultWithToolArgs("Agent1",
        [
            ("ToolA", "c1", new Dictionary<string, object?> { ["key"] = "value" })
        ]);

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should()
                .ForExecutor("Agent1")
                    .HaveCalledToolWithArgument("ToolB", "key", "value")
                .And()
                .Validate());

        Assert.Contains("ToolB", exception.Message);
    }

    [Fact]
    public void HaveCalledToolWithArgument_WhenArgNotMatch_ThrowsException()
    {
        var result = CreateResultWithToolArgs("Agent1",
        [
            ("ToolA", "c1", new Dictionary<string, object?> { ["city"] = "Paris" })
        ]);

        var exception = Assert.Throws<WorkflowAssertionException>(() =>
            result.Should()
                .ForExecutor("Agent1")
                    .HaveCalledToolWithArgument("ToolA", "city", "Tokyo")
                .And()
                .Validate());

        Assert.Contains("city", exception.Message);
        Assert.Contains("Tokyo", exception.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CHAINED WORKFLOW + TOOL ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ChainedAssertions_WorkflowAndToolLevel_AllPass()
    {
        var result = CreateResultWithTools(
            ("TripPlanner", "plan output", [("GetInfoAbout", "c1", "Tokyo info"), ("GetInfoAbout", "c2", "Beijing info")]),
            ("FlightAgent", "flight output", [("SearchFlights", "c3", "found"), ("BookFlight", "c4", "booked")]),
            ("HotelAgent", "hotel output", [("BookHotel", "c5", "booked1"), ("BookHotel", "c6", "booked2")]),
            ("Presenter", "final itinerary", []));

        var exception = Record.Exception(() =>
            result.Should()
                .HaveStepCount(4)
                .HaveNoErrors()
                .HaveAnyExecutorCalledTool("GetInfoAbout")
                .HaveAnyExecutorCalledTool("SearchFlights")
                .HaveAnyExecutorCalledTool("BookFlight")
                .HaveAnyExecutorCalledTool("BookHotel")
                .HaveTotalToolCallCount(6)
                .ForExecutor("TripPlanner")
                    .HaveCalledTool("GetInfoAbout")
                    .And()
                    .HaveToolCallCount(2)
                .And()
                .ForExecutor("FlightAgent")
                    .HaveCalledTool("SearchFlights")
                    .And()
                    .HaveCalledTool("BookFlight")
                    .And()
                    .HaveToolCallCount(2)
                .And()
                .ForExecutor("Presenter")
                    .HaveToolCallCount(0)
                .And()
                .Validate());

        Assert.Null(exception);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // WORKFLOWTESTCASE.EXPECTEDTOOLS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WorkflowTestCase_ExpectedTools_CanBeSet()
    {
        var testCase = new WorkflowTestCase
        {
            Name = "test",
            Input = "prompt",
            ExpectedTools = ["GetInfoAbout", "SearchFlights", "BookFlight", "BookHotel"]
        };

        Assert.Equal(4, testCase.ExpectedTools.Count);
        Assert.Contains("GetInfoAbout", testCase.ExpectedTools);
    }

    [Fact]
    public void WorkflowTestCase_PerExecutorExpectedTools_CanBeSet()
    {
        var testCase = new WorkflowTestCase
        {
            Name = "test",
            Input = "prompt",
            PerExecutorExpectedTools = new Dictionary<string, IReadOnlyList<string>>
            {
                ["TripPlanner"] = ["GetInfoAbout"],
                ["FlightAgent"] = ["SearchFlights", "BookFlight"]
            }
        };

        Assert.Equal(2, testCase.PerExecutorExpectedTools.Count);
        Assert.Contains("GetInfoAbout", testCase.PerExecutorExpectedTools["TripPlanner"]);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // WORKFLOWEVALUATIONHARNESS — EXPECTED TOOLS ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Harness_ExpectedTools_WhenAllPresent_Passes()
    {
        var adapter = CreateMockAdapter(
            ("Agent1", "out1", [("ToolA", "c1")]),
            ("Agent2", "out2", []));

        var testCase = new WorkflowTestCase
        {
            Name = "tool test",
            Input = "test",
            ExpectedTools = ["ToolA"]
        };

        var harness = new WorkflowEvaluationHarness(verbose: false);
        var result = await harness.RunWorkflowTestAsync(adapter, testCase);

        Assert.True(result.Passed);
        Assert.All(result.AssertionResults!, a => Assert.True(a.Passed));
    }

    [Fact]
    public async Task Harness_ExpectedTools_WhenMissing_Fails()
    {
        var adapter = CreateMockAdapter(
            ("Agent1", "out1", [("ToolA", "c1")]));

        var testCase = new WorkflowTestCase
        {
            Name = "tool test",
            Input = "test",
            ExpectedTools = ["ToolA", "ToolB"]
        };

        var harness = new WorkflowEvaluationHarness(verbose: false);
        var result = await harness.RunWorkflowTestAsync(adapter, testCase);

        Assert.False(result.Passed);
        Assert.Contains(result.AssertionResults!, a => a.AssertionName == "Expected Tools" && !a.Passed);
    }

    [Fact]
    public async Task Harness_PerExecutorExpectedTools_WhenAllPresent_Passes()
    {
        var adapter = CreateMockAdapter(
            ("Agent1", "out1", [("ToolA", "c1"), ("ToolB", "c2")]),
            ("Agent2", "out2", [("ToolC", "c3")]));

        var testCase = new WorkflowTestCase
        {
            Name = "per-executor tool test",
            Input = "test",
            PerExecutorExpectedTools = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Agent1"] = ["ToolA", "ToolB"],
                ["Agent2"] = ["ToolC"]
            }
        };

        var harness = new WorkflowEvaluationHarness(verbose: false);
        var result = await harness.RunWorkflowTestAsync(adapter, testCase);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Harness_PerExecutorExpectedTools_WhenMissing_Fails()
    {
        var adapter = CreateMockAdapter(
            ("Agent1", "out1", [("ToolA", "c1")]),
            ("Agent2", "out2", []));

        var testCase = new WorkflowTestCase
        {
            Name = "per-executor tool test",
            Input = "test",
            PerExecutorExpectedTools = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Agent2"] = ["ToolX"]
            }
        };

        var harness = new WorkflowEvaluationHarness(verbose: false);
        var result = await harness.RunWorkflowTestAsync(adapter, testCase);

        Assert.False(result.Passed);
        Assert.Contains(result.AssertionResults!,
            a => a.AssertionName.Contains("Agent2") && !a.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ForToolCall SUB-BUILDER: BeforeTool, AfterTool, WithoutError
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ForToolCall_BeforeTool_WhenCorrectOrder_Passes()
    {
        var result = CreateResultWithTools(
            ("FlightAgent", "flights", [("SearchFlights", "c1", "found"), ("BookFlight", "c2", "booked")]));

        var exception = Record.Exception(() =>
            result.Should()
                .ForExecutor("FlightAgent")
                    .HaveCalledTool("SearchFlights")
                        .BeforeTool("BookFlight", because: "must search before booking")
                    .And()
                .And()
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void ForToolCall_BeforeTool_WhenWrongOrder_Fails()
    {
        var result = CreateResultWithTools(
            ("FlightAgent", "flights", [("BookFlight", "c1", "booked"), ("SearchFlights", "c2", "found")]));

        var exception = Record.Exception(() =>
            result.Should()
                .ForExecutor("FlightAgent")
                    .HaveCalledTool("SearchFlights")
                        .BeforeTool("BookFlight", because: "must search before booking")
                    .And()
                .And()
                .Validate());

        Assert.NotNull(exception);
        Assert.IsType<WorkflowAssertionException>(exception);
        Assert.Contains("SearchFlights", exception.Message);
        Assert.Contains("BookFlight", exception.Message);
        Assert.Contains("must search before booking", exception.Message);
    }

    [Fact]
    public void ForToolCall_AfterTool_WhenCorrectOrder_Passes()
    {
        var result = CreateResultWithTools(
            ("FlightAgent", "flights", [("SearchFlights", "c1", "found"), ("BookFlight", "c2", "booked")]));

        var exception = Record.Exception(() =>
            result.Should()
                .ForExecutor("FlightAgent")
                    .HaveCalledTool("BookFlight")
                        .AfterTool("SearchFlights", because: "booking follows search")
                    .And()
                .And()
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void ForToolCall_WithoutError_WhenNoError_Passes()
    {
        var result = CreateResultWithTools(
            ("Agent1", "output", [("ToolA", "c1", "success")]));

        var exception = Record.Exception(() =>
            result.Should()
                .ForExecutor("Agent1")
                    .HaveCalledTool("ToolA")
                        .WithoutError()
                    .And()
                .And()
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void ForToolCall_WithoutError_WhenHasError_Fails()
    {
        var result = CreateResultWithToolError("Agent1", "ToolA", new InvalidOperationException("tool failed"));

        var exception = Record.Exception(() =>
            result.Should()
                .ForExecutor("Agent1")
                    .HaveCalledTool("ToolA")
                        .WithoutError(because: "all tools must succeed")
                    .And()
                .And()
                .Validate());

        Assert.NotNull(exception);
        Assert.IsType<WorkflowAssertionException>(exception);
        Assert.Contains("tool failed", exception.Message);
        Assert.Contains("all tools must succeed", exception.Message);
    }

    [Fact]
    public void ForToolCall_WithResultContaining_WhenMatch_Passes()
    {
        var result = CreateResultWithTools(
            ("Agent1", "output", [("SearchTool", "c1", "Found 5 results for query")]));

        var exception = Record.Exception(() =>
            result.Should()
                .ForExecutor("Agent1")
                    .HaveCalledTool("SearchTool")
                        .WithResultContaining("5 results")
                    .And()
                .And()
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void ForToolCall_FullChain_BeforeTool_WithoutError_Passes()
    {
        var result = CreateResultWithTools(
            ("FlightAgent", "flights", [("SearchFlights", "c1", "found"), ("BookFlight", "c2", "booked")]));

        // Full fluent chain matching the user's desired syntax
        var exception = Record.Exception(() =>
            result.Should()
                .ForExecutor("FlightAgent")
                    .HaveCalledTool("SearchFlights")
                        .BeforeTool("BookFlight", because: "can't book without search results")
                        .WithoutError()
                    .And()
                    .HaveCalledTool("BookFlight")
                        .AfterTool("SearchFlights")
                        .WithoutError()
                    .And()
                .And()
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void ForToolCall_MissingTool_ReportsFailure()
    {
        var result = CreateResultWithTools(
            ("Agent1", "output", [("ToolA", "c1", "done")]));

        var exception = Record.Exception(() =>
            result.Should()
                .ForExecutor("Agent1")
                    .HaveCalledTool("NonExistentTool")
                        .WithoutError()
                    .And()
                .And()
                .Validate());

        Assert.NotNull(exception);
        Assert.Contains("NonExistentTool", exception!.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // WORKFLOW-LEVEL HaveNoToolErrors
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveNoToolErrors_WhenAllToolsSucceed_Passes()
    {
        var result = CreateResultWithTools(
            ("Agent1", "output", [("ToolA", "c1", "ok"), ("ToolB", "c2", "ok")]),
            ("Agent2", "output2", [("ToolC", "c3", "ok")]));

        var exception = Record.Exception(() =>
            result.Should()
                .HaveNoToolErrors(because: "all tools must succeed for quality output")
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveNoToolErrors_WhenToolHasError_Fails()
    {
        var result = CreateResultWithToolError("Agent1", "ToolA", new InvalidOperationException("boom"));

        var exception = Record.Exception(() =>
            result.Should()
                .HaveNoToolErrors(because: "all tools must succeed")
                .Validate());

        Assert.NotNull(exception);
        Assert.IsType<WorkflowAssertionException>(exception);
        Assert.Contains("boom", exception.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EXECUTOR-LEVEL HaveNoToolErrors
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ExecutorHaveNoToolErrors_WhenNoErrors_Passes()
    {
        var result = CreateResultWithTools(
            ("Agent1", "output", [("ToolA", "c1", "ok")]));

        var exception = Record.Exception(() =>
            result.Should()
                .ForExecutor("Agent1")
                    .HaveNoToolErrors(because: "agent tools must not fail")
                .And()
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void ExecutorHaveNoToolErrors_WhenHasError_Fails()
    {
        var result = CreateResultWithToolError("Agent1", "ToolA", new InvalidOperationException("error!"));

        var exception = Record.Exception(() =>
            result.Should()
                .ForExecutor("Agent1")
                    .HaveNoToolErrors(because: "agent tools must not fail")
                .And()
                .Validate());

        Assert.NotNull(exception);
        Assert.Contains("error!", exception!.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // COMBINED WORKFLOW + TOOL FLUENT CHAIN (matches user's desired syntax)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FullFluentChain_WorkflowAndToolAssertions_Passes()
    {
        var result = CreateResultWithTools(
            ("TripPlanner", "plan", [("GetInfoAbout", "c1", "Tokyo info")]),
            ("FlightAgent", "flights", [("SearchFlights", "c2", "found"), ("BookFlight", "c3", "booked")]),
            ("HotelAgent", "hotels", [("BookHotel", "c4", "reserved")]),
            ("Presenter", "final", []));

        var exception = Record.Exception(() =>
            result.Should()
                // Workflow-level tool assertions (across all executors)
                .HaveCalledTool("GetInfoAbout", because: "TripPlanner must research cities")
                    .WithoutError()
                .And()
                .HaveCalledTool("SearchFlights")
                    .BeforeTool("BookFlight", because: "can't book without search results")
                    .WithoutError()
                .And()
                .HaveCalledTool("BookFlight")
                    .WithoutError()
                .And()
                .HaveCalledTool("BookHotel", because: "must book hotels")
                    .WithoutError()
                .And()
                .HaveNoToolErrors(because: "all tools must succeed for quality output")
                .HaveAtLeastTotalToolCalls(4, because: "workflow uses at least 4 tool calls")
                // Executor-specific assertions (non-tool)
                .ForExecutor("Presenter")
                    .HaveToolCallCount(0, because: "presenter only formats output")
                .And()
                .Validate());

        Assert.Null(exception);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // HELPER: Create result with a tool error
    // ═══════════════════════════════════════════════════════════════════════════

    private static WorkflowExecutionResult CreateResultWithToolError(
        string executorId, string toolName, Exception error)
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps =
            [
                new ExecutorStep
                {
                    ExecutorId = executorId,
                    Output = "output",
                    StepIndex = 0,
                    Duration = TimeSpan.FromSeconds(1),
                    ToolCalls =
                    [
                        new ToolCallRecord
                        {
                            Name = toolName,
                            CallId = "c1",
                            Order = 1,
                            ExecutorId = executorId,
                            Exception = error
                        }
                    ]
                }
            ],
            TotalDuration = TimeSpan.FromSeconds(1)
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // HELPER: Create mock MAFWorkflowAdapter
    // ═══════════════════════════════════════════════════════════════════════════

    private static MAFWorkflowAdapter CreateMockAdapter(
        params (string executorId, string output, (string toolName, string callId)[] tools)[] steps)
    {
        return new MAFWorkflowAdapter(
            "TestWorkflow",
            (prompt, ct) => EmitMockEvents(steps),
            steps.Select(s => s.executorId).Distinct());
    }

    private static async IAsyncEnumerable<WorkflowEvent> EmitMockEvents(
        (string executorId, string output, (string toolName, string callId)[] tools)[] steps)
    {
        for (int i = 0; i < steps.Length; i++)
        {
            var (executorId, output, tools) = steps[i];

            if (i > 0)
            {
                yield return new EdgeTraversedEvent(
                    steps[i - 1].executorId,
                    executorId,
                    EdgeType.Sequential);
            }

            // Emit a minimal output first to initialize the executor step,
            // then tool calls, then the remaining output.
            // This matches real MAF workflow event ordering where the executor
            // is invoked (producing initial output) before tool calls occur.
            yield return new ExecutorOutputEvent(executorId, "");

            // Emit tool call events during execution
            foreach (var (toolName, callId) in tools)
            {
                yield return new ExecutorToolCallEvent(
                    ExecutorId: executorId,
                    ToolName: toolName,
                    CallId: callId,
                    Result: $"{toolName} result",
                    Duration: TimeSpan.FromMilliseconds(50));
            }

            yield return new ExecutorOutputEvent(executorId, output);
            await Task.Yield();
        }
        yield return new WorkflowCompleteEvent();
    }
}
