# Rich Test Output & Time-Travel Debugging

AgentEval provides a rich test output system that captures detailed execution traces, enabling "time-travel debugging" for your AI agent tests. This guide walks you through enabling and using these features step by step.

## What You'll Get

After enabling rich test output, you'll have:

1. **Detailed console output** showing tool call timelines, performance metrics, and errors
2. **JSON trace files** that capture the complete execution for post-mortem debugging
3. **CI/CD integration** with automatic artifact collection for failed tests

> **🚧 Coming Soon: Visual Time-Travel UI**
> 
> Currently, trace files are viewed as JSON or analyzed programmatically. We're building a **visual time-travel debugger** that will allow you to:
> - Step through agent executions visually
> - See conversation history with syntax highlighting
> - Click through tool calls in a timeline view
> - Compare traces side-by-side for regression analysis
> 
> This feature is planned for a future release. For now, you can use the JSON traces with any JSON viewer or the programmatic APIs shown in this guide.

## Quick Start (2 Minutes)

### Step 1: Set Environment Variables

```powershell
# PowerShell (Windows)
$env:AGENTEVAL_VERBOSITY = "Detailed"
$env:AGENTEVAL_SAVE_TRACES = "true"
```

```bash
# Bash (Linux/macOS)
export AGENTEVAL_VERBOSITY=Detailed
export AGENTEVAL_SAVE_TRACES=true
```

### Step 2: Run Your Tests

```bash
dotnet test
```

That's it! You'll see rich output in the console and trace files in `TestResults/traces/`.

## Understanding Verbosity Levels

AgentEval offers four verbosity levels, each providing increasing detail:

### None

Minimal output - just pass/fail. No trace files are saved.

```
TestName ✓ PASS
```

### Summary

Basic statistics for quick scanning:

```
═══ BookingAgent_BooksFlight ═══
Status: ✓ PASS

Summary:
  Duration:    1.2s
  Tool calls:  3
  Tokens:      1,234 in / 567 out
```

### Detailed (Default)

Full tool timeline and performance breakdown:

```
═══ BookingAgent_BooksFlight ═══
Status: ✓ PASS

Performance:
  Total Duration:   1.234s
  Time to First Token: 234ms
  Total Tokens:     1,234 in / 567 out
  Estimated Cost:   $0.0234

Tools (3 calls):
  1. SearchFlights
     Args: {"destination": "Paris", "date": "2026-03-15"}
     Result: [{"flightId": "AF123", "price": 450}]
     Duration: 234ms

  2. GetFlightDetails
     Args: {"flightId": "AF123"}
     Result: {"airline": "Air France", ...}
     Duration: 89ms

  3. BookFlight
     Args: {"flightId": "AF123", "passenger": "John Doe"}
     Result: {"confirmationCode": "XYZ789"}
     Duration: 156ms
```

### Full

Everything above plus complete JSON trace:

```
═══ BookingAgent_BooksFlight ═══
Status: ✓ PASS

[... all the detailed output ...]

Full JSON Trace:
```json
{
  "testName": "BookingAgent_BooksFlight",
  "passed": true,
  "startTime": "2026-01-13T14:30:00Z",
  ...
}
```

## Step-by-Step: Using AgentEvalTestBase

For the best experience, inherit from `AgentEvalTestBase`:

### Step 1: Create Your Test Class

```csharp
using AgentEval.Output;
using AgentEval.Models;
using Xunit;
using Xunit.Abstractions;

public class MyAgentTests : AgentEvalTestBase, IDisposable
{
    public MyAgentTests(ITestOutputHelper output) 
        : base(new XUnitTextWriter(output))
    {
        // Optional: Enable trace saving for ALL tests (not just failures)
        // SaveTracesForAllTests = true;
    }

    [Fact]
    public async Task MyAgent_HandlesQuery_Successfully()
    {
        // Arrange
        var agent = new MyAgent();
        
        // Act
        var response = await agent.ExecuteAsync("Book a flight to Paris");
        
        // Assert & Record
        var result = CreateResult("MyAgent_HandlesQuery")
            .WithOutput(response)
            .WithToolCall("SearchFlights", "call_1", 
                new Dictionary<string, object?> { ["destination"] = "Paris" },
                "[{\"flightId\": \"AF123\"}]")
            .WithToolCall("BookFlight", "call_2")
            .WithTokens(inputTokens: 100, outputTokens: 50)
            .WithCost(0.0025m)
            .Passed(score: 95)
            .Build();
        
        RecordResult(result);
        
        // Traditional assertions still work
        Assert.Contains("Paris", response);
    }
}
```

### Step 2: Create XUnitTextWriter Helper

Since `AgentEvalTestBase` uses `TextWriter` (to avoid xUnit dependency in the main library), you need a simple adapter:

```csharp
using System.Text;
using Xunit.Abstractions;

public class XUnitTextWriter : TextWriter
{
    private readonly ITestOutputHelper _output;
    private readonly StringBuilder _buffer = new();
    
    public XUnitTextWriter(ITestOutputHelper output) => _output = output;
    
