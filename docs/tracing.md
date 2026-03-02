# Trace Record & Replay

AgentEval provides powerful **Record & Replay** capabilities that enable deterministic evaluation of AI agents. This "time-travel debugging" feature allows you to capture agent executions once and replay them infinitely without calling the underlying LLM.

## Why Record & Replay?

| Benefit | Description |
|---------|-------------|
| **Deterministic Evaluation** | Replay produces identical responses every time |
| **Cost Reduction** | No LLM API calls during replay |
| **Speed** | Instant replay vs. network latency |
| **CI/CD Integration** | Reliable tests without API credentials |
| **Regression Evaluation** | Detect behavior changes over time |
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
await using var recorder = new TraceRecordingAgent(realAgent, "weather_query");

var response = await recorder.InvokeAsync("What's the weather in Seattle?");
var trace = recorder.Trace;

// Save for later use
await TraceSerializer.SaveToFileAsync(trace, "weather-trace.json");

// REPLAY: Deterministic playback
var replayer = new TraceReplayingAgent(trace);
var replayed = await replayer.InvokeAsync("What's the weather in Seattle?");

// Response is IDENTICAL every time
Assert.Equal(response.Text, replayed.Text);
```

### Multi-Turn Chat Recording

```csharp
using AgentEval.Tracing;

// RECORD: Multi-turn conversation
await using var chatRecorder = new ChatTraceRecorder(chatAgent, "travel_conv");

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
    var replayed = await replayer.InvokeAsync("next turn");
    // Each response matches the original
}
```

### Workflow Recording

```csharp
using AgentEval.Tracing;

// RECORD: Multi-agent workflow
await using var workflowRecorder = new WorkflowTraceRecorder(workflowAdapter, "travel-booking-workflow");
var result = await workflowRecorder.ExecuteWorkflowAsync("Plan trip to Paris");

// Examine recorded steps
foreach (var step in result.Steps)
{
    Console.WriteLine($"Step: {step.ExecutorId} ({step.Duration.TotalSeconds:F1}s)");
}

// Save workflow trace
var workflowTrace = workflowRecorder.Trace;
await WorkflowTraceSerializer.SaveToFileAsync(workflowTrace, "workflow-trace.json");

// REPLAY: Deterministic workflow replay
var loaded = await WorkflowTraceSerializer.LoadFromFileAsync("workflow-trace.json");
var workflowReplayer = new WorkflowTraceReplayingAgent(loaded);
var replayResult = await workflowReplayer.ExecuteWorkflowAsync("Plan trip to Paris");
foreach (var step in replayResult.Steps)
{
    Console.WriteLine($"Step: {step.ExecutorId} -> {step.Output}");
}
```

## Streaming Support

Recording and replaying streaming responses:

```csharp
// RECORD: Streaming execution
await using var streamRecorder = new TraceRecordingAgent(streamingAgent, "story_stream");

var chunks = new List<string>();
await foreach (var chunk in streamRecorder.InvokeStreamingAsync("Tell me a story"))
{
    chunks.Add(chunk.Text);
}
var trace = streamRecorder.Trace;

