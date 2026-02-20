# Workflow Evaluation

> **Comprehensive guide to evaluating multi-agent workflows with AgentEval and Microsoft Agent Framework (MAF)**

AgentEval provides first-class support for evaluating Microsoft Agent Framework (MAF) workflows. Build real workflow pipelines with `WorkflowBuilder`, then evaluate them with structured assertions, timing validation, and rich execution reporting.

## Overview

MAF workflows orchestrate multiple agents in sequential or complex execution patterns:
- **Sequential pipelines**: One agent's output feeds the next (Planner → Researcher → Writer → Editor)
- **Tool-enabled workflows**: Agents with function calling working together
- **Event streaming**: Real-time execution monitoring with `AgentResponseUpdateEvent`
- **Graph extraction**: Automatic workflow structure analysis

AgentEval captures the complete workflow execution, enabling you to:
- **Evaluate execution order**: Assert agents executed in correct sequence
- **Validate timing**: Ensure workflows complete within acceptable timeframes
- **Track tool usage**: Monitor function calls across all agents
- **Generate visualizations**: Export Mermaid diagrams and timeline JSON

## Quick Start

```csharp
using AgentEval.MAF;
using AgentEval.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.WorkflowBuilder;

// 1. Create MAF workflow with WorkflowBuilder
var chatClient = new AzureOpenAIClient(endpoint, credential)
    .GetChatClient(deployment).AsIChatClient();

var planner = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "Planner", 
    ChatOptions = new() { Instructions = "Create content plans" }
});
var writer = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "Writer", 
    ChatOptions = new() { Instructions = "Write content from plans" }
});

// Bind agents as executors with event emission
var plannerBinding = planner.BindAsExecutor(emitEvents: true);
var writerBinding = writer.BindAsExecutor(emitEvents: true);

// Build MAF workflow
var workflow = new WorkflowBuilder(plannerBinding)
    .AddEdge(plannerBinding, writerBinding)
    .Build();

// 2. Create AgentEval adapter
var executorIds = new[] { "Planner", "Writer" };
var adapter = MAFWorkflowAdapter.FromMAFWorkflow(
    workflow, "ContentPipeline", executorIds, "PromptChaining");

// 3. Evaluate with WorkflowEvaluationHarness
var harness = new WorkflowEvaluationHarness(verbose: true);
var testCase = new WorkflowTestCase
{
    Name = "Content Generation Test",
    Input = "Write an article about AI agents",
    ExpectedSteps = ["Planner", "Writer"]
};

var result = await harness.RunWorkflowTestAsync(adapter, testCase);

// 4. Assert on execution
result.ExecutionResult!.Should()
    .HaveStepCount(2)
    .HaveExecutedInOrder("Planner", "Writer")
    .HaveCompletedWithin(TimeSpan.FromMinutes(2))
    .HaveNoErrors();
```

## Core Concepts

### MAF Workflow Integration

AgentEval integrates with **Microsoft Agent Framework (MAF)**'s native workflow system:

1. **Agent Binding**: Agents are bound as MAF executors with event emission enabled
2. **WorkflowBuilder**: Use MAF's `WorkflowBuilder` to define execution graphs
3. **Event Streaming**: MAF emits real-time execution events via `WatchStreamAsync()`
4. **Adapter Pattern**: `MAFWorkflowAdapter` bridges MAF workflows to AgentEval evaluation

### Workflow Test Result

The `WorkflowTestResult` captures the complete evaluation:

```csharp
public record WorkflowTestResult
{
    // Test metadata
    public required string TestName { get; init; }
    public required TimeSpan Duration { get; init; }
    public required bool Passed { get; init; }
    
    // Execution details
    public WorkflowExecutionResult? ExecutionResult { get; init; }
    
    // Error information
    public string? ErrorMessage { get; init; }
    public Exception? Exception { get; init; }
}

public record WorkflowExecutionResult  
{
    // Final workflow output
    public required string ActualOutput { get; init; }
    
    // Step-by-step execution
    public required IReadOnlyList<ExecutorStepResult> Steps { get; init; }
    
    // Performance metrics
    public required PerformanceMetrics Performance { get; init; }
    
    // Tool usage across all agents
    public ToolUsageResult? ToolUsage { get; init; }
    
    // Graph structure (extracted from MAF)
    public WorkflowGraphDefinition? GraphDefinition { get; init; }
    
    // Rich execution timeline
    public WorkflowTimelineResult Timeline { get; init; }
}
```

