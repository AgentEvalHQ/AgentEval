# ADR-011: Workflow Event Processing and Timeout Handling

## Status

✅ **Accepted** - February 14, 2026

## Context

MAF workflows emit real-time events during execution, but present unique challenges for evaluation systems:

1. **Event Volume**: Large workflows can generate thousands of events (Sample 09: 40+ events, Sample 21: hundreds with tool calls)
2. **Streaming Nature**: Events arrive continuously during LLM processing via `AgentResponseUpdateEvent` tokens
3. **Timeout Behavior**: MAF's `InProcessExecution` may not honor cancellation tokens during active LLM calls
4. **Protocol Complexity**: ChatProtocol workflows require specific event sequencing (message accumulation → TurnToken → processing)

### Problem Statement

Direct MAF workflow execution can hang indefinitely if:
- LLM service becomes unresponsive
- Network connectivity issues occur
- Agent processing enters infinite loops
- Cancellation tokens are ignored during LLM calls

This creates issues for:
- **CI/CD Pipelines**: Tests hang indefinitely, blocking deployment
- **Interactive Testing**: Developers lose control of test execution
- **Automated Evaluation**: Batch evaluation processes stall

### Current MAF Behavior

```csharp
// This can hang indefinitely
var run = await InProcessExecution.RunStreamingAsync(workflow, input, cancellationToken);
await foreach (var evt in run.WatchStreamAsync(cancellationToken))
{
    // Processing events - cancellationToken may be ignored during LLM calls
}
```

## Decision

Implement a **layered timeout strategy** combining graceful cancellation with hard timeout enforcement.

### Architecture: Dual-Timeout System

#### Layer 1: Graceful Cancellation (Preferred)

```csharp
// Standard cancellation token approach - works when MAF honors it
using var cts = new CancellationTokenSource(timeoutDuration);
try 
{
    var result = await harness.RunWorkflowTestAsync(adapter, testCase, 
        new WorkflowTestOptions { Timeout = timeoutDuration }, cts.Token);
}
catch (OperationCanceledException)
{
    // Graceful timeout - MAF acknowledged cancellation
    return CreateTimeoutResult("Workflow cancelled gracefully");
}
```

#### Layer 2: Hard Timeout (Fallback)

```csharp
// Hard timeout using Task.WhenAny - guaranteed to complete
var workflowTask = harness.RunWorkflowTestAsync(adapter, testCase, options);
var hardTimeout = Task.Delay(timeoutDuration);

if (await Task.WhenAny(workflowTask, hardTimeout) == hardTimeout)
{
    // Hard timeout triggered - MAF ignored cancellation
    Console.WriteLine("⏱️ Workflow exceeded hard timeout — moving on.");
    return CreateTimeoutResult("Workflow exceeded hard timeout");
}

var result = await workflowTask;
```

### Event Processing Strategy

#### 1. Event Stream Processing

Process MAF events in batches to handle high-volume streams efficiently:

```csharp
public class MAFWorkflowEventBridge
{
    private readonly List<WorkflowEvent> _eventBuffer = new();
    private readonly TimeSpan _batchInterval = TimeSpan.FromMilliseconds(100);
    
    public async IAsyncEnumerable<WorkflowEvaluationEvent> StreamAsAgentEvalEvents(
        string input, CancellationToken cancellationToken)
    {
        await foreach (var mafEvent in GetMAFEventStream(input, cancellationToken))
        {
            _eventBuffer.Add(mafEvent);
            
            // Process in batches for efficiency
            if (_eventBuffer.Count >= 10 || ShouldFlushBuffer())
            {
                foreach (var processedEvent in ProcessEventBatch(_eventBuffer))
                {
                    yield return processedEvent;
                }
                _eventBuffer.Clear();
            }
        }
        
        // Final flush
        foreach (var processedEvent in ProcessEventBatch(_eventBuffer))
        {
            yield return processedEvent;
        }
    }
}
```

#### 2. Streaming Token Aggregation

`AgentResponseUpdateEvent` tokens are aggregated into meaningful output chunks:

