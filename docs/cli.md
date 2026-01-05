# AgentEval CLI Reference

The AgentEval CLI provides command-line tools for running evaluations, benchmarks, and tests in CI/CD pipelines.

## Installation

```bash
# Install as a global .NET tool
dotnet tool install -g AgentEval.Cli

# Or install locally in your project
dotnet tool install AgentEval.Cli
```

## Commands

### eval

Run evaluations against a dataset of test cases.

```bash
agenteval eval [options]
```

**Options:**

| Option | Description | Default |
|--------|-------------|---------|
| `--project <path>` | Path to the project directory | Current directory |
| `--dataset <path>` | Path to dataset file (JSON, JSONL, CSV, YAML) | - |
| `--format <format>` | Output format (console, json, junit, markdown) | console |
| `--output <path>` | Output file path | stdout |
| `--verbose` | Enable verbose output | false |

**Examples:**

```bash
# Run evaluation with JSON dataset
agenteval eval --dataset testcases.json --format junit --output results.xml

# Run with JSONL dataset and markdown report
agenteval eval --dataset cases.jsonl --format markdown --output report.md

# Verbose console output
agenteval eval --dataset data.yaml --verbose
```

### benchmark

Run performance benchmarks against your agent.

```bash
agenteval benchmark [options]
```

**Options:**

| Option | Description | Default |
|--------|-------------|---------|
| `--type <type>` | Benchmark type (latency, throughput, cost) | latency |
| `--iterations <n>` | Number of iterations | 10 |
| `--warmup <n>` | Warmup iterations | 2 |
| `--format <format>` | Output format | console |
| `--output <path>` | Output file path | stdout |

**Examples:**

```bash
# Run latency benchmark
agenteval benchmark --type latency --iterations 100

# Run throughput benchmark with JSON output
agenteval benchmark --type throughput --format json --output bench.json
```

### test

Run test suites with optional baseline comparison.

```bash
agenteval test [options]
```

**Options:**

| Option | Description | Default |
|--------|-------------|---------|
| `--project <path>` | Path to test project | Current directory |
| `--baseline <path>` | Baseline file for regression testing | - |
| `--fail-on-regression` | Fail if results regress from baseline | false |
| `--format <format>` | Output format | console |
| `--output <path>` | Output file path | stdout |

**Examples:**

```bash
# Run tests with JUnit output for CI
agenteval test --format junit --output results.xml

# Compare against baseline
agenteval test --baseline baseline.json --fail-on-regression
```

## Dataset Formats

The CLI supports multiple dataset formats for loading test cases.

### JSON

```json
[
  {
    "name": "Test Case 1",
    "input": "What is the weather?",
    "expectedOutput": "The weather is sunny",
    "context": ["Weather data: sunny, 72°F"]
  }
]
```

### JSONL (JSON Lines)

```jsonl
{"name": "Test 1", "input": "Hello", "expectedOutput": "Hi there!"}
{"name": "Test 2", "input": "Goodbye", "expectedOutput": "See you later!"}
```

### CSV

```csv
name,input,expectedOutput,context
Test 1,What is 2+2?,4,
Test 2,Capital of France?,Paris,Geography data
```

### YAML

```yaml
- name: Test Case 1
  input: What is the weather?
  expectedOutput: The weather is sunny
  context:
    - "Weather data: sunny, 72°F"

- name: Test Case 2
  input: Book a flight
  expectedOutput: Flight booked successfully
  expectedTools:
    - FlightSearch
    - BookFlight
```

## Output Formats

### Console (default)

Human-readable output with colors and formatting.

### JSON

```json
{
  "summary": {
    "total": 10,
    "passed": 8,
    "failed": 2,
    "duration": "00:00:15.234"
  },
  "results": [...]
}
```

### JUnit XML

Compatible with CI systems like GitHub Actions, Azure DevOps, Jenkins.

```xml
<?xml version="1.0" encoding="utf-8"?>
<testsuites>
  <testsuite name="AgentEval" tests="10" failures="2" time="15.234">
    <testcase name="Test Case 1" time="1.234" />
    <testcase name="Test Case 2" time="2.345">
      <failure message="Expected output mismatch">...</failure>
    </testcase>
  </testsuite>
</testsuites>
```

### Markdown

```markdown
# Evaluation Results

| Test Case | Status | Duration | Score |
|-----------|--------|----------|-------|
| Test 1 | ✅ Pass | 1.23s | 95% |
| Test 2 | ❌ Fail | 2.34s | 45% |

## Summary
- **Total:** 10
- **Passed:** 8
- **Failed:** 2
```

## CI/CD Integration

### GitHub Actions

```yaml
name: Agent Evaluation

on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      
      - name: Install AgentEval CLI
        run: dotnet tool install -g AgentEval.Cli
      
      - name: Run Evaluation
        run: agenteval eval --dataset tests/cases.jsonl --format junit --output results.xml
      
      - name: Publish Results
        uses: dorny/test-reporter@v1
        if: always()
        with:
          name: Agent Tests
          path: results.xml
          reporter: java-junit
```

### Azure DevOps

```yaml
trigger:
  - main

pool:
  vmImage: 'ubuntu-latest'

steps:
  - task: UseDotNet@2
    inputs:
      version: '8.0.x'

  - script: dotnet tool install -g AgentEval.Cli
    displayName: 'Install AgentEval CLI'

  - script: agenteval eval --dataset tests/cases.jsonl --format junit --output $(Build.ArtifactStagingDirectory)/results.xml
    displayName: 'Run Evaluation'

  - task: PublishTestResults@2
    inputs:
      testResultsFormat: 'JUnit'
      testResultsFiles: '$(Build.ArtifactStagingDirectory)/results.xml'
```

## Programmatic Usage

You can also use the exporters and loaders programmatically:

```csharp
using AgentEval.Cli.Exporters;
using AgentEval.Cli.DataLoaders;

// Load test cases
var loader = DatasetLoaderFactory.CreateFromExtension(".jsonl");
var testCases = await loader.LoadAsync("testcases.jsonl");

// Export results
var exporter = ExporterFactory.Create("junit");
await exporter.ExportAsync(results, "results.xml");
```

## See Also

- [Benchmarks](benchmarks.md) - Running performance benchmarks
- [Conversations](conversations.md) - Multi-turn testing
- [Extensibility](extensibility.md) - Custom exporters and loaders
