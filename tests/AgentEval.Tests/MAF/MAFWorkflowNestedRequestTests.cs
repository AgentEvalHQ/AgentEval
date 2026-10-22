// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF;
using AgentEval.Assertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using static Microsoft.Agents.AI.Workflows.OpenTelemetryWorkflowBuilderExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public sealed class MAFWorkflowNestedRequestTests : IDisposable
{
    private readonly MAFWorkflowInstrumentation _timing = new();
    public void Dispose() => _timing.Dispose();

    [Fact]
    public async Task FromMAFWorkflow_ParallelNestedHosts_ChildrenBelongToTheObservedParentBranch()
    {
        var start = Function("Start", value => value);
        MAFWorkflows.SubworkflowBinding Host(string id)
        {
            var child = Function(id + "Work", value => id + " reply");
            return new MAFWorkflows.SubworkflowBinding(new MAFWorkflows.WorkflowBuilder(child).WithOutputFrom(child).BuildWithTiming(_timing), id, null);
        }
        var left = Host("Left");
        var right = Host("Right");
        var workflow = new MAFWorkflows.WorkflowBuilder(start).AddFanOutEdge(start, [left, right]).WithOutputFrom(left, right).BuildWithTiming(_timing);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "nested branches", [])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.IsSuccess);
        Assert.True(result.Should().HaveParallelExecution().HaveParallelBranchCount(2).IsValid);
        foreach (var id in new[] { "Left", "Right" })
        {
            var parent = Assert.Single(result.Steps, step => step.ExecutorId == id);
            var child = Assert.Single(result.Steps, step => step.ExecutorId == id + "/" + id + "Work");
            Assert.NotNull(parent.ParallelBranchId);
            Assert.Equal(parent.ParallelBranchId, child.ParallelBranchId);
        }
    }

    [Fact]
    public async Task FromMAFWorkflow_RepeatedRequests_RecordsDistinctRequestActivationsAndRealLoopEdges()
    {
        var requests = 0;
        var port = MAFWorkflows.RequestPort.Create<string, string>("Approval");
        var processor = Function("Processor", value => "processed " + value);
        var workflow = new MAFWorkflows.WorkflowBuilder(port).AddEdge(port, processor)
            .AddEdge<string>(processor, port, _ => requests < 2).WithOutputFrom(processor).BuildWithTiming(_timing);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "two approvals", [], null, (request, _) =>
            ValueTask.FromResult<MAFWorkflows.ExternalResponse?>(request.CreateResponse("yes " + ++requests)));
        var result = await adapter.ExecuteWorkflowAsync("approve", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.IsSuccess);
        Assert.Equal(2, requests);
        Assert.Equal(["Approval", "Processor", "Approval", "Processor"], result.Steps.Select(step => step.ExecutorId));
        Assert.Equal("yes 1", result.Steps[0].Output);
        Assert.Equal("yes 2", result.Steps[2].Output);
        Assert.Equal("processed yes 2", result.FinalOutput);
        Assert.DoesNotContain(result.Graph!.TraversedEdges!, edge => edge.SourceExecutorId == edge.TargetExecutorId);
        Assert.Contains(result.Graph.TraversedEdges!, edge => edge.SourceExecutorId == "Processor" && edge.TargetExecutorId == "Approval");
    }

    [Fact]
    public async Task StreamAsAgentEvalEvents_NestedExplicitIdMap_PreservesRequestedChildDisplayName()
    {
        var adapter = new MAFWorkflowAdapter("direct bridge", (input, ct) => MAFWorkflowEventBridge.StreamAsAgentEvalEvents(
            NestedChain(), input, ct, new Dictionary<string, string> { ["Sub"] = "Container", ["A"] = "First", ["B"] = "Last" }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await adapter.ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.IsSuccess);
        Assert.Equal(["Container", "Container/First", "Container/Last"], result.Steps.Select(step => step.ExecutorId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FromMAFWorkflow_Responder_AnswersRootAndNestedPorts(bool nested)
    {
        var workflow = RequestWorkflow(nested);
        var calls = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "answer", [], null, async (request, ct) =>
        {
            calls++;
            Assert.Equal(nested ? "ApprovalBridge" : "Approval", request.PortInfo.PortId);
            Assert.True(request.TryGetDataAs<string>(out var value));
            Assert.Equal("approve", value);
            await Task.Delay(30, ct);
            return request.CreateResponse("yes");
        });
        var result = await adapter.ExecuteWorkflowAsync("approve", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors?.Select(error => error.Message) ?? []));
        Assert.Equal(1, calls);
        var prefix = nested ? "Sub/" : "";
        var port = Assert.Single(result.Steps, step => step.ExecutorId == prefix + "Approval");
        Assert.Equal("yes", port.Output);
        Assert.Null(port.TimingNotMeasuredReason);
        Assert.True(port.Duration >= TimeSpan.FromMilliseconds(25));
        Assert.Equal("processed yes", result.FinalOutput);
        Assert.DoesNotContain(result.Graph!.TraversedEdges!, edge => edge.SourceExecutorId == edge.TargetExecutorId);
        Assert.Contains(result.Graph.TraversedEdges!, edge => edge.SourceExecutorId == prefix + "Approval" && edge.TargetExecutorId == prefix + "Processor");
        if (nested)
        {
            var host = Assert.Single(result.Steps, step => step.ExecutorId == "Sub");
            Assert.Null(host.TimingNotMeasuredReason);
            Assert.True(host.Duration >= port.Duration);
        }
    }

    [Theory]
    [InlineData("declined")]
    [InlineData("throws")]
    [InlineData("wrong-request")]
    [InlineData("wrong-port")]
    public async Task FromMAFWorkflow_UnusableResponse_FailsPromptlyWithRequestIdentity(string failure)
    {
        string? requestId = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(RequestWorkflow(false), "bad response", [], null, (request, _) =>
        {
            requestId = request.RequestId;
            if (failure == "throws") throw new InvalidOperationException("responder broke");
            var response = failure == "declined" ? null : request.CreateResponse("yes");
            if (failure == "wrong-request") response = response! with { RequestId = "another request" };
            if (failure == "wrong-port") response = response! with { PortInfo = response.PortInfo with { PortId = "Other" } };
            return ValueTask.FromResult(response);
        });
        var result = await adapter.ExecuteWorkflowAsync("approve", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(cancellation.IsCancellationRequested);
        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors!);
        Assert.Contains(requestId!, error.Message);
        Assert.Contains("Approval", error.Message);
        Assert.DoesNotContain(result.Steps, step => step.ExecutorId == "Processor");
    }

    [Fact]
    public async Task FromMAFWorkflow_ResponderCancellation_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(RequestWorkflow(false), "cancel responder", [], null, async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ExecuteWorkflowAsync("approve", cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void FromMAFWorkflow_NullResponder_RejectsMissingCallbackWithoutAmbiguousLegacyOverload()
    {
        var workflow = RequestWorkflow(false);
        Assert.NotNull(MAFWorkflowAdapter.FromMAFWorkflow(workflow, "legacy", [], null));
        Assert.Throws<ArgumentNullException>(() => MAFWorkflowAdapter.FromMAFWorkflow(workflow, "invalid", [], null, null!));
    }

    [Fact]
    public void FromMAFWorkflow_CollidingDisplayNames_RejectsMergedExecutorIdentity()
    {
        var first = Function("Worker_a", value => value);
        var second = Function("Worker_b", value => value);
        var workflow = new MAFWorkflows.WorkflowBuilder(first).AddEdge(first, second).WithOutputFrom(second).BuildWithTiming(_timing);
        var error = Assert.Throws<ArgumentException>(() => MAFWorkflowAdapter.FromMAFWorkflow(workflow, "ambiguous names", ["Worker"]));
        Assert.Contains("distinct", error.Message);
        Assert.Contains("Worker", error.Message);
    }

    [Fact]
    public void FromMAFWorkflow_RootExecutorWithSlash_PreservesExplicitDisplayMapping()
    {
        // MAF's concurrent builder uses root executor IDs such as Batcher/Left_<guid>.
        // A slash in a raw ID is not itself evidence of subworkflow containment.
        var worker = Function("Batcher/Left_raw", value => value);
        var workflow = new MAFWorkflows.WorkflowBuilder(worker).WithOutputFrom(worker).BuildWithTiming(_timing);
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "slash name", ["Batcher/Left"]);
        Assert.Equal("Batcher/Left", adapter.GraphDefinition!.EntryNodeId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown-agent")]
    public void RestoreNestedAgentEvent_UnidentifiableAgent_RefusesFalseAttribution(string? agentId)
    {
        var topology = MAFWorkflowTopology.Create(NestedChain());
        var update = new AgentResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "unattributed") { AgentId = agentId };
        var evt = new MAFWorkflows.WorkflowOutputEvent(update, "Sub");
        var error = Assert.Throws<NotSupportedException>(() => topology.RestoreNestedAgentEvent(evt));
        Assert.Contains("identifiable nested agent", error.Message);
        Assert.Contains("Sub", error.Message);
    }

    [Fact]
    public async Task FromMAFWorkflow_WrongResponsePayload_FailsWithoutWaitingForTimeout()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(RequestWorkflow(false), "wrong payload", [], null,
            (request, _) => ValueTask.FromResult<MAFWorkflows.ExternalResponse?>(request.CreateResponse(42)));
        var result = await adapter.ExecuteWorkflowAsync("approve", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(cancellation.IsCancellationRequested);
        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors!);
        Assert.DoesNotContain(result.Steps, step => step.ExecutorId == "Processor");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranslateEvents_IncompleteNestedStream_HostCannotPassTimeLimit(bool childCompleted)
    {
        var clock = new FakeTimeProvider();
        var adapter = new MAFWorkflowAdapter("incomplete nested", (_, ct) => MAFWorkflowEventBridge.TranslateEventsAsync(
            Events(), new HashSet<string>(), new Dictionary<string, string> { ["Host"] = "Sub", ["Child"] = "Sub/Child" },
            ct, timeProvider: clock, subworkflowExecutorIds: new HashSet<string> { "Host" },
            parentExecutorIds: new Dictionary<string, string> { ["Child"] = "Host" }));
        var result = await adapter.ExecuteWorkflowAsync("input");
        var host = Assert.Single(result.Steps, step => step.ExecutorId == "Sub");
        Assert.NotNull(host.TimingNotMeasuredReason);
        Assert.False(result.Should().ForExecutor("Sub").HaveCompletedWithin(TimeSpan.FromHours(1)).And().IsValid);
        if (childCompleted) Assert.False(result.IsSuccess);

        async IAsyncEnumerable<MAFWorkflows.WorkflowEvent> Events()
        {
            await Task.Yield();
            yield return new MAFWorkflows.ExecutorInvokedEvent("Host", "input");
            yield return new MAFWorkflows.ExecutorCompletedEvent("Host", null);
            yield return new MAFWorkflows.ExecutorInvokedEvent("Child", "input");
            clock.Advance(TimeSpan.FromSeconds(3));
            if (childCompleted)
            {
                yield return new MAFWorkflows.ExecutorCompletedEvent("Child", "partial run");
                throw new InvalidOperationException("stream broke before the nested run ended");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FromMAFWorkflow_UnexposedNestedRequest_FailsPromptlyInsteadOfHanging(bool responder)
    {
        var port = MAFWorkflows.RequestPort.Create<string, string>("Approval");
        var inner = new MAFWorkflows.WorkflowBuilder(port).WithOutputFrom(port).BuildWithTiming(_timing);
        var host = new MAFWorkflows.SubworkflowBinding(inner, "Sub", null);
        var workflow = new MAFWorkflows.WorkflowBuilder(host).WithOutputFrom(host).BuildWithTiming(_timing);
        var calls = 0;
        var adapter = responder
            ? MAFWorkflowAdapter.FromMAFWorkflow(workflow, "missing relay", [], null, (request, _) =>
                { calls++; return ValueTask.FromResult<MAFWorkflows.ExternalResponse?>(request.CreateResponse("yes")); })
            : MAFWorkflowAdapter.FromMAFWorkflow(workflow, "missing relay", []);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await adapter.ExecuteWorkflowAsync("approve", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(cancellation.IsCancellationRequested);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, calls);
        var error = Assert.Single(result.Errors!);
        Assert.Equal("Sub/Approval", error.ExecutorId);
        Assert.Contains("relay", error.Message);
        Assert.All(result.Steps, step => Assert.NotNull(step.TimingNotMeasuredReason));
    }

    [Fact]
    public async Task FromMAFWorkflow_TwoNestedLevels_QualifiesPathsAndTimesBothHosts()
    {
        var child = Function("Child", value => value + " done");
        var leaf = new MAFWorkflows.WorkflowBuilder(child).WithOutputFrom(child).BuildWithTiming(_timing);
        var innerHost = new MAFWorkflows.SubworkflowBinding(leaf, "Inner", null);
        var prepare = Function("Prepare", value => value);
        var middle = new MAFWorkflows.WorkflowBuilder(prepare).AddEdge(prepare, innerHost).WithOutputFrom(innerHost).BuildWithTiming(_timing);
        var outerHost = new MAFWorkflows.SubworkflowBinding(middle, "Outer", null);
        var workflow = new MAFWorkflows.WorkflowBuilder(outerHost).WithOutputFrom(outerHost).BuildWithTiming(_timing);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "deep", ["Outer", "Outer/Prepare", "Outer/Inner", "Outer/Inner/Child"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.IsSuccess);
        Assert.Equal(["Outer", "Outer/Prepare", "Outer/Inner", "Outer/Inner/Child"], result.Steps.Select(step => step.ExecutorId));
        Assert.Equal("input done", result.FinalOutput);
        Assert.All(result.Steps, step => Assert.Null(step.TimingNotMeasuredReason));
        Assert.True(result.Steps[0].Duration >= result.Steps[2].Duration);
        Assert.True(result.Steps[2].Duration >= result.Steps[3].Duration);
    }

    [Fact]
    public void FromMAFWorkflow_StackedCatchAllHosts_RefusesMAFSilentInputDrop()
    {
        var child = Function("Child", value => value);
        var leaf = new MAFWorkflows.WorkflowBuilder(child).WithOutputFrom(child).BuildWithTiming(_timing);
        var innerHost = new MAFWorkflows.SubworkflowBinding(leaf, "Inner", null);
        var middle = new MAFWorkflows.WorkflowBuilder(innerHost).WithOutputFrom(innerHost).BuildWithTiming(_timing);
        var outerHost = new MAFWorkflows.SubworkflowBinding(middle, "Outer", null);
        var workflow = new MAFWorkflows.WorkflowBuilder(outerHost).WithOutputFrom(outerHost).BuildWithTiming(_timing);
        var error = Assert.Throws<NotSupportedException>(() => MAFWorkflowAdapter.FromMAFWorkflow(workflow, "unsupported entry", []));
        Assert.Contains("entry executor", error.Message);
        Assert.Contains("Outer", error.Message);
    }

    [Fact]
    public void FromMAFWorkflow_DuplicateRawIdsAcrossNestedScopes_RefusesAmbiguousAttribution()
    {
        var outer = Function("A", value => value);
        var child = Function("A", value => value);
        var inner = new MAFWorkflows.WorkflowBuilder(child).WithOutputFrom(child).BuildWithTiming(_timing);
        var host = new MAFWorkflows.SubworkflowBinding(inner, "Sub", null);
        var workflow = new MAFWorkflows.WorkflowBuilder(outer).AddEdge(outer, host).WithOutputFrom(host).BuildWithTiming(_timing);
        var error = Assert.Throws<NotSupportedException>(() => MAFWorkflowAdapter.FromMAFWorkflow(workflow, "ambiguous", []));
        Assert.Contains("unique", error.Message);
        Assert.Contains("'A'", error.Message);
    }

    [Fact]
    public async Task FromMAFWorkflow_NestedAgent_PreservesChildOutputAndToolsWithoutDuplicatingHostOutput()
    {
        var calls = 0;
        var tool = AIFunctionFactory.Create((int step) => { calls++; return "found"; }, "Lookup");
        var child = new ChatClientAgent(new BridgeScriptedChatClient("Child", ["Lookup"]), name: "Child", tools: [tool]).BindAsExecutor(emitEvents: true);
        var inner = new MAFWorkflows.WorkflowBuilder(child).WithOutputFrom(child).BuildWithTiming(_timing);
        var host = new MAFWorkflows.SubworkflowBinding(inner, "Sub", null);
        var workflow = new MAFWorkflows.WorkflowBuilder(host).WithOutputFrom(host).BuildWithTiming(_timing);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "nested agent", ["Sub", "Sub/Child"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors?.Select(error => error.Message) ?? []));
        var step = Assert.Single(result.Steps, step => step.ExecutorId == "Sub/Child");
        Assert.Equal(BridgeScriptedChatClient.FinalReply("Child", 1), step.Output);
        Assert.Equal(1, calls);
        Assert.True(Assert.Single(step.ToolCalls!).WasExecuted);
        Assert.Equal(step.Output, result.FinalOutput);
        Assert.Null(Assert.Single(result.Steps, step => step.ExecutorId == "Sub").TimingNotMeasuredReason);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_NestedChain_QualifiesChildrenAndMeasuresHostThroughCompletion()
    {
        var workflow = NestedChain();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "nested", ["Sub", "Sub/A", "Sub/B"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(result.IsSuccess);
        Assert.Equal(["Sub", "Sub/A", "Sub/B"], result.Steps.Select(step => step.ExecutorId));
        var host = result.Steps[0];
        Assert.Null(host.TimingNotMeasuredReason);
        Assert.All(result.Steps.Skip(1), child =>
        {
            Assert.Null(child.TimingNotMeasuredReason);
            Assert.True(host.StartOffset <= child.StartOffset);
            Assert.True(host.StartOffset + host.Duration >= child.StartOffset + child.Duration);
        });
        Assert.True(host.Duration >= TimeSpan.FromMilliseconds(40));
        Assert.Equal("input A B", result.FinalOutput);
        Assert.Contains(result.Steps.SelectMany(step => step.OutgoingEdges ?? []), edge => edge.SourceExecutorId == "Sub/A" && edge.TargetExecutorId == "Sub/B");
        Assert.False(result.Should().ForExecutor("Sub").HaveCompletedWithin(TimeSpan.FromMilliseconds(1)).And().IsValid);
    }

    [Fact]
    public async Task TranslateEvents_AnsweredRequest_RecordsOnePortActivationAndItsAnswer()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var port = MAFWorkflows.RequestPort.Create<string, string>("Approval");
        var processor = Function("Processor", value => "processed " + value);
        var workflow = new MAFWorkflows.WorkflowBuilder(port).AddEdge(port, processor).WithOutputFrom(processor).BuildWithTiming(_timing);
        var graph = MAFGraphExtractor.ExtractGraph(workflow, ["Approval", "Processor"]);
        var adapter = new MAFWorkflowAdapter("external responder", (_, ct) =>
            MAFWorkflowEventBridge.TranslateEventsAsync(AnsweredEvents(ct), new HashSet<string>(), cancellationToken: ct, graph: graph));
        var result = await adapter.ExecuteWorkflowAsync("approve", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.IsSuccess);
        Assert.Equal(["Approval", "Processor"], result.Steps.Select(step => step.ExecutorId));
        Assert.Equal("yes", result.Steps[0].Output);
        Assert.Null(result.Steps[0].TimingNotMeasuredReason);
        Assert.True(result.Steps[0].Duration >= TimeSpan.FromMilliseconds(25));
        Assert.Equal("processed yes", result.FinalOutput);
        Assert.DoesNotContain(result.Graph!.TraversedEdges!, edge => edge.SourceExecutorId == edge.TargetExecutorId);

        async IAsyncEnumerable<MAFWorkflows.WorkflowEvent> AnsweredEvents(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await using var run = await MAFWorkflows.InProcessExecution.RunStreamingAsync(workflow, "approve", cancellationToken: ct);
            await foreach (var evt in run.WatchStreamAsync(ct))
            {
                if (evt is MAFWorkflows.RequestInfoEvent request)
                {
                    await Task.Delay(30, ct);
                    await run.SendResponseAsync(request.Request.CreateResponse("yes"));
                }
                else yield return evt;
            }
        }
    }

    private MAFWorkflows.Workflow NestedChain()
    {
        var a = Function("A", value => value + " A");
        var b = Function("B", value => value + " B");
        var inner = new MAFWorkflows.WorkflowBuilder(a).AddEdge(a, b).WithOutputFrom(b).BuildWithTiming(_timing);
        var host = new MAFWorkflows.SubworkflowBinding(inner, "Sub", null);
        return new MAFWorkflows.WorkflowBuilder(host).WithOutputFrom(host).BuildWithTiming(_timing);
    }

    private MAFWorkflows.Workflow RequestWorkflow(bool nested)
    {
        var port = MAFWorkflows.RequestPort.Create<string, string>("Approval");
        var processor = Function("Processor", value => "processed " + value);
        var inner = new MAFWorkflows.WorkflowBuilder(port).AddEdge(port, processor).WithOutputFrom(processor).BuildWithTiming(_timing);
        if (!nested) return inner;
        var host = new MAFWorkflows.SubworkflowBinding(inner, "Sub", null);
        // MAF forwards a child's request as an ExternalRequest message from its host. An outer port
        // accepts the wrapped request, exposes it to the caller, and routes the wrapped response back.
        var bridge = MAFWorkflows.RequestPort.Create<string, string>("ApprovalBridge");
        return new MAFWorkflows.WorkflowBuilder(host).AddEdge<MAFWorkflows.ExternalRequest>(host, bridge, request => request is not null)
            .AddEdge<MAFWorkflows.ExternalResponse>(bridge, host, response => response is not null).WithOutputFrom(host).BuildWithTiming(_timing);
    }

    private static MAFWorkflows.ExecutorBinding Function(string id, Func<string, string> transform) =>
        ((Func<string, CancellationToken, ValueTask<string>>)(async (value, ct) =>
        {
            await Task.Delay(25, ct);
            return transform(value);
        })).BindAsExecutor<string, string>(id);
}
