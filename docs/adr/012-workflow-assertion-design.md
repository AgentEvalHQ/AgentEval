# ADR-012: Workflow Assertion Design

## Status

✅ **Accepted** - February 14, 2026

## Context

Workflow evaluation requires a rich assertion API that can validate complex multi-agent execution patterns. Unlike simple agent evaluations that focus on single inputs/outputs, workflows involve:

1. **Sequential Execution**: Validating that agents execute in the correct order
2. **Graph Structure**: Asserting on workflow topology and edge traversal
3. **Per-Executor Validation**: Individual agent performance within the workflow context
4. **Tool Chain Evaluation**: Tool usage patterns across multiple agents
5. **Timing and Performance**: Execution timing, costs, and resource constraints
6. **Error Propagation**: How errors flow through the workflow pipeline

### Design Challenges

1. **Complexity**: Workflows have many validation dimensions (structure, timing, tools, outputs)
2. **Readability**: Assertions should read naturally despite complex validation logic
3. **Composability**: Multiple assertion types must work together seamlessly
4. **Error Messages**: Clear failure messages with actionable suggestions
5. **Performance**: Assertions shouldn't significantly impact evaluation performance

### Existing Patterns

AgentEval already has successful fluent assertion patterns:

```csharp
// Tool usage assertions (proven pattern)
result.ToolUsage!.Should()
    .HaveCalledTool("SearchFlights")
    .BeforeTool("BookFlight")
    .WithoutError();

// Performance assertions (proven pattern)  
result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))
    .HaveEstimatedCostUnder(0.05m);
```

Challenge: Extend this pattern to workflow complexity without losing readability.

## Decision

Design a **hierarchical fluent assertion system** that mirrors workflow structure while maintaining AgentEval's existing assertion patterns.

### Architecture: Hierarchical Assertions

#### Level 1: Workflow-Level Assertions

```csharp
// Entry point - validates overall workflow execution
public static WorkflowResultAssertions Should(this WorkflowExecutionResult result)
{
    return new WorkflowResultAssertions(result);
}

public class WorkflowResultAssertions
{
    public WorkflowResultAssertions HaveStepCount(int expectedCount, string? because = null)
    public WorkflowResultAssertions HaveExecutedInOrder(params string[] executorIds)
    public WorkflowResultAssertions HaveCompletedWithin(TimeSpan duration, string? because = null)
    public WorkflowResultAssertions HaveNoErrors(string? because = null)
    public WorkflowResultAssertions HaveNonEmptyOutput(string? because = null)
    
    // Navigate to sub-assertions
    public ExecutorAssertions ForExecutor(string executorId)
    public GraphAssertions HaveGraphStructure()
    public ToolUsageAssertions HaveCalledTool(string toolName, string? because = null)
    public PerformanceAssertions Performance => new(_result.Performance);
}
```

#### Level 2: Per-Executor Assertions

> **Note (shipped API):** This ADR documents the *designed* API. The executor-scoped cost/token asserts below (`HaveInputTokensLessThan`, `HaveEstimatedCostUnder`) and `HaveOutputLongerThan` were **not** implemented on the executor scope. Use `result.Performance!.Should().HaveEstimatedCostUnder(...)` for cost/tokens and `HaveNonEmptyOutput()` for output presence. See docs/workflows.md.

```csharp
public class ExecutorAssertions
{
    private readonly WorkflowExecutionResult _result;
    private readonly string _executorId;
    
    public ExecutorAssertions HaveNonEmptyOutput(string? because = null)
    public ExecutorAssertions HaveOutputContaining(string text, string? because = null)
    public ExecutorAssertions HaveOutputLongerThan(int minLength, string? because = null)
    public ExecutorAssertions HaveCompletedWithin(TimeSpan duration, string? because = null)
    public ExecutorAssertions HaveToolCalls(string? because = null)
    public ExecutorAssertions HaveNoErrors(string? because = null)
    public ExecutorAssertions HaveInputTokensLessThan(int maxTokens, string? because = null)
    public ExecutorAssertions HaveEstimatedCostUnder(decimal maxCost, string? because = null)
    
    // Return to workflow level
    public WorkflowResultAssertions And() => new(_result);
}
```

