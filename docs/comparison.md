# Migration Guide

> **Coming from another evaluation framework?** This guide helps you transition to AgentEval with code examples and pattern translations.

---

## Why Migrate?

If you're a .NET team evaluating AI agents, you may have started with a Python or Node.js framework. AgentEval brings that functionality to your native stack with some unique capabilities:

- **Native .NET** - No Python interop, no Node.js subprocess
- **Fluent assertions** - Express complex agent behavior tests naturally
- **Tool-aware testing** - First-class support for agentic tool calls
- **stochastic evaluation** - Built-in statistics for LLM non-determinism
- **Trace record/replay** - Deterministic CI without API costs

---

## Coming from Python Evaluation Frameworks

If you've been using Python frameworks for LLM/agent evaluation, here's how AgentEval maps to familiar concepts.

### Metric Equivalents

| Python Concept | AgentEval Equivalent |
|----------------|---------------------|
| Faithfulness metric | `FaithfulnessMetric` |
| Relevance/Answer relevance | `RelevanceMetric` |
| Context precision | `ContextPrecisionMetric` |
| Context recall | `ContextRecallMetric` |
| Answer correctness | `AnswerCorrectnessMetric` |
| Custom LLM judge | Implement `IMetric` or `IRAGMetric` |

### Code Translation: RAG Evaluation

**Python pattern:**
```python
# Python RAG evaluation (generic pattern)
test_case = TestCase(
    input="What is the return policy?",
    actual_output=agent_response,
    retrieval_context=["Policy doc 1", "Policy doc 2"],
    expected_output="30 days with receipt"
)

faithfulness_score = faithfulness_metric.evaluate(test_case)
relevance_score = relevance_metric.evaluate(test_case)
```

**AgentEval equivalent:**
```csharp
var context = new EvaluationContext
{
    Input = "What is the return policy?",
    Output = agentResponse,
    Context = new[] { "Policy doc 1", "Policy doc 2" },
    GroundTruth = "30 days with receipt"
};

var faithfulness = await new FaithfulnessMetric(evaluator).EvaluateAsync(context);
var relevance = await new RelevanceMetric(evaluator).EvaluateAsync(context);

// AgentEval adds: fluent assertions on the results
faithfulness.Score.Should().BeGreaterThan(80);
```

### What AgentEval Adds

Beyond RAG metrics, AgentEval provides capabilities not commonly found in Python frameworks:

```csharp
// Tool usage assertions - verify agent called the right tools
result.ToolUsage!.Should()
    .HaveCalledTool("SearchDatabase")
        .BeforeTool("FormatResponse")
        .WithArgument("query", "return policy");

// Performance SLAs - latency, cost, tokens
result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5))
    .HaveEstimatedCostUnder(0.10m);

// stochastic evaluation - statistical confidence
var stochasticResult = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase, new StochasticOptions(Runs: 10, SuccessRateThreshold: 0.8));
```

---

## Coming from CLI-Based Tools

If you've been using YAML/CLI-based evaluation tools, here's how to translate your workflows.

### Configuration Translation

**YAML-based config (generic pattern):**
```yaml
prompts:
  - "Book a flight to {{destination}}"
  
providers:
  - model: gpt-4o
    config:
      temperature: 0.7

tests:
  - vars:
      destination: Paris
    assert:
      - type: contains
        value: "booking confirmed"
      - type: cost
        threshold: 0.05
```

**AgentEval equivalent:**
```csharp
var testCase = new TestCase
{
    Name = "Book flight to Paris",
    Input = "Book a flight to Paris",
    ExpectedContains = new[] { "booking confirmed" }
};

var result = await harness.RunEvaluationAsync(agent, testCase);

// Assertions
result.ActualOutput.Should().Contain("booking confirmed");
result.Performance!.Should().HaveEstimatedCostUnder(0.05m);
```

### Dataset-Based Testing

AgentEval supports the same dataset formats you're used to:

```csharp
// Load from YAML
var testCases = await DatasetLoader.LoadAsync("tests.yaml");

// Load from JSONL
var testCases = await DatasetLoader.LoadAsync("tests.jsonl");

// Load from CSV with field mapping
var testCases = await DatasetLoader.LoadAsync("tests.csv", new LoaderOptions
{
    FieldAliases = new Dictionary<string, string>
    {
        ["question"] = "Input",
        ["expected"] = "GroundTruth"
    }
});

// Run batch evaluation
foreach (var testCase in testCases)
{
    var result = await harness.RunEvaluationAsync(agent, testCase);
    // Collect results...
}
```

### CLI Usage

AgentEval provides a CLI for CI/CD integration:

```bash
# Install the CLI tool
dotnet tool install -g AgentEval.Cli

# Run evaluation from dataset
agenteval eval --dataset tests.yaml --format junit --output results.xml

# Initialize a new project
agenteval init --format yaml --output agenteval.yaml
```

---

## Coming from Microsoft.Extensions.AI.Evaluation