```csharp
public class StreamingTokenAggregator
{
    private readonly StringBuilder _currentOutput = new();
    private string _currentExecutorId = string.Empty;
    
    public void ProcessStreamingToken(AgentResponseUpdateEvent tokenEvent)
    {
        if (tokenEvent.ExecutorId != _currentExecutorId)
        {
            // Executor changed - flush previous output
            FlushCurrentOutput();
            _currentExecutorId = tokenEvent.ExecutorId;
        }
        
        _currentOutput.Append(tokenEvent.Token);
    }
    
    private void FlushCurrentOutput()
    {
        if (_currentOutput.Length > 0)
        {
            var executorOutput = new ExecutorOutputEvent(_currentExecutorId, _currentOutput.ToString());
            EmitAggregatedOutput(executorOutput);
            _currentOutput.Clear();
        }
    }
}
```

#### 3. Event Timeline Construction

Build execution timeline from event stream:

```csharp
public record WorkflowTimelineBuilder
{
    private readonly List<ExecutorStepResult> _steps = new();
    private readonly Dictionary<string, DateTime> _executorStartTimes = new();
    
    public void ProcessExecutorInvokedEvent(ExecutorInvokedEvent evt)
    {
        _executorStartTimes[evt.ExecutorId] = DateTime.UtcNow;
    }
    
    public void ProcessExecutorCompletedEvent(ExecutorCompletedEvent evt)
    {
        if (_executorStartTimes.TryGetValue(evt.ExecutorId, out var startTime))
        {
            var duration = DateTime.UtcNow - startTime;
            _steps.Add(new ExecutorStepResult
            {
                ExecutorId = evt.ExecutorId,
                StepIndex = _steps.Count,
                Duration = duration,
                StartOffset = startTime - _workflowStartTime,
                Output = evt.Output ?? string.Empty
            });
        }
    }
}
```

### Timeout Implementation Patterns

#### Pattern 1: Sample-Level Hard Timeout

Used in critical samples to prevent CI/CD hangs:

```csharp
// From Sample09_WorkflowEvaluationReal.cs
public async Task RunSample()
{
    var workflowTask = harness.RunWorkflowTestAsync(workflowAdapter, testCase, testOptions);
    var hardTimeout = Task.Delay(TimeSpan.FromMinutes(5));

    if (await Task.WhenAny(workflowTask, hardTimeout) == hardTimeout)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("   ⏱️ Workflow exceeded 5-minute hard timeout — moving on.");
        Console.ResetColor();
        Console.WriteLine("   💡 This can happen with slow LLM backends or long content-generation chains.");
        return;  // Graceful sample termination
    }

    var testResult = await workflowTask;
    // Process successful result...
}
```

#### Pattern 2: Harness-Level Timeout Management

Built into `WorkflowEvaluationHarness` for consistent behavior:

```csharp 
public class WorkflowEvaluationHarness
{
    public async Task<WorkflowTestResult> RunWorkflowTestAsync(
        IWorkflowAdapter adapter,
        WorkflowTestCase testCase,
        WorkflowTestOptions? options = null)
    {
        var timeout = options?.Timeout ?? TimeSpan.FromMinutes(10);
        
        using var cts = new CancellationTokenSource(timeout);
        
        try
        {
            // Layer 1: Graceful cancellation
            var executionTask = ExecuteWorkflowWithEvents(adapter, testCase, cts.Token);
            var hardTimeoutTask = Task.Delay(timeout.Add(TimeSpan.FromSeconds(30))); // Extra grace period
            
            if (await Task.WhenAny(executionTask, hardTimeoutTask) == hardTimeoutTask)
            {
                // Layer 2: Hard timeout
                return new WorkflowTestResult
                {
                    TestName = testCase.Name,
                    Passed = false,
                    Duration = timeout,
                    ErrorMessage = "Workflow execution exceeded hard timeout"
                };
            }
            
            return await executionTask;
        }
        catch (OperationCanceledException)
        {
            // Layer 1: Graceful timeout
            return new WorkflowTestResult
            {
                TestName = testCase.Name,
                Passed = false,
                Duration = timeout,
                ErrorMessage = "Workflow execution was cancelled due to timeout"
            };
        }
    }
}
```