### Executor Step Results

Each agent (executor) execution is captured as an `ExecutorStepResult`:

```csharp
public record ExecutorStepResult
{
    // Agent identification
    public required string ExecutorId { get; init; }     // "Planner", "Writer", etc.
    public required int StepIndex { get; init; }          // 0, 1, 2...
    
    // Output and timing
    public required string Output { get; init; }         // Agent's response
    public required TimeSpan StartOffset { get; init; }  // When step started
    public required TimeSpan Duration { get; init; }     // Step execution time
    
    // Tool usage (if agent used tools)
    public IReadOnlyList<ToolCallRecord>? ToolCalls { get; init; }
    public bool HasToolCalls => ToolCalls?.Count > 0;
    
    // Error information
    public IReadOnlyList<AgentError>? Errors { get; init; }
    public bool HasErrors => Errors?.Count > 0;
    
    // Performance metrics
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public decimal? EstimatedCost { get; init; }
}
```

### MAF Event Processing

AgentEval captures these MAF workflow events:

| MAF Event | Purpose |
|-----------|----------|
| `SuperStepStartedEvent` | Workflow superstep begins |
| `ExecutorInvokedEvent` | Agent begins processing |
| `AgentResponseUpdateEvent` | Streaming token from LLM |
| `ExecutorCompletedEvent` | Agent finishes processing |
| `SuperStepCompletedEvent` | Workflow superstep ends |

Events are processed by `MAFWorkflowEventBridge` into AgentEval's evaluation model.

### Timeout Handling

MAF workflows may not honor cancellation tokens during active LLM calls. AgentEval implements a **hard timeout pattern** in critical samples:

```csharp
var workflowTask = harness.RunWorkflowTestAsync(adapter, testCase, options);
var hardTimeout = Task.Delay(TimeSpan.FromMinutes(5));

if (await Task.WhenAny(workflowTask, hardTimeout) == hardTimeout)
{
    Console.WriteLine("⏱️ Workflow exceeded hard timeout — moving on.");
    return; // Graceful timeout handling
}

var result = await workflowTask;
```

This prevents indefinite hangs in CI/CD environments where workflows might stall.

### Graph Structure Extraction

AgentEval automatically extracts workflow graph structure from MAF workflows:

```csharp
public record WorkflowGraphDefinition
{
    public IReadOnlyList<WorkflowNode> Nodes { get; init; }
    public IReadOnlyList<WorkflowEdge> Edges { get; init; }
    public string? EntryNodeId { get; init; }
    public IReadOnlyList<string> ExitNodeIds { get; init; }
}

public record WorkflowNode
{
    public required string NodeId { get; init; }         // "Planner", "Writer"
    public string? DisplayName { get; init; }            // Human-readable name
    public bool IsEntryPoint { get; init; }             // First node
    public bool IsExitPoint { get; init; }              // Last node
}

public record WorkflowEdge  
{
    public required string EdgeId { get; init; }
    public required string SourceNodeId { get; init; }   // "Planner"
    public required string TargetNodeId { get; init; }   // "Writer"
    public EdgeType EdgeType { get; init; }              // Sequential, etc.
}
```

## Creating MAF Workflows

### Sequential Pipeline Pattern

The most common workflow pattern is a sequential pipeline where each agent builds on the previous agent's work:

```csharp
// 1. Create AI agents with distinct roles
var azureClient = new AzureOpenAIClient(endpoint, credential);
var chatClient = azureClient.GetChatClient(deployment).AsIChatClient();

var planner = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "Planner",
    Description = "Plans content structure",
    ChatOptions = new ChatOptions
    {
        Instructions = """
            You are a content planning specialist. Create structured plans with:
            1. Logical outline with main sections
            2. Key research points per section  
            3. Target audience and tone
            4. Suggested word count
            """
    }
});

var researcher = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "Researcher", 
    Description = "Researches topics based on a plan",
    ChatOptions = new ChatOptions
    {
        Instructions = """
            You are a research specialist. Given a content plan:
            1. Identify research needs for each section
            2. Synthesize information into organized research notes
            3. Include key facts, data points, and expert insights
            4. Note current trends and credible sources
            """
    }
});

var writer = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "Writer",
    Description = "Writes comprehensive articles from research", 
    ChatOptions = new ChatOptions
    {
        Instructions = """
            You are an experienced technical writer. Transform research into:
            1. Well-structured, flowing prose
            2. Clear, accessible language with technical accuracy
            3. Practical examples and actionable insights
            4. Engaging introduction and strong conclusion
            """
    }
});

var editor = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "Editor",
    Description = "Polishes and refines articles",
    ChatOptions = new ChatOptions
    {
        Instructions = """
            You are a professional editor. Polish the draft for:
            1. Clarity, flow, and engagement
            2. Improved sentence structure and word choice
            3. Consistent tone and style
            4. Grammar, punctuation, and formatting
            """
    }
});

// 2. Bind agents as MAF executors with event emission
var plannerBinding = planner.BindAsExecutor(emitEvents: true);
var researcherBinding = researcher.BindAsExecutor(emitEvents: true);
var writerBinding = writer.BindAsExecutor(emitEvents: true);
var editorBinding = editor.BindAsExecutor(emitEvents: true);

// 3. Build the workflow graph with WorkflowBuilder
var workflow = new WorkflowBuilder(plannerBinding)    // Start with planner
    .AddEdge(plannerBinding, researcherBinding)        // Planner → Researcher
    .AddEdge(researcherBinding, writerBinding)         // Researcher → Writer  
    .AddEdge(writerBinding, editorBinding)             // Writer → Editor
    .Build();

// 4. Create AgentEval adapter
var executorIds = new[] { "Planner", "Researcher", "Writer", "Editor" };
var adapter = MAFWorkflowAdapter.FromMAFWorkflow(
    workflow, "ContentPipeline", executorIds, "PromptChaining");
```

### Tool-Enabled Workflow Pattern

Agents can use tools (function calling) within workflows. Each agent's tools are tracked both individually and at the workflow level:

```csharp
// Create tools for the TripPlanner agent
static string GetInfoAbout([Description("City to get info about")] string city)
{
    return $"Information about {city}: Beautiful city with rich history...";
}

static string SearchFlights([Description("Origin city")] string from, 
                           [Description("Destination city")] string to)
{
    return $"Found flights from {from} to {to}: Flight AA123 at 10:30 AM...";
}

static string BookFlight([Description("Flight number")] string flightNumber)
{
    return $"Booked flight {flightNumber}. Confirmation: ABC123";
}

static string BookHotel([Description("City for hotel")] string city)
{
    return $"Booked hotel in {city}. Confirmation: HTL456";
}

// Create agents with tools configured
var tripPlanner = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "TripPlanner",
    Description = "Gathers city information and plans trip itinerary",
    ChatOptions = new ChatOptions
    {
        Instructions = "Use GetInfoAbout tool for EACH city mentioned. Create day-by-day itinerary.",
        Tools = [AIFunctionFactory.Create(GetInfoAbout)]
    }
});

var flightAgent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "FlightReservation", 
    Description = "Searches and books flights between cities",
    ChatOptions = new ChatOptions
    {
        Instructions = "Use SearchFlights first, then BookFlight for each journey leg.",
        Tools = [
            AIFunctionFactory.Create(SearchFlights),
            AIFunctionFactory.Create(BookFlight)
        ]
    }
});

var hotelAgent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "HotelReservation",
    Description = "Books hotels for each city in the trip", 
    ChatOptions = new ChatOptions
    {
        Instructions = "Use BookHotel for EACH city that needs accommodation.",
        Tools = [AIFunctionFactory.Create(BookHotel)]
    }
});

var presenter = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "Presenter",
    Description = "Creates final trip presentation",
    ChatOptions = new() { Instructions = "Summarize the complete trip with all bookings and confirmations." }
});

// Build tool-enabled workflow
var tripPlannerBinding = tripPlanner.BindAsExecutor(emitEvents: true);
var flightBinding = flightAgent.BindAsExecutor(emitEvents: true);
var hotelBinding = hotelAgent.BindAsExecutor(emitEvents: true);
var presenterBinding = presenter.BindAsExecutor(emitEvents: true);

var toolWorkflow = new WorkflowBuilder(tripPlannerBinding)
    .AddEdge(tripPlannerBinding, flightBinding)
    .AddEdge(flightBinding, hotelBinding)
    .AddEdge(hotelBinding, presenterBinding)
    .Build();

var toolAdapter = MAFWorkflowAdapter.FromMAFWorkflow(
    toolWorkflow, "TripPlanner", 
    ["TripPlanner", "FlightReservation", "HotelReservation", "Presenter"]);
```

