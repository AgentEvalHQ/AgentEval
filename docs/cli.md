# AgentEval CLI Reference

The AgentEval CLI provides command-line tools for running evaluations and managing configurations in CI/CD pipelines.

## Installation

```bash
# Install as a global .NET tool
dotnet tool install -g AgentEval.Cli

# Or install locally in your project
dotnet tool install AgentEval.Cli
```

## Commands

### eval

Run evaluations against an AI agent.

```bash
agenteval eval [options]
```

**Options:**

| Option | Alias | Description | Default |
|--------|-------|-------------|---------|
| `--config <path>` | `-c` | Path to evaluation configuration file (YAML or JSON) | - |
| `--dataset <path>` | `-d` | Path to dataset file (JSON, JSONL, CSV, YAML) | - |
| `--output <path>` | `-o` | Output file path for results | stdout |
| `--format <format>` | `-f` | Output format (json, junit, markdown, trx) | json |
| `--baseline <path>` | `-b` | Baseline file for regression comparison | - |
| `--fail-on-regression` | | Exit with code 1 if regressions detected | false |
| `--pass-threshold <n>` | | Minimum score to pass (0-100) | 70 |

**Examples:**

```bash
# Run evaluation with JSON dataset
agenteval eval --dataset testcases.json --format junit --output results.xml

# Run with config file and YAML dataset
agenteval eval --config agent-config.json --dataset cases.yaml --format markdown

# Set custom pass threshold
agenteval eval --dataset data.jsonl --pass-threshold 80

# Compare against baseline
agenteval eval --dataset tests.json --baseline baseline.json --fail-on-regression
```

### init

Create a starter evaluation configuration file.

```bash
agenteval init [options]
```

**Options:**

| Option | Alias | Description | Default |
|--------|-------|-------------|---------|
| `--output <path>` | `-o` | Output path for configuration file | agenteval.json |
| `--format <format>` | `-f` | Configuration format (json, yaml) | json |

**Examples:**

```bash
# Create JSON configuration
agenteval init

# Create YAML configuration
agenteval init --format yaml --output agenteval.yaml
```

### list

List available metrics, assertions, and formats.

```bash
agenteval list <subcommand>
```

**Subcommands:**

| Subcommand | Description |
|------------|-------------|
| `metrics` | List all available evaluation metrics |
| `assertions` | List all available assertion types |
| `formats` | List available output formats |

**Examples:**

```bash
# List available metrics
agenteval list metrics

# List assertion types
agenteval list assertions

# List output formats
agenteval list formats
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

You can also use the exporters and loaders programmatically from the main AgentEval library:

```csharp
using AgentEval.DataLoaders;
using AgentEval.Exporters;

// Load test cases from various formats
var jsonlLoader = DatasetLoaderFactory.CreateFromExtension(".jsonl");
var testCases = await jsonlLoader.LoadAsync("testcases.jsonl");

// Or create by format name
var yamlLoader = DatasetLoaderFactory.Create("yaml");
var yamlCases = await yamlLoader.LoadAsync("testcases.yaml");

// Export results to various formats
var report = new EvaluationReport { /* ... */ };
var exporter = ResultExporterFactory.Create(ExportFormat.JUnit);
await exporter.ExportAsync(report, "results.xml");

// Register custom loaders
DatasetLoaderFactory.Register(".custom", () => new JsonlDatasetLoader());
```

## See Also

- [Benchmarks](benchmarks.md) - Running performance benchmarks
- [Conversations](conversations.md) - Multi-turn testing
- [Extensibility](extensibility.md) - Custom exporters and loaders
