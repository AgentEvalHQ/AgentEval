# ADR-010: MAF Workflow Integration Architecture

## Status

✅ **Accepted** - February 14, 2026

## Context

Microsoft Agent Framework (MAF) provides a native workflow system using `WorkflowBuilder` for orchestrating multi-agent execution pipelines. AgentEval needs to evaluate these workflows while maintaining compatibility with MAF's event streaming architecture and execution model.

### Key Integration Challenges

1. **Event Streaming**: MAF workflows emit real-time events via `WatchStreamAsync()` that must be captured and processed into AgentEval's evaluation model
2. **Graph Extraction**: Workflow structure must be automatically extracted from MAF's `Workflow` objects for assertion validation
3. **Timeout Handling**: MAF's `InProcessExecution` may not honor cancellation tokens during active LLM calls, requiring hard timeout mechanisms
4. **Chat Protocol**: MAF agents use a two-phase protocol (message accumulation + TurnToken processing) that requires specific event sequencing

### Existing Solutions Considered

1. **Mock Workflows**: Create fake workflow adapters with predefined outputs
   - ❌ Doesn't test real MAF integration
   - ❌ Can't validate actual agent behavior
   - ❌ Misses timing and performance characteristics

2. **Direct MAF Usage**: Use MAF workflows without AgentEval wrapper
   - ❌ No structured evaluation capabilities
   - ❌ No assertion APIs
   - ❌ No timeline generation or visualization

3. **Fork/Wrapper Approach**: Create custom workflow execution engine
   - ❌ High maintenance overhead
   - ❌ Diverges from MAF's evolution
   - ❌ Loses MAF's native optimizations

## Decision

Adopt an **Adapter Pattern** that bridges MAF workflows into AgentEval's evaluation system while preserving MAF's native execution path.

### Architecture Components

#### 1. MAFWorkflowAdapter

```csharp
public class MAFWorkflowAdapter : IWorkflowAdapter
{
    public static MAFWorkflowAdapter FromMAFWorkflow(
        Workflow workflow,               // MAF workflow object
        string workflowName,            // Human-readable name
        string[] executorIds,           // Expected executor names  
        string? workflowType = null)    // Optional classification
    {
        // Extract graph structure via MAF's ReflectEdges()
        var graph = MAFGraphExtractor.ExtractGraph(workflow);
        
        // Create event processing bridge
        var eventBridge = new MAFWorkflowEventBridge(workflow);
        
        return new MAFWorkflowAdapter(workflowName, graph, eventBridge, executorIds);
    }
}
```

#### 2. MAFWorkflowEventBridge

Converts MAF workflow events into AgentEval evaluation model:

```csharp
public class MAFWorkflowEventBridge
{
    public async IAsyncEnumerable<WorkflowEvaluationEvent> StreamAsAgentEvalEvents(
        string input,
        CancellationToken cancellationToken)
    {
        // Protocol detection for ChatProtocol vs function-based workflows
        var protocol = await _workflow.DescribeProtocolAsync(cancellationToken);
        bool isChatProtocol = ChatProtocolExtensions.IsChatProtocol(protocol);
        
        StreamingRun run;
        if (isChatProtocol)
        {
            // ChatClientAgent workflows: ChatMessage + TurnToken
            run = await InProcessExecution.RunStreamingAsync(_workflow, 
                new ChatMessage(ChatRole.User, input), cancellationToken);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        }
        else
        {
            // Function-based workflows: direct string input
            run = await InProcessExecution.RunStreamingAsync<string>(_workflow, input, cancellationToken);
        }
        
        // Convert MAF events to AgentEval events
        await foreach (var mafEvent in run.WatchStreamAsync(cancellationToken))
        {
            var agentEvalEvent = ConvertMAFEvent(mafEvent);
            if (agentEvalEvent != null)
                yield return agentEvalEvent;
        }
    }
}
```

#### 3. MAFGraphExtractor

Extracts workflow structure from MAF workflows:

```csharp
public static class MAFGraphExtractor
{
    public static WorkflowGraphDefinition ExtractGraph(Workflow workflow)
    {
        // Use MAF's native graph reflection
        var edges = workflow.ReflectEdges();
        var nodes = ExtractNodesFromEdges(edges);
        
        return new WorkflowGraphDefinition
        {
            Nodes = nodes.Select(n => new WorkflowNode { NodeId = n }).ToList(),
            Edges = edges.Select(e => new WorkflowEdge 
            { 
                EdgeId = $"{e.Source}->{e.Target}",
                SourceNodeId = e.Source,
                TargetNodeId = e.Target,
                EdgeType = EdgeType.Sequential  // MAF uses sequential by default
            }).ToList(),
            EntryNodeId = DetermineEntryNode(edges),
            ExitNodeIds = DetermineExitNodes(edges)
        };
    }
}
```

#### 4. Event Mapping Strategy

| MAF Event | AgentEval Event | Purpose |
|-----------|-----------------|---------|
| `SuperStepStartedEvent` | WorkflowStepStartEvent | Workflow execution phase begins |
| `ExecutorInvokedEvent` | ExecutorStartEvent | Agent begins processing |  
| `AgentResponseUpdateEvent` | StreamingTokenEvent | Real-time LLM token output |
| `ExecutorCompletedEvent` | ExecutorCompleteEvent | Agent finishes processing |
| `SuperStepCompletedEvent` | WorkflowStepCompleteEvent | Workflow execution phase ends |

