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
  <a href="https://codecov.io/gh/joslat/AgentEval">
    <img src="https://codecov.io/gh/joslat/AgentEval/graph/badge.svg" alt="Code Coverage" />
  </a>
  <a href="https://www.nuget.org/packages/AgentEval">
    <img src="https://img.shields.io/nuget/v/AgentEval.svg" alt="NuGet Version" />
  </a>
  <a href="https://github.com/joslat/AgentEval/blob/main/LICENSE">
    <img src="https://img.shields.io/github/license/joslat/AgentEval.svg" alt="License" />
  </a>
  <img src="https://img.shields.io/badge/tests-3000%2B%20across%203%20TFMs-brightgreen" alt="Test Count" />
</p>

---

AgentEval is a **.NET-native testing, evaluation, and benchmarking toolkit for AI agents**  built first for **Microsoft Agent Framework (MAF)**. It brings the testing experience you know and love from .NET to the world of agentic AI.

> **For years, agentic developers have imagined writing tests like this. Today, they can.**

---

## The Code You Have Been Dreaming Of

### Compare Models, Get a Winner, Ship with Confidence

```csharp
var comparer = new ModelComparer(harness);

var result = await comparer.CompareModelsAsync(
    factories: new IAgentFactory[]
    {
        new AzureModelFactory("gpt-4o", "GPT-4o"),
        new AzureModelFactory("gpt-4o-mini", "GPT-4o Mini"),  
        new AzureModelFactory("gpt-35-turbo", "GPT-3.5 Turbo")
    },
    testCases: agenticTestSuite,
    metrics: new[] { new ToolSuccessMetric(), new RelevanceMetric(evaluator) },
    options: new ComparisonOptions(RunsPerModel: 5));

Console.WriteLine(result.ToMarkdown());
```

**Output:**
```markdown
## Model Comparison Results

| Rank | Model         | Tool Accuracy | Relevance | Mean Latency | Cost/1K Req |
|------|---------------|---------------|-----------|--------------|-------------|
| 1    | GPT-4o        | 94.2%         | 91.5      | 1,234ms      | $0.0150     |
| 2    | GPT-4o Mini   | 87.5%         | 84.2      | 456ms        | $0.0003     |
| 3    | GPT-3.5 Turbo | 72.1%         | 68.9      | 312ms        | $0.0005     |

**Recommendation:** GPT-4o - Highest tool accuracy (94.2%)
**Best Value:** GPT-4o Mini - 87.5% accuracy at 50x lower cost
```

---

### Assert on Tool Chains Like You Have Always Imagined

```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("SearchFlights", because: "must search before booking")
        .WithArgument("destination", "Paris")
        .WithDurationUnder(TimeSpan.FromSeconds(2))
    .And()
    .HaveCalledTool("BookFlight", because: "booking follows search")
        .AfterTool("SearchFlights")
        .WithArgument("flightId", "AF1234")
    .And()
    .HaveCallOrder("SearchFlights", "BookFlight", "SendConfirmation")
    .HaveNoErrors();
```

---

### Stochastic Testing: Because LLMs Are Non-Deterministic

LLMs don't return the same output every time. Run tests multiple times and analyze statistics:

```csharp
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase,
    new StochasticOptions
    {
        Runs = 20,                    // Run 20 times
        SuccessRateThreshold = 0.85,  // 85% must pass
        ScoreThreshold = 75           // Min score to count as "pass"
    });

// Understanding the statistics:
// - Mean: Average score across all 20 runs (higher = better overall quality)
// - StandardDeviation: How much scores vary run-to-run (lower = more consistent)
// - SuccessRate: % of runs where score >= ScoreThreshold (75 in this case)

result.Statistics.Mean.Should().BeGreaterThan(80);            // Avg quality
result.Statistics.StandardDeviation.Should().BeLessThan(10);  // Consistency

// The test that never flakes - pass/fail based on rate, not single run
Assert.True(result.PassedThreshold, 
    $"Success rate {result.SuccessRate:P0} below 85% threshold");
```

**Why this matters:** A single test run might pass 70% of the time due to LLM randomness. Stochastic testing tells you the *actual* reliability.