    public override Encoding Encoding => Encoding.UTF8;
    
    public override void Write(char value) => _buffer.Append(value);
    
    public override void WriteLine(string? value)
    {
        if (_buffer.Length > 0)
        {
            _output.WriteLine(_buffer.ToString() + value);
            _buffer.Clear();
        }
        else
        {
            _output.WriteLine(value ?? string.Empty);
        }
    }
    
    public override void Flush()
    {
        if (_buffer.Length > 0)
        {
            _output.WriteLine(_buffer.ToString());
            _buffer.Clear();
        }
    }
}
```

### Step 3: Run and View Output

```bash
dotnet test --logger "console;verbosity=detailed"
```

## Step-by-Step: Manual Output Writer

If you prefer not to inherit from a base class:

```csharp
using AgentEval.Output;
using AgentEval.Models;

[Fact]
public async Task MyAgent_Test()
{
    // Create output writer with your settings
    var settings = new VerbositySettings
    {
        Level = VerbosityLevel.Detailed,
        IncludeToolArguments = true,
        IncludeToolResults = true,
        IncludePerformanceMetrics = true,
        SaveTraceFiles = true,
        TraceOutputDirectory = "TestResults/traces"
    };
    
    var output = new StringWriter();
    var writer = new EvaluationOutputWriter(settings, output);
    
    // Run your test...
    var result = new TestResult
    {
        TestName = "MyAgentTest",
        Passed = true,
        Score = 100,
        ActualOutput = "Flight booked successfully",
        Performance = new PerformanceMetrics
        {
            StartTime = DateTimeOffset.UtcNow.AddSeconds(-2),
            EndTime = DateTimeOffset.UtcNow,
            PromptTokens = 100,
            CompletionTokens = 50
        }
    };
    
    // Write output
    writer.WriteTestResult(result);
    
    // Output goes to the StringWriter
    Console.WriteLine(output.ToString());
}
```

## Step-by-Step: Save and Load Traces

### Saving Traces

```csharp
using AgentEval.Output;

// Create artifact manager
var manager = new TraceArtifactManager("TestResults/traces");

// Save a test result (creates JSON file)
var result = new TestResult { TestName = "MyTest", Passed = true, Score = 100 };
string filePath = manager.SaveTestResult(result);
Console.WriteLine($"Trace saved: {filePath}");
// Output: Trace saved: TestResults/traces/MyTest_20260113_143022_123.json

// Save a full TimeTravelTrace
var trace = new TimeTravelTrace
{
    TraceId = Guid.NewGuid().ToString(),
    ExecutionType = ExecutionType.SingleAgent,
    Test = new EvaluationMetadata
    {
        TestName = "DetailedTest",
        StartTime = DateTimeOffset.UtcNow.AddSeconds(-5),
        EndTime = DateTimeOffset.UtcNow,
        Passed = true
    },
    Agents = new List<AgentInfo>
    {
        new() { AgentId = "agent1", AgentName = "BookingAgent", ModelId = "gpt-4o" }
    },
    Steps = new List<ExecutionStep>
    {
        new()
        {
            StepNumber = 1,
            Type = StepType.UserInput,
            Timestamp = DateTimeOffset.UtcNow.AddSeconds(-5),
            OffsetFromStart = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(10),
            Data = new UserInputStepData { Message = "Book a flight to Paris" }
        }
    },
    Summary = new ExecutionSummary
    {
        Passed = true,
        TotalDuration = TimeSpan.FromSeconds(5),
        TotalSteps = 1,
        ToolCallCount = 0,
        ToolErrorCount = 0,
        LlmRequestCount = 1
    }
};

string tracePath = manager.SaveTrace(trace);
```

### Loading Traces

```csharp
// Load a specific trace
var loadedTrace = manager.LoadTrace("TestResults/traces/MyTest_20260113_143022_123.json");
Console.WriteLine($"Loaded trace: {loadedTrace.TraceId}");
Console.WriteLine($"Steps: {loadedTrace.Steps.Count}");

// List all traces
foreach (var file in manager.ListTraceFiles())
{
    Console.WriteLine(file);
}

// Get most recent trace for a test
var recentTrace = manager.GetMostRecentTrace("MyTest");
if (recentTrace != null)
{
    Console.WriteLine($"Most recent: {recentTrace}");
}

// Clean up old traces (older than 7 days)
int deleted = manager.CleanupOldTraces(TimeSpan.FromDays(7));
Console.WriteLine($"Deleted {deleted} old trace files");
```

## Step-by-Step: CI/CD Setup

### GitHub Actions

1. **Update your workflow file** (`.github/workflows/test.yml`):

```yaml
name: Tests

on: [push, pull_request]

env:
  # Enable rich output for all test runs
  AGENTEVAL_VERBOSITY: Detailed
  AGENTEVAL_SAVE_TRACES: true

