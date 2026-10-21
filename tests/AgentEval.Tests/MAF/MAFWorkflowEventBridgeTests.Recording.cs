// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.MAF;
using AgentEval.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public partial class MAFWorkflowEventBridgeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FromMAFWorkflow_OverlappingNames_PreservesDistinctExecutors(bool suffixed)
    {
        var suffix = suffixed ? "_0123456789abcdef0123456789abcdef" : "";
        var first = CreateFuncBinding("Fetch" + suffix, "first");
        var retry = CreateFuncBinding("Fetch_Retry" + suffix, "retry");
        var workflow = new MAFWorkflows.WorkflowBuilder(first).AddEdge(first, retry).WithOutputFrom(retry).Build();
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "prefix collision", ["Fetch", "Fetch_Retry"])
            .ExecuteWorkflowAsync("input").WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(["Fetch", "Fetch_Retry"], result.Steps.Select(s => s.ExecutorId));
        Assert.Equal(["first", "retry"], result.Steps.Select(s => s.Output));
        Assert.Contains(result.Graph!.TraversedEdges!, e => e.SourceExecutorId == "Fetch" && e.TargetExecutorId == "Fetch_Retry");
    }

    [Fact]
    public async Task TranslateEvents_ConcurrentBuilderDelayedStartToken_DoesNotInventSelfLoop()
    {
        var workflow = MAFWorkflows.AgentWorkflowBuilder.BuildConcurrent(
            [new FakeStreamableAIAgent("Left", "left"), new FakeStreamableAIAgent("Right", "right")]);
        var start = workflow.StartExecutorId;
        var turnTakingIds = MAFWorkflowEventBridge.GetChatProtocolExecutorIds(workflow);
        // Force the real off-thread input/token race, using IDs and classification from the real builder.
        var events = SuperStep(0, AgentDelivery(start, new ChatMessage(ChatRole.User, "input")))
            .Concat(SuperStep(1, AgentDelivery(start, new MAFWorkflows.TurnToken(true))))
            .Concat(SuperStep(2, AgentDelivery("Left_Left", new MAFWorkflows.TurnToken(true), "left reply")));
        var result = await RecordingAdapter(events, turnTakingIds, [start, "Left_Left"])
            .ExecuteWorkflowAsync("input").WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Contains(start, turnTakingIds);
        Assert.Single(result.Steps, step => step.ExecutorId == start);
        Assert.DoesNotContain(result.Graph!.TraversedEdges ?? [], e => e.SourceExecutorId == e.TargetExecutorId);
    }

    [Fact]
    public async Task TranslateEvents_SubworkflowError_PreservesNormalizedHostId()
    {
        var events = new MAFWorkflows.WorkflowEvent[]
        {
            new MAFWorkflows.ExecutorInvokedEvent("Sub_raw", "input"),
            new MAFWorkflows.SubworkflowErrorEvent("Sub_raw", new InvalidOperationException("nested failure")),
        };
        var adapter = new MAFWorkflowAdapter("nested error", (_, ct) =>
            MAFWorkflowEventBridge.TranslateEventsAsync(RecordingEvents(events), new HashSet<string>(),
                new Dictionary<string, string> { ["Sub_raw"] = "Sub" }, ct), ["Sub"]);
        var result = await adapter.ExecuteWorkflowAsync("input").WaitAsync(TimeSpan.FromSeconds(15));
        var error = Assert.Single(result.Errors!);
        Assert.Equal("Sub", error.ExecutorId);
        Assert.Equal("nested failure", error.Message);
        Assert.Equal(nameof(InvalidOperationException), error.ExceptionType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FromMAFWorkflow_ToolCallWithOptionalNullResult_RecordsIntentAndExecutionSeparately(bool hasResult)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var agent = new RecordingToolAgent(hasResult).BindAsExecutor(emitEvents: true);
        var end = new FakeStreamableAIAgent("End", "done").BindAsExecutor(emitEvents: true);
        var workflow = new MAFWorkflows.WorkflowBuilder(agent).AddEdge(agent, end).WithOutputFrom(end).Build();
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "tool evidence", ["Caller", "End"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        var caller = Assert.Single(result.Steps, s => s.ExecutorId == "Caller");
        var call = Assert.Single(caller.ToolCalls!);
        Assert.Equal("lookup", call.Name);
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("Caller", call.ExecutorId);
        Assert.Equal(hasResult, call.WasExecuted);
        Assert.Null(call.Result);
        Assert.Equal(hasResult, call.EndTime.HasValue);
        Assert.Equal(hasResult, call.HasTiming);
        if (hasResult) Assert.Null(Assert.Single(result.Steps, s => s.ExecutorId == "End").ToolCalls);
        else Assert.Contains(result.Errors!, e => e.Message.Contains("agent failed before tool result"));
    }

    private static MAFWorkflowAdapter RecordingAdapter(IEnumerable<MAFWorkflows.WorkflowEvent> events,
        IEnumerable<string> chatIds, string[] ids) => new("recording", (_, ct) =>
            MAFWorkflowEventBridge.TranslateEventsAsync(RecordingEvents(events), chatIds.ToHashSet(), cancellationToken: ct), ids);

    private static async IAsyncEnumerable<MAFWorkflows.WorkflowEvent> RecordingEvents(IEnumerable<MAFWorkflows.WorkflowEvent> events)
    {
        foreach (var evt in events)
        {
            await Task.Yield();
            yield return evt;
        }
    }

    private sealed class RecordingToolAgent(bool hasResult) : FakeStreamableAIAgent("Caller", "")
    {
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new AgentResponseUpdate
            {
                AgentId = Id, AuthorName = Name, MessageId = "m1", ResponseId = "r1",
                Role = ChatRole.Assistant, Contents = [new FunctionCallContent("call-1", "lookup")]
            };
            if (!hasResult) throw new InvalidOperationException("agent failed before tool result");
            yield return new AgentResponseUpdate
            {
                AgentId = Id, AuthorName = Name, MessageId = "m2", ResponseId = "r1",
                Role = ChatRole.Tool, Contents = [new FunctionResultContent("call-1", null)]
            };
            yield return new AgentResponseUpdate
            {
                AgentId = Id, AuthorName = Name, MessageId = "m3", ResponseId = "r1",
                Contents = [new TextContent("requested")], Role = ChatRole.Assistant, FinishReason = ChatFinishReason.Stop
            };
        }
    }
}
