# Getting Started with AgentEval

> **Time to complete:** 5 minutes

This guide walks you through installing AgentEval and writing your first AI agent test.

## Prerequisites

- .NET 8.0 or later
- An xUnit, NUnit, or MSTest test project
- (Optional) Azure OpenAI or OpenAI API access for AI-powered evaluation

## Installation

Install the AgentEval NuGet package:

```bash
dotnet add package AgentEval --prerelease
```

## Creating a MAF Agent

AgentEval works with Microsoft Agent Framework (MAF) agents. Here's how to create one:

```csharp
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public static AIAgent CreateMyAgent()
{
    // Connect to Azure OpenAI
    var azureClient = new AzureOpenAIClient(
        new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
        new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

    var chatClient = azureClient
        .GetChatClient("gpt-4o")  // Your deployment name
        .AsIChatClient();

    // Create a MAF ChatClientAgent
    return new ChatClientAgent(
        chatClient,
        new ChatClientAgentOptions
        {
            Name = "MyAgent",
            Instructions = "You are a helpful assistant."
        });
}
```

### Adding Tools to Your Agent

Agents with tools are more powerful and testable:

```csharp
public static AIAgent CreateWeatherAgent()
{
    var azureClient = new AzureOpenAIClient(
        new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
        new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

    var chatClient = azureClient
        .GetChatClient("gpt-4o")
        .AsIChatClient();

    return new ChatClientAgent(
        chatClient,
        new ChatClientAgentOptions
        {
            Name = "WeatherAgent",
            Instructions = "You are a weather assistant. Use the get_weather tool to check weather.",
            Tools = [AIFunctionFactory.Create(GetWeather)]  // Add your tool
        });
}

// Define a tool as a simple method
[Description("Gets the current weather for a location")]
static string GetWeather(
    [Description("The city name")] string location)
{
    // Your actual weather API call would go here
    return $"The weather in {location} is 72°F and sunny.";
}
```

## Your First Test

### 1. Create a Test Class

```csharp
using AgentEval;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Assertions;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

public class MyAgentTests
{
    [Fact]
    public async Task Agent_ShouldRespondToGreeting()
    {
        // Arrange: Create your MAF agent
        var agent = CreateGreetingAgent();
        var adapter = new MAFAgentAdapter(agent);
        var harness = new MAFTestHarness();

        // Arrange: Define the test case
        var testCase = new TestCase
        {
            Name = "Greeting Test",
            Input = "Hello, my name is Alice!",
            ExpectedOutputContains = "Alice"
        };

        // Act: Run the test
        var result = await harness.RunTestAsync(adapter, testCase);

        // Assert: Check results
        Assert.True(result.Passed, result.FailureReason);
    }

    private static AIAgent CreateGreetingAgent()
    {
        var azureClient = new AzureOpenAIClient(
            new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
            new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

        var chatClient = azureClient
            .GetChatClient("gpt-4o")
            .AsIChatClient();

        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "GreetingAgent",
                Instructions = "You are a friendly greeting assistant. When someone introduces themselves, greet them warmly by name."
            });
    }
}
```

### 2. Add Tool Assertions

AgentEval shines when testing agents that use tools:

```csharp
[Fact]
public async Task Agent_ShouldUseWeatherTool()
{
    // Arrange: Create agent with weather tool
    var agent = CreateWeatherAgent();
    var adapter = new MAFAgentAdapter(agent);
    var harness = new MAFTestHarness();

    var testCase = new TestCase
    {
        Name = "Weather Query",
        Input = "What's the weather in Seattle?"
    };

    // Act
    var result = await harness.RunTestAsync(adapter, testCase);

    // Assert: Fluent tool assertions
    result.ToolUsage!.Should()
        .HaveCalledTool("get_weather")
        .WithArgument("location", "Seattle");
    
    Assert.True(result.Passed);
}

private static AIAgent CreateWeatherAgent()
{
    var azureClient = new AzureOpenAIClient(
        new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
        new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

    var chatClient = azureClient
        .GetChatClient("gpt-4o")
        .AsIChatClient();

    return new ChatClientAgent(
        chatClient,
        new ChatClientAgentOptions
        {
            Name = "WeatherAgent",
            Instructions = "You are a weather assistant. Use the get_weather tool to check weather conditions.",
            Tools = [AIFunctionFactory.Create(GetWeather)]
        });
}

[Description("Gets the current weather for a location")]
static string GetWeather([Description("The city name")] string location)
{
    return $"The weather in {location} is 72°F and sunny.";
}
```

### 3. Add Performance Assertions

Track streaming performance and costs:

```csharp
[Fact]
public async Task Agent_ShouldMeetPerformanceSLAs()
{
    // Arrange: Reuse your agent creation method
    var agent = CreateGreetingAgent();
    var adapter = new MAFAgentAdapter(agent);
    var harness = new MAFTestHarness();

    var testCase = new TestCase
    {
        Name = "Performance Test",
        Input = "Summarize the quarterly report."
    };

    // Act
    var result = await harness.RunTestAsync(adapter, testCase);

    // Assert: Performance metrics
    result.Performance!.Should()
        .HaveTotalDurationUnder(TimeSpan.FromSeconds(5))
        .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500))
        .HaveEstimatedCostUnder(0.05m);
    
    Assert.True(result.Passed);
}
```

