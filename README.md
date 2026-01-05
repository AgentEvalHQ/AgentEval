# AgentEval

<p align="center">
  <img src="assets/AgentEval_bounded.png" alt="AgentEval Logo" width="450" />
</p>

<p align="center">
  <strong>Make agent testing feel like normal .NET testing.</strong>
</p>

<p align="center">
  <a href="https://github.com/joslat/AgentEval/actions/workflows/ci.yml">
    <img src="https://github.com/joslat/AgentEval/actions/workflows/ci.yml/badge.svg" alt="CI Status" />
  </a>
  <a href="https://www.nuget.org/packages/AgentEval">
    <img src="https://img.shields.io/nuget/v/AgentEval.svg" alt="NuGet Version" />
  </a>
  <a href="https://github.com/joslat/AgentEval/blob/main/LICENSE">
    <img src="https://img.shields.io/github/license/joslat/AgentEval.svg" alt="License" />
  </a>
</p>

---

AgentEval is a .NET-native testing, evaluation, and benchmarking toolkit for AI agents—built first for **Microsoft Agent Framework (MAF)**. It focuses on what agentic systems actually need: **tool-call visibility**, **streaming performance metrics (TTFT)**, **cost awareness**, **benchmarks**, and **run artifacts** you can inspect and “time-travel” during debugging.

> AgentEval turns agent runs into test artifacts—and agent expectations into assertions.

---

## Why AgentEval?

Traditional LLM eval tooling often answers: “Was the final response good?”  
AgentEval answers the questions engineering teams need to ship reliably:

- **What tools were called? In what order? With which arguments? Did they fail?**
- **How long did it take? What was TTFT? Which tool was slow?**
- **Did the agent stay within token/cost budgets?**
- **Did this PR regress behavior compared to baseline?**
- **Can we inspect exactly what happened without rerunning the agent?**

---

## Key Features

### ✅ Multi-turn conversation testing
- Fluent conversation builder with Turn records
- ConversationRunner for executing against any IChatClient
- ConversationCompletenessMetric for scoring conversations
- Per-turn tool call tracking and assertions

### ✅ Fluent assertions for agent behavior
- Tool usage assertions (called/not called, order, arguments, results, errors, duration)
- Response assertions (contains, patterns, length, etc.)
- Performance assertions (latency, TTFT, tokens, estimated cost, tool count)

### ✅ Streaming performance & observability
- Real-time metrics tracking (TTFT, total duration)
- Per-tool timing and execution waterfall data
- Designed to align with OpenTelemetry (OTel) workflows

### ✅ Benchmarks
- Latency / throughput / cost benchmarks
- Agentic benchmarks (tool accuracy, task completion, multi-step quality)
- Percentiles (p50/p90/p95/p99) and summary statistics

### ✅ Evaluation metrics
- RAG metrics (faithfulness, relevance, context precision/recall, correctness)
- Agentic metrics (tool selection, tool arguments, tool success, efficiency, task completion)

### ✅ Run artifacts (“time travel”)
Store run inputs/outputs, tool calls, timings, and scores as artifacts so failures are explainable and inspectable.
### ✅ Snapshot testing
Compare agent responses against saved baselines with JSON diff and semantic similarity support.

### ✅ CLI tool for CI/CD
Full command-line interface for running evaluations, benchmarks, and tests:
- Multiple output formats (Console, JSON, JUnit XML, Markdown)
- Dataset loaders (JSON, JSONL, CSV, YAML)
- Configurable via command-line options
### ✅ Standard benchmark support
- **BFCL** (Berkeley Function Calling Leaderboard) - tool selection accuracy
- **GAIA** (General AI Assistants) - multi-step reasoning
- **ToolBench** - complex API tool workflows

---

## Framework Comparison

| Feature | AgentEval | DeepEval | Promptfoo | MS.Extensions.AI.Eval |
|---------|-----------|----------|-----------|----------------------|
| **Language** | .NET | Python | Node.js | .NET |
| **Tool call tracking** | ✅ Full timeline | ⚠️ Basic | ⚠️ Basic | ❌ |
| **TTFT/streaming** | ✅ | ❌ | ❌ | ⚠️ Partial |
| **Cost estimation** | ✅ | ✅ | ✅ | ❌ |
| **Fluent assertions** | ✅ | ❌ | ❌ | ❌ |
| **RAG metrics** | ✅ | ✅ | ✅ | ✅ |
| **BFCL benchmark** | ✅ | ✅ | ❌ | ❌ |
| **Multi-turn testing** | ✅ | ✅ | ✅ | ❌ |
| **CLI for CI/CD** | ✅ | ✅ | ✅ | ❌ |
| **Result exporters** | ✅ JSON/JUnit/MD | ✅ JSON | ✅ Multiple | ❌ |
| **Dataset loaders** | ✅ JSON/JSONL/CSV/YAML | ✅ | ✅ | ❌ |
| **Snapshot testing** | ✅ | ❌ | ❌ | ❌ |

**AgentEval's niche:** Native .NET + deep agentic visibility + benchmark compatibility.
---

## Installation

> NuGet packages will be published soon. Until then, clone and reference the project locally.

Planned packages:
- `AgentEval` (core)
- `AgentEval.Maf` (MAF integration)
- `AgentEval.TestKit` (fixtures/builders/helpers)
- `AgentEval.Tracing` (OTel + run artifacts)
- `AgentEval.Studio` (future: workflow visualizer / time-travel UI)

---

## Quick Start (MAF)