jobs:
  test:
    runs-on: ubuntu-latest
    
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      
      - name: Test
        run: dotnet test --logger trx
        env:
          AZURE_OPENAI_ENDPOINT: ${{ secrets.AZURE_OPENAI_ENDPOINT }}
          AZURE_OPENAI_API_KEY: ${{ secrets.AZURE_OPENAI_API_KEY }}
      
      # Always upload traces, even on success
      - name: Upload Traces
        uses: actions/upload-artifact@v4
        if: always()
        with:
          name: test-traces
          path: TestResults/traces/
          retention-days: 7
```

2. **View traces after a failed build**:
   - Go to the failed workflow run
   - Click "Artifacts" section
   - Download `test-traces`
   - Open the JSON files to see what happened

### Azure DevOps

Add to your `azure-pipelines.yml`:

```yaml
trigger:
  - main

pool:
  vmImage: 'ubuntu-latest'

variables:
  AGENTEVAL_VERBOSITY: Detailed
  AGENTEVAL_SAVE_TRACES: true

steps:
  - task: DotNetCoreCLI@2
    inputs:
      command: test
      arguments: '--logger trx'
    env:
      AZURE_OPENAI_ENDPOINT: $(AZURE_OPENAI_ENDPOINT)
      AZURE_OPENAI_API_KEY: $(AZURE_OPENAI_API_KEY)

  - task: PublishBuildArtifacts@1
    condition: always()
    inputs:
      pathToPublish: TestResults/traces
      artifactName: AgentTraces
```

## Debugging Failed Tests

When a test fails in CI, follow these steps:

### 1. Download the Trace

From GitHub Actions:
```bash
# Using gh CLI
gh run download <run-id> -n test-traces
```

Or click the download button in the Actions UI.

### 2. Pretty-Print the JSON

```bash
# View with jq
cat TestResults/traces/FailedTest_*.json | jq '.'

# Or use VS Code's built-in JSON viewer
code TestResults/traces/FailedTest_*.json
```

### 3. Analyze the Execution

Look for key sections in the trace:

```json
{
  "testName": "BookingAgent_ShouldNotDeleteData",
  "passed": false,
  
  "steps": [
    // Find the step where things went wrong
    {
      "stepNumber": 5,
      "type": "ToolCall",
      "data": {
        "toolName": "DeleteData",  // <- Unexpected tool call!
        "arguments": { "scope": "all" }
      }
    }
  ],
  
  "summary": {
    "passed": false,
    "toolCallCount": 3,
    "toolErrorCount": 0,
    "assertions": [
      {
        "name": "NeverCallTool(DeleteData)",
        "passed": false,
        "message": "Tool was called at step 5"
      }
    ]
  }
}
```

### 4. Reproduce Locally (Optional)

If you need to debug further, the trace tells you exactly what input caused the problem:

```csharp
// Extract the user input from the trace
var userInput = trace.Steps
    .First(s => s.Type == StepType.UserInput)
    .Data as UserInputStepData;

Console.WriteLine($"User said: {userInput.Message}");
// "Delete all my old bookings"

// Now run locally with the same input
var agent = new BookingAgent();
var response = await agent.ExecuteAsync(userInput.Message);
```

## Configuration Reference

### Environment Variables

| Variable | Type | Default | Description |
|----------|------|---------|-------------|
| `AGENTEVAL_VERBOSITY` | Enum | `Detailed` | Output level: `None`, `Summary`, `Detailed`, `Full` |
| `AGENTEVAL_SAVE_TRACES` | Boolean | `true` if not `None` | Whether to save trace files |
| `AGENTEVAL_TRACE_DIR` | String | `TestResults/traces` | Directory for trace files |

### VerbositySettings Properties

```csharp
var settings = new VerbositySettings
{
    Level = VerbosityLevel.Full,              // Verbosity level
    IncludeToolArguments = true,              // Show tool arguments
    IncludeToolResults = true,                // Show tool results
    IncludePerformanceMetrics = true,         // Show perf stats
    IncludeConversationHistory = false,       // Show full conversation
    SaveTraceFiles = true,                    // Save JSON traces
    TraceOutputDirectory = "TestResults/traces"  // Where to save
};
```

## Troubleshooting

### Traces not being saved

1. Check that `AGENTEVAL_SAVE_TRACES=true`
2. Verify the trace directory is writable
3. Ensure you're calling `RecordResult()` at the end of your tests

### Output not appearing in xUnit

Make sure you're:
1. Using `ITestOutputHelper` properly
2. Passing a `TextWriter` to `AgentEvalTestBase`
3. Running with `--logger "console;verbosity=detailed"`

### Large trace files

If traces are too large:
1. Set `IncludeConversationHistory = false`
2. Use `VerbosityLevel.Summary` for routine runs
3. Use `VerbosityLevel.Full` only for debugging

### Environment variables not working

Environment variables are read at startup. If you change them:
1. In tests: use `VerbosityConfiguration.SetOverride()` instead
2. In CI: set them in the `env:` section of your workflow

## See Also

- [CI/CD Integration](ci-cd-integration.md) - Detailed CI/CD setup
- [Tracing](tracing.md) - Trace Record & Replay patterns
- [Assertions](assertions.md) - Fluent assertion API
