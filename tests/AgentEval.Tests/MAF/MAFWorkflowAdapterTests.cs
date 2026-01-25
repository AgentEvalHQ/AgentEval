// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;
using AgentEval.MAF;
using AgentEval.Models;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Unit tests for MAFWorkflowAdapter.
/// </summary>
public class MAFWorkflowAdapterTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // BASIC EXECUTION
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteWorkflowAsync_SimpleWorkflow_CapturesAllSteps()
    {
        var adapter = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("researcher", "Research findings"),
            ("writer", "Article draft"),
            ("editor", "Polished article"));

        var result = await adapter.ExecuteWorkflowAsync("Write about AI");

        Assert.NotNull(result);
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal("researcher", result.Steps[0].ExecutorId);
        Assert.Equal("writer", result.Steps[1].ExecutorId);
        Assert.Equal("editor", result.Steps[2].ExecutorId);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_CapturesFinalOutput()
    {
        var adapter = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("step1", "Intermediate"),
            ("step2", "Final result"));

        var result = await adapter.ExecuteWorkflowAsync("Test input");

        Assert.Equal("Final result", result.FinalOutput);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_RecordsOriginalPrompt()
    {
        var adapter = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("executor1", "output"));

        var result = await adapter.ExecuteWorkflowAsync("My test prompt");

        Assert.Equal("My test prompt", result.OriginalPrompt);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_TracksStepIndices()
    {
        var adapter = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("step1", "out1"),
            ("step2", "out2"),
            ("step3", "out3"));

        var result = await adapter.ExecuteWorkflowAsync("test");

        Assert.Equal(0, result.Steps[0].StepIndex);
        Assert.Equal(1, result.Steps[1].StepIndex);
        Assert.Equal(2, result.Steps[2].StepIndex);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_TracksDuration()
    {
        var adapter = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("step1", "out1"),
            ("step2", "out2"));

        var result = await adapter.ExecuteWorkflowAsync("test");

        Assert.True(result.TotalDuration > TimeSpan.Zero);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EXECUTOR ID TRACKING
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ExecutorIds_FromPredefinedSteps_ReturnsUniqueIds()
    {
        var adapter = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("executor1", "out1"),
            ("executor2", "out2"),
            ("executor1", "out3")); // Duplicate

        Assert.Equal(2, adapter.ExecutorIds.Count);
        Assert.Contains("executor1", adapter.ExecutorIds);
        Assert.Contains("executor2", adapter.ExecutorIds);
    }

    [Fact]
    public async Task ExecutorIds_AfterExecution_IncludesDiscoveredIds()
    {
        var adapter = new MAFWorkflowAdapter(
            "TestWorkflow",
            (prompt, ct) => ExecuteWithNewExecutors());

        await adapter.ExecuteWorkflowAsync("test");

        Assert.Contains("discovered_executor", adapter.ExecutorIds);
    }

    private static async IAsyncEnumerable<WorkflowEvent> ExecuteWithNewExecutors()
    {
        yield return new ExecutorOutputEvent("discovered_executor", "output");
        await Task.Yield();
        yield return new WorkflowCompleteEvent();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ERROR HANDLING
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteWorkflowAsync_WithErrors_CapturesErrorDetails()
    {
        var adapter = new MAFWorkflowAdapter(
            "TestWorkflow",
            (prompt, ct) => ExecuteWithError());

        var result = await adapter.ExecuteWorkflowAsync("test");

        Assert.NotNull(result.Errors);
        Assert.Single(result.Errors);
        Assert.Equal("executor1", result.Errors[0].ExecutorId);
        Assert.Equal("Test error message", result.Errors[0].Message);
    }

    private static async IAsyncEnumerable<WorkflowEvent> ExecuteWithError()
    {
        yield return new ExecutorOutputEvent("executor1", "partial output");
        await Task.Yield();
        yield return new ExecutorErrorEvent("executor1", "Test error message", "stack trace", "InvalidOperationException");
        yield return new WorkflowCompleteEvent();
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_WithException_CapturesAsError()
    {
        var adapter = new MAFWorkflowAdapter(
            "TestWorkflow",
            (prompt, ct) => ThrowingWorkflow());

        var result = await adapter.ExecuteWorkflowAsync("test");

        Assert.NotNull(result.Errors);
        Assert.Single(result.Errors);
        Assert.Contains("workflow", result.Errors[0].ExecutorId);
        Assert.Equal("Test exception", result.Errors[0].Message);
    }

    private static async IAsyncEnumerable<WorkflowEvent> ThrowingWorkflow()
    {
        await Task.Yield();
        throw new InvalidOperationException("Test exception");
        #pragma warning disable CS0162 // Unreachable code
        yield break;
        #pragma warning restore CS0162
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TOOL CALL TRACKING
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteWorkflowAsync_WithToolCalls_CapturesToolDetails()
    {
        var adapter = new MAFWorkflowAdapter(
            "TestWorkflow",
            (prompt, ct) => ExecuteWithToolCalls());

        var result = await adapter.ExecuteWorkflowAsync("test");

        var step = result.Steps.First(s => s.ExecutorId == "executor1");
        Assert.NotNull(step.ToolCalls);
        Assert.Single(step.ToolCalls);
        Assert.Equal("search", step.ToolCalls[0].Name);
    }

    private static async IAsyncEnumerable<WorkflowEvent> ExecuteWithToolCalls()
    {
        yield return new ExecutorOutputEvent("executor1", "Found results");
        await Task.Yield();
        yield return new ExecutorToolCallEvent(
            "executor1", 
            "search", 
            "call-1",
            new Dictionary<string, object?> { ["query"] = "test query" },
            "search results",
            TimeSpan.FromMilliseconds(100));
        yield return new WorkflowCompleteEvent();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // IEvaluableAgent INTERFACE
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InvokeAsync_ReturnsFinalOutput()
    {
        var adapter = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("step1", "Intermediate"),
            ("step2", "Final output"));

        var response = await adapter.InvokeAsync("test prompt");

        Assert.Equal("Final output", response.Text);
    }

    [Fact]
    public void Name_ReturnsWorkflowName()
    {
        var adapter = MAFWorkflowAdapter.FromSteps(
            "MyWorkflow",
            ("step1", "output"));

        Assert.Equal("MyWorkflow", adapter.Name);
    }

    [Fact]
    public void WorkflowType_WhenProvided_ReturnsType()
    {
        var adapter = new MAFWorkflowAdapter(
            "TestWorkflow",
            (prompt, ct) => EmptyWorkflow(),
            workflowType: "PromptChaining");

        Assert.Equal("PromptChaining", adapter.WorkflowType);
    }

    private static async IAsyncEnumerable<WorkflowEvent> EmptyWorkflow()
    {
        await Task.Yield();
        yield return new WorkflowCompleteEvent();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CANCELLATION
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteWorkflowAsync_WhenCancelled_CapturesAsError()
    {
        var cts = new CancellationTokenSource();
        var adapter = new MAFWorkflowAdapter(
            "TestWorkflow",
            (prompt, ct) => SlowWorkflow(ct));

        cts.Cancel();

        // Cancellation is caught and recorded as an error in the result
        var result = await adapter.ExecuteWorkflowAsync("test", cts.Token);

        Assert.NotNull(result.Errors);
        Assert.Single(result.Errors);
        Assert.Contains("cancel", result.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async IAsyncEnumerable<WorkflowEvent> SlowWorkflow(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        yield return new ExecutorOutputEvent("executor1", "output");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EDGE CASES
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteWorkflowAsync_EmptyWorkflow_ReturnsEmptyResult()
    {
        var adapter = new MAFWorkflowAdapter(
            "EmptyWorkflow",
            (prompt, ct) => EmptyWorkflow());

        var result = await adapter.ExecuteWorkflowAsync("test");

        Assert.Empty(result.Steps);
        Assert.Equal(string.Empty, result.FinalOutput);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_SingleStep_Works()
    {
        var adapter = MAFWorkflowAdapter.FromSteps(
            "SingleStep",
            ("only_executor", "single output"));

        var result = await adapter.ExecuteWorkflowAsync("test");

        Assert.Single(result.Steps);
        Assert.Equal("single output", result.FinalOutput);
    }

    [Fact]
    public void Constructor_WithNullName_ThrowsException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MAFWorkflowAdapter(null!, (p, c) => EmptyWorkflow()));
    }

    [Fact]
    public void Constructor_WithNullExecutor_ThrowsException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MAFWorkflowAdapter("Test", null!));
    }
}
