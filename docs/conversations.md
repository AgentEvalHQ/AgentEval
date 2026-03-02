# Multi-Turn Conversation Evaluation

AgentEval provides comprehensive support for evaluating multi-turn conversations with AI agents, including fluent builders, execution runners, and evaluation metrics.

## Overview

Multi-turn conversation evaluation allows you to:

- Define complex conversation flows with multiple turns
- Specify expected tool calls per turn
- Set timing constraints for the entire conversation
- Execute conversations against any `IChatClient`
- Evaluate conversation completeness and quality

## Quick Start

```csharp
using AgentEval.Testing;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

// First, create your IChatClient (any provider works)
var azureClient = new AzureOpenAIClient(
    new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
    new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

var chatClient = azureClient
    .GetChatClient("gpt-4o")  // Your deployment name
    .AsIChatClient();

// Build a conversation test case using the fluent builder
var testCase = ConversationalTestCase.Create("Customer Support Flow")
    .WithDescription("Tests a complete customer support interaction")
    .WithSystemPrompt("You are a helpful customer service agent.")
    .AddUserTurn("I need to return a product")
    .AddAssistantTurn("I'd be happy to help with your return!")
    .AddUserTurn("Order #12345")
    .ExpectTools("LookupOrder", "ProcessReturn")
    .WithMaxDuration(TimeSpan.FromSeconds(30))
    .Build();

// Run the conversation
var runner = new ConversationRunner(chatClient);
var result = await runner.RunAsync(testCase);

// Assert on the result
Assert.True(result.Success);
Assert.True(result.AllToolsCalled);
```

## Turn Types

The `Turn` record represents a single turn in a conversation:

```csharp
// User turn - input from the user
var userTurn = Turn.User("What's the weather like?");

// Assistant turn - expected response from the agent
var assistantTurn = Turn.Assistant("The weather is sunny and 72°F");

// System turn - system message injection
var systemTurn = Turn.System("You are a weather assistant");

// Tool turn - tool result injection (with tool call ID)
var toolTurn = Turn.Tool("{\"temp\": 72, \"condition\": \"sunny\"}", "call_123");
```

### Turn with Tool Calls

You can specify expected tool calls for assistant turns:

```csharp
var turnWithTools = Turn.Assistant(
    "Let me search for flights to Paris",
    new ToolCallInfo("search_flights", new Dictionary<string, object?>
    {
        ["destination"] = "Paris"
    }),
    new ToolCallInfo("book_flight")
);
```

## Building Conversation Test Cases

Use the fluent `ConversationalTestCaseBuilder`:

```csharp
var testCase = ConversationalTestCase.Create("Flight Booking Conversation")
    // Add description and category
    .WithDescription("Tests the complete flight booking flow")
    .InCategory("Booking")
    
    // System prompt
    .WithSystemPrompt("You are a travel booking assistant.")
    
    // Add user and assistant turns
    .AddUserTurn("I want to book a flight to Paris")
    .AddAssistantTurn("I'd be happy to help you book a flight to Paris!")
    .AddUserTurn("Departing from New York on December 15th")
    .AddToolResponse(@"{""flights"": [{""id"": 1, ""price"": 450}]}", "call_search_001")
    .AddAssistantTurn("I found several flights. The best option is $450.")
    .AddUserTurn("Book the first one")
    
    // Expected tools across the conversation
    .ExpectTools("search_flights", "book_flight", "send_confirmation")
    
    // Expected outcome
    .ExpectOutcome("Flight successfully booked")
    
    // Timing constraints
    .WithMaxDuration(TimeSpan.FromMinutes(2))
    
    // Custom metadata
    .WithMetadata("priority", "high")
    
    .Build();
```

## Running Conversations

The `ConversationRunner` executes conversations against an `IChatClient`:

```csharp
using Microsoft.Extensions.AI;

// Create runner with your chat client
var runner = new ConversationRunner(chatClient);

// Run a single conversation
var result = await runner.RunAsync(testCase);

// Check results
Console.WriteLine($"Success: {result.Success}");
Console.WriteLine($"Response Rate: {result.ResponseRate:P0}");
Console.WriteLine($"All Tools Called: {result.AllToolsCalled}");
Console.WriteLine($"Duration: {result.TotalDuration}");

// Access individual turn results
foreach (var turn in result.TurnResults)
{
    Console.WriteLine($"Turn {turn.TurnNumber}: {turn.Role}");
    if (turn.ToolCalls.Any())
    {
        Console.WriteLine($"  Tools: {string.Join(", ", turn.ToolCalls.Select(t => t.Name))}");
    }
}
```

### Running Multiple Conversations

```csharp
var testCases = new[]
{
    BuildBookingConversation(),
    BuildCancellationConversation(),
    BuildRefundConversation()
};

var results = await runner.RunAllAsync(testCases);

foreach (var result in results)
{
    Console.WriteLine($"{result.TestCaseName}: {(result.Success ? "PASS" : "FAIL")}");
}
```

