# Walkthrough: Evaluating Your First AI Agent

This walkthrough guides you through evaluating an AI agent with AgentEval, from setup to assertions.

---

## What You'll Learn

1. Setting up an evaluation harness
2. Wrapping an agent for evaluation
3. Running an evaluation and capturing results
4. Asserting on tool usage
5. Asserting on performance
6. Exporting results for CI/CD

---

## Prerequisites

- .NET 8.0+ SDK
- An AI agent (we'll use a mock for this tutorial)
- AgentEval installed (`dotnet add package AgentEval --prerelease`)

---

## Step 1: Create an evaluation harness

The evaluation harness runs your agent and captures all the data needed for assertions.

```csharp
using AgentEval.MAF;

// Create an evaluation harness with optional verbose logging
var harness = new MAFEvaluationHarness(verbose: true);
```

---

## Step 2: Wrap Your Agent

AgentEval uses adapters to wrap different agent types. For Microsoft Agent Framework agents:

```csharp
using AgentEval.MAF;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// First, create your MAF agent
var azureClient = new AzureOpenAIClient(
    new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
    new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

var chatClient = azureClient
    .GetChatClient("gpt-4o")
    .AsIChatClient();

var myAgent = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "TravelPlannerAgent",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are a travel planning assistant.",
            Tools = [
                AIFunctionFactory.Create(SearchFlights),
                AIFunctionFactory.Create(SearchHotels),
                AIFunctionFactory.Create(GetWeather)
            ]
        }
    });

// Then wrap it for evaluation
var adapter = new MAFAgentAdapter(myAgent);
```

For any `IChatClient`:

```csharp
using AgentEval.Adapters;

// Wrap an IChatClient
var adapter = new ChatClientAgentAdapter(chatClient, "MyAgent");
```

---

## Step 3: Define an Evaluation Case

Evaluation cases describe what to evaluate and how to judge the results:

```csharp
using AgentEval.Models;

var testCase = new TestCase
{
    Name = "Travel Planning Evaluation",
    Input = "Plan a trip to Paris for next weekend",
    
    // Optional: Expected tools the agent should use
    ExpectedTools = new[] { "SearchFlights", "SearchHotels", "GetWeather" },
    
    // Optional: Criteria for AI-powered evaluation
    EvaluationCriteria = new[] 
    { 
        "Should include flight options",
        "Should include hotel recommendations",
        "Should consider weather"
    },
    
    // Minimum score to pass (0-100)
    PassingScore = 70
};
```

---

## Step 4: Run the Evaluation

Execute the evaluation and capture results:

```csharp
using AgentEval.Core;

// Run the evaluation - tool tracking and performance metrics are captured automatically
var result = await harness.RunEvaluationAsync(adapter, testCase);

// Check if the evaluation passed
Console.WriteLine($"Passed: {result.Passed}");
Console.WriteLine($"Score: {result.Score}");
Console.WriteLine($"Output: {result.ActualOutput}");
```

---

## Step 5: Assert on Tool Usage

Use fluent assertions to verify the agent used tools correctly:

```csharp
using AgentEval.Assertions;

// Assert specific tools were called
result.ToolUsage!
    .Should()
    .HaveCalledTool("SearchFlights")
    .HaveCalledTool("SearchHotels")
    .HaveCalledTool("GetWeather");

// Assert tool ordering
result.ToolUsage!
    .Should()
    .HaveCalledTool("GetWeather")
        .BeforeTool("SearchFlights");  // Weather check before booking

// Assert tool arguments
result.ToolUsage!
    .Should()
    .HaveCalledTool("SearchFlights")
        .WithArgument("destination", "Paris");

// Assert no errors occurred
result.ToolUsage!
    .Should()
    .HaveNoErrors();

// Assert call count limits
result.ToolUsage!
    .Should()
    .HaveTotalCallsLessThan(10);  // Efficiency check
```

---

## Step 6: Assert on Performance

Verify the agent meets performance requirements:

```csharp
using AgentEval.Assertions;

result.Performance!
    .Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(30))
    .HaveTimeToFirstTokenUnder(TimeSpan.FromSeconds(2))
    .HaveTokenCountUnder(4000)
    .HaveEstimatedCostUnder(0.10m);  // Max $0.10 per request
```

---

## Step 7: Assert on Response Content

Verify the response contains expected information:

```csharp
using AgentEval.Assertions;

result.Response
    .Should()
    .Contain("Paris")
    .ContainAny("flight", "airline")
    .ContainAny("hotel", "accommodation")
    .NotContain("error")
    .HaveLengthBetween(100, 5000);
```

---

## Step 8: Export Results for CI/CD

Export results in formats your CI/CD system understands:

```csharp
using AgentEval.Exporters;

// JUnit XML for GitHub Actions, Azure DevOps, Jenkins
var junitExporter = new JUnitExporter();
await junitExporter.ExportAsync(new[] { result }, "results.xml");

// Markdown for PR comments
var markdownExporter = new MarkdownExporter();
await markdownExporter.ExportAsync(new[] { result }, "results.md");

// JSON for custom dashboards
var jsonExporter = new JsonExporter();
await jsonExporter.ExportAsync(new[] { result }, "results.json");
```

---

## Complete Example

Here's the full test in one file:

```csharp
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Core;
using AgentEval.Assertions;
using AgentEval.Exporters;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.ComponentModel;

// ═══════════════════════════════════════════════════════════════
// 1. Create your MAF agent with tools
// ═══════════════════════════════════════════════════════════════
var azureClient = new AzureOpenAIClient(
    new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
    new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

var chatClient = azureClient
    .GetChatClient("gpt-4o")
    .AsIChatClient();

var myAgent = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "TravelPlannerAgent",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are a travel planning assistant. Use tools to search for flights, hotels, and weather.",
            Tools = [
                AIFunctionFactory.Create(SearchFlights),
                AIFunctionFactory.Create(SearchHotels),
                AIFunctionFactory.Create(GetWeather)
            ]
        }
    });

// ═══════════════════════════════════════════════════════════════
// 2. Setup evaluation harness and adapter
// ═══════════════════════════════════════════════════════════════
var harness = new MAFEvaluationHarness(verbose: true);
var adapter = new MAFAgentAdapter(myAgent);

// ═══════════════════════════════════════════════════════════════
// 3. Define evaluation case
// ═══════════════════════════════════════════════════════════════
var testCase = new TestCase
{
    Name = "Travel Planning Evaluation",
    Input = "Plan a trip to Paris for next weekend",
    ExpectedTools = new[] { "SearchFlights", "SearchHotels", "GetWeather" },
    PassingScore = 70
};

// ═══════════════════════════════════════════════════════════════
// 4. Run evaluation
// ═══════════════════════════════════════════════════════════════
var result = await harness.RunEvaluationAsync(adapter, testCase);

// ═══════════════════════════════════════════════════════════════
// 5. Assert
// ═══════════════════════════════════════════════════════════════
result.ToolUsage!
    .Should()
    .HaveCalledTool("SearchFlights")
    .HaveCalledTool("SearchHotels")
    .HaveNoErrors();

result.Performance!
    .Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(30))
    .HaveEstimatedCostUnder(0.10m);

// ═══════════════════════════════════════════════════════════════
// 6. Export results
// ═══════════════════════════════════════════════════════════════
var exporter = new JUnitExporter();
await exporter.ExportAsync(new[] { result }, "results.xml");

Console.WriteLine($"✅ Evaluation {(result.Passed ? "PASSED" : "FAILED")}");
Console.WriteLine($"   Output: {result.ActualOutput}");

// ═══════════════════════════════════════════════════════════════
// Tool definitions
// ═══════════════════════════════════════════════════════════════
[Description("Search for available flights")]
static string SearchFlights(
    [Description("Destination city")] string destination,
    [Description("Departure date")] string date)
{
    return $"Found 3 flights to {destination} on {date}: AA123, UA456, DL789";
}

[Description("Search for hotels")]
static string SearchHotels(
    [Description("City name")] string city,
    [Description("Check-in date")] string checkIn)
{
    return $"Found hotels in {city}: Hilton ($200/night), Marriott ($180/night)";
}

[Description("Get weather forecast")]
static string GetWeather(
    [Description("City name")] string city)
{
    return $"Weather in {city}: Sunny, 72°F";
}
```

---

## Using with xUnit/NUnit/MSTest

AgentEval integrates naturally with evaluation frameworks:

```csharp
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Assertions;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

public class TravelAgentTests
{
    private readonly AIAgent _agent;

    public TravelAgentTests()
    {
        // Setup agent once per test class
        var azureClient = new AzureOpenAIClient(
            new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
            new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

        var chatClient = azureClient
            .GetChatClient("gpt-4o")
            .AsIChatClient();

        _agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "TravelPlannerAgent",
                ChatOptions = new ChatOptions
                {
                    Instructions = "You are a travel planning assistant.",
                    Tools = [AIFunctionFactory.Create(SearchFlights)]
                }
            });
    }

    [Fact]
    public async Task Agent_ShouldPlanTrip_WithCorrectTools()
    {
        // Arrange
        var harness = new MAFEvaluationHarness();
        var adapter = new MAFAgentAdapter(_agent);
        var testCase = new TestCase
        {
            Name = "Travel Planning Evaluation",
            Input = "Plan a trip to Paris",
            ExpectedTools = new[] { "SearchFlights" }
        };

        // Act
        var result = await harness.RunEvaluationAsync(adapter, testCase);

        // Assert
        result.ToolUsage!
            .Should()
            .HaveCalledTool("SearchFlights")
            .HaveNoErrors();
        
        Assert.True(result.Passed);
    }

    [System.ComponentModel.Description("Search for flights")]
    private static string SearchFlights(
        [System.ComponentModel.Description("Destination")] string destination)
    {
        return $"Found flights to {destination}";
    }
}
```

---

## All Samples Guide

AgentEval includes comprehensive examples covering every evaluation scenario. Here's what each sample demonstrates:

### Foundation Samples (1-4): Getting Started
**Mock Mode Available (No Azure OpenAI Required)**

- **Sample01**: Hello World - Basic agent evaluation setup
- **Sample02**: Tool Usage Assertions - Validate tool calls with fluent syntax
- **Sample03**: Performance Assertions - Check latency, cost, and token usage
- **Sample04**: RAG Metrics - Evaluate retrieval-augmented generation quality

### Core Evaluation Samples (5-12): Essential Patterns

- **Sample05**: RAG Quality Metrics - Faithfulness, relevance, context precision
- **Sample06**: Behavioral Policies - Safety guards (`NeverCallTool`, `MustConfirmBefore`)  
- **Sample07**: Snapshot Testing - Detect regressions against golden responses
- **Sample08**: Multi-Turn Conversations - Conversation flow evaluation
- **Sample09**: **Sequential Workflows** - Multi-agent pipeline evaluation
- **Sample10**: **Tool-Enabled Workflows** - Complex multi-agent tool chains
- **Sample11**: LLM-as-Judge - Using LLMs for quality assessment
- **Sample12**: Embedding Metrics - Semantic similarity evaluation

### Advanced Evaluation Samples (13-18): Production Patterns

- **Sample13**: Trace Record & Replay - API-free evaluation for CI/CD
- **Sample14**: stochastic evaluation - Handle non-deterministic behavior
- **Sample15**: Model Comparison - Compare models side-by-side
- **Sample16**: Statistical Analysis - Advanced statistical evaluation
- **Sample17**: Custom Metrics - Building domain-specific evaluators
- **Sample18**: Calibrated Judges - Multi-model consensus evaluation

### Security & Compliance Samples (19-24): Enterprise Features

- **Sample19**: Streaming vs Async - Compare streaming and non-streaming performance
- **Sample20**: Quick Red Team Scan - One-line security assessment
- **Sample21**: Advanced Red Team Pipeline - Comprehensive security evaluation
- **Sample23**: Responsible AI - Toxicity, bias, misinformation metrics
- **Sample24**: Benchmark System - Performance, agentic, standard, and cost benchmarks

### Running Samples

```bash
# Clone and run
git clone https://github.com/joslat/AgentEval
cd AgentEval/samples/AgentEval.Samples

# Mock mode (no API keys required) - Samples 1-4
dotnet run -- 1    # Hello World
dotnet run -- 2    # Tool assertions  
dotnet run -- 3    # Performance assertions
dotnet run -- 4    # RAG metrics

# Azure OpenAI required - Samples 5-24  
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"
export AZURE_OPENAI_API_KEY="your-api-key"

dotnet run -- 9    # Sequential Workflows
dotnet run -- 10   # Tool-Enabled Workflows  
dotnet run -- 20   # Quick Red Team Scan
dotnet run -- 21   # Advanced Red Team Pipeline
```

---

## Workflow Evaluation Deep Dive

**Samples 09 & 10** demonstrate multi-agent workflow evaluation—one of AgentEval's most powerful features.

### Sequential Pipeline Workflow (Sample 09)

Evaluate a content creation pipeline with multiple agents:

```csharp
// 1. Define workflow agents
var plannerAgent = CreateAgent("ContentPlanner", "Create content outlines");
var researcherAgent = CreateAgent("Researcher", "Research topics and gather facts");  
var writerAgent = CreateAgent("Writer", "Write engaging content");
var editorAgent = CreateAgent("Editor", "Edit and polish content");

// 2. Build MAF workflow
var workflow = new WorkflowBuilder()
    .BindAsExecutor("Planner", plannerAgent, emitEvents: true)
    .BindAsExecutor("Researcher", researcherAgent, emitEvents: true)
    .BindAsExecutor("Writer", writerAgent, emitEvents: true)
    .BindAsExecutor("Editor", editorAgent, emitEvents: true)
    .Build();

// 3. Create workflow adapter for evaluation
var workflowAdapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "ContentPipeline");

// 4. Define test case
var testCase = new WorkflowTestCase
{
    Name = "Content Creation Pipeline",
    Input = "Create an article about sustainable technology",
    Agents = new[] { "Planner", "Researcher", "Writer", "Editor" },
    TimeoutPerAgent = TimeSpan.FromMinutes(2),
    WorkflowTimeout = TimeSpan.FromMinutes(10)
};

// 5. Run workflow evaluation  
var harness = new WorkflowEvaluationHarness();
var result = await harness.RunWorkflowTestAsync(workflowAdapter, testCase);

// 6. Assert on workflow structure and execution
result.ExecutionResult!.Should()
    .HaveStepCount(4, because: "pipeline has 4 agents")
    .HaveExecutedInOrder("Planner", "Researcher", "Writer", "Editor")
    .HaveCompletedWithin(TimeSpan.FromMinutes(10))
    .HaveNoErrors();

// 7. Assert on individual agent performance
result.ExecutionResult!
    .ForExecutor("Writer")
        .HaveOutputLongerThan(200, because: "articles should be substantial")
        .HaveEstimatedCostUnder(0.15m)
        .And()
    .ForExecutor("Editor")
        .HaveOutputNotContaining("DRAFT")
        .And();

// 8. Validate workflow graph structure
result.ExecutionResult!
    .HaveGraphStructure()
        .HaveEntryPoint("Planner")
        .HaveExecutionPath("Planner", "Researcher", "Writer", "Editor")  
        .HaveTraversedEdge("Planner", "Researcher")
        .HaveTraversedEdge("Writer", "Editor");
```

### Tool-Enabled Workflow (Sample 10)

Evaluate workflows where agents use tools:

```csharp
// 1. Create agents with tools
var tripPlannerAgent = new ChatClientAgent(chatClient, new()
{
    Name = "TripPlanner", 
    Tools = [AIFunctionFactory.Create(GetInfoAbout)]
});

var flightReservationAgent = new ChatClientAgent(chatClient, new()
{
    Name = "FlightReservation",
    Tools = [AIFunctionFactory.Create(SearchFlights), AIFunctionFactory.Create(BookFlight)]
});

// 2. Build workflow with tool-enabled agents
var workflow = new WorkflowBuilder()
    .BindAsExecutor("TripPlanner", tripPlannerAgent, emitEvents: true)
    .BindAsExecutor("FlightReservation", flightReservationAgent, emitEvents: true)
    .Build();

var workflowAdapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "TravelBooking");

// 3. Run evaluation
var result = await harness.RunWorkflowTestAsync(workflowAdapter, testCase);

// 4. Assert on tool usage across workflow  
result.ExecutionResult!.Should()
    .HaveCalledTool("GetInfoAbout", because: "TripPlanner must research cities")
        .AtLeast(2.Times())
        .WithoutError()
        .InExecutor("TripPlanner")
        .And()
    .HaveCalledTool("SearchFlights")
        .BeforeTool("BookFlight", because: "can't book without search")
        .InExecutor("FlightReservation")
        .WithArgument("from", "Seattle")
        .And()
    .HaveNoToolErrors();

// 5. Export workflow visualization
await result.ExportWorkflowVisualizationAsync("workflow-execution.mmd");
```

### Workflow Evaluation Benefits

- **Structure Validation**: Verify workflow topology and execution order
- **Per-Agent Analysis**: Individual agent performance within workflow context  
- **Tool Chain Validation**: Tool usage patterns across multiple agents
- **Performance Monitoring**: Total workflow cost/timing with per-agent breakdown
- **Error Propagation**: Track how errors flow through workflow pipeline
- **Visualization Export**: Mermaid diagrams of workflow execution

See [Workflows Documentation](workflows.md) for complete workflow evaluation guide.

---

## Next Steps

- [Architecture](architecture.md) - Understand the framework design
- [CLI Reference](cli.md) - Run evaluations from command line
- [Workflows](workflows.md) - Complete workflow evaluation guide
- [Benchmarks](benchmarks.md) - Performance evaluation at scale
- [Conversations](conversations.md) - Multi-turn evaluation
- [Snapshots](snapshots.md) - Regression evaluation