#### Level 3: Graph Structure Assertions

```csharp
public class GraphAssertions  
{
    public GraphAssertions HaveNodes(params string[] expectedNodes)
    public GraphAssertions HaveEntryPoint(string nodeId, string? because = null)
    public GraphAssertions HaveExitPoint(string nodeId, string? because = null)
    public GraphAssertions HaveTraversedEdge(string sourceNode, string targetNode)
    public GraphAssertions NotHaveTraversedEdge(string sourceNode, string targetNode)
    public GraphAssertions HaveExecutionPath(params string[] expectedPath)
    public GraphAssertions HaveUsedEdgeType(EdgeType expectedType)
    
    // Return to workflow level
    public WorkflowResultAssertions And() => _workflowAssertions;
}
```

### Assertion Chaining Strategy

Enable natural reading flow with multiple assertion levels:

```csharp
// Complex assertion chain - reads like specification
result.ExecutionResult!.Should()
    // Level 1: Overall workflow structure
    .HaveStepCount(4, because: "pipeline has 4 distinct stages")
    .HaveExecutedInOrder("Planner", "Researcher", "Writer", "Editor")
    .HaveCompletedWithin(TimeSpan.FromMinutes(3), because: "reasonable time for content generation")
    .HaveNoErrors(because: "clean execution is required")
    
    // Level 2: Per-executor validation
    .ForExecutor("Planner")
        .HaveNonEmptyOutput()
        .HaveCompletedWithin(TimeSpan.FromSeconds(60), because: "planning should be reasonably fast")
        .And()
    .ForExecutor("Writer")    
        .HaveNonEmptyOutput()
        .And()
        
    // Level 3: Graph validation
    .HaveGraphStructure()
    .HaveEntryPoint("Planner", because: "planning is the starting point")  
    .HaveExecutionPath("Planner", "Researcher", "Writer", "Editor")
    
    // Back to Level 1: Tool validation (if applicable)
    .HaveCalledTool("SearchFlights")?.WithoutError()
    .And()
    
    // Final validation trigger
    .Validate();
```

### Error Message Design

#### Structured Error Information

```csharp
public class WorkflowAssertionException : AgentEvalAssertionException
{
    public required string WorkflowName { get; init; }
    public required string AssertionType { get; init; }  // "StepCount", "ExecutionOrder", etc.
    public required object Expected { get; init; }
    public required object Actual { get; init; }
    public string? ExecutorId { get; init; }            // For executor-specific failures
    public TimeSpan? ActualDuration { get; init; }      // For timing failures
    public List<string> Suggestions { get; init; } = new();
}
```

#### Rich Error Messages

```csharp
// Example: Step count assertion failure
throw new WorkflowAssertionException
{
    WorkflowName = "ContentPipeline",
    AssertionType = "StepCount", 
    Expected = 4,
    Actual = 3,
    Message = "Expected workflow 'ContentPipeline' to have 4 steps, but found 3 steps.",
    Suggestions = 
    [
        "Check if all agents are properly bound as executors",
        "Verify workflow graph has all expected edges",
        "Ensure no agent failed silently during execution"
    ]
};

// Example: Execution order failure
throw new WorkflowAssertionException
{
    WorkflowName = "ContentPipeline",
    AssertionType = "ExecutionOrder",
    Expected = ["Planner", "Researcher", "Writer", "Editor"],
    Actual = ["Planner", "Writer", "Editor"],  // Missing Researcher
    Message = "Expected execution order [Planner, Researcher, Writer, Editor], but got [Planner, Writer, Editor]. Missing: Researcher.",
    Suggestions =
    [
        "Verify Researcher agent is bound correctly",
        "Check if Planner → Researcher edge exists in workflow",
        "Ensure Researcher agent didn't fail silently"
    ]
};

// Example: Per-executor timeout failure
throw new WorkflowAssertionException
{
    WorkflowName = "ContentPipeline",
    AssertionType = "ExecutorTimeout",
    ExecutorId = "Writer",
    Expected = TimeSpan.FromSeconds(60),
    ActualDuration = TimeSpan.FromMinutes(3),
    Message = "Expected executor 'Writer' to complete within 60 seconds, but took 3 minutes.",
    Suggestions =
    [
        "Consider increasing timeout for content generation tasks",
        "Check if Writer agent is using efficient prompts",
        "Verify LLM service response times are normal"
    ]
};
```