## Workflow Evaluation Patterns

### Basic Sequential Workflow Evaluation

```csharp
[Fact]
public async Task ContentPipeline_Should_ExecuteInCorrectOrder()
{
    // Arrange
    var (workflow, executorIds) = CreateContentPipelineWorkflow();
    var adapter = MAFWorkflowAdapter.FromMAFWorkflow(
        workflow, "ContentPipeline", executorIds, "PromptChaining");
        
    var harness = new WorkflowEvaluationHarness(verbose: true);
    var testCase = new WorkflowTestCase
    {
        Name = "Content Generation Pipeline — AI Testing Article",
        Input = "Write a comprehensive article about AI agent evaluation testing",
        ExpectedSteps = ["Planner", "Researcher", "Writer", "Editor"]
    };
    
    var options = new WorkflowTestOptions
    {
        Timeout = TimeSpan.FromMinutes(5),
        Verbose = true
    };

    // Act - with timeout handling
    var workflowTask = harness.RunWorkflowTestAsync(adapter, testCase, options);
    var hardTimeout = Task.Delay(TimeSpan.FromMinutes(5));
    
    if (await Task.WhenAny(workflowTask, hardTimeout) == hardTimeout)
    {
        Assert.True(false, "Workflow exceeded hard timeout");
    }
    
    var result = await workflowTask;

    // Assert
    result.ExecutionResult!.Should()
        .HaveStepCount(4, because: "pipeline has 4 distinct stages")
        .HaveExecutedInOrder("Planner", "Researcher", "Writer", "Editor")
        .HaveCompletedWithin(TimeSpan.FromMinutes(3), because: "reasonable time for content generation")
        .HaveNoErrors(because: "clean execution is required")
        .HaveNonEmptyOutput()
        .Validate();
}
```

### Per-Executor Evaluation

Validate individual agent performance within the workflow:

```csharp
// Per-executor detailed assertions
result.ExecutionResult!.Should()
    .ForExecutor("Planner")
        .HaveNonEmptyOutput()
        .HaveCompletedWithin(TimeSpan.FromSeconds(60), because: "planning should be reasonably fast")
        .And()
    .ForExecutor("Researcher")
        .HaveNonEmptyOutput()
        .HaveDurationGreaterThan(TimeSpan.FromSeconds(5), because: "research takes time")
        .And()
    .ForExecutor("Writer")
        .HaveNonEmptyOutput()
        .HaveOutputLongerThan(100, because: "articles should be substantial")
        .And()
    .ForExecutor("Editor")
        .HaveNonEmptyOutput()
        .And()
    .Validate();
```

### Graph Structure Validation

Verify the workflow graph was correctly extracted and executed:

```csharp
// Graph structure assertions
result.ExecutionResult!.Should()
    .HaveGraphStructure()
    .HaveNodes("Planner", "Researcher", "Writer", "Editor")
    .HaveEntryPoint("Planner", because: "planning is the starting point")
    .HaveTraversedEdge("Researcher", "Writer")
    .HaveUsedEdgeType(EdgeType.Sequential)
    .HaveExecutionPath("Planner", "Researcher", "Writer", "Editor")
    .Validate();
```

### Tool Usage Evaluation

For workflows with tool-enabled agents, validate tool call patterns:

```csharp
[Fact]
public async Task TripPlannerWorkflow_Should_UseToolsCorrectly()
{
    // Arrange - tool-enabled workflow
    var (workflow, executorIds) = CreateTripPlannerWorkflow();
    var adapter = MAFWorkflowAdapter.FromMAFWorkflow(
        workflow, "TripPlanner", executorIds);
        
    var result = await harness.RunWorkflowTestAsync(adapter, testCase);

    // Assert tool usage at workflow level
    if (result.ExecutionResult!.ToolUsage != null)
    {
        result.ExecutionResult.Should()
            .HaveCalledTool("GetInfoAbout", because: "TripPlanner must research cities")
                .WithoutError()
            .And()
            .HaveCalledTool("SearchFlights")
                .BeforeTool("BookFlight", because: "can't book without search results")
                .WithoutError() 
            .And()
            .HaveCalledTool("BookFlight")
                .WithoutError()
            .And()
            .HaveCalledTool("BookHotel", because: "must book hotels")
                .WithoutError()
            .And()
            .HaveNoToolErrors(because: "all tools must succeed for quality output")
            .HaveAtLeastTotalToolCalls(4, because: "workflow uses at least 4 tool calls")
            .Validate();
    }
}
```

## Workflow Assertions API

### Basic Workflow Structure

```csharp
result.ExecutionResult!.Should()
    .HaveStepCount(4)                                    // Exact number of steps
    .HaveAtLeastSteps(3)                                // Minimum steps
    .HaveExecutedInOrder("A", "B", "C")                 // Sequential execution
    .HaveCompletedWithin(TimeSpan.FromMinutes(5))       // Time constraint
    .HaveNoErrors();                                     // Error-free execution
```

### Individual Executor Validation

```csharp
result.ExecutionResult!.Should()
    .ForExecutor("Planner")
        .HaveNonEmptyOutput()                           // Has output
        .HaveOutputContaining("plan")                   // Output content check
        .HaveOutputLongerThan(50)                       // Minimum output length
        .HaveCompletedWithin(TimeSpan.FromSeconds(30))  // Individual timing
        .HaveInputTokensLessThan(1000)                  // Resource usage
        .HaveEstimatedCostUnder(0.05m)                  // Cost constraint
        .And()
    .ForExecutor("Writer")
        .HaveNonEmptyOutput()
        .HaveToolCalls()                                // Used tools (if expected)
        .And();
```

### Graph Structure Assertions

```csharp
result.ExecutionResult!.Should()
    .HaveGraphStructure()                               // Graph was extracted
    .HaveNodes("A", "B", "C", "D")                      // Expected nodes
    .HaveEntryPoint("A")                               // Entry node
    .HaveExitPoint("D")                                // Exit node  
    .HaveTraversedEdge("A", "B")                       // Specific edge used
    .NotHaveTraversedEdge("A", "C")                    // Edge not used 
    .HaveExecutionPath("A", "B", "C", "D");            // Complete path
```

### Tool Usage Validation

```csharp
// At workflow level (aggregated across all agents)
result.ExecutionResult!.Should()
    .HaveCalledTool("SearchFlights")                    // Tool was called
        .AtLeast(1.Times())                            // Call frequency
        .WithoutError()                                // No tool errors
        .WithArgument("from", "Seattle")               // Specific argument
        .BeforeTool("BookFlight")                       // Call ordering
        .And()
    .HaveToolCallPattern("Search*", "Book*")           // Pattern matching
    .HaveNoToolErrors()                                // Global tool success
    .HaveAtLeastTotalToolCalls(3);                     // Minimum tool usage
```

### Performance and Cost Validation

```csharp
result.ExecutionResult!.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromMinutes(5))   // Overall timing
    .HaveEstimatedCostUnder(0.25m)                     // Overall cost 
    .HaveTokenUsageUnder(5000)                         // Token limits
    .HaveNoTimeouts();                                  // No timeout errors

// Individual step performance
result.ExecutionResult!.Should()
    .ForExecutor("SlowStep")
        .HaveCompletedWithin(TimeSpan.FromSeconds(30))
        .HaveEstimatedCostUnder(0.10m);
```

### Complex Assertion Chaining

Combine multiple assertion types for comprehensive validation:

```csharp
result.ExecutionResult!.Should()
    // Basic structure
    .HaveStepCount(4)
    .HaveExecutedInOrder("Planner", "Researcher", "Writer", "Editor")
    .HaveCompletedWithin(TimeSpan.FromMinutes(3))
    .HaveNoErrors()
    
    // Per-executor validation
    .ForExecutor("Planner")
        .HaveNonEmptyOutput()
        .HaveCompletedWithin(TimeSpan.FromSeconds(60))
        .And()
    .ForExecutor("Writer")
        .HaveOutputLongerThan(200)
        .And()
        
    // Graph validation
    .HaveGraphStructure()
    .HaveEntryPoint("Planner")
    .HaveExecutionPath("Planner", "Researcher", "Writer", "Editor")
    
    // Tool validation (if applicable)
    .HaveNoToolErrors()
    
    // Performance validation
    .Performance!.Should()
        .HaveEstimatedCostUnder(0.20m)
        .And()
    
    // Final validation
    .Validate();
```

## Visualization and Export

### Mermaid Diagram Generation

Generate flowchart diagrams for workflow visualization:

```csharp
using AgentEval.Workflows.Serialization;

// Execute workflow
var result = await harness.RunWorkflowTestAsync(adapter, testCase);

// Generate Mermaid diagram
var mermaid = WorkflowSerializer.ToMermaid(result.ExecutionResult!);
Console.WriteLine(mermaid);

// Output:
// ```mermaid
// graph TD
//     Planner([Planner])
//     Researcher[Researcher]
//     Writer[Writer]
//     Editor[[Editor]]
//     
//     Planner --> Researcher
//     Researcher --> Writer
//     Writer --> Editor
//     
//     classDef executed fill:#90EE90,stroke:#228B22
//     class Planner,Researcher,Writer,Editor executed
// ```

// Save to file for external viewing
var mermaidPath = Path.GetTempFileName() + ".mmd";
File.WriteAllText(mermaidPath, mermaid);
Console.WriteLine($"Mermaid diagram saved to: {mermaidPath}");
Console.WriteLine($"View at: https://mermaid.live");
```

### Timeline JSON Export

Export detailed execution timeline for analysis tools:

```csharp
// Generate timeline JSON with full execution details
var timeline = WorkflowSerializer.ToTimelineJson(result.ExecutionResult!);
Console.WriteLine($"Timeline JSON: {timeline.Length} characters");

// Timeline includes:
// - Step-by-step execution with timestamps
// - Tool call details and durations
// - Token usage and cost estimates
// - Error information (if any)
// - Performance metrics

// Export for external analysis
var timelineData = JsonSerializer.Deserialize<WorkflowTimelineData>(timeline);
foreach (var step in timelineData.Steps)
{
    Console.WriteLine($"[{step.StartOffset}] {step.ExecutorId}: {step.Duration}ms");
    if (step.ToolCalls?.Count > 0)
    {
        foreach (var tool in step.ToolCalls) 
        {
            Console.WriteLine($"  🔧 {tool.Name} ({tool.Duration}ms)");
        }
    }
}
```

### ASCII Timeline Visualization

For console/CI environments, generate ASCII timeline diagrams:

```csharp
// Generate ASCII timeline diagram
var asciiDiagram = result.ExecutionResult!.Timeline.ToAsciiDiagram(80);
Console.WriteLine(asciiDiagram);

// Output:
// Timeline (66.8s total):
// ├─ Planner     ████████████                          (20.1s)
// ├─ Researcher  ──────────────██████                  (14.1s)  
// ├─ Writer      ─────────────────────████████████     (17.5s)
// └─ Editor      ──────────────────────────────────    (0.0s)
//    0s    10s   20s   30s   40s   50s   60s   70s

// Include tool usage if present
if (result.ExecutionResult.ToolUsage != null)
{
    Console.WriteLine($"Tool calls: {result.ExecutionResult.ToolUsage.Count} total");
    Console.WriteLine($"Tool time: {result.ExecutionResult.Timeline.TotalToolTime.TotalMilliseconds:F0}ms");
    Console.WriteLine($"Tool efficiency: {result.ExecutionResult.Timeline.ToolTimePercentage:F1}%");
}
```

## Best Practices

### 1. Use Clear Agent Names and Instructions

```csharp
// Good - descriptive names and focused instructions
var planner = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "ContentPlanner",
    Description = "Plans article structure and research needs",
    ChatOptions = new ChatOptions
    {
        Instructions = """
            Create a structured content plan with:
            1. Clear outline with main sections and sub-topics
            2. Specific research requirements for each section
            3. Target audience and appropriate tone
            4. Estimated word count and key messaging
            """
    }
});

