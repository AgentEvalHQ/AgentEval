// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentEval.MAF;
using AgentEval.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Tests for <see cref="MAFWorkflowEventBridge"/>.
/// Verifies translation of MAF workflow events to AgentEval events.
/// </summary>
public class MAFWorkflowEventBridgeTests
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
            .Build();

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
            .Build();

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
