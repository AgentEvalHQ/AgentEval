// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;
using AgentEval.Models;
using AgentEval.Assertions;

namespace AgentEval.Tests.Assertions;

/// <summary>
/// Unit tests for WorkflowAssertions fluent API.
/// </summary>
public class WorkflowAssertionsTests
{
    private static WorkflowExecutionResult CreateTestResult(
        params (string executorId, string output)[] steps)
    {
        return new WorkflowExecutionResult
        {
            FinalOutput = steps.LastOrDefault().output ?? "",
            Steps = steps.Select((s, i) => new ExecutorStep
            {
                ExecutorId = s.executorId,
                Output = s.output,
                StepIndex = i,
                Duration = TimeSpan.FromMilliseconds(100 * (i + 1))
            }).ToList(),
            TotalDuration = TimeSpan.FromMilliseconds(steps.Length * 100),
            OriginalPrompt = "test prompt"
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // STEP COUNT ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveStepCount_WithCorrectCount_DoesNotThrow()
    {
        var result = CreateTestResult(
            ("executor1", "output1"),
            ("executor2", "output2"),
            ("executor3", "output3"));

        var exception = Record.Exception(() => 
            result.Should().HaveStepCount(3).Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveStepCount_WithWrongCount_ThrowsException()
    {
        var result = CreateTestResult(("executor1", "output1"));

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should().HaveStepCount(3).Validate());

        Assert.Contains("Expected 3 steps", exception.Message);
        Assert.Contains("found 1", exception.Message);
    }

    [Fact]
    public void HaveAtLeastSteps_WithEnoughSteps_DoesNotThrow()
    {
        var result = CreateTestResult(
            ("executor1", "output1"),
            ("executor2", "output2"));

        var exception = Record.Exception(() => 
            result.Should().HaveAtLeastSteps(2).Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveAtLeastSteps_WithFewerSteps_ThrowsException()
    {
        var result = CreateTestResult(("executor1", "output1"));

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should().HaveAtLeastSteps(3).Validate());

        Assert.Contains("at least 3 steps", exception.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EXECUTOR INVOCATION ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveInvokedExecutor_WhenExecutorExists_DoesNotThrow()
    {
        var result = CreateTestResult(("researcher", "research output"));

        var exception = Record.Exception(() => 
            result.Should().HaveInvokedExecutor("researcher").Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveInvokedExecutor_CaseInsensitive_DoesNotThrow()
    {
        var result = CreateTestResult(("Researcher", "research output"));

        var exception = Record.Exception(() => 
            result.Should().HaveInvokedExecutor("researcher").Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveInvokedExecutor_WhenExecutorMissing_ThrowsException()
    {
        var result = CreateTestResult(("writer", "write output"));

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should().HaveInvokedExecutor("researcher").Validate());

        Assert.Contains("researcher", exception.Message);
        Assert.Contains("not invoked", exception.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EXECUTOR ORDER ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveExecutedInOrder_WithCorrectOrder_DoesNotThrow()
    {
        var result = CreateTestResult(
            ("researcher", "research"),
            ("writer", "article"),
            ("editor", "polished"));

        var exception = Record.Exception(() => 
            result.Should()
                .HaveExecutedInOrder("researcher", "writer", "editor")
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveExecutedInOrder_WithWrongOrder_ThrowsException()
    {
        var result = CreateTestResult(
            ("writer", "article"),
            ("researcher", "research"));

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should()
                .HaveExecutedInOrder("researcher", "writer")
                .Validate());

        Assert.Contains("researcher → writer", exception.Message);
        Assert.Contains("writer → researcher", exception.Message);
    }

    [Fact]
    public void HaveExecutedInOrder_WithDifferentCount_ThrowsException()
    {
        var result = CreateTestResult(
            ("executor1", "out1"),
            ("executor2", "out2"));

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should()
                .HaveExecutedInOrder("executor1", "executor2", "executor3")
                .Validate());

        Assert.Contains("different count", exception.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DURATION ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveCompletedWithin_WhenWithinLimit_DoesNotThrow()
    {
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = new List<ExecutorStep>(),
            TotalDuration = TimeSpan.FromSeconds(5)
        };

        var exception = Record.Exception(() => 
            result.Should()
                .HaveCompletedWithin(TimeSpan.FromSeconds(10))
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveCompletedWithin_WhenExceeded_ThrowsException()
    {
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = new List<ExecutorStep>(),
            TotalDuration = TimeSpan.FromSeconds(15)
        };

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should()
                .HaveCompletedWithin(TimeSpan.FromSeconds(10))
                .Validate());

        Assert.Contains("15", exception.Message);
        Assert.Contains("10", exception.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ERROR ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveNoErrors_WhenNoErrors_DoesNotThrow()
    {
        var result = CreateTestResult(("executor1", "output1"));

        var exception = Record.Exception(() => 
            result.Should().HaveNoErrors().Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveNoErrors_WhenHasErrors_ThrowsException()
    {
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = new List<ExecutorStep>(),
            TotalDuration = TimeSpan.FromSeconds(1),
            Errors = new List<WorkflowError>
            {
                new WorkflowError
                {
                    ExecutorId = "executor1",
                    Message = "Something went wrong"
                }
            }
        };

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should().HaveNoErrors().Validate());

        Assert.Contains("Something went wrong", exception.Message);
    }

    [Fact]
    public void HaveSucceeded_WhenSuccess_DoesNotThrow()
    {
        var result = CreateTestResult(("executor1", "output1"));

        var exception = Record.Exception(() => 
            result.Should().HaveSucceeded().Validate());

        Assert.Null(exception);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // OUTPUT ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HaveFinalOutputContaining_WhenContains_DoesNotThrow()
    {
        var result = CreateTestResult(("executor1", "The final polished article"));

        var exception = Record.Exception(() => 
            result.Should()
                .HaveFinalOutputContaining("polished")
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveFinalOutputContaining_CaseInsensitive_DoesNotThrow()
    {
        var result = CreateTestResult(("executor1", "The Final POLISHED Article"));

        var exception = Record.Exception(() => 
            result.Should()
                .HaveFinalOutputContaining("polished")
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveFinalOutputContaining_WhenMissing_ThrowsException()
    {
        var result = CreateTestResult(("executor1", "Some other output"));

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should()
                .HaveFinalOutputContaining("polished")
                .Validate());

        Assert.Contains("polished", exception.Message);
    }

    [Fact]
    public void HaveNonEmptyOutput_WhenNonEmpty_DoesNotThrow()
    {
        var result = CreateTestResult(("executor1", "some output"));

        var exception = Record.Exception(() => 
            result.Should().HaveNonEmptyOutput().Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void HaveNonEmptyOutput_WhenEmpty_ThrowsException()
    {
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "",
            Steps = new List<ExecutorStep>(),
            TotalDuration = TimeSpan.Zero
        };

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should().HaveNonEmptyOutput().Validate());

        Assert.Contains("empty", exception.Message.ToLower());
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EXECUTOR STEP ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ForExecutor_HaveOutputContaining_DoesNotThrow()
    {
        var result = CreateTestResult(
            ("researcher", "Found relevant research data"),
            ("writer", "Created article draft"));

        var exception = Record.Exception(() => 
            result.Should()
                .ForExecutor("researcher")
                    .HaveOutputContaining("research")
                .And()
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void ForExecutor_HaveOutputContaining_WhenMissing_ThrowsException()
    {
        var result = CreateTestResult(("researcher", "Some other output"));

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should()
                .ForExecutor("researcher")
                    .HaveOutputContaining("data")
                .And()
                .Validate());

        Assert.Contains("researcher", exception.Message);
        Assert.Contains("data", exception.Message);
    }

    [Fact]
    public void ForExecutor_WhenExecutorNotFound_ThrowsException()
    {
        var result = CreateTestResult(("writer", "article"));

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should()
                .ForExecutor("researcher")
                    .HaveOutputContaining("data")
                .And()
                .Validate());

        Assert.Contains("researcher", exception.Message);
        Assert.Contains("not found", exception.Message);
    }

    [Fact]
    public void ForExecutor_HaveCompletedWithin_DoesNotThrow()
    {
        var result = CreateTestResult(("executor1", "output"));

        var exception = Record.Exception(() => 
            result.Should()
                .ForExecutor("executor1")
                    .HaveCompletedWithin(TimeSpan.FromSeconds(1))
                .And()
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void ForExecutor_HaveNonEmptyOutput_DoesNotThrow()
    {
        var result = CreateTestResult(("executor1", "some output"));

        var exception = Record.Exception(() => 
            result.Should()
                .ForExecutor("executor1")
                    .HaveNonEmptyOutput()
                .And()
                .Validate());

        Assert.Null(exception);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TOOL CALL ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ForExecutor_HaveCalledTool_DoesNotThrow()
    {
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = new List<ExecutorStep>
            {
                new ExecutorStep
                {
                    ExecutorId = "researcher",
                    Output = "Found data",
                    StepIndex = 0,
                    Duration = TimeSpan.FromSeconds(1),
                    ToolCalls = new List<ToolCallRecord>
                    {
                        new ToolCallRecord { Name = "search", CallId = "call-1", Order = 1 }
                    }
                }
            },
            TotalDuration = TimeSpan.FromSeconds(1)
        };

        var exception = Record.Exception(() => 
            result.Should()
                .ForExecutor("researcher")
                    .HaveCalledTool("search")
                .And()
                .And()
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void ForExecutor_HaveCalledTool_WhenMissing_ThrowsException()
    {
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = new List<ExecutorStep>
            {
                new ExecutorStep
                {
                    ExecutorId = "researcher",
                    Output = "Found data",
                    StepIndex = 0,
                    Duration = TimeSpan.FromSeconds(1),
                    ToolCalls = new List<ToolCallRecord>
                    {
                        new ToolCallRecord { Name = "search", CallId = "call-1", Order = 1 }
                    }
                }
            },
            TotalDuration = TimeSpan.FromSeconds(1)
        };

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should()
                .ForExecutor("researcher")
                    .HaveCalledTool("database")
                .And()
                .And()
                .Validate());

        Assert.Contains("database", exception.Message);
        Assert.Contains("search", exception.Message);
    }

    [Fact]
    public void ForExecutor_HaveToolCallCount_DoesNotThrow()
    {
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "output",
            Steps = new List<ExecutorStep>
            {
                new ExecutorStep
                {
                    ExecutorId = "researcher",
                    Output = "Found data",
                    StepIndex = 0,
                    Duration = TimeSpan.FromSeconds(1),
                    ToolCalls = new List<ToolCallRecord>
                    {
                        new ToolCallRecord { Name = "search", CallId = "call-1", Order = 1 },
                        new ToolCallRecord { Name = "fetch", CallId = "call-2", Order = 2 }
                    }
                }
            },
            TotalDuration = TimeSpan.FromSeconds(1)
        };

        var exception = Record.Exception(() => 
            result.Should()
                .ForExecutor("researcher")
                    .HaveToolCallCount(2)
                .And()
                .Validate());

        Assert.Null(exception);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CHAINED ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ChainedAssertions_AllPass_DoesNotThrow()
    {
        var result = CreateTestResult(
            ("researcher", "Research findings"),
            ("writer", "Article draft"),
            ("editor", "Final polished article"));

        var exception = Record.Exception(() => 
            result.Should()
                .HaveStepCount(3)
                .HaveExecutedInOrder("researcher", "writer", "editor")
                .HaveNoErrors()
                .HaveCompletedWithin(TimeSpan.FromMinutes(1))
                .HaveFinalOutputContaining("polished")
                .ForExecutor("researcher")
                    .HaveOutputContaining("Research")
                    .HaveNonEmptyOutput()
                .And()
                .ForExecutor("writer")
                    .HaveOutputContaining("draft")
                .And()
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void ChainedAssertions_MultipleFailures_ReportsAll()
    {
        var result = CreateTestResult(("executor1", "output1"));

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should()
                .HaveStepCount(3)
                .HaveInvokedExecutor("missing")
                .Validate());

        Assert.Contains("3 steps", exception.Message);
        Assert.Contains("missing", exception.Message);
        Assert.Contains("2 issue(s)", exception.Message);
    }

    [Fact]
    public void IsValid_WhenNoFailures_ReturnsTrue()
    {
        var result = CreateTestResult(("executor1", "output1"));

        var builder = result.Should().HaveStepCount(1);

        Assert.True(builder.IsValid);
        Assert.Empty(builder.Failures);
    }

    [Fact]
    public void IsValid_WhenHasFailures_ReturnsFalse()
    {
        var result = CreateTestResult(("executor1", "output1"));

        var builder = result.Should().HaveStepCount(5);

        Assert.False(builder.IsValid);
        Assert.Single(builder.Failures);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FORSTEP BY INDEX
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ForStep_ByIndex_DoesNotThrow()
    {
        var result = CreateTestResult(
            ("executor1", "First output"),
            ("executor2", "Second output"));

        var exception = Record.Exception(() => 
            result.Should()
                .ForStep(0)
                    .HaveOutputContaining("First")
                .And()
                .ForStep(1)
                    .HaveOutputContaining("Second")
                .And()
                .Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void ForStep_InvalidIndex_ThrowsException()
    {
        var result = CreateTestResult(("executor1", "output1"));

        var exception = Assert.Throws<WorkflowAssertionException>(() => 
            result.Should()
                .ForStep(5)
                    .HaveOutputContaining("test")
                .And()
                .Validate());

        Assert.Contains("not found", exception.Message);
    }
}
