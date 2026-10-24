// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF;
using AgentEval.Models;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public partial class MAFWorkflowEventBridgeTests
{
    [Fact]
    public async Task FromMAFWorkflow_UnansweredRequestPort_ReturnsNamedErrorWithoutCancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await RequestPortWorkflow().ExecuteWorkflowAsync("approve purchase", cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(cancellation.IsCancellationRequested);
        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors!);
        Assert.Equal("Approval", error.ExecutorId);
        Assert.Contains("request", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Approval", error.Message);
        Assert.DoesNotContain(result.Steps, step => step.ExecutorId == "Processor");
    }

    [Fact]
    public async Task RunWorkflowTestAsync_UnansweredRequestPort_FailsForPendingRequestInsteadOfTimeout()
    {
        var result = await new WorkflowEvaluationHarness(verbose: false).RunWorkflowTestAsync(
            RequestPortWorkflow(), new WorkflowTestCase { Name = "pending approval", Input = "approve purchase" },
            new WorkflowTestOptions { Timeout = TimeSpan.FromSeconds(5) }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Passed);
        Assert.Null(result.Error);
        Assert.NotNull(result.ExecutionResult);
        Assert.Contains(result.ExecutionResult.Errors!, error => error.ExecutorId == "Approval");
        Assert.Contains(result.AssertionResults!, assertion => assertion.AssertionName == "No Errors" && !assertion.Passed);
    }

    [Fact]
    public async Task TranslateEvents_RequestInfo_PreservesPortAndRequestIdentity()
    {
        var result = await PendingRequestEvents().ExecuteWorkflowAsync("input");
        var error = Assert.Single(result.Errors!);
        Assert.Equal("Approval", error.ExecutorId);
        Assert.Contains("request-123", error.Message);
        Assert.Contains("Approval", error.Message);
    }

    [Fact]
    public async Task TranslateEvents_CompletedRequestEnvelope_DoesNotBecomeStepOutput()
    {
        var result = await PendingRequestEvents().ExecuteWorkflowAsync("input");
        Assert.DoesNotContain(result.Steps, step => !string.IsNullOrEmpty(step.Output));
        Assert.Empty(result.FinalOutput);
    }

    private static MAFWorkflowAdapter RequestPortWorkflow()
    {
        var port = MAFWorkflows.RequestPort.Create<string, string>("Approval");
        var processor = CreateFuncBinding("Processor", "processed");
        var workflow = new MAFWorkflows.WorkflowBuilder(port).AddEdge(port, processor).WithOutputFrom(processor).Build();
        return MAFWorkflowAdapter.FromMAFWorkflow(workflow, "pending request", ["Approval", "Processor"]);
    }

    private static MAFWorkflowAdapter PendingRequestEvents()
    {
        var port = MAFWorkflows.RequestPort.Create<string, string>("Approval");
        var request = MAFWorkflows.ExternalRequest.Create(port, "approve purchase", "request-123");
        return RecordingAdapter([
            new MAFWorkflows.ExecutorInvokedEvent("Approval", "approve purchase"),
            new MAFWorkflows.RequestInfoEvent(request),
            new MAFWorkflows.ExecutorCompletedEvent("Approval", request)
        ], [], ["Approval"]);
    }
}
