# Multi-Turn Conversation Testing

AgentEval provides comprehensive support for testing multi-turn conversations with AI agents, including fluent builders, execution runners, and evaluation metrics.

## Overview

Multi-turn conversation testing allows you to:

- Define complex conversation flows with multiple turns
- Specify expected tool calls per turn
- Set timing constraints for the entire conversation
- Execute conversations against any `IChatClient`
- Evaluate conversation completeness and quality

## Quick Start

```csharp
using AgentEval.Testing;

// Build a conversation test case
var testCase = new ConversationalTestCaseBuilder()
    .WithName("Customer Support Flow")
    .WithSystemPrompt("You are a helpful customer service agent.")
    .AddUserTurn("I need to return a product")
    .AddAssistantTurn("I'd be happy to help with your return!")
    .AddUserTurn("Order #12345")
    .WithExpectedTools("LookupOrder", "ProcessReturn")
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

// Tool turn - tool result injection
var toolTurn = Turn.Tool("get_weather", "{\"temp\": 72, \"condition\": \"sunny\"}");
```

### Turn with Tool Calls

You can specify expected tool calls for any turn:

```csharp
var turnWithTools = Turn.User(
    "Book a flight to Paris",
    new[]
    {
        new ToolCallInfo("search_flights", new Dictionary<string, object?>
        {
            ["destination"] = "Paris"
        }),
        new ToolCallInfo("book_flight", null)
    }
);
```

## Building Conversation Test Cases

Use the fluent `ConversationalTestCaseBuilder`:

```csharp
var testCase = new ConversationalTestCaseBuilder()
    // Basic metadata
    .WithName("Flight Booking Conversation")
    .WithDescription("Tests the complete flight booking flow")
    
    // System prompt
    .WithSystemPrompt("You are a travel booking assistant.")
    
    // Add turns
    .AddUserTurn("I want to book a flight to Paris")
    .AddAssistantTurn("I'd be happy to help you book a flight to Paris!")
    .AddUserTurn("Departing from New York on December 15th")
    .AddToolTurn("search_flights", @"{""flights"": [{""id"": 1, ""price"": 450}]}")
    .AddAssistantTurn("I found several flights. The best option is $450.")
    .AddUserTurn("Book the first one")
    
    // Add custom turns
    .AddTurn(Turn.User("Confirm the booking", new[]
    {
        new ToolCallInfo("book_flight", new Dictionary<string, object?>
        {
            ["flightId"] = 1
        })
    }))
    
    // Expected tools across the conversation
    .WithExpectedTools("search_flights", "book_flight", "send_confirmation")
    
    // Timing constraints
    .WithMaxDuration(TimeSpan.FromMinutes(2))
    
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
var testCase = new ConversationalTestCaseBuilder()
    .WithName("Conditional Booking")
    .AddUserTurn("Book if price is under $500")
    .WithExpectedTools("search_flights") // Only search is always expected
    .Build();

// After execution, conditionally check booking
if (result.TurnResults.Any(t => t.Response?.Contains("under $500") == true))
{
    Assert.Contains(result.ToolCalls, t => t.Name == "book_flight");
}
```

### Error Handling

```csharp
var testCase = new ConversationalTestCaseBuilder()
    .WithName("Error Recovery")
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
var testCase = new ConversationalTestCaseBuilder()
    .WithName("Timeout Test")
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

## See Also

- [CLI Reference](cli.md) - Running conversation tests from command line
- [Benchmarks](benchmarks.md) - Performance testing conversations
- [Extensibility](extensibility.md) - Custom conversation metrics
