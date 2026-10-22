// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentEval.MAF;
using AgentEval.Assertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public sealed class MAFWorkflowBuilderShapesTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("sequential")]
    [InlineData("concurrent")]
    [InlineData("handoff")]
    [InlineData("group-chat")]
    public async Task FromMAFWorkflow_AgentBuilder_RecordsRealTurnsAndInternalOrchestration(string shape)
    {
        using var leftClient = new BuilderChatClient("Left", shape == "handoff");
        using var rightClient = new BuilderChatClient("Right");
        AIAgent left = new ChatClientAgent(leftClient, name: "Left");
        AIAgent right = new ChatClientAgent(rightClient, name: "Right");
        MAFWorkflows.Workflow workflow = shape switch
        {
            "sequential" => MAFWorkflows.AgentWorkflowBuilder.BuildSequential([left, right]),
            "concurrent" => MAFWorkflows.AgentWorkflowBuilder.BuildConcurrent([left, right]),
            "handoff" => MAFWorkflows.AgentWorkflowBuilder.CreateHandoffBuilderWith(left)
                .WithHandoff(left, right).EmitAgentResponseEvents().EmitAgentResponseUpdateEvents().Build(),
            "group-chat" => MAFWorkflows.AgentWorkflowBuilder.CreateGroupChatBuilderWith(
                agents => new MAFWorkflows.RoundRobinGroupChatManager(agents) { MaximumIterationCount = 3 })
                .AddParticipants([left, right]).Build(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        string[] order = shape switch
        {
            "sequential" => ["Left", "Right", "OutputMessages"],
            "concurrent" => ["Start", "Left", "Right", "Batcher/Left", "Batcher/Right", "ConcurrentEnd"],
            "handoff" => ["HandoffStart", "Left", "Right", "HandoffEnd"],
            _ => ["GroupChatHost", "Left", "Right"]
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, shape, order)
            .ExecuteWorkflowAsync("builder input marker", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));

        output.WriteLine(JsonSerializer.Serialize(new
        {
            shape, leftReplies = leftClient.Replies, rightReplies = rightClient.Replies, leftClient.Handoffs,
            result.FinalOutput, result.Errors,
            steps = result.Steps.Select(step => new { step.ExecutorId, step.Output, step.StartOffset, step.Duration, step.TimingNotMeasuredReason }),
            edges = result.Graph?.TraversedEdges?.Select(edge => new { edge.SourceExecutorId, edge.TargetExecutorId, edge.EdgeType })
        }));
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors?.Select(error => error.Message) ?? []));
        Assert.Equal(1, rightClient.Replies);
        Assert.Equal(shape == "group-chat" ? 2 : shape == "handoff" ? 0 : 1, leftClient.Replies);
        Assert.Equal(shape == "handoff" ? 1 : 0, leftClient.Handoffs);
        foreach (var (name, count) in new[] { ("Left", leftClient.Replies), ("Right", rightClient.Replies) })
        {
            var replies = result.Steps.Where(step => step.ExecutorId == name && !string.IsNullOrEmpty(step.Output)).ToArray();
            Assert.Equal(Enumerable.Range(1, count).Select(turn => $"{name}: reply {turn}"), replies.Select(step => step.Output));
            Assert.All(replies, step =>
            {
                Assert.Null(step.TimingNotMeasuredReason);
                Assert.True(step.Duration >= TimeSpan.FromMilliseconds(10));
            });
        }
        Assert.DoesNotContain("builder input marker", result.FinalOutput);
        Assert.Contains(shape == "group-chat" ? "Left: reply 2" : "Right: reply 1", result.FinalOutput);
        Assert.DoesNotContain("HandoffState", result.FinalOutput);
        Assert.NotEmpty(result.Graph!.TraversedEdges!);
        Assert.Equal(shape == "concurrent", result.HasParallelExecution);
        if (shape == "concurrent") Assert.True(result.Should().HaveParallelBranchCount(2).IsValid);
        Assert.All(result.Steps, step => Assert.Contains(step.ExecutorId, order));

        var observedOrder = result.Steps.Select(step => step.ExecutorId).ToArray();
        if (shape == "group-chat")
        {
            // GroupChatHost is registered by MAF as the base Executor type. Its initial accumulation
            // and turn-token deliveries can occupy adjacent activations when they span supersteps.
            observedOrder = observedOrder.Where((id, index) => id != "GroupChatHost" || index == 0 || observedOrder[index - 1] != id).ToArray();
            Assert.Equal(["GroupChatHost", "Left", "GroupChatHost", "Right", "GroupChatHost", "Left", "GroupChatHost"], observedOrder);
        }
        else Assert.Equal(order, observedOrder);

        string[] expectedEdges = shape switch
        {
            "sequential" => ["Left->Right:Sequential", "Right->OutputMessages:Sequential"],
            "concurrent" => ["Start->Left:ParallelFanOut", "Start->Right:ParallelFanOut", "Left->Batcher/Left:Sequential",
                "Right->Batcher/Right:Sequential", "Batcher/Left->ConcurrentEnd:ParallelFanIn", "Batcher/Right->ConcurrentEnd:ParallelFanIn"],
            // MAF exposes its switch selector edges as fan-outs; only the selected target ran.
            "handoff" => ["HandoffStart->Left:Sequential", "Left->Right:ParallelFanOut", "Right->HandoffEnd:ParallelFanOut"],
            _ => ["GroupChatHost->Left:Sequential", "Left->GroupChatHost:Sequential", "GroupChatHost->Right:Sequential",
                "Right->GroupChatHost:Sequential", "GroupChatHost->Left:Sequential", "Left->GroupChatHost:Sequential"]
        };
        Assert.Equal(expectedEdges.Order(StringComparer.Ordinal), result.Graph.TraversedEdges!
            .Select(edge => $"{edge.SourceExecutorId}->{edge.TargetExecutorId}:{edge.EdgeType}").Order(StringComparer.Ordinal));
        Assert.All(result.Graph.TraversedEdges!, edge =>
        {
            Assert.Equal(edge.SourceExecutorId, result.Steps[edge.SourceStepIndex].ExecutorId);
            Assert.Equal(edge.TargetExecutorId, result.Steps[edge.TargetStepIndex].ExecutorId);
        });
        string[] expectedOutput = shape switch
        {
            "handoff" => ["Right: reply 1"],
            "group-chat" => ["Left: reply 1", "Right: reply 1", "Left: reply 2"],
            _ => ["Left: reply 1", "Right: reply 1"]
        };
        var outputLines = result.FinalOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        if (shape == "concurrent") Assert.Equal(expectedOutput.Order(), outputLines.Order());
        else Assert.Equal(expectedOutput, outputLines);
        Assert.All(result.Steps, step => Assert.True(result.TotalDuration >= step.StartOffset + step.Duration));
        Assert.False(result.Should().ForExecutor("Right").HaveCompletedWithin(TimeSpan.FromMilliseconds(1)).And().IsValid);
    }

    private sealed class BuilderChatClient(string name, bool handoff = false) : IChatClient
    {
        public int Replies { get; private set; }
        public int Handoffs { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
            var id = Guid.NewGuid().ToString("N");
            if (handoff && Handoffs == 0)
            {
                var tool = Assert.Single(options!.Tools!, tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal));
                Handoffs++;
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("handoff-call", tool.Name, new Dictionary<string, object?> { ["reasonForHandoff"] = "route to Right" })])
                    { MessageId = id, ResponseId = id };
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.ToolCalls, MessageId = id, ResponseId = id };
                yield break;
            }
            Replies++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"{name}: reply {Replies}") { MessageId = id, ResponseId = id };
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, MessageId = id, ResponseId = id };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