### Benefits

1. **Native MAF Integration**: Uses MAF's actual execution engine, not simulation
2. **Event Fidelity**: Captures real-time streaming events including token-level updates  
3. **Graph Auto-Detection**: Automatically extracts workflow structure using MAF's reflection APIs
4. **Protocol Compatibility**: Handles both ChatProtocol and function-based workflow types
5. **Future-Proof**: Adapts to MAF evolution without breaking AgentEval consumers

## Implementation Details

### Workflow Creation Pattern

```csharp
// 1. Create MAF workflow with WorkflowBuilder
var chatClient = azureClient.GetChatClient(deployment).AsIChatClient();

var planner = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "Planner",
    ChatOptions = new() { Instructions = "Create content plans..." }
});
var writer = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "Writer", 
    ChatOptions = new() { Instructions = "Write content from plans..." }
});

// 2. Bind as executors with event emission
var plannerBinding = planner.BindAsExecutor(emitEvents: true);
var writerBinding = writer.BindAsExecutor(emitEvents: true);

// 3. Build MAF workflow
var workflow = new WorkflowBuilder(plannerBinding)
    .AddEdge(plannerBinding, writerBinding)
    .Build();

// 4. Create AgentEval adapter
var adapter = MAFWorkflowAdapter.FromMAFWorkflow(
    workflow, "ContentPipeline", ["Planner", "Writer"]);

// 5. Evaluate with standard harness
var harness = new WorkflowEvaluationHarness();
var result = await harness.RunWorkflowTestAsync(adapter, testCase);
```

### Error Handling Strategy

1. **MAF Errors**: Captured via MAF's error events and mapped to AgentEval error model
2. **Timeout Errors**: Hard timeout wrapper prevents indefinite hangs (see ADR-011)
3. **Protocol Errors**: EventBridge gracefully degrades if protocol detection fails

### Performance Characteristics

- **Overhead**: <5% compared to direct MAF execution (primarily event processing)
- **Memory**: Comparable to MAF (events are streamed, not buffered)
- **Latency**: Real-time event streaming maintains MAF's responsive characteristics

## Alternatives Considered

### Alternative 1: Workflow DSL

Create AgentEval-specific workflow definition language.

**Advantages:**
- Full control over evaluation capabilities
- Optimized for testing scenarios
- Custom assertion APIs

**Disadvantages:**
- High development and maintenance cost
- Diverges from MAF standard
- Forces users to learn separate workflow system
- Can't evaluate production MAF workflows

**Decision:** Rejected - too much divergence from MAF ecosystem.

### Alternative 2: MAF Extension

Extend MAF with built-in evaluation capabilities.

**Advantages:**  
- Native integration
- No adapter complexity
- Direct access to MAF internals

**Disadvantages:**
- Requires MAF team coordination
- AgentEval becomes MAF-dependent for core features
- Harder to support multiple MAF versions
- Evaluation concerns mixed with execution concerns

**Decision:** Rejected - violates separation of concerns.

### Alternative 3: Interceptor Pattern

Intercept MAF workflow calls at execution boundary.

**Advantages:**
- Transparent to workflow definition
- Could work with any execution engine

**Disadvantages:**
- Complex implementation
- Fragile to MAF internal changes  
- Limited event visibility
- Difficult to extract graph structure

**Decision:** Rejected - too fragile and complex.

## Consequences

### Positive

1. **Real-World Fidelity**: Tests actual production workflows, not simulations
2. **MAF Ecosystem Alignment**: Maintains compatibility with MAF evolution
3. **Comprehensive Event Capture**: Full visibility into workflow execution including streaming
4. **Developer Experience**: Familiar MAF workflow creation patterns
5. **Performance Visibility**: Actual timing, cost, and resource usage data

### Negative

1. **MAF Dependency**: AgentEval workflow features require MAF installation
2. **Version Coupling**: Must maintain compatibility with MAF version evolution  
3. **Protocol Complexity**: ChatProtocol handling adds implementation complexity
4. **Testing Complexity**: Integration tests require Azure OpenAI credentials

### Implementation Risks

1. **MAF Breaking Changes**: Future MAF versions might break event processing
2. **Event Model Evolution**: MAF event structure changes could require adapter updates
3. **Performance Degradation**: Event processing overhead might impact large workflows

### Mitigation Strategies

1. **Version Pinning**: Pin to specific MAF versions with tested compatibility
2. **Graceful Degradation**: EventBridge falls back to basic execution if event processing fails
3. **Integration Test Coverage**: Comprehensive tests against actual MAF workflows
4. **Performance Monitoring**: Track adapter overhead in benchmarks

## Implementation Timeline

- **Phase 1** (Completed): Basic adapter with sequential workflow support
- **Phase 2** (Completed): Event streaming and graph extraction  
- **Phase 3** (Completed): Tool usage tracking across workflow steps
- **Phase 4** (Future): Conditional routing and parallel execution support

## Related ADRs

- [ADR-011: Workflow Event Processing and Timeout Handling](011-workflow-event-processing-timeout-handling.md)
- [ADR-012: Workflow Assertion Design](012-workflow-assertion-design.md)
- [ADR-004: Trace Recording and Replay](004-trace-recording-replay.md) - Workflow trace support

---

*This ADR documents the architectural foundation for MAF workflow integration in AgentEval, establishing patterns for event processing, graph extraction, and evaluation harness integration.*