---

### Performance SLAs as Executable Tests

```csharp
result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5), 
        because: "UX requires sub-5s responses")
    .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500),
        because: "streaming responsiveness matters")
    .HaveEstimatedCostUnder(0.05m, 
        because: "stay within $0.05/request budget")
    .HaveTokenCountUnder(2000);
```

---

### Combined: Stochastic + Model Comparison

The most powerful pattern - compare models with statistical rigor (see Sample16):

```csharp
var factories = new IAgentFactory[]
{
    new AzureModelFactory("gpt-4o", "GPT-4o"),
    new AzureModelFactory("gpt-4o-mini", "GPT-4o Mini")
};

var modelResults = new List<(string ModelName, StochasticResult Result)>();

foreach (var factory in factories)
{
    var result = await stochasticRunner.RunStochasticTestAsync(
        factory, testCase, 
        new StochasticOptions(Runs: 5, SuccessRateThreshold: 0.8));
    modelResults.Add((factory.ModelName, result));
}

modelResults.PrintComparisonTable();
```

**Output:**
```
+------------------------------------------------------------------------------+
�                     Model Comparison (5 runs each)                           �
+-----------------------------------------------------------------------------�
� Model        � Pass Rate   � Mean Score � Std Dev  � Recommendation         �
+--------------+-------------+------------+----------+------------------------�
� GPT-4o       � 100%        � 92.4       � 3.2      � ?? Best Quality        �
� GPT-4o Mini  � 80%         � 84.1       � 8.7      � ?? Best Value          �
+-----------------------------------------------------------------------------+
```

---

### Behavioral Policy Guardrails (Compliance as Code)

```csharp
result.ToolUsage!.Should()
    // PCI-DSS: Never expose card numbers
    .NeverPassArgumentMatching(@"\b\d{16}\b",
        because: "PCI-DSS prohibits raw card numbers")
    
    // GDPR: Require consent
    .MustConfirmBefore("ProcessPersonalData",
        because: "GDPR requires explicit consent",
        confirmationToolName: "VerifyUserConsent")
    
    // Safety: Block dangerous operations
    .NeverCallTool("DeleteAllCustomers",
        because: "mass deletion requires manual approval");
```

---

### RAG Quality: Is Your Agent Hallucinating?

```csharp
var context = new EvaluationContext
{
    Input = "What are the return policy terms?",
    Output = agentResponse,
    Context = retrievedDocuments,
    GroundTruth = "30-day return policy with receipt"
};

var faithfulness = await new FaithfulnessMetric(evaluator).EvaluateAsync(context);
var relevance = await new RelevanceMetric(evaluator).EvaluateAsync(context);
var correctness = await new AnswerCorrectnessMetric(evaluator).EvaluateAsync(context);

// Detect hallucinations
if (faithfulness.Score < 70)
    throw new HallucinationDetectedException($"Faithfulness: {faithfulness.Score}");
```

---

### Rich Test Output: Debug CI Failures Like a Pro

Configure verbosity levels via environment variables�no code changes needed:

```bash
# In CI/CD pipeline
AGENTEVAL_VERBOSITY=Detailed     # None, Summary, Detailed, Full
AGENTEVAL_SAVE_TRACES=true       # Auto-save on test failure
AGENTEVAL_TRACE_DIR=./traces     # Where to save
```

**Time-Travel Traces:** Captured JSON files show every step of execution:

```json
{
  "schemaVersion": "1.0",
  "traceId": "booking-test-2026-01-24",
  "steps": [
    { "type": "UserInput", "data": { "input": "Book a flight to Paris" } },
    { "type": "LlmRequest", "duration": "00:00:00.450", "data": { "model": "gpt-4o" } },
    { "type": "ToolCall", "data": { "name": "SearchFlights", "args": { "destination": "Paris" } } },
    { "type": "ToolResult", "data": { "success": true, "flights": 5 } },
    { "type": "AgentResponse", "data": { "output": "Found 5 flights..." } }
  ]
}
```

**Optional Base Class for Auto-Tracing Tests:**