// REPLAY: Chunks are replayed in order
var replayer = new TraceReplayingAgent(trace);
var replayedChunks = new List<string>();
await foreach (var chunk in replayer.InvokeStreamingAsync("Tell me a story"))
{
    replayedChunks.Add(chunk.Text);
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
    public string Version { get; set; }
    public string TraceName { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public string? AgentName { get; set; }
    public string? ModelId { get; set; }
    public List<TraceEntry> Entries { get; set; }
    public TracePerformance? Performance { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
```

### TraceEntry

Each entry represents a request or response:

```csharp
public class TraceEntry
{
    public TraceEntryType Type { get; set; }    // Request or Response
    public int Index { get; set; }               // Matches request to response
    public string? Prompt { get; set; }          // For requests
    public string? Text { get; set; }            // For responses
    public long? DurationMs { get; set; }
    public TraceTokenUsage? TokenUsage { get; set; }
    public List<TraceToolCall>? ToolCalls { get; set; }
    public TraceError? Error { get; set; }
    public bool IsStreaming { get; set; }
    public List<TraceStreamChunk>? StreamingChunks { get; set; }
}
```

### WorkflowTrace

For multi-agent workflows:

```csharp
public class WorkflowTrace
{
    public string Version { get; set; }
    public string TraceName { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public string? WorkflowType { get; set; }
    public string? OriginalPrompt { get; set; }
    public string? FinalOutput { get; set; }
    public List<string> ExecutorIds { get; set; }
    public List<WorkflowTraceStep> Steps { get; set; }
    public WorkflowTracePerformance? Performance { get; set; }
}
```

## Serialization

### Save and Load Traces

```csharp
// Save trace to JSON
await TraceSerializer.SaveToFileAsync(trace, "trace.json");
await WorkflowTraceSerializer.SaveToFileAsync(workflowTrace, "workflow.json");

// Load trace from JSON
var loadedTrace = await TraceSerializer.LoadFromFileAsync("trace.json");
var loadedWorkflow = await WorkflowTraceSerializer.LoadFromFileAsync("workflow.json");
```

### JSON Format

Traces are stored in human-readable JSON:

```json
{
  "version": "1.0",
  "traceName": "weather_query",
  "capturedAt": "2026-01-08T12:00:00Z",
  "agentName": "WeatherAgent",
  "entries": [
    {
      "type": "Request",
      "index": 0,
      "prompt": "What's the weather in Seattle?"
    },
    {
      "type": "Response",
      "index": 0,
      "text": "The weather in Seattle is currently 52°F with light rain.",
      "durationMs": 1234,
      "tokenUsage": {
        "promptTokens": 15,
        "completionTokens": 20
      },
      "toolCalls": [
        {
          "name": "GetWeather",
          "result": "{\"temp\": 52, \"condition\": \"rain\"}",
          "succeeded": true
        }
      ]
    }
  ],
  "performance": {
    "totalDurationMs": 1234,
    "totalPromptTokens": 15,
    "totalCompletionTokens": 20,
    "callCount": 1
  }
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
Commit traces to source control for regression evaluation:
```bash
git add tests/traces/*.json
git commit -m "Add baseline traces for v1.0"
```

### 3. CI/CD Integration
Use replay in CI pipelines without API credentials:
```yaml
- name: Run Agent Tests
  run: dotnet test --filter "Category=Replay"
  # No AZURE_OPENAI_API_KEY needed!
```

### 4. Golden Master Evaluation
Compare new responses against recorded "golden" responses:
```csharp
var goldenTrace = await TraceSerializer.LoadFromFileAsync("golden-weather.json");
var currentAgent = new WeatherAgent(client);
await using var currentRecorder = new TraceRecordingAgent(currentAgent, "golden_test");

var currentResponse = await currentRecorder.InvokeAsync("Weather in Seattle?");
var goldenResponse = goldenTrace.Entries.First(e => e.Type == TraceEntryType.Response).Text;

Assert.Equal(goldenResponse, currentResponse);
```

### 5. Performance Baseline
Compare performance metrics over time:
```csharp
var oldTrace = await TraceSerializer.LoadFromFileAsync("baseline.json");
var newTrace = recorder.Trace;

var oldDuration = oldTrace.Entries.First(e => e.Type == TraceEntryType.Response).DurationMs;
var newDuration = newTrace.Entries.First(e => e.Type == TraceEntryType.Response).DurationMs;

Assert.True(newDuration <= oldDuration * 1.1, 
    $"Performance regression: {newDuration}ms > 110% of {oldDuration}ms");
```

## Related Guides

- [Conversations](conversations.md) - Multi-turn conversation evaluation
- [Workflow Evaluation](workflows.md) - Multi-agent orchestration evaluation
- [Snapshots](snapshots.md) - Response comparison with diff
- [Benchmarks](benchmarks.md) - Performance benchmarking

---

See [Sample 13](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/Sample13_TraceRecordReplay.cs) for runnable examples of single-agent, multi-turn, workflow, and streaming traces.