## Evaluating Conversations

The `ConversationCompletenessMetric` provides a comprehensive evaluation:

```csharp
using AgentEval.Testing;

var metric = new ConversationCompletenessMetric();
var score = metric.Evaluate(conversationResult);

Console.WriteLine($"Overall Score: {score.Score:P0}");
Console.WriteLine($"Response Rate Score: {score.ResponseRateScore:P0}");
Console.WriteLine($"Tool Usage Score: {score.ToolUsageScore:P0}");
Console.WriteLine($"Duration Score: {score.DurationScore:P0}");
Console.WriteLine($"Error Free Score: {score.ErrorFreeScore:P0}");
```

### Scoring Breakdown

The completeness metric scores conversations based on:

| Component | Weight | Description |
|-----------|--------|-------------|
| Response Rate | 40% | Percentage of user turns that received responses |
| Tool Usage | 30% | Percentage of expected tools that were called |
| Duration Compliance | 15% | Whether conversation completed within time limit |
| Error Free | 15% | Whether conversation completed without errors |

## Assertions

Use the result for xUnit assertions:

```csharp
[Fact]
public async Task BookingConversation_CompletesSuccessfully()
{
    var testCase = BuildBookingTestCase();
    var runner = new ConversationRunner(_chatClient);
    
    var result = await runner.RunAsync(testCase);
    
    // Assert success
    Assert.True(result.Success);
    
    // Assert timing
    Assert.True(result.TotalDuration < testCase.MaxDuration);
    
    // Assert tool usage
    Assert.True(result.AllToolsCalled);
    Assert.Contains(result.ToolCalls, t => t.Name == "book_flight");
    
    // Assert response quality
    var metric = new ConversationCompletenessMetric();
    var score = metric.Evaluate(result);
    Assert.True(score.Score >= 0.8, $"Expected score >= 80%, got {score.Score:P0}");
}
```

## Advanced Scenarios

### Conditional Tool Calls

```csharp
var testCase = ConversationalTestCase.Create("Conditional Booking")
    .AddUserTurn("Book if price is under $500")
    .ExpectTools("search_flights") // Only search is always expected
    .Build();

// After execution, conditionally check booking
if (result.TurnResults.Any(t => t.Response?.Contains("under $500") == true))
{
    Assert.Contains(result.ToolCalls, t => t.Name == "book_flight");
}
```

### Error Handling

```csharp
var testCase = ConversationalTestCase.Create("Error Recovery")
    .AddUserTurn("Book flight to invalid destination")
    .AddAssistantTurn("I'm sorry, I couldn't find that destination.")
    .Build();

var result = await runner.RunAsync(testCase);

// Should handle gracefully, not throw
Assert.True(result.Success);
Assert.Empty(result.Errors);
```

### Timeout Handling

```csharp
var testCase = ConversationalTestCase.Create("Timeout Test")
    .WithMaxDuration(TimeSpan.FromSeconds(5))
    .AddUserTurn("Complex multi-step task...")
    .Build();

var result = await runner.RunAsync(testCase);

if (!result.Success && result.TotalDuration >= testCase.MaxDuration)
{
    Console.WriteLine("Conversation timed out");
}
```

## Best Practices

1. **Keep conversations focused** - Test one user journey per conversation
2. **Set realistic timeouts** - Account for LLM response times
3. **Use descriptive names** - Makes test reports easier to read
4. **Test error paths** - Include conversations that should fail gracefully
5. **Verify tool arguments** - Check not just tool names but parameters too
6. **Use the completeness metric** - Get a holistic view of conversation quality

## Recording Conversations for CI/CD

Use `ChatTraceRecorder` to capture entire conversation flows for deterministic replay — no LLM API calls needed during CI:

```csharp
// Record a multi-turn conversation
await using var recorder = new ChatTraceRecorder(agent, "support_conv");
await recorder.AddUserTurnAsync("Hello, I need help with my order");
await recorder.AddUserTurnAsync("Order #12345");
await recorder.AddUserTurnAsync("I want to return it");

// Save for CI replay
await recorder.SaveAsync("support-conversation.trace.json");

// In CI — replay without API calls
var trace = await TraceSerializer.LoadFromFileAsync("support-conversation.trace.json");
var replayer = new TraceReplayingAgent(trace);
while (!replayer.IsComplete)
{
    var response = await replayer.InvokeAsync("next turn");
}
```

See [Tracing](tracing.md) for complete Record & Replay documentation.

## See Also

- [Benchmarks](benchmarks.md) - Performance benchmarks for conversations
- [Extensibility](extensibility.md) - Custom conversation metrics
- [Tracing](tracing.md) - Record & Replay for deterministic conversation testing
- [Snapshots](snapshots.md) - Snapshot regression testing for conversation outputs
- [Sample 08](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/Sample08_ConversationEvaluation.cs) - Runnable conversation evaluation example