```csharp
public class MyAgentTests : AgentEvalTestBase
{
    public MyAgentTests(ITestOutputHelper output) 
        : base(new XUnitTextWriter(output)) { }
    
    [Fact]
    public async Task BookFlight_WithValidInput_ShouldSucceed()
    {
        var result = /* ... run agent ... */;
        
        // Automatically records results with configured verbosity
        RecordResult(result);
        
        // Saves trace file on failure (configurable)
        if (!result.Passed)
            SaveTrace("booking-failure", result);
    }
}
```

**?? See:** [docs/rich-test-output-guide.md](docs/rich-test-output-guide.md) for the complete step-by-step guide.

---

## Why AgentEval?

| Challenge | How AgentEval Solves It |
|-----------|------------------------|
| "What tools did my agent call?" | **Full tool timeline** with arguments, results, timing |
| "Tests fail randomly!" | **Stochastic testing** - assert on pass *rate*, not pass/fail |
| "Which model should I use?" | **Model comparison** with cost/quality recommendations |
| "Is my agent compliant?" | **Behavioral policies** - guardrails as code |
| "Is my RAG hallucinating?" | **Faithfulness metrics** - grounding verification |
| "What's the latency/cost?" | **Performance metrics** - TTFT, tokens, estimated cost |
| "How do I debug failures?" | **Trace recording** - capture executions for step-by-step analysis |
| "CI tests pass locally but fail in CI?" | **Rich test output** - detailed logs and trace artifacts |

---

## Key Features

### Testing and Assertions
- Fluent tool assertions - order, arguments, results, duration
- Performance assertions - latency, TTFT, tokens, cost
- Response assertions - contains, patterns, length
- Behavioral policies - NeverCallTool, MustConfirmBefore, NeverPassArgumentMatching
- Multi-turn conversations - full conversation flow testing
- Snapshot testing - regression detection with semantic similarity

### Evaluation and Metrics  
- RAG metrics - faithfulness, relevance, context precision/recall, correctness
- Agentic metrics - tool selection, arguments, success, efficiency
- Embedding metrics - semantic similarity (100x cheaper than LLM)
- Custom metrics - extensible for your domain

### Reliability and Confidence
- Stochastic testing - run N times, analyze statistics (mean, std dev, p90)
- Model comparison - compare across models with recommendations
- Calibrated judging - multi-model consensus evaluation
- Trace recording - capture executions for debugging and reproduction

### Developer Experience
- Rich test output - configurable verbosity (None/Summary/Detailed/Full)
- Time-travel traces - step-by-step execution capture in JSON
- Trace artifacts - auto-save traces for failed tests
- AgentEvalTestBase - optional base class for auto-tracing tests

### Enterprise Ready
- CI/CD integration - JUnit XML, Markdown, JSON export, trace artifacts
- CLI tool - agenteval eval, agenteval init
- Benchmarks - custom patterns with dataset loaders (JSON, YAML, CSV, JSONL)
- 1,000+ tests (�3 TFMs) - production quality

---

## Installation

```bash
dotnet add package AgentEval --prerelease
```

**Supported Frameworks:** .NET 8.0, 9.0, 10.0

---

## Quick Start

```csharp
using AgentEval.MAF;
using AgentEval.Models;

// 1. Create your MAF agent
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "TravelAgent",
    Instructions = "You are a travel booking assistant.",
    Tools = [AIFunctionFactory.Create(SearchFlights), AIFunctionFactory.Create(BookFlight)]
});

// 2. Wrap and test
var harness = new MAFTestHarness();
var adapter = new MAFAgentAdapter(agent);

var testCase = new TestCase
{
    Name = "Book Flight to Paris",
    Input = "Book me a flight to Paris for March 15th",
    ExpectedTools = ["SearchFlights", "BookFlight"]
};

var result = await harness.RunTestAsync(adapter, testCase);

// 3. Assert on everything
result.ToolUsage!.Should()
    .HaveCalledTool("SearchFlights")
    .BeforeTool("BookFlight")
    .HaveNoErrors();

result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))
    .HaveEstimatedCostUnder(0.10m);

Assert.True(result.Passed);
```

