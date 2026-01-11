---
applyTo: "src/AgentEval/Tracing/**/*.cs"
description: Guidelines for implementing trace recording and replay
---

# Tracing Implementation Guidelines

## Core Tracing Components

### Recording Agents
- `TraceRecordingAgent` - Wraps agent to capture executions
- `ChatTraceRecorder` - Records multi-turn conversations
- `WorkflowTraceRecorder` - Records multi-agent workflow steps

### Replay Agents
- `TraceReplayingAgent` - Replays recorded traces deterministically
- `WorkflowTraceReplayingAgent` - Replays workflow traces

### Serialization
- `TraceSerializer` - Save/load `AgentTrace` to/from JSON
- `WorkflowTraceSerializer` - Save/load `WorkflowTrace` to/from JSON

## AgentTrace Structure

```csharp
public class AgentTrace
{
    public string TraceId { get; set; }
    public string AgentName { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public IReadOnlyList<TraceEntry> Entries { get; set; }
    public TraceMetadata Metadata { get; set; }
}

public class TraceEntry
{
    public string Prompt { get; set; }
    public string Response { get; set; }
    public TimeSpan Duration { get; set; }
    public TraceTokenUsage TokenUsage { get; set; }
    public IReadOnlyList<TraceToolCall> ToolCalls { get; set; }
    public TraceError Error { get; set; }
}
```

## Recording Pattern

```csharp
// Wrap real agent
var recorder = new TraceRecordingAgent(realAgent);

// Execute (calls real agent, captures result)
var response = await recorder.ExecuteAsync("query");

// Get trace for storage
var trace = recorder.GetTrace();

// Save to file
TraceSerializer.Save(trace, "trace.json");
```

## Replay Pattern

```csharp
// Load saved trace
var trace = TraceSerializer.Load("trace.json");

// Create replayer
var replayer = new TraceReplayingAgent(trace);

// Replay entries in order
while (!replayer.IsComplete)
{
    var response = await replayer.ReplayNextAsync();
    // Response is identical to original
}
```

## Multi-Turn Chat Recording

```csharp
var chatRecorder = new ChatTraceRecorder(chatAgent);

// Record conversation turns
await chatRecorder.AddUserTurnAsync("Hello");
await chatRecorder.AddUserTurnAsync("Book a flight");

// Get execution result and trace
var result = chatRecorder.GetResult();
var trace = chatRecorder.ToAgentTrace();
```

## Workflow Recording

```csharp
var recorder = new WorkflowTraceRecorder("workflow-name");
recorder.StartWorkflow();

// Record each step
recorder.RecordStep(new WorkflowTraceStep
{
    StepName = "Planner",
    AgentName = "TravelPlanner",
    Input = "Plan trip",
    Output = "Trip planned",
    Duration = TimeSpan.FromSeconds(2)
});

recorder.CompleteWorkflow();
var trace = recorder.GetTrace();
```

## Best Practices

### 1. Unique Trace IDs
Always generate unique IDs for traces:
```csharp
TraceId = Guid.NewGuid().ToString("N")
```

### 2. Capture Token Usage
Include token usage for cost analysis:
```csharp
TokenUsage = new TraceTokenUsage
{
    PromptTokens = 100,
    CompletionTokens = 50
}
```

### 3. Capture Errors
Record errors for debugging:
```csharp
Error = exception != null ? new TraceError
{
    Message = exception.Message,
    Type = exception.GetType().Name
} : null
```

### 4. JSON Serialization
Use System.Text.Json with proper options:
```csharp
var options = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};
```

## Test Patterns

Store traces alongside tests:
```
tests/
├── traces/
│   ├── weather-agent.json
│   ├── booking-workflow.json
│   └── chat-session.json
└── Tracing/
    └── TraceReplayTests.cs
```

Use replay in CI without API credentials:
```csharp
[Fact]
public async Task Agent_ShouldReplayCorrectly()
{
    var trace = TraceSerializer.Load("traces/weather-agent.json");
    var replayer = new TraceReplayingAgent(trace);
    var response = await replayer.ReplayNextAsync();
    Assert.Equal("expected response", response);
}
```
