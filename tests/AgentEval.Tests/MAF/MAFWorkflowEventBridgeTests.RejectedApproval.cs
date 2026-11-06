// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Testing;
using AgentEval.Tests.TestHelpers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public partial class MAFWorkflowEventBridgeTests
{
    // Fails on HEAD 143696a9 and before the bridge registered a responder's answer: AgentEval's own requestResponder
    // rejects the agent's approval request. The bridge sends that answer, MAF resumes the agent with it as input, and
    // the resumed run streams the call FunctionInvokingChatClient re-creates for it and a "rejected" result, but not the
    // decision (MEAI removes it from the messages). The bridge paired the two and recorded a tool that never ran as
    // executed. Real MAF workflow over a scripted model, offline; the model's second request shows that MAF re-created
    // the call and generated the rejected result in that run.
    [Fact]
    public async Task FromMAFWorkflow_ResponderRejectsToolApproval_CallIsNotRecordedAsExecuted()
    {
        var (result, model, requests, executed) = await RunRefundWorkflowAsync(approve: false);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors?.Select(error => error.Message) ?? []));
        Assert.Equal(1, requests);
        Assert.Equal(0, executed);
        var secondRequest = model.ReceivedMessages[^1].SelectMany(m => m.Contents).ToList();
        Assert.Contains(secondRequest, c => c is FunctionCallContent { CallId: UnansweredApprovalRun.CallId });
        Assert.Contains(secondRequest, c => c is FunctionResultContent { CallId: UnansweredApprovalRun.CallId });
        Assert.Contains(result.Steps, step => step.ExecutorId == "Support");
        Assert.DoesNotContain(result.ToolUsage?.Calls ?? [],
            call => call.Name == UnansweredApprovalRun.ToolName && call.WasExecuted);
        Assert.DoesNotContain(result.Steps.SelectMany(step => step.ToolCalls ?? []),
            call => call.Name == UnansweredApprovalRun.ToolName && call.WasExecuted);
    }

    // Guard (passes on HEAD 143696a9 too): when the responder approves, the tool runs once and the bridge records it as
    // executed with its real result. Same real MAF workflow, offline.
    [Fact]
    public async Task FromMAFWorkflow_ResponderApprovesToolApproval_CallIsRecordedAsExecuted()
    {
        var (result, _, requests, executed) = await RunRefundWorkflowAsync(approve: true);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors?.Select(error => error.Message) ?? []));
        Assert.Equal(1, requests);
        Assert.Equal(1, executed);
        var call = Assert.Single(result.ToolUsage!.Calls, c => c.Name == UnansweredApprovalRun.ToolName);
        Assert.True(call.WasExecuted);
        Assert.Equal(UnansweredApprovalRun.ApprovedResult, call.Result?.ToString());
    }

    // A real ChatClientAgent (approval binding and function invocation on by default) over a scripted model, bound as
    // the only executor of a workflow, run through FromMAFWorkflow with a responder that answers its one approval
    // request. The scripted model asks for issue_refund, then answers. No provider is called.
    private async Task<(WorkflowExecutionResult Result, ScriptedChatClient Model, int Requests, int Executed)>
        RunRefundWorkflowAsync(bool approve)
    {
        var executed = 0;
        var refund = AIFunctionFactory.Create(
            (int amount) => { Interlocked.Increment(ref executed); return $"refunded {amount}"; }, UnansweredApprovalRun.ToolName);
        var model = new ScriptedChatClient()
            .AddToolCall(UnansweredApprovalRun.CallId, UnansweredApprovalRun.ToolName, UnansweredApprovalRun.RefundArgs())
            .AddText(UnansweredApprovalRun.Answer);
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "Support",
            ChatOptions = new ChatOptions { Tools = [new ApprovalRequiredAIFunction(refund)] },
        }).BindAsExecutor(emitEvents: true);
        var workflow = new MAFWorkflows.WorkflowBuilder(agent).WithOutputFrom(agent).BuildWithTiming(_timing);
        var requests = 0;
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "approval answer", ["Support"], null, (request, _) =>
        {
            requests++;
            Assert.True(request.TryGetDataAs<ToolApprovalRequestContent>(out var approval));
            var decision = approve
                ? approval!.CreateResponse(approved: true)
                : approval!.CreateResponse(approved: false, reason: "Amount too large.");
            return ValueTask.FromResult<MAFWorkflows.ExternalResponse?>(request.CreateResponse(decision));
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await adapter.ExecuteWorkflowAsync(UnansweredApprovalRun.Pause, cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(15));
        return (result, model, requests, Volatile.Read(ref executed));
    }

    // Fails on HEAD 143696a9: the bridge read the call MAF re-created for a rejected approval request (MAF 1.25+ #9152:
    // an agent executor's session runs again while its saved request is unanswered) and paired it with the "rejected"
    // result, so the workflow reported a tool that never ran as executed. Scripted workflow events, one agent update per
    // message of the MAF 1.27 shape; not a workflow run.
    [Fact]
    public async Task TranslateEvents_RejectedApprovalCall_IsNotRecordedAsATool()
    {
        var events = new List<MAFWorkflows.WorkflowEvent>
        {
            new MAFWorkflows.ExecutorInvokedEvent("Writer", new MAFWorkflows.TurnToken(true)),
        };
        events.AddRange(UnansweredApprovalRun.Updates().Select(update => new MAFWorkflows.AgentResponseUpdateEvent("Writer", update)));
        events.Add(new MAFWorkflows.AgentResponseEvent("Writer",
            new Microsoft.Agents.AI.AgentResponse(new ChatMessage(ChatRole.Assistant, UnansweredApprovalRun.Answer))));
        events.Add(new MAFWorkflows.ExecutorCompletedEvent("Writer", null));
        events.Add(new MAFWorkflows.SuperStepCompletedEvent(0));

        var result = await RecordingAdapter(events, ["Writer"], ["Writer"]).ExecuteWorkflowAsync("input");

        Assert.Contains(result.Steps, step => step.ExecutorId == "Writer");
        Assert.DoesNotContain(result.ToolUsage?.Calls ?? [], call => call.Name == UnansweredApprovalRun.ToolName);
        Assert.DoesNotContain(result.Steps.SelectMany(step => step.ToolCalls ?? []), call => call.Name == UnansweredApprovalRun.ToolName);
    }

    // Guard (passes on HEAD too): with an approved decision the call and its real result are an executed tool call.
    [Fact]
    public async Task TranslateEvents_ApprovedApprovalCall_IsRecordedAsExecuted()
    {
        var events = new List<MAFWorkflows.WorkflowEvent>
        {
            new MAFWorkflows.ExecutorInvokedEvent("Writer", new MAFWorkflows.TurnToken(true)),
        };
        events.AddRange(UnansweredApprovalRun.Updates(approved: true)
            .Select(update => new MAFWorkflows.AgentResponseUpdateEvent("Writer", update)));
        events.Add(new MAFWorkflows.AgentResponseEvent("Writer",
            new Microsoft.Agents.AI.AgentResponse(new ChatMessage(ChatRole.Assistant, UnansweredApprovalRun.Answer))));
        events.Add(new MAFWorkflows.ExecutorCompletedEvent("Writer", null));
        events.Add(new MAFWorkflows.SuperStepCompletedEvent(0));

        var result = await RecordingAdapter(events, ["Writer"], ["Writer"]).ExecuteWorkflowAsync("input");

        var call = Assert.Single(result.ToolUsage!.Calls, c => c.Name == UnansweredApprovalRun.ToolName);
        Assert.True(call.WasExecuted);
        Assert.Equal(UnansweredApprovalRun.ApprovedResult, call.Result);
    }
}
