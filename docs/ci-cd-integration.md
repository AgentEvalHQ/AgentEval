# CI/CD Integration Guide

AgentEval provides rich test output and trace artifacts specifically designed for CI/CD environments. This guide shows how to configure AgentEval for optimal debugging in your pipelines.

## Environment Variables

AgentEval's output system can be controlled entirely via environment variables, making it ideal for CI/CD configuration.

### Core Variables

| Variable | Description | Values | Default |
|----------|-------------|--------|---------|
| `AGENTEVAL_VERBOSITY` | Controls output detail level | `None`, `Summary`, `Detailed`, `Full` | `Summary` |
| `AGENTEVAL_SAVE_TRACES` | Save trace files on test failure | `true`, `false` | `false` |
| `AGENTEVAL_TRACE_DIR` | Directory for trace files | Any valid path | `TestResults/traces` |

### Verbosity Levels

- **None**: No output (silent tests)
- **Summary**: Test name and pass/fail status only
- **Detailed**: Includes tool calls, metrics, and errors
- **Full**: Complete debugging output including all arguments and raw responses

### Example: Local Development

```powershell
# PowerShell
$env:AGENTEVAL_VERBOSITY = "Detailed"
$env:AGENTEVAL_SAVE_TRACES = "true"
dotnet test
```

```bash
# Bash
export AGENTEVAL_VERBOSITY=Detailed
export AGENTEVAL_SAVE_TRACES=true
dotnet test
```

## Trace Artifacts

When `AGENTEVAL_SAVE_TRACES=true`, AgentEval saves trace files that enable "time-travel debugging" - you can see exactly what happened in a failed test without re-running it.

### Trace File Location

```
TestResults/
└── traces/
    ├── TestClassName_TestMethodName_2025-01-30_143022.json
    ├── TestClassName_TestMethodName_2025-01-30_143022.txt
    └── ...
```

### Trace File Formats

1. **JSON format**: Machine-readable, includes full trace data
2. **Text format**: Human-readable summary

### Trace Contents

Each trace includes:
- Test metadata (name, start time, duration)
- Complete conversation history
- All tool calls with arguments and results
- Performance metrics (token usage, latency, estimated cost)
- Error details with suggestions
- Step-by-step execution timeline

## GitHub Actions Integration

### Basic Workflow

```yaml
name: Agent Tests

on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest
    
    env:
      AGENTEVAL_VERBOSITY: Detailed
      AGENTEVAL_SAVE_TRACES: true
      AGENTEVAL_TRACE_DIR: ${{ github.workspace }}/TestResults/traces

    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      
      - name: Run Agent Tests
        run: dotnet test --logger "trx;LogFileName=test-results.trx"
        env:
          AZURE_OPENAI_ENDPOINT: ${{ secrets.AZURE_OPENAI_ENDPOINT }}
          AZURE_OPENAI_API_KEY: ${{ secrets.AZURE_OPENAI_API_KEY }}
      
      - name: Upload Test Results
        uses: actions/upload-artifact@v4
        if: always()
        with:
          name: test-results
          path: |
            **/TestResults/**/*.trx
            **/TestResults/traces/**
          retention-days: 30
      
      - name: Upload Trace Artifacts
        uses: actions/upload-artifact@v4
        if: failure()
        with:
          name: failure-traces
          path: TestResults/traces/
          retention-days: 7
```

### Matrix Testing Across Models

```yaml
name: Model Comparison Tests

on:
  schedule:
    - cron: '0 0 * * *'  # Daily at midnight
  workflow_dispatch:

jobs:
  compare-models:
    runs-on: ubuntu-latest
    
    strategy:
      fail-fast: false
      matrix:
        model: [gpt-4o, gpt-4o-mini, gpt-35-turbo]
    
    env:
      AGENTEVAL_VERBOSITY: Full
      AGENTEVAL_SAVE_TRACES: true
      AZURE_OPENAI_DEPLOYMENT: ${{ matrix.model }}

    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      
      - name: Run Tests with ${{ matrix.model }}
        run: dotnet test --filter "Category=ModelComparison"
        env:
          AZURE_OPENAI_ENDPOINT: ${{ secrets.AZURE_OPENAI_ENDPOINT }}
          AZURE_OPENAI_API_KEY: ${{ secrets.AZURE_OPENAI_API_KEY }}
      
      - name: Upload Model Traces
        uses: actions/upload-artifact@v4
        if: always()
        with:
          name: traces-${{ matrix.model }}
          path: TestResults/traces/
          retention-days: 30
```

