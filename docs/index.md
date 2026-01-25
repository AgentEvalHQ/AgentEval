# AgentEval

<p align="center">
  <img src="images/AgentEval_bounded.png" alt="AgentEval Logo" width="400" />
</p>

<p align="center">
  <strong>Your AI agent works great... until it doesn't.<br/>AgentEval catches the failures before your users do.</strong>
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/AgentEval">
    <img src="https://img.shields.io/nuget/v/AgentEval.svg" alt="NuGet Version" />
  </a>
  <img src="https://img.shields.io/badge/tests-3000%2B-brightgreen" alt="Test Count" />
  <img src="https://img.shields.io/badge/license-MIT-blue" alt="License" />
</p>

---

## The .NET Evaluation Toolkit for AI Agents

AgentEval is **the comprehensive .NET toolkit for AI agent evaluation**—tool usage validation, RAG quality metrics, stochastic evaluation, and model comparison—built for **Microsoft Agent Framework (MAF)**. What RAGAS and DeepEval do for Python, AgentEval does for .NET.

> **For years, agentic developers have imagined writing tests like this. Today, they can.**

---

## The Code You've Been Dreaming Of

### Assert on Tool Chains Like Requirements

```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("AuthenticateUser", because: "security first")
        .BeforeTool("FetchUserData")
        .WithArgument("method", "OAuth2")
    .And()
    .HaveCalledTool("SendNotification")
        .AtLeastTimes(1)
    .And()
    .HaveNoErrors();
```

**No more regex parsing logs. No more "did it call that function?"**

### Performance SLAs as Executable Tests

```csharp
result.Performance!.Should()
    .HaveFirstTokenUnder(TimeSpan.FromMilliseconds(500),
        because: "streaming responsiveness matters")
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5))
    .HaveEstimatedCostUnder(0.05m,
        because: "stay within budget");
```

**Know before production if your agent is too slow or too expensive.**

### stochastic evaluation: Because LLMs Aren't Deterministic

```csharp
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase,
    new StochasticOptions(Runs: 10, SuccessRateThreshold: 0.85));

result.Statistics.SuccessRate.Should().BeGreaterThan(0.85);
result.Statistics.StandardDeviation.Should().BeLessThan(10);
```

**Run the same test 10 times. Know your actual success rate, not your lucky-run rate.**

### Compare Models, Get a Winner

```csharp
var result = await comparer.CompareModelsAsync(
    factories: new[] { gpt4o, gpt4oMini, claude },
    testCases: testSuite,
    metrics: new[] { new ToolSuccessMetric(), new RelevanceMetric(eval) },
    options: new ComparisonOptions(RunsPerModel: 5));

Console.WriteLine(result.ToMarkdown());
```

**Output:**
```markdown
| Rank | Model         | Tool Accuracy | Relevance | Cost/1K Req |
|------|---------------|---------------|-----------|-------------|
| 🥇   | GPT-4o        | 94.2%         | 91.5      | $0.0150     |
| 🥈   | GPT-4o Mini   | 87.5%         | 84.2      | $0.0003     |

**Recommendation:** GPT-4o - Highest accuracy
**Best Value:** GPT-4o Mini - 87.5% accuracy at 50x lower cost
```

### Record Once, Replay Forever (No API Costs)

```csharp
// RECORD once (live API call)
var recorder = new TraceRecordingAgent(realAgent);
await recorder.ExecuteAsync("Book a flight to Paris");
TraceSerializer.Save(recorder.GetTrace(), "booking-trace.json");

// REPLAY forever (no API call, instant, free)
var replayer = new TraceReplayingAgent(trace);
var response = await replayer.ReplayNextAsync();  // Identical every time
```

**Save API costs. Run tests in CI. Get consistent results.**

---

## 60-Second Quick Start

### 1. Install

```bash
dotnet add package AgentEval --prerelease
```

### 2. Write Your First Test

```csharp
[Fact]
public async Task Agent_ShouldHandleBookingRequest()
{
    var harness = new MAFEvaluationHarness();
    var testCase = new TestCase { Input = "Book a flight to Paris" };

    var result = await harness.RunEvaluationAsync(agent, testCase);

    result.ToolUsage!.Should()
        .HaveCalledTool("SearchFlights")
        .And()
        .HaveCalledTool("CreateBooking");

    result.Performance!.Should()
        .HaveTotalDurationUnder(TimeSpan.FromSeconds(10));
}
```

### 3. Run

```bash
dotnet test
```

**That's it.** No complex setup. No external services. No Python.

[Get Started →](getting-started.md)

---

## Why AgentEval?