### Performance-Optimized Validation

#### Lazy Validation Pattern

```csharp
public class WorkflowResultAssertions
{
    private readonly List<Func<WorkflowExecutionResult, AssertionResult>> _assertions = new();
    
    public WorkflowResultAssertions HaveStepCount(int expectedCount, string? because = null)
    {
        _assertions.Add(result => ValidateStepCount(result, expectedCount, because));
        return this;  // Fluent chaining
    }
    
    // Validate() actually executes all assertions
    public void Validate()
    {
        var failures = new List<WorkflowAssertionException>();
        
        foreach (var assertion in _assertions)
        {
            var result = assertion(_result);
            if (!result.Success)
            {
                failures.Add(result.Exception);
            }
        }
        
        if (failures.Count > 0)
        {
            throw new AggregateWorkflowAssertionException(failures);
        }
    }
}
```

#### Assertion Result Caching

```csharp
public class AssertionResultCache
{
    private readonly Dictionary<string, object?> _cache = new();
    
    public T GetOrCompute<T>(string key, Func<T> computation)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            return (T)cached!;
        }
        
        var result = computation();
        _cache[key] = result;
        return result;
    }
}

// Usage in assertions
public bool ValidateGraphStructure()
{
    return _cache.GetOrCompute("graph_extracted", () => 
    {
        // Expensive graph extraction - cache result
        return _result.GraphDefinition != null && _result.GraphDefinition.Nodes.Count > 0;
    });
}
```

### Tool Usage Integration

Extend existing tool assertions to work in workflow context:

```csharp
// Workflow-level tool validation (aggregated across all executors)
result.ExecutionResult!.Should()
    .HaveCalledTool("GetInfoAbout", because: "TripPlanner must research cities")
        .AtLeast(2.Times())                                    // Multiple cities
        .WithoutError()
        .InExecutor("TripPlanner")                            // Specific executor
        .And()
    .HaveCalledTool("SearchFlights")  
        .BeforeTool("BookFlight", because: "can't book without search results")
        .InExecutor("FlightReservation")
        .WithArgument("from", "Seattle")
        .And()
    .HaveToolCallPattern("Search", "Book")                    // Pattern across workflow
    .HaveNoToolErrors()
    .Validate();
```

### Conditional Assertions

Handle workflows where some assertions are conditional:

```csharp
public class WorkflowResultAssertions 
{
    public WorkflowResultAssertions When(Func<WorkflowExecutionResult, bool> condition)
    {
        _currentCondition = condition;
        return this;
    }
    
    public WorkflowResultAssertions HaveCalledTool(string toolName, string? because = null)
    {
        if (_currentCondition == null || _currentCondition(_result))
        {
            // Only validate if condition is met
            _assertions.Add(result => ValidateToolCall(result, toolName, because));
        }
        return this;
    }
}

// Usage: Only validate tools if workflow actually has tool usage
result.ExecutionResult!.Should()
    .When(r => r.ToolUsage != null)
        .HaveCalledTool("SearchFlights")
        .HaveNoToolErrors()
    .And()
    .HaveStepCount(4);  // Always validate step count
```

### Assertion Composition Patterns

