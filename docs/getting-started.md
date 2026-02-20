# Getting Started with AgentEval

> **Time to complete:** 5 minutes

This guide walks you through installing AgentEval and writing your first AI agent evaluation.

## Quick Path

| Time | Step |
|------|------|
| **1 min** | Install NuGet package |
| **2 min** | Create a MAF agent |
| **2 min** | Write your first evaluation |

## Prerequisites

- .NET 8.0, 9.0, or 10.0 SDK
- An xUnit, NUnit, or MSTest project
- Azure OpenAI or OpenAI API access (for LLM-as-judge evaluation)

### Mock vs Real Mode

AgentEval follows the principle: **"Evaluation Always Real, Structure Optionally Mock"**

| Component | Mock Mode | Real Mode |
|-----------|-----------|-----------|
| Tool tracking & assertions | ✅ Works | ✅ Works |
| Performance metrics | ✅ Simulated | ✅ Real timing |
| Conversation flows | ✅ Works | ✅ Works |
| LLM-as-judge evaluation | ❌ Skipped | ✅ Real scores |
| RAG quality metrics | ❌ Skipped | ✅ Real evaluation |
| Model comparison | ❌ Skipped | ✅ Real comparison |

**Without credentials:** Samples 1-4 work fully in mock mode; samples 5-24 gracefully skip or show credential-required messages.

### Required Environment Variables

Set these before running evaluations:

```powershell
# PowerShell
$env:AZURE_OPENAI_ENDPOINT = "https://your-resource.openai.azure.com/"
$env:AZURE_OPENAI_API_KEY = "your-api-key"
$env:AZURE_OPENAI_DEPLOYMENT = "gpt-4o"  # Your deployment name
```

```bash
# Bash/Linux/macOS
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"
export AZURE_OPENAI_API_KEY="your-api-key"
export AZURE_OPENAI_DEPLOYMENT="gpt-4o"
```

> **Tip:** Add these to your `.bashrc`, `.zshrc`, or Windows user environment variables for persistence.

### Running Without Credentials (Mock Mode)

If you just want to explore AgentEval's API without Azure credentials:

```bash
# Run the samples project
cd samples/AgentEval.Samples
dotnet run
```