### PR Comments with Test Summary

```yaml
name: PR Test Summary

on: pull_request

jobs:
  test-and-comment:
    runs-on: ubuntu-latest
    
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      
      - name: Run Tests
        id: test
        run: |
          dotnet test --logger "trx" --results-directory ./TestResults 2>&1 | tee test-output.txt
        continue-on-error: true
        env:
          AGENTEVAL_VERBOSITY: Summary
          AGENTEVAL_SAVE_TRACES: true
      
      - name: Generate Test Summary
        if: always()
        run: |
          echo "## 🤖 Agent Test Results" >> $GITHUB_STEP_SUMMARY
          echo "" >> $GITHUB_STEP_SUMMARY
          
          if [ -d "TestResults/traces" ]; then
            TRACE_COUNT=$(find TestResults/traces -name "*.json" | wc -l)
            echo "📊 **Traces captured:** $TRACE_COUNT" >> $GITHUB_STEP_SUMMARY
            echo "" >> $GITHUB_STEP_SUMMARY
            
            if [ $TRACE_COUNT -gt 0 ]; then
              echo "<details><summary>View trace files</summary>" >> $GITHUB_STEP_SUMMARY
              echo "" >> $GITHUB_STEP_SUMMARY
              find TestResults/traces -name "*.json" -exec basename {} \; >> $GITHUB_STEP_SUMMARY
              echo "" >> $GITHUB_STEP_SUMMARY
              echo "</details>" >> $GITHUB_STEP_SUMMARY
            fi
          fi
```

## Azure DevOps Integration

### Basic Pipeline

```yaml
trigger:
  - main
  - develop

pool:
  vmImage: 'ubuntu-latest'

variables:
  AGENTEVAL_VERBOSITY: Detailed
  AGENTEVAL_SAVE_TRACES: true
  AGENTEVAL_TRACE_DIR: $(Build.ArtifactStagingDirectory)/traces

steps:
  - task: UseDotNet@2
    displayName: 'Install .NET 9.0'
    inputs:
      packageType: 'sdk'
      version: '9.0.x'

  - task: DotNetCoreCLI@2
    displayName: 'Restore packages'
    inputs:
      command: 'restore'

  - task: DotNetCoreCLI@2
    displayName: 'Run Agent Tests'
    inputs:
      command: 'test'
      arguments: '--logger trx --results-directory $(Build.ArtifactStagingDirectory)/TestResults'
    env:
      AZURE_OPENAI_ENDPOINT: $(AZURE_OPENAI_ENDPOINT)
      AZURE_OPENAI_API_KEY: $(AZURE_OPENAI_API_KEY)

  - task: PublishTestResults@2
    displayName: 'Publish Test Results'
    condition: always()
    inputs:
      testResultsFormat: 'VSTest'
      testResultsFiles: '$(Build.ArtifactStagingDirectory)/TestResults/**/*.trx'

  - task: PublishBuildArtifacts@1
    displayName: 'Publish Trace Artifacts'
    condition: always()
    inputs:
      pathToPublish: '$(Build.ArtifactStagingDirectory)/traces'
      artifactName: 'AgentTraces'
```

### Conditional Verbosity

```yaml
variables:
  ${{ if eq(variables['Build.Reason'], 'PullRequest') }}:
    AGENTEVAL_VERBOSITY: Full
    AGENTEVAL_SAVE_TRACES: true
  ${{ else }}:
    AGENTEVAL_VERBOSITY: Summary
    AGENTEVAL_SAVE_TRACES: false
```

### Stochastic Test Pipeline