#### Common Assertion Bundles

```csharp
// Pre-built assertion patterns for common scenarios
public static class WorkflowAssertionBundles
{
    public static WorkflowResultAssertions ValidateSequentialPipeline(
        this WorkflowResultAssertions assertions,
        params string[] expectedExecutors)
    {
        return assertions
            .HaveStepCount(expectedExecutors.Length)
            .HaveExecutedInOrder(expectedExecutors)
            .HaveNoErrors()
            .HaveNonEmptyOutput();
    }
    
    public static WorkflowResultAssertions ValidatePerformanceBounds(
        this WorkflowResultAssertions assertions,
        TimeSpan maxDuration,
        decimal maxCost)
    {
        return assertions
            .HaveCompletedWithin(maxDuration)
            .Performance!.HaveEstimatedCostUnder(maxCost)
            .And();
    }
}

// Usage
result.ExecutionResult!.Should()
    .ValidateSequentialPipeline("Planner", "Writer", "Editor")
    .ValidatePerformanceBounds(TimeSpan.FromMinutes(2), 0.50m)
    .Validate();
```

#### Custom Assertion Extensions

```csharp
// Domain-specific extensions
public static class ContentPipelineAssertions
{
    public static WorkflowResultAssertions ValidateContentQuality(
        this WorkflowResultAssertions assertions)
    {
        return assertions
            .ForExecutor("Planner")
                .HaveOutputContaining("outline")
                .HaveOutputContaining("research")
                .And()
            .ForExecutor("Writer")
                .HaveNonEmptyOutput()
                .And()
            .ForExecutor("Editor")
                .And();
    }
}
```

## Benefits

### Readability Benefits

1. **Natural Language Flow**: Assertions read like specifications
2. **Hierarchical Structure**: Mirrors workflow complexity naturally
3. **Selective Focus**: Can validate just structure, just performance, or everything
4. **Contextual Chaining**: `.And()` maintains assertion context clearly

### Maintainability Benefits  

1. **Composable**: Assertion bundles reduce duplication
2. **Extensible**: Easy to add new assertion types
3. **Consistent**: Follows established AgentEval patterns
4. **Cacheable**: Expensive validations cached automatically

### Developer Experience Benefits

1. **Rich Error Messages**: Clear expected/actual with actionable suggestions
2. **Incremental Validation**: Can build assertions step-by-step
3. **IntelliSense Support**: Fluent API provides good IDE experience
4. **Conditional Logic**: `When()` clause handles complex scenarios

## Implementation Strategy

### Phase 1: Core Workflow Assertions (Completed)

- Basic structure validation (step count, execution order)
- Per-executor validation (output, timing)
- Error handling and reporting
- Integration with existing `WorkflowEvaluationHarness`

### Phase 2: Advanced Assertions (Completed)

- Graph structure validation 
- Tool usage integration at workflow level
- Performance and cost validation
- Conditional assertion logic

### Phase 3: Optimization & Convenience (Future)

- Assertion result caching for expensive operations
- Pre-built assertion bundles for common patterns
- Custom assertion extension points
- Performance monitoring of assertion overhead

## Testing Strategy

### Unit Tests for Assertions

```csharp
[Fact]
public void HaveStepCount_WhenCorrectCount_ShouldPass()
{
    var result = CreateWorkflowResult(stepCount: 3);
    
    var assertion = () => result.Should().HaveStepCount(3).Validate();
    
    assertion.Should().NotThrow();
}

[Fact] 
public void HaveStepCount_WhenIncorrectCount_ShouldThrowWithDetails()
{
    var result = CreateWorkflowResult(stepCount: 2);
    
    var assertion = () => result.Should().HaveStepCount(3).Validate();
    
    var exception = assertion.Should().Throw<WorkflowAssertionException>().Which;
    exception.AssertionType.Should().Be("StepCount");
    exception.Expected.Should().Be(3);
    exception.Actual.Should().Be(2);
    exception.Suggestions.Should().NotBeEmpty();
}

[Fact]
public void ForExecutor_WhenChained_ShouldValidateBoth()
{
    var result = CreateWorkflowResult();
    
    var assertion = () => result.Should()
        .ForExecutor("Agent1")
            .HaveNonEmptyOutput()
            .And()
        .ForExecutor("Agent2")
            .HaveNonEmptyOutput()
            .And()
        .Validate();
        
    assertion.Should().NotThrow();
}
```