You'll see samples 1-4 demonstrate tool tracking, performance metrics, and more—all without real LLM calls. Samples 5-24 will show informative "credentials required" messages.

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

    var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
    var chatClient = azureClient
        .GetChatClient(deployment)
        .AsIChatClient();

    // Create a MAF ChatClientAgent
    return new ChatClientAgent(
        chatClient,
        new ChatClientAgentOptions
        {
            Name = "MyAgent",
            ChatOptions = new() { Instructions = "You are a helpful assistant." }
        });
}
```

### Adding Tools to Your Agent

Agents with tools are more powerful and evaluable:

```csharp
public static AIAgent CreateWeatherAgent()
{
    var azureClient = new AzureOpenAIClient(
        new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
        new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

    var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
    var chatClient = azureClient
        .GetChatClient(deployment)
        .AsIChatClient();

    return new ChatClientAgent(
        chatClient,
        new ChatClientAgentOptions
        {
            Name = "WeatherAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = "You are a weather assistant. Use the get_weather tool to check weather.",
                Tools = [AIFunctionFactory.Create(GetWeather)]  // Add your tool
            }
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

## Your First Evaluation

### 1. Create an Evaluation Class

```csharp
using AgentEval;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Assertions;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

public class MyAgentEvaluations
{
    [Fact]
    public async Task Agent_ShouldRespondToGreeting()
    {
        // Arrange: Create your MAF agent
        var agent = CreateGreetingAgent();
        var adapter = new MAFAgentAdapter(agent);
        var harness = new MAFEvaluationHarness();

        // Arrange: Define the evaluation case
        var testCase = new TestCase
        {
            Name = "Greeting Evaluation",
            Input = "Hello, my name is Alice!",
            ExpectedOutputContains = "Alice"
        };

        // Act: Run the evaluation
        var result = await harness.RunEvaluationAsync(adapter, testCase);

        // Assert: Check results
        Assert.True(result.Passed, result.FailureReason);
    }

    private static AIAgent CreateGreetingAgent()
    {
        var azureClient = new AzureOpenAIClient(
            new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
            new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
        var chatClient = azureClient
            .GetChatClient(deployment)
            .AsIChatClient();

        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "GreetingAgent",
                ChatOptions = new() { Instructions = "You are a friendly greeting assistant. When someone introduces themselves, greet them warmly by name." }
            });
    }
}
```

### 2. Add Tool Assertions

AgentEval shines when evaluating agents that use tools:

```csharp
[Fact]
public async Task Agent_ShouldUseWeatherTool()
{
    // Arrange: Create agent with weather tool
    var agent = CreateWeatherAgent();
    var adapter = new MAFAgentAdapter(agent);
    var harness = new MAFEvaluationHarness();

    var testCase = new TestCase
    {
        Name = "Weather Query Evaluation",
        Input = "What's the weather in Seattle?"
    };

    // Act
    var result = await harness.RunEvaluationAsync(adapter, testCase);

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

    var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
    var chatClient = azureClient
        .GetChatClient(deployment)
        .AsIChatClient();

    return new ChatClientAgent(
        chatClient,
        new ChatClientAgentOptions
        {
            Name = "WeatherAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = "You are a weather assistant. Use the get_weather tool to check weather conditions.",
                Tools = [AIFunctionFactory.Create(GetWeather)]
            }
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
    var harness = new MAFEvaluationHarness();

    var testCase = new TestCase
    {
        Name = "Performance Evaluation",
        Input = "Summarize the quarterly report."
    };

    // Act
    var result = await harness.RunEvaluationAsync(adapter, testCase);

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

public class AdvancedAgentEvaluations
{
    private readonly IChatClient _evaluator;

    public AdvancedAgentEvaluations()
    {
        // Create an evaluator (any IChatClient implementation)
        var client = new AzureOpenAIClient(
            new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
            new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));
        
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
        _evaluator = client.GetChatClient(deployment).AsIChatClient();
    }

    [Fact]
    public async Task Agent_ShouldProvideHelpfulResponse()
    {
        // Arrange: Use evaluator for AI-powered scoring
        var harness = new MAFEvaluationHarness(_evaluator);
        var agent = CreateHelpDeskAgent();
        var adapter = new MAFAgentAdapter(agent);

        var testCase = new TestCase
        {
            Name = "Helpfulness Evaluation",
            Input = "How do I reset my password?",
            EvaluationCriteria = "Response should provide clear step-by-step instructions"
        };

        // Act
        var result = await harness.RunEvaluationAsync(adapter, testCase);

        // Assert: AI-evaluated quality
        Assert.True(result.Passed, result.Details);
        Assert.True(result.Score >= 80, $"Score was {result.Score}");
    }

    private static AIAgent CreateHelpDeskAgent()
    {
        var azureClient = new AzureOpenAIClient(
            new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
            new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
        var chatClient = azureClient
            .GetChatClient(deployment)
            .AsIChatClient();

        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "HelpDeskAgent",
                ChatOptions = new() { Instructions = "You are a helpful IT support agent. Provide clear, step-by-step instructions." }
            });
    }
}
```

## Running from CLI

AgentEval includes a CLI for batch evaluation:

```bash
# Initialize a new evaluation project
agenteval init

# Run evaluations from a dataset
agenteval eval --dataset tests.yaml --output results/

# List available exporters
agenteval list exporters
```

## Dataset-Driven Evaluation

Load evaluation cases from files:

```csharp
using AgentEval.DataLoaders;
using AgentEval.MAF;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

[Fact]
public async Task Agent_ShouldPassAllDatasetEvaluations()
{
    // Load evaluation cases from YAML
    var loader = new YamlDatasetLoader();
    var testCases = await loader.LoadAsync("testcases.yaml");

    // Create agent and harness
    var agent = CreateMyAgent();
    var harness = new MAFEvaluationHarness(_evaluator);
    var adapter = new MAFAgentAdapter(agent);

    // Run all evaluation cases
    var summary = await harness.RunBatchAsync(adapter, testCases);

    // Assert all passed
    Assert.Equal(summary.TotalTests, summary.PassedTests);
}

private static AIAgent CreateMyAgent()
{
    var azureClient = new AzureOpenAIClient(
        new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
        new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

    var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
    var chatClient = azureClient
        .GetChatClient(deployment)
        .AsIChatClient();

    return new ChatClientAgent(
        chatClient,
        new ChatClientAgentOptions
        {
            Name = "MyAgent",
            ChatOptions = new() { Instructions = "You are a helpful assistant." }
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
- **[Conversations Guide](conversations.md)** — Evaluate multi-turn agent interactions
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

### Common Evaluation Patterns

| Pattern | Use Case |
|---------|---------|
| `ExpectedOutputContains` | Simple substring matching |
| `EvaluationCriteria` | AI-powered quality evaluation |
| `ToolCalls.Should()` | Assert tool usage |
| `Performance.Should()` | Assert latency, cost, tokens |
| `ConversationRunner` | Multi-turn evaluation |
| `SnapshotComparer` | Regression evaluation |

---

## Troubleshooting

### DeploymentNotFound (HTTP 404)

**Symptom:** `DeploymentNotFound` error when running tests

**Cause:** The deployment name doesn't match your Azure OpenAI resource

**Solution:**
```powershell
# Verify your deployment exists in Azure Portal
# Set the correct deployment name:
$env:AZURE_OPENAI_DEPLOYMENT = "your-actual-deployment-name"
```

### Environment Variables Not Set

**Symptom:** `NullReferenceException` or empty configuration

**Cause:** Missing required environment variables

**Solution:** Ensure all three are set:
```powershell
$env:AZURE_OPENAI_ENDPOINT    # Required: https://xxx.openai.azure.com/
$env:AZURE_OPENAI_API_KEY     # Required: Your API key
$env:AZURE_OPENAI_DEPLOYMENT  # Required: Your deployment name (e.g., gpt-4o)
```

### Rate Limiting (HTTP 429)

**Symptom:** `TooManyRequests` error during batch evaluations

**Solution:** Add delays between evaluations:
```csharp
var options = new EvaluationRunOptions
{
    DelayBetweenTests = TimeSpan.FromSeconds(1)
};
```

### Timeout Errors

**Symptom:** Evaluations timeout waiting for response

**Solution:** Increase timeout in evaluation configuration:
```csharp
var testCase = new TestCase
{
    Name = "Long Running Evaluation",
    Input = "Generate a detailed report...",
    TimeoutSeconds = 60  // Default is 30
};
```

### Inconsistent Tool Calls

**Symptom:** Tool sometimes called, sometimes not

**Causes:**
- Prompt is ambiguous
- Temperature too high

**Solution:** Use more specific prompts:
```csharp
// ❌ Ambiguous
var testCase = new TestCase { Input = "What's the weather?" };

// ✅ Specific
var testCase = new TestCase 
{ 
    Input = "What is the current temperature in Seattle, WA in Fahrenheit?" 
};
```

### High Variance in LLM Scores

**Symptom:** Quality scores vary widely between runs

**Solution:** Use [stochastic evaluation](stochastic-evaluation.md) to run multiple times and analyze statistics:
```csharp
var stochasticRunner = new StochasticRunner(harness, statisticsCalculator: null, EvaluationOptions);
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase, 
    new StochasticOptions(Runs: 10, SuccessRateThreshold: 0.8));
```

---

*Need help? Check the [samples](https://github.com/joslat/AgentEval/tree/main/samples) or open an issue on GitHub.*