## Using AI-Powered Evaluation

For more sophisticated evaluation, provide an LLM evaluator:

```csharp
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public class AdvancedAgentTests
{
    private readonly IChatClient _evaluator;

    public AdvancedAgentTests()
    {
        // Create an evaluator (any IChatClient implementation)
        var client = new AzureOpenAIClient(
            new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
            new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));
        
        _evaluator = client.GetChatClient("gpt-4o").AsIChatClient();
    }

    [Fact]
    public async Task Agent_ShouldProvideHelpfulResponse()
    {
        // Arrange: Use evaluator for AI-powered scoring
        var harness = new MAFTestHarness(_evaluator);
        var agent = CreateHelpDeskAgent();
        var adapter = new MAFAgentAdapter(agent);

        var testCase = new TestCase
        {
            Name = "Helpfulness Test",
            Input = "How do I reset my password?",
            EvaluationCriteria = "Response should provide clear step-by-step instructions"
        };

        // Act
        var result = await harness.RunTestAsync(adapter, testCase);

        // Assert: AI-evaluated quality
        Assert.True(result.Passed, result.Details);
        Assert.True(result.Score >= 80, $"Score was {result.Score}");
    }

    private static AIAgent CreateHelpDeskAgent()
    {
        var azureClient = new AzureOpenAIClient(
            new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
            new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

        var chatClient = azureClient
            .GetChatClient("gpt-4o")
            .AsIChatClient();

        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "HelpDeskAgent",
                Instructions = "You are a helpful IT support agent. Provide clear, step-by-step instructions."
            });
    }
}
```

## Running Tests from CLI

AgentEval includes a CLI for batch evaluation:

```bash
# Initialize a new evaluation project
agenteval init

# Run evaluations from a dataset
agenteval eval --dataset tests.yaml --output results/

# List available exporters
agenteval list exporters
```

## Dataset-Driven Testing

Load test cases from files:

```csharp
using AgentEval.DataLoaders;
using AgentEval.MAF;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

[Fact]
public async Task Agent_ShouldPassAllDatasetTests()
{
    // Load test cases from YAML
    var loader = new YamlDatasetLoader();
    var testCases = await loader.LoadAsync("testcases.yaml");

    // Create agent and harness
    var agent = CreateMyAgent();
    var harness = new MAFTestHarness(_evaluator);
    var adapter = new MAFAgentAdapter(agent);

    // Run all test cases
    var summary = await harness.RunBatchAsync(adapter, testCases);

    // Assert all passed
    Assert.Equal(summary.TotalTests, summary.PassedTests);
}

private static AIAgent CreateMyAgent()
{
    var azureClient = new AzureOpenAIClient(
        new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
        new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

    var chatClient = azureClient
        .GetChatClient("gpt-4o")
        .AsIChatClient();

    return new ChatClientAgent(
        chatClient,
        new ChatClientAgentOptions
        {
            Name = "MyAgent",
            Instructions = "You are a helpful assistant."
        });
}
```

Example `testcases.yaml`:

```yaml
- name: Greeting Test
  input: Hello, how are you?
  expected_output_contains: Hello

- name: Weather Query
  input: What's the weather in Paris?
  evaluation_criteria: Should mention weather conditions

- name: Math Problem
  input: What is 25 * 4?
  expected_output_contains: "100"
```

## Next Steps

- **[Architecture Guide](architecture.md)** — Understand AgentEval's component model
- **[Benchmarks Guide](benchmarks.md)** — Run performance and agentic benchmarks
- **[CLI Reference](cli.md)** — Full CLI command documentation
- **[Conversations Guide](conversations.md)** — Test multi-turn agent interactions
- **[Extensibility Guide](extensibility.md)** — Create custom metrics and plugins

## Quick Reference

### Assertion Cheat Sheet

```csharp
// Tool assertions
result.ToolUsage!.Should()
    .HaveCalledTool("tool_name")
    .NotHaveCalledTool("forbidden_tool")
    .HaveCallCount(3)
    .HaveCallOrder("tool1", "tool2", "tool3")
    .WithArgument("param", "value")
    .WithResultContaining("expected")
    .WithDurationUnder(TimeSpan.FromSeconds(1));

// Performance assertions
result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5))
    .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500))
    .HaveTokenCountUnder(1000)
    .HaveEstimatedCostUnder(0.10m);

// Response assertions
result.ActualOutput!.Should()
    .Contain("expected text")
    .ContainAll("word1", "word2")
    .ContainAny("option1", "option2")
    .NotContain("forbidden")
    .MatchPattern(@"\d{3}-\d{4}")
    .HaveLengthBetween(100, 500);
```

### Common Test Patterns

| Pattern | Use Case |
|---------|----------|
| `ExpectedOutputContains` | Simple substring matching |
| `EvaluationCriteria` | AI-powered quality evaluation |
| `ToolCalls.Should()` | Assert tool usage |
| `Performance.Should()` | Assert latency, cost, tokens |
| `ConversationRunner` | Multi-turn testing |
| `SnapshotComparer` | Regression testing |

---

*Need help? Check the [samples](https://github.com/joslat/AgentEval/tree/main/samples) or open an issue on GitHub.*