### Integration Tests with Real Workflows

```csharp
[Fact]
public async Task ComplexWorkflowAssertions_ShouldValidateCorrectly()
{
    var result = await harness.RunWorkflowTestAsync(complexAdapter, testCase);
    
    // Test complex assertion chain
    result.ExecutionResult!.Should()
        .ValidateSequentialPipeline("A", "B", "C", "D")
        .ValidatePerformanceBounds(TimeSpan.FromMinutes(5), 1.0m)
        .ForExecutor("C")
            .HaveCalledTool("ProcessData")
            .And()
        .HaveGraphStructure()
            .HaveTraversedEdge("A", "B")
            .HaveTraversedEdge("B", "C")
        .Validate();
}
```

## Alternatives Considered

### Alternative 1: Separate Assertion Classes

Create separate classes for each assertion type.

**Advantages:**
- Clear separation of concerns
- Easier to unit test individual assertion types
- More explicit interfaces

**Disadvantages:**
- Breaks fluent chaining
- Requires manual composition
- Less readable assertion chains
- More complex API surface

**Decision:** Rejected - fluent chaining is core to AgentEval's assertion design.

### Alternative 2: Configuration-Based Assertions

Define assertions in YAML/JSON configuration.

**Advantages:**
- Non-developers can write assertions
- Version control for assertion definitions
- Reusable assertion libraries

**Disadvantages:**
- Not type-safe
- Poor IDE support
- Limited expressiveness
- Complex error reporting

**Decision:** Rejected - type safety and IDE support are crucial for developer productivity.

### Alternative 3: Simple Boolean Methods

Use simple methods returning true/false.

```csharp
Assert.True(result.HasStepCount(4));
Assert.True(result.ExecutedInOrder("A", "B", "C"));
```

**Advantages:**
- Simple implementation
- Familiar to xUnit users
- Minimal learning curve

**Disadvantages:**
- Poor error messages
- No fluent chaining
- Inconsistent with AgentEval patterns
- Limited composability

**Decision:** Rejected - doesn't provide the rich error experience AgentEval requires.

## Performance Considerations

### Assertion Overhead

```csharp
// Measured overhead of assertion processing
public class AssertionPerformanceMetrics
{
    public TimeSpan ValidationTime { get; }      // ~1-5ms for complex workflows
    public long MemoryUsage { get; }             // ~10KB for assertion state
    public int AssertionCount { get; }           // Track assertion complexity
}
```

### Optimization Strategies

1. **Lazy Evaluation**: Don't compute expensive validations until `.Validate()` called
2. **Result Caching**: Cache expensive computations (graph extraction, tool analysis)
3. **Early Exit**: Stop validation on first failure when appropriate
4. **Batch Operations**: Group similar validations for efficiency

## Related ADRs

- [ADR-010: MAF Workflow Integration Architecture](010-maf-workflow-integration-architecture.md) - Foundation for workflow evaluation
- [ADR-011: Workflow Event Processing and Timeout Handling](011-workflow-event-processing-timeout-handling.md) - Event processing that feeds assertions
- [ADR-003: CLI Review Commands](003-cli-review-commands.md) - CLI integration for workflow assertions 
- [ADR-006: Service-Based Architecture](006-service-based-architecture-di.md) - DI integration for assertion services

---

*This ADR establishes the assertion design principles for workflow evaluation, ensuring consistent, readable, and comprehensive validation capabilities across AgentEval's workflow features.*