```yaml
trigger: none

schedules:
  - cron: '0 2 * * *'
    displayName: 'Nightly stochastic tests'
    branches:
      include:
        - main
    always: true

variables:
  AGENTEVAL_VERBOSITY: Full
  AGENTEVAL_SAVE_TRACES: true
  STOCHASTIC_RUNS: 10

jobs:
  - job: StochasticTests
    displayName: 'Run Stochastic Agent Tests'
    timeoutInMinutes: 120
    
    steps:
      - task: UseDotNet@2
        inputs:
          packageType: 'sdk'
          version: '9.0.x'

      - task: DotNetCoreCLI@2
        displayName: 'Run Stochastic Tests'
        inputs:
          command: 'test'
          arguments: '--filter "Category=Stochastic" --logger trx'
        env:
          AZURE_OPENAI_ENDPOINT: $(AZURE_OPENAI_ENDPOINT)
          AZURE_OPENAI_API_KEY: $(AZURE_OPENAI_API_KEY)
          STOCHASTIC_RUNS: $(STOCHASTIC_RUNS)

      - task: PublishBuildArtifacts@1
        condition: always()
        inputs:
          pathToPublish: 'TestResults/traces'
          artifactName: 'StochasticTraces'
```

## Programmatic Configuration

You can also configure verbosity programmatically in your test classes:

```csharp
public class MyAgentTests : AgentEvalTestBase
{
    public MyAgentTests(ITestOutputHelper output) 
        : base(
            new XUnitTextWriter(output),
            new VerbositySettings
            {
                Level = VerbosityLevel.Full,
                IncludeToolArguments = true,
                IncludeToolResults = true,
                IncludePerformanceMetrics = true,
                SaveTraceFiles = true,
                TraceOutputDirectory = "TestResults/traces"
            })
    {
    }

    [Fact]
    public async Task MyAgent_HandlesComplexQuery()
    {
        // Your test code here...
        
        // Results are automatically traced
        RecordResult(result);
    }
}

// Helper to bridge ITestOutputHelper to TextWriter
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

## Debugging Failed Tests

### 1. Download Trace Artifacts

After a CI build fails, download the trace artifacts from your CI system.

### 2. Open in VS Code

The JSON traces are designed for easy viewing:

```bash
# Pretty-print a trace
cat TestResults/traces/MyTest_2025-01-30_143022.json | jq '.'
```

### 3. Analyze the Execution

```json
{
  "testName": "MyAgent_HandlesComplexQuery",
  "passed": false,
  "steps": [
    {
      "stepIndex": 0,
      "stepType": "ToolCall",
      "timestamp": "2025-01-30T14:30:22.123Z",
      "toolCall": {
        "toolName": "SearchDatabase",
        "arguments": { "query": "find user John" },
        "result": "User not found",
        "success": false,
        "durationMs": 1234
      }
    }
  ],
  "error": {
    "message": "Expected tool to return user data",
    "suggestions": [
      "Verify the test database has the expected seed data",
      "Check if the SearchDatabase tool is using the correct connection string"
    ]
  }
}
```

### 4. Replay Locally

Use the trace to understand what happened without rerunning the test with API calls:

```csharp
// Load a saved trace
var trace = TraceSerializer.Load("trace.json");

// Examine steps
foreach (var step in trace.Steps)
{
    Console.WriteLine($"Step {step.StepIndex}: {step.StepType}");
    if (step.ToolCall is not null)
    {
        Console.WriteLine($"  Tool: {step.ToolCall.ToolName}");
        Console.WriteLine($"  Result: {step.ToolCall.Result}");
    }
}
```

## Best Practices

### 1. Always Save Traces on Failure

```yaml
AGENTEVAL_SAVE_TRACES: true
```

This ensures you have debugging data when tests fail without the overhead on passing tests.

### 2. Use Full Verbosity for PR Builds

PRs need detailed feedback for quick debugging:

```yaml
env:
  AGENTEVAL_VERBOSITY: ${{ github.event_name == 'pull_request' && 'Full' || 'Summary' }}
```

### 3. Archive Traces with Build Info

Include build metadata in your trace directory:

```yaml
AGENTEVAL_TRACE_DIR: TestResults/traces/${{ github.run_id }}
```

### 4. Set Reasonable Retention

Traces can grow large. Keep failure traces longer than success artifacts:

```yaml
- uses: actions/upload-artifact@v4
  with:
    name: failure-traces
    path: TestResults/traces/
    retention-days: 30  # Keep failure data for analysis
```

### 5. Use stochastic evaluation in Nightly Builds

LLMs are non-deterministic. Run stochastic tests on a schedule:

```yaml
on:
  schedule:
    - cron: '0 0 * * *'  # Daily
```

## See Also

- [Tracing Guide](tracing.md) - Trace Record & Replay patterns
- [Assertions Reference](assertions.md) - Fluent assertion API
- [Evaluation Guide](evaluation-guide.md) - Metrics and evaluation patterns