| Challenge | How AgentEval Solves It |
|-----------|------------------------|
| "What tools did my agent call?" | **Full tool timeline** with arguments, results, timing |
| "Tests fail randomly!" | **stochastic evaluation** - assert on pass *rate*, not single run |
| "Which model should I use?" | **Model comparison** with cost/quality recommendations |
| "Is my agent compliant?" | **Behavioral policies** - guardrails as code |
| "Is my RAG hallucinating?" | **Faithfulness metrics** - grounding verification |
| "How do I debug CI failures?" | **Trace replay** - capture and reproduce executions |

---

## Feature Highlights

<div class="grid cards" markdown>

-   **🎯 Fluent Assertions**
    
    Tool chains, performance, responses - all with `Should()` syntax

-   **⚡ Performance Metrics**
    
    TTFT, latency, tokens, cost estimation with 8+ model pricing

-   **🔬 stochastic evaluation**
    
    Run N times, get statistics, assert on pass rates

-   **🤖 Model Comparison**
    
    Compare models side-by-side with recommendations

-   **🎬 Trace Record/Replay**
    
    Deterministic tests without API calls

-   **🛡️ Behavioral Policies**
    
    NeverCallTool, MustConfirmBefore, PII detection

-   **📊 RAG Metrics**
    
    Faithfulness, Relevance, Context Precision/Recall

-   **🔄 Multi-Turn Testing**
    
    Full conversation flow testing

</div>

---

## Who Is AgentEval For?

### 🏢 .NET Teams Building AI Agents

If you're building production AI agents in .NET and need to verify tool usage, enforce SLAs, handle non-determinism, or compare models—AgentEval is for you.

### 🚀 Microsoft Agent Framework (MAF) Developers

Native integration with MAF concepts: `AIAgent`, `IChatClient`, automatic tool call tracking, and performance metrics with token usage and cost estimation.

### 📊 ML Engineers Evaluating LLM Quality

Rigorous evaluation capabilities: RAG metrics (Faithfulness, Relevance, Context Precision), embedding-based similarity, and calibrated judge patterns for consistent evaluation.

---

## CLI Tool & Samples

**CLI for CI/CD:**
```bash
dotnet tool install -g AgentEval.Cli
agenteval eval --dataset tests.yaml --format junit -o results.xml
```

**18 runnable samples** included—from Hello World to Model Comparison. [View Samples →](https://github.com/joslat/AgentEval/tree/main/samples/AgentEval.Samples)

---

## Documentation

| Getting Started | Features | Advanced |
|-----------------|----------|----------|
| [Installation](installation.md) | [Assertions](assertions.md) | [stochastic evaluation](stochastic-testing.md) |
| [Quick Start](getting-started.md) | [Metrics Reference](metrics-reference.md) | [Model Comparison](model-comparison.md) |
| [Walkthrough](walkthrough.md) | [Benchmarks](benchmarks.md) | [Trace Record/Replay](tracing.md) |
| [CLI Tool](cli.md) | [Workflows](workflows.md) | [Architecture](architecture.md) |

---

## The .NET Advantage

| Feature | AgentEval | Python Alternatives |
|---------|-----------|---------------------|
| **Language** | Native C#/.NET | Python only |
| **Type Safety** | Compile-time errors | Runtime exceptions |
| **IDE Support** | Full IntelliSense | Variable |
| **MAF Integration** | First-class | None |
| **Fluent Assertions** | `Should().HaveCalledTool()` | N/A |
| **Trace Replay** | Built-in | Manual |

---

## Test Coverage: 67%+ | 3,000+ Tests

AgentEval has **1,000+ tests** running across **3 target frameworks** (net8.0, net9.0, net10.0), totaling **3,000+ test executions** per CI run.

[![codecov](https://codecov.io/gh/joslat/AgentEval/graph/badge.svg?token=Y28TAK3LNH)](https://codecov.io/gh/joslat/AgentEval)

---

## Community

- **GitHub:** [github.com/joslat/AgentEval](https://github.com/joslat/AgentEval)
- **NuGet:** [nuget.org/packages/AgentEval](https://www.nuget.org/packages/AgentEval)
- **Issues:** [Report bugs or request features](https://github.com/joslat/AgentEval/issues)
- **Discussions:** [Ask questions](https://github.com/joslat/AgentEval/discussions)

---

## Forever Open Source

AgentEval is **MIT licensed** and will remain open source forever.

- ✅ **No license changes** - MIT today, MIT forever
- ✅ **No "open core"** - All features are open source
- ✅ **Community first** - Built for the .NET AI community

---

<p align="center">
  <strong>Stop guessing if your AI agent works. Start proving it.</strong>
</p>

<p align="center">
  <a href="getting-started.md"><strong>Get Started →</strong></a>
</p>