// Avoid - generic names and vague instructions
var agent1 = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "Agent1",
    ChatOptions = new() { Instructions = "Do planning stuff" }
});
```

### 2. Implement Timeout Handling

Always use the hard timeout pattern for production workflows:

```csharp
// Production-ready timeout handling
var workflowTask = harness.RunWorkflowTestAsync(adapter, testCase, options);
var hardTimeout = Task.Delay(options.Timeout ?? TimeSpan.FromMinutes(5));

if (await Task.WhenAny(workflowTask, hardTimeout) == hardTimeout)
{
    _logger.LogWarning("Workflow {WorkflowName} exceeded timeout {Timeout}", 
                      adapter.Name, options.Timeout);
    
    // Handle gracefully - don't fail tests in CI
    return new WorkflowTestResult
    {
        TestName = testCase.Name,
        Passed = false,
        ErrorMessage = "Workflow exceeded hard timeout",
        Duration = options.Timeout ?? TimeSpan.FromMinutes(5)
    };
}

var result = await workflowTask;
```

### 3. Layer AssertionsComprehensively

Start with basic structure, then add detailed validations:

```csharp
// 1. Basic structure first
result.ExecutionResult!.Should()
    .HaveStepCount(expectedSteps.Length)
    .HaveExecutedInOrder(expectedSteps)
    .HaveNoErrors();
    
// 2. Add performance constraints  
result.ExecutionResult!.Should()
    .HaveCompletedWithin(TimeSpan.FromMinutes(3));
    
// 3. Add detailed per-executor validation
result.ExecutionResult!.Should()
    .ForExecutor("Planner")
        .HaveNonEmptyOutput()
        .HaveCompletedWithin(TimeSpan.FromSeconds(60))
        .And();
        
// 4. Add tool validation (if applicable)
if (result.ExecutionResult.ToolUsage != null)
{
    result.ExecutionResult.Should().HaveNoToolErrors();
}

// 5. Add cost/resource validation
result.ExecutionResult!.Performance!.Should()
    .HaveEstimatedCostUnder(maxExpectedCost);