#### Pattern 3: Per-Executor Timeout Tracking

Track individual agent timeouts within workflows:

```csharp
public class ExecutorTimeoutTracker
{
    private readonly TimeSpan _maxExecutorDuration;
    private readonly Dictionary<string, DateTime> _executorStartTimes = new();
    
    public void OnExecutorStarted(ExecutorInvokedEvent evt)
    {
        _executorStartTimes[evt.ExecutorId] = DateTime.UtcNow;
    }
    
    public bool CheckExecutorTimeout(string executorId, out TimeSpan actualDuration)
    {
        if (_executorStartTimes.TryGetValue(executorId, out var startTime))
        {
            actualDuration = DateTime.UtcNow - startTime;
            return actualDuration > _maxExecutorDuration;
        }
        
        actualDuration = TimeSpan.Zero;
        return false;
    }
}
```

### Event Processing Performance

#### Memory Optimization

For large workflows with many events:

```csharp
public class MemoryOptimizedEventProcessor
{
    // Process events as they arrive, don't buffer everything
    public async IAsyncEnumerable<WorkflowEvaluationEvent> ProcessEventStream(
        IAsyncEnumerable<WorkflowEvent> eventStream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in eventStream.WithCancellation(cancellationToken))
        {
            // Process immediately and yield - no accumulation
            var processedEvent = ConvertEvent(evt);
            if (processedEvent != null)
            {
                yield return processedEvent;
            }
            
            // Yield control periodically for responsive cancellation
            if (++_processedCount % 100 == 0)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}
```

#### CPU Optimization

Minimize event processing overhead:

```csharp
// Use object pooling for frequent event objects
private static readonly ObjectPool<ExecutorStepResult> _stepPool = 
    ObjectPool.Create<ExecutorStepResult>();

// Cache event type mappings to avoid reflection
private static readonly Dictionary<Type, Func<WorkflowEvent, WorkflowEvaluationEvent?>> _eventConverters = 
    new()
    {
        [typeof(ExecutorInvokedEvent)] = evt => ConvertExecutorInvokedEvent((ExecutorInvokedEvent)evt),
        [typeof(AgentResponseUpdateEvent)] = evt => ConvertAgentResponseUpdateEvent((AgentResponseUpdateEvent)evt),
        // ... other mappings
    };
```

## Implementation Guidelines

### 1. Timeout Configuration

Always provide configurable timeouts with reasonable defaults:

```csharp
public record WorkflowTestOptions
{
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(10);  // Default
    public TimeSpan? PerExecutorTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public bool EnableHardTimeout { get; init; } = true;
    public TimeSpan? GracePeriod { get; init; } = TimeSpan.FromSeconds(30);
}
```

### 2. Timeout Logging

Provide detailed timeout information for debugging:

```csharp
public class TimeoutLogger
{
    public void LogTimeout(string workflowName, TimeSpan actualDuration, TimeSpan timeoutThreshold)
    {
        _logger.LogWarning(
            "Workflow {WorkflowName} exceeded timeout. Duration: {ActualDuration}, Threshold: {TimeoutThreshold}",
            workflowName, actualDuration, timeoutThreshold);
            
        // Include additional context
        _logger.LogInformation(
            "Last executor: {LastExecutor}, Events processed: {EventCount}",
            _lastExecutorId, _processedEventCount);
    }
}
```

### 3. Graceful Degradation

When timeouts occur, provide useful partial results:

```csharp
public WorkflowTestResult CreatePartialResult(string reason, TimeSpan actualDuration)
{
    return new WorkflowTestResult
    {
        TestName = testCase.Name,
        Passed = false,
        Duration = actualDuration,
        ErrorMessage = reason,
        ExecutionResult = new WorkflowExecutionResult
        {
            ActualOutput = _lastKnownOutput ?? "Timeout before completion",
            Steps = _completedSteps,  // Return whatever steps completed
            Performance = CreatePartialPerformanceMetrics(),
            GraphDefinition = _extractedGraph  // Graph extraction usually succeeds quickly
        }
    };
}
```