---

## CLI Tool

```bash
# Install globally
dotnet tool install -g AgentEval.Cli

# Run evaluations
agenteval eval --dataset tests.yaml --format junit --output results.xml

# Initialize project
agenteval init --format yaml --output agenteval.yaml

# List available metrics
agenteval list metrics
```

### CI/CD Integration

```yaml
# GitHub Actions
- name: Run Agent Tests
  run: agenteval eval --dataset tests.yaml --format junit -o results.xml
  
- name: Publish Results
  uses: dorny/test-reporter@v1
  with:
    name: Agent Tests
    path: results.xml
    reporter: java-junit
```

---

## Documentation

| Guide | Description |
|-------|-------------|
| [Getting Started](docs/getting-started.md) | Your first agent test in 5 minutes |
| [Fluent Assertions](docs/assertions.md) | Complete assertion guide |
| [Stochastic Testing](docs/stochastic-testing.md) | Handle LLM non-determinism |
| [Model Comparison](docs/model-comparison.md) | Compare models with confidence |
| [Benchmarks](docs/benchmarks.md) | Benchmark patterns and best practices |
| [Tracing](docs/tracing.md) | Record and Replay patterns |
| [Migration Guide](docs/comparison.md) | Coming from Python/Node.js frameworks |
| [Code Gallery](docs/showcase/code-gallery.md) | Stunning code examples |

---

## Samples

Run all 18 included samples:

```bash
dotnet run --project samples/AgentEval.Samples
```

| Sample | Description | Time |
|--------|-------------|------|
| **01: Hello World** | The simplest possible agent test | 2 min |
| **02: Agent with One Tool** | Tool tracking and fluent assertions | 5 min |
| **03: Agent with Multiple Tools** | Tool ordering, timing, and timeline | 7 min |
| **04: Performance Metrics** | Latency, cost, TTFT, and token tracking | 5 min |
| **05: RAG Evaluation** | Faithfulness, relevance, precision, recall, correctness | 8 min |
| **06: Benchmarks** | Performance and agentic benchmark patterns | 5 min |
| **07: Snapshot Testing** | Regression detection with baselines | 5 min |
| **08: Conversation Testing** | Multi-turn agent interactions | 5 min |
| **09: Workflow Testing** | Multi-agent orchestration and routing | 10 min |
| **10: Datasets and Export** | Batch testing with JSON/YAML/CSV/JSONL | 5 min |
| **11: Because Assertions** | Self-documenting tests with intent clarity | 5 min |
| **12: Policy & Safety Testing** | Enterprise guardrails (NeverCallTool, MustConfirmBefore) | 8 min |
| **13: Trace Record & Replay** | Capture executions for deterministic testing | 8 min |
| **14: Stochastic Testing** | Run tests N times for statistical confidence | 5 min |
| **15: Model Comparison** | Compare multiple models on the same task | 8 min |
| **16: Combined Stochastic + Comparison** | Stochastic tests across multiple models | 10 min |
| **17: Quality & Safety Metrics** | Groundedness, coherence, fluency evaluation | 5 min |
| **18: Judge Calibration** | Multi-model consensus for reliable LLM-as-judge | 8 min |

---

## Contributing

We welcome contributions! Please see:
- [CONTRIBUTING.md](CONTRIBUTING.md)
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
- [SECURITY.md](SECURITY.md)

---

## Forever Open Source

AgentEval is **MIT licensed** and will remain open source forever. We believe in:
- ?? **No license changes** - MIT today, MIT forever
- ?? **No "open core"** - All features are open source, no proprietary tiers
- ?? **Community first** - Built for and with the .NET AI community

---

## License

MIT License. See [LICENSE](LICENSE) for details.

---

<p align="center">
  <strong>Built with love for the .NET AI community</strong>
</p>

<p align="center">
  <a href="https://github.com/joslat/AgentEval">Star us on GitHub</a> |
  <a href="https://www.nuget.org/packages/AgentEval">NuGet</a> |
  <a href="https://github.com/joslat/AgentEval/issues">Issues</a>
</p>
