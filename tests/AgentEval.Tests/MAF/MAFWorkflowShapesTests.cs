// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF;
using AgentEval.Models;
using Microsoft.Agents.AI;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using static Microsoft.Agents.AI.Workflows.OpenTelemetryWorkflowBuilderExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

/// <summary>Offline shape contracts against real MAF execution, including its message-delivery protocol.</summary>
public sealed partial class MAFWorkflowShapesTests : IDisposable
{
    private readonly MAFWorkflowInstrumentation _timing = new();
    public void Dispose() => _timing.Dispose();

    private static readonly TimeSpan WorkDelay = TimeSpan.FromMilliseconds(20);

    [Theory]
    [InlineData("function-chain")]
    [InlineData("agent-chain")]
    [InlineData("function-loop")]
    [InlineData("agent-loop")]
    public async Task ExecuteWorkflowAsync_CoreShape_RecordsExactStepsEdgesOutputAndTiming(string shape)
    {
        var (workflow, ids, expected) = CreateCoreShape(shape);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, shape, ids)
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(25));

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors?.Select(e => e.Message) ?? []));
        Assert.Equal(expected, result.Steps.Select(step => (step.ExecutorId, step.Output)));
        Assert.Equal(Enumerable.Range(0, expected.Length), result.Steps.Select(step => step.StepIndex));
        Assert.Equal(expected[^1].Output, result.FinalOutput);

        var expectedEdges = expected.Zip(expected.Skip(1), (source, target) => (source.ExecutorId, target.ExecutorId));
        Assert.Equal(expectedEdges, result.Graph!.TraversedEdges!.Select(edge => (edge.SourceExecutorId, edge.TargetExecutorId)));
        Assert.Equal(Enumerable.Range(0, expected.Length - 1), result.Graph.TraversedEdges.Select(edge => edge.SourceStepIndex));
        Assert.Equal(Enumerable.Range(1, expected.Length - 1), result.Graph.TraversedEdges.Select(edge => edge.TargetStepIndex));

        var previousEnd = TimeSpan.Zero;
        foreach (var step in result.Steps)
        {
            Assert.True(step.StartOffset >= previousEnd, $"{shape}: {step.ExecutorId} overlaps the preceding sequential step.");
            Assert.True(step.Duration >= WorkDelay / 2, $"{shape}: {step.ExecutorId} lost its measured work duration.");
            previousEnd = step.StartOffset + step.Duration;
        }
        Assert.True(result.TotalDuration >= previousEnd);
    }

    private (MAFWorkflows.Workflow Workflow, string[] Ids, (string ExecutorId, string Output)[] Expected)
        CreateCoreShape(string shape)
    {
        var isAgent = shape.StartsWith("agent", StringComparison.Ordinal);
        var isLoop = shape.EndsWith("loop", StringComparison.Ordinal);
        if (!isLoop)
        {
            string[] ids = ["First", "Middle", "Last"];
            var bindings = ids.Select(id => isAgent ? Agent(id) : Function(id, () => id + " reply")).ToArray();
            var workflow = new MAFWorkflows.WorkflowBuilder(bindings[0])
                .AddEdge(bindings[0], bindings[1]).AddEdge(bindings[1], bindings[2])
                .WithOutputFrom(bindings[2]).BuildWithTiming(_timing);
            return (workflow, ids, ids.Select(id => (id, isAgent ? BridgeScriptedChatClient.FinalReply(id, 1) : id + " reply")).ToArray());
        }

        var turns = 0;
        var client = new BridgeScriptedChatClient("Loop", [], WorkDelay);
        var loop = isAgent
            ? new ChatClientAgent(client, name: "Loop").BindAsExecutor(new MAFWorkflows.AIAgentHostOptions
                { EmitAgentUpdateEvents = true, ForwardIncomingMessages = false })
            : Function("Loop", () => $"pass-{++turns}");
        var last = isAgent ? Agent("Last") : Function("Last", () => "done");
        var builder = new MAFWorkflows.WorkflowBuilder(loop);
        if (isAgent)
            builder.AddEdge<object>(loop, loop, _ => client.Turns < 2).AddEdge<object>(loop, last, _ => client.Turns >= 2);
        else
            builder.AddEdge<string>(loop, loop, _ => turns < 2).AddEdge<string>(loop, last, _ => turns >= 2);
        return (builder.WithOutputFrom(last).BuildWithTiming(_timing), ["Loop", "Last"], isAgent
            ? [("Loop", BridgeScriptedChatClient.FinalReply("Loop", 1)), ("Loop", BridgeScriptedChatClient.FinalReply("Loop", 2)),
                ("Last", BridgeScriptedChatClient.FinalReply("Last", 1))]
            : [("Loop", "pass-1"), ("Loop", "pass-2"), ("Last", "done")]);
    }

    private static MAFWorkflows.ExecutorBinding Agent(string id) =>
        new ChatClientAgent(new BridgeScriptedChatClient(id, [], WorkDelay), name: id).BindAsExecutor(emitEvents: true);

    private static MAFWorkflows.ExecutorBinding Function(string id, Func<string> output) =>
        ((Func<string, CancellationToken, ValueTask<string>>)(async (_, ct) =>
        {
            await Task.Delay(WorkDelay, ct);
            return output();
        })).BindAsExecutor<string, string>(id);
}