## Benefits

1. **Reliability**: Workflows never hang indefinitely, critical for CI/CD
2. **Debugging**: Timeout information helps identify performance issues
3. **Performance**: Event batching and streaming reduces memory usage
4. **Flexibility**: Multiple timeout layers handle different failure modes
5. **Observability**: Detailed event processing provides execution visibility

## Trade-offs

### Advantages

- **Guaranteed Completion**: Hard timeout ensures tests always complete
- **Real-time Monitoring**: Event streaming provides immediate feedback
- **Resource Efficiency**: Streaming processing uses constant memory
- **Developer Experience**: Clear timeout messages aid debugging

### Disadvantages

- **Complexity**: Dual-timeout system adds implementation complexity
- **Potential False Timeouts**: Aggressive timeouts might interrupt legitimate long-running workflows
- **Event Processing Overhead**: Additional processing layer adds latency (~5%)
- **Memory Overhead**: Event processing state requires additional memory

## Alternatives Considered

### Alternative 1: MAF-Only Cancellation

Rely entirely on MAF's cancellation token support.

**Advantages:**
- Simple implementation
- No additional timeout logic needed
- Native MAF behavior preserved

**Disadvantages:**
- **Critical Flaw**: MAF may not honor cancellation during LLM calls
- Tests can hang indefinitely in CI
- No fallback when cancellation fails

**Decision:** Rejected due to reliability concerns.

### Alternative 2: Process Isolation

Run each workflow in a separate process with process-level timeouts.

**Advantages:**
- Guaranteed termination via process kill
- Complete isolation between tests
- No shared state concerns

**Disadvantages:**
- High overhead (process creation/teardown)
- Complex inter-process communication
- Difficult debugging
- Performance impact

**Decision:** Rejected due to complexity and performance impact.

### Alternative 3: Thread-Based Timeouts

Use `Thread.Abort()` or similar mechanisms.

**Advantages:**
- Lower overhead than process isolation
- Guaranteed termination

**Disadvantages:**
- `Thread.Abort()` is deprecated/dangerous in .NET
- Can corrupt application state
- Difficult to clean up resources
- Not supported in async contexts

**Decision:** Rejected due to safety concerns and .NET async incompatibility.

## Monitoring and Metrics

Track timeout behavior to optimize threshold values:

```csharp
public class WorkflowTimeoutMetrics
{
    public void RecordWorkflowDuration(string workflowName, TimeSpan duration, bool timedOut)
    {
        _metrics.Record(\"workflow_duration_seconds\", duration.TotalSeconds, 
            new[] { (\"workflow\", workflowName), (\"timed_out\", timedOut.ToString()) });
            
        if (timedOut)
        {
            _metrics.Increment(\"workflow_timeouts_total\", 
                new[] { (\"workflow\", workflowName) });
        }
    }
}
```

## Testing Strategy

Validate timeout behavior with dedicated test cases:

```csharp
[Fact]
public async Task WorkflowHarness_Should_HardTimeoutAfterThreshold()
{
    var slowWorkflow = CreateInfiniteLoopWorkflow();
    var harness = new WorkflowEvaluationHarness();
    var options = new WorkflowTestOptions { Timeout = TimeSpan.FromSeconds(5) };
    
    var stopwatch = Stopwatch.StartNew();
    var result = await harness.RunWorkflowTestAsync(slowWorkflow, testCase, options);
    stopwatch.Stop();
    
    Assert.False(result.Passed);
    Assert.Contains("timeout", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10)); // Hard bound
}
```

## Related ADRs

- [ADR-010: MAF Workflow Integration Architecture](010-maf-workflow-integration-architecture.md) - Overall workflow architecture
- [ADR-012: Workflow Assertion Design](012-workflow-assertion-design.md) - Assertion patterns for timeout validation
- [ADR-009: Benchmark Strategy](009-benchmark-strategy.md) - Performance measurement including timeout impact

---

*This ADR establishes the foundation for reliable workflow evaluation with guaranteed completion times, enabling robust CI/CD integration and developer productivity.*