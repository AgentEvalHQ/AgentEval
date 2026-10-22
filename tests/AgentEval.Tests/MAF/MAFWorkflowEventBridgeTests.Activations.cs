// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public partial class MAFWorkflowEventBridgeTests
{
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
