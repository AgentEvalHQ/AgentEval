# Trace Record & Replay

AgentEval provides powerful **Record & Replay** capabilities that enable deterministic testing of AI agents. This "time-travel debugging" feature allows you to capture agent executions once and replay them infinitely without calling the underlying LLM.

## Why Record & Replay?

| Benefit | Description |
|---------|-------------|
| **Deterministic Testing** | Replay produces identical responses every time |
| **Cost Reduction** | No LLM API calls during replay |
| **Speed** | Instant replay vs. network latency |
| **CI/CD Integration** | Reliable tests without API credentials |
| **Regression Testing** | Detect behavior changes over time |
| **Debugging** | Inspect and analyze past executions |

## Core Components

| Component | Description |
|-----------|-------------|
| `TraceRecordingAgent` | Wraps an agent to capture executions |
| `TraceReplayingAgent` | Replays recorded traces deterministically |
| `ChatTraceRecorder` | Records multi-turn conversations |
| `WorkflowTraceRecorder` | Records multi-agent workflow orchestrations |
| `WorkflowTraceReplayingAgent` | Replays workflow traces |
| `TraceSerializer` | Saves/loads `AgentTrace` to/from JSON |
| `WorkflowTraceSerializer` | Saves/loads `WorkflowTrace` to/from JSON |

## Quick Start

### Single-Agent Record & Replay

```csharp
using AgentEval.Tracing;

// RECORD: Capture the agent execution
var realAgent = new MyToolAgent(chatClient);
var recorder = new TraceRecordingAgent(realAgent);

var response = await recorder.ExecuteAsync("What's the weather in Seattle?");
var trace = recorder.GetTrace();

// Save for later use
TraceSerializer.Save(trace, "weather-trace.json");

// REPLAY: Deterministic playback
var replayer = new TraceReplayingAgent(trace);
var replayed = await replayer.ReplayNextAsync();

// Response is IDENTICAL every time
Assert.Equal(response, replayed);
```

### Multi-Turn Chat Recording

```csharp
using AgentEval.Tracing;

// RECORD: Multi-turn conversation
var chatRecorder = new ChatTraceRecorder(chatAgent);

var r1 = await chatRecorder.AddUserTurnAsync("Hello, what can you help me with?");
var r2 = await chatRecorder.AddUserTurnAsync("Book a flight to Paris");
var r3 = await chatRecorder.AddUserTurnAsync("Book the first option");

// Get results and trace
var chatResult = chatRecorder.GetResult();
Console.WriteLine($"Recorded {chatResult.TotalTurnCount} turns");

var trace = chatRecorder.ToAgentTrace();

// REPLAY: Deterministic conversation replay
var replayer = new TraceReplayingAgent(trace);
while (!replayer.IsComplete)
{
    var replayed = await replayer.ReplayNextAsync();
    // Each response matches the original
}
```

### Workflow Recording

```csharp
using AgentEval.Tracing;

// RECORD: Multi-agent workflow
var workflowRecorder = new WorkflowTraceRecorder("travel-booking-workflow");

workflowRecorder.StartWorkflow();

var plannerResult = await plannerAgent.ExecuteAsync("Plan trip to Paris");
workflowRecorder.RecordStep(new WorkflowTraceStep
{
    StepName = "Planner",
    AgentName = "TravelPlanner",
    Input = "Plan trip to Paris",
    Output = plannerResult,
    Duration = TimeSpan.FromSeconds(2)
});

var bookerResult = await bookerAgent.ExecuteAsync(plannerResult);
workflowRecorder.RecordStep(new WorkflowTraceStep
{
    StepName = "Booker",
    AgentName = "FlightBooker",
    Input = plannerResult,
    Output = bookerResult,
    Duration = TimeSpan.FromSeconds(1.5)
});

workflowRecorder.CompleteWorkflow();

// Save workflow trace
var workflowTrace = workflowRecorder.GetTrace();
WorkflowTraceSerializer.Save(workflowTrace, "workflow-trace.json");

// REPLAY: Deterministic workflow replay
var workflowReplayer = new WorkflowTraceReplayingAgent(workflowTrace);
while (!workflowReplayer.IsComplete)
{
    var step = workflowReplayer.ReplayNextStep();
    Console.WriteLine($"Step: {step.StepName} -> {step.Output}");
}
```

## Streaming Support

Recording and replaying streaming responses:

```csharp
// RECORD: Streaming execution
var streamRecorder = new TraceRecordingAgent(streamingAgent);

var chunks = new List<string>();
await foreach (var chunk in streamRecorder.ExecuteStreamingAsync("Tell me a story"))
{
    chunks.Add(chunk);
}
var trace = streamRecorder.GetTrace();

// REPLAY: Chunks are replayed in order
var replayer = new TraceReplayingAgent(trace);
var replayedChunks = new List<string>();
await foreach (var chunk in replayer.ReplayStreamingAsync())
{
    replayedChunks.Add(chunk);
}

// Same chunks in same order
Assert.Equal(chunks.Count, replayedChunks.Count);
```

## Trace Model

### AgentTrace

The `AgentTrace` class captures a single agent's execution:

```csharp
public class AgentTrace
{
    public string TraceId { get; set; }
    public string AgentName { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public IReadOnlyList<TraceEntry> Entries { get; set; }
    public TraceMetadata Metadata { get; set; }
}
```

### TraceEntry

Each entry represents a prompt/response pair:

```csharp
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

### WorkflowTrace

For multi-agent workflows:

```csharp
public class WorkflowTrace
{
    public string WorkflowId { get; set; }
    public string WorkflowName { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public IReadOnlyList<WorkflowTraceStep> Steps { get; set; }
    public WorkflowTracePerformance Performance { get; set; }
}
```

## Serialization

### Save and Load Traces

```csharp
// Save trace to JSON
TraceSerializer.Save(trace, "trace.json");
WorkflowTraceSerializer.Save(workflowTrace, "workflow.json");

// Load trace from JSON
var loadedTrace = TraceSerializer.Load("trace.json");
var loadedWorkflow = WorkflowTraceSerializer.Load("workflow.json");
```

### JSON Format

Traces are stored in human-readable JSON:

```json
{
  "traceId": "abc-123",
  "agentName": "WeatherAgent",
  "recordedAt": "2026-01-08T12:00:00Z",
  "entries": [
    {
      "prompt": "What's the weather in Seattle?",
      "response": "The weather in Seattle is currently 52°F with light rain.",
      "duration": "00:00:01.234",
      "tokenUsage": {
        "promptTokens": 15,
        "completionTokens": 20,
        "totalTokens": 35
      },
      "toolCalls": [
        {
          "toolName": "GetWeather",
          "arguments": {"city": "Seattle"},
          "result": {"temp": 52, "condition": "rain"},
          "duration": "00:00:00.500"
        }
      ]
    }
  ]
}
```

## Best Practices

### 1. Test Organization
Store traces alongside your tests:
```
tests/
├── traces/
│   ├── weather-agent.json
│   ├── booking-workflow.json
│   └── chat-session.json
├── AgentTests.cs
└── WorkflowTests.cs
```

### 2. Version Control
Commit traces to source control for regression testing:
```bash
git add tests/traces/*.json
git commit -m "Add baseline traces for v1.0"
```

### 3. CI/CD Integration
Use replay in CI pipelines without API credentials:
```yaml
- name: Run Agent Tests
  run: dotnet test --filter "Category=Replay"
  # No AZURE_OPENAI_KEY needed!
```

### 4. Golden Master Testing
Compare new responses against recorded "golden" responses:
```csharp
var goldenTrace = TraceSerializer.Load("golden-weather.json");
var currentAgent = new WeatherAgent(client);
var currentRecorder = new TraceRecordingAgent(currentAgent);

var currentResponse = await currentRecorder.ExecuteAsync("Weather in Seattle?");
var goldenResponse = goldenTrace.Entries[0].Response;

Assert.Equal(goldenResponse, currentResponse);
```

### 5. Performance Baseline
Compare performance metrics over time:
```csharp
var oldTrace = TraceSerializer.Load("baseline.json");
var newTrace = recorder.GetTrace();

var oldDuration = oldTrace.Entries[0].Duration;
var newDuration = newTrace.Entries[0].Duration;

Assert.True(newDuration <= oldDuration * 1.1, 
    $"Performance regression: {newDuration} > 110% of {oldDuration}");
```

## Related Guides

- [Conversations](conversations.md) - Multi-turn conversation testing
- [Workflow Testing](workflows.md) - Multi-agent orchestration testing
- [Snapshots](snapshots.md) - Response comparison with diff
- [Benchmarks](benchmarks.md) - Performance benchmarking

---

See [Sample 13](https://github.com/joslat/AgentEval/blob/main/samples/AgentEval.Samples/Sample13_TraceRecordReplay.cs) for runnable examples.