```

### 4. Export Debug Information on Failures

```csharp
[Fact]
public async Task ComplexWorkflow_Should_CompleteSuccessfully()
{
    var result = await harness.RunWorkflowTestAsync(adapter, testCase);
    
    // Export debug info on failure
    if (!result.Passed || result.ExecutionResult?.HasErrors == true)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        
        // Export Mermaid diagram
        var mermaid = WorkflowSerializer.ToMermaid(result.ExecutionResult!);
        var mermaidPath = $"failed-workflow-{timestamp}.mmd";
        File.WriteAllText(mermaidPath, mermaid);
        
        // Export timeline JSON
        var timeline = WorkflowSerializer.ToTimelineJson(result.ExecutionResult!);
        var timelinePath = $"failed-workflow-{timestamp}.json";
        File.WriteAllText(timelinePath, timeline);
        
        _testOutput.WriteLine($"Debug exports: {mermaidPath}, {timelinePath}");
    }
    
    result.Should().BeSuccessful();
}
```

### 5. Test Individual Steps in Isolation

Before testing complete workflows, validate individual agents:

```csharp
[Fact] 
public async Task PlannerAgent_Should_CreateValidPlan()
{
    // Test individual agent before workflow integration
    var planner = CreatePlannerAgent();
    var input = "Write an article about AI agent testing";
    
    var response = await planner.InvokeAsync(input);
    
    Assert.NotNull(response);
    Assert.Contains("outline", response.Result, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("research", response.Result, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public async Task FullWorkflow_Should_IntegrateAgentsCorrectly() 
{
    // Test complete workflow after individual agents work
    var result = await harness.RunWorkflowTestAsync(adapter, testCase);
    result.Should().BeSuccessful();
}
```

## Integration with CI/CD

### JUnit XML Export

Export workflow evaluation results for CI/CD pipelines:

```csharp
using AgentEval.Workflows.Serialization;

// Run multiple workflow tests
var testResults = new List<WorkflowTestResult>();
foreach (var testCase in testCases)
{
    var result = await harness.RunWorkflowTestAsync(adapter, testCase);
    testResults.Add(result);
}

// Export to JUnit XML for CI/CD
var report = new EvaluationReport
{
    Name = "Workflow Evaluation Tests",
    Results = testResults.Select(r => new TestResult
    {
        Name = r.TestName,
        Passed = r.Passed,
        Duration = r.Duration,
        ErrorMessage = r.ErrorMessage
    }).ToList()
};

using var outputStream = File.Create("workflow-test-results.xml");
await new JUnitXmlExporter().ExportAsync(report, outputStream);
```

### GitHub Actions Integration

Example workflow for GitHub Actions:

```yaml
name: Workflow Evaluation Tests

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

jobs:
  evaluate-workflows:
    runs-on: ubuntu-latest
    
    steps:
    - uses: actions/checkout@v4
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '8.0'
    
    - name: Run Workflow Evaluations
      env:
        AZURE_OPENAI_ENDPOINT: ${{ secrets.AZURE_OPENAI_ENDPOINT }}
        AZURE_OPENAI_API_KEY: ${{ secrets.AZURE_OPENAI_API_KEY }}
        AZURE_OPENAI_DEPLOYMENT: ${{ vars.AZURE_OPENAI_DEPLOYMENT }}
      run: |
        dotnet test tests/WorkflowEvaluationTests/ \
          --logger "junit;LogFilePath=workflow-results.xml" \
          --logger "console;verbosity=detailed"
    
    - name: Upload Test Results
      uses: dorny/test-reporter@v1.7.0
      if: always()
      with:
        name: Workflow Evaluation Results
        path: workflow-results.xml
        reporter: java-junit
        
    - name: Upload Workflow Diagrams
      uses: actions/upload-artifact@v4
      if: failure()
      with:
        name: workflow-debug-diagrams
        path: "*.mmd"
```

### Performance Monitoring

Track workflow performance over time:

```csharp
// Collect performance metrics
var metrics = new WorkflowMetrics
{
    WorkflowName = adapter.Name,
    ExecutionTime = result.Duration,
    StepCount = result.ExecutionResult?.Steps.Count ?? 0,
    TotalCost = result.ExecutionResult?.Performance?.EstimatedCost ?? 0,
    SuccessRate = result.Passed ? 1.0 : 0.0,
    Timestamp = DateTime.UtcNow
};

// Export to monitoring system
await MonitoringClient.RecordMetricsAsync(metrics);

// Set CI performance gates
if (metrics.ExecutionTime > TimeSpan.FromMinutes(5))
{
    throw new PerformanceException($"Workflow {adapter.Name} exceeded 5 minute SLA");
}

if (metrics.TotalCost > 0.50m)
{
    throw new CostException($"Workflow {adapter.Name} exceeded $0.50 cost limit");
}
```

## See Also

**Core Documentation:**
- [Architecture Overview](architecture.md) - System architecture including workflow components
- [Assertions](assertions.md) - Complete assertion API with workflow examples 
- [Getting Started](getting-started.md) - Includes workflow evaluation quickstart

**Related Evaluation Types:**
- [Conversations](conversations.md) - Multi-turn conversation evaluation
- [Tracing](tracing.md) - Record and replay for deterministic workflow testing
- [Stochastic Evaluation](stochastic-evaluation.md) - Statistical workflow reliability
- [Model Comparison](model-comparison.md) - Compare models in workflow contexts

**Advanced Topics:**
- [Red Team](redteam.md) - Security evaluation for workflow endpoints
- [Performance & Cost](benchmarks.md) - Workflow performance optimization
- [CI/CD Integration](ci-cd-integration.md) - Automated workflow evaluation pipelines

**Live Examples:**
- [Sample 09: Real MAF Workflow Evaluation](https://github.com/joslat/AgentEval/blob/main/samples/AgentEval.Samples/Sample09_WorkflowEvaluationReal.cs) - Sequential content pipeline (Planner → Researcher → Writer → Editor)
- [Sample 10: Workflow with Tools](https://github.com/joslat/AgentEval/blob/main/samples/AgentEval.Samples/Sample10_WorkflowWithTools.cs) - Tool-enabled trip planning workflow with function calling

**Microsoft Agent Framework:**
- [MAF Official Documentation](https://github.com/microsoft/agent-framework) - Microsoft Agent Framework docs
- [MAF Workflow Samples](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/GettingStarted/Workflows) - Official MAF workflow examples