```csharp
// Create a test harness (with optional evaluator client)
var harness = new MAFTestHarness(verbose: true);

// Wrap your MAF agent
var adapter = new MAFAgentAdapter(myAgent);

// Define a test case
var testCase = new TestCase
{
    Name = "Feature Planning Test",
    Input = "Plan a user authentication feature",
    EvaluationCriteria = new[] { "Should include security considerations" },
    ExpectedTools = new[] { "FeatureTool", "SecurityTool" },
    PassingScore = 70
};

// Run
var result = await harness.RunTestAsync(adapter, testCase, new TestOptions
{
    TrackTools = true,
    TrackPerformance = true,
    EvaluateResponse = true
});

// Assert tool behavior
result.ToolUsage!
    .Should()
    .HaveCalledTool("FeatureTool")
        .BeforeTool("SecurityTool")
    .And()
    .HaveNoErrors();

// Assert performance budgets
result.Performance!
    .Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))
    .HaveTimeToFirstTokenUnder(TimeSpan.FromSeconds(2))
    .HaveEstimatedCostUnder(0.10m);
```

---

## Benchmarks

Run industry-standard benchmarks to compare your agent against published models:

```csharp
using AgentEval.Benchmarks;

var benchmark = new AgenticBenchmark(agent);

// Tool accuracy (BFCL-style)
var toolResults = await benchmark.RunToolAccuracyBenchmarkAsync(testCases);
Console.WriteLine($"Tool Accuracy: {toolResults.OverallAccuracy:P1}");

// Multi-step reasoning (ToolBench-style)
var stepResults = await benchmark.RunMultiStepReasoningBenchmarkAsync(multiStepCases);
Console.WriteLine($"Step Completion: {stepResults.AverageStepCompletion:P1}");

// Performance benchmarks
var perfBenchmark = new PerformanceBenchmark(agent);
var latency = await perfBenchmark.RunLatencyBenchmarkAsync(testCases, iterations: 10);
Console.WriteLine($"P90 Latency: {latency.P90Latency.TotalMilliseconds}ms");
```

📖 See [docs/benchmarks.md](docs/benchmarks.md) for BFCL, GAIA, and ToolBench guides.

---

## CLI Tool

AgentEval includes a full CLI tool for CI/CD integration:

```bash
# Install as .NET tool
dotnet tool install -g AgentEval.Cli

# Run evaluation with a dataset
agenteval eval --project ./tests --dataset testcases.jsonl --format junit --output results.xml

# Run benchmarks
agenteval benchmark --type latency --iterations 10

# Compare against baseline
agenteval test --baseline baseline.json --fail-on-regression
```

### Supported Formats

| Format | Export | Import |
|--------|--------|--------|
| JSON | ✅ | ✅ |
| JSONL | ✅ | ✅ |
| JUnit XML | ✅ | - |
| Markdown | ✅ | - |
| CSV | - | ✅ |
| YAML | - | ✅ |

**GitHub Actions**:
```yaml
- name: Run Agent Tests
  run: agenteval test --format junit --output results.xml
  
- name: Publish Results
  uses: dorny/test-reporter@v1
  with:
    name: Agent Tests
    path: results.xml
    reporter: java-junit
```

---

## Multi-Turn Conversations

Test complex multi-turn agent conversations:

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

// Evaluate completeness
var metric = new ConversationCompletenessMetric();
var score = metric.Evaluate(result);
Console.WriteLine($"Completeness: {score.Score:P0}");
```

---

## Snapshot Testing

Compare agent responses against saved baselines:

```csharp
using AgentEval.Snapshots;

// Configure snapshot comparison
var options = new SnapshotOptions
{
    IgnoreFields = new[] { "timestamp", "requestId" },
    ScrubPatterns = new Dictionary<string, string>
    {
        { @"\d{4}-\d{2}-\d{2}", "[DATE]" }
    }
};

// Compare responses
var comparer = new SnapshotComparer(options);
var result = comparer.Compare(expectedJson, actualJson);

if (!result.IsMatch)
{
    Console.WriteLine($"Differences found:");
    foreach (var diff in result.Differences)
    {
        Console.WriteLine($"  {diff.Path}: {diff.Expected} → {diff.Actual}");
    }
}

// Store and retrieve snapshots
var store = new SnapshotStore("./snapshots");
store.Save("my-test", actualResponse);
var baseline = store.Load("my-test");
```

---

## Documentation

| Document | Description |
|----------|-------------|
| [Architecture](docs/architecture.md) | Component diagrams, metric hierarchy |
| [Benchmarks](docs/benchmarks.md) | BFCL, GAIA, ToolBench guides |
| [CLI Reference](docs/cli.md) | Command-line tool usage |
| [Conversations](docs/conversations.md) | Multi-turn testing guide |
| [Embedding Metrics](docs/embedding-metrics.md) | Semantic similarity metrics |
| [Extensibility](docs/extensibility.md) | Custom metrics, plugins, adapters |
| [Snapshots](docs/snapshots.md) | Snapshot testing guide |
| [Plan Forward](src/AgentEval/AgentEval-plan-forward.md) | Roadmap and strategic direction |

---

## Test Coverage

AgentEval has **554 tests** covering:
- Tool call assertions and reporting
- Multi-turn conversation testing
- Snapshot comparison and storage
- RAG metrics (faithfulness, relevance, correctness)
- Agentic metrics (tool selection, efficiency, success)
- Performance tracking (TTFT, latency, cost)
- CLI exporters (JSON, JUnit, Markdown)
- Dataset loaders (JSON, JSONL, CSV, YAML)
- Embedding similarity utilities
- Serialization and error handling

---

## License

MIT License. See [LICENSE](LICENSE) for details.
