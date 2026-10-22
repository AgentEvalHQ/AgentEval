// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentEval.Assertions;
using AgentEval.MAF;
using AgentEval.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using static Microsoft.Agents.AI.Workflows.OpenTelemetryWorkflowBuilderExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Tests for <see cref="MAFWorkflowEventBridge"/>.
/// Verifies translation of MAF workflow events to AgentEval events.
/// </summary>
public partial class MAFWorkflowEventBridgeTests
{
    [Fact]
    public async Task StreamAsAgentEvalEvents_ThrowsOnNullWorkflow()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in MAFWorkflowEventBridge.StreamAsAgentEvalEvents(null!, "test"))
            {
                // Should throw before yielding
            }
        });
    }

    [Fact]
    public async Task StreamAsAgentEvalEvents_ThrowsOnNullInput()
    {
        var binding = CreateFuncBinding("A", "output");
        var workflow = new MAFWorkflows.WorkflowBuilder(binding)
            .WithOutputFrom(binding)
            .Build(validateOrphans: false);

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in MAFWorkflowEventBridge.StreamAsAgentEvalEvents(workflow, null!))
            {
                // Should throw before yielding
            }
        });
    }

    [Fact]
    public async Task StreamAsAgentEvalEvents_SingleExecutor_YieldsOutputAndComplete()
    {
        // Arrange: single executor that echoes input
        var binding = CreateFuncBinding("Echo", "Hello World");
        var workflow = new MAFWorkflows.WorkflowBuilder(binding)
            .WithOutputFrom(binding)
            .Build(validateOrphans: false);

        // Act
        var events = new List<WorkflowEvent>();
        await foreach (var evt in MAFWorkflowEventBridge.StreamAsAgentEvalEvents(workflow, "test input"))
        {
            events.Add(evt);
        }

        // Assert: Should have at least one output event and a complete event
        Assert.NotEmpty(events);
        Assert.Contains(events, e => e is WorkflowCompleteEvent);
    }

    [Fact]
    public async Task StreamAsAgentEvalEvents_SequentialWorkflow_YieldsEdgeEvents()
    {
        // Arrange: A → B sequential chain
        var a = CreateFuncBinding("A", "output-A");
        var b = CreateFuncBinding("B", "output-B");

        var workflow = new MAFWorkflows.WorkflowBuilder(a)
            .AddEdge(a, b)
            .WithOutputFrom(b)
            .WithOpenTelemetry().Build();

        // Act
        var events = new List<WorkflowEvent>();
        await foreach (var evt in MAFWorkflowEventBridge.StreamAsAgentEvalEvents(workflow, "start"))
        {
            events.Add(evt);
        }

        // Assert
        Assert.NotEmpty(events);

        // Should have a complete event
        Assert.Contains(events, e => e is WorkflowCompleteEvent);

        // Should have output events for at least one executor
        var outputEvents = events.OfType<ExecutorOutputEvent>().ToList();
        Assert.NotEmpty(outputEvents);

        // Should have edge traversal event between A and B
        var edgeEvents = events.OfType<EdgeTraversedEvent>().ToList();
        Assert.NotEmpty(edgeEvents);
        Assert.Contains(edgeEvents, e => e.SourceExecutorId == "A" && e.TargetExecutorId == "B");
    }

    [Fact]
    public async Task StreamAsAgentEvalEvents_ThreeStepChain_YieldsCorrectEventSequence()
    {
        // Arrange: A → B → C three-step chain
        var a = CreateFuncBinding("A", "result-A");
        var b = CreateFuncBinding("B", "result-B");
        var c = CreateFuncBinding("C", "result-C");

        var workflow = new MAFWorkflows.WorkflowBuilder(a)
            .AddEdge(a, b)
            .AddEdge(b, c)
            .WithOutputFrom(c)
            .WithOpenTelemetry().Build();

        // Act
        var events = new List<WorkflowEvent>();
        await foreach (var evt in MAFWorkflowEventBridge.StreamAsAgentEvalEvents(workflow, "input"))
        {
            events.Add(evt);
        }

        // Assert
        var outputEvents = events.OfType<ExecutorOutputEvent>().ToList();
        var edgeEvents = events.OfType<EdgeTraversedEvent>().ToList();

        // Should have output events (at least 2 from flushed executors)
        Assert.True(outputEvents.Count >= 2,
            $"Expected at least 2 output events, got {outputEvents.Count}. Events: {string.Join(", ", events.Select(e => e.GetType().Name))}");

        // Should have 2 edge events: A→B and B→C
        Assert.Equal(2, edgeEvents.Count);
        Assert.Contains(edgeEvents, e => e.SourceExecutorId == "A" && e.TargetExecutorId == "B");
        Assert.Contains(edgeEvents, e => e.SourceExecutorId == "B" && e.TargetExecutorId == "C");

        // Should end with WorkflowCompleteEvent
        Assert.IsType<WorkflowCompleteEvent>(events.Last());
    }

    [Fact]
    public async Task StreamAsAgentEvalEvents_SelfLoopingExecutor_YieldsSelfEdge_NotSilentlyDropped()
    {
        // Regression (issue #15): an executor whose own edge routes back to ITSELF (a direct self-loop, e.g. a
        // retry/refine step) used to hit NEITHER the "flush + emit edge to a NEW executor" branch NOR the
        // "very first executor" branch on its re-invocation — so no EdgeTraversedEvent(A, A) was ever emitted
        // for the self-loop transition, and the two invocations' output could silently merge into one step.
        var count = 0;
        var a = ((Func<string, ValueTask<string>>)(input =>
        {
            count++;
            return new ValueTask<string>($"A-invocation-{count}");
        })).BindAsExecutor<string, string>("A");
        var b = CreateFuncBinding("B", "final-output");

        var workflow = new MAFWorkflows.WorkflowBuilder(a)
            .AddEdge<string>(a, a, (string? _) => count < 2)    // loop back to itself once
            .AddEdge<string>(a, b, (string? _) => count >= 2)   // then proceed to B
            .WithOutputFrom(b)
            .WithOpenTelemetry().Build();

        // Act
        var events = new List<WorkflowEvent>();
        await foreach (var evt in MAFWorkflowEventBridge.StreamAsAgentEvalEvents(workflow, "start"))
        {
            events.Add(evt);
        }

        // Assert: the self-loop transition must be visible — before the fix, ZERO A→A edges were ever emitted.
        var edgeEvents = events.OfType<EdgeTraversedEvent>().ToList();
        Assert.Contains(edgeEvents, e => e.SourceExecutorId == "A" && e.TargetExecutorId == "A");
        Assert.Contains(edgeEvents, e => e.SourceExecutorId == "A" && e.TargetExecutorId == "B");

        Assert.IsType<WorkflowCompleteEvent>(events.Last());
    }

    [Fact]
    public async Task StreamAsAgentEvalEvents_CancellationRespected()
    {
        // Arrange: a slow workflow
        var binding = CreateSlowFuncBinding("Slow", "output", TimeSpan.FromSeconds(30));
        var workflow = new MAFWorkflows.WorkflowBuilder(binding)
            .WithOutputFrom(binding)
            .Build(validateOrphans: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act & Assert: Should not hang indefinitely
        var events = new List<WorkflowEvent>();
        try
        {
            await foreach (var evt in MAFWorkflowEventBridge.StreamAsAgentEvalEvents(
                workflow, "test", cts.Token))
            {
                events.Add(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected — cancellation works
        }

        // Should have exited before accumulating lots of events
        Assert.True(events.Count < 100, "Stream should have been cancelled quickly");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a function-based executor binding that transforms input to a fixed output.
    /// </summary>
    private static MAFWorkflows.ExecutorBinding CreateFuncBinding(string id, string output)
    {
        return ((Func<string, ValueTask<string>>)(input => new ValueTask<string>(output)))
            .BindAsExecutor<string, string>(id);
    }

    /// <summary>
    /// Creates a slow function-based executor binding for cancellation testing.
    /// </summary>
    private static MAFWorkflows.ExecutorBinding CreateSlowFuncBinding(
        string id, string output, TimeSpan delay)
    {
        return ((Func<string, CancellationToken, ValueTask<string>>)(async (input, ct) =>
        {
            await Task.Delay(delay, ct);
            return output;
        })).BindAsExecutor<string, string>(id);
    }

    // ── AIAgent-based tests ─────────────────────────────────────────────

    [Fact]
    public async Task StreamAsAgentEvalEvents_AgentExecutor_YieldsExecutorAgentResponseEvent()
    {
        // Arrange: Create a fake AI agent that supports streaming
        var agent = new FakeStreamableAIAgent("TestAgent", "Hello from agent",
            finishReason: ChatFinishReason.Stop);

        var binding = agent.BindAsExecutor(new MAFWorkflows.AIAgentHostOptions
        {
            EmitAgentResponseEvents = true
        });

        var workflow = new MAFWorkflows.WorkflowBuilder(binding)
            .WithOutputFrom(binding)
            .Build(validateOrphans: false);

        // Act
        var events = new List<WorkflowEvent>();
        await foreach (var evt in MAFWorkflowEventBridge.StreamAsAgentEvalEvents(workflow, "test input"))
        {
            events.Add(evt);
        }

        // Assert: Should contain an ExecutorAgentResponseEvent
        var agentResponseEvents = events.OfType<ExecutorAgentResponseEvent>().ToList();
        Assert.NotEmpty(agentResponseEvents);

        var responseEvent = agentResponseEvents[0];
        Assert.Contains("Hello from agent", responseEvent.Output);
        Assert.Equal("stop", responseEvent.FinishReason);
    }

    [Fact]
    public async Task StreamAsAgentEvalEvents_AgentExecutor_PreservesExecutorId()
    {
        // Arrange
        var agent = new FakeStreamableAIAgent("MyAgent", "response text");

        var binding = agent.BindAsExecutor(new MAFWorkflows.AIAgentHostOptions
        {
            EmitAgentResponseEvents = true
        });

        var workflow = new MAFWorkflows.WorkflowBuilder(binding)
            .WithOutputFrom(binding)
            .Build(validateOrphans: false);

        // Act
        var events = new List<WorkflowEvent>();
        await foreach (var evt in MAFWorkflowEventBridge.StreamAsAgentEvalEvents(workflow, "test"))
        {
            events.Add(evt);
        }

        // Assert: ExecutorId should contain the agent name
        var agentResponseEvents = events.OfType<ExecutorAgentResponseEvent>().ToList();
        Assert.NotEmpty(agentResponseEvents);
        Assert.Contains("MyAgent", agentResponseEvents[0].ExecutorId);
    }

    [Fact]
    public async Task StreamAsAgentEvalEvents_AgentExecutor_IsNotMistakenForWorkflowOutput()
    {
        // Arrange: Agent executor with emitEvents to ensure AgentResponseEvent is emitted
        var agent = new FakeStreamableAIAgent("Agent", "agent output",
            finishReason: ChatFinishReason.Stop);

        var binding = agent.BindAsExecutor(new MAFWorkflows.AIAgentHostOptions
        {
            EmitAgentResponseEvents = true
        });

        var workflow = new MAFWorkflows.WorkflowBuilder(binding)
            .WithOutputFrom(binding)
            .Build(validateOrphans: false);

        // Act
        var events = new List<WorkflowEvent>();
        await foreach (var evt in MAFWorkflowEventBridge.StreamAsAgentEvalEvents(workflow, "test"))
        {
            events.Add(evt);
        }

        // Assert: Should have BOTH an ExecutorAgentResponseEvent AND a WorkflowCompleteEvent
        // (AgentResponseEvent should NOT be misinterpreted as the WorkflowOutputEvent)
        Assert.Contains(events, e => e is ExecutorAgentResponseEvent);
        Assert.Contains(events, e => e is WorkflowCompleteEvent);

        // The ExecutorAgentResponseEvent should appear BEFORE WorkflowCompleteEvent
        var agentResponseIndex = events.FindIndex(e => e is ExecutorAgentResponseEvent);
        var completeIndex = events.FindIndex(e => e is WorkflowCompleteEvent);
        Assert.True(agentResponseIndex < completeIndex,
            "ExecutorAgentResponseEvent should appear before WorkflowCompleteEvent");
    }

    [Fact]
    public async Task StreamAsAgentEvalEvents_AgentExecutor_IsSubtypeOfExecutorOutputEvent()
    {
        // Arrange
        var agent = new FakeStreamableAIAgent("Agent", "response");

        var binding = agent.BindAsExecutor(new MAFWorkflows.AIAgentHostOptions
        {
            EmitAgentResponseEvents = true
        });

        var workflow = new MAFWorkflows.WorkflowBuilder(binding)
            .WithOutputFrom(binding)
            .Build(validateOrphans: false);

        // Act
        var events = new List<WorkflowEvent>();
        await foreach (var evt in MAFWorkflowEventBridge.StreamAsAgentEvalEvents(workflow, "test"))
        {
            events.Add(evt);
        }

        // Assert: ExecutorAgentResponseEvent should also be discoverable via ExecutorOutputEvent (Liskov)
        var outputEvents = events.OfType<ExecutorOutputEvent>().ToList();
        Assert.Contains(outputEvents, e => e is ExecutorAgentResponseEvent);
    }

    // ── Step boundaries for chained agent executors ─────────────────────

    [Fact]
    public async Task FromMAFWorkflow_FourAgentBindAsExecutorChain_YieldsOneStepPerAgentTurn_WithThatTurnsOutputAndToolCalls()
    {
        // Regression: MAF raises one ExecutorInvokedEvent per DELIVERED MESSAGE, and an agent executor
        // (BindAsExecutor → a ChatProtocolExecutor) gets 2-3 deliveries per turn: the input / forwarded chat
        // messages, which only accumulate, then the TurnToken, which runs the turn. The #129 self-loop branch
        // read every repeated delivery as a re-entry, so this chain reported 11 steps (7 of them empty) instead
        // of 4, on any provider, and HaveStepCount / HaveExecutedInOrder / StrictExecutorOrder all failed.
        var (workflow, executorIds) = BuildFourAgentChain();
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "TripPlanner", executorIds, "PromptChaining");

        var result = await adapter.ExecuteWorkflowAsync("Plan a three-day trip to Lisbon");

        Assert.Null(result.Errors);
        Assert.Equal(executorIds, result.Steps.Select(s => s.ExecutorId).ToArray());
        for (var i = 0; i < executorIds.Length; i++)
        {
            // Each step holds exactly its own turn's reply — not empty, not merged with a neighbour's text.
            Assert.Equal(i, result.Steps[i].StepIndex);
            Assert.Equal(BridgeScriptedChatClient.FinalReply(executorIds[i], turn: 1), result.Steps[i].Output);
        }

        // Per-step tool calls: each step carries exactly the calls its own turn made, in call order.
        Assert.Equal(["GetDestinationInfo"], ToolNames(result.Steps[0]));
        Assert.Equal(["SearchFlights", "BookFlight"], ToolNames(result.Steps[1]));
        Assert.Equal(["BookHotel"], ToolNames(result.Steps[2]));
        Assert.Null(result.Steps[3].ToolCalls);
        foreach (var step in result.Steps.Where(s => s.HasToolCalls))
        {
            Assert.All(step.ToolCalls!, call =>
            {
                Assert.Equal(step.ExecutorId, call.ExecutorId);
                Assert.Contains($"{call.Name}-result-", call.Result?.ToString());
            });
        }

        Assert.Equal(BridgeScriptedChatClient.FinalReply("Presenter", turn: 1), result.FinalOutput);
    }

    [Fact]
    public async Task WorkflowHarness_FourAgentBindAsExecutorChain_StrictOrderToolAndStepAssertionsPass()
    {
        // The checks Eval 02 / Eval 05 and the C2/C3 workflow samples run against a BindAsExecutor chain: the
        // harness's StrictExecutorOrder + per-executor tool checks, then the fluent step assertions. Before the
        // fix every one of them failed on the 11-step result (ForExecutor(x) also picked the first, EMPTY step).
        var (workflow, executorIds) = BuildFourAgentChain();
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "TripPlanner", executorIds, "PromptChaining");
        var harness = new WorkflowEvaluationHarness();

        var testResult = await harness.RunWorkflowTestAsync(adapter, new WorkflowTestCase
        {
            Name = "four-agent chain",
            Input = "Plan a three-day trip to Lisbon",
            ExpectedExecutors = executorIds,
            StrictExecutorOrder = true,
            ExpectedTools = ["GetDestinationInfo", "SearchFlights", "BookFlight", "BookHotel"],
            PerExecutorExpectedTools = new Dictionary<string, IReadOnlyList<string>>
            {
                ["TripPlanner"] = ["GetDestinationInfo"],
                ["FlightReservation"] = ["SearchFlights", "BookFlight"],
                ["HotelReservation"] = ["BookHotel"],
            },
        });

        var order = Assert.Single(testResult.AssertionResults!, a => a.AssertionName == "Executor Order");
        Assert.True(order.Passed, order.FailureMessage);
        Assert.True(testResult.Passed, string.Join("; ", testResult.FailureMessages ?? []));

        testResult.ExecutionResult!.Should()
            .HaveStepCount(4)
            .HaveExecutedInOrder(executorIds)
            .HaveNoErrors()
            .HaveNonEmptyOutput()
            .ForExecutor("TripPlanner").HaveNonEmptyOutput().HaveToolCallCount(1)
                .HaveCalledTool("GetDestinationInfo").WithoutError().And().And()
            .ForExecutor("FlightReservation").HaveNonEmptyOutput().HaveToolCallCount(2)
                .HaveCalledTool("SearchFlights").BeforeTool("BookFlight").WithoutError().And().And()
            .ForExecutor("HotelReservation").HaveNonEmptyOutput().HaveToolCallCount(1)
                .HaveCalledTool("BookHotel").WithoutError().And().And()
            .ForExecutor("Presenter").HaveNonEmptyOutput().HaveToolCallCount(0).And()
            .Validate();
    }

    [Fact]
    public async Task FromMAFWorkflow_SelfLoopingFunctionExecutor_KeepsEachIterationAsItsOwnStep()
    {
        // The #129 self-loop scenario seen through the adapter: A's own edge routes back to A once, so MAF
        // re-invokes A in the NEXT superstep. That genuine re-entry must stay two steps with their own output.
        var count = 0;
        var a = ((Func<string, ValueTask<string>>)(input =>
        {
            count++;
            return new ValueTask<string>($"A-invocation-{count}");
        })).BindAsExecutor<string, string>("A");
        var b = CreateFuncBinding("B", "final-output");

        var workflow = new MAFWorkflows.WorkflowBuilder(a)
            .AddEdge<string>(a, a, (string? _) => count < 2)
            .AddEdge<string>(a, b, (string? _) => count >= 2)
            .WithOutputFrom(b)
            .WithOpenTelemetry().Build();
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "self-loop", ["A", "B"]);

        var result = await adapter.ExecuteWorkflowAsync("start");

        Assert.Equal(
            ["A:A-invocation-1", "A:A-invocation-2", "B:final-output"],
            result.Steps.Select(s => $"{s.ExecutorId}:{s.Output}").ToArray());
        Assert.Contains(result.Graph!.TraversedEdges!, e => e.SourceExecutorId == "A" && e.TargetExecutorId == "A");
    }

    [Fact]
    public async Task FromMAFWorkflow_AgentExecutorSelfLoop_RecordsEachTurnAsItsOwnStep()
    {
        // A GENUINE re-entry of an AGENT executor: the Refiner's own edge routes its reply and TurnToken back to
        // itself once (a refine pass), so MAF delivers them in the NEXT superstep and the Refiner takes a second
        // turn before handing over to the Publisher. Folding the per-message deliveries of one turn into one
        // step must not fold these two turns together: they stay two steps, joined by the Refiner→Refiner edge.
        var refinerClient = new BridgeScriptedChatClient("Refiner", []);
        var refiner = new ChatClientAgent(refinerClient, name: "Refiner").BindAsExecutor(new MAFWorkflows.AIAgentHostOptions
        {
            EmitAgentUpdateEvents = true,
            ForwardIncomingMessages = false, // send only the reply + TurnToken, so both edges see the same messages
        });
        var publisher = new ChatClientAgent(new BridgeScriptedChatClient("Publisher", []), name: "Publisher")
            .BindAsExecutor(emitEvents: true);

        var workflow = new MAFWorkflows.WorkflowBuilder(refiner)
            .AddEdge<object>(refiner, refiner, (object? _) => refinerClient.Turns < 2)  // loop back once
            .AddEdge<object>(refiner, publisher, (object? _) => refinerClient.Turns >= 2)
            .WithOutputFrom(publisher)
            .WithOpenTelemetry().Build();
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "refine-loop", ["Refiner", "Publisher"]);

        var result = await adapter.ExecuteWorkflowAsync("Draft a tagline");

        Assert.Null(result.Errors);
        Assert.Equal(
            [
                $"Refiner:{BridgeScriptedChatClient.FinalReply("Refiner", turn: 1)}",
                $"Refiner:{BridgeScriptedChatClient.FinalReply("Refiner", turn: 2)}",
                $"Publisher:{BridgeScriptedChatClient.FinalReply("Publisher", turn: 1)}",
            ],
            result.Steps.Select(s => $"{s.ExecutorId}:{s.Output}").ToArray());
        Assert.Contains(result.Graph!.TraversedEdges!, e => e.SourceExecutorId == "Refiner" && e.TargetExecutorId == "Refiner");
        Assert.Contains(result.Graph!.TraversedEdges!, e => e.SourceExecutorId == "Refiner" && e.TargetExecutorId == "Publisher");
    }

    [Fact]
    public async Task GetChatProtocolExecutorIds_FindsEveryBindAsExecutorAgent_AndNoFunctionExecutor()
    {
        // The bridge keeps one turn's deliveries together across a superstep boundary (the off-thread input race
        // below) only for executors it KNOWS take turns, and it learns that from MAF's binding metadata before the
        // run starts. The live chain tests hit that race only when the timing happens to fire, so pin the
        // detection itself: if a MAF update stopped declaring the agent host type, or raised events under other
        // IDs than its bindings, the race would silently split the start agent's turn again while those tests
        // still passed on most runs.
        var (agentChain, _) = BuildFourAgentChain();
        var agentExecutors = agentChain.ReflectExecutors();

        var agentIds = MAFWorkflowEventBridge.GetChatProtocolExecutorIds(agentChain);

        Assert.Equal(4, agentIds.Count);
        Assert.All(agentIds, id => Assert.IsAssignableFrom<MAFWorkflows.AIAgentBinding>(agentExecutors[id]));

        // ...and they are the very IDs MAF raises ExecutorInvokedEvent under when the chain runs.
        var invokedIds = new HashSet<string>(StringComparer.Ordinal);
        await using (var run = await MAFWorkflows.InProcessExecution.RunStreamingAsync(
            agentChain, new ChatMessage(ChatRole.User, "Plan a three-day trip to Lisbon")))
        {
            await run.TrySendMessageAsync(new MAFWorkflows.TurnToken(emitEvents: true));
            await foreach (var evt in run.WatchStreamAsync(CancellationToken.None))
            {
                if (evt is MAFWorkflows.ExecutorInvokedEvent invoked)
                    invokedIds.Add(invoked.ExecutorId);
            }
        }

        Assert.Equal(agentIds.Order(StringComparer.Ordinal), invokedIds.Order(StringComparer.Ordinal));

        var a = CreateFuncBinding("A", "output-A");
        var b = CreateFuncBinding("B", "output-B");
        var functionChain = new MAFWorkflows.WorkflowBuilder(a).AddEdge(a, b).WithOutputFrom(b).WithOpenTelemetry().Build();
        Assert.Empty(MAFWorkflowEventBridge.GetChatProtocolExecutorIds(functionChain));
    }

    // ── Step timing ─────────────────────────────────────────────────────

    [Fact]
    public async Task FromMAFWorkflow_SlowFunctionChain_TimesEachStepAsTheExecutorRan()
    {
        // Regression: the adapter timed steps by event arrival. A step with no incoming edge (the first one) started
        // its clock at its first ExecutorOutputEvent, which the bridge only yields once the step has finished, and
        // stopped it at the edge yielded right after: the first step measured ~0 ms, starting at its own end, however
        // long it ran, so HaveCompletedWithin on it could never fail.
        var work = TimeSpan.FromMilliseconds(300);
        var a = CreateSlowFuncBinding("A", "output-A", work);
        var b = CreateSlowFuncBinding("B", "output-B", work);
        var c = CreateSlowFuncBinding("C", "output-C", work);
        var workflow = new MAFWorkflows.WorkflowBuilder(a)
            .AddEdge(a, b)
            .AddEdge(b, c)
            .WithOutputFrom(c)
            .WithOpenTelemetry().Build();
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "slow-chain", ["A", "B", "C"]);

        var result = await adapter.ExecuteWorkflowAsync("start");

        Assert.Null(result.Errors);
        Assert.Equal(["A", "B", "C"], result.Steps.Select(s => s.ExecutorId).ToArray());
        AssertStepsTimedPlausibly(result, work, work, work);
    }

    [Fact]
    public async Task FromMAFWorkflow_SlowFourAgentChain_TimesEachTurn_AndStepTimeLimitsCanFail()
    {
        // The C2/C3 workflow samples' shape: four BindAsExecutor agents whose model round-trips take time. Each step
        // must span its own turn (all its round-trips), and a per-step time limit must be able to fail: before the
        // fix the first agent's two round-trips measured ~0 ms (live: C3's TripPlanner, 3 ms), so a 50 ms limit passed.
        var roundTrip = TimeSpan.FromMilliseconds(100);
        var (workflow, executorIds) = BuildFourAgentChain(roundTrip);
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "TripPlanner", executorIds, "PromptChaining");

        var result = await adapter.ExecuteWorkflowAsync("Plan a three-day trip to Lisbon");

        Assert.Null(result.Errors);
        Assert.Equal(executorIds, result.Steps.Select(s => s.ExecutorId).ToArray());
        // One round-trip per tool call plus the final reply: 1+1, 2+1, 1+1 and 0+1.
        AssertStepsTimedPlausibly(result, 2 * roundTrip, 3 * roundTrip, 2 * roundTrip, roundTrip);

        var tooTight = result.Should().ForExecutor("TripPlanner").HaveCompletedWithin(TimeSpan.FromMilliseconds(50)).And();
        Assert.False(tooTight.IsValid);
        Assert.Contains("Executor 'TripPlanner' took", Assert.Single(tooTight.Failures));

        var generous = result.Should().ForExecutor("TripPlanner").HaveCompletedWithin(TimeSpan.FromSeconds(60)).And();
        Assert.True(generous.IsValid, string.Join("; ", generous.Failures));
    }

    /// <summary>
    /// Asserts the steps are timed as MAF ran them: each step lasts at least half its executor's known work (half,
    /// allowing timer resolution differences while checking the producer span), the steps run in
    /// order without overlapping inside the run, and together they cover most of the run's total — the rest is MAF
    /// and bridge overhead before the first step and between supersteps.
    /// </summary>
    private static void AssertStepsTimedPlausibly(WorkflowExecutionResult result, params TimeSpan[] workPerStep)
    {
        Assert.Equal(workPerStep.Length, result.Steps.Count);
        var previousEnd = TimeSpan.Zero;
        for (var i = 0; i < workPerStep.Length; i++)
        {
            var step = result.Steps[i];
            Assert.True(step.Duration >= workPerStep[i] / 2,
                $"Step {i} ({step.ExecutorId}) lasted {step.Duration.TotalMilliseconds:F0} ms; its executor worked {workPerStep[i].TotalMilliseconds:F0} ms.");
            Assert.True(step.StartOffset >= previousEnd,
                $"Step {i} ({step.ExecutorId}) starts at {step.StartOffset.TotalMilliseconds:F0} ms, before the previous step ended at {previousEnd.TotalMilliseconds:F0} ms.");
            previousEnd = step.StartOffset + step.Duration;
        }

        Assert.True(previousEnd <= result.TotalDuration,
            $"The last step ends at {previousEnd.TotalMilliseconds:F0} ms, after the run's total of {result.TotalDuration.TotalMilliseconds:F0} ms.");
        var stepTotal = TimeSpan.FromTicks(result.Steps.Sum(s => s.Duration.Ticks));
        Assert.True(stepTotal >= result.TotalDuration / 2,
            $"The steps add up to {stepTotal.TotalMilliseconds:F0} ms of a {result.TotalDuration.TotalMilliseconds:F0} ms run.");
    }

    // ── Exact MAF event orderings (TranslateEventsAsync) ────────────────
    // These feed the translator the event shapes MAF 1.24 raises (one ExecutorInvokedEvent per delivered
    // message, bracketed by SuperStepStarted/Completed), including orderings a live run produces only
    // nondeterministically, and read the result back through the adapter exactly as FromMAFWorkflow does.

    [Theory]
    [InlineData("ChatMessage")]
    [InlineData("List<ChatMessage>")]
    [InlineData("ChatMessage[]")]
    [InlineData("string")] // AIAgentHostExecutor sets StringMessageChatRole = User, so a string only accumulates too
    public async Task TranslateEvents_InputAndTurnTokenInDifferentSupersteps_StayOneStep(string inputShape)
    {
        // InProcessExecution runs off-thread: the bridge's input ChatMessage can start superstep 0 before the
        // TurnToken it sends next is queued, so the start agent is invoked in superstep 0 (accumulate only) and
        // again in superstep 1 (the turn). That is still ONE turn — not a self-loop, not an empty extra step —
        // whichever of the chat protocol's accumulate-only message shapes carried the input.
        object input = inputShape switch
        {
            "ChatMessage" => new ChatMessage(ChatRole.User, "Plan a trip"),
            "List<ChatMessage>" => new List<ChatMessage> { new(ChatRole.User, "Plan a trip") },
            "ChatMessage[]" => new[] { new ChatMessage(ChatRole.User, "Plan a trip") },
            "string" => "Plan a trip",
            _ => throw new ArgumentOutOfRangeException(nameof(inputShape), inputShape, null),
        };
        var result = await RunTranslatedAsync(
            chatProtocolExecutorIds: ["TripPlanner_1", "FlightReservation_2"],
            SuperStep(0, AgentDelivery("TripPlanner_1", input)),
            SuperStep(1, AgentDelivery("TripPlanner_1", new MAFWorkflows.TurnToken(true), "TripPlanner reply")),
            SuperStep(2,
                AgentDelivery("FlightReservation_2", new List<ChatMessage> { new(ChatRole.User, "Plan a trip") }),
                AgentDelivery("FlightReservation_2", new List<ChatMessage> { new(ChatRole.Assistant, "TripPlanner reply") }),
                AgentDelivery("FlightReservation_2", new MAFWorkflows.TurnToken(true), "FlightReservation reply")));

        Assert.Equal(
            ["TripPlanner:TripPlanner reply", "FlightReservation:FlightReservation reply"],
            result.Steps.Select(s => $"{s.ExecutorId}:{s.Output}").ToArray());
        Assert.DoesNotContain(result.Graph!.TraversedEdges!, e => e.SourceExecutorId == e.TargetExecutorId);
    }

    [Fact]
    public async Task TranslateEvents_DelayedTurnTokenThenGenuineSelfLoop_KeepsTheLoopAsItsOwnStep()
    {
        // The input race (turn 1's TurnToken one superstep late) must not mask a later genuine re-entry: once the
        // Refiner's first turn has run, its own reply + TurnToken arriving in a later superstep is a self-loop.
        var result = await RunTranslatedAsync(
            chatProtocolExecutorIds: ["Refiner_1", "Publisher_2"],
            SuperStep(0, AgentDelivery("Refiner_1", new ChatMessage(ChatRole.User, "Draft a tagline"))),
            SuperStep(1, AgentDelivery("Refiner_1", new MAFWorkflows.TurnToken(true), "Refiner pass 1")),
            SuperStep(2,
                AgentDelivery("Refiner_1", new List<ChatMessage> { new(ChatRole.Assistant, "Refiner pass 1") }),
                AgentDelivery("Refiner_1", new MAFWorkflows.TurnToken(true), "Refiner pass 2")),
            SuperStep(3,
                AgentDelivery("Publisher_2", new List<ChatMessage> { new(ChatRole.Assistant, "Refiner pass 2") }),
                AgentDelivery("Publisher_2", new MAFWorkflows.TurnToken(true), "Published")));

        Assert.Equal(
            ["Refiner:Refiner pass 1", "Refiner:Refiner pass 2", "Publisher:Published"],
            result.Steps.Select(s => $"{s.ExecutorId}:{s.Output}").ToArray());
        Assert.Contains(result.Graph!.TraversedEdges!, e => e.SourceExecutorId == "Refiner" && e.TargetExecutorId == "Refiner");
    }

    [Fact]
    public async Task TranslateEvents_TwoDeliveriesToAFunctionExecutorInOneSuperstep_AreOneActivation()
    {
        // A non-agent executor handed two messages in ONE superstep (e.g. two upstream senders) is one activation:
        // nothing can route back into an executor within its own superstep, so no Join→Join edge may be invented.
        var result = await RunTranslatedAsync(
            chatProtocolExecutorIds: [],
            SuperStep(0, FunctionDelivery("Start_1", "go", "started")),
            SuperStep(1,
                FunctionDelivery("Join_2", "from-left", "left-joined;"),
                FunctionDelivery("Join_2", "from-right", "right-joined;")));

        Assert.Equal(["Start", "Join"], result.Steps.Select(s => s.ExecutorId).ToArray());
        Assert.Equal("left-joined;right-joined;", result.Steps[1].Output);
        Assert.DoesNotContain(result.Graph!.TraversedEdges!, e => e.SourceExecutorId == e.TargetExecutorId);
    }

    private static async Task<WorkflowExecutionResult> RunTranslatedAsync(
        IReadOnlyCollection<string> chatProtocolExecutorIds,
        params IEnumerable<MAFWorkflows.WorkflowEvent>[] superSteps)
    {
        var events = superSteps.SelectMany(s => s).ToList();
        var rawIds = events.OfType<MAFWorkflows.ExecutorEvent>().Select(e => e.ExecutorId).Distinct().ToList();
        var idMap = rawIds.ToDictionary(id => id, id => id[..id.LastIndexOf('_')]);
        var chatIds = chatProtocolExecutorIds.ToHashSet(StringComparer.Ordinal);

        var adapter = new MAFWorkflowAdapter(
            "synthetic",
            (prompt, ct) => MAFWorkflowEventBridge.TranslateEventsAsync(ToAsync(events), chatIds, idMap, ct),
            idMap.Values.Distinct());
        return await adapter.ExecuteWorkflowAsync("input");

        static async IAsyncEnumerable<MAFWorkflows.WorkflowEvent> ToAsync(IEnumerable<MAFWorkflows.WorkflowEvent> source)
        {
            foreach (var e in source)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private static IEnumerable<MAFWorkflows.WorkflowEvent> SuperStep(
        int stepNumber, params IEnumerable<MAFWorkflows.WorkflowEvent>[] deliveries)
    {
        yield return new MAFWorkflows.SuperStepStartedEvent(stepNumber);
        foreach (var e in deliveries.SelectMany(d => d))
            yield return e;
        yield return new MAFWorkflows.SuperStepCompletedEvent(stepNumber);
    }

    /// <summary>One message delivered to an agent executor: invoked, optionally the turn's streamed reply, completed (no data).</summary>
    private static IEnumerable<MAFWorkflows.WorkflowEvent> AgentDelivery(string executorId, object message, string? streamedReply = null)
    {
        yield return new MAFWorkflows.ExecutorInvokedEvent(executorId, message);
        if (streamedReply is not null)
        {
            yield return new MAFWorkflows.AgentResponseUpdateEvent(executorId, new AgentResponseUpdate
            {
                Role = ChatRole.Assistant,
                AuthorName = executorId,
                Contents = [new TextContent(streamedReply)],
            });
        }
        yield return new MAFWorkflows.ExecutorCompletedEvent(executorId, null);
    }

    /// <summary>One message delivered to a function executor: invoked, then completed with the handler's result.</summary>
    private static IEnumerable<MAFWorkflows.WorkflowEvent> FunctionDelivery(string executorId, string message, string result)
    {
        yield return new MAFWorkflows.ExecutorInvokedEvent(executorId, message);
        yield return new MAFWorkflows.ExecutorCompletedEvent(executorId, result);
    }

    /// <summary>
    /// The TravelDemo trip-planner topology (samples/AgentEval.TravelDemo/Workflows/TripPlannerWorkflow.cs): four
    /// agents bound with <c>BindAsExecutor(emitEvents: true)</c> and chained with <c>WorkflowBuilder</c>, each on a
    /// deterministic in-memory chat client (no network, no provider) that calls its tools and then replies.
    /// </summary>
    private static (MAFWorkflows.Workflow Workflow, string[] ExecutorIds) BuildFourAgentChain(TimeSpan roundTripDelay = default)
    {
        var tripPlanner = CreateScriptedAgent("TripPlanner", roundTripDelay, "GetDestinationInfo").BindAsExecutor(emitEvents: true);
        var flightReservation = CreateScriptedAgent("FlightReservation", roundTripDelay, "SearchFlights", "BookFlight").BindAsExecutor(emitEvents: true);
        var hotelReservation = CreateScriptedAgent("HotelReservation", roundTripDelay, "BookHotel").BindAsExecutor(emitEvents: true);
        var presenter = CreateScriptedAgent("Presenter", roundTripDelay).BindAsExecutor(emitEvents: true);

        var workflow = new MAFWorkflows.WorkflowBuilder(tripPlanner)
            .AddEdge(tripPlanner, flightReservation)
            .AddEdge(flightReservation, hotelReservation)
            .AddEdge(hotelReservation, presenter)
            .WithOutputFrom(presenter)
            .WithOpenTelemetry().Build();

        return (workflow, ["TripPlanner", "FlightReservation", "HotelReservation", "Presenter"]);
    }

    private static AIAgent CreateScriptedAgent(string name, TimeSpan roundTripDelay, params string[] toolNames)
    {
        var tools = toolNames
            .Select(toolName => (AITool)AIFunctionFactory.Create((int step) => $"{toolName}-result-{step}", toolName))
            .ToList();
        return new ChatClientAgent(new BridgeScriptedChatClient(name, toolNames, roundTripDelay), name: name, tools: tools);
    }

    private static string[] ToolNames(ExecutorStep step) =>
        (step.ToolCalls ?? []).Select(c => c.Name).ToArray();
}

// ── Test Helpers ────────────────────────────────────────────────────────

/// <summary>
/// A fake AIAgent that supports both streaming and non-streaming invocation,
/// suitable for testing workflow event bridge scenarios.
/// </summary>
internal class FakeStreamableAIAgent : AIAgent
{
    private readonly string _responseText;
    private readonly ChatFinishReason? _finishReason;

    public override string? Name { get; }
    protected override string? IdCore => Name;

    public FakeStreamableAIAgent(
        string name,
        string responseText,
        ChatFinishReason? finishReason = null)
    {
        Name = name;
        _responseText = responseText;
        _finishReason = finishReason;
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(
        CancellationToken cancellationToken = default)
        => new(new FakeStreamableAgentSession());

    protected override Task<Microsoft.Agents.AI.AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.RunStreamingAsync(messages, session, options, cancellationToken)
            .ToAgentResponseAsync(cancellationToken);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AgentResponseUpdate
        {
            AgentId = Id,
            AuthorName = Name,
            MessageId = Guid.NewGuid().ToString("N"),
            ResponseId = Guid.NewGuid().ToString("N"),
            Contents = [new TextContent(_responseText)],
            Role = ChatRole.Assistant,
            FinishReason = _finishReason,
        };
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => new(JsonSerializer.SerializeToElement(new { }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => new(new FakeStreamableAgentSession());
}

/// <summary>
/// Minimal AgentSession implementation for FakeStreamableAIAgent.
/// </summary>
internal class FakeStreamableAgentSession : AgentSession
{
}

/// <summary>
/// A deterministic, network-free <see cref="IChatClient"/> for ONE agent: each model round-trip calls the next of
/// its scripted tools (one call per round-trip, in order), and once every tool has a result it answers with
/// <see cref="FinalReply"/>. The next action is derived from the conversation it is sent — how many of ITS OWN
/// tool results it already holds — so whatever history MAF forwards from upstream agents does not disturb it.
/// Each round-trip takes <paramref name="roundTripDelay"/>, standing in for model latency.
/// </summary>
internal sealed class BridgeScriptedChatClient(
    string agentName, IReadOnlyList<string> toolNames, TimeSpan roundTripDelay = default) : IChatClient
{
    private int _turns;

    /// <summary>The number of final (non-tool) replies this client has produced, i.e. completed agent turns.</summary>
    public int Turns => Volatile.Read(ref _turns);

    public static string FinalReply(string agentName, int turn) => $"{agentName}: turn {turn} complete";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        if (roundTripDelay > TimeSpan.Zero)
            await Task.Delay(roundTripDelay, cancellationToken);
        var callIdPrefix = agentName + "-call-";
        var completedCalls = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Count(r => r.CallId?.StartsWith(callIdPrefix, StringComparison.Ordinal) == true);
        var id = Guid.NewGuid().ToString("N");

        if (completedCalls < toolNames.Count)
        {
            var callNumber = completedCalls + 1;
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent(callIdPrefix + callNumber, toolNames[completedCalls],
                    new Dictionary<string, object?> { ["step"] = callNumber })])
            { MessageId = id, ResponseId = id };
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.ToolCalls, MessageId = id, ResponseId = id };
            yield break;
        }

        var turn = Interlocked.Increment(ref _turns);
        yield return new ChatResponseUpdate(ChatRole.Assistant, FinalReply(agentName, turn)) { MessageId = id, ResponseId = id };
        yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, MessageId = id, ResponseId = id };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
