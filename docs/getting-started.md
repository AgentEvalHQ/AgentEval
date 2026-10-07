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

The Getting Started samples (group A in `samples/AgentEval.Samples`) run against a real model by default.
Mock mode is an offline walkthrough with canned replies. It runs only when you pass `--mock`, and it says so.

| Component | Real (default) | `--mock` (on request) |
|-----------|----------------|-----------------------|
| Agent replies | From the configured model | Canned replies |
| Tool tracking & assertions | ✅ Real tool calls | Scripted tool calls; labelled MOCK |
| Performance metrics | ✅ Real timing and tokens | From a canned reply; labelled MOCK |
| LLM-as-judge evaluation (A5) | ✅ Real scores | ❌ Not run; the bundles are listed |

A mock run prints `🎭 MOCK MODE (--mock): the agent returns canned replies. Nothing here measures a model.`
Every pass/fail line ends with `(MOCK: a canned reply, not a measurement)`.
Through 0.42 these samples switched to canned replies by themselves when no provider was set, and printed ✅ passes.

**Without credentials:** the Getting Started samples stop and say what to set, unless you pass `--mock`. Other samples that need a model stop too. Some samples run offline by design, such as Dataset Loaders and Extensibility (group F) and H1 Registry Discovery; the [samples README](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/README.md) lists each sample's requirement.

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

**Azure OpenAI is not the only option.** As of v0.41.0-beta the CLI resolves
`AI_INFERENCE_PROVIDER`, so `agenteval bench` and `agenteval ... calibrate` also run on Bitdeer,
OpenAI, Azure AI Foundry or any OpenAI-compatible endpoint. Leaving the selector unset keeps the
Azure behaviour above exactly as it is. See
[CLI Reference → Environment variables](cli.md#environment-variables) for each provider's variables.
The samples read the same selector (`bitdeer` | `openai` | `foundry` | `azure` | `openai-compatible`), or take
`--provider <name>` for one run; see [Choosing a provider](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/README.md#choosing-a-provider).

### Running Without Credentials (`--mock`)

With no provider configured, a Getting Started sample stops. It prints the "No chat provider configured" box, then:

```
This sample runs a real model. Configure a provider above and re-run,
or pass --mock for an offline walkthrough with canned replies.
```

To explore AgentEval's API without a model, ask for the offline walkthrough:

```bash
dotnet run --project samples/AgentEval.Samples -- 1 --mock   # Hello World (A1) with canned replies
dotnet run --project samples/AgentEval.Samples -- --mock     # the interactive menu, same flag
```

`--mock` covers the Getting Started samples A1–A5. They run the same assertions on canned replies; nothing in a mock run measures a model. In mock mode A5 does not run its LLM-judged demo (Quality/Safety); it lists the bundles instead. A6 Session Lifecycle and A7 Advanced MAF Features need a provider.

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
    var datasetCases = await loader.LoadAsync("testcases.yaml");

    // Create agent and harness
    var agent = CreateMyAgent();
    var harness = new MAFEvaluationHarness(_evaluator);
    var adapter = new MAFAgentAdapter(agent);

    // Run all evaluation cases
    var results = new List<TestResult>();
    foreach (var dc in datasetCases)
    {
        var testCase = dc.ToTestCase(); // Convert DatasetTestCase → TestCase
        var result = await harness.RunEvaluationAsync(adapter, testCase);
        results.Add(result);
    }

    var summary = new TestSummary("Dataset Evaluation", results);

    // Assert all passed — note: TotalCount / PassedCount (not TotalTests / PassedTests)
    Assert.Equal(summary.TotalCount, summary.PassedCount);
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

> **Note:** Dataset files use `DatasetTestCase` field names (e.g., `id`, `input`, `expected`),
> which differ from `TestCase` field names (e.g., `Name`, `ExpectedOutputContains`).
> The `ToTestCase()` extension handles the conversion automatically.
> Field names match in any spelling in every format: `expected_output`, `expectedOutput` and `ExpectedOutput` are the
> same field, and a key that is not a field is kept in the test case's metadata.

```yaml
- id: Greeting Test
  input: Hello, how are you?
  expected: Hello

- id: Weather Query
  input: What's the weather in Paris?
  evaluation_criteria:
    - Should mention weather conditions

- id: Math Problem
  input: What is 25 * 4?
  expected: "100"
```

## Beyond Evaluation: Gatekeeper and Glass Box

Everything above evaluates an agent *after* a run finishes. AgentEval also ships two runtime capabilities that
sit alongside those evaluation, benchmark, and red-team workflows:

- **[Gatekeeper](gatekeeper/introduction.md)** — fail-closed runtime enforcement. Gates wrap your MAF agent at
  the tool-call, run, and session seams and **block** a forbidden tool call, a poisoned argument, or a
  compromised conversation *before it happens*, instead of only flagging it afterward.
- **[Glass Box](glass-box.md)** — dual-boundary tracing that records what your agent's model and tools
  *actually* saw, not just what the framework reports. Attach a trace and see what your agent did after the
  fact — tool reliability, prompt-injection, and argument-sanitization diagnostics that were previously
  invisible.

Both plug into the same MAF pipeline you've already built above — no rewrite needed. See Next Steps
below for the full walkthroughs.

## Next Steps

- **[Architecture Guide](architecture.md)** — Understand AgentEval's component model
- **[Benchmarks Guide](benchmarks.md)** — Run performance and agentic benchmarks
- **[Conversations Guide](conversations.md)** — Evaluate multi-turn agent interactions
- **[Extensibility Guide](extensibility.md)** — Create custom metrics and plugins
- **[Gatekeeper Introduction](gatekeeper/introduction.md)** — Add fail-closed runtime enforcement to your agent
- **[Glass Box](glass-box.md)** — Trace what your agent actually did, not just what it reported
- **[Red Team Guide](redteam.md)** — Probe your agent for prompt injection, jailbreaks, and other attacks
- **[CLI Reference](cli.md)** — Run evaluations, benchmarks, gates, and red-team scans from the command line

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

**Solution:** Add an explicit delay between iterations in your evaluation loop:
```csharp
foreach (var testCase in testCases)
{
    var result = await harness.RunEvaluationAsync(adapter, testCase);
    // Simple throttle — adjust to your provider's RPM limit.
    await Task.Delay(TimeSpan.FromSeconds(1));
}
```

For more sophisticated rate-limit handling, wrap the harness call in [Polly's `RateLimiter` policy](https://www.thepollyproject.org/) or use the `IChatClient` middleware pattern to gate at the transport layer.

### Timeout Errors

**Symptom:** Evaluations timeout waiting for response

**Solution:** Drive the timeout via a `CancellationToken`:
```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
try
{
    var result = await harness.RunEvaluationAsync(adapter, testCase, cts.Token);
}
catch (OperationCanceledException)
{
    // Per-test timeout exceeded — log and continue.
}
```

If `RunEvaluationAsync` doesn't accept a `CancellationToken` overload in your version of AgentEval, gate the awaiter:
```csharp
var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
var evalTask = harness.RunEvaluationAsync(adapter, testCase);
if (await Task.WhenAny(evalTask, timeoutTask) == timeoutTask)
    throw new TimeoutException($"{testCase.Name} exceeded 60s");
var result = await evalTask;
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

*Need help? Check the [samples](https://github.com/AgentEvalHQ/AgentEval/tree/main/samples) or open an issue on GitHub.*