If you're already using the Microsoft evaluation library, AgentEval builds on the same abstractions with additional capabilities.

### Shared Foundation

Both libraries use `Microsoft.Extensions.AI` abstractions:

```csharp
// Same IChatClient works in both
IChatClient chatClient = new AzureOpenAIChatClient(endpoint, credential, deployment);

// MS.Extensions.AI.Eval
var evaluator = new ChatClientEvaluator(chatClient);
var score = await evaluator.EvaluateAsync(response, criteria);

// AgentEval - builds on this with agent-specific features
var harness = new MAFEvaluationHarness();
var result = await harness.RunEvaluationAsync(agent, testCase);
```

### What AgentEval Adds

| Capability | MS.Extensions.AI.Eval | AgentEval |
|------------|----------------------|-----------|
| Basic evaluation | Yes | Yes |
| Tool call tracking | No | Full timeline |
| Tool ordering assertions | No | Yes |
| stochastic evaluation | Manual | Built-in |
| Model comparison | Manual | With recommendations |
| Trace record/replay | No | Yes |
| Behavioral policies | No | NeverCallTool, etc. |

### Migration Pattern

```csharp
// If you have this with MS.Extensions.AI.Eval:
var score = await evaluator.EvaluateAsync(response, "Is this helpful?");

// You can add AgentEval for agent-specific testing:
var result = await harness.RunEvaluationAsync(agent, testCase);

// Get RAG scores (like MS.Extensions.AI.Eval)
var faithfulness = await new FaithfulnessMetric(evaluator).EvaluateAsync(context);

// Plus agent-specific assertions
result.ToolUsage!.Should()
    .HaveCalledTool("SearchProducts")
    .HaveNoErrors();
```

---

## Key Concepts Mapping

### Test Structure

| Concept | Python Frameworks | AgentEval |
|---------|-------------------|-----------|
| Test case | `TestCase`, `LLMTestCase` | `TestCase` |
| Evaluation context | Dict or object | `EvaluationContext` |
| Metric result | Float score | `MetricResult` (score + metadata) |
| Test result | Dict | `TestResult` |
| Assertions | `assert` statements | Fluent `.Should()` |

### Metric Types

| Type | Python | AgentEval |
|------|--------|-----------|
| RAG metrics | Built-in | `IRAGMetric` implementations |
| Agent/tool metrics | Often missing | `IAgenticMetric` implementations |
| Embedding-based | Varies | `EmbeddingSimilarityMetric` |
| Custom | Inherit base class | Implement `IMetric` |

### Test Execution

| Pattern | CLI Tools | AgentEval |
|---------|-----------|-----------|
| Single test | CLI command | `harness.RunEvaluationAsync()` |
| Batch testing | YAML dataset | `DatasetLoader` + loop |
| Parallel | Varies | `Parallel.ForEachAsync()` |
| CI/CD output | Various formats | JUnit XML, Markdown, JSON |

---

## Quick Start for Migrating Teams

### Step 1: Install AgentEval

```bash
dotnet add package AgentEval --prerelease
```

### Step 2: Translate Your First Test

```csharp
using AgentEval.MAF;
using AgentEval.Models;

// Create test case (same structure as other frameworks)
var testCase = new TestCase
{
    Name = "Customer Support Query",
    Input = "How do I return a product?",
    GroundTruth = "30-day return policy with receipt"
};

// Run test
var harness = new MAFEvaluationHarness();
var result = await harness.RunEvaluationAsync(agent, testCase);

// Assert (familiar patterns, more expressive)
Assert.True(result.Passed);
result.ActualOutput.Should().Contain("30 day");
```

### Step 3: Add Agent-Specific Assertions

```csharp
// Now leverage what AgentEval does uniquely well:

// Tool verification
result.ToolUsage!.Should()
    .HaveCalledTool("LookupPolicy")
    .HaveNoErrors();

// Performance SLAs
result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(3))
    .HaveEstimatedCostUnder(0.05m);
```

### Step 4: Handle LLM Non-Determinism

```csharp
// stochastic evaluation - run multiple times, analyze statistics
var stochasticResult = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase, 
    new StochasticOptions
    {
        Runs = 10,
        SuccessRateThreshold = 0.8
    });

// Assert on statistical properties
Assert.True(stochasticResult.PassedThreshold);
stochasticResult.Statistics.Mean.Should().BeGreaterThan(75);
```

---

## Need Help?

- [Getting Started Guide](getting-started.md) - Full tutorial
- [Code Gallery](showcase/code-gallery.md) - Real examples
- [Samples](https://github.com/joslat/AgentEval/tree/main/samples) - 18 runnable samples
- [Issues](https://github.com/joslat/AgentEval/issues) - Report problems or ask questions

---

<div align="center">

**Ready to get started?**

[Get Started](getting-started.md) | [See All Samples](https://github.com/joslat/AgentEval/tree/main/samples)

</div>
