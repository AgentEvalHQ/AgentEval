# Walkthrough: Testing Your First AI Agent

This walkthrough guides you through testing an AI agent with AgentEval, from setup to assertions.

---

## What You'll Learn

1. Setting up a evaluation harness
2. Wrapping an agent for testing
3. Running a test and capturing results
4. Asserting on tool usage
5. Asserting on performance
6. Exporting results for CI/CD

---

## Prerequisites

- .NET 8.0+ SDK
- An AI agent (we'll use a mock for this tutorial)
- AgentEval installed (`dotnet add package AgentEval --prerelease`)

---

## Step 1: Create a evaluation harness

The evaluation harness runs your agent and captures all the data needed for assertions.

```csharp
using AgentEval.MAF;

// Create a evaluation harness with optional verbose logging
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
        Instructions = "You are a travel planning assistant.",
        Tools = [
            AIFunctionFactory.Create(SearchFlights),
            AIFunctionFactory.Create(SearchHotels),
            AIFunctionFactory.Create(GetWeather)
        ]
    });

// Then wrap it for testing
var adapter = new MAFAgentAdapter(myAgent);
```

For any `IChatClient`:

```csharp
using AgentEval.Adapters;

// Wrap an IChatClient
var adapter = new ChatClientAgentAdapter(chatClient, "MyAgent");
```

---

## Step 3: Define a Test Case

Test cases describe what to test and how to evaluate the results:

```csharp
using AgentEval.Models;

var testCase = new TestCase
{
    Name = "Travel Planning Test",
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

## Step 4: Run the Test

Execute the test and capture results:

```csharp
using AgentEval.Core;

// Run the test - tool tracking and performance metrics are captured automatically
var result = await harness.RunEvaluationAsync(adapter, testCase);

// Check if the test passed
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
        Instructions = "You are a travel planning assistant. Use tools to search for flights, hotels, and weather.",
        Tools = [
            AIFunctionFactory.Create(SearchFlights),
            AIFunctionFactory.Create(SearchHotels),
            AIFunctionFactory.Create(GetWeather)
        ]
    });

// ═══════════════════════════════════════════════════════════════
// 2. Setup evaluation harness and adapter
// ═══════════════════════════════════════════════════════════════
var harness = new MAFEvaluationHarness(verbose: true);
var adapter = new MAFAgentAdapter(myAgent);

// ═══════════════════════════════════════════════════════════════
// 3. Define test case
// ═══════════════════════════════════════════════════════════════
var testCase = new TestCase
{
    Name = "Travel Planning Test",
    Input = "Plan a trip to Paris for next weekend",
    ExpectedTools = new[] { "SearchFlights", "SearchHotels", "GetWeather" },
    PassingScore = 70
};

// ═══════════════════════════════════════════════════════════════
// 4. Run test
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

Console.WriteLine($"✅ Test {(result.Passed ? "PASSED" : "FAILED")}");
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

AgentEval integrates naturally with test frameworks:

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
                Instructions = "You are a travel planning assistant.",
                Tools = [AIFunctionFactory.Create(SearchFlights)]
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
            Name = "Travel Planning",
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

## Next Steps

- [Architecture](architecture.md) - Understand the framework design
- [CLI Reference](cli.md) - Run tests from command line
- [Benchmarks](benchmarks.md) - Performance testing at scale
- [Conversations](conversations.md) - Multi-turn testing
- [Snapshots](snapshots.md) - Regression testing
