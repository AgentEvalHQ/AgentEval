// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF;
using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Models;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public partial class MAFWorkflowEventBridgeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranslateEvents_ToolFailureCaughtByAgent_PreservesExecutionError(bool lateResult)
    {
        var (result, _) = await RecordToolOutcome(lateResult, failed: true);
        var call = Assert.Single(result.ToolUsage!.Calls, call => call.Name == "lookup");

        Assert.True(result.IsSuccess); // The agent recovered; this is not an executor failure.
        Assert.True(call.WasExecuted);
        Assert.IsType<InvalidOperationException>(call.Exception);
        Assert.Equal("lookup failed", call.Exception!.Message);
        Assert.Equal("diagnostic", call.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolSuccessMetric_CaughtWorkflowToolFailure_DoesNotClaimSuccess(bool lateResult)
    {
        var (result, _) = await RecordToolOutcome(lateResult, failed: true);
        var metric = await new ToolSuccessMetric().EvaluateAsync(new EvaluationContext
        {
            Input = "input", Output = result.FinalOutput, ToolUsage = result.ToolUsage,
        });

        Assert.Equal(0, metric.Score);
        Assert.False(metric.Passed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkflowTrace_CaughtToolFailure_PersistsFailedExecution(bool lateResult)
    {
        var (_, trace) = await RecordToolOutcome(lateResult, failed: true);
        using var serialized = new MemoryStream();
        await WorkflowTraceSerializer.SerializeAsync(trace, serialized);
        serialized.Position = 0;
        var reloaded = await WorkflowTraceSerializer.DeserializeAsync(serialized);
        var call = Assert.Single(reloaded.Steps.SelectMany(step => step.ToolCalls ?? []));

        Assert.True(call.WasExecuted);
        Assert.False(call.Succeeded);
        Assert.Equal("lookup failed", call.Error);
        Assert.Equal("diagnostic", call.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranslateEvents_NullToolResultAndUnfinishedIntent_RemainDistinct(bool lateResult)
    {
        var (result, trace) = await RecordToolOutcome(lateResult, failed: false);
        var executed = Assert.Single(result.ToolUsage!.Calls, call => call.Name == "lookup");
        var intent = Assert.Single(result.ToolUsage.Calls, call => call.Name == "pending");

        Assert.True(executed.WasExecuted);
        Assert.Null(executed.Result);
        Assert.Null(executed.Exception);
        Assert.False(intent.WasExecuted);
        Assert.Null(intent.Exception);
        var calls = trace.Steps.SelectMany(step => step.ToolCalls ?? []).ToList();
        Assert.True(Assert.Single(calls, call => call.Name == "lookup").Succeeded);
        Assert.False(Assert.Single(calls, call => call.Name == "pending").Succeeded);
    }

    private static async Task<(WorkflowExecutionResult Result, WorkflowTrace Trace)> RecordToolOutcome(bool lateResult, bool failed)
    {
        var pairedResult = new MAFWorkflows.AgentResponseUpdateEvent("Writer", new AgentResponseUpdate
        {
            Contents = [new FunctionResultContent("lookup-id", failed ? "diagnostic" : null)
            {
                Exception = failed ? new InvalidOperationException("lookup failed") : null,
            }],
        });
        var events = new List<MAFWorkflows.WorkflowEvent>
        {
            new MAFWorkflows.ExecutorInvokedEvent("Writer", new MAFWorkflows.TurnToken(true)),
            new MAFWorkflows.AgentResponseUpdateEvent("Writer", new AgentResponseUpdate
                { Contents = [new FunctionCallContent("lookup-id", "lookup")] }),
        };
        if (!failed)
            events.Add(new MAFWorkflows.AgentResponseUpdateEvent("Writer", new AgentResponseUpdate
                { Contents = [new FunctionCallContent("pending-id", "pending")] }));
        if (!lateResult) events.Add(pairedResult);
        events.Add(new MAFWorkflows.AgentResponseEvent("Writer",
            new Microsoft.Agents.AI.AgentResponse(new ChatMessage(ChatRole.Assistant, "recovered"))));
        events.Add(new MAFWorkflows.ExecutorCompletedEvent("Writer", null));
        events.Add(new MAFWorkflows.SuperStepCompletedEvent(0));
        if (lateResult) events.Add(pairedResult);

        await using var recorder = new WorkflowTraceRecorder(RecordingAdapter(events, ["Writer"], ["Writer"]), "tool-outcome");
        var result = await recorder.ExecuteWorkflowAsync("input");
        return (result, recorder.Trace);
    }

    [Fact]
    public async Task TranslateEvents_InterleavedResponses_PreserveUsageAndDuplicateCallIdsPerExecutor()
    {
        MAFWorkflows.AgentResponseUpdateEvent Call(string id) => new(id, new AgentResponseUpdate
            { Contents = [new FunctionCallContent("shared-id", "lookup-" + id)] });
        MAFWorkflows.AgentResponseUpdateEvent Result(string id) => new(id, new AgentResponseUpdate
            { Contents = [new FunctionResultContent("shared-id", "result-" + id)] });
        MAFWorkflows.AgentResponseEvent Response(string id, int tokens) => new(id,
            new Microsoft.Agents.AI.AgentResponse(new ChatMessage(ChatRole.Assistant, "reply-" + id))
            { Usage = new UsageDetails { InputTokenCount = tokens, OutputTokenCount = 1 }, FinishReason = ChatFinishReason.Stop });
        var events = new MAFWorkflows.WorkflowEvent[]
        {
            new MAFWorkflows.ExecutorInvokedEvent("Left", new MAFWorkflows.TurnToken(true)),
            new MAFWorkflows.ExecutorInvokedEvent("Right", new MAFWorkflows.TurnToken(true)),
            Call("Left"), Call("Right"), Result("Left"), Result("Right"), Response("Left", 11), Response("Right", 22),
            new MAFWorkflows.ExecutorCompletedEvent("Right", null), new MAFWorkflows.ExecutorCompletedEvent("Left", null)
        };
        var result = await RecordingAdapter(events, ["Left", "Right"], ["Left", "Right"]).ExecuteWorkflowAsync("input");

        Assert.Equal(2, result.Steps.Count);
        foreach (var (id, tokens) in new[] { ("Left", 11), ("Right", 22) })
        {
            var step = Assert.Single(result.Steps, step => step.ExecutorId == id);
            Assert.Equal("reply-" + id, step.Output);
            Assert.Equal(tokens, step.TokenUsage!.PromptTokens);
            Assert.Equal(1, step.TokenUsage.CompletionTokens);
            Assert.Equal("stop", step.FinishReason);
            var call = Assert.Single(step.ToolCalls!);
            Assert.Equal("lookup-" + id, call.Name);
            Assert.Equal("result-" + id, call.Result);
            Assert.True(call.WasExecuted);
        }
    }

    [Fact]
    public async Task TranslateEvents_StreamThrowsAfterPartialTurn_PreservesOutputAndUnfinishedToolIntent()
    {
        var adapter = new MAFWorkflowAdapter("broken stream", (_, ct) =>
            MAFWorkflowEventBridge.TranslateEventsAsync(Broken(), new HashSet<string> { "Writer" }, cancellationToken: ct));
        var result = await adapter.ExecuteWorkflowAsync("input");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, error => error.Message.Contains("stream broke"));
        var step = Assert.Single(result.Steps);
        Assert.Equal("partial", step.Output);
        Assert.False(Assert.Single(step.ToolCalls!).WasExecuted);

        static async IAsyncEnumerable<MAFWorkflows.WorkflowEvent> Broken()
        {
            await Task.Yield();
            yield return new MAFWorkflows.ExecutorInvokedEvent("Writer", new MAFWorkflows.TurnToken(true));
            yield return new MAFWorkflows.AgentResponseUpdateEvent("Writer", new AgentResponseUpdate
            {
                Contents = [new TextContent("partial"), new FunctionCallContent("pending", "lookup")]
            });
            throw new InvalidOperationException("stream broke");
        }
    }

    [Fact]
    public async Task TranslateEvents_TwoAgentTurnsInOneSuperstep_PreservesBothActivations()
    {
        var events = SuperStep(0,
            AgentDelivery("Join", new MAFWorkflows.TurnToken(true), "first turn"),
            AgentDelivery("Join", new MAFWorkflows.TurnToken(true), "second turn"));
        var result = await RecordingAdapter(events, ["Join"], ["Join"]).ExecuteWorkflowAsync("input");

        Assert.Equal(["first turn", "second turn"], result.Steps.Select(step => step.Output));
        Assert.Equal([0, 1], result.Steps.Select(step => step.StepIndex));
    }

    [Fact]
    public async Task TranslateEvents_CustomExecutorEvent_DoesNotAttributeDataToOpenSibling()
    {
        var events = new MAFWorkflows.WorkflowEvent[]
        {
            new MAFWorkflows.ExecutorInvokedEvent("Left", "input"),
            new MAFWorkflows.ExecutorInvokedEvent("Right", "input"),
            new MAFWorkflows.ExecutorEvent("Left", "left-custom"),
            new MAFWorkflows.ExecutorCompletedEvent("Right", null),
            new MAFWorkflows.ExecutorCompletedEvent("Left", null),
        };
        var result = await RecordingAdapter(events, [], ["Left", "Right"]).ExecuteWorkflowAsync("input");

        Assert.Equal(["Left:left-custom", "Right:"], result.Steps.Select(step => $"{step.ExecutorId}:{step.Output}"));
        Assert.DoesNotContain(result.Steps, step => step.ExecutorId == "Right" && step.Output.Contains("left-custom"));
    }
}
